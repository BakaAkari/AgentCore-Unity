using System;
using System.Collections.Generic;
using AgentCore.Editor.Tools;
using AgentCore.Editor.Workspace.Safety;

namespace AgentCore.Editor.Tools.Safety
{
    /// <summary>
    /// 工具风险策略评估器（G.1 治理层核心）。
    /// <para>
    /// 在工具执行前根据 <see cref="ToolMetadata"/>、目标路径所在的
    /// <see cref="WorkspaceRootRole"/>、以及参数摘要，合并产出
    /// <see cref="ToolPolicyDecision"/>。
    /// </para>
    /// <para>
    /// 当前策略采用 VCS 友好的宽松默认：读、写、执行类工具默认通过；删除类操作仍需确认。
    /// 阻断只保留给 Workspace 明确禁止的目标。
    /// </para>
    /// </summary>
    public static class ToolRiskPolicy
    {
        /// <summary>删除类 action 需要确认。</summary>
        private static readonly string[] DeleteActionMarkers =
        {
            "delete",
            "remove",
            "destroy"
        };

        /// <summary>
        /// 评估一次工具调用的策略决策。
        /// </summary>
        /// <param name="metadata">工具元数据（不可为 null）。</param>
        /// <param name="pathRisk">
        /// 目标路径所在 Workspace Root 的操作风险。
        /// 若工具不涉及文件系统操作，传 <see cref="WorkspaceOperationRisk.Safe"/>。
        /// </param>
        /// <param name="toolName">触发本次调用的工具名称（用于确认请求展示）。</param>
        /// <param name="action">本次调用的 action（若工具不区分 action，可传空字符串）。</param>
        /// <param name="parameterSummary">脱敏后的参数摘要（用于确认请求展示，不参与风险评估）。</param>
        /// <param name="targets">受影响的目标列表（路径 / GameObject 名 / Asset GUID 等）。</param>
        /// <returns>策略决策。</returns>
        public static ToolPolicyDecision Evaluate(
            ToolMetadata metadata,
            WorkspaceOperationRisk pathRisk,
            string toolName,
            string action,
            IReadOnlyDictionary<string, string> parameterSummary = null,
            IReadOnlyList<string> targets = null)
        {
            if (metadata == null)
            {
                var unknownRisk = new ToolExecutionRisk(
                    ToolRiskLevel.High,
                    ToolCapability.None,
                    pathRisk,
                    requiresConfirmationByDeclaration: true);

                return ToolPolicyDecision.Block(
                    unknownRisk,
                    new[] { "Tool metadata is missing; refusing to execute unverified tool." });
            }

            var risk = new ToolExecutionRisk(
                metadata.RiskLevel,
                metadata.Capabilities,
                pathRisk,
                metadata.RequiresConfirmation);

            var reasons = new List<string>();

            if (pathRisk == WorkspaceOperationRisk.Blocked)
            {
                reasons.Add($"Target path is in a Blocked workspace root (PathRisk={pathRisk}).");
                return ToolPolicyDecision.Block(risk, reasons);
            }

            if (RequiresDeleteConfirmation(action))
            {
                reasons.Add("Delete/remove/destroy action requires explicit confirmation.");
                var confirmation = BuildConfirmationRequest(
                    toolName,
                    action,
                    risk,
                    metadata,
                    reasons,
                    parameterSummary,
                    targets,
                    allowSessionTrust: true);

                return ToolPolicyDecision.RequireConfirmation(risk, confirmation, reasons);
            }

            reasons.Add("VCS-friendly policy allows non-delete tool execution without confirmation.");
            return ToolPolicyDecision.Allow(risk, reasons);
        }

        /// <summary>
        /// 评估快捷重载：无路径风险信息时使用。
        /// 仅基于工具元数据本身做判定，<see cref="WorkspaceOperationRisk"/> 默认 <c>Safe</c>。
        /// </summary>
        public static ToolPolicyDecision Evaluate(
            ToolMetadata metadata,
            string toolName,
            string action = "",
            IReadOnlyDictionary<string, string> parameterSummary = null,
            IReadOnlyList<string> targets = null)
        {
            return Evaluate(
                metadata,
                WorkspaceOperationRisk.Safe,
                toolName,
                action,
                parameterSummary,
                targets);
        }

        private static bool RequiresDeleteConfirmation(string action)
        {
            if (string.IsNullOrWhiteSpace(action))
            {
                return false;
            }

            foreach (var marker in DeleteActionMarkers)
            {
                if (action.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static ToolConfirmationRequest BuildConfirmationRequest(
            string toolName,
            string action,
            ToolExecutionRisk risk,
            ToolMetadata metadata,
            IReadOnlyList<string> reasons,
            IReadOnlyDictionary<string, string> parameterSummary,
            IReadOnlyList<string> targets,
            bool allowSessionTrust)
        {
            string title = string.IsNullOrEmpty(action)
                ? toolName
                : $"{toolName}: {action}";

            string description = string.IsNullOrEmpty(metadata.Description)
                ? $"Confirm execution of tool '{toolName}'."
                : metadata.Description;

            var trustScopes = allowSessionTrust
                ? new[] { ToolConfirmationTrustScope.Once, ToolConfirmationTrustScope.SessionExactTarget }
                : new[] { ToolConfirmationTrustScope.Once };

            return new ToolConfirmationRequest(
                toolName: toolName ?? metadata.Name,
                action: action ?? string.Empty,
                risk: risk,
                title: title,
                description: description,
                reasons: reasons,
                parameterSummary: parameterSummary,
                targets: targets,
                allowedTrustScopes: trustScopes);
        }
    }
}
