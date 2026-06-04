namespace AgentCore.Editor.Workspace.Resolution
{
    /// <summary>
    /// 根据 WorkspaceRootInfo 的 ScopeType 和目录名称推断默认 Role。
    /// 用于 ScopeRootResolver 自动标注，用户可在 Settings 中覆盖。
    /// </summary>
    public static class WorkspaceRootRoleResolver
    {
        /// <summary>
        /// 根据 ScopeType 推断默认 Role。
        /// </summary>
        public static WorkspaceRootRole ResolveDefaultRole(WorkspaceScopeType scopeType)
        {
            switch (scopeType)
            {
                case WorkspaceScopeType.Project:    return WorkspaceRootRole.EditableProjectCode;
                case WorkspaceScopeType.Map:        return WorkspaceRootRole.WorkspacePackage;
                case WorkspaceScopeType.Mode:       return WorkspaceRootRole.WorkspacePackage;
                case WorkspaceScopeType.Package:    return WorkspaceRootRole.WorkspacePackage;
                case WorkspaceScopeType.Shared:     return WorkspaceRootRole.SharedCode;
                case WorkspaceScopeType.UI:         return WorkspaceRootRole.EditableProjectCode;
                case WorkspaceScopeType.Localization: return WorkspaceRootRole.ReadOnlyReference;
                case WorkspaceScopeType.Engine:     return WorkspaceRootRole.EngineCode;
                case WorkspaceScopeType.Plugin:     return WorkspaceRootRole.CommercialPlugin;
                case WorkspaceScopeType.Tools:      return WorkspaceRootRole.ToolingCode;
                case WorkspaceScopeType.Generated:  return WorkspaceRootRole.GeneratedCode;
                default:                            return WorkspaceRootRole.ReadOnlyReference;
            }
        }

        /// <summary>
        /// 根据 Role 判断是否默认只读。
        /// </summary>
        public static bool IsDefaultReadOnly(WorkspaceRootRole role)
        {
            switch (role)
            {
                case WorkspaceRootRole.CommercialPlugin:
                case WorkspaceRootRole.EngineCode:
                case WorkspaceRootRole.ReadOnlyReference:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 根据 Role 判断是否为自动生成目录。
        /// </summary>
        public static bool IsDefaultGenerated(WorkspaceRootRole role)
        {
            return role == WorkspaceRootRole.GeneratedCode;
        }
    }
}
