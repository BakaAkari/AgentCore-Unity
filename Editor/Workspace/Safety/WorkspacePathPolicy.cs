namespace AgentCore.Editor.Workspace.Safety
{
    /// <summary>
    /// 基于 WorkspaceRootRole 的工具操作安全策略。
    /// P0 阶段定义数据模型，后续工具系统逐步接入。
    /// </summary>
    public static class WorkspacePathPolicy
    {
        /// <summary>
        /// 根据 Role 获取操作风险等级。
        /// </summary>
        public static WorkspaceOperationRisk GetRisk(WorkspaceRootRole role)
        {
            switch (role)
            {
                case WorkspaceRootRole.EditableProjectCode:
                    return WorkspaceOperationRisk.Safe;

                case WorkspaceRootRole.SharedCode:
                    return WorkspaceOperationRisk.LowRisk;

                case WorkspaceRootRole.WorkspacePackage:
                    return WorkspaceOperationRisk.LowRisk;

                case WorkspaceRootRole.CustomPlugin:
                    return WorkspaceOperationRisk.MediumRisk;

                case WorkspaceRootRole.ToolingCode:
                    return WorkspaceOperationRisk.MediumRisk;

                case WorkspaceRootRole.CommercialPlugin:
                    return WorkspaceOperationRisk.HighRisk;

                case WorkspaceRootRole.EngineCode:
                    return WorkspaceOperationRisk.HighRisk;

                case WorkspaceRootRole.GeneratedCode:
                    return WorkspaceOperationRisk.Blocked;

                case WorkspaceRootRole.ReadOnlyReference:
                    return WorkspaceOperationRisk.Blocked;

                default:
                    return WorkspaceOperationRisk.HighRisk;
            }
        }

        /// <summary>
        /// 检查指定 Role 是否允许写入操作。
        /// </summary>
        public static bool IsWriteAllowed(WorkspaceRootRole role)
        {
            var risk = GetRisk(role);
            return risk != WorkspaceOperationRisk.Blocked;
        }

        /// <summary>
        /// 获取写入操作的用户提示文本（用于工具确认对话框）。
        /// </summary>
        public static string GetWriteWarning(WorkspaceRootRole role, string rootDisplayName)
        {
            switch (GetRisk(role))
            {
                case WorkspaceOperationRisk.Blocked:
                    return $"[{rootDisplayName}] 为 {role}，禁止写入。";
                case WorkspaceOperationRisk.HighRisk:
                    return $"[{rootDisplayName}] 为 {role}，写入操作需要明确确认。";
                case WorkspaceOperationRisk.MediumRisk:
                    return $"[{rootDisplayName}] 为 {role}，写入可能影响构建/部署流程。";
                case WorkspaceOperationRisk.LowRisk:
                    return $"[{rootDisplayName}] 为 {role}，写入将影响共享代码，请确认影响范围。";
                default:
                    return null;
            }
        }
    }
}
