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
    /// MCP 调用之间串行执行（同一时刻只有一个外部调用在改编辑器状态）。
    /// </summary>
    public static class McpToolExecutor
    {
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        /// <summary>测试可替换：按客户端名构造确认提供器。</summary>
        internal static Func<string, IToolConfirmationProvider> ProviderFactory = client => new McpConfirmationProvider(client);

        public static async Task<McpCallOutcome> ExecuteAsync(string toolName, JObject arguments, string clientName, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(toolName))
                return McpCallOutcome.Error("Missing tool name.");
            if (McpToolCatalog.ExcludedTools.TryGetValue(toolName, out var reason))
                return McpCallOutcome.Error($"Tool '{toolName}' is not available over MCP ({reason}).");

            arguments ??= new JObject();
            var stopwatch = Stopwatch.StartNew();
            await Gate.WaitAsync(ct).ConfigureAwait(false);
            McpCallOutcome outcome;
            try
            {
                var call = new ToolCall
                {
                    Id = "mcp_" + Guid.NewGuid().ToString("N").Substring(0, 12),
                    Function = new FunctionCall { Name = toolName, Arguments = arguments.ToString(Formatting.None) }
                };
                var dispatcher = new ToolCallDispatcher(ToolRegistry.Instance, ProviderFactory(clientName));

                var dispatchTask = await MainThread.RunAsync(() => dispatcher.DispatchAsync(call, ct)).ConfigureAwait(false);
                var callResult = await dispatchTask.ConfigureAwait(false);
                outcome = ToOutcome(toolName, callResult?.Result);
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
            finally
            {
                Gate.Release();
            }

            McpAuditLog.Append(clientName, toolName, arguments, outcome.Status, stopwatch.Elapsed.TotalMilliseconds,
                outcome.Status == McpBridgeProtocol.StatusOk ? null : outcome.Text);
            return outcome;
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
