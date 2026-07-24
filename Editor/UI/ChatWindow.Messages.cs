using System;
using System.Collections.Generic;
using AgentCore.Editor.Core;
using AgentCore.Editor.LLM;
using AgentCore.Editor.Session;
using AgentCore.Editor.UI.Components;
using AgentCore.Editor.Utils;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI
{
    public partial class ChatWindow
    {
        #region 消息 UI 管理

        /// <summary>
        /// 添加用户消息气泡到消息容器。
        /// </summary>
        /// <param name="content">用户消息内容</param>
        private void AddUserMessage(string content)
        {
            var messageId = Guid.NewGuid().ToString();
            var bubble = new MessageBubble(messageId, "user", content);
            _messageBubbles[messageId] = bubble;
            _messageListManager?.AddItem(bubble);
            ScrollToBottom(force: true); // 新消息添加，强制滚动到底部
        }

        /// <summary>
        /// 确保当前存在一个助手消息气泡（流式模式）。
        /// 在 Thinking 状态时预创建，以便后续 StreamToken 事件可以追加文本。
        /// </summary>
        private void EnsureAssistantBubbleExists()
        {
            // 查找最新的助手对话轮次
            if (_agentLoop == null) return;

            var history = _agentLoop.ConversationHistory;
            for (int i = history.Count - 1; i >= 0; i--)
            {
                var turn = history[i];
                if (turn.Role == "assistant" && turn.IsStreaming)
                {
                    _currentAssistantTurnId = turn.Id;
                    if (!_messageBubbles.ContainsKey(turn.Id))
                    {
                        AddAssistantMessageBubble(turn.Id);
                    }
                    return;
                }
            }
        }

        /// <summary>
        /// 创建助手消息气泡（流式模式）并添加到消息容器。
        /// </summary>
        /// <param name="messageId">消息唯一标识</param>
        private void AddAssistantMessageBubble(string messageId)
        {
            var turnView = EnsureAssistantTurnView(messageId);
            var bubble = turnView.EnsureBubble(messageId, "", isStreaming: true);
            _messageBubbles[messageId] = bubble;
            _currentAssistantTurnId = messageId;
            ScrollToBottom(force: true); // 新消息气泡添加，强制滚动到底部
        }

        /// <summary>
        /// 确保 assistant turn 视图容器存在。
        /// </summary>
        /// <param name="messageId">assistant turn ID。</param>
        /// <returns>assistant turn 视图。</returns>
        private AssistantTurnView EnsureAssistantTurnView(string messageId)
        {
            if (string.IsNullOrEmpty(messageId)) return null;

            if (_assistantTurnViews.TryGetValue(messageId, out var existing))
                return existing;

            var turnView = new AssistantTurnView(messageId);
            _assistantTurnViews[messageId] = turnView;
            _messageListManager?.AddItem(turnView);
            return turnView;
        }

        /// <summary>
        /// 追加 reasoning token 到对应 assistant turn 的 ThinkingDrawer。
        /// </summary>
        /// <param name="token">reasoning token。</param>
        /// <param name="messageId">assistant turn ID。</param>
        /// <param name="source">reasoning 来源。</param>
        private void AppendReasoningToken(string token, string messageId, ThinkingTraceSource source)
        {
            if (string.IsNullOrEmpty(messageId) || string.IsNullOrEmpty(token)) return;

            var turnView = EnsureAssistantTurnView(messageId);
            turnView?.ThinkingDrawer.AppendReasoning(token, source);
            _currentAssistantTurnId = messageId;
            // v1.6.5: ScrollToBottom 由 flush 的 ScheduleReasoningFlush 间接触发，
            // 不在 per-token 路径调用 — 100ms 节流已足够，但函数调用本身有开销
        }

        /// <summary>
        /// 标记 ThinkingDrawer 完成。
        /// </summary>
        /// <param name="messageId">assistant turn ID。</param>
        /// <param name="durationMs">累计耗时毫秒。</param>
        /// <param name="source">reasoning 来源。</param>
        private void CompleteReasoning(string messageId, double durationMs, ThinkingTraceSource source)
        {
            if (string.IsNullOrEmpty(messageId)) return;

            if (_assistantTurnViews.TryGetValue(messageId, out var turnView))
            {
                turnView.ThinkingDrawer.Complete(durationMs, source);
            }
        }

        /// <summary>
        /// 追加流式 token 到对应的助手消息气泡。
        /// 如果气泡尚不存在，会自动创建。
        /// </summary>
        /// <param name="token">token 文本</param>
        /// <param name="messageId">消息唯一标识</param>
        private void AppendStreamToken(string token, string messageId)
        {
            if (string.IsNullOrEmpty(messageId)) return;

            // 如果气泡不存在，创建一个
            if (!_messageBubbles.TryGetValue(messageId, out var bubble))
            {
                AddAssistantMessageBubble(messageId);
                bubble = _messageBubbles[messageId];
            }

            bubble.AppendStreamToken(token);
            // v1.6.5: ScrollToBottom 移到 StreamingTextElement.FlushPending 的帧节流里，
            // 不在 per-token 路径调用
        }

        /// <summary>
        /// 最终化助手消息内容。
        /// 流式输出完成后调用，设置完整文本并结束流式模式。
        /// </summary>
        /// <param name="fullContent">完整的消息内容</param>
        /// <param name="messageId">消息唯一标识</param>
        private void FinalizeAssistantMessage(string fullContent, string messageId)
        {
            if (string.IsNullOrEmpty(messageId)) return;

            // reasoning-only 空正文回复：GLM 把 max_tokens 全用在 reasoning、没输出正文时，
            // 移除空的正文气泡壳，避免留下视觉上空白的气泡。ThinkingDrawer 是 AssistantTurnView 的
            // 独立子元素，会按自身 reasoning 是否为空自动显示/隐藏，不受此处影响（有思考仍可展开查看）。
            if (string.IsNullOrWhiteSpace(fullContent))
            {
                if (_assistantTurnViews.TryGetValue(messageId, out var emptyTurnView))
                {
                    emptyTurnView.RemoveBubble();
                }
                _messageBubbles.Remove(messageId);
                return;
            }

            if (_messageBubbles.TryGetValue(messageId, out var bubble))
            {
                bubble.FinalizeContent(fullContent);
            }
        }

        /// <summary>
        /// 显示错误消息气泡。
        /// 如果有最后一条用户消息且 Agent 处于 Idle 状态，会显示重试按钮。
        /// 当携带 <see cref="ErrorDetail"/> 时，显示结构化的详细错误信息。
        /// </summary>
        /// <param name="errorMessage">错误信息</param>
        /// <param name="detail">结构化错误详情（可选）</param>
        private void ShowError(string errorMessage, ErrorDetail detail = null)
        {
            var messageId = Guid.NewGuid().ToString();

            // 使用 ErrorDetail 格式化显示内容（如果有）
            var displayMessage = detail != null
                ? detail.FormatForDisplay()
                : (errorMessage ?? AgentCore.Editor.L10n.Loc.Tr("chat.error.unknown", "未知错误"));

            var bubble = new MessageBubble(messageId, "error", displayMessage);
            _messageBubbles[messageId] = bubble;
            _messageListManager?.AddItem(bubble);

            // 如果有堆栈信息，添加可展开的详情区域
            if (detail != null)
            {
                var stackInfo = detail.GetStackForDisplay();
                if (!string.IsNullOrEmpty(stackInfo))
                {
                    bubble.AddExpandableDetail(AgentCore.Editor.L10n.Loc.Tr("chat.error.stackInfo", "堆栈信息"), stackInfo);
                }
            }

            // 如果有最后一条用户消息，添加重试按钮
            if (!string.IsNullOrEmpty(_lastUserMessage))
            {
                var retryMessage = _lastUserMessage;
                bubble.AddRetryButton(() => RetryLastMessage(retryMessage));
            }

            ScrollToBottom(force: true); // 错误消息添加，强制滚动到底部
        }

        /// <summary>
        /// 重试最后一条用户消息。
        /// 从错误气泡的重试按钮触发，重新发送之前失败的消息。
        /// </summary>
        /// <param name="message">要重试的消息文本</param>
        private void RetryLastMessage(string message)
        {
            if (_agentLoop == null)
            {
                AgentCoreLog.Error("[AgentCore] AgentLoop is not initialized, cannot retry.");
                return;
            }

            if (_agentLoop.CurrentState != AgentState.Idle)
            {
                AgentCoreLog.Warning("[AgentCore] Cannot retry while agent is busy.");
                return;
            }

            if (string.IsNullOrEmpty(message))
            {
                AgentCoreLog.Warning("[AgentCore] No message to retry.");
                return;
            }

            // 更新状态标签
            UpdateStatusLabel(AgentCore.Editor.L10n.Loc.Tr("chat.status.retrying", "重试中..."));

            // 添加用户消息气泡（显示重试标记）
            AddUserMessage(AgentCore.Editor.L10n.Loc.Tr("chat.error.retryPrefix", "[重试] {0}", message));

            // 异步发送消息
            AsyncHelper.RunAsync(
                () => _agentLoop.SendMessageAsync(message),
                onError: ex => AgentCoreLog.Error($"[AgentCore] Retry error: {ex.Message}")
            );
        }

        /// <summary>
        /// 清空所有消息气泡。
        /// </summary>
        private void ClearMessages()
        {
            _messageListManager?.Clear();
            _messageBubbles.Clear();
            _assistantTurnViews.Clear();
            _activeToolCards.Clear();
            _currentToolCallGroup = null;
            _currentAssistantTurnId = null;
            _toolCallCounter = 0;

            // Phase 4.5: 清空文件变更面板
            _fileChangeSummaryPanel?.ClearAndHide();
        }

        /// <summary>
        /// 从 AgentLoop 的 ConversationHistory 重建所有消息气泡。
        /// 用于会话恢复时重建 UI。
        /// </summary>
        private void RebuildMessageBubbles()
        {
            if (_agentLoop == null || _messageContainer == null)
            {
                AgentCoreLog.Warning($"[AgentCore.UI] RebuildMessageBubbles 中止: _agentLoop={(_agentLoop != null ? "OK" : "null")}, _messageContainer={(_messageContainer != null ? "OK" : "null")}");
                return;
            }

            // 清空现有 UI
            _messageListManager?.Clear();
            _messageBubbles.Clear();
            _assistantTurnViews.Clear();
            _activeToolCards.Clear();

            var history = _agentLoop.ConversationHistory;
            AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore.UI] RebuildMessageBubbles: 历史记录共 {history?.Count ?? 0} 条");
            ToolCallGroup restoreGroup = null;

            for (int i = 0; i < history.Count; i++)
            {
                var turn = history[i];

                if (turn.Role == "user")
                {
                    // 用户消息前，结束上一个工具调用分组
                    restoreGroup = null;

                    // 用户消息气泡
                    var bubble = new MessageBubble(turn.Id, "user", turn.Content);
                    _messageBubbles[turn.Id] = bubble;
                    _messageListManager?.AddItem(bubble);
                }
                else if (turn.Role == "assistant")
                {
                    // 助手消息使用固定 turn 容器：ThinkingDrawer -> SelfChallengeCard -> ToolCallGroup -> MessageBubble
                    var turnView = EnsureAssistantTurnView(turn.Id);
                    turnView.RestoreThinking(turn);
                    var bubble = turnView.EnsureBubble(turn.Id, turn.Content, isStreaming: false);
                    _messageBubbles[turn.Id] = bubble;

                    // 恢复 Self-Challenge 卡片（v1.5.0-alpha2）
                    // 兼容性：v1.4.x 及以前的会话 turn.SelfChallenge == null，直接跳过不渲染卡片。
                    if (turn.SelfChallenge != null)
                    {
                        var scCard = turnView.EnsureSelfChallengeCard(turn.Id);
                        scCard.SetData(turn.SelfChallenge);
                    }

                    // 恢复工具调用卡片（统一放入分组容器）
                    if (turn.ToolCalls != null && turn.ToolCalls.Count > 0)
                    {
                        AgentCore.Editor.Utils.AgentCoreLog.Debug($"[AgentCore.UI] RebuildMessageBubbles: 恢复 {turn.ToolCalls.Count} 个工具调用 (turn={turn.Id})");
                        restoreGroup = new ToolCallGroup();

                        foreach (var tc in turn.ToolCalls)
                        {
                            var card = new ToolCallCard(tc.ToolName, tc.Arguments);
                            var status = tc.Success ? ToolCallStatus.Completed : ToolCallStatus.Failed;
                            var statusText = tc.Success
                                ? $"完成 ({tc.ExecutionTimeMs:F0}ms)"
                                : "失败";
                            card.SetStatus(status, statusText);

                            if (!string.IsNullOrEmpty(tc.Result))
                            {
                                // v1.4.8：完整保留原始结果，不再做 200 字符截断。
                                // 会话恢复时同样让用户能查看完整详情并复制。
                                card.SetDetails(tc.Result);
                            }

                            restoreGroup.AddToolCard(card);
                        }

                        // 历史工具调用全部完成，通知分组更新统计并折叠
                        restoreGroup.NotifyToolStatusChanged();
                        turnView.SetToolGroup(restoreGroup);
                        AgentCore.Editor.Utils.AgentCoreLog.Debug($"[AgentCore.UI] RebuildMessageBubbles: ToolCallGroup 已恢复到 AssistantTurnView");

                        // 助手消息后结束分组
                        restoreGroup = null;
                    }
                }
            }

            // 清除临时分组引用
            _currentToolCallGroup = null;
            _currentAssistantTurnId = null;

            // 滚动到底部（RebuildMessageBubbles 完成后强制滚动）
            ScrollToBottom(force: true);
        }

        /// <summary>
        /// 滚动消息列表到底部。
        /// <para>
        /// 普通调用（流式 token）有 100ms 节流，避免高频调用。
        /// force=true 时跳过节流，并延迟两帧执行，确保 DOM 布局更新完成后再滚动。
        /// </para>
        /// </summary>
        // P2: 节流滚动，避免流式输出时频繁调用
        private double _lastScrollTime = 0;
        
        private void ScrollToBottom(bool force = false)
        {
            // 用户手动上翻后禁止自动追底（除非 force 传入,如"跳到最新"按钮或新用户消息）
            if (!force && _userScrolledUp)
            {
                UpdateScrollToBottomButtonVisibility();
                return;
            }

            if (!force)
            {
                var now = EditorApplication.timeSinceStartup;
                if (now - _lastScrollTime < 0.1) // 100ms 节流
                    return;
                _lastScrollTime = now;
            }

            if (force)
            {
                _userScrolledUp = false;
                UpdateScrollToBottomButtonVisibility();
                // force 模式：延迟两帧，确保 DOM 布局（包括 MessageListManager 的 DOM 变更）完成后再滚动
                _messageScrollView?.schedule.Execute(() =>
                {
                    _messageScrollView?.schedule.Execute(() =>
                    {
                        if (_messageScrollView != null)
                            _messageScrollView.scrollOffset = new Vector2(0, float.MaxValue);
                    });
                });
            }
            else
            {
                _messageScrollView?.schedule.Execute(() =>
                {
                    if (_messageScrollView != null)
                        _messageScrollView.scrollOffset = new Vector2(0, float.MaxValue);
                });
            }
        }

        // ========== 用户上翻检测 & 跳到最新按钮 ==========

        /// <summary>
        /// 判断消息滚动区当前是否处于"接近底部"状态。
        /// 阈值：距底部 40px 以内视为在底部。
        /// </summary>
        private bool IsScrollAtBottom()
        {
            if (_messageScrollView == null) return true;
            var scroller = _messageScrollView.verticalScroller;
            if (scroller == null) return true;
            // 高值 = 最底部
            return scroller.value >= scroller.highValue - 40f;
        }

        /// <summary>
        /// 检查用户是否已手动上翻，同步 _userScrolledUp + 更新按钮可见性。
        /// </summary>
        /// <param name="force">true 时不管 flag 强制刷新（用户滚滚轮时用）</param>
        private void CheckUserScrolled(bool force = false)
        {
            var atBottom = IsScrollAtBottom();
            if (force || _userScrolledUp != !atBottom)
            {
                _userScrolledUp = !atBottom;
                UpdateScrollToBottomButtonVisibility();
            }
        }

        /// <summary>
        /// ScrollView 值变化的持续回调（滚动条被拖动 / 滚轮）
        /// </summary>
        private void OnMessageScrollValueChanged(float _)
        {
            CheckUserScrolled();
        }

        /// <summary>
        /// 更新"跳到最新"浮动按钮的可见性。
        /// </summary>
        private void UpdateScrollToBottomButtonVisibility()
        {
            if (_scrollToBottomButton == null) return;
            _scrollToBottomButton.style.display = _userScrolledUp ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>
        /// "跳到最新"按钮点击 — 重置追底 + 强制滚到底。
        /// </summary>
        private void OnScrollToBottomClicked()
        {
            ScrollToBottom(force: true);
        }

        #endregion
    }
}
