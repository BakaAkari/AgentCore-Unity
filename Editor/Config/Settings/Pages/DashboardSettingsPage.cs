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
            DrawSetupStatusCard(context);
            EditorGUILayout.Space(8);
            DrawQuickActionsCard(context);
            EditorGUILayout.Space(8);
            DrawPackageInfoCard(context);
        }

        private static void DrawSetupStatusCard(AgentCoreSettingsContext context)
        {
            var settings = context.Settings;

            context.Ui.DrawCard("Setup Status", "Current configuration state for core services.", () =>
            {
                // LLM
                var llmConfigured = !string.IsNullOrWhiteSpace(settings.llmEndpoint);
                var llmText = llmConfigured
                    ? $"LLM: Configured ({settings.llmModel})"
                    : "LLM: Missing Endpoint";
                context.Ui.DrawStatusLabel(
                    llmText,
                    llmConfigured ? SettingsStatusLevel.Success : SettingsStatusLevel.Error,
                    miniLabel: true);

                // Memory
                var mem0Text = settings.mem0Enabled
                    ? $"Memory: Enabled ({settings.mem0Endpoint})"
                    : "Memory: Disabled";
                context.Ui.DrawStatusLabel(
                    mem0Text,
                    settings.mem0Enabled ? SettingsStatusLevel.Success : SettingsStatusLevel.None,
                    miniLabel: true);

                // Knowledge
                var ragText = settings.lightragEnabled
                    ? $"Knowledge: Enabled ({settings.lightragEndpoint})"
                    : "Knowledge: Disabled";
                context.Ui.DrawStatusLabel(
                    ragText,
                    settings.lightragEnabled ? SettingsStatusLevel.Success : SettingsStatusLevel.None,
                    miniLabel: true);

                // VCS
                var vcsEnabled = OptionalComponentManager.IsVcsEnabled();
                var vcsText = vcsEnabled ? "VCS: Enabled" : "VCS: Disabled";
                context.Ui.DrawStatusLabel(
                    vcsText,
                    vcsEnabled ? SettingsStatusLevel.Success : SettingsStatusLevel.None,
                    miniLabel: true);

                // Tools
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
            });
        }

        private static void DrawQuickActionsCard(AgentCoreSettingsContext context)
        {
            context.Ui.DrawCard("Quick Actions", "Common shortcuts to AgentCore features.", () =>
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
            });
        }

        private static void DrawPackageInfoCard(AgentCoreSettingsContext context)
        {
            context.Ui.DrawCard("Package", "Installed package identity and version.", () =>
            {
                EditorGUILayout.LabelField("Package", "com.agentcore.unity");
                EditorGUILayout.LabelField("Version", GetPackageVersion());
                EditorGUILayout.LabelField("Description", "Unity Editor embedded AI Agent plugin");
            });
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
