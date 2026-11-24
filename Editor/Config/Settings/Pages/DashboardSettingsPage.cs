using System;
using System.Globalization;
using System.IO;
using System.Linq;
using AgentCore.Editor.Core;
using AgentCore.Editor.Extensions;
using AgentCore.Editor.L10n;
using AgentCore.Editor.LLM;
using AgentCore.Editor.Tools;
using AgentCore.Editor.UI;
using AgentCore.Editor.Utils;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Config.Settings.Pages
{
    /// <summary>
    /// Dashboard page showing setup status, persistent usage statistics, quick actions, and package info.
    /// </summary>
    public sealed class DashboardSettingsPage : IAgentCoreSettingsPage
    {
        internal const string ModelBreakdownWindowPrefKey = "AgentCore.Dashboard.UsageModelWindow";

        private static readonly UsageWindow[] DashboardUsageWindows =
        {
            UsageWindow.Today,
            UsageWindow.Days7,
            UsageWindow.Days30,
            UsageWindow.Days90,
        };

        /// <inheritdoc />
        public string Id => "dashboard";

        /// <inheritdoc />
        public string Title => Loc.Tr("settings.dashboard.title", "Dashboard");

        /// <inheritdoc />
        public string Description => Loc.Tr(
            "settings.dashboard.description",
            "Overview of AgentCore configuration status and quick actions.");

        /// <inheritdoc />
        public int Order => 100;

        /// <inheritdoc />
        public void OnActivate(AgentCoreSettingsContext context) { }

        /// <inheritdoc />
        public void OnDeactivate(AgentCoreSettingsContext context) { }

        /// <inheritdoc />
        public void Draw(AgentCoreSettingsContext context)
        {
            // v1.4.2: package info merged into the bottom of Setup Status to save a full card.
            DrawSetupStatusCard(context);
            EditorGUILayout.Space(8);
            DrawUsageStatisticsCard(context);
            EditorGUILayout.Space(8);
            DrawLanguageCard(context);
            EditorGUILayout.Space(8);
            DrawQuickActionsCard(context);
            EditorGUILayout.Space(8);
            DrawLogVerbosityCard(context);
        }

        /// <summary>
        /// v1.23.0+: 持久化 LLM 用量统计（1d / 7d / 30d / 90d）与外部 MCP 工具活动卡片。
        /// </summary>
        private static void DrawUsageStatisticsCard(AgentCoreSettingsContext context)
        {
            context.Ui.DrawCard(
                Loc.Tr("settings.dashboard.usage.title", "Usage Statistics (1d / 7d / 30d / 90d)"),
                Loc.Tr(
                    "settings.dashboard.usage.description",
                    "Persistent cross-project LLM usage and external MCP tool activity over the last 90 days."),
                () =>
                {
                    var s1 = UsageLedger.GetWindowSummary(UsageWindow.Today);
                    var s7 = UsageLedger.GetWindowSummary(UsageWindow.Days7);
                    var s30 = UsageLedger.GetWindowSummary(UsageWindow.Days30);
                    var s90 = UsageLedger.GetWindowSummary(UsageWindow.Days90);

                    // ── Multi-window LLM summary table ──
                    EditorGUILayout.LabelField(
                        Loc.Tr("settings.dashboard.usage.llmHeader", "LLM Usage"),
                        EditorStyles.boldLabel);

                    DrawUsageTableHeader();
                    DrawUsageTableRow(Loc.Tr("settings.dashboard.usage.window1d", "Today (1d)"), s1);
                    DrawUsageTableRow(Loc.Tr("settings.dashboard.usage.window7d", "7 Days"), s7);
                    DrawUsageTableRow(Loc.Tr("settings.dashboard.usage.window30d", "30 Days"), s30);
                    DrawUsageTableRow(Loc.Tr("settings.dashboard.usage.window90d", "90 Days"), s90);

                    EditorGUILayout.Space(6);

                    // ── Top models breakdown (selectable window, default 30d) ──
                    var selectedModelWindow = LoadModelBreakdownWindow();
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(
                        Loc.Tr("settings.dashboard.usage.topModelsHeader", "Top Models"),
                        EditorStyles.boldLabel,
                        GUILayout.Width(100));

                    var currentIdx = Array.IndexOf(DashboardUsageWindows, selectedModelWindow);
                    if (currentIdx < 0) currentIdx = 2; // default 30d
                    var periodLabels = new[]
                    {
                        Loc.Tr("settings.dashboard.usage.tab1d", "1d"),
                        Loc.Tr("settings.dashboard.usage.tab7d", "7d"),
                        Loc.Tr("settings.dashboard.usage.tab30d", "30d"),
                        Loc.Tr("settings.dashboard.usage.tab90d", "90d"),
                    };
                    var newIdx = GUILayout.Toolbar(currentIdx, periodLabels, EditorStyles.miniButton, GUILayout.Width(180));
                    if (newIdx != currentIdx && newIdx >= 0 && newIdx < DashboardUsageWindows.Length)
                    {
                        selectedModelWindow = DashboardUsageWindows[newIdx];
                        SaveModelBreakdownWindow(selectedModelWindow);
                    }
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.EndHorizontal();

                    var selectedSummary = selectedModelWindow switch
                    {
                        UsageWindow.Today => s1,
                        UsageWindow.Days7 => s7,
                        UsageWindow.Days90 => s90,
                        _ => s30,
                    };

                    if (selectedSummary?.Models == null || selectedSummary.Models.Count == 0)
                    {
                        EditorGUILayout.LabelField(
                            Loc.Tr("settings.dashboard.usage.noData", "No usage recorded in this period."),
                            EditorStyles.miniLabel);
                    }
                    else
                    {
                        DrawModelBreakdownHeader();
                        var limit = Math.Min(5, selectedSummary.Models.Count);
                        for (var i = 0; i < limit; i++)
                        {
                            DrawModelBreakdownRow(selectedSummary.Models[i]);
                        }
                    }

                    EditorGUILayout.Space(6);

                    // ── External MCP Tool Activity summary ──
                    var ext1 = ExternalToolActivity.GetWindowStats(ActivityWindow.Today);
                    var ext7 = ExternalToolActivity.GetWindowStats(ActivityWindow.Days7);
                    var ext30 = ExternalToolActivity.GetWindowStats(ActivityWindow.Days30);
                    var ext90 = ExternalToolActivity.GetWindowStats(ActivityWindow.Days90);

                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(
                        Loc.Tr("settings.dashboard.usage.mcpHeader", "External MCP Activity"),
                        EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(
                        Loc.Tr("settings.dashboard.usage.openExternalActivity", "Open External Activity"),
                        EditorStyles.miniButton,
                        GUILayout.Width(150)))
                    {
                        ExternalActivityWindow.ShowWindow();
                    }
                    EditorGUILayout.EndHorizontal();

                    EditorGUILayout.LabelField(
                        Loc.Tr(
                            "settings.dashboard.usage.mcpSummaryLine",
                            "1d: {0} ({1}) · 7d: {2} ({3}) · 30d: {4} ({5}) · 90d: {6} ({7})",
                            ext1.TotalCalls, FormatSuccessRate(ext1),
                            ext7.TotalCalls, FormatSuccessRate(ext7),
                            ext30.TotalCalls, FormatSuccessRate(ext30),
                            ext90.TotalCalls, FormatSuccessRate(ext90)),
                        EditorStyles.wordWrappedMiniLabel);

                    EditorGUILayout.Space(6);

                    // ── Global storage path & maintenance actions ──
                    var historyPath = UsageLedger.HistoryFilePath ?? string.Empty;
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(
                        Loc.Tr("settings.dashboard.usage.storageFile", "Storage File"),
                        EditorStyles.miniLabel,
                        GUILayout.Width(90));
                    EditorGUILayout.SelectableLabel(
                        historyPath,
                        EditorStyles.miniTextField,
                        GUILayout.Height(18));
                    EditorGUILayout.EndHorizontal();

                    EditorGUILayout.Space(2);
                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button(
                        Loc.Tr("settings.dashboard.usage.revealFile", "Open Storage Folder"),
                        GUILayout.Width(150)))
                    {
                        RevealUsageHistoryPath(historyPath);
                    }

                    if (GUILayout.Button(
                        Loc.Tr("settings.dashboard.usage.reload", "Reload"),
                        GUILayout.Width(90)))
                    {
                        UsageLedger.ReloadFromDisk();
                        ExternalToolActivity.ReloadFromAuditLog();
                    }

                    GUILayout.FlexibleSpace();

                    var prevColor = GUI.color;
                    GUI.color = new Color(1f, 0.72f, 0.72f);
                    if (GUILayout.Button(
                        Loc.Tr("settings.dashboard.usage.clearHistory", "Clear Usage History"),
                        GUILayout.Width(150)))
                    {
                        if (EditorUtility.DisplayDialog(
                            Loc.Tr("settings.dashboard.usage.clearHistory.title", "Clear Usage History"),
                            Loc.Tr(
                                "settings.dashboard.usage.clearHistory.message",
                                "This will clear all persisted 90-day cross-project LLM usage statistics and current session counters.\n\nThis action cannot be undone."),
                            Loc.Tr("settings.dashboard.usage.clearHistory.confirm", "Clear"),
                            Loc.Tr("common.cancel", "Cancel")))
                        {
                            UsageLedger.ClearHistory();
                            AgentCoreLog.Info("[AgentCore] Persistent LLM usage history cleared.");
                        }
                    }
                    GUI.color = prevColor;
                    EditorGUILayout.EndHorizontal();
                });
        }

        private static UsageWindow LoadModelBreakdownWindow()
        {
            var raw = EditorPrefs.GetInt(ModelBreakdownWindowPrefKey, (int)UsageWindow.Days30);
            return raw == (int)UsageWindow.Today
                || raw == (int)UsageWindow.Days7
                || raw == (int)UsageWindow.Days30
                || raw == (int)UsageWindow.Days90
                ? (UsageWindow)raw
                : UsageWindow.Days30;
        }

        private static void SaveModelBreakdownWindow(UsageWindow window)
        {
            EditorPrefs.SetInt(ModelBreakdownWindowPrefKey, (int)window);
        }

        private static void DrawUsageTableHeader()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(Loc.Tr("settings.dashboard.usage.col.period", "Period"), EditorStyles.miniBoldLabel, GUILayout.Width(76));
            EditorGUILayout.LabelField(Loc.Tr("settings.dashboard.usage.col.calls", "Calls"), EditorStyles.miniBoldLabel, GUILayout.Width(50));
            EditorGUILayout.LabelField(Loc.Tr("settings.dashboard.usage.col.days", "Days"), EditorStyles.miniBoldLabel, GUILayout.Width(42));
            EditorGUILayout.LabelField(Loc.Tr("settings.dashboard.usage.col.tokens", "Tokens (In / Out / Cache)"), EditorStyles.miniBoldLabel, GUILayout.MinWidth(140));
            EditorGUILayout.LabelField(Loc.Tr("settings.dashboard.usage.col.cacheHit", "Cache"), EditorStyles.miniBoldLabel, GUILayout.Width(56));
            EditorGUILayout.LabelField(Loc.Tr("settings.dashboard.usage.col.cost", "Cost"), EditorStyles.miniBoldLabel, GUILayout.Width(72));
            EditorGUILayout.EndHorizontal();
        }

        private static void DrawUsageTableRow(string periodLabel, UsagePeriodSummary summary)
        {
            var calls = summary?.CallCount ?? 0;
            var activeDays = summary?.ActiveDays ?? 0;
            var tokensText = calls > 0
                ? $"{UsageLedger.FormatTokens(summary.TotalTokens)} ({UsageLedger.FormatTokens(summary.InputTokens)} / {UsageLedger.FormatTokens(summary.OutputTokens)} / {UsageLedger.FormatTokens(summary.CacheReadTokens)})"
                : "-";
            var cacheText = calls > 0 && summary.CacheMetricsKnown && summary.InputTokens > 0
                ? string.Format(CultureInfo.InvariantCulture, "{0:F1}%", summary.CacheHitRate * 100.0)
                : "-";
            var costText = FormatCost(summary?.CostUsd ?? 0.0, summary?.CostPartial ?? false, calls);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(periodLabel, EditorStyles.miniLabel, GUILayout.Width(76));
            EditorGUILayout.LabelField(calls.ToString(CultureInfo.InvariantCulture), EditorStyles.miniLabel, GUILayout.Width(50));
            EditorGUILayout.LabelField(activeDays.ToString(CultureInfo.InvariantCulture), EditorStyles.miniLabel, GUILayout.Width(42));
            EditorGUILayout.LabelField(tokensText, EditorStyles.miniLabel, GUILayout.MinWidth(140));
            EditorGUILayout.LabelField(cacheText, EditorStyles.miniLabel, GUILayout.Width(56));
            EditorGUILayout.LabelField(costText, EditorStyles.miniLabel, GUILayout.Width(72));
            EditorGUILayout.EndHorizontal();
        }

        private static void DrawModelBreakdownHeader()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(Loc.Tr("settings.dashboard.usage.col.model", "Model"), EditorStyles.miniBoldLabel, GUILayout.MinWidth(130));
            EditorGUILayout.LabelField(Loc.Tr("settings.dashboard.usage.col.calls", "Calls"), EditorStyles.miniBoldLabel, GUILayout.Width(50));
            EditorGUILayout.LabelField(Loc.Tr("settings.dashboard.usage.col.modelTokens", "Tokens"), EditorStyles.miniBoldLabel, GUILayout.Width(100));
            EditorGUILayout.LabelField(Loc.Tr("settings.dashboard.usage.col.cost", "Cost"), EditorStyles.miniBoldLabel, GUILayout.Width(72));
            EditorGUILayout.EndHorizontal();
        }

        private static void DrawModelBreakdownRow(UsageModelBreakdown model)
        {
            if (model == null) return;
            var tokenText = $"{UsageLedger.FormatTokens(model.TotalTokens)} ({UsageLedger.FormatTokens(model.InputTokens)}/{UsageLedger.FormatTokens(model.OutputTokens)})";
            var costText = FormatCost(model.CostUsd, model.CostPartial, model.CallCount);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(model.Model ?? "unknown", EditorStyles.miniLabel, GUILayout.MinWidth(130));
            EditorGUILayout.LabelField(model.CallCount.ToString(CultureInfo.InvariantCulture), EditorStyles.miniLabel, GUILayout.Width(50));
            EditorGUILayout.LabelField(tokenText, EditorStyles.miniLabel, GUILayout.Width(100));
            EditorGUILayout.LabelField(costText, EditorStyles.miniLabel, GUILayout.Width(72));
            EditorGUILayout.EndHorizontal();
        }

        private static string FormatCost(double costUsd, bool costPartial, long callCount)
        {
            if (callCount <= 0)
                return "-";
            if (costUsd > 0)
            {
                var value = costUsd >= 1.0
                    ? costUsd.ToString("F2", CultureInfo.InvariantCulture)
                    : costUsd.ToString("F4", CultureInfo.InvariantCulture);
                return $"${value}{(costPartial ? "+" : string.Empty)}";
            }

            return costPartial ? Loc.Tr("contextUsage.cost.notAvailable", "N/A") : "$0";
        }

        private static string FormatSuccessRate(ActivityWindowStats stats)
        {
            if (stats == null || stats.TotalCalls <= 0)
                return "-";
            return string.Format(CultureInfo.InvariantCulture, "{0:F0}%", stats.SuccessRate * 100.0);
        }

        private static void RevealUsageHistoryPath(string historyPath)
        {
            if (string.IsNullOrWhiteSpace(historyPath))
                return;

            if (File.Exists(historyPath))
            {
                EditorUtility.RevealInFinder(historyPath);
                return;
            }

            var directory = Path.GetDirectoryName(historyPath);
            if (!string.IsNullOrEmpty(directory))
            {
                if (!Directory.Exists(directory))
                {
                    try { Directory.CreateDirectory(directory); }
                    catch { /* ignore and fall through */ }
                }

                if (Directory.Exists(directory))
                {
                    EditorUtility.RevealInFinder(directory);
                }
            }
        }

        /// <summary>
        /// v1.9.0+: 语言 (Language) 设置卡片.
        /// UI 语言用 EditorPrefs 全局持久化, 独立于 AgentCoreSettings.
        /// </summary>
        private static void DrawLanguageCard(AgentCoreSettingsContext context)
        {
            context.Ui.DrawCard(
                AgentCore.Editor.L10n.Loc.Tr("settings.language.card.title", "Language"),
                AgentCore.Editor.L10n.Loc.Tr(
                    "settings.language.card.description",
                    "UI language for the AgentCore editor plugin. Stored globally across projects. Switching applies immediately to all AgentCore windows."),
                () =>
                {
                    var supported = AgentCore.Editor.L10n.LanguageManager.SupportedLanguages;

                    // 构建下拉选项 (display name)
                    var displayNames = new string[supported.Count];
                    var codes = new string[supported.Count];
                    var current = AgentCore.Editor.L10n.LanguageManager.CurrentLanguage;
                    int currentIndex = 0;
                    for (int i = 0; i < supported.Count; i++)
                    {
                        displayNames[i] = supported[i].DisplayName;
                        codes[i] = supported[i].Code;
                        if (string.Equals(supported[i].Code, current, StringComparison.OrdinalIgnoreCase))
                            currentIndex = i;
                    }

                    var newIndex = EditorGUILayout.Popup(
                        new GUIContent(
                            AgentCore.Editor.L10n.Loc.Tr("settings.language.field.label", "Interface language")),
                        currentIndex,
                        displayNames);
                    if (newIndex != currentIndex && newIndex >= 0 && newIndex < codes.Length)
                    {
                        AgentCore.Editor.L10n.LanguageManager.SetLanguage(codes[newIndex]);
                        AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] UI language changed to {codes[newIndex]}");
                    }

                    EditorGUILayout.Space(4);

                    // LLM 语言跟随开关
                    var currentFollow = AgentCore.Editor.L10n.LanguageManager.LlmFollowUiLanguage;
                    var newFollow = EditorGUILayout.ToggleLeft(
                        new GUIContent(
                            AgentCore.Editor.L10n.Loc.Tr("settings.language.llmFollow.label", "LLM replies follow UI language"),
                            AgentCore.Editor.L10n.Loc.Tr(
                                "settings.language.llmFollow.tooltip",
                                "When enabled, the assistant is instructed to reply in the same language as the UI. Disable to let the model decide by user input.")),
                        currentFollow);
                    if (newFollow != currentFollow)
                    {
                        AgentCore.Editor.L10n.LanguageManager.LlmFollowUiLanguage = newFollow;
                    }
                });
        }

        /// <summary>
        /// v1.6.5+: 日志详细程度下拉菜单。
        /// </summary>
        private static void DrawLogVerbosityCard(AgentCoreSettingsContext context)
        {
            context.Ui.DrawCard(
                Loc.Tr("settings.dashboard.logCard.title", "Log Verbosity"),
                Loc.Tr(
                    "settings.dashboard.logCard.description",
                    "Controls the verbosity of [AgentCore]-prefixed logs. If heavy logging during responses lags the Editor, switch to Warning or Error."),
                () =>
                {
                    var settings = context.Settings;
                    var current = settings.logLevel;
                    var newLevel = (AgentCore.Editor.Utils.LogLevel)EditorGUILayout.EnumPopup(
                        new GUIContent(
                            Loc.Tr("settings.dashboard.logLevel", "Log Level"),
                            Loc.Tr(
                                "settings.dashboard.logLevelTooltip",
                                "Silent: fully silent (use with caution) · Error: errors only · Warning: default, warnings + errors · Info: key business events · Debug: everything, incl. streaming token / per-event high-frequency logs")),
                        current);
                    if (newLevel != current)
                    {
                        settings.logLevel = newLevel;
                        settings.SaveSettings();
                        AgentCore.Editor.Utils.AgentCoreLog.Invalidate();
                        AgentCoreLog.Info($"[AgentCore] Log level changed: {current} -> {newLevel}");
                    }
                });
        }

        private static void DrawSetupStatusCard(AgentCoreSettingsContext context)
        {
            var settings = context.Settings;

            context.Ui.DrawCard(
                Loc.Tr("settings.dashboard.setup.title", "Setup Status"),
                Loc.Tr("settings.dashboard.setup.description", "Current configuration state for core services."),
                () =>
            {
                // LLM — required, so missing active profile is an Error.
                DrawServiceStatusRow(context,
                    Loc.Tr("settings.dashboard.service.llm", "LLM"),
                    isEnabled: ActiveModelConfig.IsUsingProfile,
                    enabledDetail: ActiveModelConfig.IsUsingProfile ? ActiveModelConfig.ModelName : "",
                    disabledIsError: true);

                // Memory (mem0) — optional.
                DrawServiceStatusRow(context,
                    Loc.Tr("settings.dashboard.service.memory", "Memory"),
                    isEnabled: settings.mem0Enabled,
                    enabledDetail: settings.mem0Endpoint,
                    disabledIsError: false);

                // Knowledge (LightRAG) — optional.
                DrawServiceStatusRow(context,
                    Loc.Tr("settings.dashboard.service.knowledge", "Knowledge"),
                    isEnabled: settings.lightragEnabled,
                    enabledDetail: settings.lightragEndpoint,
                    disabledIsError: false);

                // VCS — optional component.
                DrawServiceStatusRow(context,
                    Loc.Tr("settings.dashboard.service.vcs", "VCS"),
                    isEnabled: OptionalComponentManager.IsVcsEnabled(),
                    enabledDetail: null,
                    disabledIsError: false);

                // Tools — informational count.
                var allTools = ToolRegistry.Instance.GetAllTools();
                if (allTools != null && allTools.Count > 0)
                {
                    var enabledCount = allTools.Count(tool =>
                        tool?.Metadata != null && !settings.IsToolDisabled(tool.Metadata.Name, tool.Metadata.Category));
                    context.Ui.DrawStatusLabel(
                        Loc.Tr("settings.dashboard.toolsEnabled", "Tools: {0}/{1} enabled", enabledCount, allTools.Count),
                        SettingsStatusLevel.None,
                        miniLabel: true);
                }
                else
                {
                    context.Ui.DrawStatusLabel(
                        Loc.Tr("settings.dashboard.toolsNotInitialized", "Tools: not initialized yet"),
                        SettingsStatusLevel.Warning,
                        miniLabel: true);
                }

                // Package footer — merged in from the removed Package card.
                EditorGUILayout.Space(6);
                var footerStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleLeft,
                };
                footerStyle.normal.textColor = new Color(0.5f, 0.5f, 0.5f);
                EditorGUILayout.LabelField($"com.agentcore.unity  v{GetPackageVersion()}", footerStyle);
            });
        }

        /// <summary>
        /// Draws a single status row with a leading badge + service name + optional detail.
        /// Disabled services are dimmed (gray) and clearly labeled instead of using the default label color.
        /// </summary>
        private static void DrawServiceStatusRow(
            AgentCoreSettingsContext context,
            string serviceName,
            bool isEnabled,
            string enabledDetail,
            bool disabledIsError)
        {
            string text;
            SettingsStatusLevel level;

            if (isEnabled)
            {
                text = string.IsNullOrEmpty(enabledDetail)
                    ? Loc.Tr("settings.dashboard.status.enabled", "[ON]  {0}: Enabled", serviceName)
                    : Loc.Tr("settings.dashboard.status.enabledDetail", "[ON]  {0}: Enabled ({1})", serviceName, enabledDetail);
                level = SettingsStatusLevel.Success;
            }
            else if (disabledIsError)
            {
                text = Loc.Tr("settings.dashboard.status.missing", "[FAIL] {0}: Missing configuration", serviceName);
                level = SettingsStatusLevel.Error;
            }
            else
            {
                text = Loc.Tr("settings.dashboard.status.disabled", "[OFF] {0}: Disabled", serviceName);
                level = SettingsStatusLevel.None;
            }

            context.Ui.DrawStatusLabel(text, level, miniLabel: true);
        }

        private static void DrawQuickActionsCard(AgentCoreSettingsContext context)
        {
            // 所有 Quick Actions 按钮统一宽度，避免每行按钮宽度不一致（此前混用 140/150）
            const float ButtonWidth = 150f;

            context.Ui.DrawCard(
                Loc.Tr("settings.dashboard.quick.title", "Quick Actions"),
                Loc.Tr("settings.dashboard.quick.description", "Common shortcuts and maintenance actions."),
                () =>
            {
                EditorGUILayout.BeginHorizontal();

                if (GUILayout.Button(Loc.Tr("settings.dashboard.quick.openChat", "Open Chat Window"), GUILayout.Width(ButtonWidth)))
                {
                    ChatWindow.ShowWindow();
                }

                if (GUILayout.Button(Loc.Tr("settings.dashboard.quick.refreshTools", "Refresh Tool Registry"), GUILayout.Width(ButtonWidth)))
                {
                    AgentCore.Editor.Tools.Infrastructure.ToolAutoDiscovery.DiscoverAndRegisterAll();
                    AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] Tool registry refreshed.");
                }

                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();

                if (GUILayout.Button(Loc.Tr("settings.dashboard.quick.openLogs", "Open Logs"), GUILayout.Width(ButtonWidth)))
                {
                    var logPath = GetEditorLogPath();
                    if (!string.IsNullOrEmpty(logPath) && File.Exists(logPath))
                    {
                        EditorUtility.OpenWithDefaultApp(logPath);
                    }
                    else
                    {
                        EditorUtility.DisplayDialog(
                            Loc.Tr("settings.dashboard.quick.openLogs", "Open Logs"),
                            Loc.Tr("settings.dashboard.logs.notFound", "Could not locate the Unity Editor log file."),
                            Loc.Tr("common.ok", "OK"));
                    }
                }

                if (GUILayout.Button(Loc.Tr("settings.dashboard.reset.button", "Reset Settings"), GUILayout.Width(ButtonWidth)))
                {
                    if (EditorUtility.DisplayDialog(
                        Loc.Tr("settings.dashboard.reset.title", "Reset Settings"),
                        Loc.Tr("settings.dashboard.reset.message", "This will reset all AgentCore settings to their default values.\n\nThis action cannot be undone."),
                        Loc.Tr("settings.dashboard.reset.confirm", "Reset"),
                        Loc.Tr("common.cancel", "Cancel")))
                    {
                        context.Settings.ResetToDefaults();
                        AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] Settings reset to defaults.");
                    }
                }

                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();

                if (GUILayout.Button(Loc.Tr("settings.dashboard.clearKeys.button", "Clear Secure Keys"), GUILayout.Width(ButtonWidth)))
                {
                    if (EditorUtility.DisplayDialog(
                        Loc.Tr("settings.dashboard.clearKeys.title", "Clear Secure Keys"),
                        Loc.Tr("settings.dashboard.clearKeys.message", "This will clear all stored API keys (LLM, mem0, LightRAG).\n\nThis action cannot be undone."),
                        Loc.Tr("settings.dashboard.clearKeys.confirm", "Clear"),
                        Loc.Tr("common.cancel", "Cancel")))
                    {
                        SecureKeyStorage.ClearAll();
                        AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] All secure keys cleared.");
                    }
                }

                if (GUILayout.Button(Loc.Tr("settings.dashboard.clearRules.button", "Clear Learned Rules"), GUILayout.Width(ButtonWidth)))
                {
                    if (EditorUtility.DisplayDialog(
                        Loc.Tr("settings.dashboard.clearRules.title", "Clear Learned Request Rules"),
                        Loc.Tr(
                            "settings.dashboard.clearRules.message",
                            "This will forget all auto-learned request field restrictions (e.g. which endpoints reject 'reasoning' or 'temperature').\n\n" +
                            "Each affected model will re-learn on its next call (one recoverable 400 retry)."),
                        Loc.Tr("settings.dashboard.clearRules.confirm", "Clear"),
                        Loc.Tr("common.cancel", "Cancel")))
                    {
                        AgentCore.Editor.LLM.RequestPruningRegistry.ClearAll();
                    }
                }

                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            });
        }

        private static string GetEditorLogPath()
        {
            var os = SystemInfo.operatingSystemFamily;
            if (os == OperatingSystemFamily.Windows)
            {
                var localAppData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(localAppData, "Unity", "Editor", "Editor.log");
            }
            else if (os == OperatingSystemFamily.MacOSX)
            {
                var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal);
                return Path.Combine(home, "Library", "Logs", "Unity", "Editor.log");
            }
            else
            {
                var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal);
                return Path.Combine(home, ".config", "unity3d", "Editor.log");
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
                AgentCoreLog.Warning($"[AgentCore] Failed to read package version: {ex.Message}");
            }

            return "unknown";
        }
    }
}
