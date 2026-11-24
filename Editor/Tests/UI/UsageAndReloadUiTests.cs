using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AgentCore.Editor.Config;
using AgentCore.Editor.Config.Settings;
using AgentCore.Editor.Config.Settings.Pages;
using AgentCore.Editor.Core;
using AgentCore.Editor.L10n;
using AgentCore.Editor.LLM;
using AgentCore.Editor.UI;
using AgentCore.Editor.UI.Components;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;

namespace AgentCore.Tests.Editor.UI
{
    /// <summary>
    /// v1.23.0 EditMode tests for persistent usage UI (<see cref="ContextUsagePanel"/>,
    /// <see cref="ModelAgentSettingsPage"/>), external MCP activity stats (<see cref="ExternalActivityWindow"/>),
    /// Domain Reload state persistence (<see cref="ChatWindow"/>, <see cref="AgentCoreSettingsState"/>,
    /// <see cref="AgentCoreSettingsProvider"/>), and L10n parity.
    /// All tests run with strictly isolated temp paths, EditorPrefs keys, and SessionState keys.
    /// </summary>
    [TestFixture]
    public class UsageAndReloadUiTests
    {
        private const string TestContextWindowPrefKey = "AgentCore.Tests.UsageAndReload.ContextUsage.Window";
        private const string TestContextCollapsedPrefKey = "AgentCore.Tests.UsageAndReload.ContextUsage.Collapsed";
        private const string TestExtActivityWindowPrefKey = "AgentCore.Tests.UsageAndReload.ExternalActivity.Window";
        private const string TestChatActiveModulePrefKey = "AgentCore.Tests.UsageAndReload.ChatWindow.ActiveModule";
        private const string TestSettingsSectionPrefKey = "AgentCore.Tests.UsageAndReload.Settings.Section";
        private const string TestSettingsFoldoutsSessionKey = "AgentCore.Tests.UsageAndReload.Settings.Foldouts";
        private const string TestSettingsStatusesSessionKey = "AgentCore.Tests.UsageAndReload.Settings.Statuses";

        private string _tempDir;
        private string _usageHistoryPath;
        private string _auditLogPath;
        private DateTime _now;

        [SetUp]
        public void SetUp()
        {
            CleanupTestState();

            _tempDir = Path.Combine(Path.GetTempPath(), "AgentCore_UsageAndReloadUiTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _usageHistoryPath = Path.Combine(_tempDir, "UsageHistory.json");
            _auditLogPath = Path.Combine(_tempDir, "audit.jsonl");
            _now = new DateTime(2026, 11, 24, 16, 0, 0, DateTimeKind.Local);

            UsageLedger.ConfigureForTesting(() => _usageHistoryPath, () => _now);
            ExternalToolActivity.ConfigureForTesting(() => new[] { _auditLogPath }, () => _now);
        }

        [TearDown]
        public void TearDown()
        {
            UsageLedger.ResetTestingOverrides();
            ExternalToolActivity.ResetTestingOverrides();
            CleanupTestState();

            if (!string.IsNullOrEmpty(_tempDir) && Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, true); } catch { /* ignore temp cleanup failure */ }
            }
        }

        private static void CleanupTestState()
        {
            EditorPrefs.DeleteKey(TestContextWindowPrefKey);
            EditorPrefs.DeleteKey(TestContextCollapsedPrefKey);
            EditorPrefs.DeleteKey(TestExtActivityWindowPrefKey);
            EditorPrefs.DeleteKey(TestChatActiveModulePrefKey);
            EditorPrefs.DeleteKey(TestSettingsSectionPrefKey);
            SessionState.EraseString(TestSettingsFoldoutsSessionKey);
            SessionState.EraseString(TestSettingsStatusesSessionKey);
            AgentCoreSettingsProvider.SetPendingSectionForTests(null);
        }

        private static LlmUsage MakeUsage(
            string model,
            int inputUncached,
            int cacheRead = 0,
            int cacheWrite = 0,
            int output = 0,
            int reasoning = 0,
            bool cacheKnown = true)
        {
            return new LlmUsage
            {
                Model = model,
                InputUncached = inputUncached,
                CacheRead = cacheRead,
                CacheWrite = cacheWrite,
                Output = output,
                Reasoning = reasoning,
                Calls = 1,
                CacheMetricsKnown = cacheKnown,
            };
        }

        #region ContextUsagePanel

        [Test]
        public void ContextUsagePanel_PersistenceHelpers_RoundTripAndInvalidFallback()
        {
            Assert.AreEqual(UsageWindow.Session, ContextUsagePanel.LoadSelectedWindow(TestContextWindowPrefKey));
            Assert.IsTrue(ContextUsagePanel.LoadCollapsedState(TestContextCollapsedPrefKey));

            var windows = new[]
            {
                UsageWindow.Session,
                UsageWindow.Today,
                UsageWindow.Days7,
                UsageWindow.Days30,
                UsageWindow.Days90,
            };

            foreach (var window in windows)
            {
                ContextUsagePanel.SaveSelectedWindow(window, TestContextWindowPrefKey);
                Assert.AreEqual(window, ContextUsagePanel.LoadSelectedWindow(TestContextWindowPrefKey));
                Assert.IsFalse(string.IsNullOrWhiteSpace(ContextUsagePanel.GetWindowShortLabel(window)));
            }

            // Invalid persisted integer falls back safely to Session
            EditorPrefs.SetInt(TestContextWindowPrefKey, 999);
            Assert.AreEqual(UsageWindow.Session, ContextUsagePanel.LoadSelectedWindow(TestContextWindowPrefKey));

            ContextUsagePanel.SaveCollapsedState(false, TestContextCollapsedPrefKey);
            Assert.IsFalse(ContextUsagePanel.LoadCollapsedState(TestContextCollapsedPrefKey));

            ContextUsagePanel.SaveCollapsedState(true, TestContextCollapsedPrefKey);
            Assert.IsTrue(ContextUsagePanel.LoadCollapsedState(TestContextCollapsedPrefKey));
        }

        [Test]
        public void ContextUsagePanel_PersistsWindowAndCollapsedState_AndRendersAllWindows()
        {
            var baseToday = _now;
            _now = baseToday.AddDays(-40);
            UsageLedger.Record(MakeUsage("claude-opus-4-6", inputUncached: 2000, cacheRead: 1000, output: 500), "test");

            _now = baseToday.AddDays(-10);
            UsageLedger.Record(MakeUsage("claude-haiku-4-5", inputUncached: 1500, cacheRead: 2500, output: 400), "test");

            _now = baseToday;
            UsageLedger.Record(MakeUsage("claude-sonnet-4-6", inputUncached: 1000, cacheRead: 3000, output: 600, reasoning: 200), "test");

            var panel = new ContextUsagePanel(TestContextWindowPrefKey, TestContextCollapsedPrefKey);
            Assert.IsTrue(panel.IsCollapsed);
            Assert.AreEqual(UsageWindow.Session, panel.SelectedWindow);

            panel.SetCollapsed(false);
            panel.SetSelectedWindow(UsageWindow.Days30);

            // Recreate panel to simulate Domain Reload / ChatWindow reopen
            var restoredPanel = new ContextUsagePanel(TestContextWindowPrefKey, TestContextCollapsedPrefKey);
            Assert.IsFalse(restoredPanel.IsCollapsed);
            Assert.AreEqual(UsageWindow.Days30, restoredPanel.SelectedWindow);
            StringAssert.Contains("claude-sonnet-4-6", restoredPanel.SecondaryValueText);
            StringAssert.Contains("claude-haiku-4-5", restoredPanel.SecondaryValueText);
            Assert.IsFalse(string.IsNullOrWhiteSpace(restoredPanel.HeaderUsageText));
            Assert.IsFalse(string.IsNullOrWhiteSpace(restoredPanel.LlmUsageValueText));

            // 90d window includes the 40-day-old opus call
            restoredPanel.SetSelectedWindow(UsageWindow.Days90);
            StringAssert.Contains("claude-opus-4-6", restoredPanel.SecondaryValueText);

            // ResetSession clears Session only while preserving Today..Days90
            UsageLedger.ResetSession();
            restoredPanel.SetSelectedWindow(UsageWindow.Session);
            Assert.AreEqual("-", restoredPanel.SecondaryValueText);
            Assert.AreEqual(string.Empty, restoredPanel.HeaderUsageText);

            restoredPanel.SetSelectedWindow(UsageWindow.Today);
            StringAssert.Contains("claude-sonnet-4-6", restoredPanel.SecondaryValueText);
            Assert.IsFalse(string.IsNullOrWhiteSpace(restoredPanel.HeaderUsageText));
        }

        #endregion

        #region ExternalActivityWindow & ModelAgentSettingsPage

        [Test]
        public void ExternalActivityWindow_SelectedWindowPersistence_AndAuditStatsRoundTrip()
        {
            Assert.AreEqual(ActivityWindow.Today, ExternalActivityWindow.LoadSelectedWindow(TestExtActivityWindowPrefKey));

            var windows = new[]
            {
                ActivityWindow.Today,
                ActivityWindow.Days7,
                ActivityWindow.Days30,
                ActivityWindow.Days90,
            };

            foreach (var window in windows)
            {
                ExternalActivityWindow.SaveSelectedWindow(window, TestExtActivityWindowPrefKey);
                Assert.AreEqual(window, ExternalActivityWindow.LoadSelectedWindow(TestExtActivityWindowPrefKey));
                Assert.IsFalse(string.IsNullOrWhiteSpace(ExternalActivityWindow.GetWindowLabel(window)));
            }

            EditorPrefs.SetInt(TestExtActivityWindowPrefKey, -42);
            Assert.AreEqual(ActivityWindow.Today, ExternalActivityWindow.LoadSelectedWindow(TestExtActivityWindowPrefKey));

            // Write synthetic audit lines and verify Clear + ReloadFromAuditLog
            var lines = new[]
            {
                new JObject
                {
                    ["time"] = _now.AddMinutes(-15).ToString("o"),
                    ["client"] = "claude-code",
                    ["tool"] = "manage_scene",
                    ["action"] = "get_hierarchy",
                    ["arguments"] = "{}",
                    ["status"] = "ok",
                    ["elapsedMs"] = 120.0,
                }.ToString(Formatting.None),
                new JObject
                {
                    ["time"] = _now.AddDays(-5).ToString("o"),
                    ["client"] = "codex",
                    ["tool"] = "read_console",
                    ["action"] = "get_count",
                    ["arguments"] = "{}",
                    ["status"] = "error",
                    ["elapsedMs"] = 80.0,
                    ["error"] = "sample",
                }.ToString(Formatting.None),
            };
            File.WriteAllLines(_auditLogPath, lines, new UTF8Encoding(false));

            ExternalToolActivity.ReloadFromAuditLog();
            Assert.AreEqual(1, ExternalToolActivity.GetWindowStats(ActivityWindow.Today).TotalCalls);
            Assert.AreEqual(2, ExternalToolActivity.GetWindowStats(ActivityWindow.Days7).TotalCalls);

            ExternalToolActivity.Clear();
            Assert.AreEqual(0, ExternalToolActivity.Snapshot().Count);

            ExternalToolActivity.ReloadFromAuditLog();
            Assert.AreEqual(2, ExternalToolActivity.Snapshot().Count);
            Assert.AreEqual(2, ExternalToolActivity.GetWindowStats(ActivityWindow.Days7).TotalCalls);
        }

        [Test]
        public void ModelAgentSettingsPage_FormatMultiWindowUsageSummary_PreservesHistoryAfterSessionReset()
        {
            var baseToday = _now;
            _now = baseToday.AddDays(-12);
            UsageLedger.Record(MakeUsage("claude-sonnet-4-6", inputUncached: 1000, output: 500), "test");

            _now = baseToday;
            UsageLedger.Record(MakeUsage("claude-sonnet-4-6", inputUncached: 2000, cacheRead: 2000, output: 500), "test");

            // Reset current session; multi-window history summary must remain populated
            UsageLedger.ResetSession();
            Assert.AreEqual(0, UsageLedger.Session.Calls);

            var summary = ModelAgentSettingsPage.FormatMultiWindowUsageSummary();
            Assert.IsFalse(string.IsNullOrWhiteSpace(summary));
            StringAssert.Contains("4.5K", summary); // Today (1d): 4500 tokens
            StringAssert.Contains("6.0K", summary); // 30d/90d: 6000 tokens
        }

        #endregion

        #region ChatWindow Hub Module & Session Binding

        [Test]
        public void ChatWindow_HubModulePersistence_RestoresValidSavedModuleAndFallsBackToChat()
        {
            var available = new[] { ChatWindow.ChatModuleId, "vcs", "memory", "indexing" };

            // Default when no pref exists
            Assert.AreEqual(ChatWindow.ChatModuleId, ChatWindow.LoadSavedActiveModuleId(TestChatActiveModulePrefKey));
            Assert.AreEqual(ChatWindow.ChatModuleId, ChatWindow.ResolveInitialModuleId(available, TestChatActiveModulePrefKey));

            // Save and restore valid module
            ChatWindow.SaveActiveModuleId("vcs", TestChatActiveModulePrefKey);
            Assert.AreEqual("vcs", ChatWindow.LoadSavedActiveModuleId(TestChatActiveModulePrefKey));
            Assert.AreEqual("vcs", ChatWindow.ResolveInitialModuleId(available, TestChatActiveModulePrefKey));

            // Saved module no longer present (e.g. optional component disabled) -> fallback to Chat
            var reduced = new[] { ChatWindow.ChatModuleId, "memory" };
            Assert.AreEqual(ChatWindow.ChatModuleId, ChatWindow.ResolveInitialModuleId(reduced, TestChatActiveModulePrefKey));

            // Empty/whitespace value -> fallback to Chat
            EditorPrefs.SetString(TestChatActiveModulePrefKey, "   ");
            Assert.AreEqual(ChatWindow.ChatModuleId, ChatWindow.LoadSavedActiveModuleId(TestChatActiveModulePrefKey));
            Assert.AreEqual(ChatWindow.ChatModuleId, ChatWindow.ResolveInitialModuleId(available, TestChatActiveModulePrefKey));
        }

        [Test]
        public void UsageLedger_BindSessionContract_PreservesOnReloadAndResetsOnSessionSwitch()
        {
            UsageLedger.BindSession("session-reload-01", resetIfChanged: false);
            UsageLedger.Record(MakeUsage("claude-sonnet-4-6", inputUncached: 1200, output: 300), "test");

            // Simulate Domain Reload restoring the same active session
            UsageLedger.ReloadFromDisk();
            UsageLedger.BindSession("session-reload-01", resetIfChanged: false);
            Assert.AreEqual("session-reload-01", UsageLedger.CurrentSessionId);
            Assert.AreEqual(1, UsageLedger.Session.Calls);
            Assert.AreEqual(1500L, UsageLedger.GetWindowSummary(UsageWindow.Session).TotalTokens);

            // Simulate user switching to another session -> resets session counters, keeps daily history
            UsageLedger.BindSession("session-reload-02", resetIfChanged: true);
            Assert.AreEqual("session-reload-02", UsageLedger.CurrentSessionId);
            Assert.AreEqual(0, UsageLedger.Session.Calls);
            Assert.AreEqual(1L, UsageLedger.GetWindowSummary(UsageWindow.Today).CallCount);
        }

        #endregion

        #region AgentCoreSettingsState & AgentCoreSettingsProvider Persistence

        [Test]
        public void AgentCoreSettingsState_Persistence_RoundTripsAndSkipsLoadingAndCorruptJson()
        {
            var state = AgentCoreSettingsState.CreatePersistent(
                TestSettingsSectionPrefKey,
                TestSettingsFoldoutsSessionKey,
                TestSettingsStatusesSessionKey);

            state.SelectedSectionId = AgentCoreSettingsProvider.ToolsExtensionsSectionId;
            state.SetFoldout("tools.category.scene", true);
            state.SetFoldout("tools.category.file", false);
            state.SetStatus("conn.ok", "Connected", SettingsStatusLevel.Success);
            state.SetStatus("conn.warn", "Rate limited", SettingsStatusLevel.Warning);
            state.SetStatus("conn.loading", "Testing...", SettingsStatusLevel.Loading);

            // Recreate persistent state to simulate Domain Reload
            var restored = AgentCoreSettingsState.CreatePersistent(
                TestSettingsSectionPrefKey,
                TestSettingsFoldoutsSessionKey,
                TestSettingsStatusesSessionKey);

            Assert.AreEqual(AgentCoreSettingsProvider.ToolsExtensionsSectionId, restored.SelectedSectionId);
            Assert.IsTrue(restored.GetFoldout("tools.category.scene", defaultValue: false));
            Assert.IsFalse(restored.GetFoldout("tools.category.file", defaultValue: true));
            Assert.AreEqual("Connected", restored.GetStatusMessage("conn.ok"));
            Assert.AreEqual(SettingsStatusLevel.Success, restored.GetStatusLevel("conn.ok"));
            Assert.AreEqual("Rate limited", restored.GetStatusMessage("conn.warn"));
            Assert.AreEqual(SettingsStatusLevel.Warning, restored.GetStatusLevel("conn.warn"));
            Assert.AreEqual(SettingsStatusLevel.None, restored.GetStatusLevel("conn.loading"));
            Assert.AreEqual(string.Empty, restored.GetStatusMessage("conn.loading"));

            // Corrupt SessionState JSON must not throw and should fall back cleanly
            SessionState.SetString(TestSettingsFoldoutsSessionKey, "{{not-json");
            SessionState.SetString(TestSettingsStatusesSessionKey, "{{not-json");
            AgentCoreSettingsState corruptRestored = null;
            Assert.DoesNotThrow(() =>
            {
                corruptRestored = AgentCoreSettingsState.CreatePersistent(
                    TestSettingsSectionPrefKey,
                    TestSettingsFoldoutsSessionKey,
                    TestSettingsStatusesSessionKey);
            });
            Assert.IsNotNull(corruptRestored);
            Assert.AreEqual(AgentCoreSettingsProvider.ToolsExtensionsSectionId, corruptRestored.SelectedSectionId);
            Assert.AreEqual(0, corruptRestored.Foldouts.Count);
            Assert.AreEqual(0, corruptRestored.StatusMessages.Count);
        }

        [Test]
        public void AgentCoreSettingsProvider_PersistentMode_RestoresLastSection_RespectsDeepLink_AndFallsBack()
        {
            var dashboard = new FakeSettingsPage("dashboard", 100);
            var modelAgent = new FakeSettingsPage("model-agent", 200);
            var workspace = new FakeSettingsPage("workspace", 600);
            var pages = new IAgentCoreSettingsPage[] { dashboard, modelAgent, workspace };

            var firstProvider = new AgentCoreSettingsProvider(
                AgentCoreSettingsProvider.SettingsPath,
                SettingsScope.User,
                pages,
                persistState: true,
                selectedSectionPrefKey: TestSettingsSectionPrefKey,
                foldoutsSessionKey: TestSettingsFoldoutsSessionKey,
                statusesSessionKey: TestSettingsStatusesSessionKey);

            firstProvider.OnActivate(string.Empty, null);
            Assert.AreEqual("dashboard", firstProvider.SelectedSectionId);
            Assert.AreEqual(1, dashboard.ActivateCount);

            // Simulate user selecting workspace tab
            firstProvider.State.SelectedSectionId = "workspace";

            // Simulate Domain Reload / reopening Preferences -> restores "workspace"
            var reloadedProvider = new AgentCoreSettingsProvider(
                AgentCoreSettingsProvider.SettingsPath,
                SettingsScope.User,
                pages,
                persistState: true,
                selectedSectionPrefKey: TestSettingsSectionPrefKey,
                foldoutsSessionKey: TestSettingsFoldoutsSessionKey,
                statusesSessionKey: TestSettingsStatusesSessionKey);

            reloadedProvider.OnActivate(string.Empty, null);
            Assert.AreEqual("workspace", reloadedProvider.SelectedSectionId);
            Assert.AreEqual(1, workspace.ActivateCount);

            // Pending deep-link takes priority over saved section
            AgentCoreSettingsProvider.SetPendingSectionForTests("model-agent");
            var deepLinkProvider = new AgentCoreSettingsProvider(
                AgentCoreSettingsProvider.SettingsPath,
                SettingsScope.User,
                pages,
                persistState: true,
                selectedSectionPrefKey: TestSettingsSectionPrefKey,
                foldoutsSessionKey: TestSettingsFoldoutsSessionKey,
                statusesSessionKey: TestSettingsStatusesSessionKey);

            deepLinkProvider.OnActivate(string.Empty, null);
            Assert.AreEqual("model-agent", deepLinkProvider.SelectedSectionId);
            Assert.AreEqual(1, modelAgent.ActivateCount);

            // If persisted section no longer exists in page list, falls back to first page
            var reducedPages = new IAgentCoreSettingsPage[] { dashboard, workspace };
            var fallbackProvider = new AgentCoreSettingsProvider(
                AgentCoreSettingsProvider.SettingsPath,
                SettingsScope.User,
                reducedPages,
                persistState: true,
                selectedSectionPrefKey: TestSettingsSectionPrefKey,
                foldoutsSessionKey: TestSettingsFoldoutsSessionKey,
                statusesSessionKey: TestSettingsStatusesSessionKey);

            fallbackProvider.OnActivate(string.Empty, null);
            Assert.AreEqual("dashboard", fallbackProvider.SelectedSectionId);
        }

        private sealed class FakeSettingsPage : IAgentCoreSettingsPage
        {
            public FakeSettingsPage(string id, int order)
            {
                Id = id;
                Title = id;
                Description = id;
                Order = order;
            }

            public string Id { get; }
            public string Title { get; }
            public string Description { get; }
            public int Order { get; }
            public int ActivateCount { get; private set; }
            public int DeactivateCount { get; private set; }

            public void OnActivate(AgentCoreSettingsContext context) => ActivateCount++;
            public void OnDeactivate(AgentCoreSettingsContext context) => DeactivateCount++;
            public void Draw(AgentCoreSettingsContext context) { }
        }

        #endregion

        #region L10n Parity

        [Test]
        public void L10nResources_EnUsAndZhCn_HaveIdenticalKeysAndV123UsageKeysPresent()
        {
            var en = LoadL10nJson("en-US");
            var zh = LoadL10nJson("zh-CN");

            var enKeys = new HashSet<string>(en.Properties().Select(p => p.Name), StringComparer.Ordinal);
            var zhKeys = new HashSet<string>(zh.Properties().Select(p => p.Name), StringComparer.Ordinal);

            var onlyInEn = enKeys.Except(zhKeys).OrderBy(k => k, StringComparer.Ordinal).ToArray();
            var onlyInZh = zhKeys.Except(enKeys).OrderBy(k => k, StringComparer.Ordinal).ToArray();

            Assert.IsEmpty(onlyInEn, "Keys in en-US.json missing from zh-CN.json: " + string.Join(", ", onlyInEn));
            Assert.IsEmpty(onlyInZh, "Keys in zh-CN.json missing from en-US.json: " + string.Join(", ", onlyInZh));

            var requiredKeys = new[]
            {
                "contextUsage.period.label",
                "contextUsage.period.session",
                "contextUsage.period.1d",
                "contextUsage.period.7d",
                "contextUsage.period.30d",
                "contextUsage.period.90d",
                "contextUsage.periodUsage.label",
                "contextUsage.periodUsage.singleDayValue",
                "contextUsage.periodUsage.multiDayValue",
                "contextUsage.period.header",
                "contextUsage.period.headerEmpty",
                "contextUsage.topModels.label",
                "contextUsage.reasoning.inline",
                "contextUsage.cost.inline",
                "contextUsage.cost.notAvailable",
                "extActivity.windowTitle",
                "extActivity.runningSummary",
                "extActivity.recentCalls",
                "extActivity.reload",
                "extActivity.openAuditLog",
                "extActivity.noAuditLog",
                "extActivity.clear",
                "extActivity.emptyHint",
                "extActivity.openSettings",
                "extActivity.arguments",
                "extActivity.result",
                "extActivity.running",
                "extActivity.stats.window1d",
                "extActivity.stats.window7d",
                "extActivity.stats.window30d",
                "extActivity.stats.window90d",
                "extActivity.stats.empty",
                "extActivity.stats.summaryLine",
                "settings.dashboard.usage.title",
                "settings.dashboard.usage.description",
                "settings.dashboard.usage.llmHeader",
                "settings.dashboard.usage.window1d",
                "settings.dashboard.usage.window7d",
                "settings.dashboard.usage.window30d",
                "settings.dashboard.usage.window90d",
                "settings.dashboard.usage.tab1d",
                "settings.dashboard.usage.tab7d",
                "settings.dashboard.usage.tab30d",
                "settings.dashboard.usage.tab90d",
                "settings.dashboard.usage.topModelsHeader",
                "settings.dashboard.usage.noData",
                "settings.dashboard.usage.mcpHeader",
                "settings.dashboard.usage.openExternalActivity",
                "settings.dashboard.usage.mcpSummaryLine",
                "settings.dashboard.usage.storageFile",
                "settings.dashboard.usage.revealFile",
                "settings.dashboard.usage.reload",
                "settings.dashboard.usage.clearHistory",
                "settings.dashboard.usage.clearHistory.title",
                "settings.dashboard.usage.clearHistory.message",
                "settings.dashboard.usage.clearHistory.confirm",
                "settings.dashboard.usage.col.period",
                "settings.dashboard.usage.col.calls",
                "settings.dashboard.usage.col.days",
                "settings.dashboard.usage.col.tokens",
                "settings.dashboard.usage.col.cacheHit",
                "settings.dashboard.usage.col.cost",
                "settings.dashboard.usage.col.model",
                "settings.dashboard.usage.col.modelTokens",
                "settings.model.resetSessionUsage",
                "settings.model.resetSessionUsageTooltip",
                "settings.model.usageHistory",
                "settings.model.usageHistoryHint",
            };

            foreach (var key in requiredKeys)
            {
                Assert.IsTrue(
                    en.TryGetValue(key, out var enVal) && enVal.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)enVal),
                    $"Missing or empty key in en-US.json: {key}");
                Assert.IsTrue(
                    zh.TryGetValue(key, out var zhVal) && zhVal.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)zhVal),
                    $"Missing or empty key in zh-CN.json: {key}");
            }
        }

        private static JObject LoadL10nJson(string languageCode)
        {
            var candidates = new List<string>();

            var pkgInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(LanguageManager).Assembly);
            if (pkgInfo != null && !string.IsNullOrWhiteSpace(pkgInfo.resolvedPath))
            {
                candidates.Add(Path.Combine(pkgInfo.resolvedPath, "Editor", "L10n", "Resources", languageCode + ".json"));
            }

            candidates.Add(Path.Combine("Packages", "com.agentcore.unity", "Editor", "L10n", "Resources", languageCode + ".json"));
            candidates.Add(Path.Combine("Editor", "L10n", "Resources", languageCode + ".json"));

            foreach (var path in candidates)
            {
                if (File.Exists(path))
                {
                    return JObject.Parse(File.ReadAllText(path));
                }
            }

            Assert.Fail($"Unable to locate L10n resource '{languageCode}.json'. Tried: {string.Join(" | ", candidates)}");
            return null;
        }

        #endregion
    }
}
