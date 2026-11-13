using AgentCore.Editor.Components.MCP.Bridge;
using AgentCore.Editor.Components.MCP.Relay;
using AgentCore.Editor.Extensions;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Components.MCP.Config
{
    /// <summary>
    /// Tools &amp; Extensions 页 MCP Server 卡片：运行状态、已连接客户端、最近一次调用、复制 Claude Code / Codex 配置。
    /// 端口、令牌、超时等内部参数不暴露（ADR-17）。
    /// </summary>
    public sealed class McpSettingsContribution : IAgentCoreSettingsContribution
    {
        /// <summary>上次复制配置时的 Mono 路径（按工程区分）：换了 Unity 版本后提示重新复制。</summary>
        private static string CopiedMonoKey => "AgentCore.Mcp.CopiedMonoPath." + McpPaths.ProjectRoot;

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
            EditorGUILayout.LabelField("Connect a client", EditorStyles.boldLabel);
            DrawClientConfig();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(
                "External clients follow the review level of the AgentCore chat (including YOLO); confirmations appear in the AgentCore window.",
                EditorStyles.wordWrappedMiniLabel);
        }

        private static void DrawClientConfig()
        {
            var build = McpRelayBuilder.Last;
            if (build == null)
            {
                EditorGUILayout.LabelField("Preparing the relay...", EditorStyles.miniLabel);
                if (GUILayout.Button("Retry"))
                    McpRelayBuilder.EnsureBuiltAsync();
                return;
            }

            if (!build.Success)
            {
                EditorGUILayout.HelpBox("The AgentCore relay could not be built:\n" + build.Error, MessageType.Error);
                if (GUILayout.Button("Retry"))
                    McpRelayBuilder.EnsureBuiltAsync();
                return;
            }

            if (!string.IsNullOrEmpty(build.Warning))
                EditorGUILayout.HelpBox(build.Warning, MessageType.Info);

            var copiedMono = EditorPrefs.GetString(CopiedMonoKey, string.Empty);
            if (!string.IsNullOrEmpty(copiedMono) && copiedMono != build.MonoPath)
            {
                EditorGUILayout.HelpBox(
                    "The Unity editor path changed since the configuration was copied (different Unity version). Copy it again and re-add it to your client.",
                    MessageType.Warning);
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("Copy Claude Code command",
                    "Run the copied command once in a terminal. Adds the 'agentcore' MCP server for your user account.")))
            {
                Copy(McpClientConfig.ClaudeCodeCommand(build.MonoPath, build.RelayPath, McpPaths.ProjectRoot), build.MonoPath);
            }

            if (GUILayout.Button(new GUIContent("Copy Codex config",
                    "Paste the copied block into ~/.codex/config.toml (replace an existing [mcp_servers.agentcore] block).")))
            {
                Copy(McpClientConfig.CodexToml(build.MonoPath, build.RelayPath, McpPaths.ProjectRoot), build.MonoPath);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField(
                "Claude Code: paste the command into a terminal. Codex: paste the block into ~/.codex/config.toml. Restart the client afterwards.",
                EditorStyles.wordWrappedMiniLabel);
        }

        private static void Copy(string text, string monoPath)
        {
            EditorGUIUtility.systemCopyBuffer = text;
            EditorPrefs.SetString(CopiedMonoKey, monoPath);
            Debug.Log("[AgentCore][MCP] Copied to clipboard:\n" + text);
        }
    }
}
