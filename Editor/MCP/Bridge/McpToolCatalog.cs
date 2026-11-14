using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using AgentCore.Editor.Tools;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Tools.Safety;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.Components.MCP.Bridge
{
    /// <summary>
    /// 暴露给 MCP 客户端的工具列表：AgentCore 全部已启用工具（忽略 OnDemand 可见性，外部客户端自己管理工具上下文），
    /// 去掉只在内嵌 Chat 里有意义的工具，按名称排序（规范建议确定性顺序，便于客户端缓存与 prompt 缓存）。
    /// </summary>
    public static class McpToolCatalog
    {
        /// <summary>不暴露给 MCP 的工具及原因。</summary>
        public static readonly IReadOnlyDictionary<string, string> ExcludedTools = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["request_tools"] = "AgentCore's own lazy tool loading; MCP clients receive the full list",
            ["ask_user"] = "asks the user inside the AgentCore chat; MCP clients talk to their user directly",
            ["load_skill"] = "loads a skill into the AgentCore chat context, not into the MCP client",
            ["search_sessions"] = "searches AgentCore chat history, which is not the MCP client's conversation",
            ["manage_memory"] = "edits AgentCore's own long-term memory for its chat agent",
        };

        public sealed class Snapshot
        {
            public JArray Tools;
            public string SchemaHash;
        }

        /// <summary>构建工具列表（主线程：读取设置与工具注册表）。</summary>
        public static Snapshot Build()
        {
            ToolAutoDiscovery.DiscoverAndRegisterAll();

            var tools = new JArray();
            foreach (var definition in ToolDefinitionBuilder.BuildAllEnabled()
                         .Where(d => d?.Function != null && !ExcludedTools.ContainsKey(d.Function.Name))
                         // 外部 MCP 服务器的代理工具（AgentCore 作为客户端接入的）不再转出去
                         .Where(d => !d.Function.Name.StartsWith(AgentCore.Editor.McpClient.McpProxyTool.NamePrefix, StringComparison.Ordinal))
                         .OrderBy(d => d.Function.Name, StringComparer.Ordinal))
            {
                var tool = new JObject
                {
                    ["name"] = definition.Function.Name,
                    ["description"] = definition.Function.Description ?? string.Empty,
                    ["inputSchema"] = definition.Function.Parameters != null
                        ? definition.Function.Parameters.DeepClone()
                        : (JToken)new JObject { ["type"] = "object", ["properties"] = new JObject() }
                };

                // 纯只读工具标 readOnlyHint：客户端（如 Codex）据此可免确认直接调用；其余工具不标，由客户端按自己的规则询问
                var metadata = ToolRegistry.Instance.GetTool(definition.Function.Name)?.Metadata;
                if (metadata != null && metadata.RiskLevel == ToolRiskLevel.ReadOnly)
                    tool["annotations"] = new JObject { ["readOnlyHint"] = true };

                tools.Add(tool);
            }

            return new Snapshot { Tools = tools, SchemaHash = ComputeHash(tools) };
        }

        public static string ComputeHash(JArray tools)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(tools.ToString(Formatting.None)));
                var sb = new StringBuilder(16);
                for (var i = 0; i < 8; i++)
                    sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
