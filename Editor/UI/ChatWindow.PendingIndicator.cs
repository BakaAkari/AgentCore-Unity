using AgentCore.Editor.Core;
using AgentCore.Editor.UI.Components;

namespace AgentCore.Editor.UI
{
    /// <summary>
    /// ChatWindow 分部类 — Pending Indicator（用户发送后、真实 assistant turn 出现前的占位气泡）。
    /// </summary>
    public partial class ChatWindow
    {
        /// <summary>
        /// 在消息列表末尾显示 pending 占位气泡。
        /// 由 <see cref="OnSendClicked"/> 在异步发送前调用；重复调用会更新已有的 pending 文本而不新建。
        /// </summary>
        /// <param name="initialText">初始动作描述（不含末尾省略号，由 PendingIndicator 内部动画补上）。</param>
        private void ShowPendingIndicator(string initialText)
        {
            if (_pendingIndicator == null)
            {
                _pendingIndicator = new PendingIndicator();
                _messageListManager?.AddItem(_pendingIndicator);
            }

            _pendingIndicator.SetActionText(initialText ?? "思考中");
            ScrollToBottom(force: true);
        }

        /// <summary>
        /// 更新 pending 气泡的动作文本（无则忽略）。
        /// 由 <see cref="UpdateUIState"/> 在 Agent 状态变化时调用。
        /// </summary>
        private void UpdatePendingIndicatorAction(string text)
        {
            _pendingIndicator?.SetActionText(text);
        }

        /// <summary>
        /// 从消息列表移除 pending 气泡（如果存在）。
        /// 由 <see cref="HandleAgentEvent"/> 在真实 assistant turn 出现或错误时调用。
        /// </summary>
        private void DismissPendingIndicator()
        {
            if (_pendingIndicator == null) return;
            _pendingIndicator.Dismiss();
            _pendingIndicator = null;
        }

        /// <summary>
        /// 根据 Agent 状态映射到 pending 气泡的动作文本。
        /// 只在 pending 存在时更新（真实 turn 出现后 pending 已消失，无需更新）。
        /// </summary>
        private void SyncPendingIndicatorFromState(AgentState state)
        {
            if (_pendingIndicator == null) return;

            switch (state)
            {
                case AgentState.Thinking:
                    UpdatePendingIndicatorAction("思考中");
                    break;
                case AgentState.ExecutingTool:
                    UpdatePendingIndicatorAction("调用工具中");
                    break;
                case AgentState.Streaming:
                    UpdatePendingIndicatorAction("回复中");
                    break;
                case AgentState.Compressing:
                    UpdatePendingIndicatorAction("压缩上下文");
                    break;
                case AgentState.ReviewingAnswer:
                    UpdatePendingIndicatorAction("审阅答案");
                    break;
                case AgentState.WaitingForClarification:
                case AgentState.Idle:
                case AgentState.Error:
                    // 这三个状态下 pending 应该已经被 event handler 清理，不再更新
                    break;
            }
        }
    }
}
