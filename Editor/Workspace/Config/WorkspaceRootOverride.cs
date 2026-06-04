using System;

namespace AgentCore.Editor.Workspace.Config
{
    /// <summary>
    /// workspace.json 中单个 Scope Root 的用户覆盖配置。
    /// 与 ScopeRootResolver 自动发现结果合并。
    /// </summary>
    [Serializable]
    public sealed class WorkspaceRootOverride
    {
        /// <summary>相对于 WorkspaceRoot 的路径（规范化正斜杠，必填）。</summary>
        public string RelativePath { get; set; }

        /// <summary>可选的友好显示名称（覆盖自动推断的目录名）。</summary>
        public string DisplayName { get; set; }

        /// <summary>可选的 ScopeType 覆盖（null 表示使用自动推断值）。</summary>
        public WorkspaceScopeType? ScopeType { get; set; }

        /// <summary>可选的 Role 覆盖（null 表示使用默认 Role）。</summary>
        public WorkspaceRootRole? Role { get; set; }

        /// <summary>是否在 AgentCore 中启用此 Root（默认 true）。</summary>
        public bool IsEnabled { get; set; } = true;
    }
}
