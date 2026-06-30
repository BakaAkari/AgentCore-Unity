using AgentCore.Editor.Core;
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

        private const string ArrowCollapsed = ">";
        private const string ArrowExpanded = "v";

        private readonly VisualElement _header;
        private readonly Label _arrowLabel;
        private readonly Label _titleLabel;
        private readonly Label _sourceLabel;
        private readonly VisualElement _content;
        private readonly Label _reasoningLabel;

        private string _reasoningText = string.Empty;
        private bool _isExpanded;
        private bool _isRunning;
        private double _durationMs;
        private double _startedAt;
        private ThinkingTraceSource _source = ThinkingTraceSource.None;
        private IVisualElementScheduledItem _timer;

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

            _arrowLabel = new Label(ArrowCollapsed);
            _arrowLabel.style.fontSize = 10;
            _arrowLabel.style.color = TextSecondary;
            _arrowLabel.style.marginRight = 6;
            _arrowLabel.style.minWidth = 14;
            _arrowLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _header.Add(_arrowLabel);

            _titleLabel = new Label("思考中 · 0s");
            _titleLabel.style.fontSize = 12;
            _titleLabel.style.color = TextPrimary;
            _titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _titleLabel.style.marginRight = 8;
            _header.Add(_titleLabel);

            _sourceLabel = new Label(string.Empty);
            _sourceLabel.style.fontSize = 11;
            _sourceLabel.style.color = TextSecondary;
            _sourceLabel.style.flexGrow = 1;
            _sourceLabel.style.overflow = Overflow.Hidden;
            _sourceLabel.style.textOverflow = TextOverflow.Ellipsis;
            _header.Add(_sourceLabel);

            _header.RegisterCallback<MouseEnterEvent>(_ => _header.style.backgroundColor = HeaderBgHover);
            _header.RegisterCallback<MouseLeaveEvent>(_ => _header.style.backgroundColor = HeaderBg);
            _header.RegisterCallback<ClickEvent>(_ => SetExpanded(!_isExpanded));
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
            }

            _source = MergeSource(_source, source);
            _reasoningText += token;
            UpdateSourceLabel();
            if (_isExpanded)
            {
                _reasoningLabel.text = ContentFilter.SanitizeUnsupportedEmoji(_reasoningText);
            }
            UpdateTitle();
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
            _source = source;
            _durationMs = Mathf.Max(0, (float)durationMs);
            _isRunning = false;
            style.display = string.IsNullOrEmpty(_reasoningText) ? DisplayStyle.None : DisplayStyle.Flex;
            StopTimer();
            UpdateSourceLabel();
            UpdateTitle();
            if (_isExpanded)
            {
                _reasoningLabel.text = ContentFilter.SanitizeUnsupportedEmoji(_reasoningText);
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

            _durationMs = Mathf.Max(0, (float)durationMs);
            _source = MergeSource(_source, source);
            _isRunning = false;
            StopTimer();
            UpdateSourceLabel();
            UpdateTitle();
        }

        private void SetExpanded(bool expanded)
        {
            _isExpanded = expanded;
            _arrowLabel.text = expanded ? ArrowExpanded : ArrowCollapsed;
            _content.style.display = expanded ? DisplayStyle.Flex : DisplayStyle.None;
            _reasoningLabel.text = expanded ? ContentFilter.SanitizeUnsupportedEmoji(_reasoningText) : string.Empty;
        }

        private void StartTimer()
        {
            StopTimer();
            _timer = schedule.Execute(UpdateTitle).Every(250);
        }

        private void StopTimer()
        {
            _timer?.Pause();
            _timer = null;
        }

        private void UpdateTitle()
        {
            var seconds = GetDisplaySeconds();
            _titleLabel.text = _isRunning ? $"思考中 · {seconds}s" : $"思考完成 · {seconds}s";
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
