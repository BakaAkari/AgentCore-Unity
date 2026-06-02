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
    public sealed class ContextMemorySettingsPage : IAgentCoreSettingsPage
    {
        private const string Mem0ApiKeyDisplayKey = "context-memory.mem0.apiKeyDisplay";
        private const string LightRagApiKeyDisplayKey = "context-memory.lightrag.apiKeyDisplay";
        private const string CompressionAdvancedFoldoutKey = "context-memory.compression-llm";

        /// <inheritdoc />
        public string Id => "context-memory";

        /// <inheritdoc />
        public string Title => "Context & Memory";

        /// <inheritdoc />
        public string Description => "Configure context sources, budget, compression, memory service, and knowledge base.";

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
            var settings = context.Settings;

            // ── Context Sources ──
            context.Ui.DrawCard("Context Sources", "Bootstrap pipeline and project-local context files.", () =>
            {
                EditorGUI.BeginChangeCheck();

                settings.bootstrapEnabled = EditorGUILayout.Toggle(
                    new GUIContent("Bootstrap Files", "Enable the Bootstrap Files system"),
                    settings.bootstrapEnabled);

                settings.autoProjectContext = EditorGUILayout.Toggle(
                    new GUIContent("Auto Project Context", "Automatically collect project context information"),
                    settings.autoProjectContext);

                if (EditorGUI.EndChangeCheck())
                {
                    settings.SaveSettings();
                }

                EditorGUILayout.Space(6);
                DrawUserFileRow("MEMORY.md", "Local knowledge file — project context the Agent can reference.");
                DrawUserFileRow("USER.md", "User preference file — define Agent behavior and rules.");
            });

            EditorGUILayout.Space(8);

            // ── Context Budget ──
            context.Ui.DrawCard("Context Budget", null, () =>
            {
                EditorGUI.BeginChangeCheck();

                settings.maxContextTokens = EditorGUILayout.IntField(
                    new GUIContent("Max Context Tokens", "Context window token limit (0 = auto-infer from model name)"),
                    settings.maxContextTokens);
                settings.maxContextTokens = Mathf.Max(settings.maxContextTokens, 0);

                settings.reserveResponseTokens = EditorGUILayout.IntField(
                    new GUIContent("Reserve Response Tokens", "Tokens reserved for the AI response"),
                    settings.reserveResponseTokens);
                settings.reserveResponseTokens = Mathf.Clamp(settings.reserveResponseTokens, 500, 16000);

                if (EditorGUI.EndChangeCheck())
                {
                    settings.SaveSettings();
                }
            });

            EditorGUILayout.Space(8);

            // ── Compression ──
            context.Ui.DrawCard("Compression", null, () =>
            {
                EditorGUI.BeginChangeCheck();

                settings.compressionEnabled = EditorGUILayout.Toggle(
                    new GUIContent("Enable Compression", "Compress tool results and conversation history instead of truncating"),
                    settings.compressionEnabled);

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Tool Result", EditorStyles.miniLabel);

                settings.toolResultCompressionThreshold = EditorGUILayout.IntField(
                    new GUIContent("Threshold", "Tool results exceeding this token count are compressed"),
                    settings.toolResultCompressionThreshold);

                settings.toolResultTargetTokens = EditorGUILayout.IntField(
                    new GUIContent("Target", "Target token count after compression"),
                    settings.toolResultTargetTokens);

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Conversation", EditorStyles.miniLabel);

                settings.conversationCompressionTrigger = EditorGUILayout.Slider(
                    new GUIContent("Trigger Ratio", "Trigger conversation compression when context usage exceeds this ratio (0.3–0.95)"),
                    settings.conversationCompressionTrigger, 0.3f, 0.95f);

                if (EditorGUI.EndChangeCheck())
                {
                    settings.SaveSettings();
                }
            });

            EditorGUILayout.Space(8);

            // ── Compression LLM (foldout) ──
            var compressionExpanded = context.State.GetFoldout(CompressionAdvancedFoldoutKey);
            compressionExpanded = EditorGUILayout.Foldout(compressionExpanded, "Separate Compression LLM", true);
            context.State.SetFoldout(CompressionAdvancedFoldoutKey, compressionExpanded);

            if (compressionExpanded)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUI.BeginChangeCheck();

                settings.useSeparateCompressionLLM = EditorGUILayout.Toggle(
                    new GUIContent("Use Separate LLM", "Use a dedicated LLM for compression tasks"),
                    settings.useSeparateCompressionLLM);

                if (settings.useSeparateCompressionLLM)
                {
                    settings.compressionLLMEndpoint = EditorGUILayout.TextField(
                        new GUIContent("Endpoint", "Compression LLM API endpoint"),
                        settings.compressionLLMEndpoint);

                    settings.compressionLLMModel = EditorGUILayout.TextField(
                        new GUIContent("Model", "Compression LLM model name"),
                        settings.compressionLLMModel);

                    context.Ui.DrawApiKeyRow(
                        "API Key",
                        "Compression LLM API key",
                        SecureKeyStorage.HasCompressionLLMApiKey() ? "••••••••" : "(not set)",
                        "Set Compression LLM API Key",
                        "Enter your compression LLM API key:",
                        SecureKeyStorage.SetCompressionLLMApiKey,
                        () => SecureKeyStorage.SetCompressionLLMApiKey(string.Empty));
                }

                if (EditorGUI.EndChangeCheck())
                {
                    settings.SaveSettings();
                }
                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.Space(8);

            // ── Memory Service ──
            context.Ui.DrawCard("Memory Service", "mem0 persistent memory service for cross-session knowledge.", () =>
            {
                EditorGUI.BeginChangeCheck();

                settings.mem0Enabled = EditorGUILayout.Toggle(
                    new GUIContent("Enabled", "Enable mem0 memory service"),
                    settings.mem0Enabled);

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

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Auto Memory", EditorStyles.miniLabel);

                settings.autoMemoryEnabled = EditorGUILayout.Toggle(
                    new GUIContent("Enabled", "Automatically extract key information to mem0 at session end"),
                    settings.autoMemoryEnabled);

                settings.autoMemoryMinTurns = EditorGUILayout.IntSlider(
                    new GUIContent("Min Turns", "Minimum user turns before triggering auto-memory"),
                    settings.autoMemoryMinTurns, 1, 20);

                if (EditorGUI.EndChangeCheck())
                {
                    settings.SaveSettings();
                }
            });

            EditorGUILayout.Space(8);

            // ── Knowledge Base ──
            context.Ui.DrawCard("Knowledge Base", "LightRAG knowledge base for project-specific retrieval.", () =>
            {
                EditorGUI.BeginChangeCheck();

                settings.lightragEnabled = EditorGUILayout.Toggle(
                    new GUIContent("Enabled", "Enable LightRAG knowledge base"),
                    settings.lightragEnabled);

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

        // ── User Files ──

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

        // ── Helpers ──

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
