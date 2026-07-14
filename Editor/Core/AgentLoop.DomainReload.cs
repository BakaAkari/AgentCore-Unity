using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.LLM;
using AgentCore.Editor.Session;
using AgentCore.Editor.Tools;
using AgentCore.Editor.Utils;
using UnityEngine;

namespace AgentCore.Editor.Core
{
    public partial class AgentLoop
    {
        /// <summary>
        /// AssemblyReloadEvents.beforeAssemblyReload 回调。
        /// 在 Domain Reload 前保存中断状态和对话历史。
        /// Phase 2 增强：额外保存用户消息、assistant 部分内容、tool_call ID。
        /// </summary>
        private void OnBeforeAssemblyReload()
        {
            // 1. 检查当前是否正在执行操作
            if (CurrentState == AgentState.Idle)
            {
                // 修复 #6: Agent 空闲时也需要保存当前会话到磁盘，
                // 确保 Domain Reload 后 TryRestoreSession() 能恢复会话。
                // 不保存中断状态（WasInterrupted 保持 false），只保存会话数据。
                AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] beforeAssemblyReload: Agent is idle, saving session before reload.");

                // Phase 4.5: 空闲状态也保存文件变更追踪数据
                try
                {
                    if (_fileChangeTracker != null && _fileChangeTracker.HasChanges)
                    {
                        var fileChangesJson = _fileChangeTracker.SerializeToJson();
                        DomainReloadState.instance.SaveFileChangeRecords(fileChangesJson);
                        AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] beforeAssemblyReload: File change records saved (idle, {_fileChangeTracker.RecordCount} records).");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[AgentCore] beforeAssemblyReload: Failed to save file change records (idle): {ex.Message}");
                }

                // Phase 5: 空闲状态也保存压缩统计数据
                try
                {
                    if (_compressionMetrics != null)
                    {
                        DomainReloadState.instance.SaveCompressionMetrics(
                            _compressionMetrics.ToolResultCompressionSuccessCount,
                            _compressionMetrics.ConversationCompressionSuccessCount,
                            _compressionMetrics.TotalTokensSaved,
                            _compressionMetrics.ToolResultOriginalTokens,
                            _compressionMetrics.ConversationOriginalTokens
                        );
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[AgentCore] beforeAssemblyReload: Failed to save compression metrics (idle): {ex.Message}");
                }

                try
                {
                    SessionManager.Instance.ForceSave(
                        new List<ChatMessage>(_messages),
                        new List<ConversationTurn>(_conversationTurns),
                        _compressionMetrics);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[AgentCore] beforeAssemblyReload: Failed to save idle session: {ex.Message}");
                }
                return;
            }

            // 2. 映射当前 AgentState 到 InterruptPhase
            InterruptPhase phase;
            switch (CurrentState)
            {
                case AgentState.Streaming:
                case AgentState.Thinking:
                    phase = InterruptPhase.Streaming;
                    break;
                case AgentState.ExecutingTool:
                    phase = InterruptPhase.ExecutingTool;
                    break;
                default:
                    phase = InterruptPhase.None;
                    break;
            }

            // 3. 获取最后执行的工具名和 pending tool call 信息
            string lastToolName = null;
            string interruptedToolCallId = null;
            bool hadPendingToolCalls = false;
            for (int i = _messages.Count - 1; i >= 0; i--)
            {
                var msg = _messages[i];
                if (msg.Role == "assistant" && msg.ToolCalls != null && msg.ToolCalls.Count > 0)
                {
                    var lastToolCall = msg.ToolCalls[msg.ToolCalls.Count - 1];
                    lastToolName = lastToolCall.Function?.Name;
                    // 检查是否有未完成的 tool calls（assistant 发了 tool_calls 但还没有对应的 tool 结果）
                    int toolCallCount = msg.ToolCalls.Count;
                    int toolResultCount = 0;
                    for (int j = i + 1; j < _messages.Count; j++)
                    {
                        if (_messages[j].Role == "tool") toolResultCount++;
                        else break;
                    }
                    hadPendingToolCalls = toolResultCount < toolCallCount;
                    if (hadPendingToolCalls)
                    {
                        // 保存第一个未完成的 tool_call ID
                        int pendingIndex = toolResultCount;
                        if (pendingIndex < msg.ToolCalls.Count)
                        {
                            interruptedToolCallId = msg.ToolCalls[pendingIndex].Id;
                        }
                    }
                    break;
                }
            }

            // 4. Phase 2: 提取最后一条用户消息
            string pendingUserMessage = null;
            for (int i = _messages.Count - 1; i >= 0; i--)
            {
                if (_messages[i].Role == "user")
                {
                    pendingUserMessage = _messages[i].Content;
                    break;
                }
            }

            // 5. Phase 2: 提取最后一条 assistant 部分内容（从 ConversationTurns 中获取流式累积内容）
            string lastAssistantContent = null;
            string lastAssistantReasoning = null;
            ThinkingTraceSource lastAssistantReasoningSource = ThinkingTraceSource.None;
            double lastAssistantReasoningDurationMs = 0;
            string lastAssistantRawContent = null;
            VisiblePlanningTraceState lastAssistantPlanningTraceState = VisiblePlanningTraceState.None;
            for (int i = _conversationTurns.Count - 1; i >= 0; i--)
            {
                var turn = _conversationTurns[i];
                if (turn.Role == "assistant" &&
                    (!string.IsNullOrEmpty(turn.Content) || !string.IsNullOrEmpty(turn.Reasoning)))
                {
                    lastAssistantContent = turn.Content;
                    lastAssistantReasoning = turn.Reasoning;
                    lastAssistantReasoningSource = turn.ReasoningSource;
                    lastAssistantReasoningDurationMs = turn.ReasoningDurationMs;
                    lastAssistantRawContent = turn.RawAssistantContent;
                    lastAssistantPlanningTraceState = turn.PlanningTraceState;
                    break;
                }
            }

            // 6. 保存中断标记到 DomainReloadState（Phase 2 增强版）
            var sessionId = SessionManager.Instance.CurrentSessionId;
            DomainReloadState.instance.MarkInterrupted(
                sessionId,
                phase,
                lastToolName,
                hadPendingToolCalls,
                pendingUserMessage,
                lastAssistantContent,
                interruptedToolCallId,
                lastAssistantReasoning,
                lastAssistantReasoningSource,
                lastAssistantReasoningDurationMs,
                lastAssistantRawContent,
                lastAssistantPlanningTraceState
            );

            // 7. Phase 4.5: 保存文件变更追踪数据到 DomainReloadState
            try
            {
                if (_fileChangeTracker != null && _fileChangeTracker.HasChanges)
                {
                    var fileChangesJson = _fileChangeTracker.SerializeToJson();
                    DomainReloadState.instance.SaveFileChangeRecords(fileChangesJson);
                    AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] beforeAssemblyReload: File change records saved ({_fileChangeTracker.RecordCount} records).");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentCore] beforeAssemblyReload: Failed to save file change records: {ex.Message}");
            }

            // 7.5 Phase 5: 保存压缩统计数据到 DomainReloadState
            try
            {
                if (_compressionMetrics != null)
                {
                    DomainReloadState.instance.SaveCompressionMetrics(
                        _compressionMetrics.ToolResultCompressionSuccessCount,
                        _compressionMetrics.ConversationCompressionSuccessCount,
                        _compressionMetrics.TotalTokensSaved,
                        _compressionMetrics.ToolResultOriginalTokens,
                        _compressionMetrics.ConversationOriginalTokens
                    );
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentCore] beforeAssemblyReload: Failed to save compression metrics: {ex.Message}");
            }

            // 8. 强制保存当前对话历史到磁盘
            try
            {
                SessionManager.Instance.ForceSave(
                    new List<ChatMessage>(_messages),
                    new List<ConversationTurn>(_conversationTurns),
                    _compressionMetrics);
                AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] beforeAssemblyReload: Session saved successfully.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentCore] beforeAssemblyReload: Failed to save session: {ex.Message}");
            }

            // 8. 取消当前操作的 CancellationToken
            if (_currentCts != null && !_currentCts.IsCancellationRequested)
            {
                _currentCts.Cancel();
                AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] beforeAssemblyReload: Cancelled current operation.");
            }

            AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] beforeAssemblyReload: Interruption saved — state={CurrentState}, phase={phase}, " +
                      $"tool={lastToolName}, toolCallId={interruptedToolCallId}, " +
                      $"hasUserMsg={!string.IsNullOrEmpty(pendingUserMessage)}, " +
                      $"hasAssistantContent={!string.IsNullOrEmpty(lastAssistantContent)}");
        }

        /// <summary>
        /// 尝试在 Domain Reload 后恢复 Agent 工作流。
        /// <para>
        /// 根据 <see cref="DomainReloadState"/> 中记录的中断信息，决定恢复策略：
        /// <list type="bullet">
        ///   <item><b>Streaming 中断</b>：注入系统消息说明中断原因，重新发送请求让 LLM 继续</item>
        ///   <item><b>ExecutingTool 中断</b>：注入系统消息说明 tool 执行被中断，让 LLM 重新调用该 tool</item>
        ///   <item><b>WaitingCompilation 中断</b>：注入编译结果消息，让 AgentLoop 继续处理</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <returns>是否成功触发了恢复流程</returns>
        public bool TryResumeAfterReload()
        {
            var reloadState = DomainReloadState.instance;

            // 1. 检查是否有中断标记
            if (!reloadState.WasInterrupted)
            {
                AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] TryResumeAfterReload: No interruption detected, skipping.");
                return false;
            }

            // 2. 确保 AgentLoop 已完全初始化
            if (!_isInitialized)
            {
                Debug.LogWarning("[AgentCore] TryResumeAfterReload: AgentLoop not initialized, cannot resume.");
                reloadState.ClearInterruption();
                return false;
            }

            // 3. 确保当前处于 Idle 状态
            if (CurrentState != AgentState.Idle)
            {
                Debug.LogWarning($"[AgentCore] TryResumeAfterReload: Agent is in {CurrentState} state, cannot resume.");
                reloadState.ClearInterruption();
                return false;
            }

            // 4. 确保消息历史不为空（至少有 system prompt）
            if (_messages.Count == 0)
            {
                Debug.LogWarning("[AgentCore] TryResumeAfterReload: Message history is empty, cannot resume.");
                reloadState.ClearInterruption();
                return false;
            }

            var phase = reloadState.InterruptPhase;
            var lastToolName = reloadState.LastToolName;
            var interruptedToolCallId = reloadState.InterruptedToolCallId;
            var lastAssistantContent = reloadState.LastAssistantContent;
            var lastAssistantReasoning = reloadState.LastAssistantReasoning;
            var lastAssistantReasoningSource = reloadState.LastAssistantReasoningSource;
            var lastAssistantReasoningDurationMs = reloadState.LastAssistantReasoningDurationMs;
            var lastAssistantRawContent = reloadState.LastAssistantRawContent;
            var lastAssistantPlanningTraceState = reloadState.LastAssistantPlanningTraceState;
            var compilationSucceeded = reloadState.CompilationSucceeded;
            var compilationErrors = reloadState.CompilationErrors;

            AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] TryResumeAfterReload: Resuming from {phase} interruption " +
                      $"(tool={lastToolName}, compilationOK={compilationSucceeded})");

            // 5. 根据中断阶段构建恢复消息
            string recoveryMessage = BuildRecoveryMessage(phase, lastToolName, interruptedToolCallId,
                compilationSucceeded, compilationErrors);

            // 6. 根据中断阶段执行恢复策略
            switch (phase)
            {
                case InterruptPhase.Streaming:
                    ResumeFromStreaming(
                        recoveryMessage,
                        lastAssistantContent,
                        lastAssistantReasoning,
                        lastAssistantReasoningSource,
                        lastAssistantReasoningDurationMs,
                        lastAssistantRawContent,
                        lastAssistantPlanningTraceState);
                    break;

                case InterruptPhase.ExecutingTool:
                    ResumeFromExecutingTool(recoveryMessage, interruptedToolCallId, lastToolName);
                    break;

                case InterruptPhase.WaitingCompilation:
                    ResumeFromWaitingCompilation(recoveryMessage, interruptedToolCallId,
                        compilationSucceeded, compilationErrors);
                    break;

                default:
                    Debug.LogWarning($"[AgentCore] TryResumeAfterReload: Unknown phase {phase}, clearing state.");
                    reloadState.ClearInterruption();
                    return false;
            }

            // 7. 清除中断标记
            reloadState.ClearInterruption();

            AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] TryResumeAfterReload: Recovery initiated successfully.");
            return true;
        }

        /// <summary>
        /// 构建 Domain Reload 恢复系统消息。
        /// </summary>
        /// <param name="phase">中断阶段</param>
        /// <param name="lastToolName">最后执行的工具名</param>
        /// <param name="toolCallId">被中断的 tool_call ID</param>
        /// <param name="compilationSucceeded">编译是否成功</param>
        /// <param name="compilationErrors">编译错误信息</param>
        /// <returns>恢复系统消息文本</returns>
        private static string BuildRecoveryMessage(
            InterruptPhase phase,
            string lastToolName,
            string toolCallId,
            bool compilationSucceeded,
            string compilationErrors)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[Domain Reload Recovery] Unity code compilation triggered a Domain Reload, which interrupted the Agent workflow.");
            sb.AppendLine($"- Interruption phase: {phase}");

            if (!string.IsNullOrEmpty(lastToolName))
            {
                sb.AppendLine($"- Last tool being used: {lastToolName}");
            }

            if (!string.IsNullOrEmpty(toolCallId))
            {
                sb.AppendLine($"- Interrupted tool_call ID: {toolCallId}");
            }

            // 编译结果
            if (compilationSucceeded)
            {
                sb.AppendLine("- Compilation result: Success");
            }
            else if (!string.IsNullOrEmpty(compilationErrors))
            {
                sb.AppendLine($"- Compilation result: Failed");
                sb.AppendLine($"- Compilation errors:\n{compilationErrors}");
            }
            else
            {
                sb.AppendLine("- Compilation result: Unknown (compilation status not captured)");
            }

            sb.AppendLine("Please continue from where you left off. If you were in the middle of executing a tool, please retry the operation.");

            return sb.ToString();
        }

        /// <summary>
        /// 从 Streaming 中断恢复：注入已清洗 assistant 部分内容和系统消息，重新调用 LLM。
        /// </summary>
        /// <param name="recoveryMessage">恢复系统消息</param>
        /// <param name="lastAssistantContent">中断前的 assistant 可见内容</param>
        /// <param name="lastAssistantReasoning">中断前的 reasoning / planning trace 内容</param>
        /// <param name="lastAssistantReasoningSource">中断前的 reasoning 来源</param>
        /// <param name="lastAssistantReasoningDurationMs">中断前的 reasoning 用时</param>
        /// <param name="lastAssistantRawContent">中断前的 assistant 原始内容</param>
        /// <param name="lastAssistantPlanningTraceState">中断前的可见规划 trace 状态</param>
        private void ResumeFromStreaming(
            string recoveryMessage,
            string lastAssistantContent,
            string lastAssistantReasoning,
            ThinkingTraceSource lastAssistantReasoningSource,
            double lastAssistantReasoningDurationMs,
            string lastAssistantRawContent,
            VisiblePlanningTraceState lastAssistantPlanningTraceState)
        {
            AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] ResumeFromStreaming: Injecting recovery message and re-calling LLM.");

            var recoveredAssistantTurn = RestoreInterruptedAssistantTurn(
                lastAssistantContent,
                lastAssistantReasoning,
                lastAssistantReasoningSource,
                lastAssistantReasoningDurationMs,
                lastAssistantRawContent,
                lastAssistantPlanningTraceState);

            // 如果有 assistant 部分可见内容，添加到消息历史中。RawAssistantContent 与 Reasoning 永不进入 _messages。
            if (recoveredAssistantTurn != null && !string.IsNullOrEmpty(recoveredAssistantTurn.Content))
            {
                var cleanContent = recoveredAssistantTurn.Content;
                bool alreadyHasAssistant = _messages.Count > 0 &&
                    _messages[_messages.Count - 1].Role == "assistant" &&
                    _messages[_messages.Count - 1].Content == cleanContent;

                if (!alreadyHasAssistant)
                {
                    _messages.Add(ChatMessage.Assistant(cleanContent));
                    AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] ResumeFromStreaming: Added sanitized partial assistant content ({cleanContent.Length} chars).");
                }
            }

            // 注入恢复系统消息
            _messages.Add(ChatMessage.System(recoveryMessage));

            // 触发新的 LLM 调用（通过 SendMessageAsync 的内部机制）
            TriggerResumeLLMCall();
        }

        /// <summary>
        /// 还原 Domain Reload 前被中断的 assistant 轮次，仅用于 UI/session 审计。
        /// </summary>
        /// <param name="content">已清洗的可见内容。</param>
        /// <param name="reasoning">reasoning / planning trace 内容。</param>
        /// <param name="source">reasoning 来源。</param>
        /// <param name="durationMs">reasoning 用时。</param>
        /// <param name="rawContent">原始 assistant 内容。</param>
        /// <param name="planningTraceState">可见规划 trace 状态。</param>
        /// <returns>恢复出的 assistant 轮次；无内容时返回 null。</returns>
        private ConversationTurn RestoreInterruptedAssistantTurn(
            string content,
            string reasoning,
            ThinkingTraceSource source,
            double durationMs,
            string rawContent,
            VisiblePlanningTraceState planningTraceState)
        {
            if (string.IsNullOrEmpty(content) && string.IsNullOrEmpty(reasoning) && string.IsNullOrEmpty(rawContent))
                return null;

            var cleanContent = content ?? string.Empty;
            var raw = rawContent ?? string.Empty;

            if (!string.IsNullOrEmpty(raw))
            {
                var finalResult = VisiblePlanningTraceExtractor.FinalizeContent(raw);
                if (finalResult.State == VisiblePlanningTraceState.Completed)
                {
                    cleanContent = finalResult.Content;
                    reasoning = MergeReasoningText(reasoning, finalResult.Reasoning);
                    source = MergeReasoningSource(source, ThinkingTraceSource.VisiblePlanningTrace);
                    planningTraceState = finalResult.State;
                }
            }

            var turn = new ConversationTurn("assistant", cleanContent)
            {
                IsStreaming = false,
                Reasoning = reasoning ?? string.Empty,
                ReasoningSource = source,
                ReasoningDurationMs = Math.Max(0, durationMs),
                RawAssistantContent = raw,
                PlanningTraceState = planningTraceState
            };

            _conversationTurns.Add(turn);
            SessionManager.Instance.MarkDirty();
            return turn;
        }

        /// <summary>
        /// 从 ExecutingTool 中断恢复：注入系统消息说明 tool 执行被中断，让 LLM 重新调用。
        /// </summary>
        /// <param name="recoveryMessage">恢复系统消息</param>
        /// <param name="interruptedToolCallId">被中断的 tool_call ID</param>
        /// <param name="lastToolName">最后执行的工具名</param>
        private void ResumeFromExecutingTool(string recoveryMessage, string interruptedToolCallId, string lastToolName)
        {
            AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] ResumeFromExecutingTool: Tool '{lastToolName}' was interrupted (callId={interruptedToolCallId}).");

            // 如果有未完成的 tool_call，需要补充一个 tool response 以保持消息格式合法
            if (!string.IsNullOrEmpty(interruptedToolCallId))
            {
                // 检查是否已经有对应的 tool response
                bool hasResponse = false;
                for (int i = _messages.Count - 1; i >= 0; i--)
                {
                    if (_messages[i].Role == "tool" && _messages[i].ToolCallId == interruptedToolCallId)
                    {
                        hasResponse = true;
                        break;
                    }
                    // 如果遇到了 assistant 消息，说明还没有对应的 tool response
                    if (_messages[i].Role == "assistant") break;
                }

                if (!hasResponse)
                {
                    // 补充一个表示中断的 tool response
                    _messages.Add(ChatMessage.Tool(interruptedToolCallId,
                        $"[Tool execution interrupted by Domain Reload] The tool '{lastToolName}' was interrupted " +
                        "because Unity triggered a code compilation and Domain Reload. " +
                        "The tool result is unknown. Please retry the operation if needed."));
                    AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] ResumeFromExecutingTool: Added placeholder tool response for {interruptedToolCallId}.");
                }
            }

            // 注入恢复系统消息
            _messages.Add(ChatMessage.System(recoveryMessage));

            // 触发新的 LLM 调用
            TriggerResumeLLMCall();
        }

        /// <summary>
        /// 从 WaitingCompilation 中断恢复：注入编译结果作为 tool response，继续 AgentLoop。
        /// </summary>
        /// <param name="recoveryMessage">恢复系统消息</param>
        /// <param name="interruptedToolCallId">被中断的 tool_call ID</param>
        /// <param name="compilationSucceeded">编译是否成功</param>
        /// <param name="compilationErrors">编译错误信息</param>
        private void ResumeFromWaitingCompilation(
            string recoveryMessage,
            string interruptedToolCallId,
            bool compilationSucceeded,
            string compilationErrors)
        {
            AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] ResumeFromWaitingCompilation: Compilation {(compilationSucceeded ? "succeeded" : "failed")}.");

            // 如果有未完成的 tool_call，补充编译结果作为 tool response
            if (!string.IsNullOrEmpty(interruptedToolCallId))
            {
                bool hasResponse = false;
                for (int i = _messages.Count - 1; i >= 0; i--)
                {
                    if (_messages[i].Role == "tool" && _messages[i].ToolCallId == interruptedToolCallId)
                    {
                        hasResponse = true;
                        break;
                    }
                    if (_messages[i].Role == "assistant") break;
                }

                if (!hasResponse)
                {
                    string compilationResult;
                    if (compilationSucceeded)
                    {
                        compilationResult = "Compilation completed successfully. The script changes have been applied.";
                    }
                    else if (!string.IsNullOrEmpty(compilationErrors))
                    {
                        compilationResult = $"Compilation failed with errors:\n{compilationErrors}\nPlease fix the compilation errors.";
                    }
                    else
                    {
                        compilationResult = "Compilation completed (result unknown). Please verify the script changes.";
                    }

                    _messages.Add(ChatMessage.Tool(interruptedToolCallId, compilationResult));
                    AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] ResumeFromWaitingCompilation: Added compilation result as tool response.");
                }
            }

            // 注入恢复系统消息
            _messages.Add(ChatMessage.System(recoveryMessage));

            // 触发新的 LLM 调用
            TriggerResumeLLMCall();
        }

        /// <summary>
        /// 触发恢复后的 LLM 调用。
        /// 创建新的 assistant 轮次并启动异步 SendMessage 流程。
        /// </summary>
        private void TriggerResumeLLMCall()
        {
            AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] TriggerResumeLLMCall: Starting resumed LLM call...");

            // 修复 #7: 在发送 LLM 请求前，清理消息历史中不完整的 tool_use/tool_result 配对
            // Resume 方法可能只修复了单个 interruptedToolCallId，但 assistant 消息可能有多个 tool_calls
            SanitizeMessageHistory();

            // 恢复调用会产生新的 LLM 回复，标记会话内容已变更
            SessionManager.Instance.MarkDirty();

            // 使用 AsyncHelper 在主线程上异步执行恢复调用
            AsyncHelper.RunAsync(
                async () =>
                {
                    // 创建取消令牌
                    _currentCts?.Dispose();
                    _currentCts = new CancellationTokenSource();
                    var ct = _currentCts.Token;

                    // 创建助手轮次（流式输出占位）
                    var assistantTurn = new ConversationTurn("assistant")
                    {
                        IsStreaming = true
                    };
                    _conversationTurns.Add(assistantTurn);

                    try
                    {
                        // 构建工具定义列表
                        var toolDefinitions = BuildToolDefinitions();

                        // P0-2 fix: 使用提取的公共方法，消除与 SendMessageAsync 的代码重复
                        // RunToolCallLoopAsync 内部已包含自动保存逻辑
                        await RunToolCallLoopAsync(assistantTurn, toolDefinitions, ct, " Resume");

                        SetState(AgentState.Idle);
                    }
                    catch (OperationCanceledException)
                    {
                        AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] Resume LLM call was cancelled.");
                        assistantTurn.IsStreaming = false;
                        SetState(AgentState.Idle);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[AgentCore] Error during resume LLM call: {ex}");
                        assistantTurn.IsStreaming = false;
                        EmitEvent(AgentEvent.ErrorEvent(ex, "Domain Reload 恢复"));
                        SetState(AgentState.Error);
                        SetState(AgentState.Idle);
                    }
                },
                onError: ex => Debug.LogError($"[AgentCore] TriggerResumeLLMCall error: {ex.Message}")
            );
        }
    }
}
