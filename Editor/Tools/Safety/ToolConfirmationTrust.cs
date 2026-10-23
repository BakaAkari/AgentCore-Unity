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

        /// <summary>
        /// 用当前权限规则重新评估一个待确认请求：规则（deny &gt; ask &gt; allow，与 Dispatcher 同一
        /// <see cref="PermissionRuleMatcher.Evaluate"/>、同一工具名 / action / 规则路径）最终命中 allow 时返回 true。
        /// <para>
        /// 用于"始终允许"新建 allow 规则后自动批准排队中的同类确认。以下情况返回 false（保持等待用户）：
        /// ask 规则触发的确认（只接受显式点击或 YOLO）；batch_execute 外壳（规则按子操作匹配）；
        /// 仍有 deny / ask 规则覆盖；没有任何 allow 规则命中。
        /// </para>
        /// </summary>
        public static bool IsAllowedByPermissionRules(ToolConfirmationRequest request, IReadOnlyList<ScopedPermissionRule> rules)
        {
            if (request == null || rules == null || rules.Count == 0)
                return false;
            if (request.TriggeredByRule != null)
                return false;
            if (string.Equals(request.ToolName, "batch_execute", System.StringComparison.OrdinalIgnoreCase))
                return false;

            var match = PermissionRuleMatcher.Evaluate(rules, request.ToolName, request.Action, request.RulePaths);
            return match != null && match.HasMatch && match.Effect == PermissionRuleEffect.Allow;
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
