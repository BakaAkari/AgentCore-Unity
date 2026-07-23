using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Config;
using AgentCore.Editor.Core.Compression;
using AgentCore.Editor.LLM;
using AgentCore.Editor.Tools;
using UnityEngine;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Core
{
    public partial class AgentLoop
    {
        /// <summary>
        /// 调用 LLM 流式接口并返回完整的 assistant 消息。
        /// <para>
        /// 从 Phase 1 的内联逻辑提取为独立方法，支持工具定义参数。
        /// 处理流式回调中的 ContentToken、ToolCallDelta、Done 和 Error 事件。
        /// </para>
        /// </summary>
        /// <param name="assistantTurn">当前助手对话轮次（用于流式内容追加）</param>
        /// <param name="tools">工具定义列表（可为 null 或空列表表示不使用工具）</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>完整的 assistant ChatMessage（可能包含 tool_calls）</returns>
        private async Task<ChatMessage> CallLLMStreamAsync(
            ConversationTurn assistantTurn,
            List<ToolDefinition> tools,
            CancellationToken ct)
        {
            // Phase 3: 上下文窗口截断
            // 创建 _messages 的浅拷贝，对拷贝进行截断，不修改原始列表（保留完整历史用于 UI 显示）
            var settings = AgentCoreSettings.instance;

            // v1.6.5+: 每次 LLM 调用前自适应调整参数（确保与 ModelCapabilityProbe 探测值同步）
            settings.ApplyAdaptiveDefaults();

            int maxTokens = settings.maxContextTokens > 0
                ? settings.maxContextTokens
                : ContextWindowManager.GetModelMaxTokens(settings.llmModel);
            int reserveTokens = settings.reserveResponseTokens;

            // Phase 5: 在 TrimToFit 之前尝试对话压缩（智能压缩优先于暴力截断）
            if (_conversationCompressor != null && settings.compressionEnabled)
            {
                // 预检查 token 使用率，仅在超过阈值时才显示压缩状态（避免每次 LLM 调用都闪烁状态）
                int currentTokens = TokenCounter.EstimateConversationTokens(_messages);
                float usageRatio = (float)currentTokens / maxTokens;

                if (usageRatio >= settings.conversationCompressionTrigger)
                {
                    try
                    {
                        SetState(AgentState.Compressing);
                        bool compressed = await _conversationCompressor.CompressIfNeededAsync(_messages, maxTokens, ct);
                        if (compressed)
                        {
                            AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] Conversation compression completed successfully.");
                        }
                    }
                    catch (System.Exception ex)
                    {
                        AgentCoreLog.Warning($"[AgentCore] Conversation compression failed (non-fatal): {ex.Message}");
                    }
                }
            }

            var messagesSnapshot = ContextWindowManager.TrimToFit(
                _messages, maxTokens, reserveTokens);

            // 每次 LLM 调用重新识别可见规划 trace；reasoning 内容保留在同一个 assistant turn 中追加。
            _visiblePlanningTraceExtractor.Reset();

            // Phase 9: 每次 LLM 调用前重置 SelfChallenge extractors (Node A / Node B stream 抽取器)
            ResetSelfChallengeExtractorsForNewRound();

            // 切换到 Streaming 状态
            SetState(AgentState.Streaming);

            // 传递有效的工具列表（空列表时传 null，避免 API 报错）
            var effectiveTools = (tools != null && tools.Count > 0) ? tools : null;

            // Phase 2 Step 11: 使用 FallbackRouter 包装 LLM 调用，支持自动重试
            var assistantMessage = await _fallbackRouter.ExecuteStreamWithRetryAsync(
                _llmClient,
                messagesSnapshot,
                chunk => OnStreamChunkReceived(chunk, assistantTurn, ct),
                tools: effectiveTools,
                ct: ct,
                onStatusUpdate: status => EmitEvent(AgentEvent.ErrorEvent($"[Retry] {status}"))
            );

            // Phase 9: 如果 Node A 结构校验失败, 触发独立小会话 correction retry (v0.9 §11.5)
            if (_pendingNodeAValidationIssues != null && _pendingNodeAValidationIssues.Count > 0)
            {
                var lastUser = _messages.LastOrDefault(m => m.Role == "user")?.Content ?? string.Empty;
                var issuesSnapshot = _pendingNodeAValidationIssues;
                _pendingNodeAValidationIssues = null;
                await TryNodeACorrectionRetryAsync(lastUser, assistantTurn, issuesSnapshot, ct);
            }

            // v1.6.5+: 空内容检测 — GLM-5.2 reasoning 吃光 maxTokens 时 content 为空
            // 流式路径不能在 FallbackRouter 中重试（reasoning chunks 已发送到 UI）
            // 这里检测空内容并记录警告，上层 HandleFinalResponse 会显示 fallback 消息
            if (assistantMessage != null && string.IsNullOrEmpty(assistantMessage.Content))
            {
                AgentCore.Editor.Utils.AgentCoreLog.Warning(
                    "[AgentCore] LLM returned empty content. " +
                    "Reasoning may have consumed the entire max_tokens budget. " +
                    "Consider increasing maxTokens or reducing reasoningMaxTokens.");
            }

            return assistantMessage;
        }

        /// <summary>
        /// 处理 LLM 流式回调中的单个 chunk。
        /// 此方法可能在后台线程被调用，通过 <see cref="EmitEvent"/> 确保事件在主线程派发。
        /// </summary>
        /// <param name="chunk">流式 chunk 数据</param>
        /// <param name="assistantTurn">当前助手对话轮次</param>
        /// <param name="ct">取消令牌</param>
        private void OnStreamChunkReceived(StreamChunk chunk, ConversationTurn assistantTurn, CancellationToken ct)
        {
            // 检查取消
            if (ct.IsCancellationRequested)
            {
                return;
            }

            switch (chunk.Type)
            {
                case StreamChunkType.ContentToken:
                    HandleContentToken(chunk.Content, assistantTurn);
                    break;

                case StreamChunkType.ReasoningToken:
                    AppendReasoningToken(chunk.ReasoningContent, assistantTurn, ThinkingTraceSource.StructuredReasoning);
                    break;

                case StreamChunkType.Done:
                    CompleteReasoningIfNeeded(assistantTurn);
                    // v1.8.5: UI 逐字流式改批量 flush — 完成时一次性 emit 累积的内容,
                    // 消除主线程 UnitySynchronizationContext.ExecuteTasks 每帧 200+ ms 阻塞.
                    // HTTP stream / tool call / reasoning 仍在后台正常流式解析, 只有 UI 显示延迟.
                    FlushAccumulatedContentIfAny(assistantTurn);
                    // 流式完成，由 SendMessageAsync 的后续逻辑处理
                    AgentCore.Editor.Utils.AgentCoreLog.Debug($"[AgentCore] Stream completed. Finish reason: {chunk.FinishReason}");
                    break;

                case StreamChunkType.Error:
                    // 流式过程中的解析错误
                    // v1.8.5: 出错也 flush 已累积内容, 避免用户丢失部分回复
                    FlushAccumulatedContentIfAny(assistantTurn);
                    AgentCoreLog.Error($"[AgentCore] Stream error: {chunk.Error}");
                    EmitEvent(AgentEvent.ErrorEvent(chunk.Error));
                    break;

                case StreamChunkType.ToolCallDelta:
                    CompleteReasoningIfNeeded(assistantTurn);
                    // Phase 2：ToolCallDelta 由 OpenAICompatibleClient 内部累积，
                    // 最终通过 Done 事件返回完整的 tool_calls 列表。
                    // 此处仅记录日志用于调试。
                    AgentCore.Editor.Utils.AgentCoreLog.Debug($"[AgentCore] Received ToolCallDelta: {chunk.ToolCallDelta?.Function?.Name ?? "(accumulating)"}");
                    break;
            }
        }

        /// <summary>
        /// 处理普通 content token，并抽取开头的可见规划 trace。
        /// </summary>
        /// <param name="content">普通 content token。</param>
        /// <param name="assistantTurn">当前助手对话轮次。</param>
        private void HandleContentToken(string content, ConversationTurn assistantTurn)
        {
            if (string.IsNullOrEmpty(content) || assistantTurn == null)
                return;

            // Phase 9: 先过 SelfChallenge extractors (Node A / Node B), 剥离 challenge 块后再送 VisiblePlanningTraceExtractor
            string filtered = ProcessTokenThroughSelfChallengeExtractors(content, assistantTurn);
            if (string.IsNullOrEmpty(filtered)) return;

            var delta = _visiblePlanningTraceExtractor.Append(filtered);
            assistantTurn.PlanningTraceState = delta.State;

            if (!string.IsNullOrEmpty(delta.ReasoningContent))
            {
                AppendReasoningToken(delta.ReasoningContent, assistantTurn, ThinkingTraceSource.VisiblePlanningTrace);
            }

            if (!string.IsNullOrEmpty(delta.VisibleContent))
            {
                CompleteReasoningIfNeeded(assistantTurn);
                assistantTurn.Content += delta.VisibleContent;
                // v1.8.5: 不再每 token emit StreamToken 事件. 累积到 _pendingStreamContent,
                // 由 StreamChunkType.Done 时 FlushAccumulatedContentIfAny 一次性 emit.
                // 消除主线程 200+ ms/帧 阻塞. 副作用: UI 不再逐字显示, 完成时一次性显示.
                if (_pendingStreamContent == null)
                    _pendingStreamContent = new StringBuilder();
                _pendingStreamContent.Append(delta.VisibleContent);
            }
        }

        /// <summary>
        /// v1.8.5: 一次性 flush 累积的 stream content 到 UI (StreamChunkType.Done 时调用).
        /// </summary>
        private void FlushAccumulatedContentIfAny(ConversationTurn assistantTurn)
        {
            if (_pendingStreamContent == null || _pendingStreamContent.Length == 0)
            {
                return;
            }
            var fullContent = _pendingStreamContent.ToString();
            _pendingStreamContent.Clear();
            if (assistantTurn != null)
            {
                EmitEvent(AgentEvent.StreamToken(fullContent, assistantTurn.Id));
            }
        }

        /// <summary>
        /// v1.8.5: 累积 stream 期的 UI content, 完成时一次性 emit.
        /// </summary>
        private StringBuilder _pendingStreamContent;

        /// <summary>
        /// 追加 reasoning / planning trace token 到当前 assistant turn。
        /// </summary>
        /// <param name="token">reasoning token。</param>
        /// <param name="assistantTurn">当前助手对话轮次。</param>
        /// <param name="source">reasoning 来源。</param>
        private void AppendReasoningToken(string token, ConversationTurn assistantTurn, ThinkingTraceSource source)
        {
            if (string.IsNullOrEmpty(token) || assistantTurn == null)
                return;

            BeginReasoningIfNeeded(assistantTurn, source);
            assistantTurn.Reasoning += token;
            EmitEvent(AgentEvent.ReasoningToken(token, assistantTurn.Id, assistantTurn.ReasoningSource));
        }

        /// <summary>
        /// 标记当前 assistant turn 开始接收 reasoning / planning trace。
        /// </summary>
        /// <param name="assistantTurn">当前助手对话轮次。</param>
        /// <param name="source">reasoning 来源。</param>
        private void BeginReasoningIfNeeded(ConversationTurn assistantTurn, ThinkingTraceSource source)
        {
            if (!_reasoningActive)
            {
                _reasoningStartedUtc = DateTime.UtcNow;
                _reasoningActive = true;
                _reasoningCompleted = false;
            }

            _activeReasoningSource = MergeReasoningSource(_activeReasoningSource, source);
            assistantTurn.ReasoningSource = MergeReasoningSource(assistantTurn.ReasoningSource, source);
        }

        /// <summary>
        /// 完成当前 assistant turn 的 reasoning 计时并发送完成事件。
        /// </summary>
        /// <param name="assistantTurn">当前助手对话轮次。</param>
        private void CompleteReasoningIfNeeded(ConversationTurn assistantTurn)
        {
            if (assistantTurn == null || !_reasoningActive || _reasoningCompleted)
                return;

            var started = _reasoningStartedUtc ?? DateTime.UtcNow;
            var elapsedMs = Math.Max(0, (DateTime.UtcNow - started).TotalMilliseconds);
            assistantTurn.ReasoningDurationMs += elapsedMs;
            _reasoningActive = false;
            _reasoningCompleted = true;
            _reasoningStartedUtc = null;
            EmitEvent(AgentEvent.ReasoningCompleted(assistantTurn.Id, assistantTurn.ReasoningDurationMs, assistantTurn.ReasoningSource));
        }

        /// <summary>
        /// 合并 reasoning 来源。
        /// </summary>
        /// <param name="current">当前来源。</param>
        /// <param name="next">新增来源。</param>
        /// <returns>合并后的来源。</returns>
        private static ThinkingTraceSource MergeReasoningSource(ThinkingTraceSource current, ThinkingTraceSource next)
        {
            if (next == ThinkingTraceSource.None) return current;
            if (current == ThinkingTraceSource.None) return next;
            if (current == next) return current;
            return ThinkingTraceSource.Mixed;
        }
    }
}
