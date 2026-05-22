using System;
using System.IO;
using AgentCore.Editor.Bootstrap;
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

            context.Ui.DrawCard("User Files", "Project-local context files that AgentCore includes in the bootstrap prompt when available.", () =>
            {
                DrawUserFileRow("MEMORY.md", "本地知识文件 — Agent 可参考的项目知识和上下文");
                DrawUserFileRow("USER.md", "用户偏好文件 — 定义 Agent 的行为偏好和规则");
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
                File.WriteAllText(filePath, GenerateUserFileTemplate(fileName), System.Text.Encoding.UTF8);
                AssetDatabase.Refresh();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AgentCore] Failed to create {fileName}: {ex.Message}");
            }
        }

        private static string GenerateUserFileTemplate(string fileName)
        {
            if (fileName == "MEMORY.md")
            {
                return "# MEMORY.md — 项目知识库\n\n## 项目概述\n\n<!-- 在此描述你的项目，Agent 会据此理解项目背景。 -->\n";
            }

            return "# USER.md — Agent 行为预设\n\n## 语言与沟通\n\n- 使用中文回复，技术术语保留英文原文。\n";
        }
    }
}
