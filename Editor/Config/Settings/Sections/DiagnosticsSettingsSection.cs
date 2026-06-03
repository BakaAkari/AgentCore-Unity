using System;
using System.IO;
using AgentCore.Editor.Bootstrap;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Config.Settings.Sections
{
    /// <summary>
    /// Provides diagnostics and maintenance actions.
    /// </summary>
    public sealed class DiagnosticsSettingsSection : SettingsSectionBase
    {
        /// <inheritdoc />
        public override string Id => "diagnostics";

        /// <inheritdoc />
        public override string Title => "Diagnostics";

        /// <inheritdoc />
        public override string Description => "Connection checks, maintenance actions, and troubleshooting.";

        /// <inheritdoc />
        public override string Category => "Diagnostics";

        /// <inheritdoc />
        public override int Order => 1100;

        /// <inheritdoc />
        public override void Draw(AgentCoreSettingsContext context)
        {
            context.Ui.DrawCard("Configuration Summary", "Fast local checks that do not call external services.", () =>
            {
                var settings = context.Settings;
                context.Ui.DrawStatusLabel(
                    string.IsNullOrWhiteSpace(settings.llmEndpoint) ? "LLM endpoint is empty." : $"LLM endpoint: {settings.llmEndpoint}",
                    string.IsNullOrWhiteSpace(settings.llmEndpoint) ? SettingsStatusLevel.Warning : SettingsStatusLevel.Success,
                    miniLabel: true);
                context.Ui.DrawStatusLabel(
                    settings.mem0Enabled ? $"mem0 enabled: {settings.mem0Endpoint}" : "mem0 disabled.",
                    settings.mem0Enabled ? SettingsStatusLevel.Success : SettingsStatusLevel.None,
                    miniLabel: true);
                context.Ui.DrawStatusLabel(
                    settings.lightragEnabled ? $"LightRAG enabled: {settings.lightragEndpoint}" : "LightRAG disabled.",
                    settings.lightragEnabled ? SettingsStatusLevel.Success : SettingsStatusLevel.None,
                    miniLabel: true);
            });

            EditorGUILayout.Space(8);

            context.Ui.DrawCard("Project Context Files", "Open or create project-local context files used by the bootstrap pipeline.", () =>
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Open PROJECT.md", GUILayout.Width(130)))
                {
                    OpenOrCreateUserFile("PROJECT.md");
                }

                if (GUILayout.Button("Open SOUL.ext.md", GUILayout.Width(130)))
                {
                    OpenOrCreateUserFile("SOUL.ext.md");
                }

                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            });
        }

        private static void OpenOrCreateUserFile(string fileName)
        {
            var filePath = BootstrapLoader.FindUserFilePath(fileName);
            if (filePath == null)
            {
                CreateUserFile(fileName);
                filePath = BootstrapLoader.FindUserFilePath(fileName);
            }

            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                System.Diagnostics.Process.Start(filePath);
            }
        }

        private static void CreateUserFile(string fileName)
        {
            var filePath = BootstrapLoader.GetDefaultUserFilePath(fileName);
            if (filePath == null)
            {
                Debug.LogError("[AgentCore] Cannot determine project root directory.");
                return;
            }

            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            try
            {
                File.WriteAllText(filePath, BootstrapLoader.GenerateUserFileTemplate(fileName), System.Text.Encoding.UTF8);
                AssetDatabase.Refresh();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AgentCore] Failed to create {fileName}: {ex.Message}");
            }
        }

    }
}
