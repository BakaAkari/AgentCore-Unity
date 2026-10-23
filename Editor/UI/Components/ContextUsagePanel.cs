using AgentCore.Editor.L10n;
using AgentCore.Editor.LLM;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Components
{
    /// <summary>
    /// Context usage visualization panel.
    /// Displays token usage, compression statistics, and budget allocation.
    /// </summary>
    /// <remarks>
    /// v1.9.0+: 所有可见文本走 L10n. 订阅 <see cref="LanguageManager.LanguageChanged"/>,
    /// 语言切换时用最近一次的 budget 重绘 (标签 + 数值一起刷新).
    /// </remarks>
    public class ContextUsagePanel : VisualElement
    {
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
        private readonly Label _llmUsageRowLabel;
        private readonly Label _llmUsageValueLabel;
        private readonly Label _lastCallRowLabel;
        private readonly Label _lastCallValueLabel;
        private bool _usageRefreshQueued;
        #endregion

        #region State
        private bool _isCollapsed = false;
        private Core.ContextBudgetInfo _currentBudget;
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
        {
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

            // v1.17.0: LLM 实际用量（来自供应商 usage 字段：缓存命中 / 写入 / 输出 / 估算费用）
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

            // Initialize with empty data
            UpdateDisplay(new Core.ContextBudgetInfo());

            // 默认折叠（v1.6.4：占用聊天区空间过大，用户主动展开时再显示详情）
            SetCollapsed(true);

            // v1.9.0+: 订阅语言事件, 切语言时用最近 budget 重绘
            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                LanguageManager.LanguageChanged += OnLanguageChanged;
                UsageLedger.OnUsageRecorded += OnUsageRecorded;
                RenderUsage();
            });
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                LanguageManager.LanguageChanged -= OnLanguageChanged;
                UsageLedger.OnUsageRecorded -= OnUsageRecorded;
            });
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
        /// Collapses or expands the panel.
        /// </summary>
        public void SetCollapsed(bool collapsed)
        {
            _isCollapsed = collapsed;
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
        #endregion

        #region Private Methods

        private void OnLanguageChanged(string _)
        {
            _headerLabel.text = Loc.Tr("contextUsage.header", "上下文使用情况");
            RenderCurrent();
            RenderUsage();
        }

        /// <summary>
        /// v1.17.0: UsageLedger 事件可能来自后台线程（HTTP 回调），合并后切回主线程刷新。
        /// </summary>
        private void OnUsageRecorded(LlmUsage _)
        {
            if (_usageRefreshQueued) return;
            _usageRefreshQueued = true;
            EditorApplication.delayCall += () =>
            {
                _usageRefreshQueued = false;
                RenderUsage();
            };
        }

        /// <summary>v1.17.0: 渲染会话累计用量与最近一次调用。</summary>
        private void RenderUsage()
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

            string costText = cost > 0 ? $"≈${cost:F2}{(partial ? "+" : "")}" : (partial ? Loc.Tr("contextUsage.cost.notAvailable", "未知") : "$0");
            _llmUsageValueLabel.text = Loc.Tr(
                "contextUsage.llmUsage.cacheStatusValue",
                "{0} 次 | 输入 {1}（缓存命中 {2}）| 输出 {3} | 费用 {4}",
                session.Calls,
                UsageLedger.FormatTokens(session.TotalInput),
                session.CacheStatus == "unknown" ? Loc.Tr("contextUsage.cache.unknown", "未知") : session.CacheStatus,
                UsageLedger.FormatTokens(session.Output),
                costText);

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

            _headerUsageLabel.text = Loc.Tr("contextUsage.cache.header", "缓存 {0} · {1}", session.CacheStatus == "unknown" ? Loc.Tr("contextUsage.cache.unknown", "未知") : session.CacheStatus, costText);
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
