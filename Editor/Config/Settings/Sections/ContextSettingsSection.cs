using System;
using System.IO;
using AgentCore.Editor.Bootstrap;
using AgentCore.Editor.Config;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Config.Settings.Sections
{
    /// <summary>
    /// Configures local context and bootstrap files.
    /// </summary>
    public sealed class ContextSettingsSection : SettingsSectionBase
    {
        /// <inheritdoc />
        public override string Id => "context";

        /// <inheritdoc />
        public override string Title => "Context";

        /// <inheritdoc />
        public override string Description => "Local bootstrap files and project context collection.";

        /// <inheritdoc />
        public override string Category => "Intelligence";

        /// <inheritdoc />
        public override int Order => 400;

        /// <inheritdoc />
        public override void Draw(AgentCoreSettingsContext context)
        {
            var settings = context.Settings;

            context.Ui.DrawCard("Bootstrap Pipeline", "Controls the system prompt assembly pipeline and project-local context files.", () =>
            {
                EditorGUI.BeginChangeCheck();

                settings.bootstrapEnabled = EditorGUILayout.Toggle(
                    new GUIContent("Enabled", "启用 Bootstrap Files 系统"),
                    settings.bootstrapEnabled);

                settings.autoProjectContext = EditorGUILayout.Toggle(
                    new GUIContent("Auto Project Context", "自动收集项目上下文信息"),
                    settings.autoProjectContext);

                if (EditorGUI.EndChangeCheck())
                {
                    settings.SaveSettings();
                }
            });

            EditorGUILayout.Space(8);

            context.Ui.DrawCard("Project Files", "User-editable files included in the bootstrap prompt. PROJECT.md is recommended for VCS commit; SOUL.ext.md extends agent behavior rules.", () =>
            {
                DrawUserFileRow("PROJECT.md", "项目约定与个人偏好 — 团队共享，建议 VCS 提交");
                DrawUserFileRow("SOUL.ext.md", "Agent 行为规则扩展 — 追加到内置 SOUL，建议 VCS 提交");
            });

            EditorGUILayout.Space(8);

            context.Ui.DrawCard("Rules System", "Structured rules injected at the end of the System Prompt. Two layers: workspace-wide rules and project-specific rules. Both are recommended for VCS commit.", () =>
            {
                EditorGUI.BeginChangeCheck();

                settings.rulesEnabled = EditorGUILayout.Toggle(
                    new GUIContent("Enabled", "启用规则系统，从 rules.md 文件加载规则注入 System Prompt"),
                    settings.rulesEnabled);

                if (EditorGUI.EndChangeCheck())
                {
                    settings.SaveSettings();
                }

                EditorGUILayout.Space(4);

                DrawRulesFileRow("workspace", "Workspace Rules", "团队规则 — 适用于整个 Workspace，建议 VCS 提交");
                DrawRulesFileRow("project", "Project Rules", "项目规则 — 适用于当前 Unity 项目，建议 VCS 提交");
            });
        }

        private static void DrawUserFileRow(string fileName, string description)
        {
            var filePath = BootstrapLoader.FindUserFilePath(fileName);
            var exists = filePath != null;

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(new GUIContent(fileName, description), GUILayout.Width(140));

            if (exists)
            {
                var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
                var relativePath = filePath.StartsWith(projectRoot, StringComparison.Ordinal)
                    ? filePath.Substring(projectRoot.Length + 1).Replace('\\', '/')
                    : filePath;
                EditorGUILayout.LabelField(relativePath, EditorStyles.miniLabel);

                if (GUILayout.Button("Edit", GUILayout.Width(50)))
                {
                    System.Diagnostics.Process.Start(filePath);
                }

                if (GUILayout.Button("Show", GUILayout.Width(50)))
                {
                    EditorUtility.RevealInFinder(filePath);
                }
            }
            else
            {
                EditorGUILayout.LabelField("(not created)", EditorStyles.miniLabel);
                if (GUILayout.Button("Create", GUILayout.Width(60)))
                {
                    CreateUserFile(fileName);
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        private static void DrawRulesFileRow(string layer, string label, string description)
        {
            var filePath = layer == "workspace"
                ? RulesLoader.GetWorkspaceRulesPath()
                : RulesLoader.GetProjectRulesPath();

            var exists = filePath != null && File.Exists(filePath);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(new GUIContent(label, description), GUILayout.Width(140));

            if (exists)
            {
                var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
                var relativePath = filePath.StartsWith(projectRoot, StringComparison.Ordinal)
                    ? filePath.Substring(projectRoot.Length + 1).Replace('\\', '/')
                    : filePath;
                EditorGUILayout.LabelField(relativePath, EditorStyles.miniLabel);

                if (GUILayout.Button("Edit", GUILayout.Width(50)))
                {
                    System.Diagnostics.Process.Start(filePath);
                }

                if (GUILayout.Button("Show", GUILayout.Width(50)))
                {
                    EditorUtility.RevealInFinder(filePath);
                }
            }
            else
            {
                var defaultPath = layer == "workspace"
                    ? RulesLoader.GetWorkspaceRulesPath()
                    : RulesLoader.GetProjectRulesPath();
                EditorGUILayout.LabelField("(not created)", EditorStyles.miniLabel);
                if (GUILayout.Button("Create", GUILayout.Width(60)))
                {
                    CreateRulesFile(layer, defaultPath);
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        private static void CreateRulesFile(string layer, string filePath)
        {
            if (filePath == null)
            {
                Debug.LogError("[AgentCore] Cannot determine rules file path.");
                return;
            }

            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            try
            {
                File.WriteAllText(filePath, RulesLoader.GenerateRulesTemplate(layer), System.Text.Encoding.UTF8);
                AssetDatabase.Refresh();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AgentCore] Failed to create rules.md ({layer}): {ex.Message}");
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
