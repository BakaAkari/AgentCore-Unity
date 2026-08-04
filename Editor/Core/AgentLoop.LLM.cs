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
            // v1.14.6 fix: 快照本轮开始前 assistantTurn.Content 已累积的文本（前面所有轮次的可见内容）。
            // 供 PrepareAssistantMessageForHistory 在清洗/重写本轮内容时拼回前缀，避免多轮工具调用场景下
            // 中间轮次的可见文字被后续轮次的 Prepare 覆盖丢失（根因：Prepare 此前用 "=" 赋值而非累加）。
            _turnContentBeforeCurrentRound = assistantTurn?.Content ?? string.Empty;

            // Phase 3: 上下文窗口截断
            // 创建 _messages 的浅拷贝，对拷贝进行截断，不修改原始列表（保留完整历史用于 UI 显示）
            var settings = AgentCoreSettings.instance;

            // v1.6.5+: 每次 LLM 调用前自适应调整参数（确保与 ModelCapabilityProbe 探测值同步）
            settings.ApplyAdaptiveDefaults();

            int maxTokens = settings.maxContextTokens > 0
                ? settings.maxContextTokens
                : ContextWindowManager.GetModelMaxTokens(ActiveModelConfig.ModelName);
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

            // v1.14.5: 每轮 LLM 调用重置工具调用参数接收进度节流状态
            ResetToolCallProgressState();

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
                    // v1.14.5: 此前每个 delta 都打一行 "(accumulating)" Debug 日志（刷屏无信息），
                    // 且完全没有对应 UI 事件 —— 用户在此期间只能看到 console 疯狂滚动、chat 窗口
                    // 静止不动，容易误判为卡死。改为节流心跳：按时间(800ms)或字符量(2000字符)双阈值，
                    // 取先到者才记录一次，同时 emit ToolCallProgress 事件驱动 UI 侧的活体信号。
                    HandleToolCallDeltaProgress(chunk.ToolCallDelta, assistantTurn);
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
                // v1.14.5: content 与 reasoning 统一为"分块 flush"策略 —— 之前 v1.8.5 曾
                // 全累积到 Done 才一次性 emit(为消除逐字 200+ms/帧阻塞), 但副作用是最终回复
                // 正文完全没有流式感, 用户反馈"看不出任何进度"。reasoning 侧在 v1.8.8 已经改为
                // 攒够 ContentFlushCharThreshold 字符或遇到 \n\n 段落边界就中间 flush 一次,
                // 这里把 content 也统一到同一策略 —— 频率上限仍受阈值控制, 不会重现 v1.8.5 的
                // 逐 token 阻塞问题。
                if (_pendingStreamContent == null)
                    _pendingStreamContent = new StringBuilder();
                _pendingStreamContent.Append(delta.VisibleContent);

                if (ShouldFlushPendingContent())
                {
                    FlushPendingContentIfAny(assistantTurn);
                }
            }
        }

        /// <summary>
        /// v1.14.5: 判断累积的 content 是否达到中间 flush 阈值。
        /// 触发条件（任一先到）：
        /// - buffer 含 "\n\n"（段落边界，语义完整点）
        /// - buffer 长度 >= ContentFlushCharThreshold（兜底）
        /// 与 <see cref="ShouldFlushPendingReasoning"/> 保持同一节流哲学。
        /// </summary>
        private bool ShouldFlushPendingContent()
        {
            if (_pendingStreamContent == null || _pendingStreamContent.Length == 0) return false;
            if (_pendingStreamContent.Length >= ContentFlushCharThreshold) return true;
            var text = _pendingStreamContent.ToString();
            return text.Contains("\n\n");
        }

        /// <summary>
        /// v1.14.5: 只 flush content（不影响 reasoning），用于流式期间的中间分块吐出。
        /// </summary>
        private void FlushPendingContentIfAny(ConversationTurn assistantTurn)
        {
            if (_pendingStreamContent == null || _pendingStreamContent.Length == 0) return;
            var chunk = _pendingStreamContent.ToString();
            _pendingStreamContent.Clear();
            if (assistantTurn != null)
            {
                EmitEvent(AgentEvent.StreamToken(chunk, assistantTurn.Id));
            }
        }

        /// <summary>
        /// v1.14.5: content 分块 flush 的字符阈值（兜底），与 reasoning 侧同一权衡：
        /// 太小又变逐字流式重新引入高频问题；太大失去流式感。
        /// </summary>
        private const int ContentFlushCharThreshold = 200;

        /// <summary>
        /// v1.14.5: 流式结束/出错时的收尾 flush —— 清空 <see cref="ShouldFlushPendingContent"/>/
        /// <see cref="ShouldFlushPendingReasoning"/> 尚未触发中间 flush 而残留在 buffer 里的尾段。
        /// 正常情况下 buffer 里只剩不足一个阈值的小段（因为中间 flush 已经按段落/字符阈值持续吐出），
        /// 这里只是兜底保证不丢失最后一小段。
        /// </summary>
        private void FlushAccumulatedContentIfAny(ConversationTurn assistantTurn)
        {
            // 先 flush reasoning 残留 (顺序与 UI 组织一致：reasoning 在前，content 在后)
            if (_pendingStreamReasoning != null && _pendingStreamReasoning.Length > 0)
            {
                var fullReasoning = _pendingStreamReasoning.ToString();
                _pendingStreamReasoning.Clear();
                if (assistantTurn != null)
                {
                    EmitEvent(AgentEvent.ReasoningToken(fullReasoning, assistantTurn.Id, _pendingStreamReasoningSource));
                }
            }

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
        /// v1.14.6 fix: 累积 stream 期尚未达到 flush 阈值的 content 尾段（分块 flush 策略下的缓冲区）。
        /// </summary>
        private StringBuilder _pendingStreamContent;

        /// <summary>
        /// v1.14.6 fix: 本轮 LLM 调用（CallLLMStreamAsync）开始前，assistantTurn.Content 已经累积
        /// 的文本快照（即之前所有轮次已经流式显示过的可见内容）。供 PrepareAssistantMessageForHistory
        /// 在清洗/重写本轮内容时拼回前缀，避免多轮工具调用场景下用整体赋值（"="）覆盖掉此前轮次通过
        /// 逐 token 追加（"+="）写入的可见文字。
        /// </summary>
        private string _turnContentBeforeCurrentRound = string.Empty;

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
            // v1.8.6 + v1.8.8: reasoning token 累积到 _pendingStreamReasoning, 减少 UI 事件频率.
            // v1.8.6 曾"全累积到 Done 一次 flush", 实测导致单帧 render 长 markdown 出现 829ms 尖峰.
            // v1.8.8 改为"分块 flush": 累积到 \n\n 段落边界 或 超过 200 字 时中间 flush 一次,
            // Done 时无论如何强制 flush 剩余. \n\n 优先 (语义完整分段), 200 字兜底 (防止长段落堆积).
            if (_pendingStreamReasoning == null)
                _pendingStreamReasoning = new StringBuilder();
            _pendingStreamReasoning.Append(token);
            _pendingStreamReasoningSource = assistantTurn.ReasoningSource;

            // 检查是否触发中间 flush
            if (ShouldFlushPendingReasoning())
            {
                FlushPendingReasoningIfAny(assistantTurn);
            }
        }

        /// <summary>
        /// v1.8.8: 判断累积的 reasoning 是否达到中间 flush 阈值.
        /// 触发条件 (任一先到):
        /// - buffer 含 "\n\n" (段落边界, 语义完整点)
        /// - buffer 长度 >= ReasoningFlushCharThreshold (兜底)
        /// </summary>
        private bool ShouldFlushPendingReasoning()
        {
            if (_pendingStreamReasoning == null || _pendingStreamReasoning.Length == 0) return false;
            if (_pendingStreamReasoning.Length >= ReasoningFlushCharThreshold) return true;
            // ToString + IndexOf 每次都 alloc — 但 reasoning append 频率低于 content, 可接受.
            // 未来若成为热点可改为增量扫描 (记录上次扫描位置).
            var text = _pendingStreamReasoning.ToString();
            return text.Contains("\n\n");
        }

        /// <summary>
        /// v1.8.8: 只 flush reasoning (不 flush content), 用于中间分块.
        /// </summary>
        private void FlushPendingReasoningIfAny(ConversationTurn assistantTurn)
        {
            if (_pendingStreamReasoning == null || _pendingStreamReasoning.Length == 0) return;
            var chunk = _pendingStreamReasoning.ToString();
            _pendingStreamReasoning.Clear();
            if (assistantTurn != null)
            {
                EmitEvent(AgentEvent.ReasoningToken(chunk, assistantTurn.Id, _pendingStreamReasoningSource));
            }
        }

        /// <summary>
        /// v1.8.6: 累积 stream 期的 UI reasoning, 完成时一次性 emit.
        /// v1.8.8: 改为分块 flush, 阈值 <see cref="ReasoningFlushCharThreshold"/>.
        /// </summary>
        private StringBuilder _pendingStreamReasoning;
        private ThinkingTraceSource _pendingStreamReasoningSource;

        /// <summary>
        /// v1.8.8: reasoning 分块 flush 的字符阈值 (兜底). \n\n 优先.
        /// 200 是权衡点: 太小 (50) 频繁 flush 又变成"逐字流式"; 太大 (500+) 单次 render 长
        /// markdown 又出现 v1.8.6 那种 829ms 尖峰.
        /// </summary>
        private const int ReasoningFlushCharThreshold = 200;

        // === v1.14.5: 工具调用参数接收进度节流状态 ===

        /// <summary>本轮 LLM 调用中，当前正在接收参数的工具调用累积字符数（跨多个 delta）。</summary>
        private int _toolCallProgressCharCount;

        /// <summary>上次 emit ToolCallProgress 时的累积字符数（用于字符阈值判断）。</summary>
        private int _toolCallProgressLastEmittedCharCount;

        /// <summary>上次 emit ToolCallProgress 的时间（用于时间阈值判断）。</summary>
        private DateTime? _toolCallProgressLastEmittedUtc;

        /// <summary>本次工具调用参数开始接收的时间（用于计算耗时）。</summary>
        private DateTime? _toolCallProgressStartedUtc;

        /// <summary>当前已知的工具名（function.name delta 到达后填充；可能长期为 null）。</summary>
        private string _toolCallProgressToolName;

        /// <summary>
        /// v1.14.5: 心跳节流的时间阈值 —— 与字符阈值任一先到即 emit 一次。
        /// 800ms 是权衡点：短于此会在正常小参数工具调用上也频繁触发（无意义噪音）；
        /// 长于此用户在大参数场景下等待反馈的间隔会变得明显。
        /// </summary>
        private const int ToolCallProgressFlushIntervalMs = 800;

        /// <summary>
        /// v1.14.5: 心跳节流的字符阈值 —— 与时间阈值任一先到即 emit 一次。
        /// 2000 字符对应典型 batch_execute/manage_script 大参数场景下每次心跳约代表一次
        /// 有意义的增量，不会在参数量小的常规调用上触发多次。
        /// </summary>
        private const int ToolCallProgressFlushCharThreshold = 2000;

        /// <summary>
        /// v1.14.5: 每轮 LLM 调用开始前重置节流状态，避免跨轮次累积字符数失真。
        /// </summary>
        private void ResetToolCallProgressState()
        {
            _toolCallProgressCharCount = 0;
            _toolCallProgressLastEmittedCharCount = 0;
            _toolCallProgressLastEmittedUtc = null;
            _toolCallProgressStartedUtc = null;
            _toolCallProgressToolName = null;
        }

        /// <summary>
        /// v1.14.5: 处理单个 ToolCallDelta，做节流统计并在达到阈值时 emit 心跳。
        /// <para>
        /// 治本设计：不是每个 delta 都 emit（会重现 v1.8.x 那种高频问题），而是维护累积状态，
        /// 按"时间(800ms) 或 字符量(2000) 任一先到"节流发出。数据不流入时（网络卡住/模型停止）
        /// 心跳也不会凭空跳动 —— 这是"事件驱动"而非"定时器驱动"的关键区别：如果模型真的卡住，
        /// UI 会诚实地停在最后一次心跳，不会用假动画掩盖真实的卡死状态。
        /// </para>
        /// </summary>
        private void HandleToolCallDeltaProgress(ToolCall delta, ConversationTurn assistantTurn)
        {
            if (delta == null) return;

            if (_toolCallProgressStartedUtc == null)
            {
                _toolCallProgressStartedUtc = DateTime.UtcNow;
            }

            if (delta.Function != null)
            {
                if (!string.IsNullOrEmpty(delta.Function.Name))
                    _toolCallProgressToolName = delta.Function.Name;

                if (!string.IsNullOrEmpty(delta.Function.Arguments))
                    _toolCallProgressCharCount += delta.Function.Arguments.Length;
            }

            var now = DateTime.UtcNow;
            bool charThresholdHit = (_toolCallProgressCharCount - _toolCallProgressLastEmittedCharCount) >= ToolCallProgressFlushCharThreshold;
            bool timeThresholdHit = _toolCallProgressLastEmittedUtc == null
                || (now - _toolCallProgressLastEmittedUtc.Value).TotalMilliseconds >= ToolCallProgressFlushIntervalMs;

            if (!charThresholdHit && !timeThresholdHit) return;

            _toolCallProgressLastEmittedCharCount = _toolCallProgressCharCount;
            _toolCallProgressLastEmittedUtc = now;

            var elapsedMs = Math.Max(0, (now - _toolCallProgressStartedUtc.Value).TotalMilliseconds);

            // 心跳日志：带进度信息，替代旧版每 delta 一行的 "(accumulating)" 刷屏日志
            AgentCore.Editor.Utils.AgentCoreLog.Debug(
                $"[AgentCore] Tool call argument stream: tool={_toolCallProgressToolName ?? "(pending)"} " +
                $"chars={_toolCallProgressCharCount} elapsed={elapsedMs:F0}ms");

            EmitEvent(AgentEvent.ToolCallProgress(
                _toolCallProgressToolName,
                delta.Id,
                _toolCallProgressCharCount,
                elapsedMs,
                assistantTurn?.Id));
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
