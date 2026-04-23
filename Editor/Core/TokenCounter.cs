using System.Collections.Generic;
using AgentCore.Editor.LLM;

namespace AgentCore.Editor.Core
{
    /// <summary>
    /// Token 近似计数器 — 估算消息的 token 数量。
    /// <para>
    /// 使用近似算法，无需依赖外部 tokenizer 库：
    /// <list type="bullet">
    ///   <item>英文/数字/符号：每 4 字符约 1 token</item>
    ///   <item>中文/日文/韩文（CJK）：每字符约 2 token</item>
    ///   <item>每条消息固定开销约 4 token（role + 分隔符）</item>
    ///   <item>tool_call 额外开销：function name 约 2 token + arguments 的 token 数</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class TokenCounter
    {
        /// <summary>每条消息的基础开销（role + 分隔符）</summary>
        private const int MessageOverhead = 4;

        /// <summary>tool_call 中 function name 的额外开销</summary>
        private const int ToolCallNameOverhead = 2;

        /// <summary>
        /// 估算单段文本的 token 数。
        /// <para>
        /// 算法规则：
        /// <list type="bullet">
        ///   <item>CJK 统一汉字（U+4E00–U+9FFF）：每字符 2 token</item>
        ///   <item>CJK 扩展 A（U+3400–U+4DBF）：每字符 2 token</item>
        ///   <item>日文平假名（U+3040–U+309F）/ 片假名（U+30A0–U+30FF）：每字符 2 token</item>
        ///   <item>韩文音节（U+AC00–U+D7AF）：每字符 2 token</item>
        ///   <item>其他字符：每 4 字符 1 token</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <param name="text">要估算的文本</param>
        /// <returns>估算的 token 数</returns>
        public static int EstimateTokens(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            int cjkChars = 0;
            int otherChars = 0;

            foreach (char c in text)
            {
                if (IsCJKCharacter(c))
                    cjkChars++;
                else
                    otherChars++;
            }

            // CJK 字符每个约 2 token，其他字符每 4 个约 1 token
            int tokens = cjkChars * 2 + (otherChars + 3) / 4;
            return tokens > 0 ? tokens : 1; // 非空文本至少 1 token
        }

        /// <summary>
        /// 估算单条消息的 token 数。
        /// <para>
        /// 包含：
        /// <list type="bullet">
        ///   <item>基础开销（约 4 token，role + 分隔符）</item>
        ///   <item>content 文本的 token 数</item>
        ///   <item>tool_calls 的 token 数（function name + arguments）</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <param name="message">要估算的消息</param>
        /// <returns>估算的 token 数</returns>
        public static int EstimateMessageTokens(ChatMessage message)
        {
            if (message == null) return 0;

            int tokens = MessageOverhead;

            // content 文本
            tokens += EstimateTokens(message.Content);

            // tool_calls（assistant 消息可能包含）
            if (message.ToolCalls != null)
            {
                foreach (var toolCall in message.ToolCalls)
                {
                    // function name 额外开销
                    tokens += ToolCallNameOverhead;
                    tokens += EstimateTokens(toolCall.Function?.Name);
                    tokens += EstimateTokens(toolCall.Function?.Arguments);
                }
            }

            // tool_call_id（tool 消息包含）
            if (!string.IsNullOrEmpty(message.ToolCallId))
            {
                tokens += EstimateTokens(message.ToolCallId);
            }

            return tokens;
        }

        /// <summary>
        /// 估算整个对话的 token 数。
        /// </summary>
        /// <param name="messages">消息列表</param>
        /// <returns>估算的总 token 数</returns>
        public static int EstimateConversationTokens(List<ChatMessage> messages)
        {
            if (messages == null || messages.Count == 0) return 0;

            int total = 3; // 消息列表固定开销（对话格式开销）
            foreach (var msg in messages)
            {
                total += EstimateMessageTokens(msg);
            }
            return total;
        }

        /// <summary>
        /// 判断字符是否为 CJK（中日韩）字符。
        /// </summary>
        /// <param name="c">要判断的字符</param>
        /// <returns>是否为 CJK 字符</returns>
        private static bool IsCJKCharacter(char c)
        {
            // CJK 统一汉字
            if (c >= 0x4E00 && c <= 0x9FFF) return true;
            // CJK 统一汉字扩展 A
            if (c >= 0x3400 && c <= 0x4DBF) return true;
            // 日文平假名
            if (c >= 0x3040 && c <= 0x309F) return true;
            // 日文片假名
            if (c >= 0x30A0 && c <= 0x30FF) return true;
            // 韩文音节
            if (c >= 0xAC00 && c <= 0xD7AF) return true;

            return false;
        }
    }
}
