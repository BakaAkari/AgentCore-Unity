using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Tools.Safety;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.McpClient
{
    /// <summary>
    /// 外部 MCP 服务器上的一个工具，以 <c>mcp__{server}__{tool}</c> 注册进 AgentCore 的工具表，供内嵌 Chat 使用。
    /// <para>
    /// 风险：标了 <c>readOnlyHint</c> 的按只读处理，其余按高风险（网络访问 + 可能写文件）处理，经与原生工具相同的
    /// 策略、权限规则与确认流程。可见性为按需（分类 <c>MCP: {server}</c>，经 request_tools 激活）。
    /// </para>
    /// </summary>
    public sealed class McpProxyTool : IAgentTool
    {
        public const string NamePrefix = "mcp__";
        public const string CategoryPrefix = "MCP: ";

        private readonly McpClientServer _server;
        private readonly string _remoteName;

        public McpProxyTool(McpClientServer server, JObject definition)
        {
            _server = server;
            _remoteName = (string)definition["name"];
            var readOnly = (bool?)definition["annotations"]?["readOnlyHint"] == true;
            var schema = definition["inputSchema"] as JObject ?? new JObject { ["type"] = "object", ["properties"] = new JObject() };
            var description = (string)definition["description"] ?? string.Empty;
            var title = (string)definition["title"] ?? (string)definition["annotations"]?["title"];

            Metadata = new ToolMetadata(
                ToolName(server.Config.Name, _remoteName),
                $"[External MCP server '{server.Config.Name}'] " + (string.IsNullOrEmpty(title) ? string.Empty : title + ": ") + description,
                CategoryPrefix + server.Config.Name,
                (JObject)schema.DeepClone(),
                requiresMainThread: false,
                riskLevel: readOnly ? ToolRiskLevel.ReadOnly : ToolRiskLevel.High,
                capabilities: readOnly ? ToolCapability.ReadProject : ToolCapability.NetworkAccess | ToolCapability.WriteProjectFiles,
                requiresConfirmation: !readOnly,
                visibility: ToolVisibility.OnDemand);
        }

        public ToolMetadata Metadata { get; }

        public string RemoteName => _remoteName;

        /// <summary>工具名：<c>mcp__server__tool</c>，只保留字母数字下划线连字符，最长 64 字符（兼容各家 LLM 的函数名限制）。</summary>
        public static string ToolName(string server, string tool)
        {
            var name = NamePrefix + Sanitize(server) + "__" + Sanitize(tool);
            return name.Length <= 64 ? name : name.Substring(0, 64);
        }

        private static string Sanitize(string value) => Regex.Replace(value ?? string.Empty, "[^A-Za-z0-9_-]", "_");

        public async Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
        {
            var started = DateTime.UtcNow;
            try
            {
                var result = await _server.CallToolAsync(_remoteName, parameters, cancellationToken).ConfigureAwait(false);
                var text = FlattenContent(result);
                var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;
                return (bool?)result["isError"] == true ? ToolResult.Fail(text, elapsed) : ToolResult.Ok(text, elapsed);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return ToolResult.Fail($"External MCP server '{_server.Config.Name}': {ex.Message}", (DateTime.UtcNow - started).TotalMilliseconds);
            }
        }

        /// <summary>CallToolResult 的内容转成文本：text 原样，图片 / 音频注明类型与大小，资源给出 URI 与内嵌文本，附带 structuredContent。</summary>
        internal static string FlattenContent(JObject result)
        {
            var sb = new StringBuilder();
            if (result["content"] is JArray content)
            {
                foreach (var item in content.OfType<JObject>())
                {
                    if (sb.Length > 0)
                        sb.Append('\n');
                    switch ((string)item["type"])
                    {
                        case "text":
                            sb.Append((string)item["text"]);
                            break;
                        case "image":
                        case "audio":
                            sb.Append($"[{(string)item["type"]} {(string)item["mimeType"]}, {((string)item["data"])?.Length ?? 0} base64 chars omitted]");
                            break;
                        case "resource_link":
                            sb.Append($"[resource {(string)item["uri"]}] {(string)item["description"]}");
                            break;
                        case "resource":
                            sb.Append($"[resource {(string)item["resource"]?["uri"]}]\n{(string)item["resource"]?["text"]}");
                            break;
                        default:
                            sb.Append(item.ToString(Newtonsoft.Json.Formatting.None));
                            break;
                    }
                }
            }
            if (result["structuredContent"] != null && sb.Length == 0)
                sb.Append(result["structuredContent"].ToString(Newtonsoft.Json.Formatting.None));
            return sb.Length == 0 ? "(no output)" : sb.ToString();
        }
    }
}
