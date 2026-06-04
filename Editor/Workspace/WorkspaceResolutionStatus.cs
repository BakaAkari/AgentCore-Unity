namespace AgentCore.Editor.Workspace
{
    /// <summary>
    /// WorkspaceContext 解析状态。
    /// </summary>
    public enum WorkspaceResolutionStatus
    {
        /// <summary>尚未解析。</summary>
        NotResolved,

        /// <summary>解析成功，WorkspaceRoot 已确认为 SVN 工作副本根。</summary>
        Resolved,

        /// <summary>未找到 SVN 工作副本，已回退到 UnityRoot 作为 WorkspaceRoot。</summary>
        FallbackToUnityRoot,

        /// <summary>用户通过 Settings 手动指定了 WorkspaceRoot。</summary>
        ManualOverride,

        /// <summary>解析过程中发生错误，WorkspaceRoot 不可用。</summary>
        Error
    }
}
