using AgentCore.Editor.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI
{
    public partial class ChatWindow
    {
        #region 事件处理

        /// <summary>
        /// 处理 AgentLoop 派发的事件。
        /// 根据事件类型更新 UI 状态、追加流式文本、显示错误等。
        /// </summary>
        /// <param name="evt">Agent 事件</param>
        private void HandleAgentEvent(AgentEvent evt)
        {
            // 诊断日志：追踪工具调用相关事件
            if (evt.Type == AgentEventType.ToolCallStarted ||
                evt.Type == AgentEventType.ToolCallCompleted ||
                evt.Type == AgentEventType.ToolCallFailed ||
                evt.Type == AgentEventType.LoopRoundStarted ||
                evt.Type == AgentEventType.LoopCompleted)
            {
                Debug.Log($"[AgentCore.UI] HandleAgentEvent 收到事件: {evt.Type}, tool={evt.ToolName ?? "(none)"}, toolCallId={evt.ToolCallId ?? "(none)"}");
            }

            switch (evt.Type)
            {
                case AgentEventType.StateChanged:
                    UpdateUIState(evt.State);
                    break;

                case AgentEventType.StreamToken:
                    AppendStreamToken(evt.Content, evt.MessageId);
                    break;

                case AgentEventType.ReasoningToken:
                    AppendReasoningToken(evt.Content, evt.MessageId, evt.ReasoningSource);
                    break;

                case AgentEventType.ReasoningCompleted:
                    CompleteReasoning(evt.MessageId, evt.ExecutionTimeMs, evt.ReasoningSource);
                    break;

                case AgentEventType.AssistantMessage:
                    // pending 气泡不再需要，真实回复已到达
                    DismissPendingIndicator();
                    FinalizeAssistantMessage(evt.Content, evt.MessageId);
                    // 助手消息完成后，结束当前工具调用分组（下次工具调用创建新分组）
                    _currentToolCallGroup = null;
                    _currentAssistantTurnId = null;
                    // 消息完成后精准更新当前会话标题（避免重建整个列表导致排序跳动）
                    UpdateCurrentSessionTitle();
                    break;

                case AgentEventType.Error:
                    DismissPendingIndicator();
                    ShowError(evt.Content, evt.Detail);
                    break;

                // Phase 2: 工具调用事件
                case AgentEventType.ToolCallStarted:
                    HandleToolCallStarted(evt);
                    break;

                case AgentEventType.ToolCallCompleted:
                    HandleToolCallCompleted(evt);
                    break;

                case AgentEventType.ToolCallFailed:
                    HandleToolCallFailed(evt);
                    break;

                case AgentEventType.LoopRoundStarted:
                    HandleLoopRoundStarted(evt);
                    break;

                case AgentEventType.LoopCompleted:
                    // 循环结束，无需特殊 UI 处理
                    break;

                // Phase 4.5: 文件变更更新事件
                case AgentEventType.FileChangesUpdated:
                    _fileChangeSummaryPanel?.UpdateChanges(evt.FileChanges);
                    break;

                // Phase 9: Self-Challenge 事件
                case AgentEventType.IntentChallengeCompleted:
                case AgentEventType.AnswerChallengeCompleted:
                case AgentEventType.AnswerChallengeRegenerating:
                case AgentEventType.AnswerChallengeRegenerated:
                    HandleSelfChallengeEvent(evt);
                    break;
            }

            // Phase 6.0.4: 每次事件后更新上下文使用情况面板
            UpdateContextUsagePanel();
        }

        /// <summary>
        /// 根据 Agent 状态更新 UI 元素（状态标签、按钮可用性）。
        /// </summary>
        /// <param name="state">新的 Agent 状态</param>
        private void UpdateUIState(AgentState state)
        {
            switch (state)
            {
                case AgentState.Idle:
                    UpdateStatusLabel("就绪");
                    SetSendEnabled(true);
                    SetCancelVisible(false);
                    // Idle 表示本轮已彻底结束（正常/取消/错误），清理 pending 保险
                    DismissPendingIndicator();
                    break;

                case AgentState.Thinking:
                    UpdateStatusLabel("思考中...");
                    SetSendEnabled(false);
                    SetCancelVisible(true);
                    // 创建助手消息气泡占位（流式模式）
                    EnsureAssistantBubbleExists();
                    // 真实 turn view 已就绪，pending 完成使命
                    DismissPendingIndicator();
                    break;

                case AgentState.Streaming:
                    UpdateStatusLabel("回复中...");
                    SetSendEnabled(false);
                    SetCancelVisible(true);
                    break;

                case AgentState.WaitingForClarification:
                    UpdateStatusLabel("等待你的澄清...");
                    SetSendEnabled(true);
                    SetCancelVisible(false);
                    break;

                case AgentState.ReviewingAnswer:
                    // ADR: self-challenge-model-tier-escape §3.4 B1 — Node B 运行中
                    UpdateStatusLabel("审阅答案中...");
                    SetSendEnabled(false);
                    SetCancelVisible(true);
                    break;

                case AgentState.ExecutingTool:
                    UpdateStatusLabel("执行工具...");
                    SetSendEnabled(false);
                    SetCancelVisible(true);
                    break;

                case AgentState.Compressing:
                    UpdateStatusLabel("压缩上下文中...");
                    SetSendEnabled(false);
                    SetCancelVisible(true);
                    break;

                case AgentState.Error:
                    UpdateStatusLabel("错误", true);
                    DismissPendingIndicator();
                    break;
            }

            // pending 若仍存在（LLM 未开始 stream），根据状态同步文字
            SyncPendingIndicatorFromState(state);

            // Phase 6.0.4: 状态变更后更新上下文使用情况面板
            UpdateContextUsagePanel();
        }

        /// <summary>
        /// 更新上下文使用情况面板。
        /// Phase 6.0.4: 从 AgentLoop 获取最新的上下文预算信息并更新 UI。
        /// </summary>
        private void UpdateContextUsagePanel()
        {
            if (_contextUsagePanel == null || _agentLoop == null)
                return;

            try
            {
                var budget = _agentLoop.GetContextBudget();
                _contextUsagePanel.UpdateDisplay(budget);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[AgentCore.UI] Failed to update context usage panel: {ex.Message}");
            }
        }

        #endregion
    }
}
