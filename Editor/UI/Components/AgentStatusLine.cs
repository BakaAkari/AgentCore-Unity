using System;
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

        private static readonly Color BgColor = new Color(0.14f, 0.14f, 0.14f);
        private static readonly Color TopBorderColor = new Color(0.30f, 0.30f, 0.30f);
        private static readonly Color ColorIdle = new Color(0.60f, 0.60f, 0.60f);
        private static readonly Color ColorActive = new Color(0.35f, 0.66f, 1.0f);
        private static readonly Color ColorError = new Color(0.95f, 0.35f, 0.35f);
        private static readonly Color ColorActiveText = new Color(0.75f, 0.85f, 1.0f);

        private const int PulseIntervalMs = 600;

        #endregion

        #region UI 元素

        private readonly Label _dot;
        private readonly Label _text;

        #endregion

        #region 状态

        private bool _isAnimating;
        private bool _pulsePhase;
        private DateTime? _lastPulseUtc;

        #endregion

        public AgentStatusLine()
        {
            style.flexDirection = FlexDirection.Row;
            style.alignItems = Align.Center;
            style.paddingLeft = 14;
            style.paddingRight = 14;
            style.paddingTop = 5;
            style.paddingBottom = 5;
            style.height = 28;
            style.flexShrink = 0;
            style.backgroundColor = BgColor;
            style.borderTopWidth = 1;
            style.borderTopColor = TopBorderColor;
            style.borderBottomWidth = 1;
            style.borderBottomColor = TopBorderColor;

            _dot = new Label("●");
            _dot.style.fontSize = 13;
            _dot.style.marginRight = 8;
            _dot.style.color = ColorIdle;
            _dot.style.unityTextAlign = TextAnchor.MiddleCenter;
            _dot.style.unityFontStyleAndWeight = FontStyle.Bold;
            Add(_dot);

            // v1.9.0+: 走 Loc, 与 ChatWindow.Events.cs 里 AgentState.Idle 分支保持一致的 key
            _text = new Label(AgentCore.Editor.L10n.Loc.Tr("chat.status.idle", "就绪"));
            _text.style.fontSize = 13;
            _text.style.color = ColorIdle;
            _text.style.flexGrow = 1;
            _text.style.unityTextAlign = TextAnchor.MiddleLeft;
            _text.style.overflow = Overflow.Hidden;
            _text.style.textOverflow = TextOverflow.Ellipsis;
            _text.style.unityFontStyleAndWeight = FontStyle.Bold;
            Add(_text);

            // v1.14.5: 呼吸动画不再用 schedule.Execute().Every() 自驱动定时器（v1.8.7 曾因
            // 400ms 定时器持续 post continuation 累积主线程阻塞而全关）。改为完全被动：
            // 由外部（AgentLoop 的节流心跳事件）调用 Tick() 才翻转一帧。数据不流入时
            // (模型真的卡住/网络断开) 动画也不会凭空跳动 —— 诚实反映系统状态，而非用假动画
            // 掩盖卡死。详见 HandleAgentEvent 中 ToolCallProgress / StreamToken / ReasoningToken
            // 分支对 Tick() 的调用。
        }

        #region 公开方法

        /// <summary>
        /// v1.14.5: 由外部事件驱动的动画帧 tick —— 每次收到"系统仍在流式产出数据"的
        /// 信号时调用一次（ToolCallProgress 心跳 / StreamToken / ReasoningToken）。
        /// 内部按 <see cref="PulseIntervalMs"/> 做时间防抖，即使被高频调用也不会视觉闪烁；
        /// 若长时间没有调用（数据流真的停了），呼吸动画会自然停在最后一帧，不伪造活跃状态。
        /// </summary>
        public void Tick()
        {
            if (!_isAnimating) return;

            var now = DateTime.UtcNow;
            if (_lastPulseUtc != null && (now - _lastPulseUtc.Value).TotalMilliseconds < PulseIntervalMs)
                return;

            _lastPulseUtc = now;
            _pulsePhase = !_pulsePhase;
            // 呼吸效果：1.0 ↔ 0.35
            _dot.style.opacity = _pulsePhase ? 0.35f : 1f;
        }

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
                _text.style.color = ColorActiveText;
                _dot.style.color = ColorActive;
                _dot.style.opacity = 1f;
                _isAnimating = true;
                _pulsePhase = false;
                _lastPulseUtc = null;
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
    }
}
