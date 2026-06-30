using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Components
{
    /// <summary>
    /// 工具调用分组容器。
    /// <para>
    /// 将一次 Agent 交互中的所有工具调用卡片统一放入一个可折叠的容器中，
    /// 默认折叠状态只显示一行摘要（轮次信息 + 成功/失败统计），
    /// 点击展开后显示所有 ToolCallCard 和轮次分隔线。
    /// </para>
    /// <para>
    /// 自动折叠策略：
    /// - 有工具正在执行时：自动展开
    /// - 所有工具执行完成后：自动折叠
    /// - 用户手动切换后：不再自动改变
    /// </para>
    /// </summary>
    public class ToolCallGroup : VisualElement
    {
        #region 常量

        // 颜色常量
        private static readonly Color HeaderBg = new Color(0.20f, 0.20f, 0.20f);        // #333333
        private static readonly Color HeaderBgHover = new Color(0.24f, 0.24f, 0.24f);   // #3D3D3D
        private static readonly Color ContentBg = new Color(0.16f, 0.16f, 0.16f);       // #292929
        private static readonly Color TextPrimary = new Color(0.83f, 0.83f, 0.83f);     // #D4D4D4
        private static readonly Color TextSecondary = new Color(0.53f, 0.53f, 0.53f);   // #888888
        private static readonly Color AccentBlue = new Color(0.29f, 0.57f, 0.85f);      // #4A90D9
        private static readonly Color AccentGreen = new Color(0.30f, 0.69f, 0.31f);     // #4CAF50
        private static readonly Color AccentRed = new Color(0.96f, 0.26f, 0.21f);       // #F44336
        private static readonly Color AccentOrange = new Color(0.95f, 0.61f, 0.07f);    // #F29C12
        private static readonly Color BorderColor = new Color(0.22f, 0.22f, 0.22f);     // #383838

        // 折叠/展开箭头（纯 ASCII，避免 Unity 字体缺字）
        private const string ArrowCollapsed = ">";
        private const string ArrowExpanded = "v";

        #endregion

        #region UI 元素

        private readonly VisualElement _header;
        private readonly Label _arrowLabel;
        private readonly Label _titleLabel;
        private readonly Label _summaryLabel;
        private readonly VisualElement _content;

        #endregion

        #region 状态

        private bool _isExpanded;
        private bool _userToggled;

        // 统计信息
        private int _totalCalls;
        private int _completedCalls;
        private int _failedCalls;
        private int _runningCalls;
        private int _currentRound;
        private int _maxRounds;

        // 内部卡片列表（用于统计）
        private readonly List<ToolCallCard> _cards = new List<ToolCallCard>();

        #endregion

        #region 构造函数

        /// <summary>
        /// 创建工具调用分组容器。
        /// </summary>
        public ToolCallGroup()
        {
            _isExpanded = false;
            _userToggled = false;
            _currentRound = 1;
            _maxRounds = 1;

            // === 根容器样式 ===
            AddToClassList("tool-call-group");
            style.flexDirection = FlexDirection.Column;
            style.marginLeft = 8;
            style.marginRight = 8;
            style.marginTop = 4;
            style.marginBottom = 4;
            style.borderTopLeftRadius = 4;
            style.borderTopRightRadius = 4;
            style.borderBottomLeftRadius = 4;
            style.borderBottomRightRadius = 4;
            style.borderTopWidth = 1;
            style.borderBottomWidth = 1;
            style.borderLeftWidth = 1;
            style.borderRightWidth = 1;
            style.borderTopColor = BorderColor;
            style.borderBottomColor = BorderColor;
            style.borderLeftColor = BorderColor;
            style.borderRightColor = BorderColor;
            style.overflow = Overflow.Hidden;

            // === 头部（可点击，显示摘要） ===
            _header = new VisualElement();
            _header.AddToClassList("tool-call-group__header");
            _header.style.flexDirection = FlexDirection.Row;
            _header.style.alignItems = Align.Center;
            _header.style.backgroundColor = HeaderBg;
            _header.style.paddingLeft = 10;
            _header.style.paddingRight = 10;
            _header.style.paddingTop = 6;
            _header.style.paddingBottom = 6;
            _header.style.minHeight = 28;

            // 折叠箭头
            _arrowLabel = new Label(ArrowCollapsed);
            _arrowLabel.AddToClassList("tool-call-group__arrow");
            _arrowLabel.style.fontSize = 10;
            _arrowLabel.style.color = TextSecondary;
            _arrowLabel.style.marginRight = 6;
            _arrowLabel.style.minWidth = 14;
            _arrowLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _arrowLabel.style.flexShrink = 0;
            _header.Add(_arrowLabel);

            // 标题文本
            _titleLabel = new Label("工具执行过程");
            _titleLabel.AddToClassList("tool-call-group__title");
            _titleLabel.style.fontSize = 12;
            _titleLabel.style.color = TextPrimary;
            _titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _titleLabel.style.marginRight = 8;
            _titleLabel.style.flexShrink = 0;
            _header.Add(_titleLabel);

            // 摘要标签（轮次 + 统计）
            _summaryLabel = new Label("");
            _summaryLabel.AddToClassList("tool-call-group__summary");
            _summaryLabel.style.fontSize = 11;
            _summaryLabel.style.color = TextSecondary;
            _summaryLabel.style.flexGrow = 1;
            _summaryLabel.style.overflow = Overflow.Hidden;
            _summaryLabel.style.textOverflow = TextOverflow.Ellipsis;
            _header.Add(_summaryLabel);

            // 头部悬停效果
            _header.RegisterCallback<MouseEnterEvent>(_ =>
            {
                _header.style.backgroundColor = HeaderBgHover;
            });
            _header.RegisterCallback<MouseLeaveEvent>(_ =>
            {
                _header.style.backgroundColor = HeaderBg;
            });

            // 头部点击事件
            _header.RegisterCallback<ClickEvent>(OnHeaderClicked);

            Add(_header);

            // === 内容区域（包含所有 ToolCallCard 和轮次分隔线） ===
            _content = new VisualElement();
            _content.AddToClassList("tool-call-group__content");
            _content.style.flexDirection = FlexDirection.Column;
            _content.style.backgroundColor = ContentBg;
            _content.style.paddingTop = 2;
            _content.style.paddingBottom = 2;
            _content.style.display = DisplayStyle.None; // 默认折叠

            Add(_content);

            // 初始化摘要
            UpdateSummaryText();
        }

        #endregion

        #region 公开方法

        /// <summary>
        /// 添加工具调用卡片到分组容器。
        /// </summary>
        /// <param name="card">工具调用卡片</param>
        public void AddToolCard(ToolCallCard card)
        {
            if (card == null) return;

            _cards.Add(card);
            _content.Add(card);
            _totalCalls++;

            // 根据卡片当前状态更新统计
            UpdateStatsFromCard(card);
            UpdateSummaryText();
            UpdateHeaderAccentColor();

            // 有新工具调用时自动展开（除非用户手动折叠过）
            if (!_userToggled && card.Status == ToolCallStatus.Running)
            {
                SetExpanded(true);
            }
        }

        /// <summary>
        /// 添加轮次分隔线到分组容器。
        /// </summary>
        /// <param name="separator">分隔线 VisualElement</param>
        public void AddSeparator(VisualElement separator)
        {
            _content.Add(separator);
        }

        /// <summary>
        /// 更新轮次信息。
        /// </summary>
        /// <param name="currentRound">当前轮次</param>
        /// <param name="maxRounds">最大轮次</param>
        public void UpdateRoundInfo(int currentRound, int maxRounds)
        {
            _currentRound = currentRound;
            _maxRounds = maxRounds;
            UpdateSummaryText();
        }

        /// <summary>
        /// 通知某个工具调用状态已变更，更新统计信息。
        /// </summary>
        public void NotifyToolStatusChanged()
        {
            RecalculateStats();
            UpdateSummaryText();
            UpdateHeaderAccentColor();

            // 自动折叠/展开逻辑（仅在用户未手动切换时生效）
            if (!_userToggled)
            {
                if (_runningCalls > 0)
                {
                    // 有工具正在执行时展开
                    SetExpanded(true);
                }
                else if (_totalCalls > 0 && _runningCalls == 0)
                {
                    // 所有工具执行完成后折叠
                    SetExpanded(false);
                }
            }
        }

        /// <summary>
        /// 强制设置展开/折叠状态（不影响 _userToggled 标记）。
        /// 用于会话恢复等场景。
        /// </summary>
        /// <param name="expanded">是否展开</param>
        public void ForceSetExpanded(bool expanded)
        {
            SetExpanded(expanded);
        }

        #endregion

        #region 私有方法

        /// <summary>
        /// 头部点击事件处理：切换展开/折叠。
        /// </summary>
        private void OnHeaderClicked(ClickEvent evt)
        {
            _userToggled = true;
            SetExpanded(!_isExpanded);
            evt.StopPropagation();
        }

        /// <summary>
        /// 设置展开/折叠状态并更新 UI。
        /// </summary>
        /// <param name="expanded">是否展开</param>
        private void SetExpanded(bool expanded)
        {
            // 避免重复设置相同状态导致的视觉抖动
            if (_isExpanded == expanded) return;
            
            _isExpanded = expanded;
            _content.style.display = _isExpanded ? DisplayStyle.Flex : DisplayStyle.None;
            _arrowLabel.text = _isExpanded ? ArrowExpanded : ArrowCollapsed;
        }

        /// <summary>
        /// 从单个卡片状态更新统计（增量更新，用于新增卡片时）。
        /// </summary>
        private void UpdateStatsFromCard(ToolCallCard card)
        {
            switch (card.Status)
            {
                case ToolCallStatus.Running:
                    _runningCalls++;
                    break;
                case ToolCallStatus.Completed:
                    _completedCalls++;
                    break;
                case ToolCallStatus.Failed:
                    _failedCalls++;
                    break;
            }
        }

        /// <summary>
        /// 重新计算所有统计信息（全量重算，用于状态变更通知时）。
        /// </summary>
        private void RecalculateStats()
        {
            _totalCalls = _cards.Count;
            _completedCalls = 0;
            _failedCalls = 0;
            _runningCalls = 0;

            foreach (var card in _cards)
            {
                switch (card.Status)
                {
                    case ToolCallStatus.Running:
                        _runningCalls++;
                        break;
                    case ToolCallStatus.Completed:
                        _completedCalls++;
                        break;
                    case ToolCallStatus.Failed:
                        _failedCalls++;
                        break;
                }
            }
        }

        /// <summary>
        /// 更新摘要文本。
        /// 格式：[第 2/25 轮 | 3 个调用: 2 成功, 1 执行中]
        /// </summary>
        private void UpdateSummaryText()
        {
            var parts = new List<string>();

            // 轮次信息
            if (_maxRounds > 1 || _currentRound > 1)
            {
                parts.Add($"第 {_currentRound}/{_maxRounds} 轮");
            }

            // 调用统计
            if (_totalCalls > 0)
            {
                var statParts = new List<string>();

                if (_completedCalls > 0)
                    statParts.Add($"{_completedCalls} 成功");

                if (_failedCalls > 0)
                    statParts.Add($"{_failedCalls} 失败");

                if (_runningCalls > 0)
                    statParts.Add($"{_runningCalls} 执行中");

                var pending = _totalCalls - _completedCalls - _failedCalls - _runningCalls;
                if (pending > 0)
                    statParts.Add($"{pending} 等待");

                parts.Add($"{_totalCalls} 个调用: {string.Join(", ", statParts)}");
            }

            _summaryLabel.text = parts.Count > 0
                ? $"[{string.Join(" | ", parts)}]"
                : "";
        }

        /// <summary>
        /// 根据当前状态更新头部左侧的强调色边框。
        /// </summary>
        private void UpdateHeaderAccentColor()
        {
            Color accentColor;

            if (_runningCalls > 0)
            {
                accentColor = AccentBlue; // 执行中 - 蓝色
            }
            else if (_failedCalls > 0)
            {
                accentColor = AccentRed; // 有失败 - 红色
            }
            else if (_completedCalls > 0 && _completedCalls == _totalCalls)
            {
                accentColor = AccentGreen; // 全部成功 - 绿色
            }
            else
            {
                accentColor = AccentBlue; // 默认 - 蓝色
            }

            style.borderLeftWidth = 3;
            style.borderLeftColor = accentColor;
        }

        #endregion
    }
}
