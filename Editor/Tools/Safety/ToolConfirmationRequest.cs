using System;
using System.Collections.Generic;

namespace AgentCore.Editor.Tools.Safety
{
    /// <summary>
    /// 工具执行确认请求（G.1 治理层）。
    /// <para>
    /// 当 <see cref="ToolRiskPolicy"/> 评估为 <see cref="ToolPolicyOutcome.RequireConfirmation"/> 时，
    /// Dispatcher 应构造本对象交给 UI 层（或 Headless 流程的策略钩子），让用户做出 Approve / Reject 决定。
    /// </para>
    /// <para>本结构是数据载体，不直接执行任何 UI 操作，UI 层（G.1.c+）独立消费它。</para>
    /// </summary>
    public sealed class ToolConfirmationRequest
    {
        /// <summary>触发确认的工具名称。</summary>
        public string ToolName { get; }

        /// <summary>触发确认的 action（若工具不区分 action，则为空字符串）。</summary>
        public string Action { get; }

        /// <summary>合并后的风险快照。</summary>
        public ToolExecutionRisk Risk { get; }

        /// <summary>
        /// 人类可读的标题，例如 <c>"manage_file: delete"</c>。
        /// </summary>
        public string Title { get; }

        /// <summary>
        /// 详细描述（多行）。
        /// 由 Dispatcher / Policy 根据工具描述、参数摘要、目标路径生成。
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// 触发确认的具体原因列表（与 <see cref="ToolPolicyDecision.Reasons"/> 一致）。
        /// </summary>
        public IReadOnlyList<string> Reasons { get; }

        /// <summary>
        /// 关键参数摘要（key → value），便于 UI 直观展示工具实际要做什么。
        /// <para>不应包含敏感字段（API Key、Token 等），由调用方负责脱敏。</para>
        /// </summary>
        public IReadOnlyDictionary<string, string> ParameterSummary { get; }

        /// <summary>
        /// 受影响的目标（如文件路径、GameObject 名称）。
        /// 与 <see cref="ParameterSummary"/> 互补：ParameterSummary 是输入快照，Targets 是预期影响面。
        /// </summary>
        public IReadOnlyList<string> Targets { get; }

        /// <summary>确认后允许提供给 UI 的短期信任范围。</summary>
        public IReadOnlyList<ToolConfirmationTrustScope> AllowedTrustScopes { get; }

        /// <summary>
        /// 触发本次确认的 <c>ask</c> 权限规则（P2）；为 null 表示确认由风险策略本身触发。
        /// <para>非 null 时 Trust Low/Med 会话信任不会自动放行（只有显式点击或 YOLO）。</para>
        /// </summary>
        public PermissionRuleMatch TriggeredByRule { get; }

        /// <summary>
        /// Dispatcher 做权限规则匹配时使用的规范化项目相对路径（可为 null = 调用无路径参数）。
        /// <para>
        /// 供 UI 在新增规则（"始终允许"）后用与 Dispatcher 相同的输入重新评估排队中的确认
        /// （<see cref="ToolConfirmationTrust.IsAllowedByPermissionRules"/>）；不参与展示。
        /// </para>
        /// </summary>
        public IReadOnlyList<string> RulePaths { get; internal set; }

        /// <summary>
        /// 调用来源（如 MCP 客户端名 "claude-code"）；为 null 表示内嵌 Chat 自己的调用。仅用于确认 UI 展示。
        /// </summary>
        public string Source { get; set; }

        public ToolConfirmationRequest(
            string toolName,
            string action,
            ToolExecutionRisk risk,
            string title,
            string description,
            IReadOnlyList<string> reasons,
            IReadOnlyDictionary<string, string> parameterSummary,
            IReadOnlyList<string> targets,
            IReadOnlyList<ToolConfirmationTrustScope> allowedTrustScopes = null,
            PermissionRuleMatch triggeredByRule = null)
        {
            ToolName = toolName ?? throw new ArgumentNullException(nameof(toolName));
            Action = action ?? string.Empty;
            Risk = risk;
            Title = title ?? toolName;
            Description = description ?? string.Empty;
            Reasons = reasons ?? Array.Empty<string>();
            ParameterSummary = parameterSummary ?? new Dictionary<string, string>();
            Targets = targets ?? Array.Empty<string>();
            // v1.6.5: 默认允许 Low/Med + All (YOLO) 两档会话级信任。
            // 如需限制某个工具不允许 YOLO,构造时显式传入过滤后的数组。
            AllowedTrustScopes = allowedTrustScopes ?? new[]
            {
                ToolConfirmationTrustScope.SessionLowMediumRisk,
                ToolConfirmationTrustScope.SessionAll
            };
            TriggeredByRule = triggeredByRule != null && triggeredByRule.HasMatch ? triggeredByRule : null;
        }

        /// <summary>
        /// 返回标记了 <c>ask</c> 规则的副本，并把 <paramref name="extraReason"/> 追加到原因列表首位。
        /// </summary>
        public ToolConfirmationRequest WithPermissionRule(PermissionRuleMatch match, string extraReason)
        {
            var reasons = new List<string>(Reasons.Count + 1);
            if (!string.IsNullOrEmpty(extraReason))
                reasons.Add(extraReason);
            reasons.AddRange(Reasons);
            return new ToolConfirmationRequest(
                ToolName,
                Action,
                Risk,
                Title,
                Description,
                reasons,
                ParameterSummary,
                Targets,
                AllowedTrustScopes,
                match)
            {
                RulePaths = RulePaths
            };
        }
    }
}
