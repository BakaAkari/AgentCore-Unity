using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace AgentCore.Editor.Core.SelfChallenge
{
    // ─── Step 4 / Step 5 / Verdict 类型安全枚举（v0.9 §11.2）──────────

    /// <summary>Node A Step 4 维度 A：是否存在歧义信号。</summary>
    public enum Step4Ambiguity { Yes, No }

    /// <summary>Node A Step 4 维度 B：Interpretation 差异严重度。</summary>
    public enum Step4Severity { Severe, Minor }

    /// <summary>Node A Step 4 维度 C：操作是否破坏性。</summary>
    public enum Step4OperationRisk { Destructive, Safe }

    /// <summary>Node A Step 4 维度 D：chosen 关键词是否来自 query 原文。</summary>
    public enum Step4Attribution { Inferred, Verbatim }

    /// <summary>Node A Step 4 结论：命中组合 1 / 组合 2 / 直接执行。</summary>
    public enum Step4Conclusion { Combo1, Combo2, DirectExecute }

    /// <summary>Node A Step 5 verdict：LLM 自校验结论。</summary>
    public enum Step5Verdict { Consistent, Corrected }

    /// <summary>Node B verdict：draft 通过 / 需要修正 / 需要回 tool loop。</summary>
    public enum NodeBVerdict { PASS, REVISE, BLOCK }

    /// <summary>
    /// Correction retry 耗尽后的 fallback 类型（v0.9 §11.5）。
    /// </summary>
    public enum FallbackType
    {
        /// <summary>Node A 结构校验超过最大重试次数。</summary>
        NodeAStructural,

        /// <summary>Node A Continuation 模式结构校验超过最大重试次数。</summary>
        NodeAContinuationStructural,

        /// <summary>Node B 结构校验超过最大重试次数。</summary>
        NodeBStructural
    }

    // ─── SelfChallengeData 完整 schema（v0.9 §11.2 / v0.10 §0.8）──────

    /// <summary>
    /// 单个 assistant turn 的 self-challenge 全量数据（Phase 9 骨架）。
    /// <para>
    /// 挂载到 <see cref="Session.SerializableConversationTurn"/>，随 SessionData 序列化到磁盘。
    /// v1.4.9 骨架版本：schema 完整定义，实际字段填充由 Stage 2-7 完成。
    /// </para>
    /// <para>
    /// 版本兼容：v1.4.x 及以前的 session 反序列化时该字段为 <c>null</c>，UI 层遇到 null 直接不渲染。
    /// </para>
    /// </summary>
    [Serializable]
    public class SelfChallengeData
    {
        // ─── Node A (Intent Self-Challenge) ─────────────────────────

        /// <summary>Node A 是否触发（false 表示 skip）。</summary>
        [JsonProperty("node_a_triggered")]
        public bool NodeATriggered { get; set; }

        /// <summary>
        /// Skip 时的原因：
        /// v0.9 保留字符串枚举 <c>"R1_short"</c> / <c>"R3_url"</c> / <c>"forced_termination"</c>（§0.3）/
        /// <c>"domain_reload_interrupt"</c>（§0.5）；未 skip 时为 <c>null</c>。
        /// </summary>
        [JsonProperty("node_a_skip_reason", NullValueHandling = NullValueHandling.Ignore)]
        public string NodeASkipReason { get; set; }

        /// <summary>是否为 Continuation 模式（<c>true</c> = Continuation，<c>false</c> = 完整 Node A）。</summary>
        [JsonProperty("is_node_a_continuation")]
        public bool IsNodeAContinuation { get; set; }

        /// <summary>Node A 完整输出的 <c>&lt;intent_challenge&gt;</c> 或 <c>&lt;intent_challenge_continuation&gt;</c> 块原文。</summary>
        [JsonProperty("node_a_output", NullValueHandling = NullValueHandling.Ignore)]
        public string NodeAOutput { get; set; }

        /// <summary>解析后的 Interpretation 列表（Continuation 模式为空）。</summary>
        [JsonProperty("interpretations", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> Interpretations { get; set; }

        /// <summary>解析后的歧义词列表。</summary>
        [JsonProperty("ambiguity_signals", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> AmbiguitySignals { get; set; }

        /// <summary>选定的 chosen interpretation 文本。</summary>
        [JsonProperty("chosen_interpretation", NullValueHandling = NullValueHandling.Ignore)]
        public string ChosenInterpretation { get; set; }

        /// <summary>关键假设列表。</summary>
        [JsonProperty("key_assumptions", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> KeyAssumptions { get; set; }

        /// <summary>Step 4 维度 A：是否存在歧义信号。</summary>
        [JsonProperty("step4_a", NullValueHandling = NullValueHandling.Ignore)]
        public Step4Ambiguity? Step4A { get; set; }

        /// <summary>Step 4 维度 B：Interpretation 差异严重度。</summary>
        [JsonProperty("step4_b", NullValueHandling = NullValueHandling.Ignore)]
        public Step4Severity? Step4B { get; set; }

        /// <summary>Step 4 维度 C：操作是否破坏性。</summary>
        [JsonProperty("step4_c", NullValueHandling = NullValueHandling.Ignore)]
        public Step4OperationRisk? Step4C { get; set; }

        /// <summary>Step 4 维度 D：chosen 关键词是否来自 query 原文。</summary>
        [JsonProperty("step4_d", NullValueHandling = NullValueHandling.Ignore)]
        public Step4Attribution? Step4D { get; set; }

        /// <summary>当 <see cref="Step4D"/> = <see cref="Step4Attribution.Inferred"/> 时的推断词列表。</summary>
        [JsonProperty("inferred_words", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> InferredWords { get; set; }

        /// <summary>Step 4 最终结论。</summary>
        [JsonProperty("step4_conclusion", NullValueHandling = NullValueHandling.Ignore)]
        public Step4Conclusion? Step4Conclusion { get; set; }

        /// <summary>Step 5 verdict（LLM 自校验结论）。</summary>
        [JsonProperty("step5_verdict", NullValueHandling = NullValueHandling.Ignore)]
        public Step5Verdict? Step5Verdict { get; set; }

        /// <summary>Step 5 corrected judgement 原文（仅当 <see cref="Step5Verdict"/> = <see cref="SelfChallenge.Step5Verdict.Corrected"/> 时非空）。</summary>
        [JsonProperty("step5_corrected_judgement", NullValueHandling = NullValueHandling.Ignore)]
        public string Step5CorrectedJudgement { get; set; }

        /// <summary>Node A 触发的 correction retry 次数。</summary>
        [JsonProperty("node_a_retry_count")]
        public int NodeARetryCount { get; set; }

        /// <summary>
        /// Node A 是否最终导致 Agent 进入 <c>WaitingForClarification</c> 状态。
        /// 等价于 <see cref="Step4Conclusion"/> 非 <see cref="SelfChallenge.Step4Conclusion.DirectExecute"/>。
        /// </summary>
        [JsonProperty("triggered_clarification")]
        public bool TriggeredClarification { get; set; }

        /// <summary>
        /// Continuation 模式下引用的上一轮 turn ID；非 Continuation 时为 <c>null</c>。
        /// </summary>
        [JsonProperty("previous_turn_node_a_id", NullValueHandling = NullValueHandling.Ignore)]
        public string PreviousTurnNodeAId { get; set; }

        // ─── Node B (Answer Self-Challenge) ─────────────────────────

        /// <summary>Node B 是否触发（false 表示 skip）。</summary>
        [JsonProperty("node_b_triggered")]
        public bool NodeBTriggered { get; set; }

        /// <summary>
        /// Node B skip 原因：
        /// <c>"short_response"</c> / <c>"pure_question"</c> / <c>"forced_termination"</c>（§0.3）/
        /// <c>"domain_reload_interrupt"</c>（§0.5）；未 skip 时为 <c>null</c>。
        /// </summary>
        [JsonProperty("node_b_skip_reason", NullValueHandling = NullValueHandling.Ignore)]
        public string NodeBSkipReason { get; set; }

        /// <summary>Node B 完整输出的 <c>&lt;answer_challenge&gt;</c> 块原文。</summary>
        [JsonProperty("node_b_output", NullValueHandling = NullValueHandling.Ignore)]
        public string NodeBOutput { get; set; }

        /// <summary>Node B verdict。</summary>
        [JsonProperty("node_b_verdict", NullValueHandling = NullValueHandling.Ignore)]
        public NodeBVerdict? NodeBVerdict { get; set; }

        /// <summary>Counter-Example 数量（应 ≥ 3）。</summary>
        [JsonProperty("counter_example_count")]
        public int CounterExampleCount { get; set; }

        /// <summary>Counter-Example 里所有 <c>&lt;draft-quote&gt;...&lt;/draft-quote&gt;</c> 引用内容。</summary>
        [JsonProperty("counter_example_quotes", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> CounterExampleQuotes { get; set; }

        /// <summary>REVISE 时的 issues 列表。</summary>
        [JsonProperty("revise_issues", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> ReviseIssues { get; set; }

        /// <summary>BLOCK 时的 verifications 列表。</summary>
        [JsonProperty("block_verifications", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> BlockVerifications { get; set; }

        /// <summary>Node B 触发的 correction retry 次数。</summary>
        [JsonProperty("node_b_retry_count")]
        public int NodeBRetryCount { get; set; }

        /// <summary>REVISE 时是否触发了 draft 重新生成（v0.10 §0.4：单次不复审）。</summary>
        [JsonProperty("draft_regenerated")]
        public bool DraftRegenerated { get; set; }

        // ─── Metadata ────────────────────────────────────────────────

        /// <summary>本轮 self-challenge 的总耗时（毫秒）。</summary>
        [JsonProperty("total_duration_ms")]
        public long TotalDurationMs { get; set; }

        /// <summary>本轮 self-challenge 消耗的总 token 数（input + output 估算）。</summary>
        [JsonProperty("total_tokens_estimate")]
        public int TotalTokensEstimate { get; set; }

        /// <summary>本轮 self-challenge 的 Unix 时间戳（秒）。</summary>
        [JsonProperty("timestamp_unix")]
        public long TimestampUnix { get; set; }

        /// <summary>
        /// 创建一个默认实例；用于新一轮 self-challenge 开始时。
        /// </summary>
        public SelfChallengeData()
        {
            TimestampUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
    }
}
