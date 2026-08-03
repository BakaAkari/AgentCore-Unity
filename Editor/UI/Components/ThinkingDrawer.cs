using AgentCore.Editor.Core;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Components
{
    /// <summary>
    /// 默认折叠的 reasoning / planning trace 文本抽屉。
    /// </summary>
    public class ThinkingDrawer : VisualElement
    {
        private static readonly Color HeaderBg = new Color(0.18f, 0.18f, 0.18f);
        private static readonly Color HeaderBgHover = new Color(0.22f, 0.22f, 0.22f);
        private static readonly Color ContentBg = new Color(0.145f, 0.145f, 0.145f);
        private static readonly Color TextPrimary = new Color(0.78f, 0.78f, 0.78f);
        private static readonly Color TextSecondary = new Color(0.55f, 0.55f, 0.55f);
        private static readonly Color BorderColor = new Color(0.25f, 0.25f, 0.25f);
        private static readonly Color AccentColor = new Color(0.60f, 0.45f, 0.85f);

        // ASCII-only 三角箭头（避免 Unity 默认字体不支持 BMP Geometric Shapes 时渲染成方块）
        private const string ArrowCollapsed = ">";
        private const string ArrowExpanded = "v";

        private readonly VisualElement _header;
        private readonly Button _toggleButton;
        private readonly Label _titleLabel;
        private readonly Label _sourceLabel;
        private readonly Label _previewLabel;
        private readonly VisualElement _content;
        private readonly Label _reasoningLabel;

        private const int PreviewMaxChars = 60;

        /// <summary>
        /// UI 渲染字符上限(v1.14.4)。UIToolkit 单个 Label 生成 mesh 时每字符约占若干顶点，
        /// 长文本达到约 49152 顶点会被截断显示，65535 顶点直接报错
        /// "A VisualElement must not allocate more than 65535 vertices"。
        /// 长任务场景下模型单轮 reasoning 可能轻松写出数万字，必须在渲染层做硬性上限，
        /// 否则不仅报错，撞线前的全量文本重排(见 FlushReasoningPending)还会随文本增长
        /// 拖慢主线程、造成"看起来卡死"的体感。取远低于两个阈值的安全值。
        /// 注意：此上限只影响 UI 显示，不裁剪 <see cref="_reasoningText"/> 本身，
        /// 完整 reasoning 内容仍会整体持久化到 ConversationTurn.Reasoning。
        /// </summary>
        private const int MaxRenderChars = 6000;

        private string _reasoningText = string.Empty;

        /// <summary>
        /// v1.14.4: reasoning token 的追加用累加器。<see cref="_reasoningText"/> 每 token 一次
        /// 字符串 += 拼接是 O(n) 重新分配拷贝，文本越长单次追加越慢，长任务(数万字 reasoning)
        /// 下会造成随对话推进愈发明显的主线程卡顿。改用 StringBuilder 累积，
        /// 仅在 <see cref="FlushReasoningPending"/>(节流后，非每 token)同步到 <see cref="_reasoningText"/>。
        /// </summary>
        private readonly StringBuilder _reasoningAccumulator = new StringBuilder();
        private bool _isExpanded;
        private bool _isRunning;
        private double _durationMs;
        private double _startedAt;
        private ThinkingTraceSource _source = ThinkingTraceSource.None;
        private IVisualElementScheduledItem _timer;

        // --- v1.6.5 性能优化：reasoning token 缓冲 + 帧节流 ---
        // 旧实现：每 token 调 UpdatePreview/UpdateTitle/UpdateSourceLabel → 高频 UI relayout
        // 新实现：token 累积到 _reasoningPending，每帧只 flush 一次
        private StringBuilder _reasoningPending = new();
        private bool _reasoningFlushScheduled;
        private const int ReasoningFlushIntervalMs = 16;

        /// <summary>
        /// 创建 ThinkingDrawer。
        /// </summary>
        public ThinkingDrawer()
        {
            AddToClassList("thinking-drawer");
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
            style.display = DisplayStyle.None;

            _header = new VisualElement();
            _header.style.flexDirection = FlexDirection.Row;
            _header.style.alignItems = Align.Center;
            _header.style.backgroundColor = HeaderBg;
            _header.style.paddingLeft = 10;
            _header.style.paddingRight = 10;
            _header.style.paddingTop = 6;
            _header.style.paddingBottom = 6;
            _header.style.minHeight = 28;

            var accent = new VisualElement();
            accent.style.width = 3;
            accent.style.height = 16;
            accent.style.marginRight = 7;
            accent.style.backgroundColor = AccentColor;
            accent.style.borderTopLeftRadius = 2;
            accent.style.borderTopRightRadius = 2;
            accent.style.borderBottomLeftRadius = 2;
            accent.style.borderBottomRightRadius = 2;
            _header.Add(accent);

            // 独立的展开/折叠按钮（用户明确要求：可视化按钮）
            _toggleButton = new Button(() => SetExpanded(!_isExpanded))
            {
                text = ArrowCollapsed
            };
            _toggleButton.AddToClassList("thinking-drawer__toggle");
            _toggleButton.style.fontSize = 10;
            _toggleButton.style.color = TextSecondary;
            _toggleButton.style.marginRight = 6;
            _toggleButton.style.marginLeft = 0;
            _toggleButton.style.marginTop = 0;
            _toggleButton.style.marginBottom = 0;
            _toggleButton.style.paddingLeft = 4;
            _toggleButton.style.paddingRight = 4;
            _toggleButton.style.paddingTop = 0;
            _toggleButton.style.paddingBottom = 0;
            _toggleButton.style.minWidth = 22;
            _toggleButton.style.height = 20;
            _toggleButton.style.backgroundColor = new Color(0.24f, 0.24f, 0.24f);
            _toggleButton.style.borderTopLeftRadius = 3;
            _toggleButton.style.borderTopRightRadius = 3;
            _toggleButton.style.borderBottomLeftRadius = 3;
            _toggleButton.style.borderBottomRightRadius = 3;
            _toggleButton.style.unityTextAlign = TextAnchor.MiddleCenter;
            _toggleButton.tooltip = AgentCore.Editor.L10n.Loc.Tr("thinking.tooltip.toggle", "展开 / 折叠 Thinking");
            _header.Add(_toggleButton);

            _titleLabel = new Label(AgentCore.Editor.L10n.Loc.Tr("thinking.title.running", "思考中 · {0}s", "0"));
            _titleLabel.style.fontSize = 12;
            _titleLabel.style.color = TextPrimary;
            _titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _titleLabel.style.marginRight = 8;
            _header.Add(_titleLabel);

            _sourceLabel = new Label(string.Empty);
            _sourceLabel.style.fontSize = 11;
            _sourceLabel.style.color = TextSecondary;
            _sourceLabel.style.flexShrink = 0;
            _sourceLabel.style.marginRight = 6;
            _header.Add(_sourceLabel);

            // 折叠状态下显示 reasoning 尾部预览（让用户不展开也能感知内容正在流入）
            _previewLabel = new Label(string.Empty);
            _previewLabel.AddToClassList("thinking-drawer__preview");
            _previewLabel.style.fontSize = 11;
            _previewLabel.style.color = TextSecondary;
            _previewLabel.style.flexGrow = 1;
            _previewLabel.style.overflow = Overflow.Hidden;
            _previewLabel.style.textOverflow = TextOverflow.Ellipsis;
            _previewLabel.style.unityFontStyleAndWeight = FontStyle.Italic;
            _header.Add(_previewLabel);

            _header.RegisterCallback<MouseEnterEvent>(_ => _header.style.backgroundColor = HeaderBgHover);
            _header.RegisterCallback<MouseLeaveEvent>(_ => _header.style.backgroundColor = HeaderBg);
            // header 点击也 toggle（除按钮外的空白区）— 双入口，用户拖拽选中不受影响
            _header.RegisterCallback<ClickEvent>(evt =>
            {
                if (evt.target == _toggleButton) return; // 按钮已单独处理
                SetExpanded(!_isExpanded);
            });
            Add(_header);

            _content = new VisualElement();
            _content.style.flexDirection = FlexDirection.Column;
            _content.style.backgroundColor = ContentBg;
            _content.style.paddingLeft = 12;
            _content.style.paddingRight = 12;
            _content.style.paddingTop = 8;
            _content.style.paddingBottom = 8;
            _content.style.display = DisplayStyle.None;

            _reasoningLabel = new Label(string.Empty);
            _reasoningLabel.style.fontSize = 12;
            _reasoningLabel.style.color = TextPrimary;
            _reasoningLabel.style.whiteSpace = WhiteSpace.Normal;
            _reasoningLabel.style.unityTextAlign = TextAnchor.UpperLeft;
            _content.Add(_reasoningLabel);
            Add(_content);

            // v1.9.0+: 订阅语言变化事件, 切语言时刷新 tooltip 和 title
            RegisterCallback<AttachToPanelEvent>(_ =>
                AgentCore.Editor.L10n.LanguageManager.LanguageChanged += OnLanguageChanged);
            RegisterCallback<DetachFromPanelEvent>(_ =>
                AgentCore.Editor.L10n.LanguageManager.LanguageChanged -= OnLanguageChanged);
        }

        /// <summary>
        /// 获取可安全渲染的 reasoning 文本：超过 <see cref="MaxRenderChars"/> 时只取最新片段，
        /// 头部加省略提示，避免 UIToolkit 单文本框顶点上限截断/报错。
        /// 完整内容仍保留在 <see cref="_reasoningText"/>，此方法只影响展示。
        /// </summary>
        private string GetRenderableReasoningText()
        {
            var text = _reasoningText ?? string.Empty;
            if (text.Length <= MaxRenderChars) return text;

            var omitted = text.Length - MaxRenderChars;
            var head = $"...[已省略前 {omitted} 字，仅显示最新内容，完整思考过程已完整保存]...\n\n";
            return head + text.Substring(text.Length - MaxRenderChars);
        }

        private void OnLanguageChanged(string _)
        {
            // 刷新 tooltip
            _toggleButton.tooltip = _isExpanded
                ? AgentCore.Editor.L10n.Loc.Tr("thinking.tooltip.collapse", "折叠 Thinking")
                : AgentCore.Editor.L10n.Loc.Tr("thinking.tooltip.expand", "展开 Thinking");

            // 刷新 title (running/done 都可能滞留旧语言, 强制走一次 UpdateTitle 逻辑)
            var seconds = GetDisplaySeconds();
            _titleLabel.text = _isRunning
                ? AgentCore.Editor.L10n.Loc.Tr("thinking.title.running", "思考中 · {0}s", seconds)
                : AgentCore.Editor.L10n.Loc.Tr("thinking.title.done", "思考完成 · {0}s", seconds);
        }

        /// <summary>
        /// 追加 reasoning token 并启动计时。
        /// </summary>
        /// <param name="token">reasoning token。</param>
        /// <param name="source">reasoning 来源。</param>
        public void AppendReasoning(string token, ThinkingTraceSource source)
        {
            if (string.IsNullOrEmpty(token)) return;

            style.display = DisplayStyle.Flex;
            if (!_isRunning)
            {
                _isRunning = true;
                _startedAt = EditorTime;
                StartTimer();
                // 开始接收 reasoning 时启用脉动动画
                AddToClassList("active-pulse");
            }

            _source = MergeSource(_source, source);

            // v1.14.4: 用 StringBuilder 累积，避免每 token 一次 O(n) 字符串拼接。
            _reasoningAccumulator.Append(token);

            // v1.6.5: 累积 token，帧节流 flush — 不每 token 更新 UI
            _reasoningPending.Append(token);
            ScheduleReasoningFlush();
        }

        /// <summary>
        /// 调度一次延迟 reasoning flush。已排队则跳过。
        /// </summary>
        private void ScheduleReasoningFlush()
        {
            if (_reasoningFlushScheduled) return;
            _reasoningFlushScheduled = true;
            schedule.Execute(FlushReasoningPending).StartingIn(ReasoningFlushIntervalMs);
        }

        /// <summary>
        /// 将累积的 reasoning token 一次性更新到 UI。
        /// 每 16ms 最多一次：UpdateSourceLabel + UpdatePreview/Label + UpdateTitle。
        /// </summary>
        private void FlushReasoningPending()
        {
            _reasoningFlushScheduled = false;
            if (_reasoningPending.Length == 0) return;

            // v1.14.4: 节流后统一同步一次（而非每 token），ToString() 有拷贝开销但频率已降到 ~60fps。
            _reasoningText = _reasoningAccumulator.ToString();

            UpdateSourceLabel();
            if (_isExpanded)
            {
                _reasoningLabel.text = ContentFilter.SanitizeUnsupportedEmoji(GetRenderableReasoningText());
            }
            else
            {
                UpdatePreview();
            }
            UpdateTitle();

            _reasoningPending.Clear();
        }

        /// <summary>
        /// 使用已持久化内容恢复 drawer。
        /// </summary>
        /// <param name="reasoning">完整 reasoning 内容。</param>
        /// <param name="source">reasoning 来源。</param>
        /// <param name="durationMs">耗时毫秒。</param>
        public void SetReasoning(string reasoning, ThinkingTraceSource source, double durationMs)
        {
            _reasoningText = reasoning ?? string.Empty;
            // v1.14.4: 会话恢复走直接赋值路径，同步累加器避免后续 AppendReasoning 时状态不一致。
            _reasoningAccumulator.Clear();
            _reasoningAccumulator.Append(_reasoningText);
            _source = source;
            _durationMs = Mathf.Max(0, (float)durationMs);
            _isRunning = false;
            style.display = string.IsNullOrEmpty(_reasoningText) ? DisplayStyle.None : DisplayStyle.Flex;
            StopTimer();
            UpdateSourceLabel();
            UpdateTitle();
            if (_isExpanded)
            {
                _reasoningLabel.text = ContentFilter.SanitizeUnsupportedEmoji(GetRenderableReasoningText());
            }
        }

        /// <summary>
        /// 标记 reasoning 完成。
        /// </summary>
        /// <param name="durationMs">累计耗时毫秒。</param>
        /// <param name="source">reasoning 来源。</param>
        public void Complete(double durationMs, ThinkingTraceSource source)
        {
            if (style.display == DisplayStyle.None && string.IsNullOrEmpty(_reasoningText)) return;

            // v1.6.5: 完成时 flush 残留 token
            _reasoningFlushScheduled = false;
            _reasoningPending.Clear();

            // v1.14.4: 确保最后一批未被节流 flush 同步到 _reasoningText 的 token 不丢失。
            _reasoningText = _reasoningAccumulator.ToString();

            _durationMs = Mathf.Max(0, (float)durationMs);
            _source = MergeSource(_source, source);
            _isRunning = false;
            StopTimer();
            UpdateSourceLabel();
            UpdateTitle();
            // 完成后不再需要脉动
            RemoveFromClassList("active-pulse");
        }

        private void SetExpanded(bool expanded)
        {
            _isExpanded = expanded;
            _toggleButton.text = expanded ? ArrowExpanded : ArrowCollapsed;
            _toggleButton.tooltip = expanded
                ? AgentCore.Editor.L10n.Loc.Tr("thinking.tooltip.collapse", "折叠 Thinking")
                : AgentCore.Editor.L10n.Loc.Tr("thinking.tooltip.expand", "展开 Thinking");
            _content.style.display = expanded ? DisplayStyle.Flex : DisplayStyle.None;
            _reasoningLabel.text = expanded ? ContentFilter.SanitizeUnsupportedEmoji(GetRenderableReasoningText()) : string.Empty;
            // 展开时隐藏 preview（避免重复展示），折叠时刷新 preview
            _previewLabel.style.display = expanded ? DisplayStyle.None : DisplayStyle.Flex;
            if (!expanded) UpdatePreview();
        }

        /// <summary>
        /// 更新折叠状态下的尾部预览（最新 <see cref="PreviewMaxChars"/> 字符）。
        /// </summary>
        private void UpdatePreview()
        {
            if (_previewLabel == null) return;

            var text = _reasoningText ?? string.Empty;
            // 只取最新片段，去除换行让单行显示不跳动
            var trimmed = text.Replace('\r', ' ').Replace('\n', ' ');
            if (trimmed.Length > PreviewMaxChars)
                trimmed = "..." + trimmed.Substring(trimmed.Length - PreviewMaxChars);

            _previewLabel.text = trimmed;
        }

        private void StartTimer()
        {
            StopTimer();
            // v1.8.7: 全关动态效果, ThinkingDrawer 标题定时更新禁用
            // _timer = schedule.Execute(UpdateTitle).Every(250);
        }

        private void StopTimer()
        {
            _timer?.Pause();
            _timer = null;
        }

        private void UpdateTitle()
        {
            var seconds = GetDisplaySeconds();
            _titleLabel.text = _isRunning
                ? AgentCore.Editor.L10n.Loc.Tr("thinking.title.running", "思考中 · {0}s", seconds)
                : AgentCore.Editor.L10n.Loc.Tr("thinking.title.done", "思考完成 · {0}s", seconds);
        }

        private int GetDisplaySeconds()
        {
            if (_isRunning)
            {
                return Mathf.Max(0, Mathf.FloorToInt((float)(EditorTime - _startedAt)));
            }
            return Mathf.Max(0, Mathf.CeilToInt((float)_durationMs / 1000f));
        }

        private void UpdateSourceLabel()
        {
            switch (_source)
            {
                case ThinkingTraceSource.StructuredReasoning:
                    _sourceLabel.text = "Structured Reasoning";
                    break;
                case ThinkingTraceSource.VisiblePlanningTrace:
                    _sourceLabel.text = "Visible Planning Trace";
                    break;
                case ThinkingTraceSource.Mixed:
                    _sourceLabel.text = "Mixed";
                    break;
                default:
                    _sourceLabel.text = string.Empty;
                    break;
            }
        }

        private static ThinkingTraceSource MergeSource(ThinkingTraceSource current, ThinkingTraceSource next)
        {
            if (next == ThinkingTraceSource.None) return current;
            if (current == ThinkingTraceSource.None) return next;
            if (current == next) return current;
            return ThinkingTraceSource.Mixed;
        }

        private static double EditorTime => UnityEditor.EditorApplication.timeSinceStartup;
    }
}
