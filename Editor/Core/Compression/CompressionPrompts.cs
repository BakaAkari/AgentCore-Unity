namespace AgentCore.Editor.Core.Compression
{
    /// <summary>
    /// 上下文压缩系统使用的 Prompt 模板。
    /// <para>
    /// 所有压缩相关的提示词集中管理，便于调优和维护。
    /// 设计原则：
    /// <list type="bullet">
    ///   <item>指令简洁明确，减少压缩 LLM 的 token 消耗</item>
    ///   <item>保留关键信息（数据、路径、错误信息）</item>
    ///   <item>输出格式统一，便于后续处理</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class CompressionPrompts
    {
        /// <summary>
        /// 工具结果压缩的 System Prompt。
        /// 指导 LLM 将冗长的工具输出压缩为简洁摘要。
        /// </summary>
        public const string ToolResultCompressionSystem =
            "You are a precise summarization assistant. Your task is to compress tool execution results " +
            "into concise summaries while preserving ALL critical information.\n\n" +
            "Rules:\n" +
            "1. Preserve: file paths, error messages, numeric values, object names, key findings\n" +
            "2. Remove: verbose formatting, redundant descriptions, repeated patterns\n" +
            "3. Use bullet points for multiple items\n" +
            "4. Keep the summary under the target token count\n" +
            "5. If the result contains errors, prioritize error details\n" +
            "6. Never add information not present in the original\n" +
            "7. Respond ONLY with the compressed summary, no preamble";

        /// <summary>
        /// 工具结果压缩的 User Prompt 模板。
        /// {0} = 工具名称, {1} = 目标 token 数, {2} = 原始内容
        /// </summary>
        public const string ToolResultCompressionUser =
            "Compress the following output from tool '{0}' to approximately {1} tokens.\n" +
            "Preserve all critical data (paths, errors, values, names).\n\n" +
            "--- ORIGINAL OUTPUT ---\n{2}\n--- END ---";

        /// <summary>
        /// 对话历史压缩的 System Prompt。
        /// 指导 LLM 将多轮对话摘要为一段上下文。
        /// </summary>
        public const string ConversationCompressionSystem =
            "You are a conversation summarizer. Your task is to compress a segment of conversation history " +
            "into a concise summary that preserves the essential context for continuing the conversation.\n\n" +
            "Rules:\n" +
            "1. Preserve: user's goals, decisions made, files modified, errors encountered, current state\n" +
            "2. Preserve: any specific values, paths, or configurations mentioned\n" +
            "3. Remove: pleasantries, verbose explanations, intermediate reasoning\n" +
            "4. Structure: Start with 'Summary of previous conversation:' then bullet points\n" +
            "5. Include what tools were used and their key outcomes\n" +
            "6. Never fabricate information not in the original conversation\n" +
            "7. Respond ONLY with the summary, no preamble";

        /// <summary>
        /// 对话历史压缩的 User Prompt 模板。
        /// {0} = 目标 token 数, {1} = 对话内容
        /// </summary>
        public const string ConversationCompressionUser =
            "Compress the following conversation segment to approximately {0} tokens.\n" +
            "This summary will replace the original messages in the conversation context.\n\n" +
            "--- CONVERSATION SEGMENT ---\n{1}\n--- END ---";

        /// <summary>
        /// 压缩后的消息前缀标记。
        /// 用于标识已压缩的内容，让主 LLM 知道这是摘要而非原始内容。
        /// </summary>
        public const string CompressedToolResultPrefix = "[compressed] ";

        /// <summary>
        /// 压缩后的对话摘要前缀标记。
        /// </summary>
        public const string CompressedConversationPrefix = "[conversation summary] ";
    }
}
