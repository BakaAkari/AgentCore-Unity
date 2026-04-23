using System;
using System.Collections.Generic;
using AgentCore.Editor.LLM;
using UnityEngine;

namespace AgentCore.Editor.Core
{
    /// <summary>
    /// 上下文窗口管理器 — 在发送 LLM 请求前截断消息历史。
    /// <para>
    /// 截断策略（滑动窗口）：
    /// <list type="number">
    ///   <item>永远保留：第一条 system 消息（system prompt）</item>
    ///   <item>永远保留：最后一轮完整的 tool_call + tool_result 对（如果有的话）</item>
    ///   <item>从旧到新删除：从 system 消息之后开始，逐条删除最旧的消息，直到总 token 数 &lt;= 可用 token 数</item>
    ///   <item>删除时保持完整性：如果删除一条 assistant 消息包含 tool_calls，必须同时删除对应的 tool result 消息</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class ContextWindowManager
    {
        #region 模型 Token 上限映射

        /// <summary>已知模型的最大 token 数映射表</summary>
        private static readonly Dictionary<string, int> ModelMaxTokensMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "claude-opus-4-6", 200000 },
            { "claude-sonnet-4-5-20250929", 200000 },
            { "claude-3-5-sonnet-20241022", 200000 },
            { "claude-3-opus-20240229", 200000 },
            { "claude-3-haiku-20240307", 200000 },
            { "gpt-4o", 128000 },
            { "gpt-4o-mini", 128000 },
            { "gpt-4-turbo", 128000 },
            { "gpt-4", 8192 },
            { "gpt-3.5-turbo", 16385 },
            { "deepseek-chat", 64000 },
            { "deepseek-coder", 64000 },
        };

        /// <summary>未知模型的默认最大 token 数</summary>
        private const int DefaultMaxTokens = 128000;

        #endregion

        #region 公开方法

        /// <summary>
        /// 根据模型名称返回最大 token 数。
        /// <para>
        /// 支持精确匹配和前缀匹配（如 "gpt-4o-2024-05-13" 会匹配 "gpt-4o"）。
        /// 未知模型默认返回 128000。
        /// </para>
        /// </summary>
        /// <param name="modelName">模型名称</param>
        /// <returns>模型的最大 token 数</returns>
        public static int GetModelMaxTokens(string modelName)
        {
            if (string.IsNullOrEmpty(modelName))
                return DefaultMaxTokens;

            // 精确匹配
            if (ModelMaxTokensMap.TryGetValue(modelName, out int exactMatch))
                return exactMatch;

            // 前缀匹配（处理带日期后缀的模型名，如 "gpt-4o-2024-05-13"）
            foreach (var kvp in ModelMaxTokensMap)
            {
                if (modelName.StartsWith(kvp.Key, StringComparison.OrdinalIgnoreCase))
                    return kvp.Value;
            }

            return DefaultMaxTokens;
        }

        /// <summary>
        /// 截断消息历史使其适应上下文窗口限制。
        /// <para>
        /// 返回截断后的消息列表（新列表），不修改原始数据。
        /// 截断策略：
        /// <list type="number">
        ///   <item>永远保留第一条 system 消息</item>
        ///   <item>永远保留最后一轮完整的 tool_call + tool_result 对</item>
        ///   <item>从 system 消息之后开始，逐条删除最旧的消息</item>
        ///   <item>tool_calls assistant 消息与对应的 tool result 消息成对删除</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <param name="messages">原始消息列表</param>
        /// <param name="maxTokens">模型的最大上下文窗口大小</param>
        /// <param name="reserveTokens">为 AI 回复预留的 token 数（默认 2000）</param>
        /// <returns>截断后的消息列表（新列表）</returns>
        public static List<ChatMessage> TrimToFit(List<ChatMessage> messages, int maxTokens, int reserveTokens = 2000)
        {
            if (messages == null || messages.Count == 0)
                return new List<ChatMessage>();

            int availableTokens = maxTokens - reserveTokens;
            if (availableTokens <= 0)
                availableTokens = maxTokens / 2;

            // 检查当前总 token 数是否已在限制内
            int currentTokens = TokenCounter.EstimateConversationTokens(messages);
            if (currentTokens <= availableTokens)
                return new List<ChatMessage>(messages);

            // 需要截断
            int beforeTokens = currentTokens;

            // 1. 分离 system 消息和对话消息
            ChatMessage systemMessage = null;
            int conversationStartIndex = 0;

            if (messages.Count > 0 && messages[0].Role == "system")
            {
                systemMessage = messages[0];
                conversationStartIndex = 1;
            }

            // 2. 计算 system 消息占用的 token
            int systemTokens = systemMessage != null ? TokenCounter.EstimateMessageTokens(systemMessage) : 0;
            int conversationBudget = availableTokens - systemTokens - 3; // 3 = 消息列表固定开销

            if (conversationBudget <= 0)
            {
                // system prompt 已超限，只保留 system prompt
                var minimal = new List<ChatMessage>();
                if (systemMessage != null) minimal.Add(systemMessage);
                Debug.LogWarning("[AgentCore] Context window trimmed: system prompt alone exceeds budget.");
                return minimal;
            }

            // 3. 提取对话消息
            var conversationMessages = new List<ChatMessage>();
            for (int i = conversationStartIndex; i < messages.Count; i++)
            {
                conversationMessages.Add(messages[i]);
            }

            // 4. 从旧到新删除消息，直到总 token 数在预算内
            var trimmedConversation = TrimConversation(conversationMessages, conversationBudget);

            // 5. 合并结果
            var result = new List<ChatMessage>();
            if (systemMessage != null) result.Add(systemMessage);
            result.AddRange(trimmedConversation);

            // 6. 输出日志
            int afterTokens = TokenCounter.EstimateConversationTokens(result);
            int removedCount = messages.Count - result.Count;
            Debug.Log($"[AgentCore] Context window trimmed: removed {removedCount} messages, {beforeTokens} → {afterTokens} tokens");

            return result;
        }

        #endregion

        #region 私有方法

        /// <summary>
        /// 从对话消息列表中截断旧消息，使总 token 数不超过预算。
        /// <para>
        /// 从后往前累加 token，找到可以保留的起始位置。
        /// 确保 tool_calls 和 tool response 成对保留或移除。
        /// </para>
        /// </summary>
        /// <param name="messages">对话消息列表（不含 system 消息）</param>
        /// <param name="tokenBudget">可用 token 预算</param>
        /// <returns>截断后的消息列表</returns>
        private static List<ChatMessage> TrimConversation(List<ChatMessage> messages, int tokenBudget)
        {
            if (messages.Count == 0)
                return new List<ChatMessage>();

            // 从后往前累加 token，找到可以保留的起始位置
            int totalTokens = 0;
            int keepFromIndex = messages.Count; // 初始值：不保留任何消息

            for (int i = messages.Count - 1; i >= 0; i--)
            {
                int msgTokens = TokenCounter.EstimateMessageTokens(messages[i]);
                if (totalTokens + msgTokens > tokenBudget)
                    break;
                totalTokens += msgTokens;
                keepFromIndex = i;
            }

            // 确保不会在 tool_calls 对中间截断
            keepFromIndex = AdjustForToolCallPairs(messages, keepFromIndex);

            // 如果调整后超出预算，继续向后移动直到在预算内
            // （跳过整个 tool_call 组）
            while (keepFromIndex < messages.Count)
            {
                int estimatedTokens = 0;
                for (int i = keepFromIndex; i < messages.Count; i++)
                {
                    estimatedTokens += TokenCounter.EstimateMessageTokens(messages[i]);
                }
                if (estimatedTokens <= tokenBudget)
                    break;

                // 跳过当前消息组（如果是 assistant+tool_calls，跳过整组）
                keepFromIndex = SkipMessageGroup(messages, keepFromIndex);
            }

            // 构建结果
            var result = new List<ChatMessage>();
            for (int i = keepFromIndex; i < messages.Count; i++)
            {
                result.Add(messages[i]);
            }

            return result;
        }

        /// <summary>
        /// 调整截断位置，确保不在 tool_calls 对中间截断。
        /// <para>
        /// 如果截断位置落在 tool response 消息上，向前移动到对应的 assistant 消息。
        /// 如果截断位置落在包含 tool_calls 的 assistant 消息上，
        /// 需要确保其后续的所有 tool response 也被保留。
        /// </para>
        /// </summary>
        /// <param name="messages">消息列表</param>
        /// <param name="index">初始截断位置</param>
        /// <returns>调整后的截断位置</returns>
        private static int AdjustForToolCallPairs(List<ChatMessage> messages, int index)
        {
            if (index >= messages.Count) return index;

            // 如果截断位置落在 tool response 上，向前移动到对应的 assistant message
            while (index < messages.Count && messages[index].Role == "tool")
            {
                index--;
                if (index < 0)
                {
                    index = 0;
                    break;
                }
            }

            // 如果截断位置落在包含 tool_calls 的 assistant 消息之后，
            // 但该 assistant 消息的 tool responses 还在截断范围外，
            // 需要向前移动以包含完整的 assistant 消息
            if (index > 0 && index < messages.Count)
            {
                // 检查前一条消息是否是包含 tool_calls 的 assistant 消息
                var prevMsg = messages[index - 1];
                if (prevMsg.Role == "assistant" && prevMsg.ToolCalls != null && prevMsg.ToolCalls.Count > 0)
                {
                    // 检查当前位置是否是该 assistant 消息的 tool response
                    if (messages[index].Role == "tool")
                    {
                        // 需要包含 assistant 消息
                        index--;
                    }
                }
            }

            return Math.Max(0, index);
        }

        /// <summary>
        /// 跳过一个消息组（单条消息或 assistant+tool_calls 组）。
        /// </summary>
        /// <param name="messages">消息列表</param>
        /// <param name="index">当前位置</param>
        /// <returns>跳过后的位置</returns>
        private static int SkipMessageGroup(List<ChatMessage> messages, int index)
        {
            if (index >= messages.Count) return index;

            var msg = messages[index];

            // 如果是包含 tool_calls 的 assistant 消息，跳过它和所有后续的 tool response
            if (msg.Role == "assistant" && msg.ToolCalls != null && msg.ToolCalls.Count > 0)
            {
                index++; // 跳过 assistant 消息
                // 跳过所有后续的 tool response
                while (index < messages.Count && messages[index].Role == "tool")
                {
                    index++;
                }
                return index;
            }

            // 普通消息，跳过一条
            return index + 1;
        }

        #endregion
    }
}
