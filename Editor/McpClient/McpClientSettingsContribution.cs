using System.Linq;
using AgentCore.Editor.Extensions;
using AgentCore.Editor.L10n;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.McpClient
{
    /// <summary>
    /// Tools &amp; Extensions 页“External MCP servers”卡片：让 AgentCore 的 Chat 使用外部 MCP 服务器（Jira、Figma、内部文档等）的工具。
    /// 配置以 JSON 文件编辑（与 Claude Desktop / Claude Code 的 mcpServers 格式相同，可直接粘贴）。
    /// </summary>
    public sealed class McpClientSettingsContribution : IAgentCoreSettingsContribution
    {
        private static bool _showDetails;

        public string Id => "external-mcp-servers";
        public string Title => Loc.Tr("settings.mcpClient.title", "External MCP servers");
        public string Description => Loc.Tr(
            "settings.mcpClient.description",
            "Let the AgentCore chat use tools from MCP servers (same mcpServers format as Claude Desktop / Claude Code).");
        public int Order => 500;
        public string OwnerComponentId => null;

        public void DrawGUI()
        {
            var servers = McpClientManager.Servers;
            if (servers.Count == 0)
            {
                EditorGUILayout.LabelField(Loc.Tr("settings.mcpClient.none", "No servers configured."), EditorStyles.miniLabel);
            }
            else
            {
                foreach (var server in servers)
                {
                    var status = server.Status == "connected"
                        ? Loc.Tr("settings.mcpClient.connected", "Connected · {0} tools", server.Tools.Count)
                        : server.Status;
                    EditorGUILayout.LabelField($"{server.Config.Name} ({server.Config.Scope})", status);
                    if (!string.IsNullOrEmpty(server.Error))
                        EditorGUILayout.HelpBox(server.Error, MessageType.Warning);
                }

                _showDetails = EditorGUILayout.Foldout(_showDetails, Loc.Tr("settings.mcpClient.details", "Details"), true);
                if (_showDetails)
                {
                    foreach (var server in servers)
                    {
                        EditorGUILayout.LabelField(server.Config.Name, EditorStyles.boldLabel);
                        EditorGUILayout.SelectableLabel(server.Config.Describe(), EditorStyles.wordWrappedMiniLabel);
                        if (server.Tools.Count > 0)
                            EditorGUILayout.LabelField(Loc.Tr("settings.mcpClient.tools", "Tools: {0}", string.Join(", ", server.Tools.Select(t => t.RemoteName))), EditorStyles.wordWrappedMiniLabel);
                        if (!string.IsNullOrEmpty(server.Diagnostics))
                            EditorGUILayout.SelectableLabel(server.Diagnostics, EditorStyles.wordWrappedMiniLabel, GUILayout.MinHeight(40));
                    }
                }
            }

            foreach (var error in McpClientManager.ConfigErrors)
                EditorGUILayout.HelpBox(error, MessageType.Error);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(
                Loc.Tr("settings.mcpClient.openConfig", "Open personal config"),
                McpServerConfigStore.UserRelativePath + " " + Loc.Tr("settings.mcpClient.notInVcs", "(not in version control)"))))
                EditorUtility.OpenWithDefaultApp(McpServerConfigStore.EnsureUserFile());
            if (GUILayout.Button(Loc.Tr("settings.mcpClient.reload", "Reload"), GUILayout.MinWidth(70)))
                McpClientManager.Reload();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField(
                Loc.Tr(
                    "settings.mcpClient.help",
                    "Server tools appear in the chat as mcp__<server>__<tool> (activated on demand). They follow the same review level and permission rules as AgentCore's own tools; only tools marked read-only by the server skip confirmation."),
                EditorStyles.wordWrappedMiniLabel);
        }
    }
}
