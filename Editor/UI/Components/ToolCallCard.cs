using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using AgentCore.Editor.Utils;

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
    /// 折叠策略：
    /// - 已完成（成功）的工具调用：默认折叠，只显示工具名 + 状态 + 耗时
    /// - 失败的工具调用：默认展开，显示错误信息
    /// - 正在执行的工具调用：展开显示
    /// - 等待中的工具调用：折叠显示
    /// - 用户可以点击切换折叠/展开状态
    /// </para>
    /// <para>
    /// v1.4.8 UX 增强：
    /// - 详情区域改用只读 <see cref="TextField"/>（multiline + isReadOnly），支持文本选择和 Ctrl+C 复制
    /// - 详情容器包裹在 <see cref="ScrollView"/> 中，避免 maxHeight 裁剪导致内容不可见
    /// - 头部新增"复制"按钮，一键复制完整原始内容到系统剪贴板
    /// - 移除 200 字符截断——保留完整参数 / 结果 / 错误信息，方便用户诊断
    /// </para>
    /// <para>
    /// 纯代码构建 UI，不依赖外部 UXML/USS 文件。
    /// </para>
    /// </summary>
    public class ToolCallCard : VisualElement
    {
        #region 常量

        // 颜色常量
        // v1.7.x: 语义色（蓝/绿/红）统一引用 AgentCoreColors 单一真源，
        // 消除此前 ToolCallCard 自带的 #4A90D9/#4CAF50/#F44336 与 USS(#4a86c8/#5cb85c/#d9534f) 不一致问题。
        private static readonly Color BackgroundColor = UI.AgentCoreColors.CardBackground;   // #2D2D2D
        private static readonly Color BorderBlue = UI.AgentCoreColors.Accent;                // #4a86c8（统一）
        private static readonly Color BorderGreen = UI.AgentCoreColors.Success;              // #5cb85c（统一）
        private static readonly Color BorderRed = UI.AgentCoreColors.Danger;                 // #d9534f（统一）
        private static readonly Color TextPrimary = UI.AgentCoreColors.TextPrimary;          // #D4D4D4
        private static readonly Color TextSecondary = UI.AgentCoreColors.TextSecondary;      // #888888
        private static readonly Color DetailsBg = UI.AgentCoreColors.DetailBackground;       // #272727
        private static readonly Color ToggleArrowColor = new Color(0.45f, 0.45f, 0.45f);   // #737373
        private static readonly Color CopyButtonBg = new Color(0.24f, 0.24f, 0.24f);       // #3D3D3D
        private static readonly Color CopyButtonBgHover = new Color(0.30f, 0.30f, 0.30f);  // #4D4D4D
        private static readonly Color CopyButtonBgFlash = UI.AgentCoreColors.Success; // green flash（与 BorderGreen 一致）

        // 状态图标（纯文本字符，不使用 emoji）
        private const string IconPending = "[.]";
        private const string IconRunning = "[>]";
        private const string IconCompleted = "[v]";
        private const string IconFailed = "[x]";

        // 折叠/展开箭头指示器（纯 ASCII，避免 Unity 字体缺字）
        private const string ArrowCollapsed = ">";
        private const string ArrowExpanded = "v";

        // 详情区域最大高度（超过时出滚动条而不是裁剪）
        // v1.4.8：从"maxHeight+Hidden 裁剪"改为"maxHeight+ScrollView"。
        // 值保守选择 240px：一般 6~8 行可见，足以直观查看常见结果；超出则滚动。
        private const float DetailsMaxHeight = 240f;

        // v1.7.x 性能：TextField 显示内容的字符上限。
        // 只读 multiline TextField 会为全部文本构建文本网格，非虚拟化控件；
        // 读大文件 / 大 JSON 等工具结果可达数万字符，全量塞入会在展开时造成明显卡顿。
        // 策略：显示截断到此上限并追加提示，完整原文始终保留在 _detailsRaw 供"复制"按钮取用。
        private const int DetailsDisplayLimit = 8000;

        #endregion

        #region UI 元素

        private readonly Label _statusIcon;
        private readonly Label _toolNameLabel;
        private readonly Label _statusLabel;
        private readonly Button _copyButton;
        private readonly Label _toggleArrow;
        private readonly VisualElement _detailsContainer;
        private readonly ScrollView _detailsScroll;
        private readonly TextField _detailsField;
        private bool _isExpanded;

        /// <summary>是否由用户手动切换过折叠状态（手动切换后不再自动改变）</summary>
        private bool _userToggled;

        /// <summary>
        /// 完整的详情原始文本（未做任何截断），供"复制"按钮读取。
        /// 与 <c>_detailsField.value</c> 保持一致，但显式独立字段更清晰。
        /// </summary>
        private string _detailsRaw = string.Empty;

        #endregion

        #region 公开属性

        /// <summary>工具名称</summary>
        public string ToolName { get; private set; }

        /// <summary>当前状态</summary>
        public ToolCallStatus Status { get; private set; }

        /// <summary>
        /// 完整的详情原始文本（未截断）。方便外部（例如批量导出）读取。
        /// </summary>
        public string DetailsRaw => _detailsRaw;

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
            _userToggled = false;

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

            // === 头部行（图标 + 工具名 + 状态文本 + [复制] + 折叠箭头）===
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

            // 复制按钮（默认隐藏，有详情时才显示，避免 UI 噪音）
            _copyButton = new Button(OnCopyClicked) { text = "复制" };
            _copyButton.style.fontSize = 10;
            _copyButton.style.color = TextPrimary;
            _copyButton.style.backgroundColor = CopyButtonBg;
            _copyButton.style.marginLeft = 6;
            _copyButton.style.marginTop = 0;
            _copyButton.style.marginBottom = 0;
            _copyButton.style.paddingLeft = 6;
            _copyButton.style.paddingRight = 6;
            _copyButton.style.paddingTop = 1;
            _copyButton.style.paddingBottom = 1;
            _copyButton.style.borderTopWidth = 0;
            _copyButton.style.borderBottomWidth = 0;
            _copyButton.style.borderLeftWidth = 0;
            _copyButton.style.borderRightWidth = 0;
            _copyButton.style.borderTopLeftRadius = 2;
            _copyButton.style.borderBottomLeftRadius = 2;
            _copyButton.style.borderTopRightRadius = 2;
            _copyButton.style.borderBottomRightRadius = 2;
            _copyButton.style.flexShrink = 0;
            _copyButton.style.display = DisplayStyle.None; // 默认隐藏
            _copyButton.tooltip = "复制完整原始详情到剪贴板";
            // 阻止点击复制按钮时冒泡到卡片本身触发折叠切换
            _copyButton.RegisterCallback<ClickEvent>(evt => evt.StopPropagation());
            headerRow.Add(_copyButton);

            // 折叠/展开箭头指示器
            _toggleArrow = new Label(ArrowCollapsed);
            _toggleArrow.style.fontSize = 9;
            _toggleArrow.style.color = ToggleArrowColor;
            _toggleArrow.style.marginLeft = 6;
            _toggleArrow.style.minWidth = 14;
            _toggleArrow.style.unityTextAlign = TextAnchor.MiddleCenter;
            _toggleArrow.style.flexShrink = 0;
            headerRow.Add(_toggleArrow);

            Add(headerRow);

            // === 详情区域（默认隐藏）===
            // v1.4.8：详情容器包裹一层 ScrollView，超过 DetailsMaxHeight 时可滚动查看
            // 而不是被 overflow:hidden 裁剪。TextField 支持文本选择 + Ctrl+C 复制。
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

            // ScrollView：详情高度超过 DetailsMaxHeight 时可垂直滚动
            _detailsScroll = new ScrollView(ScrollViewMode.Vertical);
            _detailsScroll.style.maxHeight = DetailsMaxHeight;
            _detailsScroll.style.flexGrow = 1;
            // 阻止 ScrollView 的滚轮 / 点击事件冒泡到卡片触发折叠切换
            _detailsScroll.RegisterCallback<ClickEvent>(evt => evt.StopPropagation());

            // 只读 TextField，multiline，支持选择和 Ctrl+C 复制
            _detailsField = new TextField
            {
                multiline = true,
                isReadOnly = true,
                value = string.Empty
            };
            _detailsField.style.fontSize = 11;
            _detailsField.style.whiteSpace = WhiteSpace.Normal;
            _detailsField.style.flexGrow = 1;
            // TextField 默认背景色是白色，覆盖为深色主题背景
            _detailsField.style.backgroundColor = DetailsBg;
            // 让 TextField 内部的实际输入子元素 (.unity-text-element) 显示 secondary 灰色文字
            _detailsField.style.color = TextPrimary;
            // 去除默认 border（在 dark theme 下会显示为一圈亮线）
            _detailsField.style.borderTopWidth = 0;
            _detailsField.style.borderBottomWidth = 0;
            _detailsField.style.borderLeftWidth = 0;
            _detailsField.style.borderRightWidth = 0;
            // 阻止 TextField 的点击事件冒泡到卡片触发折叠切换
            _detailsField.RegisterCallback<ClickEvent>(evt => evt.StopPropagation());

            _detailsScroll.Add(_detailsField);
            _detailsContainer.Add(_detailsScroll);
            Add(_detailsContainer);

            // 如果有参数，预设详情内容（v1.4.8 起不再截断）
            if (!string.IsNullOrEmpty(arguments))
            {
                SetDetailsInternal("参数: " + arguments);
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

            // 自动折叠/展开逻辑（仅在用户未手动切换时生效）
            if (!_userToggled)
            {
                ApplyAutoExpandCollapse();
            }
        }

        /// <summary>
        /// 设置详情内容（参数/结果/错误信息）。
        /// v1.4.8 起完整保留原始文本，不做长度截断。
        /// </summary>
        /// <param name="details">详情文本</param>
        public void SetDetails(string details)
        {
            if (string.IsNullOrEmpty(details)) return;

            SetDetailsInternal(details);

            // 失败状态自动展开详情（仅在用户未手动切换时）
            if (Status == ToolCallStatus.Failed && !_isExpanded && !_userToggled)
            {
                SetExpanded(true);
            }
        }

        #endregion

        #region 私有方法

        /// <summary>
        /// 内部详情设置——同步更新 _detailsRaw、_detailsField.value，
        /// 并根据是否有内容切换复制按钮的可见性。
        /// </summary>
        private void SetDetailsInternal(string details)
        {
            _detailsRaw = details ?? string.Empty;

            // v1.7.x 性能：只把前 DetailsDisplayLimit 字符送入 TextField 显示，
            // 避免超长文本（数万字符）构建全量文本网格导致展开卡顿。
            // 完整原文保留在 _detailsRaw，复制按钮取的是完整内容。
            string displayValue = _detailsRaw;
            if (_detailsRaw.Length > DetailsDisplayLimit)
            {
                var omitted = _detailsRaw.Length - DetailsDisplayLimit;
                displayValue = _detailsRaw.Substring(0, DetailsDisplayLimit)
                    + $"\n\n… [已截断 {omitted:N0} 字符，仅影响此处显示。点击右上角\"复制\"可获取完整内容] …";
            }

            _detailsField.SetValueWithoutNotify(displayValue);
            _copyButton.style.display = string.IsNullOrEmpty(_detailsRaw)
                ? DisplayStyle.None
                : DisplayStyle.Flex;
        }

        /// <summary>
        /// 复制按钮回调：把 <see cref="_detailsRaw"/> 完整内容写入系统剪贴板，
        /// 并做一个短暂的绿色闪烁反馈让用户知道确实复制了。
        /// </summary>
        private void OnCopyClicked()
        {
            if (string.IsNullOrEmpty(_detailsRaw))
                return;

            try
            {
                // EditorGUIUtility.systemCopyBuffer 是 Unity Editor 的标准剪贴板 API，
                // 跨平台可用（Windows / macOS / Linux）。
                EditorGUIUtility.systemCopyBuffer = _detailsRaw;
            }
            catch (System.Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore.UI] ToolCallCard 复制到剪贴板失败: {ex.Message}");
                return;
            }

            // 视觉反馈：按钮短暂变绿 + 文字变"已复制"，1 秒后恢复
            var originalText = _copyButton.text;
            var originalBg = _copyButton.style.backgroundColor;
            _copyButton.text = "已复制";
            _copyButton.style.backgroundColor = CopyButtonBgFlash;

            // 使用 schedule 而不是 Coroutine——UI Toolkit VisualElement 自带 schedule 系统，
            // 无需依赖 EditorApplication.update 或 MonoBehaviour。
            _copyButton.schedule.Execute(() =>
            {
                _copyButton.text = originalText;
                _copyButton.style.backgroundColor = originalBg;
            }).StartingIn(900);
        }

        /// <summary>
        /// 根据状态自动决定展开/折叠。
        /// - Running: 展开（让用户看到执行中的状态）
        /// - Completed: 折叠（减少空间占用）
        /// - Failed: 展开（显示错误信息）
        /// - Pending: 折叠
        /// </summary>
        private void ApplyAutoExpandCollapse()
        {
            switch (Status)
            {
                case ToolCallStatus.Pending:
                    SetExpanded(false);
                    break;

                case ToolCallStatus.Running:
                    // Running 状态不自动展开详情区域（详情通常还没有内容）
                    // 但保持当前状态不变
                    break;

                case ToolCallStatus.Completed:
                    // 完成后自动折叠，减少空间占用
                    SetExpanded(false);
                    break;

                case ToolCallStatus.Failed:
                    // 失败时自动展开，显示错误信息
                    if (!string.IsNullOrEmpty(_detailsRaw))
                    {
                        SetExpanded(true);
                    }
                    break;
            }
        }

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
        /// 设置展开/折叠状态并更新 UI。
        /// </summary>
        /// <param name="expanded">是否展开</param>
        private void SetExpanded(bool expanded)
        {
            _isExpanded = expanded;
            _detailsContainer.style.display = _isExpanded ? DisplayStyle.Flex : DisplayStyle.None;
            _toggleArrow.text = _isExpanded ? ArrowExpanded : ArrowCollapsed;
        }

        /// <summary>
        /// 切换详情区域的展开/折叠状态。
        /// </summary>
        private void ToggleDetails()
        {
            SetExpanded(!_isExpanded);
        }

        /// <summary>
        /// 卡片点击事件处理：展开/折叠详情。
        /// v1.4.8：复制按钮、ScrollView、TextField 的点击事件都已 StopPropagation，
        /// 所以此处收到的都是 header 空白区域或折叠状态下的点击，不会被误触。
        /// </summary>
        private void OnCardClicked(ClickEvent evt)
        {
            // 仅在有详情内容时才允许展开
            if (!string.IsNullOrEmpty(_detailsRaw))
            {
                _userToggled = true; // 标记用户已手动切换
                ToggleDetails();
            }
            evt.StopPropagation();
        }

        #endregion
    }
}
