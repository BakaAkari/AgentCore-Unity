using System;
using System.Collections.Generic;
using AgentCore.Editor.LLM;
using UnityEngine;
using AgentCore.Editor.Utils;

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

        /// <summary>
        /// 基于前缀的模型 token 上限映射表。
        /// 按前缀从长到短排列（更具体的前缀优先匹配）。
        /// 新增模型系列只需添加一行前缀即可，无需逐个版本枚举。
        /// </summary>
        private static readonly (string prefix, int maxTokens)[] ModelPrefixMap =
        {
            // Claude 系列 — 所有版本均为 200k
            ("claude-", 200000),

            // GPT-o 系列（o1/o3/o4）— 200k（必须在 gpt- 之前匹配）
            ("o1-", 200000),
            ("o3-", 200000),
            ("o4-", 200000),
            // GPT-4.5 / GPT-5 系列 — 128k（必须在 gpt-4 之前匹配）
            ("gpt-4.5", 128000),
            ("gpt-5", 128000),
            // GPT-4o 系列 — 128k（必须在 gpt-4 之前匹配）
            ("gpt-4o", 128000),
            // GPT-4 Turbo — 128k
            ("gpt-4-turbo", 128000),
            // GPT-4 基础版 — 128k（旧版 8k 已停用，现行 API 均为 128k）
            ("gpt-4", 128000),
            // GPT-3.5 系列 — 16k
            ("gpt-3.5", 16385),

            // DeepSeek V3 / V4 系列 — 128k（V2 及以上均为 128k）
            ("deepseek-v", 128000),
            // DeepSeek R 系列（推理模型）— 128k
            ("deepseek-r", 128000),
            // DeepSeek 其他系列 — 128k（保守估计）
            ("deepseek-", 128000),

            // Kimi / Moonshot 系列 — 128k
            ("kimi-", 128000),
            ("moonshot-", 128000),

            // Qwen 系列 — 128k
            ("qwen-", 128000),

            // GLM 系列（Z.ai）— 按版本细分，更具体前缀优先
            // GLM-5.2 — 部署版 max_model_len=200000（W4AFP8 量化变体，非 1M 规格）
            ("glm-5.2", 200000),
            // GLM-5 / GLM-5.1 / GLM-5-turbo / GLM-5v-turbo — 200k~262k
            ("glm-5", 202752),
            // GLM-4.5~4.7 — 128k~200k（取 200k 上限）
            ("glm-4", 200000),
            // GLM 其他系列（保守估计）
            ("glm-", 128000),

            // Gemini 系列 — 1M（Google 最新模型）
            ("gemini-", 1000000),

            // Llama 系列 — 128k（Meta Llama 3.1+）
            ("llama-3", 128000),
            ("llama3", 128000),

            // Mistral 系列 — 128k
            ("mistral-", 128000),
        };

        /// <summary>未知模型的默认最大 token 数（现代 LLM 最低公约数）</summary>
        private const int DefaultMaxTokens = 128000;

        #endregion

        #region 公开方法

        /// <summary>
        /// 根据模型名称返回最大 token 数。
        /// <para>
        /// 使用前缀匹配，按优先级从高到低扫描。
        /// 例如 "gpt-4o-2024-05-13" 匹配 "gpt-4o" 前缀返回 128000，
        /// 而 "gpt-4-0613" 匹配 "gpt-4" 前缀返回 8192。
        /// 未知模型默认返回 128000。
        /// </para>
        /// </summary>
        /// <param name="modelName">模型名称</param>
        /// <returns>模型的最大 token 数</returns>
        public static int GetModelMaxTokens(string modelName)
        {
            // 优先使用 ModelCapabilityProbe 的探测值（/v1/models 返回的实际 max_model_len）
            if (ModelCapabilityProbe.CachedMaxModelLen > 0)
                return ModelCapabilityProbe.CachedMaxModelLen;

            if (string.IsNullOrEmpty(modelName))
                return DefaultMaxTokens;

            // 前缀匹配（按优先级顺序，更具体的前缀排在前面）
            foreach (var (prefix, maxTokens) in ModelPrefixMap)
            {
                if (modelName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return maxTokens;
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
                AgentCoreLog.Warning("[AgentCore] Context window trimmed: system prompt alone exceeds budget.");
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
            AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] Context window trimmed: removed {removedCount} messages, {beforeTokens} → {afterTokens} tokens");

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

            // P2-3 fix: 使用增量计算代替 O(n²) 的重复遍历
            // 先计算从 keepFromIndex 开始的总 token 数
            int estimatedTokens = 0;
            for (int i = keepFromIndex; i < messages.Count; i++)
            {
                estimatedTokens += TokenCounter.EstimateMessageTokens(messages[i]);
            }

            // 如果调整后超出预算，继续向后移动直到在预算内
            // （跳过整个 tool_call 组，增量减去被跳过消息的 token）
            while (keepFromIndex < messages.Count && estimatedTokens > tokenBudget)
            {
                int nextIndex = SkipMessageGroup(messages, keepFromIndex);

                // 增量减去被跳过的消息 token
                for (int i = keepFromIndex; i < nextIndex; i++)
                {
                    estimatedTokens -= TokenCounter.EstimateMessageTokens(messages[i]);
                }

                keepFromIndex = nextIndex;
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
