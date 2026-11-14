using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Core;
using AgentCore.Editor.LLM;
using AgentCore.Editor.Tools;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Tools.Safety;
using AgentCore.Editor.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.Components.MCP.Bridge
{
    /// <summary>一次 MCP 工具调用的结果（映射为 MCP content 由 relay 完成）。</summary>
    public sealed class McpCallOutcome
    {
        public string Status = McpBridgeProtocol.StatusOk;
        public string Text = string.Empty;
        public JArray Images = new JArray();

        /// <summary>工具结果与编译相关（写脚本 / 请求编译）：重载后需要补上编译结果。</summary>
        public bool CompileRelated;

        /// <summary>
        /// 调用还没开始执行就被 Domain Reload 丢弃（主线程工作队列在重载前被清空）：不答复，
        /// relay 重连后 resume 得到 unknownCalls，对同一编辑器进程重发。
        /// </summary>
        public bool NotStarted;

        public JObject ToMessage(string id) => new JObject
        {
            ["type"] = McpBridgeProtocol.Result,
            ["id"] = id,
            ["status"] = Status,
            ["text"] = Text ?? string.Empty,
            ["images"] = Images ?? new JArray(),
        };

        public static McpCallOutcome Error(string text) => new McpCallOutcome { Status = McpBridgeProtocol.StatusError, Text = text };
    }

    /// <summary>
    /// 执行 MCP 工具调用：每次调用一个绑定来源客户端的 <see cref="ToolCallDispatcher"/>，
    /// 走与 Chat 完全相同的校验、Play Mode 预检、风险策略、权限规则与检查点；整个分发在主线程上进行。
    /// 各调用的同步部分都在主线程上执行；不做全局串行，避免一个等待人工确认的调用堵住其他调用。
    /// </summary>
    public static class McpToolExecutor
    {

        /// <summary>测试可替换：按客户端名构造确认提供器。</summary>
        internal static Func<McpCallContext, IToolConfirmationProvider> ProviderFactory = context => new McpConfirmationProvider(context);

        public static Task<McpCallOutcome> ExecuteAsync(string toolName, JObject arguments, string clientName, CancellationToken ct) =>
            ExecuteAsync(toolName, arguments, McpCallContext.ForClient(clientName), ct);

        public static async Task<McpCallOutcome> ExecuteAsync(string toolName, JObject arguments, McpCallContext context, CancellationToken ct)
        {
            context ??= new McpCallContext();
            var clientName = context.ClientName;
            if (string.IsNullOrEmpty(toolName))
                return McpCallOutcome.Error("Missing tool name.");
            if (McpToolCatalog.ExcludedTools.TryGetValue(toolName, out var reason))
                return McpCallOutcome.Error($"Tool '{toolName}' is not available over MCP ({reason}).");
            if (toolName.StartsWith(AgentCore.Editor.McpClient.McpProxyTool.NamePrefix, StringComparison.Ordinal))
                return McpCallOutcome.Error($"Tool '{toolName}' belongs to an external MCP server and is not re-exported.");

            arguments ??= new JObject();

            // 工具注册表可能还没建立（Chat 窗口从未打开、或被其他流程清空）：先在主线程做一次自动发现
            if (!ToolRegistry.Instance.HasTool(toolName))
            {
                try
                {
                    await MainThread.RunAsync(() =>
                    {
                        ToolAutoDiscovery.DiscoverAndRegisterAll();
                        // 发现过一次后注册表又被清空时，DiscoverAndRegisterAll 会直接跳过：重置后再来一次
                        if (ToolRegistry.Instance.Count == 0)
                        {
                            ToolAutoDiscovery.Reset();
                            ToolAutoDiscovery.DiscoverAndRegisterAll();
                        }
                    }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AgentCoreLog.Warning($"[AgentCore][MCP] Tool discovery failed: {ex.Message}");
                }
            }

            var stopwatch = Stopwatch.StartNew();
            var argumentsText = arguments.ToString(Formatting.None);
            var activity = ExternalToolActivity.Begin(clientName, toolName, (string)arguments["action"],
                argumentsText.Length > 2000 ? argumentsText.Substring(0, 2000) + "..." : argumentsText);
            McpCallOutcome outcome;
            try
            {
                var call = new ToolCall
                {
                    Id = "mcp_" + Guid.NewGuid().ToString("N").Substring(0, 12),
                    Function = new FunctionCall { Name = toolName, Arguments = arguments.ToString(Formatting.None) }
                };
                var dispatcher = new ToolCallDispatcher(ToolRegistry.Instance, ProviderFactory(context));

                var started = false;
                try
                {
                    var dispatchTask = await MainThread.RunAsync(() =>
                    {
                        started = true;
                        return dispatcher.DispatchAsync(call, ct);
                    }).ConfigureAwait(false);
                    var callResult = await dispatchTask.ConfigureAwait(false);
                    outcome = ToOutcome(toolName, callResult?.Result);
                }
                catch (OperationCanceledException) when (!started && !ct.IsCancellationRequested)
                {
                    outcome = new McpCallOutcome { NotStarted = true, Status = McpBridgeProtocol.StatusError, Text = "Not started (script reload)." };
                }
            }
            catch (OperationCanceledException)
            {
                outcome = McpCallOutcome.Error($"Tool '{toolName}' was cancelled.");
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore][MCP] Tool '{toolName}' failed: {ex.Message}");
                outcome = McpCallOutcome.Error($"Exception executing '{toolName}': {ex.Message}");
            }

            // Unity 的延迟事件（hierarchyChanged、projectChanged 等）在下一次编辑器 tick 才派发：唤醒一次，资源变化通知才能及时发出
            try { UnityEditor.EditorApplication.QueuePlayerLoopUpdate(); } catch (Exception) { /* ignore */ }

            var preview = outcome.Text ?? string.Empty;
            ExternalToolActivity.End(activity,
                outcome.NotStarted ? "cancelled" : outcome.Status,
                outcome.NotStarted ? "Not started: Unity reloaded scripts; the client resends it." : (preview.Length > 4000 ? preview.Substring(0, 4000) + "..." : preview));

            if (outcome.NotStarted)
                return outcome;

            // 可写工具执行后：场景、工程文件与版本控制状态可能变了（编辑器空闲时 Unity 自己的变更事件可能迟迟不派发）
            if (outcome.Status != McpBridgeProtocol.StatusError && !IsReadOnlyTool(toolName))
            {
                McpResourceEvents.MarkDirty(McpResourceCatalog.SceneHierarchyUri);
                McpResourceEvents.MarkDirty(McpResourceCatalog.AssetUriPrefix + "*");
                McpResourceEvents.MarkDirty(McpResourceCatalog.VcsStatusUri);
            }

            McpAuditLog.Append(clientName, toolName, arguments, outcome.Status, stopwatch.Elapsed.TotalMilliseconds,
                outcome.Status == McpBridgeProtocol.StatusOk ? null : outcome.Text);
            return outcome;
        }

        private static bool IsReadOnlyTool(string toolName)
        {
            var metadata = ToolRegistry.Instance.GetTool(toolName)?.Metadata;
            return metadata != null && metadata.RiskLevel == AgentCore.Editor.Tools.Safety.ToolRiskLevel.ReadOnly;
        }

        /// <summary>
        /// 该调用是否可能改了脚本、需要等编译（与内嵌 Chat 的 IsScriptModifyingCommand 一致：
        /// 工具声明了 MayModifyScripts，且 action 不是只读动作）。
        /// </summary>
        public static bool MayTriggerCompilation(string toolName, JObject arguments)
        {
            var tool = string.IsNullOrEmpty(toolName) ? null : ToolRegistry.Instance.GetTool(toolName);
            if (tool == null)
                return false;
            var attribute = (AgentToolAttribute)Attribute.GetCustomAttribute(tool.GetType(), typeof(AgentToolAttribute));
            if (attribute == null || !attribute.MayModifyScripts)
                return false;
            var action = ((string)arguments?["action"])?.ToLowerInvariant();
            return !(action == "read" || action == "list" || action == "get_info" || action == "get"
                     || action == "search" || action == "validate");
        }

        internal static McpCallOutcome ToOutcome(string toolName, ToolResult result)
        {
            if (result == null)
                return McpCallOutcome.Error($"Tool '{toolName}' returned no result.");

            var outcome = new McpCallOutcome
            {
                Status = !result.Success ? McpBridgeProtocol.StatusError
                    : result.IsPartial ? McpBridgeProtocol.StatusPartial
                    : McpBridgeProtocol.StatusOk,
                Text = result.Success ? result.Output : (string.IsNullOrEmpty(result.Error) ? result.Output : result.Error),
                CompileRelated = result.IsCompileRelated,
            };

            // send_image 把图放进待发图暂存区（Chat 用来画进气泡）；MCP 调用直接把它作为图片结果带回并取走
            if (result.Success && toolName == "send_image" && SendImageStore.TryGetCurrent(out var dataUrl))
            {
                if (TryParseDataUrl(dataUrl, out var mimeType, out var base64))
                    outcome.Images.Add(new JObject { ["mimeType"] = mimeType, ["data"] = base64 });
                SendImageStore.Clear();
            }

            return outcome;
        }

        internal static bool TryParseDataUrl(string dataUrl, out string mimeType, out string base64)
        {
            mimeType = null;
            base64 = null;
            if (string.IsNullOrEmpty(dataUrl) || !dataUrl.StartsWith("data:", StringComparison.Ordinal))
                return false;
            var comma = dataUrl.IndexOf(',');
            var marker = dataUrl.IndexOf(";base64", StringComparison.Ordinal);
            if (comma < 0 || marker < 0 || marker > comma)
                return false;
            mimeType = dataUrl.Substring(5, marker - 5);
            base64 = dataUrl.Substring(comma + 1);
            return mimeType.Length > 0 && base64.Length > 0;
        }
    }
}
