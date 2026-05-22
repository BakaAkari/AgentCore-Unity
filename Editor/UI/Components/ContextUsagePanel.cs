using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Components
{
    /// <summary>
    /// Context usage visualization panel.
    /// Displays token usage, compression statistics, and budget allocation.
    /// </summary>
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
        private readonly Button _toggleButton;
        private readonly VisualElement _content;
        private readonly VisualElement _progressBar;
        private readonly VisualElement _progressFill;
        private readonly Label _usageLabel;
        private readonly Label _tokenCountLabel;
        private readonly Label _compressionStatsLabel;
        private readonly VisualElement _compressionBadge;
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

            // Header
            _header = new VisualElement();
            _header.AddToClassList(HeaderClassName);

            _headerLabel = new Label("上下文使用情况");
            _header.Add(_headerLabel);

            _toggleButton = new Button(ToggleCollapse) { text = "v" };
            _header.Add(_toggleButton);

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
            _usageLabel = new Label("0% (0 / 0 tokens)");
            _usageLabel.AddToClassList(StatLabelClassName);
            _content.Add(_usageLabel);

            // Token count details
            var tokenRow = CreateStatsRow("Token 分配", "");
            _tokenCountLabel = tokenRow.Q<Label>(className: StatValueClassName);
            _content.Add(tokenRow);

            // Compression stats
            var compressionRow = CreateStatsRow("压缩统计", "");
            _compressionStatsLabel = compressionRow.Q<Label>(className: StatValueClassName);
            _content.Add(compressionRow);

            // Compression badge
            _compressionBadge = new VisualElement();
            _compressionBadge.AddToClassList(CompressionBadgeClassName);
            var badgeLabel = new Label("压缩已激活");
            _compressionBadge.Add(badgeLabel);
            _compressionBadge.style.display = DisplayStyle.None;
            _content.Add(_compressionBadge);

            Add(_content);

            // Initialize with empty data
            UpdateDisplay(new Core.ContextBudgetInfo());
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Updates the panel with new budget information.
        /// </summary>
        public void UpdateDisplay(Core.ContextBudgetInfo budget)
        {
            _currentBudget = budget;

            // Update progress bar
            var percentage = Mathf.Clamp01(budget.UsagePercentage);
            _progressFill.style.width = Length.Percent(percentage * 100f);

            // Color coding based on usage
            if (percentage < 0.5f)
            {
                _progressFill.style.backgroundColor = new Color(0.2f, 0.8f, 0.3f); // Green
            }
            else if (percentage < 0.7f)
            {
                _progressFill.style.backgroundColor = new Color(1f, 0.8f, 0f); // Yellow
            }
            else if (percentage < 0.9f)
            {
                _progressFill.style.backgroundColor = new Color(1f, 0.5f, 0f); // Orange
            }
            else
            {
                _progressFill.style.backgroundColor = new Color(1f, 0.2f, 0.2f); // Red
            }

            // Update usage label
            _usageLabel.text = $"{percentage * 100f:F1}% ({budget.CurrentTokens:N0} / {budget.AvailableTokens:N0} tokens)";

            // Update token count details
            _tokenCountLabel.text = $"最大: {budget.MaxTokens:N0} | 预留: {budget.ReservedTokens:N0} | 可用: {budget.AvailableTokens:N0}";

            // Update compression stats
            if (budget.ToolResultCompressions > 0 || budget.ConversationCompressions > 0)
            {
                var ratio = budget.CompressionRatio > 0 ? $"{budget.CompressionRatio * 100f:F1}%" : "0%";
                _compressionStatsLabel.text = $"工具: {budget.ToolResultCompressions} | 对话: {budget.ConversationCompressions} | 节省: {budget.TokensSaved:N0} tokens | 压缩率: {ratio}";
            }
            else
            {
                _compressionStatsLabel.text = "暂无压缩";
            }

            // Show/hide compression badge
            _compressionBadge.style.display = budget.IsCompressionActive ? DisplayStyle.Flex : DisplayStyle.None;
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
                _toggleButton.text = ">";
                _content.style.display = DisplayStyle.None;
            }
            else
            {
                RemoveFromClassList(CollapsedClassName);
                _toggleButton.text = "v";
                _content.style.display = DisplayStyle.Flex;
            }
        }
        #endregion

        #region Private Methods
        private void ToggleCollapse()
        {
            SetCollapsed(!_isCollapsed);
        }

        private VisualElement CreateStatsRow(string label, string value)
        {
            var row = new VisualElement();
            row.AddToClassList(StatsRowClassName);

            var labelElement = new Label(label);
            labelElement.AddToClassList(StatLabelClassName);
            row.Add(labelElement);

            var valueElement = new Label(value);
            valueElement.AddToClassList(StatValueClassName);
            row.Add(valueElement);

            return row;
        }
        #endregion
    }
}
