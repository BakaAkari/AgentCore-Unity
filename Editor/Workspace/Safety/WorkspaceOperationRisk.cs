namespace AgentCore.Editor.Workspace.Safety
{
    /// <summary>
    /// 工具操作风险等级，基于 WorkspaceRootRole 决定。
    /// </summary>
    public enum WorkspaceOperationRisk
    {
        /// <summary>可自由读写，无需额外确认。</summary>
        Safe,

        /// <summary>可读写，但应提示影响范围（如 SharedCode）。</summary>
        LowRisk,

        /// <summary>可读写，但需提示构建/部署影响（如 ToolingCode）。</summary>
        MediumRisk,

        /// <summary>默认只读，写入需用户明确确认（如 CommercialPlugin）。</summary>
        HighRisk,

        /// <summary>禁止写入（如 GeneratedCode、ReadOnlyReference）。</summary>
        Blocked
    }
}
