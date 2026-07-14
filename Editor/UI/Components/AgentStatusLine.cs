using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Components
{
    /// <summary>
    /// Agent 状态行 — 在消息流底部、文件变更面板上方显示当前 agent 状态。
    /// <para>
    /// 整个 turn 生命周期内始终可见，跟随 AgentState 变化：
    /// 思考中 → 执行工具: {toolName} → 回复中 → 就绪。
    /// active 状态下圆点呼吸动画，idle/error 静止。
    /// </para>
    /// </summary>
    public class AgentStatusLine : VisualElement
    {
        #region 常量

        private static readonly Color BgColor = new Color(0.16f, 0.16f, 0.16f);
        private static readonly Color TopBorderColor = new Color(0.22f, 0.22f, 0.22f);
        private static readonly Color ColorIdle = new Color(0.53f, 0.53f, 0.53f);
        private static readonly Color ColorActive = new Color(0.29f, 0.57f, 0.85f);
        private static readonly Color ColorError = new Color(0.9f, 0.4f, 0.4f);

        private const int PulseIntervalMs = 600;

        #endregion

        #region UI 元素

        private readonly Label _dot;
        private readonly Label _text;

        #endregion

        #region 状态

        private bool _isAnimating;
        private bool _pulsePhase;

        #endregion

        public AgentStatusLine()
        {
            style.flexDirection = FlexDirection.Row;
            style.alignItems = Align.Center;
            style.paddingLeft = 12;
            style.paddingRight = 12;
            style.paddingTop = 3;
            style.paddingBottom = 3;
            style.height = 22;
            style.flexShrink = 0;
            style.backgroundColor = BgColor;
            style.borderTopWidth = 1;
            style.borderTopColor = TopBorderColor;

            _dot = new Label("●");
            _dot.style.fontSize = 9;
            _dot.style.marginRight = 6;
            _dot.style.color = ColorIdle;
            _dot.style.unityTextAlign = TextAnchor.MiddleCenter;
            _dot.style.unityFontStyleAndWeight = FontStyle.Bold;
            Add(_dot);

            _text = new Label("就绪");
            _text.style.fontSize = 11;
            _text.style.color = ColorIdle;
            _text.style.flexGrow = 1;
            _text.style.unityTextAlign = TextAnchor.MiddleLeft;
            _text.style.overflow = Overflow.Hidden;
            _text.style.textOverflow = TextOverflow.Ellipsis;
            Add(_text);

            // 呼吸动画调度器
            schedule.Execute(OnPulseTick).Every(PulseIntervalMs);
        }

        #region 公开方法

        /// <summary>
        /// 更新状态行文本和样式。
        /// </summary>
        /// <param name="text">状态文本</param>
        /// <param name="isError">是否为错误状态（红色）</param>
        /// <param name="isActive">是否处于活动状态（蓝色 + 呼吸动画）</param>
        public void SetStatus(string text, bool isError = false, bool isActive = false)
        {
            _text.text = text;

            if (isError)
            {
                _text.style.color = ColorError;
                _dot.style.color = ColorError;
                _dot.style.opacity = 1f;
                _isAnimating = false;
            }
            else if (isActive)
            {
                _text.style.color = ColorActive;
                _dot.style.color = ColorActive;
                _dot.style.opacity = 1f;
                _isAnimating = true;
                _pulsePhase = false;
            }
            else
            {
                _text.style.color = ColorIdle;
                _dot.style.color = ColorIdle;
                _dot.style.opacity = 1f;
                _isAnimating = false;
            }
        }

        #endregion

        #region 私有方法

        private void OnPulseTick()
        {
            if (!_isAnimating) return;

            _pulsePhase = !_pulsePhase;
            // 呼吸效果：1.0 ↔ 0.35
            _dot.style.opacity = _pulsePhase ? 0.35f : 1f;
        }

        #endregion
    }
}
