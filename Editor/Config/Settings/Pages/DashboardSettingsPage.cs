using System;
using System.IO;
using System.Linq;
using AgentCore.Editor.Extensions;
using AgentCore.Editor.Tools;
using AgentCore.Editor.UI;
using AgentCore.Editor.Utils;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Config.Settings.Pages
{
    /// <summary>
    /// Dashboard page showing setup status, quick actions, and package info.
    /// </summary>
    public sealed class DashboardSettingsPage : IAgentCoreSettingsPage
    {
        /// <inheritdoc />
        public string Id => "dashboard";

        /// <inheritdoc />
        public string Title => "Dashboard";

        /// <inheritdoc />
        public string Description => "Overview of AgentCore configuration status and quick actions.";

        /// <inheritdoc />
        public int Order => 100;

        /// <inheritdoc />
        public void OnActivate(AgentCoreSettingsContext context) { }

        /// <inheritdoc />
        public void OnDeactivate(AgentCoreSettingsContext context) { }

        /// <inheritdoc />
        public void Draw(AgentCoreSettingsContext context)
        {
            // v1.4.2: package info merged into the bottom of Setup Status to save a full card.
            DrawSetupStatusCard(context);
            EditorGUILayout.Space(8);
            DrawQuickActionsCard(context);
        }

        private static void DrawSetupStatusCard(AgentCoreSettingsContext context)
        {
            var settings = context.Settings;

            context.Ui.DrawCard("Setup Status", "Current configuration state for core services.", () =>
            {
                // LLM — required, so missing endpoint is an Error.
                DrawServiceStatusRow(context,
                    "LLM",
                    isEnabled: !string.IsNullOrWhiteSpace(settings.llmEndpoint),
                    enabledDetail: settings.llmModel,
                    disabledIsError: true);

                // Memory (mem0) — optional.
                DrawServiceStatusRow(context,
                    "Memory",
                    isEnabled: settings.mem0Enabled,
                    enabledDetail: settings.mem0Endpoint,
                    disabledIsError: false);

                // Knowledge (LightRAG) — optional.
                DrawServiceStatusRow(context,
                    "Knowledge",
                    isEnabled: settings.lightragEnabled,
                    enabledDetail: settings.lightragEndpoint,
                    disabledIsError: false);

                // VCS — optional component.
                DrawServiceStatusRow(context,
                    "VCS",
                    isEnabled: OptionalComponentManager.IsVcsEnabled(),
                    enabledDetail: null,
                    disabledIsError: false);

                // Tools — informational count.
                var allTools = ToolRegistry.Instance.GetAllTools();
                if (allTools != null && allTools.Count > 0)
                {
                    var enabledCount = allTools.Count(tool =>
                        tool?.Metadata != null && !settings.IsToolDisabled(tool.Metadata.Name, tool.Metadata.Category));
                    context.Ui.DrawStatusLabel(
                        $"Tools: {enabledCount}/{allTools.Count} enabled",
                        SettingsStatusLevel.None,
                        miniLabel: true);
                }
                else
                {
                    context.Ui.DrawStatusLabel(
                        "Tools: not initialized yet",
                        SettingsStatusLevel.Warning,
                        miniLabel: true);
                }

                // Package footer — merged in from the removed Package card.
                EditorGUILayout.Space(6);
                var footerStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleLeft,
                };
                footerStyle.normal.textColor = new Color(0.5f, 0.5f, 0.5f);
                EditorGUILayout.LabelField($"com.agentcore.unity  v{GetPackageVersion()}", footerStyle);
            });
        }

        /// <summary>
        /// Draws a single status row with a leading badge + service name + optional detail.
        /// Disabled services are dimmed (gray) and clearly labeled instead of using the default label color.
        /// </summary>
        private static void DrawServiceStatusRow(
            AgentCoreSettingsContext context,
            string serviceName,
            bool isEnabled,
            string enabledDetail,
            bool disabledIsError)
        {
            string text;
            SettingsStatusLevel level;

            if (isEnabled)
            {
                text = string.IsNullOrEmpty(enabledDetail)
                    ? $"● {serviceName}: Enabled"
                    : $"● {serviceName}: Enabled ({enabledDetail})";
                level = SettingsStatusLevel.Success;
            }
            else if (disabledIsError)
            {
                text = $"✗ {serviceName}: Missing configuration";
                level = SettingsStatusLevel.Error;
            }
            else
            {
                text = $"○ {serviceName}: Disabled";
                level = SettingsStatusLevel.None;
            }

            context.Ui.DrawStatusLabel(text, level, miniLabel: true);
        }

        private static void DrawQuickActionsCard(AgentCoreSettingsContext context)
        {
            context.Ui.DrawCard("Quick Actions", "Common shortcuts and maintenance actions.", () =>
            {
                EditorGUILayout.BeginHorizontal();

                if (GUILayout.Button("Open Chat Window", GUILayout.Width(140)))
                {
                    ChatWindow.ShowWindow();
                }

                if (GUILayout.Button("Refresh Tool Registry", GUILayout.Width(150)))
                {
                    AgentCore.Editor.Tools.Infrastructure.ToolAutoDiscovery.DiscoverAndRegisterAll();
                    Debug.Log("[AgentCore] Tool registry refreshed.");
                }

                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();

                if (GUILayout.Button("Open Logs", GUILayout.Width(140)))
                {
                    var logPath = GetEditorLogPath();
                    if (!string.IsNullOrEmpty(logPath) && File.Exists(logPath))
                    {
                        EditorUtility.OpenWithDefaultApp(logPath);
                    }
                    else
                    {
                        EditorUtility.DisplayDialog("Open Logs", "Could not locate the Unity Editor log file.", "OK");
                    }
                }

                if (GUILayout.Button("Reset Settings", GUILayout.Width(150)))
                {
                    if (EditorUtility.DisplayDialog(
                        "Reset Settings",
                        "This will reset all AgentCore settings to their default values.\n\nThis action cannot be undone.",
                        "Reset",
                        "Cancel"))
                    {
                        context.Settings.ResetToDefaults();
                        Debug.Log("[AgentCore] Settings reset to defaults.");
                    }
                }

                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();

                if (GUILayout.Button("Clear Secure Keys", GUILayout.Width(150)))
                {
                    if (EditorUtility.DisplayDialog(
                        "Clear Secure Keys",
                        "This will clear all stored API keys (LLM, mem0, LightRAG, Compression LLM).\n\nThis action cannot be undone.",
                        "Clear",
                        "Cancel"))
                    {
                        SecureKeyStorage.ClearAll();
                        Debug.Log("[AgentCore] All secure keys cleared.");
                    }
                }

                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            });
        }

        private static string GetEditorLogPath()
        {
            var os = SystemInfo.operatingSystemFamily;
            if (os == OperatingSystemFamily.Windows)
            {
                var localAppData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(localAppData, "Unity", "Editor", "Editor.log");
            }
            else if (os == OperatingSystemFamily.MacOSX)
            {
                var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal);
                return Path.Combine(home, "Library", "Logs", "Unity", "Editor.log");
            }
            else
            {
                var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal);
                return Path.Combine(home, ".config", "unity3d", "Editor.log");
            }
        }

        private static string GetPackageVersion()
        {
            try
            {
                var packagePath = "Packages/com.agentcore.unity/package.json";
                if (File.Exists(packagePath))
                {
                    var json = File.ReadAllText(packagePath);
                    var jobj = JsonHelper.ParseObject(json);
                    return JsonHelper.GetString(jobj, "version", "unknown");
                }

                var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(AgentCoreSettings).Assembly);
                if (packageInfo != null)
                {
                    return packageInfo.version;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentCore] Failed to read package version: {ex.Message}");
            }

            return "unknown";
        }
    }
}
