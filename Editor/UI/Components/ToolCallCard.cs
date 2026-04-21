using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Components
{
    /// <summary>
    /// 工具调用状态枚举。
    /// </summary>
    public enum ToolCallStatus
    {
        /// <summary>等待执行</summary>
        Pending,

        /// <summary>正在执行</summary>
        Running,

        /// <summary>执行完成</summary>
        Completed,

        /// <summary>执行失败</summary>
        Failed
    }

    /// <summary>
    /// 工具调用卡片组件。
    /// <para>
    /// 轻量级 VisualElement，在聊天窗口中显示单个工具调用的状态。
    /// 支持 Pending/Running/Completed/Failed 四种状态，可展开/折叠查看详情。
    /// </para>
    /// <para>
    /// 纯代码构建 UI，不依赖外部 UXML/USS 文件。
    /// </para>
    /// </summary>
    public class ToolCallCard : VisualElement
    {
        #region 常量

        // 颜色常量
        private static readonly Color BackgroundColor = new Color(0.176f, 0.176f, 0.176f); // #2D2D2D
        private static readonly Color BorderBlue = new Color(0.290f, 0.565f, 0.851f);      // #4A90D9
        private static readonly Color BorderGreen = new Color(0.298f, 0.686f, 0.314f);     // #4CAF50
        private static readonly Color BorderRed = new Color(0.957f, 0.263f, 0.212f);       // #F44336
        private static readonly Color TextPrimary = new Color(0.831f, 0.831f, 0.831f);     // #D4D4D4
        private static readonly Color TextSecondary = new Color(0.533f, 0.533f, 0.533f);   // #888888
        private static readonly Color DetailsBg = new Color(0.153f, 0.153f, 0.153f);       // #272727

        // 状态图标（纯文本字符，不使用 emoji）
        private const string IconPending = "[.]";
        private const string IconRunning = "[>]";
        private const string IconCompleted = "[v]";
        private const string IconFailed = "[x]";

        #endregion

        #region UI 元素

        private readonly Label _statusIcon;
        private readonly Label _toolNameLabel;
        private readonly Label _statusLabel;
        private readonly VisualElement _detailsContainer;
        private readonly Label _detailsLabel;
        private bool _isExpanded;

        #endregion

        #region 公开属性

        /// <summary>工具名称</summary>
        public string ToolName { get; private set; }

        /// <summary>当前状态</summary>
        public ToolCallStatus Status { get; private set; }

        #endregion

        #region 构造函数

        /// <summary>
        /// 创建工具调用卡片。
        /// </summary>
        /// <param name="toolName">工具名称</param>
        /// <param name="arguments">工具参数（JSON string，可选）</param>
        public ToolCallCard(string toolName, string arguments = null)
        {
            ToolName = toolName ?? "unknown";
            Status = ToolCallStatus.Pending;
            _isExpanded = false;

            // === 卡片根容器样式 ===
            style.flexDirection = FlexDirection.Column;
            style.marginLeft = 12;
            style.marginRight = 12;
            style.marginTop = 2;
            style.marginBottom = 2;
            style.paddingLeft = 8;
            style.paddingRight = 8;
            style.paddingTop = 4;
            style.paddingBottom = 4;
            style.backgroundColor = BackgroundColor;
            style.borderLeftWidth = 2;
            style.borderRightWidth = 0;
            style.borderTopWidth = 0;
            style.borderBottomWidth = 0;
            style.borderLeftColor = BorderBlue;
            style.borderTopLeftRadius = 3;
            style.borderBottomLeftRadius = 3;
            style.borderTopRightRadius = 3;
            style.borderBottomRightRadius = 3;

            // === 头部行（图标 + 工具名 + 状态文本）===
            var headerRow = new VisualElement();
            headerRow.style.flexDirection = FlexDirection.Row;
            headerRow.style.alignItems = Align.Center;
            headerRow.style.minHeight = 20;

            // 状态图标
            _statusIcon = new Label(IconPending);
            _statusIcon.style.fontSize = 11;
            _statusIcon.style.color = TextSecondary;
            _statusIcon.style.marginRight = 6;
            _statusIcon.style.minWidth = 24;
            _statusIcon.style.unityTextAlign = TextAnchor.MiddleLeft;
            headerRow.Add(_statusIcon);

            // 工具名称
            _toolNameLabel = new Label(ToolName);
            _toolNameLabel.style.fontSize = 12;
            _toolNameLabel.style.color = TextPrimary;
            _toolNameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _toolNameLabel.style.flexGrow = 1;
            _toolNameLabel.style.overflow = Overflow.Hidden;
            _toolNameLabel.style.textOverflow = TextOverflow.Ellipsis;
            headerRow.Add(_toolNameLabel);

            // 状态文本
            _statusLabel = new Label("等待中...");
            _statusLabel.style.fontSize = 11;
            _statusLabel.style.color = TextSecondary;
            _statusLabel.style.marginLeft = 8;
            _statusLabel.style.flexShrink = 0;
            headerRow.Add(_statusLabel);

            Add(headerRow);

            // === 详情区域（默认隐藏）===
            _detailsContainer = new VisualElement();
            _detailsContainer.style.display = DisplayStyle.None;
            _detailsContainer.style.marginTop = 4;
            _detailsContainer.style.paddingLeft = 30; // 与工具名对齐（图标宽度 + margin）
            _detailsContainer.style.paddingRight = 4;
            _detailsContainer.style.paddingTop = 4;
            _detailsContainer.style.paddingBottom = 4;
            _detailsContainer.style.backgroundColor = DetailsBg;
            _detailsContainer.style.borderTopLeftRadius = 2;
            _detailsContainer.style.borderBottomLeftRadius = 2;
            _detailsContainer.style.borderTopRightRadius = 2;
            _detailsContainer.style.borderBottomRightRadius = 2;

            _detailsLabel = new Label("");
            _detailsLabel.style.fontSize = 11;
            _detailsLabel.style.color = TextSecondary;
            _detailsLabel.style.whiteSpace = WhiteSpace.Normal;
            _detailsLabel.style.overflow = Overflow.Hidden;
            _detailsLabel.style.maxHeight = 120;
            _detailsContainer.Add(_detailsLabel);

            Add(_detailsContainer);

            // 如果有参数，预设详情内容
            if (!string.IsNullOrEmpty(arguments))
            {
                var truncatedArgs = arguments.Length > 200
                    ? arguments.Substring(0, 200) + "..."
                    : arguments;
                _detailsLabel.text = "参数: " + truncatedArgs;
            }

            // === 点击事件：展开/折叠详情 ===
            RegisterCallback<ClickEvent>(OnCardClicked);
        }

        #endregion

        #region 公开方法

        /// <summary>
        /// 设置工具调用状态。
        /// </summary>
        /// <param name="status">新状态</param>
        /// <param name="statusText">状态文本（可选，如 "执行中..."、"完成 (1.2s)"）</param>
        public void SetStatus(ToolCallStatus status, string statusText = null)
        {
            Status = status;

            // 更新状态文本
            if (!string.IsNullOrEmpty(statusText))
            {
                _statusLabel.text = statusText;
            }

            UpdateStatusVisuals();
        }

        /// <summary>
        /// 设置详情内容（参数/结果/错误信息）。
        /// </summary>
        /// <param name="details">详情文本</param>
        public void SetDetails(string details)
        {
            if (string.IsNullOrEmpty(details)) return;

            _detailsLabel.text = details;

            // 失败状态自动展开详情
            if (Status == ToolCallStatus.Failed && !_isExpanded)
            {
                ToggleDetails();
            }
        }

        #endregion

        #region 私有方法

        /// <summary>
        /// 根据当前状态更新视觉样式（图标、颜色、边框）。
        /// </summary>
        private void UpdateStatusVisuals()
        {
            switch (Status)
            {
                case ToolCallStatus.Pending:
                    _statusIcon.text = IconPending;
                    _statusIcon.style.color = TextSecondary;
                    style.borderLeftColor = BorderBlue;
                    if (string.IsNullOrEmpty(_statusLabel.text) || _statusLabel.text == "等待中...")
                    {
                        _statusLabel.text = "等待中...";
                    }
                    break;

                case ToolCallStatus.Running:
                    _statusIcon.text = IconRunning;
                    _statusIcon.style.color = BorderBlue;
                    style.borderLeftColor = BorderBlue;
                    if (string.IsNullOrEmpty(_statusLabel.text))
                    {
                        _statusLabel.text = "执行中...";
                    }
                    break;

                case ToolCallStatus.Completed:
                    _statusIcon.text = IconCompleted;
                    _statusIcon.style.color = BorderGreen;
                    style.borderLeftColor = BorderGreen;
                    if (string.IsNullOrEmpty(_statusLabel.text))
                    {
                        _statusLabel.text = "完成";
                    }
                    break;

                case ToolCallStatus.Failed:
                    _statusIcon.text = IconFailed;
                    _statusIcon.style.color = BorderRed;
                    style.borderLeftColor = BorderRed;
                    if (string.IsNullOrEmpty(_statusLabel.text))
                    {
                        _statusLabel.text = "失败";
                    }
                    break;
            }
        }

        /// <summary>
        /// 切换详情区域的展开/折叠状态。
        /// </summary>
        private void ToggleDetails()
        {
            _isExpanded = !_isExpanded;
            _detailsContainer.style.display = _isExpanded ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>
        /// 卡片点击事件处理：展开/折叠详情。
        /// </summary>
        private void OnCardClicked(ClickEvent evt)
        {
            // 仅在有详情内容时才允许展开
            if (!string.IsNullOrEmpty(_detailsLabel.text))
            {
                ToggleDetails();
            }
            evt.StopPropagation();
        }

        #endregion
    }
}
