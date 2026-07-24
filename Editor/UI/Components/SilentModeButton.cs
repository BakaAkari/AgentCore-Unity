using System;
using AgentCore.Editor.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Components
{
    /// <summary>
    /// v1.8.8: Silent 模式切换按钮.
    /// <para>
    /// 位于 ChatWindow 输入栏最左侧. 点击切换 <see cref="SessionModeState.Current"/> 在
    /// Batched / Silent 之间. 按钮样式:
    /// - Batched (默认): 灰色文字, 无背景
    /// - Silent (激活): 黄色文字/背景, 视觉明显
    /// </para>
    /// <para>
    /// 组件自身订阅 <see cref="SessionModeState.Changed"/>, 状态变化时自动刷新样式.
    /// 不通过外部 API 拉更新, ChatWindow 不需要每次手动 SetSilentVisual.
    /// </para>
    /// </summary>
    public class SilentModeButton : Button
    {
        private static readonly Color YellowActive = new Color(0.96f, 0.77f, 0.09f);   // #F5C518-ish
        private static readonly Color YellowBgActive = new Color(0.96f, 0.77f, 0.09f, 0.18f);
        private static readonly Color GrayIdle = new Color(0.68f, 0.68f, 0.68f);

        // v1.8.10: 28x28 正方形只能塞下单字符. 用 'S' 表示 Silent, 具体含义由 tooltip 说明.
        private const string BtnText = "S";
        private const string BtnTooltip =
            "Silent mode: freeze chat UI during agent execution to avoid interfering with " +
            "performance measurements. Results appear all at once when the task finishes.";

        public SilentModeButton()
        {
            text = BtnText;
            tooltip = BtnTooltip;

            // v1.8.10: 与 send/cancel 按钮 (28x60) 完全对齐: 底对齐, 高度 28, 宽度 28 (正方形)
            style.flexShrink = 0;
            style.width = 28;                 // 正方形
            style.height = 28;                // 与 send/cancel 一致
            style.marginRight = 4;
            style.marginLeft = 0;
            style.marginTop = 0;
            style.marginBottom = 0;
            style.paddingLeft = 0;
            style.paddingRight = 0;
            style.paddingTop = 0;
            style.paddingBottom = 0;
            style.borderTopLeftRadius = 6;    // 圆角与 send/cancel 一致
            style.borderTopRightRadius = 6;
            style.borderBottomLeftRadius = 6;
            style.borderBottomRightRadius = 6;
            style.borderLeftWidth = 0;
            style.borderRightWidth = 0;
            style.borderTopWidth = 0;
            style.borderBottomWidth = 0;
            style.fontSize = 14;              // 单字符 'S' 稍大更清晰
            style.unityTextAlign = TextAnchor.MiddleCenter;
            style.alignSelf = Align.FlexEnd;  // input-area 是 flex-end, 与 send/cancel 底对齐

            clicked += OnClicked;
            RegisterCallback<AttachToPanelEvent>(OnAttach);
            RegisterCallback<DetachFromPanelEvent>(OnDetach);
            ApplyVisual(SessionModeState.Current);
        }

        private void OnAttach(AttachToPanelEvent evt)
        {
            SessionModeState.Changed += OnModeChanged;
            ApplyVisual(SessionModeState.Current);
        }

        private void OnDetach(DetachFromPanelEvent evt)
        {
            SessionModeState.Changed -= OnModeChanged;
        }

        private void OnClicked()
        {
            // toggle
            var next = SessionModeState.Current == SessionMode.Silent
                ? SessionMode.Batched
                : SessionMode.Silent;
            SessionModeState.Set(next);
            // 视觉更新由 OnModeChanged 触发, 无需手动调 ApplyVisual
        }

        private void OnModeChanged(SessionMode newMode)
        {
            ApplyVisual(newMode);
        }

        private void ApplyVisual(SessionMode mode)
        {
            if (mode == SessionMode.Silent)
            {
                style.color = YellowActive;
                style.backgroundColor = YellowBgActive;
                style.unityFontStyleAndWeight = FontStyle.Bold;
            }
            else
            {
                style.color = GrayIdle;
                style.backgroundColor = new Color(0f, 0f, 0f, 0f); // 透明背景 (让父容器色号透出来, 与 send/cancel 按钮一致)
                style.unityFontStyleAndWeight = FontStyle.Normal;
            }
        }
    }
}
