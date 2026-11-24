using System;
using System.Globalization;
using AgentCore.Editor.Config;
using AgentCore.Editor.Core;
using AgentCore.Editor.L10n;
using AgentCore.Editor.Utils;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.UI
{
    /// <summary>
    /// 外部活动：MCP 客户端（Claude Code、Codex 等）在本编辑器里调用了哪些工具、结果如何。
    /// 数据来自 <see cref="ExternalToolActivity"/>（内存中最近 200 条，启动/域重载时自动从审计日志水合）；
    /// 完整记录见工程的 Library/AgentCore/Mcp/audit.jsonl。
    /// </summary>
    public sealed class ExternalActivityWindow : EditorWindow
    {
        internal const string SelectedWindowPrefKey = "AgentCore.ExternalActivity.SelectedWindow";

        private static readonly ActivityWindow[] StatWindows =
        {
            ActivityWindow.Today,
            ActivityWindow.Days7,
            ActivityWindow.Days30,
            ActivityWindow.Days90,
        };

        private Vector2 _listScroll;
        private Vector2 _detailScroll;
        private string _selectedId;
        private ActivityWindow _selectedWindow = ActivityWindow.Today;
        private bool _subscribed;

        [MenuItem("Window/AgentCore External Activity")]
        public static void ShowWindow()
        {
            var window = GetWindow<ExternalActivityWindow>();
            window.titleContent = new GUIContent(Loc.Tr("extActivity.windowTitle", "AgentCore External Activity"));
            window.minSize = new Vector2(460, 280);
            window.Show();
        }

        internal static bool IsValidActivityWindow(int rawValue)
        {
            return rawValue == (int)ActivityWindow.Today
                || rawValue == (int)ActivityWindow.Days7
                || rawValue == (int)ActivityWindow.Days30
                || rawValue == (int)ActivityWindow.Days90;
        }

        internal static ActivityWindow LoadSelectedWindow(string prefKey = SelectedWindowPrefKey)
        {
            var key = string.IsNullOrEmpty(prefKey) ? SelectedWindowPrefKey : prefKey;
            var raw = EditorPrefs.GetInt(key, (int)ActivityWindow.Today);
            return IsValidActivityWindow(raw) ? (ActivityWindow)raw : ActivityWindow.Today;
        }

        internal static void SaveSelectedWindow(ActivityWindow window, string prefKey = SelectedWindowPrefKey)
        {
            var key = string.IsNullOrEmpty(prefKey) ? SelectedWindowPrefKey : prefKey;
            var normalized = IsValidActivityWindow((int)window) ? window : ActivityWindow.Today;
            EditorPrefs.SetInt(key, (int)normalized);
        }

        private void OnEnable()
        {
            _selectedWindow = LoadSelectedWindow();
            titleContent = new GUIContent(Loc.Tr("extActivity.windowTitle", "AgentCore External Activity"));

            if (!_subscribed)
            {
                ExternalToolActivity.Changed += OnChanged;
                LanguageManager.LanguageChanged += OnLanguageChanged;
                _subscribed = true;
            }

            // 触发首次水合，确保首次打开或 Domain Reload 后立即展示历史记录与统计
            ExternalToolActivity.Snapshot();
            Repaint();
        }

        private void OnDisable()
        {
            if (_subscribed)
            {
                ExternalToolActivity.Changed -= OnChanged;
                LanguageManager.LanguageChanged -= OnLanguageChanged;
                _subscribed = false;
            }
        }

        private void OnChanged() => MainThread.Post(Repaint);

        private void OnLanguageChanged(string _)
        {
            titleContent = new GUIContent(Loc.Tr("extActivity.windowTitle", "AgentCore External Activity"));
            Repaint();
        }

        private void OnGUI()
        {
            var records = ExternalToolActivity.Snapshot();

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                var running = 0;
                foreach (var r in records)
                {
                    if (r.IsRunning)
                        running++;
                }
                GUILayout.Label(
                    running > 0
                        ? Loc.Tr("extActivity.runningSummary", "{0} running · {1} recent", running, records.Count)
                        : Loc.Tr("extActivity.recentCalls", "{0} recent calls", records.Count),
                    EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(Loc.Tr("extActivity.reload", "Reload"), EditorStyles.toolbarButton))
                {
                    ExternalToolActivity.ReloadFromAuditLog();
                    records = ExternalToolActivity.Snapshot();
                }
                if (GUILayout.Button(Loc.Tr("extActivity.openAuditLog", "Open audit log"), EditorStyles.toolbarButton))
                {
                    var path = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Library", "AgentCore", "Mcp", "audit.jsonl");
                    if (System.IO.File.Exists(path))
                        EditorUtility.RevealInFinder(path);
                    else
                        ShowNotification(new GUIContent(Loc.Tr("extActivity.noAuditLog", "No audit log yet")));
                }
                if (GUILayout.Button(Loc.Tr("extActivity.clear", "Clear"), EditorStyles.toolbarButton))
                {
                    ExternalToolActivity.Clear();
                    _selectedId = null;
                    records = ExternalToolActivity.Snapshot();
                }
            }

            DrawStatsSummaryBar();

            if (records.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    Loc.Tr(
                        "extActivity.emptyHint",
                        "No calls from external clients yet. Enable MCP Server in Preferences > AgentCore > Tools & Extensions and connect Claude Code or Codex."),
                    MessageType.Info);
                if (GUILayout.Button(Loc.Tr("extActivity.openSettings", "Open Tools & Extensions")))
                {
                    AgentCoreSettingsProvider.OpenPreferences(AgentCoreSettingsProvider.ToolsExtensionsSectionId);
                }
                return;
            }

            _listScroll = EditorGUILayout.BeginScrollView(_listScroll, GUILayout.ExpandHeight(true));
            ExternalToolActivity.Record selected = null;
            foreach (var record in records)
            {
                if (record.Id == _selectedId)
                    selected = record;
                var label = $"{record.StartedAt:HH:mm:ss}  {StatusMark(record)}  {record.Source}  →  {record.Tool}" +
                            (string.IsNullOrEmpty(record.Action) ? string.Empty : " / " + record.Action) +
                            $"   ({record.ElapsedSeconds:0.0}s)";
                var style = record.Id == _selectedId ? EditorStyles.boldLabel : EditorStyles.label;
                if (GUILayout.Button(label, style))
                    _selectedId = record.Id;
            }
            EditorGUILayout.EndScrollView();

            if (selected != null)
            {
                EditorGUILayout.Space();
                _detailScroll = EditorGUILayout.BeginScrollView(_detailScroll, GUILayout.Height(140));
                EditorGUILayout.LabelField(Loc.Tr("extActivity.arguments", "Arguments"), EditorStyles.boldLabel);
                EditorGUILayout.SelectableLabel(selected.ArgumentsSummary ?? string.Empty, EditorStyles.wordWrappedLabel, GUILayout.MinHeight(30));
                EditorGUILayout.LabelField(Loc.Tr("extActivity.result", "Result"), EditorStyles.boldLabel);
                EditorGUILayout.SelectableLabel(selected.IsRunning ? Loc.Tr("extActivity.running", "(running)") : selected.ResultPreview ?? string.Empty, EditorStyles.wordWrappedLabel, GUILayout.MinHeight(50));
                EditorGUILayout.EndScrollView();
            }

            // 有调用在执行时保持计时刷新
            foreach (var record in records)
            {
                if (record.IsRunning)
                {
                    Repaint();
                    break;
                }
            }
        }

        private void DrawStatsSummaryBar()
        {
            var stats1d = ExternalToolActivity.GetWindowStats(ActivityWindow.Today);
            var stats7d = ExternalToolActivity.GetWindowStats(ActivityWindow.Days7);
            var stats30d = ExternalToolActivity.GetWindowStats(ActivityWindow.Days30);
            var stats90d = ExternalToolActivity.GetWindowStats(ActivityWindow.Days90);

            ActivityWindowStats selectedStats;
            switch (_selectedWindow)
            {
                case ActivityWindow.Days7:
                    selectedStats = stats7d;
                    break;
                case ActivityWindow.Days30:
                    selectedStats = stats30d;
                    break;
                case ActivityWindow.Days90:
                    selectedStats = stats90d;
                    break;
                default:
                    selectedStats = stats1d;
                    break;
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var selectedIdx = Array.IndexOf(StatWindows, _selectedWindow);
                    if (selectedIdx < 0) selectedIdx = 0;

                    var labels = new[]
                    {
                        $"{GetWindowLabel(ActivityWindow.Today)} ({stats1d.TotalCalls})",
                        $"{GetWindowLabel(ActivityWindow.Days7)} ({stats7d.TotalCalls})",
                        $"{GetWindowLabel(ActivityWindow.Days30)} ({stats30d.TotalCalls})",
                        $"{GetWindowLabel(ActivityWindow.Days90)} ({stats90d.TotalCalls})",
                    };

                    var newIdx = GUILayout.Toolbar(selectedIdx, labels, EditorStyles.miniButton, GUILayout.Height(20));
                    if (newIdx != selectedIdx && newIdx >= 0 && newIdx < StatWindows.Length)
                    {
                        _selectedWindow = StatWindows[newIdx];
                        SaveSelectedWindow(_selectedWindow);
                    }
                }

                if (selectedStats == null || selectedStats.TotalCalls == 0)
                {
                    EditorGUILayout.LabelField(
                        Loc.Tr("extActivity.stats.empty", "No completed calls in this period."),
                        EditorStyles.miniLabel);
                }
                else
                {
                    var successRateText = string.Format(CultureInfo.InvariantCulture, "{0:F1}%", selectedStats.SuccessRate * 100.0);
                    var avgDurationText = FormatDuration(selectedStats.AverageDurationMs);
                    var topToolText = string.IsNullOrEmpty(selectedStats.TopToolName)
                        ? "-"
                        : $"{selectedStats.TopToolName} ({selectedStats.TopToolCalls})";

                    EditorGUILayout.LabelField(
                        Loc.Tr(
                            "extActivity.stats.summaryLine",
                            "Calls: {0} ({1} ok / {2} failed) · Success: {3} · Avg: {4} · Top: {5}",
                            selectedStats.TotalCalls,
                            selectedStats.SucceededCalls,
                            selectedStats.FailedCalls,
                            successRateText,
                            avgDurationText,
                            topToolText),
                        EditorStyles.wordWrappedMiniLabel);
                }
            }
        }

        internal static string GetWindowLabel(ActivityWindow window)
        {
            switch (window)
            {
                case ActivityWindow.Days7:
                    return Loc.Tr("extActivity.stats.window7d", "7d");
                case ActivityWindow.Days30:
                    return Loc.Tr("extActivity.stats.window30d", "30d");
                case ActivityWindow.Days90:
                    return Loc.Tr("extActivity.stats.window90d", "90d");
                default:
                    return Loc.Tr("extActivity.stats.window1d", "1d");
            }
        }

        private static string FormatDuration(double durationMs)
        {
            if (durationMs >= 1000.0)
                return string.Format(CultureInfo.InvariantCulture, "{0:F2}s", durationMs / 1000.0);
            return string.Format(CultureInfo.InvariantCulture, "{0:F0}ms", Math.Max(0.0, durationMs));
        }

        private static string StatusMark(ExternalToolActivity.Record record)
        {
            switch (record.Status)
            {
                case "running": return "…";
                case "ok": return "✓";
                case "partial": return "◐";
                case "cancelled": return "⊘";
                default: return "✗";
            }
        }
    }
}
