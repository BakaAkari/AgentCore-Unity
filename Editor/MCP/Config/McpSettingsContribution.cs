using AgentCore.Editor.Components.MCP.Bridge;
using AgentCore.Editor.Components.MCP.Relay;
using AgentCore.Editor.Extensions;
using AgentCore.Editor.L10n;
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
        public string Title => Loc.Tr("settings.mcp.title", "MCP Server");
        public string Description => Loc.Tr("settings.mcp.description", "Expose AgentCore tools to MCP clients.");
        public int Order => 400;
        public string OwnerComponentId => "mcp";

        public void DrawGUI()
        {
            EditorGUILayout.LabelField(
                Loc.Tr("settings.mcp.status", "Status"),
                McpBridgeService.IsRunning ? Loc.Tr("settings.mcp.status.ready", "Ready") : Loc.Tr("settings.mcp.status.notRunning", "Not running"));

            var clients = McpBridgeService.ConnectedClients;
            EditorGUILayout.LabelField(
                Loc.Tr("settings.mcp.clients", "Connected clients"),
                clients.Length == 0 ? Loc.Tr("settings.mcp.clients.none", "None") : string.Join(", ", clients));

            if (!string.IsNullOrEmpty(McpBridgeService.LastCallSummary))
                EditorGUILayout.LabelField(Loc.Tr("settings.mcp.lastCall", "Last call"), McpBridgeService.LastCallSummary);
            if (GUILayout.Button(new GUIContent(
                    Loc.Tr("settings.mcp.openActivity", "Open activity"),
                    Loc.Tr("settings.mcp.openActivityTooltip", "What MCP clients have done in this editor: tools, arguments and results."))))
                AgentCore.Editor.UI.ExternalActivityWindow.ShowWindow();

            if (!string.IsNullOrEmpty(McpBridgeService.LastError))
                EditorGUILayout.HelpBox(McpBridgeService.LastError, MessageType.Warning);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Loc.Tr("settings.mcp.connect.title", "Connect a client"), EditorStyles.boldLabel);
            DrawClientConfig();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Loc.Tr("settings.mcp.review.title", "Review"), EditorStyles.boldLabel);
            DrawReviewSettings();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Loc.Tr("settings.mcp.http.title", "Local HTTP endpoint"), EditorStyles.boldLabel);
            DrawHttpEndpoint();
        }

        private static void DrawHttpEndpoint()
        {
            EditorGUI.BeginChangeCheck();
            var enabled = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    Loc.Tr("settings.mcp.http.enable", "Enable (for clients that connect by URL)"),
                    Loc.Tr("settings.mcp.http.enableTooltip", "Runs an HTTP endpoint on 127.0.0.1 in a separate process that survives script reloads. Most users only need the stdio setup above.")),
                McpSettings.HttpEnabled);
            if (EditorGUI.EndChangeCheck())
            {
                McpSettings.HttpEnabled = enabled;
                McpHttpEndpoint.Apply();
            }

            if (!McpSettings.HttpEnabled)
                return;

            EditorGUILayout.LabelField(Loc.Tr("settings.mcp.http.url", "URL"), McpHttpEndpoint.Url);
            EditorGUILayout.LabelField(
                Loc.Tr("settings.mcp.status", "Status"),
                McpHttpEndpoint.IsRunning ? Loc.Tr("settings.mcp.http.running", "Running") : Loc.Tr("settings.mcp.http.stopped", "Starting / stopped"));
            if (!string.IsNullOrEmpty(McpHttpEndpoint.LastError))
                EditorGUILayout.HelpBox(McpHttpEndpoint.LastError, MessageType.Warning);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.Tr("settings.mcp.http.copyClaude", "Copy Claude Code command (HTTP)")))
                Copy(McpHttpEndpoint.ClaudeCodeCommand(McpHttpEndpoint.Url, McpHttpEndpoint.Token), null);
            if (GUILayout.Button(Loc.Tr("settings.mcp.http.copyCodex", "Copy Codex config (HTTP)")))
                Copy(McpHttpEndpoint.CodexToml(McpHttpEndpoint.Url, McpHttpEndpoint.Token), null);
            if (!McpHttpEndpoint.IsRunning && GUILayout.Button(Loc.Tr("settings.mcp.http.restart", "Restart"), GUILayout.MinWidth(70)))
                McpHttpEndpoint.Apply();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField(
                Loc.Tr(
                    "settings.mcp.http.tokenWarning",
                    "The copied configuration contains an access token for this project. Anyone with it can use AgentCore's tools on this machine; do not share it."),
                EditorStyles.wordWrappedMiniLabel);
        }

        // Built per draw so a UI language switch applies immediately (no cached localized text).
        private static GUIContent[] BuildReviewLevelOptions() => new[]
        {
            new GUIContent(
                Loc.Tr("settings.mcp.review.follow", "Follow AgentCore chat"),
                Loc.Tr("settings.mcp.review.followTooltip", "Same review level as the AgentCore chat, including YOLO and permission rules.")),
            new GUIContent(
                Loc.Tr("settings.mcp.review.trust", "Trust MCP clients"),
                Loc.Tr("settings.mcp.review.trustTooltip", "Calls from MCP clients run without confirmation (the client asks you itself). Permission deny rules are skipped, like YOLO.")),
        };

        private static GUIContent[] BuildConfirmLocationOptions() => new[]
        {
            new GUIContent(
                Loc.Tr("settings.mcp.review.inUnity", "AgentCore window in Unity"),
                Loc.Tr("settings.mcp.review.inUnityTooltip", "Confirmation cards appear in the AgentCore window, labelled with the client.")),
            new GUIContent(
                Loc.Tr("settings.mcp.review.inClient", "MCP client when supported"),
                Loc.Tr("settings.mcp.review.inClientTooltip", "Ask in the client (MCP elicitation). Clients without support fall back to the AgentCore window.")),
        };

        private static void DrawReviewSettings()
        {
            EditorGUI.BeginChangeCheck();
            var level = EditorGUILayout.Popup(new GUIContent(Loc.Tr("settings.mcp.review.level", "Review level")), (int)McpSettings.ReviewLevel, BuildReviewLevelOptions());
            if (EditorGUI.EndChangeCheck())
                McpSettings.ReviewLevel = (McpReviewLevel)level;

            using (new EditorGUI.DisabledScope(McpSettings.ReviewLevel == McpReviewLevel.TrustClient))
            {
                EditorGUI.BeginChangeCheck();
                var location = EditorGUILayout.Popup(new GUIContent(Loc.Tr("settings.mcp.review.confirmIn", "Confirm in")), (int)McpSettings.ConfirmLocation, BuildConfirmLocationOptions());
                if (EditorGUI.EndChangeCheck())
                    McpSettings.ConfirmLocation = (McpConfirmLocation)location;
            }

            if (McpSettings.ReviewLevel == McpReviewLevel.TrustClient)
            {
                EditorGUILayout.HelpBox(
                    Loc.Tr(
                        "settings.mcp.review.trustWarning",
                        "MCP clients run every tool without asking here, including deletes and script changes. Version control is your safety net."),
                    MessageType.Info);
            }
        }

        private static void DrawClientConfig()
        {
            var build = McpRelayBuilder.Last;
            if (build == null)
            {
                EditorGUILayout.LabelField(Loc.Tr("settings.mcp.relay.preparing", "Preparing the relay..."), EditorStyles.miniLabel);
                if (GUILayout.Button(Loc.Tr("settings.mcp.relay.retry", "Retry")))
                    McpRelayBuilder.EnsureBuiltAsync();
                return;
            }

            if (!build.Success)
            {
                EditorGUILayout.HelpBox(
                    Loc.Tr("settings.mcp.relay.buildFailed", "The AgentCore relay could not be built:\n{0}", build.Error),
                    MessageType.Error);
                if (GUILayout.Button(Loc.Tr("settings.mcp.relay.retry", "Retry")))
                    McpRelayBuilder.EnsureBuiltAsync();
                return;
            }

            if (!string.IsNullOrEmpty(build.Warning))
                EditorGUILayout.HelpBox(build.Warning, MessageType.Info);

            var copiedMono = EditorPrefs.GetString(CopiedMonoKey, string.Empty);
            if (!string.IsNullOrEmpty(copiedMono) && copiedMono != build.MonoPath)
            {
                EditorGUILayout.HelpBox(
                    Loc.Tr(
                        "settings.mcp.relay.monoChanged",
                        "The Unity editor path changed since the configuration was copied (different Unity version). Copy it again and re-add it to your client."),
                    MessageType.Warning);
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(
                    Loc.Tr("settings.mcp.connect.copyClaude", "Copy Claude Code command"),
                    Loc.Tr("settings.mcp.connect.copyClaudeTooltip", "Run the copied command once in a terminal. Adds the 'agentcore' MCP server for your user account."))))
            {
                Copy(McpClientConfig.ClaudeCodeCommand(build.MonoPath, build.RelayPath, McpPaths.ProjectRoot), build.MonoPath);
            }

            if (GUILayout.Button(new GUIContent(
                    Loc.Tr("settings.mcp.connect.copyCodex", "Copy Codex config"),
                    Loc.Tr("settings.mcp.connect.copyCodexTooltip", "Paste the copied block into ~/.codex/config.toml (replace an existing [mcp_servers.agentcore] block)."))))
            {
                Copy(McpClientConfig.CodexToml(build.MonoPath, build.RelayPath, McpPaths.ProjectRoot), build.MonoPath);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField(
                Loc.Tr(
                    "settings.mcp.connect.help",
                    "Claude Code: paste the command into a terminal. Codex: paste the block into ~/.codex/config.toml. Restart the client afterwards."),
                EditorStyles.wordWrappedMiniLabel);
        }

        private static void Copy(string text, string monoPath)
        {
            EditorGUIUtility.systemCopyBuffer = text;
            if (monoPath != null)
            {
                EditorPrefs.SetString(CopiedMonoKey, monoPath);
                Debug.Log("[AgentCore][MCP] Copied to clipboard:\n" + text);
            }
            else
            {
                // HTTP 配置含访问令牌：不写进日志
                Debug.Log("[AgentCore][MCP] Copied the HTTP configuration to the clipboard.");
            }
        }
    }
}
