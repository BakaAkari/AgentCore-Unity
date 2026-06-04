namespace AgentCore.Editor.Workspace
{
    /// <summary>
    /// Workspace 层轻量 VCS 类型枚举。
    /// 主程序集内独立定义，避免依赖可选 VCS 组件程序集。
    /// </summary>
    public enum WorkspaceVcsType
    {
        /// <summary>未检测到版本控制系统。</summary>
        None,

        /// <summary>Subversion (SVN)。</summary>
        Svn,

        /// <summary>Git。</summary>
        Git,

        /// <summary>Perforce。</summary>
        Perforce,

        /// <summary>检测到 VCS 但类型未知。</summary>
        Unknown
    }
}
