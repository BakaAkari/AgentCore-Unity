using System.Linq;
using AgentCore.Editor.Extensions;
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
        public string Title => "External MCP servers";
        public string Description => "Let the AgentCore chat use tools from MCP servers (same mcpServers format as Claude Desktop / Claude Code).";
        public int Order => 500;
        public string OwnerComponentId => null;

        public void DrawGUI()
        {
            var servers = McpClientManager.Servers;
            if (servers.Count == 0)
            {
                EditorGUILayout.LabelField("No servers configured.", EditorStyles.miniLabel);
            }
            else
            {
                foreach (var server in servers)
                {
                    var status = server.Status == "connected" ? $"Connected · {server.Tools.Count} tools" : server.Status;
                    EditorGUILayout.LabelField($"{server.Config.Name} ({server.Config.Scope})", status);
                    if (!string.IsNullOrEmpty(server.Error))
                        EditorGUILayout.HelpBox(server.Error, MessageType.Warning);
                }

                _showDetails = EditorGUILayout.Foldout(_showDetails, "Details", true);
                if (_showDetails)
                {
                    foreach (var server in servers)
                    {
                        EditorGUILayout.LabelField(server.Config.Name, EditorStyles.boldLabel);
                        EditorGUILayout.SelectableLabel(server.Config.Describe(), EditorStyles.wordWrappedMiniLabel);
                        if (server.Tools.Count > 0)
                            EditorGUILayout.LabelField("Tools: " + string.Join(", ", server.Tools.Select(t => t.RemoteName)), EditorStyles.wordWrappedMiniLabel);
                        if (!string.IsNullOrEmpty(server.Diagnostics))
                            EditorGUILayout.SelectableLabel(server.Diagnostics, EditorStyles.wordWrappedMiniLabel, GUILayout.MinHeight(40));
                    }
                }
            }

            foreach (var error in McpClientManager.ConfigErrors)
                EditorGUILayout.HelpBox(error, MessageType.Error);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("Open personal config", McpServerConfigStore.UserRelativePath + " (not in version control)")))
                EditorUtility.OpenWithDefaultApp(McpServerConfigStore.EnsureFile(McpServerConfigStore.UserFile));
            if (GUILayout.Button(new GUIContent("Open project config", McpServerConfigStore.ProjectRelativePath + " (shared with the team)")))
                EditorUtility.OpenWithDefaultApp(McpServerConfigStore.EnsureFile(McpServerConfigStore.ProjectFile));
            if (GUILayout.Button("Reload", GUILayout.Width(70)))
                McpClientManager.Reload();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField(
                "Server tools appear in the chat as mcp__<server>__<tool> (activated on demand). They follow the same review level and permission rules as AgentCore's own tools; only tools marked read-only by the server skip confirmation.",
                EditorStyles.wordWrappedMiniLabel);
        }
    }
}
