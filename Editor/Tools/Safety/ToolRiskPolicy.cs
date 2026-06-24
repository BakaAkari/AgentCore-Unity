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
    /// 本类是<b>纯函数</b>：无副作用、无可变状态、无 UI 依赖，可被 Dispatcher / Headless
    /// 流程 / 未来的 MCP server / 单元测试任意复用。
    /// </para>
    /// <para>
    /// G.1.b 阶段只实现策略<b>评估</b>，不接入 Dispatcher（接入工作放在 G.1.c）。
    /// </para>
    /// </summary>
    public static class ToolRiskPolicy
    {
        // ---------------------------------------------------------------
        // 阈值常量 — 集中在此便于审计与未来调参
        // ---------------------------------------------------------------

        /// <summary>本风险等级及以上的工具默认强制确认（不论参数）。</summary>
        private const ToolRiskLevel ConfirmThresholdRiskLevel = ToolRiskLevel.High;

        /// <summary>本路径风险及以上时，即便工具自身风险不高也强制确认。</summary>
        private const WorkspaceOperationRisk ConfirmThresholdPathRisk = WorkspaceOperationRisk.MediumRisk;

        // ---------------------------------------------------------------
        // 主入口
        // ---------------------------------------------------------------

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
                // 没有元数据 — 一律拦截，避免无声放行未知风险
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

            // 1. 硬拦截 ----------------------------------------------------
            if (pathRisk == WorkspaceOperationRisk.Blocked)
            {
                reasons.Add($"Target path is in a Blocked workspace root (PathRisk={pathRisk}).");
                return ToolPolicyDecision.Block(risk, reasons);
            }

            // 2. 强制确认（不可被参数覆盖）---------------------------------
            bool requireConfirmation = false;

            if (risk.IsCodeExecution)
            {
                requireConfirmation = true;
                reasons.Add("Tool performs arbitrary code execution; user confirmation always required.");
            }

            if (metadata.RequiresConfirmation)
            {
                requireConfirmation = true;
                reasons.Add("Tool metadata explicitly declares RequiresConfirmation = true.");
            }

            if (metadata.RiskLevel >= ConfirmThresholdRiskLevel)
            {
                requireConfirmation = true;
                reasons.Add($"Tool RiskLevel ({metadata.RiskLevel}) >= {ConfirmThresholdRiskLevel} threshold.");
            }

            if (pathRisk >= ConfirmThresholdPathRisk)
            {
                requireConfirmation = true;
                reasons.Add($"Target path risk ({pathRisk}) >= {ConfirmThresholdPathRisk} threshold.");
            }

            // 3. 能力位驱动的额外确认 ---------------------------------------
            if (risk.HasCapability(ToolCapability.DeleteProjectFiles))
            {
                requireConfirmation = true;
                reasons.Add("Tool can delete project files.");
            }

            if (risk.HasCapability(ToolCapability.ModifyScripts))
            {
                requireConfirmation = true;
                reasons.Add("Tool can modify C# scripts (will trigger compilation).");
            }

            if (risk.HasCapability(ToolCapability.InstallPackages))
            {
                requireConfirmation = true;
                reasons.Add("Tool can install/remove UPM packages.");
            }

            if (risk.HasCapability(ToolCapability.BuildPlayer))
            {
                requireConfirmation = true;
                reasons.Add("Tool can trigger player build.");
            }

            if (risk.HasCapability(ToolCapability.VersionControlWrite))
            {
                requireConfirmation = true;
                reasons.Add("Tool can write to version control (commit/push/reset).");
            }

            if (risk.HasCapability(ToolCapability.ModifyAgentConfig))
            {
                requireConfirmation = true;
                reasons.Add("Tool can modify AgentCore configuration.");
            }

            // 4. 产出决策 --------------------------------------------------
            if (requireConfirmation)
            {
                var confirmation = BuildConfirmationRequest(
                    toolName,
                    action,
                    risk,
                    metadata,
                    reasons,
                    parameterSummary,
                    targets);

                return ToolPolicyDecision.RequireConfirmation(risk, confirmation, reasons);
            }

            // 默认 Allow
            if (reasons.Count == 0)
            {
                reasons.Add($"Tool risk ({metadata.RiskLevel}) and path risk ({pathRisk}) below confirmation threshold.");
            }
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

        // ---------------------------------------------------------------
        // 内部辅助
        // ---------------------------------------------------------------

        private static ToolConfirmationRequest BuildConfirmationRequest(
            string toolName,
            string action,
            ToolExecutionRisk risk,
            ToolMetadata metadata,
            IReadOnlyList<string> reasons,
            IReadOnlyDictionary<string, string> parameterSummary,
            IReadOnlyList<string> targets)
        {
            string title = string.IsNullOrEmpty(action)
                ? toolName
                : $"{toolName}: {action}";

            string description = string.IsNullOrEmpty(metadata.Description)
                ? $"Confirm execution of tool '{toolName}'."
                : metadata.Description;

            return new ToolConfirmationRequest(
                toolName: toolName ?? metadata.Name,
                action: action ?? string.Empty,
                risk: risk,
                title: title,
                description: description,
                reasons: reasons,
                parameterSummary: parameterSummary,
                targets: targets);
        }
    }
}
