using System;
using System.IO;
using AgentCore.Editor.Bootstrap;
using AgentCore.Editor.Cloud;
using AgentCore.Editor.L10n;
using AgentCore.Editor.Utils;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Config.Settings.Pages
{
    /// <summary>
    /// Context &amp; Memory settings page — context sources, budget, compression, memory, and knowledge base.
    /// </summary>
    /// <remarks>
    /// v1.4.2: Memory Service and Knowledge Base render as
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
        public string Title => Loc.Tr("settings.contextMemory.title", "Context & Memory");

        /// <inheritdoc />
        public string Description => Loc.Tr(
            "settings.contextMemory.description",
            "Configure context sources, budget, compression, memory service, and knowledge base.");

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
            // 已删除: Context Sources (Bootstrap Files/Auto Project Context) / Context Budget / Compression 内部参数
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
                Loc.Tr("settings.contextMemory.projectFiles.title", "Project Files"),
                Loc.Tr(
                    "settings.contextMemory.projectFiles.description",
                    "PROJECT.md describes project conventions; SOUL.ext.md adds behavior rules. Both are auto-loaded by the agent and stored locally in UserSettings/AgentCore/ (personal, not in version control). Legacy copies in the project root or AgentCore/ are read as a fallback only."),
                () =>
                {
                    DrawUserFileRow("PROJECT.md", Loc.Tr(
                        "settings.contextMemory.projectFiles.projectMdDesc",
                        "Project conventions and personal preferences — stored locally in UserSettings/AgentCore/. Agent can edit this file directly."));
                    DrawUserFileRow("SOUL.ext.md", Loc.Tr(
                        "settings.contextMemory.projectFiles.soulExtDesc",
                        "Agent behavior rule extensions — appended to built-in SOUL, stored locally in UserSettings/AgentCore/."));
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
                Loc.Tr("settings.contextMemory.compression.title", "Context Compression"),
                Loc.Tr(
                    "settings.contextMemory.compression.description",
                    "Auto-compress conversation history when context window fills up. Enabled by default."),
                () =>
                {
                    EditorGUI.BeginChangeCheck();

                    settings.compressionEnabled = EditorGUILayout.Toggle(
                        new GUIContent(
                            Loc.Tr("settings.contextMemory.compression.enable", "Enable Compression"),
                            Loc.Tr(
                                "settings.contextMemory.compression.enableTooltip",
                                "Compress tool results and history instead of truncating.")),
                        settings.compressionEnabled);

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
                title: Loc.Tr("settings.contextMemory.memory.title", "Long-Term Memory (mem0)"),
                description: Loc.Tr(
                    "settings.contextMemory.memory.description",
                    "Cross-session persistent memory. Agent extracts key info from conversations and recalls in later sessions. Requires self-hosted mem0."),
                enabled: settings.mem0Enabled,
                onEnabledChanged: value =>
                {
                    settings.mem0Enabled = value;
                    settings.SaveSettings();
                },
                statusHint: settings.mem0Enabled && !string.IsNullOrEmpty(settings.mem0Endpoint)
                    ? $"-> {settings.mem0Endpoint}"
                    : null,
                drawEnabledBody: () =>
                {
                    EditorGUI.BeginChangeCheck();

                    settings.mem0Endpoint = EditorGUILayout.TextField(
                        new GUIContent(
                            Loc.Tr("settings.contextMemory.memory.endpoint", "Endpoint"),
                            Loc.Tr("settings.contextMemory.memory.endpointTooltip", "mem0 service URL")),
                        settings.mem0Endpoint);

                    context.Ui.DrawApiKeyRow(
                        Loc.Tr("settings.contextMemory.memory.apiKey", "API Key"),
                        Loc.Tr("settings.contextMemory.memory.apiKeyTooltip", "mem0 service API key"),
                        context.State.StringValues[Mem0ApiKeyDisplayKey],
                        Loc.Tr("settings.contextMemory.memory.setApiKey", "Set mem0 API Key"),
                        Loc.Tr("settings.contextMemory.memory.enterApiKey", "Enter your mem0 API Key:"),
                        newKey =>
                        {
                            SecureKeyStorage.SetMem0ApiKey(newKey);
                            context.State.StringValues[Mem0ApiKeyDisplayKey] = string.IsNullOrEmpty(newKey)
                                ? Loc.Tr("settings.ui.notSet", "(not set)")
                                : "••••••••••••";
                        },
                        () =>
                        {
                            SecureKeyStorage.SetMem0ApiKey(string.Empty);
                            context.State.StringValues[Mem0ApiKeyDisplayKey] = Loc.Tr("settings.ui.notSet", "(not set)");
                        });

                    GUI.enabled = false;
                    EditorGUILayout.TextField(
                        new GUIContent(
                            Loc.Tr("settings.contextMemory.memory.userId", "User ID"),
                            Loc.Tr(
                                "settings.contextMemory.memory.userIdTooltip",
                                "Auto-generated unique user identifier for memory isolation")),
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
                    autoExpanded = EditorGUILayout.Foldout(autoExpanded, Loc.Tr("settings.contextMemory.memory.autoMemory", "Auto Memory (advanced)"), true);
                    context.State.SetFoldout(AutoMemoryFoldoutKey, autoExpanded);

                    if (autoExpanded)
                    {
                        EditorGUI.indentLevel++;
                        EditorGUI.BeginChangeCheck();

                        settings.autoMemoryEnabled = EditorGUILayout.Toggle(
                            new GUIContent(
                                Loc.Tr("settings.contextMemory.memory.autoExtract", "Auto-Extract at Session End"),
                                Loc.Tr(
                                    "settings.contextMemory.memory.autoExtractTooltip",
                                    "Automatically extract key information to mem0 at session end")),
                            settings.autoMemoryEnabled);

                        settings.autoMemoryMinTurns = EditorGUILayout.IntSlider(
                            new GUIContent(
                                Loc.Tr("settings.contextMemory.memory.minTurns", "Min Turns"),
                                Loc.Tr(
                                    "settings.contextMemory.memory.minTurnsTooltip",
                                    "Minimum user turns before triggering auto-memory")),
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
                title: Loc.Tr("settings.contextMemory.knowledge.title", "Knowledge Base (LightRAG)"),
                description: Loc.Tr(
                    "settings.contextMemory.knowledge.description",
                    "Project doc vector search. Indexes project docs and returns semantically relevant snippets. Requires self-hosted LightRAG."),
                enabled: settings.lightragEnabled,
                onEnabledChanged: value =>
                {
                    settings.lightragEnabled = value;
                    settings.SaveSettings();
                },
                statusHint: settings.lightragEnabled && !string.IsNullOrEmpty(settings.lightragEndpoint)
                    ? $"-> {settings.lightragEndpoint}"
                    : null,
                drawEnabledBody: () =>
                {
                    EditorGUI.BeginChangeCheck();

                    settings.lightragEndpoint = EditorGUILayout.TextField(
                        new GUIContent(
                            Loc.Tr("settings.contextMemory.knowledge.endpoint", "Endpoint"),
                            Loc.Tr("settings.contextMemory.knowledge.endpointTooltip", "LightRAG service URL")),
                        settings.lightragEndpoint);

                    context.Ui.DrawApiKeyRow(
                        Loc.Tr("settings.contextMemory.knowledge.apiKey", "API Key"),
                        Loc.Tr("settings.contextMemory.knowledge.apiKeyTooltip", "LightRAG service API key"),
                        context.State.StringValues[LightRagApiKeyDisplayKey],
                        Loc.Tr("settings.contextMemory.knowledge.setApiKey", "Set LightRAG API Key"),
                        Loc.Tr("settings.contextMemory.knowledge.enterApiKey", "Enter your LightRAG API Key:"),
                        newKey =>
                        {
                            SecureKeyStorage.SetLightRAGApiKey(newKey);
                            context.State.StringValues[LightRagApiKeyDisplayKey] = string.IsNullOrEmpty(newKey)
                                ? Loc.Tr("settings.ui.notSet", "(not set)")
                                : "••••••••••••";
                        },
                        () =>
                        {
                            SecureKeyStorage.SetLightRAGApiKey(string.Empty);
                            context.State.StringValues[LightRagApiKeyDisplayKey] = Loc.Tr("settings.ui.notSet", "(not set)");
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
            if (GUILayout.Button(IsRunning(context, TestMem0RunningKey)
                    ? Loc.Tr("settings.common.testing", "Testing...")
                    : Loc.Tr("settings.contextMemory.memory.test", "Test mem0"),
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
            if (GUILayout.Button(IsRunning(context, TestLightRAGRunningKey)
                    ? Loc.Tr("settings.common.testing", "Testing...")
                    : Loc.Tr("settings.contextMemory.knowledge.test", "Test LightRAG"),
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
            context.State.SetStatus(TestMem0StatusKey, Loc.Tr("settings.common.testing", "Testing..."), SettingsStatusLevel.Loading);
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
                            success
                                ? Loc.Tr("settings.contextMemory.test.ok", "[OK] {0}", message)
                                : Loc.Tr("settings.contextMemory.test.fail", "[FAIL] {0}", message),
                            success ? SettingsStatusLevel.Success : SettingsStatusLevel.Error);
                        SetRunning(context, TestMem0RunningKey, false);
                    });
                }
                catch (Exception ex)
                {
                    AsyncHelper.RunOnMainThread(() =>
                    {
                        context.State.SetStatus(TestMem0StatusKey,
                            Loc.Tr("settings.contextMemory.test.fail", "[FAIL] {0}", ex.Message), SettingsStatusLevel.Error);
                        SetRunning(context, TestMem0RunningKey, false);
                    });
                }
            });
        }

        private static void TestLightRAGConnection(AgentCoreSettingsContext context)
        {
            SetRunning(context, TestLightRAGRunningKey, true);
            context.State.SetStatus(TestLightRAGStatusKey, Loc.Tr("settings.common.testing", "Testing..."), SettingsStatusLevel.Loading);
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
                            success
                                ? Loc.Tr("settings.contextMemory.knowledge.connected", "[OK] Connected")
                                : Loc.Tr("settings.contextMemory.knowledge.unhealthy", "[FAIL] Unhealthy"),
                            success ? SettingsStatusLevel.Success : SettingsStatusLevel.Error);
                        SetRunning(context, TestLightRAGRunningKey, false);
                    });
                }
                catch (Exception ex)
                {
                    AsyncHelper.RunOnMainThread(() =>
                    {
                        context.State.SetStatus(TestLightRAGStatusKey,
                            Loc.Tr("settings.contextMemory.test.fail", "[FAIL] {0}", ex.Message), SettingsStatusLevel.Error);
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
                bool legacy = !string.Equals(
                    Path.GetFullPath(filePath),
                    Path.GetFullPath(BootstrapLoader.GetDefaultUserFilePath(fileName) ?? filePath),
                    StringComparison.OrdinalIgnoreCase);
                EditorGUILayout.LabelField(legacy
                    ? relativePath + "  " + Loc.Tr("settings.contextMemory.files.legacyFallback", "(legacy fallback)")
                    : relativePath, EditorStyles.miniLabel);

                if (GUILayout.Button(Loc.Tr("settings.contextMemory.files.edit", "Edit"), GUILayout.MinWidth(50)))
                {
                    System.Diagnostics.Process.Start(filePath);
                }

                if (GUILayout.Button(Loc.Tr("settings.contextMemory.files.show", "Show"), GUILayout.MinWidth(50)))
                {
                    EditorUtility.RevealInFinder(filePath);
                }
            }
            else
            {
                EditorGUILayout.LabelField(Loc.Tr("settings.contextMemory.files.notCreated", "(not created)"), EditorStyles.miniLabel);
                if (GUILayout.Button(Loc.Tr("settings.contextMemory.files.create", "Create"), GUILayout.MinWidth(60)))
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
                AgentCoreLog.Error("[AgentCore] Cannot determine project root directory.");
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
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"[AgentCore] Failed to create {fileName}: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────

        private static void EnsureApiKeyDisplays(AgentCoreSettingsContext context)
        {
            if (!context.State.StringValues.ContainsKey(Mem0ApiKeyDisplayKey))
            {
                context.State.StringValues[Mem0ApiKeyDisplayKey] = SecureKeyStorage.HasMem0ApiKey() ? "••••••••••••" : Loc.Tr("settings.ui.notSet", "(not set)");
            }

            if (!context.State.StringValues.ContainsKey(LightRagApiKeyDisplayKey))
            {
                context.State.StringValues[LightRagApiKeyDisplayKey] = SecureKeyStorage.HasLightRAGApiKey() ? "••••••••••••" : Loc.Tr("settings.ui.notSet", "(not set)");
            }
        }
    }
}
