using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Config;
using AgentCore.Editor.Core.SelfChallenge;
using AgentCore.Editor.LLM;
using AgentCore.Editor.Session;
using AgentCore.Editor.Tools;
using AgentCore.Editor.Tools.Safety;
using AgentCore.Editor.Utils;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AgentCore.Editor.Core
{
    public partial class AgentLoop
    {
        /// <summary>
        /// 工具调用循环的核心逻辑。
        /// <para>
        /// 从 SendMessageAsync 和 TriggerResumeLLMCall 中提取的公共方法，
        /// 包含 while 循环、连续失败检测、单工具失败追踪、强制退出、最大轮次检查、
        /// LoopCompleted 事件发送和自动保存。
        /// </para>
        /// </summary>
        /// <param name="assistantTurn">当前助手对话轮次</param>
        /// <param name="toolDefinitions">工具定义列表</param>
        /// <param name="ct">取消令牌</param>
        /// <param name="logPrefix">日志前缀（用于区分正常调用和恢复调用）</param>
        private async Task RunToolCallLoopAsync(
            ConversationTurn assistantTurn,
            List<ToolDefinition> toolDefinitions,
            CancellationToken ct,
            string logPrefix = "")
        {
            var settings = AgentCoreSettings.instance;
            int maxRounds = settings.maxToolCallRounds;
            int tokenBudget = settings.maxTokenBudget; // 0 = 不限制
            int currentRound = 0;
            int accumulatedTokens = 0; // 本次任务累计消耗的 token 数

            // 连续失败检测 — 两级响应（warning / block）
            int consecutiveAllFailRounds = 0;
            int allToolsFailBlockThreshold = Math.Max(2, settings.allToolsFailBlockThreshold);

            // 单工具连续失败追踪 — 阈值从 Settings 读取
            var perToolFailCount = new Dictionary<string, int>();
            int toolFailWarningThreshold = Math.Max(1, settings.toolFailWarningThreshold);
            int toolFailBlockThreshold = Math.Max(2, settings.toolFailBlockThreshold);

            // 已发出警告的工具集合（同一工具在同一循环内只警告一次）
            var warnedTools = new HashSet<string>();

            // 同一工具对同一目标重复调用检测 —— 防止 LLM 在单文件/单目标上无限循环
            var perToolTargetCallCount = new Dictionary<string, int>();
            const int toolTargetRepeatWarningThreshold = 4;
            const int toolTargetRepeatBlockThreshold = 7;
            var warnedToolTargets = new HashSet<string>();

            while (currentRound < maxRounds)
            {
                currentRound++;

                // Token Budget 检查（在轮次开始前，第 1 轮不检查）
                if (tokenBudget > 0 && currentRound > 1 && accumulatedTokens >= tokenBudget)
                {
                    AgentCoreLog.Warning($"[AgentCore]{logPrefix} Token budget exceeded ({accumulatedTokens}/{tokenBudget}) at round {currentRound}. Triggering summary.");
                    break;
                }

                if (ct.IsCancellationRequested)
                {
                    AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore]{logPrefix} Cancelled before round {currentRound}.");
                    break;
                }

                // 调用 LLM（流式）。先进入 Thinking 以确保 ChatWindow 已创建 AssistantTurnView，
                // 再发 LoopRoundStarted，避免 ToolCallGroup 降级挂到根消息列表。
                SetState(AgentState.Thinking);
                EmitEvent(AgentEvent.LoopRoundStarted(currentRound, maxRounds, accumulatedTokens));
                var assistantMessage = await CallLLMStreamAsync(assistantTurn, toolDefinitions, ct);

                if (ct.IsCancellationRequested)
                {
                    AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore]{logPrefix} Cancelled during LLM call.");
                    break;
                }

                // 检查是否有 tool_calls
                if (assistantMessage == null ||
                    assistantMessage.ToolCalls == null ||
                    assistantMessage.ToolCalls.Count == 0)
                {
                    // 纯文本回复 — 循环结束
                    HandleFinalResponse(assistantMessage, assistantTurn);
                    break;
                }

                // Phase 9: WaitingForClarification 状态下拒绝分发任何 tool_calls
                //   Node A Step 4 结论 = 反问用户时 Agent 已进入 WaitingForClarification;
                //   若 LLM 忽略指令仍然输出 tool_calls, 视为 bug 并强制降级为 final response。
                if (CurrentState == AgentState.WaitingForClarification)
                {
                    AgentCoreLog.Warning($"[AgentCore][SelfChallenge] LLM returned {assistantMessage.ToolCalls.Count} tool_call(s) while in WaitingForClarification state. Rejecting tool dispatch and forcing final response.");
                    assistantMessage.ToolCalls.Clear();
                    HandleFinalResponse(assistantMessage, assistantTurn);
                    break;
                }

                // 有 tool_calls — 执行工具。写入 LLM 历史前必须清洗可见规划 trace，避免 thinking 泄漏到上下文。
                AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore]{logPrefix} Round {currentRound}: LLM returned {assistantMessage.ToolCalls.Count} tool call(s).");
                PrepareAssistantMessageForHistory(assistantMessage, assistantTurn);

                // Token Budget: 记录本轮 LLM 输出 token（assistant message）
                int roundTokensBefore = _messages.Count;
                _messages.Add(assistantMessage);

                SetState(AgentState.ExecutingTool);
                await ExecuteToolCallsAsync(assistantMessage.ToolCalls, assistantTurn, ct);

                // Token Budget: 累计本轮消耗（assistant 输出 + 工具结果）
                int roundTokens = 0;
                for (int mi = roundTokensBefore; mi < _messages.Count; mi++)
                {
                    roundTokens += TokenCounter.EstimateMessageTokens(_messages[mi]);
                }
                accumulatedTokens += roundTokens;

                // ask_user 挂起截断：某工具调用 ask_user → loop 干净结束、进入 WaitingForUserInput 等用户应答。
                // 占位 tool_result 已由 ExecuteToolCallsAsync 写入历史（合法）；用户应答后经
                // ResumeFromUserInput 追加 user 消息并 TriggerResumeLLMCall 唤醒继续。
                if (!string.IsNullOrEmpty(_pendingUserInputToolCallId))
                {
                    AgentCore.Editor.Utils.AgentCoreLog.Info(
                        $"[AgentCore]{logPrefix} ask_user suspend: truncating loop, waiting for user input (toolCallId={_pendingUserInputToolCallId}).");
                    SetState(AgentState.WaitingForUserInput);
                    return;
                }

                if (ct.IsCancellationRequested)
                {
                    AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore]{logPrefix} Cancelled during tool execution.");
                    break;
                }


                // 连续失败检测
                bool allToolCallsFailed = CheckAllToolCallsFailed(assistantTurn, assistantMessage.ToolCalls.Count);
                if (allToolCallsFailed)
                {
                    consecutiveAllFailRounds++;
                    AgentCoreLog.Warning($"[AgentCore]{logPrefix} All tool calls failed in round {currentRound}. " +
                                     $"Consecutive failure rounds: {consecutiveAllFailRounds}/{allToolsFailBlockThreshold}");
                }
                else
                {
                    consecutiveAllFailRounds = 0;
                }

                // 单工具连续失败追踪
                UpdatePerToolFailCounts(assistantTurn, assistantMessage.ToolCalls, perToolFailCount);

                // 两级响应：检查是否需要警告或阻断
                string blockTool = null;
                string warnTool = null;
                foreach (var kvp in perToolFailCount)
                {
                    int effectiveBlock = GetEffectiveThreshold(kvp.Key, toolFailBlockThreshold);
                    int effectiveWarn = GetEffectiveThreshold(kvp.Key, toolFailWarningThreshold);

                    if (kvp.Value >= effectiveBlock)
                    {
                        blockTool = kvp.Key;
                        break;
                    }
                    else if (kvp.Value >= effectiveWarn && !warnedTools.Contains(kvp.Key))
                    {
                        warnTool = kvp.Key;
                    }
                }

                // Level 1: 警告 — 注入降级提示但不中断循环
                if (warnTool != null && blockTool == null)
                {
                    warnedTools.Add(warnTool);
                    int failCount = perToolFailCount[warnTool];
                    int effectiveBlock = GetEffectiveThreshold(warnTool, toolFailBlockThreshold);

                    AgentCoreLog.Warning($"[AgentCore]{logPrefix} Tool '{warnTool}' has failed {failCount} times (block at {effectiveBlock}). Sending degradation warning.");

                    _messages.Add(ChatMessage.System(
                        $"[SYSTEM WARNING] Tool '{warnTool}' 已连续失败 {failCount} 次。" +
                        $"如果继续失败到 {effectiveBlock} 次将被强制中断。" +
                        "请尝试不同的参数、方法或工具来解决问题。如果确认该工具无法完成任务，请直接告知用户。"));
                }

                // Level 2: 阻断 — 强制退出工具循环
                bool shouldForceExit = consecutiveAllFailRounds >= allToolsFailBlockThreshold || blockTool != null;
                if (shouldForceExit)
                {
                    string reason = blockTool != null
                        ? $"Tool '{blockTool}' has failed {perToolFailCount[blockTool]} consecutive times (threshold: {GetEffectiveThreshold(blockTool, toolFailBlockThreshold)})"
                        : $"All tool calls have failed consecutively {consecutiveAllFailRounds} rounds (threshold: {allToolsFailBlockThreshold})";

                    AgentCoreLog.Warning($"[AgentCore]{logPrefix} {reason}. Forcing final response.");

                    _messages.Add(ChatMessage.System(
                        "[SYSTEM] " + reason + "。" +
                        "你现在必须立即停止调用任何工具，直接用纯文本向用户解释问题。" +
                        "总结你之前尝试做什么以及哪里出了问题。不要再发起任何 tool_call。" +
                        "用户可以发送新消息继续对话，届时失败计数将重置。"));

                    assistantTurn.IsStreaming = true;
                    SetState(AgentState.Thinking);
                    var finalMessage = await CallLLMStreamAsync(assistantTurn, toolDefinitions, ct);

                    if (finalMessage != null && finalMessage.ToolCalls != null && finalMessage.ToolCalls.Count > 0)
                    {
                        AgentCoreLog.Warning($"[AgentCore]{logPrefix} LLM returned {finalMessage.ToolCalls.Count} tool call(s) in final round despite stop instruction. Ignoring.");
                        finalMessage.ToolCalls.Clear();
                    }

                    HandleFinalResponse(finalMessage, assistantTurn);
                    break;
                }

                // 重复调用检测：同一工具对同一目标反复调用（即使工具返回成功）
                UpdatePerToolTargetCallCounts(assistantMessage.ToolCalls, perToolTargetCallCount);

                string repeatBlockTarget = null;
                string repeatWarnTarget = null;
                foreach (var kvp in perToolTargetCallCount)
                {
                    if (kvp.Value >= toolTargetRepeatBlockThreshold)
                    {
                        repeatBlockTarget = kvp.Key;
                        break;
                    }
                    else if (kvp.Value >= toolTargetRepeatWarningThreshold && !warnedToolTargets.Contains(kvp.Key))
                    {
                        repeatWarnTarget = kvp.Key;
                    }
                }

                if (repeatWarnTarget != null && repeatBlockTarget == null)
                {
                    warnedToolTargets.Add(repeatWarnTarget);
                    var (warnToolName, warnTarget) = SplitToolTargetKey(repeatWarnTarget);
                    AgentCoreLog.Warning($"[AgentCore]{logPrefix} Tool '{warnToolName}' has been called {perToolTargetCallCount[repeatWarnTarget]} times on target '{warnTarget}'. Sending repetition warning.");

                    _messages.Add(ChatMessage.System(
                        $"[SYSTEM WARNING] Tool '{warnToolName}' 已连续调用 {perToolTargetCallCount[repeatWarnTarget]} 次针对同一目标 '{warnTarget}'。" +
                        $"如果继续重复调用到 {toolTargetRepeatBlockThreshold} 次将被强制中断。" +
                        "请确认任务是否已完成：如果已完成，请直接回复用户结果；如果陷入僵局，请向用户说明当前状态和卡点，不要继续盲目重试。"));
                }

                if (repeatBlockTarget != null)
                {
                    var (blockToolName, blockTarget) = SplitToolTargetKey(repeatBlockTarget);
                    string reason = $"Tool '{blockToolName}' has been called {perToolTargetCallCount[repeatBlockTarget]} times on the same target '{blockTarget}' (threshold: {toolTargetRepeatBlockThreshold})";

                    AgentCoreLog.Warning($"[AgentCore]{logPrefix} {reason}. Forcing final response.");

                    _messages.Add(ChatMessage.System(
                        "[SYSTEM] " + reason + "。" +
                        "你现在必须立即停止调用任何工具，直接用纯文本向用户解释：你刚才反复尝试做什么、当前进展如何、遇到了什么卡点。" +
                        "不要再发起任何 tool_call。用户可以发送新消息继续对话。"));

                    assistantTurn.IsStreaming = true;
                    SetState(AgentState.Thinking);
                    var finalMessage = await CallLLMStreamAsync(assistantTurn, toolDefinitions, ct);

                    if (finalMessage != null && finalMessage.ToolCalls != null && finalMessage.ToolCalls.Count > 0)
                    {
                        AgentCoreLog.Warning($"[AgentCore]{logPrefix} LLM returned {finalMessage.ToolCalls.Count} tool call(s) in final round despite stop instruction. Ignoring.");
                        finalMessage.ToolCalls.Clear();
                    }

                    HandleFinalResponse(finalMessage, assistantTurn);
                    break;
                }

                // 重置流式状态，准备下一轮
                assistantTurn.IsStreaming = true;
            }

            // 检查是否因达到限制（轮次上限或 Token Budget）而退出，触发总结
            bool reachedRoundLimit = currentRound >= maxRounds;
            bool reachedTokenBudget = tokenBudget > 0 && accumulatedTokens >= tokenBudget;

            if ((reachedRoundLimit || reachedTokenBudget) && CurrentState != AgentState.Idle)
            {
                string limitReason = reachedTokenBudget
                    ? $"Token 预算已耗尽（已消耗 {accumulatedTokens:N0}，预算 {tokenBudget:N0}）"
                    : $"已达到工具调用轮次上限（{maxRounds} 轮）";

                AgentCoreLog.Warning($"[AgentCore]{logPrefix} {limitReason}. Requesting LLM to summarize.");

                _messages.Add(ChatMessage.System(
                    "[SYSTEM] " + limitReason + "。" +
                    "你现在必须立即停止调用任何工具，直接用纯文本总结当前已完成的工作进度和结果。" +
                    "不要再发起任何 tool_call。"));

                assistantTurn.IsStreaming = true;
                SetState(AgentState.Thinking);
                var summaryMessage = await CallLLMStreamAsync(assistantTurn, toolDefinitions, ct);

                if (summaryMessage != null && summaryMessage.ToolCalls != null && summaryMessage.ToolCalls.Count > 0)
                {
                    AgentCoreLog.Warning($"[AgentCore]{logPrefix} LLM returned {summaryMessage.ToolCalls.Count} tool call(s) in summary round despite stop instruction. Ignoring.");
                    summaryMessage.ToolCalls.Clear();
                }

                HandleFinalResponse(summaryMessage, assistantTurn);
            }

            // 发送循环结束事件
            EmitEvent(AgentEvent.LoopCompleted(currentRound));

            // v1.8.8: turn 结束时 flush Silent buffer.
            // Silent 模式下, 上面所有 EmitEvent 已被写入 _silentBuffer 而没 marshal 到主线程.
            // FlushSilentBuffer 会把 buffer 内容 (含刚 emit 的 LoopCompleted) 逐个 RunOnMainThread,
            // 一波集中送到 UI. Batched 模式下 buffer 是空的, FlushSilentBuffer 短路返回.
            FlushSilentBuffer();

            // 自动保存会话
            try
            {
                SessionManager.Instance.AutoSave(
                    new List<ChatMessage>(_messages),
                    new List<ConversationTurn>(_conversationTurns),
                    _compressionMetrics);
            }
            catch (Exception saveEx)
            {
                AgentCoreLog.Warning($"[AgentCore]{logPrefix} Auto-save failed: {saveEx.Message}");
            }
        }

        /// <summary>
        /// 处理 LLM 的最终文本响应（无 tool_calls 的纯文本回复）。
        /// <para>
        /// 将完整的 assistant 消息添加到 LLM 历史，
        /// 更新 UI 轮次状态，并发送 <see cref="AgentEventType.AssistantMessage"/> 事件。
        /// </para>
        /// </summary>
        /// <param name="assistantMessage">LLM 返回的完整 assistant 消息</param>
        /// <param name="assistantTurn">当前助手对话轮次</param>
        private void HandleFinalResponse(ChatMessage assistantMessage, ConversationTurn assistantTurn)
        {
            CompleteReasoningIfNeeded(assistantTurn);

            // 标记流式结束
            assistantTurn.IsStreaming = false;

            // 将完整的助手消息添加到 LLM 历史；RawAssistantContent 仅留在 ConversationTurn。
            if (assistantMessage != null)
            {
                PrepareAssistantMessageForHistory(assistantMessage, assistantTurn);
                _messages.Add(assistantMessage);

                // 确保 UI 轮次内容与清洗后的 LLM 历史一致。
                // 用 IsNullOrWhiteSpace：GLM-5.2 reasoning 吃满预算时可能返回纯空白符（空格/换行）content，
                // 若用 IsNullOrEmpty 会把空白符当有效正文赋值，导致后续 fallback 判断失效、留下空正文气泡。
                if (!string.IsNullOrWhiteSpace(assistantMessage.Content))
                {
                    assistantTurn.Content = assistantMessage.Content;
                }
            }
            else
            {
                // 兜底：如果返回 null，使用流式累积的已清洗内容
                _messages.Add(ChatMessage.Assistant(assistantTurn.Content));
            }

            // 空正文检测（含纯空白符）：reasoning-only 回复（GLM 把预算全用在思考、没输出正文）不再填
            // "[系统提示]" 占位文本，而是标记为空 —— UI 侧据此移除空正文气泡（保留 reasoning 折叠区）。
            bool isBlankFinalContent = string.IsNullOrWhiteSpace(assistantTurn.Content);
            if (isBlankFinalContent)
            {
                // 归一化为空串，避免空白符流入下游（Node A/B 分析、历史）造成误判。
                assistantTurn.Content = string.Empty;
                AgentCoreLog.Warning("[AgentCore] HandleFinalResponse: Assistant content is empty/whitespace (reasoning-only turn); UI will drop the empty content bubble.");
            }

            // Phase 9: Node A 完成后的分派 — 若结论 = 反问用户, 进入 WaitingForClarification 状态
            //   注意: 使用 assistantTurn.RawAssistantContent(未剥离 challenge 块)判定, 因为 [CLARIFICATION NEEDED] 可能在 challenge 块之外
            string rawForNodeAAnalysis = !string.IsNullOrEmpty(assistantTurn.RawAssistantContent)
                ? assistantTurn.RawAssistantContent
                : assistantTurn.Content;
            string userMessageForContext = GetLastUserMessageContent();
            bool entersClarification = HandleNodeAConclusionForFinalResponse(userMessageForContext, assistantTurn, rawForNodeAAnalysis);

            // Phase 9: Node B 触发(仅当未进入 WaitingForClarification 且总开关 selfChallengeEnabled=true)
            //   Node B 与 Node A 共享单一开关(v1.5.0-alpha 极简哲学: 一开全开)
            //   ADR: self-challenge-model-tier-escape — 高级模型逃逸 Node B, 避免与 native reasoning 重复
            //   ADR §3.4 B1 — Node B 触发前进入 ReviewingAnswer 状态, 隔离本轮数据, 完成时由 TriggerNodeBAsync 恢复 Idle
            if (!entersClarification)
            {
                var settings = AgentCoreSettings.instance;
                bool nodeBShouldRun = settings.selfChallengeEnabled &&
                    !(settings.selfChallengeEscapeEnabled &&
                      ModelCapabilityDetector.HasNativeReasoning(settings.llmModel));

                if (nodeBShouldRun)
                {
                    SetState(AgentState.ReviewingAnswer);
                    _ = TriggerNodeBAsync(assistantMessage, assistantTurn, _currentSelfChallengeData);
                }
            }

            // 发送完整助手消息事件
            EmitEvent(AgentEvent.AssistantMessage(assistantTurn.Content, assistantTurn.Id));
            ResetReasoningRuntimeState();
        }

        /// <summary>
        /// 获取最后一条 user message 的内容, 供 Node A / Node B 分析使用。
        /// </summary>
        private string GetLastUserMessageContent()
        {
            for (int i = _messages.Count - 1; i >= 0; i--)
            {
                if (_messages[i].Role == "user")
                    return _messages[i].Content;
            }
            return string.Empty;
        }

        /// <summary>
        /// 触发 Node B(Answer Self-Challenge)独立 LLM 调用, 并根据 verdict 处理 REVISE / BLOCK 分支。
        /// 该方法是 fire-and-forget, 不阻塞主循环 — 因为设计文档 §1.3.2 允许 Node B 异步完成。
        /// ADR §3.4 B1: 接收 turnBoundData 参数隔离本轮 SelfChallengeData, 避免跨 turn 实例字段覆盖。
        ///   完成时(任何路径)恢复 Idle 状态(若仍处于 ReviewingAnswer)。
        /// </summary>
        private async System.Threading.Tasks.Task TriggerNodeBAsync(
            ChatMessage assistantMessage,
            ConversationTurn assistantTurn,
            SelfChallengeData turnBoundData)
        {
            try
            {
                var draftContent = assistantTurn?.Content ?? string.Empty;
                var userMessage = GetLastUserMessageContent();
                var ct = _currentCts?.Token ?? System.Threading.CancellationToken.None;

                var reviewResult = await InvokeNodeBAsync(draftContent, userMessage, assistantTurn, ct, turnBoundData);

                if (reviewResult.Skipped) return;

                // REVISE: 重新生成 draft(v0.10 §0.4: 新 draft 不再过 Node B)
                if (reviewResult.Verdict == NodeBVerdict.REVISE && reviewResult.ReviseIssues != null && reviewResult.ReviseIssues.Count > 0)
                {
                    EmitEvent(AgentEvent.AnswerChallengeRegenerating(assistantTurn.SelfChallenge, assistantTurn.Id));
                    await RegenerateDraftForReviseAsync(assistantMessage, assistantTurn, reviewResult.ReviseIssues, ct);
                    if (assistantTurn.SelfChallenge != null)
                        assistantTurn.SelfChallenge.DraftRegenerated = true;
                    EmitEvent(AgentEvent.AnswerChallengeRegenerated(assistantTurn.SelfChallenge, assistantTurn.Id));
                }

                // BLOCK: 需要触发验证性 tool call, 此处 v1.5.0-alpha 仅记录, 完整实施留给 v1.5.0-beta
                if (reviewResult.Verdict == NodeBVerdict.BLOCK)
                {
                    AgentCoreLog.Warning("[AgentCore][SelfChallenge] Node B verdict = BLOCK. Verification-loop back to tool loop not implemented in v1.5.0-alpha; accepting draft with warning.");
                }
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"[AgentCore][SelfChallenge] Node B invocation failed: {ex.Message}");
            }
            finally
            {
                // ADR §3.4 B1: Node B 生命周期结束 → 恢复 Idle
                //   仅当仍处于 ReviewingAnswer 时才恢复; 若期间用户已取消/进入其他状态, 不覆盖
                if (CurrentState == AgentState.ReviewingAnswer)
                {
                    SetState(AgentState.Idle);
                }
            }
        }

        /// <summary>
        /// Node B verdict = REVISE 时, 用 reviewer issues 作为 feedback, 让 LLM 重新生成 final response。
        /// v0.10 §0.4: 新 draft 不再过 Node B, 单次不复审。
        /// </summary>
        private async System.Threading.Tasks.Task RegenerateDraftForReviseAsync(
            ChatMessage originalDraft,
            ConversationTurn assistantTurn,
            IReadOnlyList<string> reviseIssues,
            System.Threading.CancellationToken ct)
        {
            try
            {
                // 追加 feedback 到主历史
                var feedback = AnswerChallengePromptBuilder.BuildDraftRegenerationFeedback(reviseIssues);
                _messages.Add(ChatMessage.System(feedback));

                AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore][SelfChallenge] REVISE: regenerating draft with {reviseIssues.Count} issue(s).");

                assistantTurn.IsStreaming = true;
                SetState(AgentState.Thinking);

                // 复用 CallLLMStreamAsync 触发流式重生成, 但**禁用 Node A 抽取**(Node A 已完成)
                var newMessage = await CallLLMStreamAsync(assistantTurn, tools: null, ct);

                if (newMessage != null)
                {
                    PrepareAssistantMessageForHistory(newMessage, assistantTurn);
                    // 替换主历史中最后一条 assistant message
                    for (int i = _messages.Count - 1; i >= 0; i--)
                    {
                        if (_messages[i] == originalDraft)
                        {
                            _messages[i] = newMessage;
                            break;
                        }
                    }

                    if (!string.IsNullOrEmpty(newMessage.Content))
                    {
                        assistantTurn.Content = newMessage.Content;
                        EmitEvent(AgentEvent.AssistantMessage(assistantTurn.Content, assistantTurn.Id));
                    }
                }

                assistantTurn.IsStreaming = false;
                SetState(AgentState.Idle);
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"[AgentCore][SelfChallenge] Draft regeneration failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 清洗 assistant message 中的可见规划 trace，并把原始内容保存在 UI/session 层。
        /// </summary>
        /// <param name="assistantMessage">LLM 返回的 assistant message。</param>
        /// <param name="assistantTurn">当前助手对话轮次。</param>
        private void PrepareAssistantMessageForHistory(ChatMessage assistantMessage, ConversationTurn assistantTurn)
        {
            if (assistantMessage == null || assistantTurn == null)
                return;

            var rawContent = assistantMessage.Content ?? string.Empty;
            if (!string.IsNullOrEmpty(rawContent))
            {
                assistantTurn.RawAssistantContent = rawContent;
            }

            // Phase 9 v0.10 §0.6: 剥离主历史里的 SelfChallenge 块, 避免 token 无谓膨胀
            //   完整 challenge 块只保留在 SelfChallengeData (供 UI + Session JSON)
            var challengeStripped = StripChallengeBlocks(rawContent);
            if (!string.Equals(challengeStripped, rawContent, StringComparison.Ordinal))
            {
                assistantMessage.Content = challengeStripped;
                rawContent = challengeStripped;
            }

            var finalResult = VisiblePlanningTraceExtractor.FinalizeContent(rawContent);
            assistantTurn.PlanningTraceState = finalResult.State;

            if (!string.IsNullOrEmpty(finalResult.Reasoning))
            {
                var previousReasoning = assistantTurn.Reasoning ?? string.Empty;
                var mergedReasoning = MergeReasoningText(previousReasoning, finalResult.Reasoning);
                var appendedReasoning = GetAppendedReasoning(previousReasoning, mergedReasoning, finalResult.Reasoning);

                assistantTurn.Reasoning = mergedReasoning;
                assistantTurn.ReasoningSource = MergeReasoningSource(assistantTurn.ReasoningSource, ThinkingTraceSource.VisiblePlanningTrace);
                EmitFinalizedReasoningIfNeeded(assistantTurn, appendedReasoning);
            }

            if (finalResult.State == VisiblePlanningTraceState.Completed)
            {
                assistantMessage.Content = finalResult.Content;
                assistantTurn.Content = finalResult.Content;
            }
            else if (!string.IsNullOrEmpty(rawContent))
            {
                assistantTurn.Content = rawContent;
            }
        }

        /// <summary>
        /// 发送最终兜底抽取到的 reasoning 内容，确保非流式可见规划 trace 也能立即更新 ThinkingDrawer。
        /// </summary>
        /// <param name="assistantTurn">当前助手对话轮次。</param>
        /// <param name="appendedReasoning">本次最终抽取新增的 reasoning 文本。</param>
        private void EmitFinalizedReasoningIfNeeded(ConversationTurn assistantTurn, string appendedReasoning)
        {
            if (assistantTurn == null || string.IsNullOrEmpty(appendedReasoning))
                return;

            EmitEvent(AgentEvent.ReasoningToken(appendedReasoning, assistantTurn.Id, assistantTurn.ReasoningSource));

            if (_reasoningActive)
            {
                CompleteReasoningIfNeeded(assistantTurn);
                return;
            }

            EmitEvent(AgentEvent.ReasoningCompleted(assistantTurn.Id, assistantTurn.ReasoningDurationMs, assistantTurn.ReasoningSource));
        }

        /// <summary>
        /// 获取最终兜底抽取相对已有 reasoning 的新增文本，避免重复发送 UI 事件。
        /// </summary>
        /// <param name="previous">已有 reasoning 文本。</param>
        /// <param name="merged">合并后的 reasoning 文本。</param>
        /// <param name="fallback">无法计算差量时使用的兜底文本。</param>
        /// <returns>本次新增的 reasoning 文本。</returns>
        private static string GetAppendedReasoning(string previous, string merged, string fallback)
        {
            if (string.IsNullOrEmpty(merged)) return string.Empty;
            if (string.IsNullOrEmpty(previous)) return merged;
            if (string.Equals(previous, merged, StringComparison.Ordinal)) return string.Empty;
            if (merged.StartsWith(previous, StringComparison.Ordinal))
            {
                return merged.Substring(previous.Length).TrimStart('\r', '\n');
            }

            return fallback ?? string.Empty;
        }

        /// <summary>
        /// 合并已流式抽取与最终兜底抽取的 reasoning 文本，避免重复追加同一段内容。
        /// </summary>
        /// <param name="current">当前 reasoning 文本。</param>
        /// <param name="next">新增 reasoning 文本。</param>
        /// <returns>合并后的 reasoning 文本。</returns>
        private static string MergeReasoningText(string current, string next)
        {
            if (string.IsNullOrEmpty(next)) return current ?? string.Empty;
            if (string.IsNullOrEmpty(current)) return next;
            if (current.Contains(next)) return current;
            return current + "\n" + next;
        }

        /// <summary>
        /// 重置当前 LLM 调用的 reasoning 运行态。
        /// </summary>
        private void ResetReasoningRuntimeState()
        {
            _reasoningStartedUtc = null;
            _reasoningActive = false;
            _reasoningCompleted = false;
            _activeReasoningSource = ThinkingTraceSource.None;
            _visiblePlanningTraceExtractor.Reset();
        }

        /// <summary>
        /// 检查本轮所有工具调用是否全部失败。
        /// <para>
        /// 从 <see cref="ConversationTurn.ToolCalls"/> 列表末尾取最近 N 条记录
        /// （N = 本轮 LLM 返回的 tool_calls 数量），检查是否全部 <see cref="ToolCallInfo.Success"/> 为 false。
        /// </para>
        /// </summary>
        /// <param name="assistantTurn">当前助手对话轮次</param>
        /// <param name="toolCallCount">本轮 LLM 返回的 tool_calls 数量</param>
        /// <returns>如果本轮所有工具调用都失败则返回 true，否则返回 false</returns>
        private static bool CheckAllToolCallsFailed(ConversationTurn assistantTurn, int toolCallCount)
        {
            if (assistantTurn.ToolCalls == null || assistantTurn.ToolCalls.Count == 0 || toolCallCount <= 0)
            {
                return false;
            }

            // 从列表末尾取本轮的工具调用记录
            int startIndex = Math.Max(0, assistantTurn.ToolCalls.Count - toolCallCount);
            for (int i = startIndex; i < assistantTurn.ToolCalls.Count; i++)
            {
                if (assistantTurn.ToolCalls[i].Success)
                {
                    return false; // 至少有一个成功，不算全部失败
                }
            }

            return true; // 全部失败
        }

        /// <summary>
        /// 更新每个工具的连续失败计数。
        /// <para>
        /// 遍历本轮的工具调用结果，对于失败的工具增加其连续失败计数，
        /// 对于成功的工具重置其计数为 0。这样即使一轮中有其他成功的工具，
        /// 也能追踪到某个特定工具的连续失败情况。
        /// </para>
        /// </summary>
        /// <param name="assistantTurn">当前助手对话轮次</param>
        /// <param name="toolCalls">本轮 LLM 返回的工具调用列表</param>
        /// <param name="perToolFailCount">每个工具的连续失败计数字典</param>
        private static void UpdatePerToolFailCounts(
            ConversationTurn assistantTurn,
            List<ToolCall> toolCalls,
            Dictionary<string, int> perToolFailCount)
        {
            if (assistantTurn.ToolCalls == null || toolCalls == null || toolCalls.Count == 0)
                return;

            // 从列表末尾取本轮的工具调用记录
            int startIndex = Math.Max(0, assistantTurn.ToolCalls.Count - toolCalls.Count);
            for (int i = startIndex; i < assistantTurn.ToolCalls.Count; i++)
            {
                var callInfo = assistantTurn.ToolCalls[i];
                var toolName = callInfo.ToolName;
                if (string.IsNullOrEmpty(toolName)) continue;

                if (!callInfo.Success)
                {
                    // 失败：增加该工具的连续失败计数
                    if (perToolFailCount.ContainsKey(toolName))
                        perToolFailCount[toolName]++;
                    else
                        perToolFailCount[toolName] = 1;

                    AgentCoreLog.Warning($"[AgentCore] Tool '{toolName}' failed. " +
                                     $"Consecutive failures for this tool: {perToolFailCount[toolName]}");
                }
                else
                {
                    // 成功：重置该工具的连续失败计数
                    if (perToolFailCount.ContainsKey(toolName))
                        perToolFailCount[toolName] = 0;
                }
            }
        }

        /// <summary>
        /// 更新每个（工具 + 目标）组合的累计调用次数。
        /// <para>
        /// 目标从工具参数中按常见路径字段（path/script_path/file_path/asset_path/directory/name/query）提取。
        /// 该计数用于检测 LLM 是否在同一个文件/对象上反复调用同一工具而陷入循环。
        /// </para>
        /// </summary>
        /// <param name="toolCalls">本轮 LLM 返回的工具调用列表</param>
        /// <param name="perToolTargetCallCount">（工具 + 目标）累计计数</param>
        private static void UpdatePerToolTargetCallCounts(
            List<ToolCall> toolCalls,
            Dictionary<string, int> perToolTargetCallCount)
        {
            if (toolCalls == null || toolCalls.Count == 0)
                return;

            foreach (var toolCall in toolCalls)
            {
                var toolName = toolCall.Function?.Name;
                if (string.IsNullOrEmpty(toolName))
                    continue;

                var targetKey = ExtractToolTargetKey(toolName, toolCall.Function?.Arguments);
                var dictKey = BuildToolTargetKey(toolName, targetKey);

                if (perToolTargetCallCount.ContainsKey(dictKey))
                    perToolTargetCallCount[dictKey]++;
                else
                    perToolTargetCallCount[dictKey] = 1;
            }
        }

        /// <summary>
        /// 从工具参数 JSON 中提取最能代表“操作目标”的键值。
        /// </summary>
        /// <param name="toolName">工具名称</param>
        /// <param name="argumentsJson">工具参数 JSON 字符串</param>
        /// <returns>目标键值；无法提取时返回空字符串</returns>
        private static string ExtractToolTargetKey(string toolName, string argumentsJson)
        {
            if (string.IsNullOrEmpty(argumentsJson))
                return string.Empty;

            var args = JsonHelper.ParseObject(argumentsJson);
            if (args == null)
                return string.Empty;

            // 优先匹配常见的路径/目标字段
            var candidateKeys = new[]
            {
                "script_path",
                "asset_path",
                "file_path",
                "path",
                "directory",
                "name",
                "query",
                "class_name",
                "method_name",
                "field_name"
            };

            foreach (var key in candidateKeys)
            {
                if (args.TryGetValue(key, out var token) && token != null)
                {
                    var value = token.ToString().Trim();
                    if (!string.IsNullOrEmpty(value))
                        return value;
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// 构建（工具 + 目标）字典键。
        /// </summary>
        private static string BuildToolTargetKey(string toolName, string targetKey)
        {
            return $"{toolName}::{targetKey ?? string.Empty}";
        }

        /// <summary>
        /// 将 <see cref="BuildToolTargetKey"/> 生成的键拆分为工具名和目标。
        /// </summary>
        private static (string ToolName, string Target) SplitToolTargetKey(string combinedKey)
        {
            if (string.IsNullOrEmpty(combinedKey))
                return (string.Empty, string.Empty);

            var parts = combinedKey.Split(new[] { "::" }, StringSplitOptions.None);
            if (parts.Length >= 2)
                return (parts[0], parts[1]);

            return (combinedKey, string.Empty);
        }

        /// <summary>
        /// 根据工具的风险等级计算有效阈值。
        /// <para>
        /// 低风险工具（ReadOnly / Low）获得 2 倍阈值宽容度，
        /// 因为这些工具的失败通常是参数或环境问题，不会造成破坏性后果。
        /// </para>
        /// </summary>
        /// <param name="toolName">工具名称</param>
        /// <param name="baseThreshold">基础阈值</param>
        /// <returns>考虑风险等级后的有效阈值</returns>
        private static int GetEffectiveThreshold(string toolName, int baseThreshold)
        {
            if (string.IsNullOrEmpty(toolName))
                return baseThreshold;

            var tool = ToolRegistry.Instance.GetTool(toolName);
            if (tool == null)
                return baseThreshold;

            var riskLevel = tool.Metadata.RiskLevel;
            // 低风险工具（ReadOnly / Low）获得 2 倍宽容度
            if (riskLevel <= ToolRiskLevel.Low)
            {
                return baseThreshold * 2;
            }

            return baseThreshold;
        }
    }
}
