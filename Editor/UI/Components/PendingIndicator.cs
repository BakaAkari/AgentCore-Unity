using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Components
{
    /// <summary>
    /// 用户点击发送后、真正的 assistant turn 出现前的"占位气泡"。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 目的：解决"点击发送后 5-30 秒 UI 无反应"的用户感知问题。
    /// LLM 请求 in-flight 期间没有任何流式事件抵达，但用户需要立刻看到反馈。
    /// </para>
    /// <para>
    /// 生命周期：
    /// <list type="bullet">
    ///   <item>由 <see cref="UI.ChatWindow.OnSendClicked"/> 在发送后立刻创建并加入消息列表</item>
    ///   <item>Agent 状态变化时通过 <see cref="SetActionText"/> 更新描述</item>
    ///   <item>真正的 assistant turn view 出现时由 <see cref="UI.ChatWindow.HandleAgentEvent"/> 移除</item>
    /// </list>
    /// </para>
    /// <para>
    /// 视觉：灰色气泡，左侧 3 个脉动点 + 一行动作文本。带 `pending-indicator--active` USS class 触发 CSS 动画。
    /// </para>
    /// </remarks>
    public sealed class PendingIndicator : VisualElement
    {
        private readonly Label _actionLabel;
        private IVisualElementScheduledItem _dotAnim;
        private int _dotFrame;

        public PendingIndicator()
        {
            AddToClassList("pending-indicator");
            style.flexDirection = FlexDirection.Row;
            style.alignItems = Align.Center;
            style.marginLeft = 12;
            style.marginRight = 12;
            style.marginTop = 4;
            style.marginBottom = 4;
            style.paddingLeft = 12;
            style.paddingRight = 12;
            style.paddingTop = 8;
            style.paddingBottom = 8;
            style.borderTopLeftRadius = 8;
            style.borderTopRightRadius = 8;
            style.borderBottomLeftRadius = 8;
            style.borderBottomRightRadius = 8;
            style.backgroundColor = new Color(0.25f, 0.25f, 0.25f, 0.6f);
            style.borderTopWidth = 1;
            style.borderBottomWidth = 1;
            style.borderLeftWidth = 1;
            style.borderRightWidth = 1;
            style.borderTopColor = new Color(0.4f, 0.4f, 0.4f, 0.5f);
            style.borderBottomColor = new Color(0.4f, 0.4f, 0.4f, 0.5f);
            style.borderLeftColor = new Color(0.4f, 0.4f, 0.4f, 0.5f);
            style.borderRightColor = new Color(0.4f, 0.4f, 0.4f, 0.5f);
            style.alignSelf = Align.FlexStart;

            _actionLabel = new Label("思考中");
            _actionLabel.style.fontSize = 12;
            _actionLabel.style.color = new Color(0.8f, 0.8f, 0.8f);
            _actionLabel.style.unityFontStyleAndWeight = FontStyle.Normal;
            Add(_actionLabel);

            // 用 IVisualElementScheduledItem 驱动 3 点动画（UI Toolkit 支持性最好的方式）
            _dotAnim = schedule.Execute(TickDots).Every(400);
        }

        /// <summary>
        /// 更新动作描述文本。由 <see cref="UI.ChatWindow"/> 在 Agent 状态变化时调用。
        /// </summary>
        public void SetActionText(string text)
        {
            _actionLabel.text = text ?? "处理中";
        }

        /// <summary>
        /// 更新 3 点循环动画（当前，下一，下下一 → 1, 2, 3 个点）。
        /// </summary>
        private void TickDots()
        {
            _dotFrame = (_dotFrame + 1) % 3;
            var dots = new string('.', _dotFrame + 1);
            // 保留 action text 主体，只更新末尾的 dots
            var baseText = _actionLabel.text ?? "思考中";
            var trimBase = baseText.TrimEnd('.');
            _actionLabel.text = trimBase + dots;
        }

        /// <summary>
        /// 停止动画并从父容器移除。
        /// </summary>
        public void Dismiss()
        {
            _dotAnim?.Pause();
            _dotAnim = null;
            parent?.Remove(this);
        }
    }
}
