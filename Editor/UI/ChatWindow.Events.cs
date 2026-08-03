using AgentCore.Editor.Core;
using UnityEngine;
using UnityEngine.UIElements;
using AgentCore.Editor.Utils;

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
            using var _hae = AgentCore.Editor.Utils.AgentCoreProfilerMarkers.UIHandleAgentEvent.Auto();

            // 诊断日志：追踪工具调用相关事件
            if (evt.Type == AgentEventType.ToolCallStarted ||
                evt.Type == AgentEventType.ToolCallCompleted ||
                evt.Type == AgentEventType.ToolCallFailed ||
                evt.Type == AgentEventType.LoopRoundStarted ||
                evt.Type == AgentEventType.LoopCompleted)
            {
                AgentCore.Editor.Utils.AgentCoreLog.Debug($"[AgentCore.UI] HandleAgentEvent 收到事件: {evt.Type}, tool={evt.ToolName ?? "(none)"}, toolCallId={evt.ToolCallId ?? "(none)"}");
            }

            switch (evt.Type)
            {
                case AgentEventType.StateChanged:
                    UpdateUIState(evt.State);
                    break;

                case AgentEventType.StreamToken:
                    AppendStreamToken(evt.Content, evt.MessageId);
                    _agentStatusLine?.Tick();
                    ThrottledScrollToBottom();
                    break;

                case AgentEventType.ReasoningToken:
                    AppendReasoningToken(evt.Content, evt.MessageId, evt.ReasoningSource);
                    _agentStatusLine?.Tick();
                    ThrottledScrollToBottom();
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

                // v1.14.5: 工具调用参数流式接收进度心跳（节流后，非每 delta 一次）。
                // 覆盖"模型决定调用工具 → 参数 JSON 到达 → ToolCallStarted 真正开始执行"
                // 之间此前完全无 UI 反馈的空窗期。
                case AgentEventType.ToolCallProgress:
                    HandleToolCallProgress(evt);
                    _agentStatusLine?.Tick();
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

            // Phase 6.0.4 / perf: 更新上下文使用情况面板。
            // 仅在"可能改变已入历史 token 预算"的事件后刷新，而非每个事件都刷。
            // StreamToken / ReasoningToken 是高频事件（AI 每吐一个字触发一次），
            // 但流式吐字期间消息尚未入历史、预算不变，且 GetContextBudget() 每次调用会
            // O(N) 遍历整个消息历史逐条估算 token —— 挂在 per-token 路径上会造成
            // O(token 数 × 消息数) 的重复无效计算。故这里用白名单排除高频事件。
            if (EventAffectsContextBudget(evt.Type))
            {
                UpdateContextUsagePanel();
            }
        }

        /// <summary>
        /// 判断某个 Agent 事件是否可能改变上下文 token 预算（即已进入消息历史的内容）。
        /// <para>
        /// 只有会向 ConversationHistory 增删内容、或触发压缩/状态切换的事件才影响预算。
        /// 流式 token 事件（StreamToken/ReasoningToken/ReasoningCompleted）在吐字期间
        /// 不改变已入历史的 token，排除在外，避免高频遍历。
        /// </para>
        /// </summary>
        private static bool EventAffectsContextBudget(AgentEventType type)
        {
            switch (type)
            {
                case AgentEventType.StateChanged:        // 状态切换（含 Compressing→Idle，压缩后预算变化）
                case AgentEventType.AssistantMessage:    // 助手回复入历史
                case AgentEventType.ToolCallCompleted:   // 工具结果入历史
                case AgentEventType.ToolCallFailed:      // 失败结果入历史
                case AgentEventType.Error:               // 错误可能改变历史/状态
                    return true;
                default:
                    return false;
            }
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
                    UpdateStatusLabel(AgentCore.Editor.L10n.Loc.Tr("chat.status.idle", "就绪"), isError: false, isActive: false);
                    SetSendEnabled(true);
                    SetCancelVisible(false);
                    // Idle 表示本轮已彻底结束（正常/取消/错误），清理 pending 保险
                    DismissPendingIndicator();
                    break;

                case AgentState.Thinking:
                    UpdateStatusLabel(AgentCore.Editor.L10n.Loc.Tr("chat.status.thinking", "思考中..."));
                    SetSendEnabled(false);
                    SetCancelVisible(true);
                    // 创建助手消息气泡占位（流式模式）
                    EnsureAssistantBubbleExists();
                    // 真实 turn view 已就绪，pending 完成使命
                    DismissPendingIndicator();
                    break;

                case AgentState.Streaming:
                    UpdateStatusLabel(AgentCore.Editor.L10n.Loc.Tr("chat.status.streaming", "回复中..."));
                    SetSendEnabled(false);
                    SetCancelVisible(true);
                    break;

                case AgentState.WaitingForClarification:
                    UpdateStatusLabel(AgentCore.Editor.L10n.Loc.Tr("chat.status.waitingClarification", "等待你的澄清..."), isError: false, isActive: false);
                    SetSendEnabled(true);
                    SetCancelVisible(false);
                    break;

                case AgentState.ReviewingAnswer:
                    // ADR: self-challenge-model-tier-escape §3.4 B1 — Node B 运行中
                    UpdateStatusLabel(AgentCore.Editor.L10n.Loc.Tr("chat.status.reviewingAnswer", "审阅答案中..."));
                    SetSendEnabled(false);
                    SetCancelVisible(true);
                    break;

                case AgentState.ExecutingTool:
                    UpdateStatusLabel(AgentCore.Editor.L10n.Loc.Tr("chat.status.executingTool", "执行工具..."));
                    SetSendEnabled(false);
                    SetCancelVisible(true);
                    break;

                case AgentState.Compressing:
                    UpdateStatusLabel(AgentCore.Editor.L10n.Loc.Tr("chat.status.compressing", "压缩上下文中..."));
                    SetSendEnabled(false);
                    SetCancelVisible(true);
                    break;

                case AgentState.Error:
                    UpdateStatusLabel(AgentCore.Editor.L10n.Loc.Tr("chat.status.error", "错误"), true);
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

            using var _ucp = AgentCore.Editor.Utils.AgentCoreProfilerMarkers.UIUpdateContextPanel.Auto();

            try
            {
                var budget = _agentLoop.GetContextBudget();
                _contextUsagePanel.UpdateDisplay(budget);
            }
            catch (System.Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore.UI] Failed to update context usage panel: {ex.Message}");
            }
        }

        // v1.6.5: 帧节流滚动 — 每 100ms 最多一次，不阻塞 per-token 路径
        private bool _scrollScheduled;
        private void ThrottledScrollToBottom()
        {
            if (_scrollScheduled) return;
            _scrollScheduled = true;
            _messageScrollView?.schedule.Execute(() =>
            {
                _scrollScheduled = false;
                ScrollToBottom();
            }).StartingIn(100);
        }

        #endregion
    }
}
