using System;
using System.IO;
using AgentCore.Editor.Bootstrap;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Config.Settings.Pages
{
    /// <summary>
    /// Context &amp; Memory settings page — context sources, budget, compression, memory, and knowledge base.
    /// </summary>
    /// <remarks>
    /// v1.4.2: Memory Service, Knowledge Base and Separate Compression LLM all render as
    /// unified "service cards" (see <see cref="AgentCoreSettingsUi.DrawServiceCard"/>).
    /// When the service is disabled, only the title + status badge + Enable toggle are shown;
    /// configuration fields (endpoint, API key, auto-memory, model name) render only after
    /// the user opts in. This significantly reduces default visual density for optional cloud
    /// services and matches the AGENTS.md §10.1 "connection-type settings" pattern.
    /// </remarks>
    public sealed class ContextMemorySettingsPage : IAgentCoreSettingsPage
    {
        private const string Mem0ApiKeyDisplayKey = "context-memory.mem0.apiKeyDisplay";
        private const string LightRagApiKeyDisplayKey = "context-memory.lightrag.apiKeyDisplay";

        // Foldout keys for advanced / optional configuration groups nested inside service cards.
        private const string AutoMemoryFoldoutKey = "context-memory.auto-memory";

        /// <inheritdoc />
        public string Id => "context-memory";

        /// <inheritdoc />
        public string Title => "Context & Memory";

        /// <inheritdoc />
        public string Description => "Configure context sources, budget, compression, memory service, and knowledge base.";

        /// <inheritdoc />
        public int Order => 300;

        /// <inheritdoc />
        public void OnActivate(AgentCoreSettingsContext context)
        {
            EnsureApiKeyDisplays(context);
        }

        /// <inheritdoc />
        public void OnDeactivate(AgentCoreSettingsContext context) { }

        /// <inheritdoc />
        public void Draw(AgentCoreSettingsContext context)
        {
            EnsureApiKeyDisplays(context);

            // ADR-17 极简: 只保留 Compression 总开关 + PROJECT.md/SOUL.ext.md 文件行 + 可选服务
            // 已删除: Context Sources (Bootstrap Files/Auto Project Context) / Context Budget / Compression 内部参数 / Compression LLM
            DrawProjectFilesCard(context);
            EditorGUILayout.Space(8);

            DrawCompressionCard(context);
            EditorGUILayout.Space(8);

            DrawMemoryServiceCard(context);
            EditorGUILayout.Space(8);

            DrawKnowledgeBaseCard(context);
        }

        // ── PROJECT.md / SOUL.ext.md 文件卡片 (ADR-17 保留) ──

        private static void DrawProjectFilesCard(AgentCoreSettingsContext context)
        {
            context.Ui.DrawCard(
                "项目上下文文件",
                "PROJECT.md 描述项目约定, SOUL.ext.md 追加行为规则。Agent 会自动读取, 建议提交到版本控制。",
                () =>
                {
                    DrawUserFileRow("PROJECT.md", "项目约定与个人偏好, 团队共享, 建议 VCS 提交。Agent 可以直接编辑此文件。");
                    DrawUserFileRow("SOUL.ext.md", "Agent 行为规则扩展, 追加到内置 SOUL 后, 建议 VCS 提交。");
                });
        }

        // ─────────────────────────────────────────────────────────────────────
        // Cards
        // ─────────────────────────────────────────────────────────────────────

        // ADR-17 极简:
        //   - DrawContextSourcesCard 已删除, PROJECT.md/SOUL.ext.md 移到 DrawProjectFilesCard
        //   - DrawContextBudgetCard 已删除 (Max Context Tokens / Reserve Response Tokens 内部化)

        // ADR-17 极简: 只保留一个总开关, 内部参数 (Threshold/Target/Trigger Ratio) 内部化
        private static void DrawCompressionCard(AgentCoreSettingsContext context)
        {
            var settings = context.Settings;

            context.Ui.DrawCard(
                "上下文压缩",
                "长对话时自动压缩历史消息, 避免超出上下文窗口。默认启用。",
                () =>
                {
                    EditorGUI.BeginChangeCheck();

                    settings.compressionEnabled = EditorGUILayout.Toggle(
                        new GUIContent("启用压缩", "长对话超过阈值时自动压缩工具结果和历史消息。关闭则采用截断策略。"),
                        settings.compressionEnabled);

                    if (EditorGUI.EndChangeCheck())
                    {
                        settings.SaveSettings();
                    }
                });
        }

        // ADR-17 极简: DrawCompressionLlmCard 已删除 (useSeparateCompressionLLM 内部化, 极小众功能)

        private static void DrawMemoryServiceCard(AgentCoreSettingsContext context)
        {
            var settings = context.Settings;

            context.Ui.DrawServiceCard(
                title: "长期记忆 (mem0)",
                description: "跨会话的长期记忆服务。Agent 会自动从对话中提取关键信息, 并在后续会话中回忆使用。需要自己部署 mem0 服务。",
                enabled: settings.mem0Enabled,
                onEnabledChanged: value =>
                {
                    settings.mem0Enabled = value;
                    settings.SaveSettings();
                },
                statusHint: settings.mem0Enabled && !string.IsNullOrEmpty(settings.mem0Endpoint)
                    ? $"→ {settings.mem0Endpoint}"
                    : null,
                drawEnabledBody: () =>
                {
                    EditorGUI.BeginChangeCheck();

                    settings.mem0Endpoint = EditorGUILayout.TextField(
                        new GUIContent("Endpoint", "mem0 service URL"),
                        settings.mem0Endpoint);

                    context.Ui.DrawApiKeyRow(
                        "API Key",
                        "mem0 service API key",
                        context.State.StringValues[Mem0ApiKeyDisplayKey],
                        "Set mem0 API Key",
                        "Enter your mem0 API Key:",
                        newKey =>
                        {
                            SecureKeyStorage.SetMem0ApiKey(newKey);
                            context.State.StringValues[Mem0ApiKeyDisplayKey] = string.IsNullOrEmpty(newKey) ? "(not set)" : "••••••••••••";
                        },
                        () =>
                        {
                            SecureKeyStorage.SetMem0ApiKey(string.Empty);
                            context.State.StringValues[Mem0ApiKeyDisplayKey] = "(not set)";
                        });

                    GUI.enabled = false;
                    EditorGUILayout.TextField(
                        new GUIContent("User ID", "Auto-generated unique user identifier for memory isolation"),
                        settings.EffectiveUserId);
                    GUI.enabled = true;

                    if (EditorGUI.EndChangeCheck())
                    {
                        settings.SaveSettings();
                    }

                    // Auto Memory — advanced, collapsed by default.
                    EditorGUILayout.Space(4);
                    var autoExpanded = context.State.GetFoldout(AutoMemoryFoldoutKey, FoldoutDefaults.Advanced);
                    autoExpanded = EditorGUILayout.Foldout(autoExpanded, "Auto Memory (advanced)", true);
                    context.State.SetFoldout(AutoMemoryFoldoutKey, autoExpanded);

                    if (autoExpanded)
                    {
                        EditorGUI.indentLevel++;
                        EditorGUI.BeginChangeCheck();

                        settings.autoMemoryEnabled = EditorGUILayout.Toggle(
                            new GUIContent("Auto-Extract at Session End",
                                "Automatically extract key information to mem0 at session end"),
                            settings.autoMemoryEnabled);

                        settings.autoMemoryMinTurns = EditorGUILayout.IntSlider(
                            new GUIContent("Min Turns", "Minimum user turns before triggering auto-memory"),
                            settings.autoMemoryMinTurns, 1, 20);

                        if (EditorGUI.EndChangeCheck())
                        {
                            settings.SaveSettings();
                        }
                        EditorGUI.indentLevel--;
                    }
                });
        }

        private static void DrawKnowledgeBaseCard(AgentCoreSettingsContext context)
        {
            var settings = context.Settings;

            context.Ui.DrawServiceCard(
                title: "项目知识库 (LightRAG)",
                description: "项目文档的向量检索增强。索引项目内的文档, 需要时按语义相关性返回。需要自己部署 LightRAG 服务。",
                enabled: settings.lightragEnabled,
                onEnabledChanged: value =>
                {
                    settings.lightragEnabled = value;
                    settings.SaveSettings();
                },
                statusHint: settings.lightragEnabled && !string.IsNullOrEmpty(settings.lightragEndpoint)
                    ? $"→ {settings.lightragEndpoint}"
                    : null,
                drawEnabledBody: () =>
                {
                    EditorGUI.BeginChangeCheck();

                    settings.lightragEndpoint = EditorGUILayout.TextField(
                        new GUIContent("Endpoint", "LightRAG service URL"),
                        settings.lightragEndpoint);

                    context.Ui.DrawApiKeyRow(
                        "API Key",
                        "LightRAG service API key",
                        context.State.StringValues[LightRagApiKeyDisplayKey],
                        "Set LightRAG API Key",
                        "Enter your LightRAG API Key:",
                        newKey =>
                        {
                            SecureKeyStorage.SetLightRAGApiKey(newKey);
                            context.State.StringValues[LightRagApiKeyDisplayKey] = string.IsNullOrEmpty(newKey) ? "(not set)" : "••••••••••••";
                        },
                        () =>
                        {
                            SecureKeyStorage.SetLightRAGApiKey(string.Empty);
                            context.State.StringValues[LightRagApiKeyDisplayKey] = "(not set)";
                        });

                    if (EditorGUI.EndChangeCheck())
                    {
                        settings.SaveSettings();
                    }
                });
        }

        // ─────────────────────────────────────────────────────────────────────
        // User Files (PROJECT.md / SOUL.ext.md rows)
        // ─────────────────────────────────────────────────────────────────────

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
                File.WriteAllText(filePath, BootstrapLoader.GenerateUserFileTemplate(fileName), System.Text.Encoding.UTF8);
                AssetDatabase.Refresh();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AgentCore] Failed to create {fileName}: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────

        private static void EnsureApiKeyDisplays(AgentCoreSettingsContext context)
        {
            if (!context.State.StringValues.ContainsKey(Mem0ApiKeyDisplayKey))
            {
                context.State.StringValues[Mem0ApiKeyDisplayKey] = SecureKeyStorage.HasMem0ApiKey() ? "••••••••••••" : "(not set)";
            }

            if (!context.State.StringValues.ContainsKey(LightRagApiKeyDisplayKey))
            {
                context.State.StringValues[LightRagApiKeyDisplayKey] = SecureKeyStorage.HasLightRAGApiKey() ? "••••••••••••" : "(not set)";
            }
        }
    }
}
