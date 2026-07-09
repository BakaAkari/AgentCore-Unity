namespace AgentCore.Editor.Core.SelfChallenge
{
    /// <summary>
    /// Self-Challenge 机制的**工程侧常量**。
    /// <para>
    /// 这些常量与 <see cref="Config.AgentCoreSettings"/> 中的用户配置字段**互补**：
    /// - Settings：用户可见、用户可改的开关和阈值
    /// - Config：工程侧硬编码的边界（marker、长度、格式），改动需要走代码 PR
    /// </para>
    /// <para>
    /// 依据设计文档 v0.10 §0 与 v0.9 §11 定稿。
    /// </para>
    /// <para>
    /// **可见性**：类整体为 <c>public</c>，因 <see cref="SelfChallengeData"/>（含 skip reason 字符串 /
    /// verdict enum）在公共 API 面暴露，下游消费方（UI 层的 <c>SelfChallengeCard</c>、Statistics 面板、
    /// 未来的 Stage 2 抽取器）需要直接引用本类常量做匹配判断，避免硬编码魔法字符串。
    /// v1.4.9 骨架版本从 <c>internal</c> 提升为 <c>public</c>，无 API 破坏（原本就没有外部消费者）。
    /// </para>
    /// </summary>
    public static class SelfChallengeConfig
    {
        // ─── Marker（Stage 2 抽取器使用）──────────────────────────

        /// <summary>Node A 完整模式起始 marker（v0.9 §1.2.2 / v0.10 §0.1）。</summary>
        public const string NodeAOpenMarker = "<intent_challenge>";

        /// <summary>Node A 完整模式结束 marker。</summary>
        public const string NodeACloseMarker = "</intent_challenge>";

        /// <summary>Node A Continuation 模式起始 marker（v0.9 §1.2.5）。</summary>
        public const string NodeAContinuationOpenMarker = "<intent_challenge_continuation>";

        /// <summary>Node A Continuation 模式结束 marker。</summary>
        public const string NodeAContinuationCloseMarker = "</intent_challenge_continuation>";

        /// <summary>Node B 完整输出块 marker（v0.9 §1.3.3）。</summary>
        public const string NodeBOpenMarker = "<answer_challenge>";

        /// <summary>Node B 完整输出块结束 marker。</summary>
        public const string NodeBCloseMarker = "</answer_challenge>";

        /// <summary>Consistency Correction 块起始 marker（v0.9 §1.2.2 Step 5）。</summary>
        public const string ConsistencyCorrectionOpenMarker = "<consistency_correction>";

        /// <summary>Consistency Correction 块结束 marker。</summary>
        public const string ConsistencyCorrectionCloseMarker = "</consistency_correction>";

        /// <summary>Node B Counter-Example 引用 marker 起始（v0.9 §1.3.3 Step 2）。</summary>
        public const string DraftQuoteOpenMarker = "<draft-quote>";

        /// <summary>Node B Counter-Example 引用 marker 结束。</summary>
        public const string DraftQuoteCloseMarker = "</draft-quote>";

        // ─── Skip 规则阈值（v0.9 §1.2.1 精简版：R1 + R3）─────────

        /// <summary>
        /// R1 长度阈值：消息去除所有空白后的 Unicode 字符数 ≤ 此值即 skip Node A。
        /// v0.9 明确取 15，中英文一视同仁。
        /// </summary>
        public const int R1_ShortMessageMaxChars = 15;

        // ─── Skip 原因常量（v0.9 §11.2）──────────────────────────

        /// <summary>Skip 原因：消息长度 ≤ 15 字符（R1）。</summary>
        public const string SkipReasonR1Short = "R1_short";

        /// <summary>Skip 原因：消息为纯 URL（R3）。</summary>
        public const string SkipReasonR3Url = "R3_url";

        /// <summary>Skip 原因：强制终止路径产生的 final response（v0.10 §0.3）。</summary>
        public const string SkipReasonForcedTermination = "forced_termination";

        /// <summary>Skip 原因：Node B in-flight 时发生 domain reload（v0.10 §0.5）。</summary>
        public const string SkipReasonDomainReloadInterrupt = "domain_reload_interrupt";

        /// <summary>Skip 原因（Node B 专用）：response 长度 ≤ 50 字（v0.9 §1.3.1）。</summary>
        public const string SkipReasonShortResponse = "short_response";

        /// <summary>Skip 原因（Node B 专用）：response 是纯问题（Agent 反问用户）。</summary>
        public const string SkipReasonPureQuestion = "pure_question";

        // ─── 结构校验阈值（Stage 2/3 使用）────────────────────────

        /// <summary>Node A 最少 Interpretation 数量（v0.9 §1.2.2 Step 1）。</summary>
        public const int MinInterpretationCount = 3;

        /// <summary>Node A Interpretation 最短字符数（v0.9 §1.2.4）。</summary>
        public const int MinInterpretationLength = 20;

        /// <summary>Node B 最少 Counter-Example 数量（v0.9 §1.3.3 Step 2）。</summary>
        public const int MinCounterExampleCount = 3;

        /// <summary>Node B draft-quote 最短字符数（v0.9 §1.3.3 Step 2）。</summary>
        public const int MinDraftQuoteLength = 8;

        // ─── Statistics 阈值（Stage 7 使用；镜像 §5.4 健康阈值）────

        /// <summary>Statistics 面板最多保留的样本数（v0.9 §11.6）。</summary>
        public const int MaxStatisticsSamples = 200;

        /// <summary>首周引导：Self-Challenge Card 强制展开的前 N 次数量（v0.9 §5.5）。</summary>
        public const int DefaultForcedExpansionCount = 5;
    }
}
