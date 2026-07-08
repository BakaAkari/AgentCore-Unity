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
    /// v1.4.5 起策略规则（优先级从高到低）：
    /// <list type="number">
    ///   <item><description>Workspace 目标路径为 Blocked → 直接 Block。</description></item>
    ///   <item><description><c>metadata.RequiresConfirmation == true</c> → RequireConfirmation（工具声明生效）。</description></item>
    ///   <item><description>action 名的任一 token 命中破坏性 token 列表（write/create/delete/copy/move/add_method/... 见 <see cref="DestructiveActionTokens"/>） → RequireConfirmation。</description></item>
    ///   <item><description>其他情况 → Allow（VCS 友好宽松默认，只读 action 直通）。</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// v1.4.5 修复：此前实现构造了 <see cref="ToolExecutionRisk"/> 并透传了
    /// <c>RequiresConfirmation</c> 字段，但从未真正使用它做决策，导致
    /// <c>[AgentTool(RequiresConfirmation = true)]</c> 声明形同虚设（例如 ExecuteCodeTool /
    /// ManageBuildTool / ManagePackageTool）。现在声明会真正触发用户确认，
    /// 并且 UI 端 <c>SessionExactTarget</c> 信任缓存会让用户"批准一次，本会话内不再问"。
    /// </para>
    /// </summary>
    public static class ToolRiskPolicy
    {
        /// <summary>
        /// 破坏性 action 的 token 列表（v1.4.5 起扩展）。
        /// <para>
        /// 匹配规则：把 action 名按下划线拆成 token，任一 token 命中此列表即视为破坏性。
        /// 这样 <c>write_file</c> / <c>create_directory</c> / <c>add_method</c> / <c>add_field</c>
        /// 等复合 action 都能被覆盖，而 <c>read_file</c> / <c>list_directory</c> / <c>find_references</c>
        /// 保持直通。
        /// </para>
        /// <para>
        /// 历史行为兼容：v1.4.4 及以前只识别 <c>delete</c> / <c>remove</c> / <c>destroy</c>
        /// 且用 <see cref="string.IndexOf(string, StringComparison)"/> 做子串匹配。v1.4.5 起改为
        /// token-level 匹配，行为更精准，同时覆盖 write/create/copy/move/add 等破坏性 token。
        /// </para>
        /// </summary>
        private static readonly HashSet<string> DestructiveActionTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Legacy delete-family tokens (v1.4.4 及之前的策略)
            "delete",
            "remove",
            "destroy",

            // File / script write & create
            "write",
            "create",
            "overwrite",
            "modify",
            "update",
            "replace",

            // ManageScriptTool 特有的 code injection
            "add_method",
            "add_field",
            "add",     // add_method / add_field 拆成 [add, method] / [add, field] 之后，"add" token 也能兜底命中

            // ManageFileTool 特有的破坏性文件系统操作
            "copy",
            "move",
            "rename",

            // Prefab / Asset 生成类
            "instantiate",
            "duplicate",
            "clone",

            // Build / Package / VCS write（工具级 RequiresConfirmation 也会拦，这里是兜底）
            "install",
            "uninstall",
            "commit",
            "push",
            "revert",
            "reset",
            "checkout",
            "merge",
            "rebase"
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

            // v1.4.5: metadata.RequiresConfirmation 现在真正生效。工具通过
            // [AgentTool(RequiresConfirmation = true)] 显式声明"我需要用户批准"，
            // 例如 ExecuteCodeTool、ManageBuildTool、ManagePackageTool、ManageScriptTool 的写入 action、
            // ManageFileTool 的写入 action 等。UI 端支持 SessionExactTarget 信任缓存，
            // 用户勾选"信任本会话相同目标"后，同一 tool+action+targets 的后续调用会直通。
            if (metadata.RequiresConfirmation)
            {
                reasons.Add("Tool declared RequiresConfirmation=true; explicit user approval required.");
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

            if (RequiresDestructiveActionConfirmation(action, out var matchedToken))
            {
                reasons.Add($"Destructive action token '{matchedToken}' detected in '{action}'; explicit user approval required.");
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

            reasons.Add("VCS-friendly policy allows non-destructive tool execution without confirmation.");
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

        /// <summary>
        /// 判断 action 名是否包含破坏性 token（v1.4.5 起）。
        /// <para>
        /// 匹配算法：把 action 按 <c>_</c> 拆成 token 列表，任一 token 命中
        /// <see cref="DestructiveActionTokens"/> 集合即返回 true。
        /// 例：
        /// <list type="bullet">
        ///   <item><description><c>write_file</c> → [write, file] → 命中 "write"</description></item>
        ///   <item><description><c>create_directory</c> → [create, directory] → 命中 "create"</description></item>
        ///   <item><description><c>add_method</c> → [add_method, add, method] → 命中 "add_method" 或 "add"</description></item>
        ///   <item><description><c>read_file</c> → [read, file] → 无命中</description></item>
        ///   <item><description><c>find_references</c> → [find, references] → 无命中</description></item>
        /// </list>
        /// </para>
        /// <para>
        /// 相比 v1.4.4 及以前的 <see cref="string.IndexOf(string, StringComparison)"/> 子串匹配，
        /// token 匹配避免了 <c>overwrite_mode</c> 里的 "write" 误伤（虽然当前 token 列表中 "overwrite"
        /// 本身也在列表里，但语义清晰性提升）；同时保证 <c>find_references</c>、<c>read_file</c>
        /// 等只读 action 不会被误判为破坏性。
        /// </para>
        /// </summary>
        /// <param name="action">工具 action 名（可能带下划线）。</param>
        /// <param name="matchedToken">命中的破坏性 token，用于错误提示。未命中时为 null。</param>
        /// <returns>是否需要触发用户确认。</returns>
        private static bool RequiresDestructiveActionConfirmation(string action, out string matchedToken)
        {
            matchedToken = null;
            if (string.IsNullOrWhiteSpace(action))
            {
                return false;
            }

            // 先看完整 action 是否直接命中（覆盖 add_method / add_field 这种复合 token）
            if (DestructiveActionTokens.Contains(action))
            {
                matchedToken = action;
                return true;
            }

            // 再按下划线拆 token 逐个匹配
            var tokens = action.Split('_');
            foreach (var token in tokens)
            {
                if (string.IsNullOrEmpty(token))
                    continue;
                if (DestructiveActionTokens.Contains(token))
                {
                    matchedToken = token;
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
