using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Config;
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
                    Debug.LogWarning($"[AgentCore]{logPrefix} Token budget exceeded ({accumulatedTokens}/{tokenBudget}) at round {currentRound}. Triggering summary.");
                    break;
                }

                if (ct.IsCancellationRequested)
                {
                    Debug.Log($"[AgentCore]{logPrefix} Cancelled before round {currentRound}.");
                    break;
                }

                // 调用 LLM（流式）。先进入 Thinking 以确保 ChatWindow 已创建 AssistantTurnView，
                // 再发 LoopRoundStarted，避免 ToolCallGroup 降级挂到根消息列表。
                SetState(AgentState.Thinking);
                EmitEvent(AgentEvent.LoopRoundStarted(currentRound, maxRounds, accumulatedTokens));
                var assistantMessage = await CallLLMStreamAsync(assistantTurn, toolDefinitions, ct);

                if (ct.IsCancellationRequested)
                {
                    Debug.Log($"[AgentCore]{logPrefix} Cancelled during LLM call.");
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

                // 有 tool_calls — 执行工具。写入 LLM 历史前必须清洗可见规划 trace，避免 thinking 泄漏到上下文。
                Debug.Log($"[AgentCore]{logPrefix} Round {currentRound}: LLM returned {assistantMessage.ToolCalls.Count} tool call(s).");
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

                if (ct.IsCancellationRequested)
                {
                    Debug.Log($"[AgentCore]{logPrefix} Cancelled during tool execution.");
                    break;
                }

                // 连续失败检测
                bool allToolCallsFailed = CheckAllToolCallsFailed(assistantTurn, assistantMessage.ToolCalls.Count);
                if (allToolCallsFailed)
                {
                    consecutiveAllFailRounds++;
                    Debug.LogWarning($"[AgentCore]{logPrefix} All tool calls failed in round {currentRound}. " +
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

                    Debug.LogWarning($"[AgentCore]{logPrefix} Tool '{warnTool}' has failed {failCount} times (block at {effectiveBlock}). Sending degradation warning.");

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

                    Debug.LogWarning($"[AgentCore]{logPrefix} {reason}. Forcing final response.");

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
                        Debug.LogWarning($"[AgentCore]{logPrefix} LLM returned {finalMessage.ToolCalls.Count} tool call(s) in final round despite stop instruction. Ignoring.");
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
                    Debug.LogWarning($"[AgentCore]{logPrefix} Tool '{warnToolName}' has been called {perToolTargetCallCount[repeatWarnTarget]} times on target '{warnTarget}'. Sending repetition warning.");

                    _messages.Add(ChatMessage.System(
                        $"[SYSTEM WARNING] Tool '{warnToolName}' 已连续调用 {perToolTargetCallCount[repeatWarnTarget]} 次针对同一目标 '{warnTarget}'。" +
                        $"如果继续重复调用到 {toolTargetRepeatBlockThreshold} 次将被强制中断。" +
                        "请确认任务是否已完成：如果已完成，请直接回复用户结果；如果陷入僵局，请向用户说明当前状态和卡点，不要继续盲目重试。"));
                }

                if (repeatBlockTarget != null)
                {
                    var (blockToolName, blockTarget) = SplitToolTargetKey(repeatBlockTarget);
                    string reason = $"Tool '{blockToolName}' has been called {perToolTargetCallCount[repeatBlockTarget]} times on the same target '{blockTarget}' (threshold: {toolTargetRepeatBlockThreshold})";

                    Debug.LogWarning($"[AgentCore]{logPrefix} {reason}. Forcing final response.");

                    _messages.Add(ChatMessage.System(
                        "[SYSTEM] " + reason + "。" +
                        "你现在必须立即停止调用任何工具，直接用纯文本向用户解释：你刚才反复尝试做什么、当前进展如何、遇到了什么卡点。" +
                        "不要再发起任何 tool_call。用户可以发送新消息继续对话。"));

                    assistantTurn.IsStreaming = true;
                    SetState(AgentState.Thinking);
                    var finalMessage = await CallLLMStreamAsync(assistantTurn, toolDefinitions, ct);

                    if (finalMessage != null && finalMessage.ToolCalls != null && finalMessage.ToolCalls.Count > 0)
                    {
                        Debug.LogWarning($"[AgentCore]{logPrefix} LLM returned {finalMessage.ToolCalls.Count} tool call(s) in final round despite stop instruction. Ignoring.");
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

                Debug.LogWarning($"[AgentCore]{logPrefix} {limitReason}. Requesting LLM to summarize.");

                _messages.Add(ChatMessage.System(
                    "[SYSTEM] " + limitReason + "。" +
                    "你现在必须立即停止调用任何工具，直接用纯文本总结当前已完成的工作进度和结果。" +
                    "不要再发起任何 tool_call。"));

                assistantTurn.IsStreaming = true;
                SetState(AgentState.Thinking);
                var summaryMessage = await CallLLMStreamAsync(assistantTurn, toolDefinitions, ct);

                if (summaryMessage != null && summaryMessage.ToolCalls != null && summaryMessage.ToolCalls.Count > 0)
                {
                    Debug.LogWarning($"[AgentCore]{logPrefix} LLM returned {summaryMessage.ToolCalls.Count} tool call(s) in summary round despite stop instruction. Ignoring.");
                    summaryMessage.ToolCalls.Clear();
                }

                HandleFinalResponse(summaryMessage, assistantTurn);
            }

            // 发送循环结束事件
            EmitEvent(AgentEvent.LoopCompleted(currentRound));

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
                Debug.LogWarning($"[AgentCore]{logPrefix} Auto-save failed: {saveEx.Message}");
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

                // 确保 UI 轮次内容与清洗后的 LLM 历史一致
                if (!string.IsNullOrEmpty(assistantMessage.Content))
                {
                    assistantTurn.Content = assistantMessage.Content;
                }
            }
            else
            {
                // 兜底：如果返回 null，使用流式累积的已清洗内容
                _messages.Add(ChatMessage.Assistant(assistantTurn.Content));
            }

            // 兜底：如果最终内容仍为空，提供默认消息
            if (string.IsNullOrEmpty(assistantTurn.Content))
            {
                assistantTurn.Content = "[系统提示] 助手未返回任何内容。";
                Debug.LogWarning("[AgentCore] HandleFinalResponse: Assistant content is empty, using fallback message.");
            }

            // 发送完整助手消息事件
            EmitEvent(AgentEvent.AssistantMessage(assistantTurn.Content, assistantTurn.Id));
            ResetReasoningRuntimeState();
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

                    Debug.LogWarning($"[AgentCore] Tool '{toolName}' failed. " +
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
