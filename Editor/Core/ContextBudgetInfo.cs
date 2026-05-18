namespace AgentCore.Editor.Core
{
    /// <summary>
    /// Context budget information for visualization.
    /// Provides a snapshot of current token usage and compression statistics.
    /// </summary>
    public struct ContextBudgetInfo
    {
        /// <summary>
        /// Current total token count in conversation history.
        /// </summary>
        public int CurrentTokens;

        /// <summary>
        /// Maximum tokens allowed by the model.
        /// </summary>
        public int MaxTokens;

        /// <summary>
        /// Reserved tokens for system prompt and tools.
        /// </summary>
        public int ReservedTokens;

        /// <summary>
        /// Available tokens for conversation (MaxTokens - ReservedTokens).
        /// </summary>
        public int AvailableTokens;

        /// <summary>
        /// Current usage percentage (0.0 - 1.0).
        /// </summary>
        public float UsagePercentage;

        /// <summary>
        /// Number of tool result compressions performed.
        /// </summary>
        public int ToolResultCompressions;

        /// <summary>
        /// Number of conversation compressions performed.
        /// </summary>
        public int ConversationCompressions;

        /// <summary>
        /// Total tokens saved by compression.
        /// </summary>
        public int TokensSaved;

        /// <summary>
        /// Overall compression ratio (0.0 - 1.0).
        /// Higher means more compression.
        /// </summary>
        public float CompressionRatio;

        /// <summary>
        /// Whether compression is currently active (usage > 70%).
        /// </summary>
        public bool IsCompressionActive;

        /// <summary>
        /// Current model name.
        /// </summary>
        public string ModelName;
    }
}
