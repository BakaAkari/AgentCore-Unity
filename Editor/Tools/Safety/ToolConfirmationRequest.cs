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

        public ToolConfirmationRequest(
            string toolName,
            string action,
            ToolExecutionRisk risk,
            string title,
            string description,
            IReadOnlyList<string> reasons,
            IReadOnlyDictionary<string, string> parameterSummary,
            IReadOnlyList<string> targets)
        {
            ToolName = toolName ?? throw new ArgumentNullException(nameof(toolName));
            Action = action ?? string.Empty;
            Risk = risk;
            Title = title ?? toolName;
            Description = description ?? string.Empty;
            Reasons = reasons ?? Array.Empty<string>();
            ParameterSummary = parameterSummary ?? new Dictionary<string, string>();
            Targets = targets ?? Array.Empty<string>();
        }
    }
}
