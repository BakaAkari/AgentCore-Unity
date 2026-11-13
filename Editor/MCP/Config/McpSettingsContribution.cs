using AgentCore.Editor.Components.MCP.Bridge;
using AgentCore.Editor.Extensions;
using UnityEditor;

namespace AgentCore.Editor.Components.MCP.Config
{
    /// <summary>
    /// Tools &amp; Extensions 页 MCP Server 卡片：运行状态、已连接客户端、最近一次调用。
    /// 端口、令牌、超时等内部参数不暴露（ADR-17）。
    /// </summary>
    public sealed class McpSettingsContribution : IAgentCoreSettingsContribution
    {
        public string Id => "mcp-settings";
        public string Title => "MCP Server";
        public string Description => "Expose AgentCore tools to MCP clients.";
        public int Order => 400;
        public string OwnerComponentId => "mcp";

        public void DrawGUI()
        {
            EditorGUILayout.LabelField("Status", McpBridgeService.IsRunning ? "Ready" : "Not running");

            var clients = McpBridgeService.ConnectedClients;
            EditorGUILayout.LabelField("Connected clients", clients.Length == 0 ? "None" : string.Join(", ", clients));

            if (!string.IsNullOrEmpty(McpBridgeService.LastCallSummary))
                EditorGUILayout.LabelField("Last call", McpBridgeService.LastCallSummary);

            if (!string.IsNullOrEmpty(McpBridgeService.LastError))
                EditorGUILayout.HelpBox(McpBridgeService.LastError, MessageType.Warning);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(
                "External clients follow the review level of the AgentCore chat (including YOLO); confirmations appear in the AgentCore window.",
                EditorStyles.wordWrappedMiniLabel);
        }
    }
}
