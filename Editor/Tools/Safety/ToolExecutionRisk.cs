using AgentCore.Editor.Workspace.Safety;

namespace AgentCore.Editor.Tools.Safety
{
    /// <summary>
    /// 工具执行风险评估结果（G.1 治理层）。
    /// <para>
    /// <see cref="ToolRiskPolicy"/> 在工具实际执行前评估生成，合并三个维度：
    /// </para>
    /// <list type="number">
    ///   <item><description><see cref="ToolRiskLevel"/> — 工具能力的最坏严重程度。</description></item>
    ///   <item><description><see cref="ToolCapability"/> — 实际触达的能力面。</description></item>
    ///   <item><description><see cref="WorkspaceOperationRisk"/> — 目标路径所在 Root 的脆弱性。</description></item>
    /// </list>
    /// <para>
    /// 该结构是只读快照，由 <see cref="ToolPolicyDecision"/> 引用，最终决定 Allow / Block / RequireConfirmation。
    /// </para>
    /// </summary>
    public readonly struct ToolExecutionRisk
    {
        /// <summary>工具声明的风险等级（来自 ToolMetadata）。</summary>
        public ToolRiskLevel ToolRisk { get; }

        /// <summary>工具声明的能力位（来自 ToolMetadata）。</summary>
        public ToolCapability Capabilities { get; }

        /// <summary>
        /// 目标路径所在 Workspace Root 的风险（若工具不涉及路径，则为 <see cref="WorkspaceOperationRisk.Safe"/>）。
        /// </summary>
        public WorkspaceOperationRisk PathRisk { get; }

        /// <summary>
        /// 工具元数据是否声明了 <c>RequiresConfirmation</c>。
        /// 即使最终 RiskLevel 不算高，也会被本字段强制升级到 RequireConfirmation。
        /// </summary>
        public bool RequiresConfirmationByDeclaration { get; }

        /// <summary>是否包含 <see cref="ToolCapability.ExecuteCode"/> — 任何情况下都强制确认。</summary>
        public bool IsCodeExecution { get; }

        /// <summary>是否触达外部网络。用于审计与未来的"离线模式"策略。</summary>
        public bool HasNetworkAccess { get; }

        public ToolExecutionRisk(
            ToolRiskLevel toolRisk,
            ToolCapability capabilities,
            WorkspaceOperationRisk pathRisk,
            bool requiresConfirmationByDeclaration)
        {
            ToolRisk = toolRisk;
            Capabilities = capabilities;
            PathRisk = pathRisk;
            RequiresConfirmationByDeclaration = requiresConfirmationByDeclaration;
            IsCodeExecution = (capabilities & ToolCapability.ExecuteCode) != 0
                              || toolRisk == ToolRiskLevel.CodeExecution;
            HasNetworkAccess = (capabilities & ToolCapability.NetworkAccess) != 0;
        }

        /// <summary>是否包含指定能力位。</summary>
        public bool HasCapability(ToolCapability capability)
        {
            return (Capabilities & capability) == capability && capability != ToolCapability.None;
        }
    }
}
