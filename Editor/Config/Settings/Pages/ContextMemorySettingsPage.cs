using System;
using System.IO;
using AgentCore.Editor.Bootstrap;
using AgentCore.Editor.Cloud;
using AgentCore.Editor.Utils;
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

        // Connection-test state keys (migrated from UiDiagnosticsSettingsPage — see ADR below).
        private const string TestMem0RunningKey = "context-memory.testMem0Running";
        private const string TestMem0StatusKey = "context-memory.testMem0Status";
        private const string TestLightRAGRunningKey = "context-memory.testLightRAGRunning";
        private const string TestLightRAGStatusKey = "context-memory.testLightRAGStatus";

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

        // ── Project Files card (ADR-17 kept) ──

        private static void DrawProjectFilesCard(AgentCoreSettingsContext context)
        {
            context.Ui.DrawCard(
                "Project Files",
                "PROJECT.md describes project conventions; SOUL.ext.md adds behavior rules. Both are auto-loaded by the agent; recommended to commit to VCS.",
                () =>
                {
                    DrawUserFileRow("PROJECT.md", "Project conventions and personal preferences — team-shared, recommended for VCS commit. Agent can edit this file directly.");
                    DrawUserFileRow("SOUL.ext.md", "Agent behavior rule extensions — appended to built-in SOUL, recommended for VCS commit.");
                });
        }

        // ─────────────────────────────────────────────────────────────────────
        // Cards
        // ─────────────────────────────────────────────────────────────────────

        // ADR-17 极简:
        //   - DrawContextSourcesCard 已删除, PROJECT.md/SOUL.ext.md 移到 DrawProjectFilesCard
        //   - DrawContextBudgetCard 已删除 (Max Context Tokens / Reserve Response Tokens 内部化)

        // ADR-17: single toggle only; internal params (Threshold/Target/Trigger Ratio) hidden
        private static void DrawCompressionCard(AgentCoreSettingsContext context)
        {
            var settings = context.Settings;

            context.Ui.DrawCard(
                "Context Compression",
                "Auto-compress conversation history when context window fills up. Enabled by default.",
                () =>
                {
                    EditorGUI.BeginChangeCheck();

                    settings.compressionEnabled = EditorGUILayout.Toggle(
                        new GUIContent("Enable Compression", "Compress tool results and history instead of truncating."),
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
                title: "Long-Term Memory (mem0)",
                description: "Cross-session persistent memory. Agent extracts key info from conversations and recalls in later sessions. Requires self-hosted mem0.",
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

                    // Connection test — migrated from UiDiagnostics (settings page consolidation).
                    DrawTestMem0Button(context);

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
                description: "Project doc vector search. Indexes project docs and returns semantically relevant snippets. Requires self-hosted LightRAG.",
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

                    // Connection test — migrated from UiDiagnostics (settings page consolidation).
                    DrawTestLightRAGButton(context);
                });
        }

        // ─────────────────────────────────────────────────────────────────────
        // Connection Tests (migrated from UiDiagnosticsSettingsPage — page consolidation)
        // ─────────────────────────────────────────────────────────────────────

        private static void DrawTestMem0Button(AgentCoreSettingsContext context)
        {
            EditorGUILayout.BeginHorizontal();
            GUI.enabled = !IsRunning(context, TestMem0RunningKey);
            if (GUILayout.Button(IsRunning(context, TestMem0RunningKey) ? "Testing..." : "Test mem0",
                GUILayout.MinWidth(120), GUILayout.MaxWidth(160)))
            {
                TestMem0Connection(context);
            }
            GUI.enabled = true;

            context.Ui.DrawStatusLabel(
                context.State.GetStatusMessage(TestMem0StatusKey),
                context.State.GetStatusLevel(TestMem0StatusKey));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private static void DrawTestLightRAGButton(AgentCoreSettingsContext context)
        {
            EditorGUILayout.BeginHorizontal();
            GUI.enabled = !IsRunning(context, TestLightRAGRunningKey);
            if (GUILayout.Button(IsRunning(context, TestLightRAGRunningKey) ? "Testing..." : "Test LightRAG",
                GUILayout.MinWidth(140), GUILayout.MaxWidth(180)))
            {
                TestLightRAGConnection(context);
            }
            GUI.enabled = true;

            context.Ui.DrawStatusLabel(
                context.State.GetStatusMessage(TestLightRAGStatusKey),
                context.State.GetStatusLevel(TestLightRAGStatusKey));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private static void TestMem0Connection(AgentCoreSettingsContext context)
        {
            SetRunning(context, TestMem0RunningKey, true);
            context.State.SetStatus(TestMem0StatusKey, "Testing...", SettingsStatusLevel.Loading);
            var endpoint = context.Settings.mem0Endpoint;
            var apiKey = SecureKeyStorage.GetMem0ApiKey();
            var userId = context.Settings.EffectiveUserId;

            AsyncHelper.RunAsync(async () =>
            {
                try
                {
                    var client = new Mem0Client(endpoint, apiKey, userId);
                    var (success, message) = await client.TestConnectionAsync();
                    AsyncHelper.RunOnMainThread(() =>
                    {
                        context.State.SetStatus(
                            TestMem0StatusKey,
                            success ? $"[OK] {message}" : $"[FAIL] {message}",
                            success ? SettingsStatusLevel.Success : SettingsStatusLevel.Error);
                        SetRunning(context, TestMem0RunningKey, false);
                    });
                }
                catch (Exception ex)
                {
                    AsyncHelper.RunOnMainThread(() =>
                    {
                        context.State.SetStatus(TestMem0StatusKey, $"[FAIL] {ex.Message}", SettingsStatusLevel.Error);
                        SetRunning(context, TestMem0RunningKey, false);
                    });
                }
            });
        }

        private static void TestLightRAGConnection(AgentCoreSettingsContext context)
        {
            SetRunning(context, TestLightRAGRunningKey, true);
            context.State.SetStatus(TestLightRAGStatusKey, "Testing...", SettingsStatusLevel.Loading);
            var endpoint = context.Settings.lightragEndpoint;
            var apiKey = SecureKeyStorage.GetLightRAGApiKey();

            AsyncHelper.RunAsync(async () =>
            {
                try
                {
                    var client = new LightRAGClient(endpoint, apiKey);
                    var success = await client.TestConnectionAsync();
                    AsyncHelper.RunOnMainThread(() =>
                    {
                        context.State.SetStatus(
                            TestLightRAGStatusKey,
                            success ? "[OK] Connected" : "[FAIL] Unhealthy",
                            success ? SettingsStatusLevel.Success : SettingsStatusLevel.Error);
                        SetRunning(context, TestLightRAGRunningKey, false);
                    });
                }
                catch (Exception ex)
                {
                    AsyncHelper.RunOnMainThread(() =>
                    {
                        context.State.SetStatus(TestLightRAGStatusKey, $"[FAIL] {ex.Message}", SettingsStatusLevel.Error);
                        SetRunning(context, TestLightRAGRunningKey, false);
                    });
                }
            });
        }

        private static bool IsRunning(AgentCoreSettingsContext context, string key)
        {
            return context.State.RunningTasks.Contains(key);
        }

        private static void SetRunning(AgentCoreSettingsContext context, string key, bool running)
        {
            if (running)
            {
                context.State.RunningTasks.Add(key);
            }
            else
            {
                context.State.RunningTasks.Remove(key);
            }
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
