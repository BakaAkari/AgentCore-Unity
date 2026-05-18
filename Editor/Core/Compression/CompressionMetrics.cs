namespace AgentCore.Editor.Core.Compression
{
    /// <summary>
    /// 压缩统计指标 — 追踪上下文压缩系统的运行数据。
    /// <para>
    /// 记录每次压缩操作的 token 节省量、压缩比、成功/失败次数等。
    /// 数据仅在当前会话内有效，不持久化（Domain Reload 后重置）。
    /// </para>
    /// </summary>
    public class CompressionMetrics
    {
        #region 工具结果压缩统计

        /// <summary>工具结果压缩总次数</summary>
        public int ToolResultCompressionCount { get; private set; }

        /// <summary>工具结果压缩成功次数</summary>
        public int ToolResultCompressionSuccessCount { get; private set; }

        /// <summary>工具结果压缩失败次数（降级到截断）</summary>
        public int ToolResultCompressionFailureCount { get; private set; }

        /// <summary>工具结果压缩跳过次数（未超阈值）</summary>
        public int ToolResultCompressionSkippedCount { get; private set; }

        /// <summary>工具结果压缩节省的总 token 数</summary>
        public int ToolResultTokensSaved { get; private set; }

        /// <summary>工具结果压缩前的总 token 数</summary>
        public int ToolResultOriginalTokens { get; private set; }

        #endregion

        #region 对话压缩统计

        /// <summary>对话压缩总次数</summary>
        public int ConversationCompressionCount { get; private set; }

        /// <summary>对话压缩成功次数</summary>
        public int ConversationCompressionSuccessCount { get; private set; }

        /// <summary>对话压缩失败次数（降级到截断）</summary>
        public int ConversationCompressionFailureCount { get; private set; }

        /// <summary>对话压缩节省的总 token 数</summary>
        public int ConversationTokensSaved { get; private set; }

        /// <summary>对话压缩前的总 token 数</summary>
        public int ConversationOriginalTokens { get; private set; }

        /// <summary>被压缩的消息总数</summary>
        public int ConversationMessagesCompressed { get; private set; }

        #endregion

        #region 汇总统计

        /// <summary>总节省 token 数（工具 + 对话）</summary>
        public int TotalTokensSaved => ToolResultTokensSaved + ConversationTokensSaved;

        /// <summary>总压缩次数</summary>
        public int TotalCompressionCount => ToolResultCompressionCount + ConversationCompressionCount;

        /// <summary>总成功次数</summary>
        public int TotalSuccessCount => ToolResultCompressionSuccessCount + ConversationCompressionSuccessCount;

        /// <summary>总失败次数</summary>
        public int TotalFailureCount => ToolResultCompressionFailureCount + ConversationCompressionFailureCount;

        /// <summary>
        /// 总体压缩比（压缩后 / 压缩前）。
        /// 值越小表示压缩效果越好。返回 0 表示无数据。
        /// </summary>
        public float OverallCompressionRatio
        {
            get
            {
                int totalOriginal = ToolResultOriginalTokens + ConversationOriginalTokens;
                if (totalOriginal == 0) return 0f;
                int totalCompressed = totalOriginal - TotalTokensSaved;
                return (float)totalCompressed / totalOriginal;
            }
        }

        #endregion

        #region 记录方法

        /// <summary>
        /// 记录一次工具结果压缩成功。
        /// </summary>
        /// <param name="originalTokens">压缩前 token 数</param>
        /// <param name="compressedTokens">压缩后 token 数</param>
        public void RecordToolResultCompression(int originalTokens, int compressedTokens)
        {
            ToolResultCompressionCount++;
            ToolResultCompressionSuccessCount++;
            ToolResultOriginalTokens += originalTokens;
            ToolResultTokensSaved += (originalTokens - compressedTokens);
        }

        /// <summary>
        /// 记录一次工具结果压缩失败（降级）。
        /// </summary>
        public void RecordToolResultCompressionFailure()
        {
            ToolResultCompressionCount++;
            ToolResultCompressionFailureCount++;
        }

        /// <summary>
        /// 记录一次工具结果压缩跳过（未超阈值）。
        /// </summary>
        public void RecordToolResultCompressionSkipped()
        {
            ToolResultCompressionSkippedCount++;
        }

        /// <summary>
        /// 记录一次对话压缩成功。
        /// </summary>
        /// <param name="originalTokens">压缩前 token 数</param>
        /// <param name="compressedTokens">压缩后 token 数</param>
        /// <param name="messagesCompressed">被压缩的消息数量</param>
        public void RecordConversationCompression(int originalTokens, int compressedTokens, int messagesCompressed)
        {
            ConversationCompressionCount++;
            ConversationCompressionSuccessCount++;
            ConversationOriginalTokens += originalTokens;
            ConversationTokensSaved += (originalTokens - compressedTokens);
            ConversationMessagesCompressed += messagesCompressed;
        }

        /// <summary>
        /// 记录一次对话压缩失败（降级）。
        /// </summary>
        public void RecordConversationCompressionFailure()
        {
            ConversationCompressionCount++;
            ConversationCompressionFailureCount++;
        }

        /// <summary>
        /// 重置所有统计数据。
        /// </summary>
        public void Reset()
        {
            ToolResultCompressionCount = 0;
            ToolResultCompressionSuccessCount = 0;
            ToolResultCompressionFailureCount = 0;
            ToolResultCompressionSkippedCount = 0;
            ToolResultTokensSaved = 0;
            ToolResultOriginalTokens = 0;

            ConversationCompressionCount = 0;
            ConversationCompressionSuccessCount = 0;
            ConversationCompressionFailureCount = 0;
            ConversationTokensSaved = 0;
            ConversationOriginalTokens = 0;
            ConversationMessagesCompressed = 0;
        }

        /// <summary>
        /// 从持久化数据恢复统计信息（用于 Domain Reload 后恢复）。
        /// </summary>
        /// <param name="toolResultSuccessCount">工具结果压缩成功次数</param>
        /// <param name="conversationSuccessCount">对话压缩成功次数</param>
        /// <param name="toolResultOriginalTokens">工具结果压缩前的总 token 数</param>
        /// <param name="conversationOriginalTokens">对话压缩前的总 token 数</param>
        /// <param name="toolResultTokensSaved">工具结果节省的 token 数</param>
        /// <param name="conversationTokensSaved">对话节省的 token 数</param>
        public void RestoreFromPersistence(
            int toolResultSuccessCount,
            int conversationSuccessCount,
            int toolResultOriginalTokens,
            int conversationOriginalTokens,
            int toolResultTokensSaved,
            int conversationTokensSaved)
        {
            // 恢复成功次数（同时设置总次数，假设恢复时只保留成功的压缩）
            ToolResultCompressionSuccessCount = toolResultSuccessCount;
            ToolResultCompressionCount = toolResultSuccessCount;
            ConversationCompressionSuccessCount = conversationSuccessCount;
            ConversationCompressionCount = conversationSuccessCount;

            // 恢复 token 统计
            ToolResultOriginalTokens = toolResultOriginalTokens;
            ConversationOriginalTokens = conversationOriginalTokens;
            ToolResultTokensSaved = toolResultTokensSaved;
            ConversationTokensSaved = conversationTokensSaved;

            // 失败和跳过次数不恢复（设为 0）
            ToolResultCompressionFailureCount = 0;
            ToolResultCompressionSkippedCount = 0;
            ConversationCompressionFailureCount = 0;
            ConversationMessagesCompressed = 0; // 消息数量不持久化
        }

        /// <summary>
        /// 生成人类可读的统计摘要。
        /// </summary>
        /// <returns>格式化的统计信息字符串</returns>
        public string GetSummary()
        {
            return $"[Compression Metrics]\n" +
                   $"  Tool Results: {ToolResultCompressionSuccessCount} compressed, " +
                   $"{ToolResultCompressionFailureCount} failed, " +
                   $"{ToolResultCompressionSkippedCount} skipped, " +
                   $"{ToolResultTokensSaved} tokens saved\n" +
                   $"  Conversations: {ConversationCompressionSuccessCount} compressed, " +
                   $"{ConversationCompressionFailureCount} failed, " +
                   $"{ConversationMessagesCompressed} messages summarized, " +
                   $"{ConversationTokensSaved} tokens saved\n" +
                   $"  Total: {TotalTokensSaved} tokens saved, " +
                   $"compression ratio: {OverallCompressionRatio:P1}";
        }

        #endregion
    }
}
