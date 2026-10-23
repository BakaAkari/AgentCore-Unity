using System.Collections.Generic;

namespace AgentCore.Editor.Tools.Safety
{
    /// <summary>
    /// 会话级信任判定（纯逻辑，供 ChatWindow 内嵌确认 UI 使用并可单测）。
    /// <list type="bullet">
    ///   <item><description><see cref="ToolConfirmationTrustScope.SessionAll"/>（YOLO）：一切直通。</description></item>
    ///   <item><description><c>ask</c> 权限规则触发的请求（<see cref="ToolConfirmationRequest.TriggeredByRule"/>）：只有 YOLO 或显式点击能放行。</description></item>
    ///   <item><description><see cref="ToolConfirmationTrustScope.SessionLowMediumRisk"/>：ReadOnly/Low/Medium 且不含副作用能力位的请求直通。</description></item>
    /// </list>
    /// </summary>
    public static class ToolConfirmationTrust
    {
        /// <summary>
        /// 有副作用的能力位掩码（与 <see cref="ToolRiskPolicy"/> 的 ConfirmationCapabilityMask 保持一致）。
        /// 含这些能力的工具即便标称 Low/Medium，也不会被 Trust Low/Med 信任放行，须显式 YOLO。
        /// </summary>
        public const ToolCapability SideEffectCapabilityMask =
            ToolCapability.WriteProjectFiles |
            ToolCapability.DeleteProjectFiles |
            ToolCapability.ModifyScene |
            ToolCapability.ModifyAssets |
            ToolCapability.ModifyScripts |
            ToolCapability.ExecuteCode |
            ToolCapability.InstallPackages |
            ToolCapability.BuildPlayer |
            ToolCapability.NetworkAccess |
            ToolCapability.VersionControlWrite |
            ToolCapability.ModifyProjectSettings |
            ToolCapability.ModifyAgentConfig;

        /// <summary>判定请求是否已被会话内某个已激活的 scope 直通。</summary>
        public static bool IsTrusted(ToolConfirmationRequest request, ICollection<ToolConfirmationTrustScope> activeScopes)
        {
            if (request == null || activeScopes == null || activeScopes.Count == 0)
                return false;

            // YOLO: 完全不审查，所有工具直通（含 ask 规则）
            if (activeScopes.Contains(ToolConfirmationTrustScope.SessionAll))
                return true;

            // P2: ask 规则触发的确认只接受显式点击或 YOLO，Trust Low/Med 不自动放行
            if (request.TriggeredByRule != null)
                return false;

            // Low-Med: 仅 ReadOnly/Low/Medium 且不含副作用能力位的工具直通。
            // v1.7.16：仅看 ToolRisk 不够——大量工具漏标 RiskLevel（默认 Medium），
            // 若只按等级放行，会把"删文件/改脚本/装包/VCS 写/网络"等标称 Medium 的写工具
            // 静默放过。故追加能力位闸门（用户想全放行应显式选 YOLO）。
            return activeScopes.Contains(ToolConfirmationTrustScope.SessionLowMediumRisk)
                && IsScopeAllowed(request, ToolConfirmationTrustScope.SessionLowMediumRisk)
                && IsLowOrMediumRisk(request.Risk.ToolRisk)
                && (request.Risk.Capabilities & SideEffectCapabilityMask) == 0;
        }

        /// <summary>判定某个 scope 是否被 request 允许提供（由 tool 层通过 AllowedTrustScopes 决定）。</summary>
        public static bool IsScopeAllowed(ToolConfirmationRequest request, ToolConfirmationTrustScope scope)
        {
            if (request?.AllowedTrustScopes == null)
                return false;

            foreach (var s in request.AllowedTrustScopes)
            {
                if (s == scope)
                    return true;
            }

            return false;
        }

        private static bool IsLowOrMediumRisk(ToolRiskLevel level)
        {
            return level == ToolRiskLevel.ReadOnly
                || level == ToolRiskLevel.Low
                || level == ToolRiskLevel.Medium;
        }
    }
}
