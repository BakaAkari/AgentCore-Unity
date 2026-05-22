using System;
using System.IO;
using System.Linq;
using AgentCore.Editor.Tools;
using AgentCore.Editor.Utils;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Config.Settings.Sections
{
    /// <summary>
    /// Displays high-level AgentCore settings status.
    /// </summary>
    public sealed class GeneralSettingsSection : SettingsSectionBase
    {
        /// <inheritdoc />
        public override string Id => "general";

        /// <inheritdoc />
        public override string Title => "General";

        /// <inheritdoc />
        public override string Description => "Overview, package status, and quick entry points.";

        /// <inheritdoc />
        public override string Category => "Core";

        /// <inheritdoc />
        public override int Order => 100;

        /// <inheritdoc />
        public override void Draw(AgentCoreSettingsContext context)
        {
            var settings = context.Settings;
            context.Ui.DrawCard("Status Overview", "Current high-level configuration status.", () =>
            {
                DrawStatusRows(context, settings);
            });

            EditorGUILayout.Space(8);

            context.Ui.DrawCard("Package", "Installed package identity and version.", () =>
            {
                EditorGUILayout.LabelField("Package", "com.agentcore.unity");
                EditorGUILayout.LabelField("Version", GetPackageVersion());
                EditorGUILayout.LabelField("Description", "Unity Editor embedded AI Agent plugin");
            });
        }

        private static void DrawStatusRows(AgentCoreSettingsContext context, AgentCoreSettings settings)
        {
            var llmStatus = string.IsNullOrWhiteSpace(settings.llmEndpoint)
                ? "Not configured"
                : $"{settings.llmModel} @ {settings.llmEndpoint}";
            context.Ui.DrawStatusLabel(
                $"LLM: {llmStatus}",
                string.IsNullOrWhiteSpace(settings.llmEndpoint) ? SettingsStatusLevel.Warning : SettingsStatusLevel.Success,
                miniLabel: true);

            context.Ui.DrawStatusLabel(
                settings.mem0Enabled ? $"mem0: Enabled @ {settings.mem0Endpoint}" : "mem0: Disabled",
                settings.mem0Enabled ? SettingsStatusLevel.Success : SettingsStatusLevel.None,
                miniLabel: true);

            context.Ui.DrawStatusLabel(
                settings.lightragEnabled ? $"LightRAG: Enabled @ {settings.lightragEndpoint}" : "LightRAG: Disabled",
                settings.lightragEnabled ? SettingsStatusLevel.Success : SettingsStatusLevel.None,
                miniLabel: true);

            var allTools = ToolRegistry.Instance.GetAllTools();
            if (allTools != null && allTools.Count > 0)
            {
                var enabledCount = allTools.Count(tool =>
                    tool.Metadata != null && !settings.IsToolDisabled(tool.Metadata.Name, tool.Metadata.Category));
                context.Ui.DrawStatusLabel($"Tools: {enabledCount}/{allTools.Count} enabled", SettingsStatusLevel.None, miniLabel: true);
            }
            else
            {
                context.Ui.DrawStatusLabel("Tools: not initialized yet", SettingsStatusLevel.Warning, miniLabel: true);
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
