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

            DrawContextSourcesCard(context);
            EditorGUILayout.Space(8);

            DrawContextBudgetCard(context);
            EditorGUILayout.Space(8);

            DrawCompressionCard(context);
            EditorGUILayout.Space(8);

            DrawCompressionLlmCard(context);
            EditorGUILayout.Space(8);

            DrawMemoryServiceCard(context);
            EditorGUILayout.Space(8);

            DrawKnowledgeBaseCard(context);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Cards
        // ─────────────────────────────────────────────────────────────────────

        private static void DrawContextSourcesCard(AgentCoreSettingsContext context)
        {
            var settings = context.Settings;

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
                DrawUserFileRow("PROJECT.md", "Project conventions and personal preferences — team-shared, recommended for VCS commit.");
                DrawUserFileRow("SOUL.ext.md", "Agent behavior rule extensions — appended to built-in SOUL, recommended for VCS commit.");
            });
        }

        private static void DrawContextBudgetCard(AgentCoreSettingsContext context)
        {
            var settings = context.Settings;

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
        }

        private static void DrawCompressionCard(AgentCoreSettingsContext context)
        {
            var settings = context.Settings;

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
        }

        private static void DrawCompressionLlmCard(AgentCoreSettingsContext context)
        {
            var settings = context.Settings;

            context.Ui.DrawServiceCard(
                title: "Separate Compression LLM",
                description: "Use a dedicated LLM for summarizing tool results and conversation history. Falls back to the main LLM when disabled.",
                enabled: settings.useSeparateCompressionLLM,
                onEnabledChanged: value =>
                {
                    settings.useSeparateCompressionLLM = value;
                    settings.SaveSettings();
                },
                statusHint: settings.useSeparateCompressionLLM && !string.IsNullOrEmpty(settings.compressionLLMEndpoint)
                    ? $"→ {settings.compressionLLMEndpoint}"
                    : null,
                drawEnabledBody: () =>
                {
                    EditorGUI.BeginChangeCheck();

                    settings.compressionLLMEndpoint = EditorGUILayout.TextField(
                        new GUIContent("Endpoint", "Compression LLM API endpoint"),
                        settings.compressionLLMEndpoint);

                    settings.compressionLLMModel = EditorGUILayout.TextField(
                        new GUIContent("Model", "Compression LLM model name"),
                        settings.compressionLLMModel);

                    context.Ui.DrawApiKeyRow(
                        "API Key",
                        "Compression LLM API key",
                        SecureKeyStorage.HasCompressionLLMApiKey() ? "••••••••••••" : "(not set)",
                        "Set Compression LLM API Key",
                        "Enter your compression LLM API key:",
                        SecureKeyStorage.SetCompressionLLMApiKey,
                        () => SecureKeyStorage.SetCompressionLLMApiKey(string.Empty));

                    if (EditorGUI.EndChangeCheck())
                    {
                        settings.SaveSettings();
                    }
                });
        }

        private static void DrawMemoryServiceCard(AgentCoreSettingsContext context)
        {
            var settings = context.Settings;

            context.Ui.DrawServiceCard(
                title: "Memory Service (mem0)",
                description: "Persistent cross-session memory service. Extracts and recalls key information from conversations.",
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
                title: "Knowledge Base (LightRAG)",
                description: "Project-specific retrieval augmented generation. Indexes docs and returns relevant snippets on demand.",
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
