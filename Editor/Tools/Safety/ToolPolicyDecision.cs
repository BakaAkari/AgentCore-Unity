using System;
using System.Collections.Generic;

namespace AgentCore.Editor.Tools.Safety
{
    /// <summary>
    /// 工具策略决策结果（G.1 治理层）。
    /// <para>
    /// <see cref="ToolRiskPolicy"/> 评估完一个工具调用后产出本结构，
    /// 由 <c>ToolCallDispatcher</c> 在实际执行前消费，分别走 Allow / Block / RequireConfirmation 路径。
    /// </para>
    /// <para>本结构是只读快照，决策一旦做出不应被外部修改。</para>
    /// </summary>
    public readonly struct ToolPolicyDecision
    {
        /// <summary>决策结果。</summary>
        public ToolPolicyOutcome Outcome { get; }

        /// <summary>评估时的风险快照。</summary>
        public ToolExecutionRisk Risk { get; }

        /// <summary>
        /// 决策原因列表（人类可读）。
        /// <para>用于 UI 提示、Dispatcher 日志，以及未来的审计追溯。</para>
        /// </summary>
        public IReadOnlyList<string> Reasons { get; }

        /// <summary>
        /// 如需用户确认，提供结构化确认请求；否则为 <c>null</c>。
        /// </summary>
        public ToolConfirmationRequest ConfirmationRequest { get; }

        private ToolPolicyDecision(
            ToolPolicyOutcome outcome,
            ToolExecutionRisk risk,
            IReadOnlyList<string> reasons,
            ToolConfirmationRequest confirmationRequest)
        {
            Outcome = outcome;
            Risk = risk;
            Reasons = reasons ?? Array.Empty<string>();
            ConfirmationRequest = confirmationRequest;
        }

        /// <summary>构造 Allow 决策。</summary>
        public static ToolPolicyDecision Allow(ToolExecutionRisk risk, IReadOnlyList<string> reasons = null)
        {
            return new ToolPolicyDecision(ToolPolicyOutcome.Allow, risk, reasons, null);
        }

        /// <summary>
        /// 构造 RequireConfirmation 决策。
        /// </summary>
        public static ToolPolicyDecision RequireConfirmation(
            ToolExecutionRisk risk,
            ToolConfirmationRequest request,
            IReadOnlyList<string> reasons = null)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return new ToolPolicyDecision(ToolPolicyOutcome.RequireConfirmation, risk, reasons, request);
        }

        /// <summary>
        /// 构造 Block 决策。
        /// </summary>
        public static ToolPolicyDecision Block(ToolExecutionRisk risk, IReadOnlyList<string> reasons)
        {
            if (reasons == null || reasons.Count == 0)
                throw new ArgumentException("Block decision must provide at least one reason.", nameof(reasons));
            return new ToolPolicyDecision(ToolPolicyOutcome.Block, risk, reasons, null);
        }

        /// <summary>是否允许直接执行。</summary>
        public bool IsAllowed => Outcome == ToolPolicyOutcome.Allow;

        /// <summary>是否需要用户确认。</summary>
        public bool RequiresConfirmation => Outcome == ToolPolicyOutcome.RequireConfirmation;

        /// <summary>是否被策略拦截。</summary>
        public bool IsBlocked => Outcome == ToolPolicyOutcome.Block;
    }

    /// <summary>
    /// 策略决策的三种可能结果。
    /// </summary>
    public enum ToolPolicyOutcome
    {
        /// <summary>直接允许执行。</summary>
        Allow = 0,

        /// <summary>需要用户显式确认后才能执行。</summary>
        RequireConfirmation = 1,

        /// <summary>策略拦截，禁止执行。</summary>
        Block = 2
    }
}
