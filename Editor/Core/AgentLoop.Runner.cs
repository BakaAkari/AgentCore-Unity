using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Config;
using AgentCore.Editor.LLM;
using AgentCore.Editor.Session;
using AgentCore.Editor.Tools;
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
            int currentRound = 0;

            // 连续失败检测
            int consecutiveAllFailRounds = 0;
            const int maxConsecutiveFailures = 3;

            // 单工具连续失败追踪
            var perToolFailCount = new Dictionary<string, int>();
            const int maxPerToolFailures = 3;

            while (currentRound < maxRounds)
            {
                currentRound++;
                EmitEvent(AgentEvent.LoopRoundStarted(currentRound, maxRounds));

                if (ct.IsCancellationRequested)
                {
                    Debug.Log($"[AgentCore]{logPrefix} Cancelled before round {currentRound}.");
                    break;
                }

                // 调用 LLM（流式）
                SetState(AgentState.Thinking);
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

                // 有 tool_calls — 执行工具
                Debug.Log($"[AgentCore]{logPrefix} Round {currentRound}: LLM returned {assistantMessage.ToolCalls.Count} tool call(s).");
                _messages.Add(assistantMessage);

                SetState(AgentState.ExecutingTool);
                await ExecuteToolCallsAsync(assistantMessage.ToolCalls, assistantTurn, ct);

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
                                     $"Consecutive failure rounds: {consecutiveAllFailRounds}/{maxConsecutiveFailures}");
                }
                else
                {
                    consecutiveAllFailRounds = 0;
                }

                // 单工具连续失败追踪
                UpdatePerToolFailCounts(assistantTurn, assistantMessage.ToolCalls, perToolFailCount);
                string repeatedFailTool = null;
                foreach (var kvp in perToolFailCount)
                {
                    if (kvp.Value >= maxPerToolFailures)
                    {
                        repeatedFailTool = kvp.Key;
                        break;
                    }
                }

                // 判断是否需要强制退出
                bool shouldForceExit = consecutiveAllFailRounds >= maxConsecutiveFailures || repeatedFailTool != null;
                if (shouldForceExit)
                {
                    string reason = repeatedFailTool != null
                        ? $"Tool '{repeatedFailTool}' has failed {maxPerToolFailures} consecutive times"
                        : $"All tool calls have failed consecutively {maxConsecutiveFailures} rounds";

                    Debug.LogWarning($"[AgentCore]{logPrefix} {reason}. Forcing final response.");

                    _messages.Add(ChatMessage.System(
                        "[SYSTEM] " + reason + "。" +
                        "你现在必须立即停止调用任何工具，直接用纯文本向用户解释问题。" +
                        "总结你之前尝试做什么以及哪里出了问题。不要再发起任何 tool_call。"));

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

            // 检查是否因达到最大轮次而退出
            if (currentRound >= maxRounds && CurrentState != AgentState.Idle)
            {
                Debug.LogWarning($"[AgentCore]{logPrefix} Reached max tool call rounds ({maxRounds}). Requesting LLM to summarize.");

                _messages.Add(ChatMessage.System(
                    "[SYSTEM] 你已达到工具调用上限（" + maxRounds + "轮）。" +
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
            // 标记流式结束
            assistantTurn.IsStreaming = false;

            // 将完整的助手消息添加到 LLM 历史
            if (assistantMessage != null)
            {
                _messages.Add(assistantMessage);

                // 确保 UI 轮次内容与 LLM 返回一致
                if (!string.IsNullOrEmpty(assistantMessage.Content))
                {
                    assistantTurn.Content = assistantMessage.Content;
                }
            }
            else
            {
                // 兜底：如果返回 null，使用流式累积的内容
                _messages.Add(ChatMessage.Assistant(assistantTurn.Content));
            }

            // 发送完整助手消息事件
            EmitEvent(AgentEvent.AssistantMessage(assistantTurn.Content, assistantTurn.Id));
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
    }
}
