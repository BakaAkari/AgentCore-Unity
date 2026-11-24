using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AgentCore.Editor.L10n;
using AgentCore.Editor.LLM;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Components
{
    /// <summary>
    /// Context usage visualization panel.
    /// Displays token usage, compression statistics, budget allocation, and persistent LLM usage across time windows.
    /// </summary>
    /// <remarks>
    /// v1.9.0+: 所有可见文本走 L10n. 订阅 <see cref="LanguageManager.LanguageChanged"/>,
    /// 语言切换时用最近一次的 budget 重绘 (标签 + 数值一起刷新).
    /// v1.23.0+: 支持 Session / 1d / 7d / 30d / 90d 周期切换与折叠状态持久化（EditorPrefs），
    /// 并订阅 <see cref="UsageLedger.OnChanged"/> 在清空历史、重置会话、切换会话与重载后实时刷新。
    /// </remarks>
    public class ContextUsagePanel : VisualElement
    {
        #region EditorPrefs Keys
        internal const string SelectedWindowPrefKey = "AgentCore.ContextUsage.SelectedWindow";
        internal const string CollapsedPrefKey = "AgentCore.ContextUsage.Collapsed";

        private static readonly UsageWindow[] PeriodWindows =
        {
            UsageWindow.Session,
            UsageWindow.Today,
            UsageWindow.Days7,
            UsageWindow.Days30,
            UsageWindow.Days90,
        };
        #endregion

        #region USS Class Names
        private const string UssClassName = "context-usage-panel";
        private const string HeaderClassName = "context-usage-panel__header";
        private const string ContentClassName = "context-usage-panel__content";
        private const string ProgressBarClassName = "context-usage-panel__progress-bar";
        private const string ProgressFillClassName = "context-usage-panel__progress-fill";
        private const string StatsRowClassName = "context-usage-panel__stats-row";
        private const string StatLabelClassName = "context-usage-panel__stat-label";
        private const string StatValueClassName = "context-usage-panel__stat-value";
        private const string CompressionBadgeClassName = "context-usage-panel__compression-badge";
        private const string CollapsedClassName = "context-usage-panel--collapsed";
        private const string PeriodRowClassName = "context-usage-panel__period-row";
        private const string PeriodButtonsClassName = "context-usage-panel__period-buttons";
        private const string PeriodButtonClassName = "context-usage-panel__period-btn";
        private const string PeriodButtonActiveClassName = "context-usage-panel__period-btn--active";
        #endregion

        #region UI Elements
        private readonly VisualElement _header;
        private readonly Label _headerLabel;
        private readonly Label _toggleIndicator;
        private readonly VisualElement _content;
        private readonly VisualElement _progressBar;
        private readonly VisualElement _progressFill;
        private readonly Label _usageLabel;
        private readonly Label _tokenAllocationLabel; // label 部分 (前缀), 用 Loc 本地化
        private readonly Label _tokenCountLabel;      // value 部分 (数字), 用 Loc format
        private readonly Label _compressionRowLabel;  // label 部分
        private readonly Label _compressionStatsLabel;// value 部分
        private readonly Label _compressionBadgeLabel;
        private readonly VisualElement _compressionBadge;
        private readonly Label _headerUsageLabel;     // v1.17.0: 折叠时也可见的缓存命中 / 费用摘要
        private readonly Label _periodRowLabel;
        private readonly Dictionary<UsageWindow, Button> _periodButtons = new Dictionary<UsageWindow, Button>();
        private readonly Label _llmUsageRowLabel;
        private readonly Label _llmUsageValueLabel;
        private readonly Label _lastCallRowLabel;
        private readonly Label _lastCallValueLabel;
        private bool _usageRefreshQueued;
        #endregion

        #region State
        private bool _isCollapsed = true;
        private UsageWindow _selectedWindow = UsageWindow.Session;
        private Core.ContextBudgetInfo _currentBudget;
        private readonly string _selectedWindowPrefKey;
        private readonly string _collapsedPrefKey;
        #endregion

        #region Factory
        /// <summary>
        /// Creates a new ContextUsagePanel instance.
        /// </summary>
        public new class UxmlFactory : UxmlFactory<ContextUsagePanel, UxmlTraits> { }

        public new class UxmlTraits : VisualElement.UxmlTraits { }
        #endregion

        #region Constructor
        public ContextUsagePanel()
            : this(SelectedWindowPrefKey, CollapsedPrefKey)
        {
        }

        internal ContextUsagePanel(string selectedWindowPrefKey, string collapsedPrefKey)
        {
            _selectedWindowPrefKey = string.IsNullOrEmpty(selectedWindowPrefKey)
                ? SelectedWindowPrefKey
                : selectedWindowPrefKey;
            _collapsedPrefKey = string.IsNullOrEmpty(collapsedPrefKey)
                ? CollapsedPrefKey
                : collapsedPrefKey;

            AddToClassList(UssClassName);

            // Header (整个 header 都可点击展开/收起,cursor + hover 由 USS 定义)
            _header = new VisualElement();
            _header.AddToClassList(HeaderClassName);
            _header.RegisterCallback<ClickEvent>(_ => ToggleCollapse());

            _headerLabel = new Label(Loc.Tr("contextUsage.header", "上下文使用情况"));
            _header.Add(_headerLabel);

            _headerUsageLabel = new Label();
            _headerUsageLabel.AddToClassList(StatValueClassName);
            _headerUsageLabel.style.flexGrow = 1;
            _headerUsageLabel.style.unityTextAlign = TextAnchor.MiddleRight;
            _headerUsageLabel.style.marginRight = 6;
            _header.Add(_headerUsageLabel);

            // 展开/收起指示符 (只是视觉指示,不再是可独立点击的 Button)
            _toggleIndicator = new Label("v");
            _toggleIndicator.AddToClassList("context-usage-panel__toggle-indicator");
            _header.Add(_toggleIndicator);

            Add(_header);

            // Content
            _content = new VisualElement();
            _content.AddToClassList(ContentClassName);

            // Progress bar
            _progressBar = new VisualElement();
            _progressBar.AddToClassList(ProgressBarClassName);

            _progressFill = new VisualElement();
            _progressFill.AddToClassList(ProgressFillClassName);
            _progressBar.Add(_progressFill);

            _content.Add(_progressBar);

            // Usage label
            _usageLabel = new Label();
            _usageLabel.AddToClassList(StatLabelClassName);
            _content.Add(_usageLabel);

            // Token count details
            var tokenRow = CreateStatsRow(out _tokenAllocationLabel, out _tokenCountLabel);
            _content.Add(tokenRow);

            // Compression stats
            var compressionRow = CreateStatsRow(out _compressionRowLabel, out _compressionStatsLabel);
            _content.Add(compressionRow);

            // v1.23.0: 周期选择器（Session | 1d | 7d | 30d | 90d）
            var periodRow = new VisualElement();
            periodRow.AddToClassList(PeriodRowClassName);

            _periodRowLabel = new Label();
            _periodRowLabel.AddToClassList(StatLabelClassName);
            periodRow.Add(_periodRowLabel);

            var periodButtonsContainer = new VisualElement();
            periodButtonsContainer.AddToClassList(PeriodButtonsClassName);
            for (var i = 0; i < PeriodWindows.Length; i++)
            {
                var window = PeriodWindows[i];
                var captured = window;
                var button = new Button(() => SetSelectedWindow(captured));
                button.AddToClassList(PeriodButtonClassName);
                button.name = $"context-usage-period-{window}";
                _periodButtons[window] = button;
                periodButtonsContainer.Add(button);
            }
            periodRow.Add(periodButtonsContainer);
            _content.Add(periodRow);

            // v1.17.0 / v1.23.0: LLM 实际用量（会话或持久化多周期统计）
            _content.Add(CreateStatsRow(out _llmUsageRowLabel, out _llmUsageValueLabel));
            _content.Add(CreateStatsRow(out _lastCallRowLabel, out _lastCallValueLabel));

            // Compression badge
            _compressionBadge = new VisualElement();
            _compressionBadge.AddToClassList(CompressionBadgeClassName);
            _compressionBadgeLabel = new Label();
            _compressionBadge.Add(_compressionBadgeLabel);
            _compressionBadge.style.display = DisplayStyle.None;
            _content.Add(_compressionBadge);

            Add(_content);

            // Restore persisted state (default collapsed = true, window = Session)
            _selectedWindow = LoadSelectedWindow(_selectedWindowPrefKey);
            UpdatePeriodButtonLabels();
            UpdatePeriodButtonSelection();

            // Initialize with empty data
            UpdateDisplay(new Core.ContextBudgetInfo());
            RenderUsage();

            SetCollapsed(LoadCollapsedState(_collapsedPrefKey), persist: false);

            // v1.9.0+: 订阅语言事件, 切语言时用最近 budget 重绘
            // v1.23.0+: 订阅 UsageLedger.OnChanged，重置会话/清空历史/绑定会话/重载磁盘均即时刷新
            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                LanguageManager.LanguageChanged += OnLanguageChanged;
                UsageLedger.OnUsageRecorded += OnUsageRecorded;
                UsageLedger.OnChanged += OnUsageChanged;
                RenderUsage();
            });
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                LanguageManager.LanguageChanged -= OnLanguageChanged;
                UsageLedger.OnUsageRecorded -= OnUsageRecorded;
                UsageLedger.OnChanged -= OnUsageChanged;
            });
        }
        #endregion

        #region Properties & Test Accessors
        /// <summary>Gets the currently selected usage period window.</summary>
        public UsageWindow SelectedWindow => _selectedWindow;

        /// <summary>Gets whether the panel is currently collapsed.</summary>
        public bool IsCollapsed => _isCollapsed;

        internal string HeaderUsageText => _headerUsageLabel.text;
        internal string LlmUsageRowLabelText => _llmUsageRowLabel.text;
        internal string LlmUsageValueText => _llmUsageValueLabel.text;
        internal string SecondaryRowLabelText => _lastCallRowLabel.text;
        internal string SecondaryValueText => _lastCallValueLabel.text;
        #endregion

        #region Persistence Helpers
        internal static bool IsValidUsageWindow(int rawValue)
        {
            return rawValue == (int)UsageWindow.Session
                || rawValue == (int)UsageWindow.Today
                || rawValue == (int)UsageWindow.Days7
                || rawValue == (int)UsageWindow.Days30
                || rawValue == (int)UsageWindow.Days90;
        }

        internal static UsageWindow LoadSelectedWindow(string prefKey = SelectedWindowPrefKey)
        {
            var key = string.IsNullOrEmpty(prefKey) ? SelectedWindowPrefKey : prefKey;
            var raw = EditorPrefs.GetInt(key, (int)UsageWindow.Session);
            return IsValidUsageWindow(raw) ? (UsageWindow)raw : UsageWindow.Session;
        }

        internal static void SaveSelectedWindow(UsageWindow window, string prefKey = SelectedWindowPrefKey)
        {
            var key = string.IsNullOrEmpty(prefKey) ? SelectedWindowPrefKey : prefKey;
            var normalized = IsValidUsageWindow((int)window) ? window : UsageWindow.Session;
            EditorPrefs.SetInt(key, (int)normalized);
        }

        internal static bool LoadCollapsedState(string prefKey = CollapsedPrefKey)
        {
            var key = string.IsNullOrEmpty(prefKey) ? CollapsedPrefKey : prefKey;
            return EditorPrefs.GetBool(key, true);
        }

        internal static void SaveCollapsedState(bool collapsed, string prefKey = CollapsedPrefKey)
        {
            var key = string.IsNullOrEmpty(prefKey) ? CollapsedPrefKey : prefKey;
            EditorPrefs.SetBool(key, collapsed);
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Updates the panel with new budget information.
        /// </summary>
        public void UpdateDisplay(Core.ContextBudgetInfo budget)
        {
            _currentBudget = budget;
            RenderCurrent();
        }

        /// <summary>
        /// Collapses or expands the panel and persists the state.
        /// </summary>
        public void SetCollapsed(bool collapsed)
        {
            SetCollapsed(collapsed, persist: true);
        }

        internal void SetCollapsed(bool collapsed, bool persist)
        {
            _isCollapsed = collapsed;
            if (persist)
            {
                SaveCollapsedState(collapsed, _collapsedPrefKey);
            }

            if (_isCollapsed)
            {
                AddToClassList(CollapsedClassName);
                _toggleIndicator.text = ">";
                _content.style.display = DisplayStyle.None;
            }
            else
            {
                RemoveFromClassList(CollapsedClassName);
                _toggleIndicator.text = "v";
                _content.style.display = DisplayStyle.Flex;
            }
        }

        /// <summary>
        /// Selects a usage period window and refreshes the usage display.
        /// </summary>
        public void SetSelectedWindow(UsageWindow window)
        {
            SetSelectedWindow(window, persist: true);
        }

        internal void SetSelectedWindow(UsageWindow window, bool persist)
        {
            var normalized = IsValidUsageWindow((int)window) ? window : UsageWindow.Session;
            _selectedWindow = normalized;
            if (persist)
            {
                SaveSelectedWindow(normalized, _selectedWindowPrefKey);
            }

            UpdatePeriodButtonSelection();
            RenderUsage();
        }

        /// <summary>Forces an immediate synchronous refresh of usage labels (useful in tests).</summary>
        internal void RefreshUsageNow()
        {
            RenderUsage();
        }
        #endregion

        #region Private Methods

        private void OnLanguageChanged(string _)
        {
            _headerLabel.text = Loc.Tr("contextUsage.header", "上下文使用情况");
            UpdatePeriodButtonLabels();
            RenderCurrent();
            RenderUsage();
        }

        /// <summary>
        /// v1.17.0: UsageLedger 事件可能来自后台线程（HTTP 回调），合并后切回主线程刷新。
        /// </summary>
        private void OnUsageRecorded(LlmUsage _) => QueueUsageRefresh();

        private void OnUsageChanged() => QueueUsageRefresh();

        private void QueueUsageRefresh()
        {
            if (_usageRefreshQueued) return;
            _usageRefreshQueued = true;
            EditorApplication.delayCall += () =>
            {
                _usageRefreshQueued = false;
                RenderUsage();
            };
        }

        private void UpdatePeriodButtonLabels()
        {
            _periodRowLabel.text = Loc.Tr("contextUsage.period.label", "统计周期");
            foreach (var kvp in _periodButtons)
            {
                kvp.Value.text = GetWindowShortLabel(kvp.Key);
            }
        }

        private void UpdatePeriodButtonSelection()
        {
            foreach (var kvp in _periodButtons)
            {
                if (kvp.Key == _selectedWindow)
                {
                    kvp.Value.AddToClassList(PeriodButtonActiveClassName);
                }
                else
                {
                    kvp.Value.RemoveFromClassList(PeriodButtonActiveClassName);
                }
            }
        }

        internal static string GetWindowShortLabel(UsageWindow window)
        {
            switch (window)
            {
                case UsageWindow.Today:
                    return Loc.Tr("contextUsage.period.1d", "今日");
                case UsageWindow.Days7:
                    return Loc.Tr("contextUsage.period.7d", "7天");
                case UsageWindow.Days30:
                    return Loc.Tr("contextUsage.period.30d", "30天");
                case UsageWindow.Days90:
                    return Loc.Tr("contextUsage.period.90d", "90天");
                default:
                    return Loc.Tr("contextUsage.period.session", "本次会话");
            }
        }

        private static string FormatCostUsd(double costUsd, bool partial)
        {
            if (costUsd > 0)
            {
                var formatted = costUsd >= 1.0
                    ? costUsd.ToString("F2", CultureInfo.InvariantCulture)
                    : costUsd.ToString("F4", CultureInfo.InvariantCulture);
                return $"≈${formatted}{(partial ? "+" : string.Empty)}";
            }

            return partial
                ? Loc.Tr("contextUsage.cost.notAvailable", "未知")
                : "$0";
        }

        /// <summary>v1.17.0 / v1.23.0: 按当前选中周期渲染 LLM 用量。</summary>
        private void RenderUsage()
        {
            if (_selectedWindow == UsageWindow.Session)
            {
                RenderSessionUsage();
            }
            else
            {
                RenderPeriodUsage(_selectedWindow);
            }
        }

        private void RenderSessionUsage()
        {
            var session = UsageLedger.Session;
            var last = UsageLedger.Last;
            double cost = UsageLedger.SessionCostUsd;
            bool partial = UsageLedger.SessionCostPartial;

            _llmUsageRowLabel.text = Loc.Tr("contextUsage.llmUsage.label", "LLM 用量");
            _lastCallRowLabel.text = Loc.Tr("contextUsage.lastCall.label", "上次调用");

            if (session == null || session.Calls == 0)
            {
                _llmUsageValueLabel.text = Loc.Tr("contextUsage.llmUsage.none", "暂无调用");
                _lastCallValueLabel.text = "-";
                _headerUsageLabel.text = string.Empty;
                return;
            }

            string costText = FormatCostUsd(cost, partial);
            string cacheStatus = session.CacheStatus == "unknown"
                ? Loc.Tr("contextUsage.cache.unknown", "未知")
                : session.CacheStatus;
            string usageText = Loc.Tr(
                "contextUsage.llmUsage.cacheStatusValue",
                "{0} 次 | 输入 {1}（缓存命中 {2}）| 输出 {3} | 费用 {4}",
                session.Calls,
                UsageLedger.FormatTokens(session.TotalInput),
                cacheStatus,
                UsageLedger.FormatTokens(session.Output),
                costText);

            if (session.Reasoning > 0)
            {
                usageText += " | " + Loc.Tr(
                    "contextUsage.reasoning.inline",
                    "推理 {0}",
                    UsageLedger.FormatTokens(session.Reasoning));
            }

            _llmUsageValueLabel.text = usageText;

            _lastCallValueLabel.text = Loc.Tr("contextUsage.cache.notReported", "缓存未知（服务未上报）");
            if (last != null && !last.IsEmpty && last.CacheMetricsKnown)
            {
                _lastCallValueLabel.text = Loc.Tr(
                    "contextUsage.lastCall.value",
                    "读缓存 {0} | 写缓存 {1} | 未缓存 {2} | 输出 {3}",
                    UsageLedger.FormatTokens(last.CacheRead),
                    UsageLedger.FormatTokens(last.CacheWrite),
                    UsageLedger.FormatTokens(last.InputUncached),
                    UsageLedger.FormatTokens(last.Output));
            }

            _headerUsageLabel.text = Loc.Tr(
                "contextUsage.cache.header",
                "缓存 {0} · {1}",
                cacheStatus,
                costText);
        }

        private void RenderPeriodUsage(UsageWindow window)
        {
            var windowLabel = GetWindowShortLabel(window);
            var summary = UsageLedger.GetWindowSummary(window);

            _llmUsageRowLabel.text = Loc.Tr("contextUsage.periodUsage.label", "LLM 用量（{0}）", windowLabel);
            _lastCallRowLabel.text = Loc.Tr("contextUsage.topModels.label", "常用模型");

            if (summary == null || summary.CallCount == 0)
            {
                _llmUsageValueLabel.text = Loc.Tr("contextUsage.llmUsage.none", "暂无调用");
                _lastCallValueLabel.text = "-";
                _headerUsageLabel.text = Loc.Tr(
                    "contextUsage.period.headerEmpty",
                    "{0} · 暂无调用",
                    windowLabel);
                return;
            }

            var cacheText = summary.CacheMetricsKnown && summary.InputTokens > 0
                ? string.Format(CultureInfo.InvariantCulture, "{0:F1}%", summary.CacheHitRate * 100.0)
                : Loc.Tr("contextUsage.cache.unknown", "未知");
            var costText = FormatCostUsd(summary.CostUsd, summary.CostPartial);

            var sb = new StringBuilder();
            if (summary.WindowDays > 1)
            {
                sb.Append(Loc.Tr(
                    "contextUsage.periodUsage.multiDayValue",
                    "{0} 次（{1} 天活跃）| 总计 {2} | 输入 {3}（读缓存 {4}, 命中 {5}）| 输出 {6}",
                    summary.CallCount,
                    summary.ActiveDays,
                    UsageLedger.FormatTokens(summary.TotalTokens),
                    UsageLedger.FormatTokens(summary.InputTokens),
                    UsageLedger.FormatTokens(summary.CacheReadTokens),
                    cacheText,
                    UsageLedger.FormatTokens(summary.OutputTokens)));
            }
            else
            {
                sb.Append(Loc.Tr(
                    "contextUsage.periodUsage.singleDayValue",
                    "{0} 次 | 总计 {1} | 输入 {2}（读缓存 {3}, 命中 {4}）| 输出 {5}",
                    summary.CallCount,
                    UsageLedger.FormatTokens(summary.TotalTokens),
                    UsageLedger.FormatTokens(summary.InputTokens),
                    UsageLedger.FormatTokens(summary.CacheReadTokens),
                    cacheText,
                    UsageLedger.FormatTokens(summary.OutputTokens)));
            }

            if (summary.ReasoningTokens > 0)
            {
                sb.Append(" | ");
                sb.Append(Loc.Tr(
                    "contextUsage.reasoning.inline",
                    "推理 {0}",
                    UsageLedger.FormatTokens(summary.ReasoningTokens)));
            }

            sb.Append(" | ");
            sb.Append(Loc.Tr("contextUsage.cost.inline", "费用 {0}", costText));
            _llmUsageValueLabel.text = sb.ToString();

            _lastCallValueLabel.text = FormatTopModels(summary.Models, maxCount: 3);
            _headerUsageLabel.text = Loc.Tr(
                "contextUsage.period.header",
                "{0} · {1}",
                windowLabel,
                UsageLedger.FormatWindowSummary(window));
        }

        private static string FormatTopModels(IReadOnlyList<UsageModelBreakdown> models, int maxCount)
        {
            if (models == null || models.Count == 0)
                return "-";

            var count = Math.Min(models.Count, maxCount);
            var parts = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                var m = models[i];
                if (m == null) continue;
                var costPart = m.CostUsd > 0 ? $" · {FormatCostUsd(m.CostUsd, m.CostPartial)}" : string.Empty;
                parts.Add($"{m.Model} ({m.CallCount} · {UsageLedger.FormatTokens(m.TotalTokens)}{costPart})");
            }

            return parts.Count > 0 ? string.Join(", ", parts) : "-";
        }

        /// <summary>
        /// v1.9.0+: 抽出的实际渲染方法, 用 _currentBudget 重绘所有本地化文本 + 数值.
        /// UpdateDisplay 和 OnLanguageChanged 共用.
        /// </summary>
        private void RenderCurrent()
        {
            // ContextBudgetInfo 是 struct (value type), 未初始化时字段全 0, 直接用即可
            var budget = _currentBudget;

            // Update progress bar
            var percentage = Mathf.Clamp01(budget.UsagePercentage);
            _progressFill.style.width = Length.Percent(percentage * 100f);

            // Color coding based on usage
            // v1.7.x: 统一引用 AgentCoreColors 单一真源，消除此前硬编码 Color(0.2,0.8,0.3) 等
            // 与 USS/ToolCallCard 语义色不一致的问题（同一"成功绿"曾有三个不同值）。
            if (percentage < 0.5f)
            {
                _progressFill.style.backgroundColor = AgentCore.Editor.UI.AgentCoreColors.Success; // 绿
            }
            else if (percentage < 0.7f)
            {
                _progressFill.style.backgroundColor = AgentCore.Editor.UI.AgentCoreColors.Warning; // 黄
            }
            else if (percentage < 0.9f)
            {
                _progressFill.style.backgroundColor = AgentCore.Editor.UI.AgentCoreColors.Orange; // 橙
            }
            else
            {
                _progressFill.style.backgroundColor = AgentCore.Editor.UI.AgentCoreColors.Danger; // 红
            }

            // Usage label — 数值使用 CultureInfo.Invariant, 模板本身可本地化
            _usageLabel.text = Loc.Tr(
                "contextUsage.usage",
                "{0}% ({1} / {2} tokens)",
                $"{percentage * 100f:F1}",
                $"{budget.CurrentTokens:N0}",
                $"{budget.AvailableTokens:N0}");

            // Token allocation
            _tokenAllocationLabel.text = Loc.Tr("contextUsage.tokenAllocation.label", "Token 分配");
            _tokenCountLabel.text = Loc.Tr(
                "contextUsage.tokenAllocation.value",
                "最大: {0} | 预留: {1} | 可用: {2}",
                $"{budget.MaxTokens:N0}",
                $"{budget.ReservedTokens:N0}",
                $"{budget.AvailableTokens:N0}");

            // Compression stats
            _compressionRowLabel.text = Loc.Tr("contextUsage.compression.label", "压缩统计");
            if (budget.ToolResultCompressions > 0 || budget.ConversationCompressions > 0)
            {
                var ratio = budget.CompressionRatio > 0 ? $"{budget.CompressionRatio * 100f:F1}%" : "0%";
                _compressionStatsLabel.text = Loc.Tr(
                    "contextUsage.compression.value",
                    "工具: {0} | 对话: {1} | 节省: {2} tokens | 压缩率: {3}",
                    budget.ToolResultCompressions,
                    budget.ConversationCompressions,
                    $"{budget.TokensSaved:N0}",
                    ratio);
            }
            else
            {
                _compressionStatsLabel.text = Loc.Tr("contextUsage.compression.none", "暂无压缩");
            }

            // Compression badge
            _compressionBadgeLabel.text = Loc.Tr("contextUsage.compression.active", "压缩已激活");
            _compressionBadge.style.display = budget.IsCompressionActive ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void ToggleCollapse()
        {
            SetCollapsed(!_isCollapsed);
        }

        /// <summary>
        /// v1.9.0+: 拆分 label / value 两个 Label 引用返回, 便于后续单独刷新本地化.
        /// </summary>
        private VisualElement CreateStatsRow(out Label labelRef, out Label valueRef)
        {
            var row = new VisualElement();
            row.AddToClassList(StatsRowClassName);

            labelRef = new Label();
            labelRef.AddToClassList(StatLabelClassName);
            row.Add(labelRef);

            valueRef = new Label();
            valueRef.AddToClassList(StatValueClassName);
            row.Add(valueRef);

            return row;
        }
        #endregion
    }
}
