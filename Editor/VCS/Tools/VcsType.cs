namespace AgentCore.Editor.Components.VCS.Tools
{
    /// <summary>
    /// 版本控制系统类型
    /// </summary>
    public enum VcsType
    {
        /// <summary>
        /// 未检测到版本控制系统
        /// </summary>
        None,
        
        /// <summary>
        /// Subversion (SVN)
        /// </summary>
        Svn,
        
        /// <summary>
        /// Perforce (P4)
        /// </summary>
        Perforce,
        
        /// <summary>
        /// Git
        /// </summary>
        Git
    }
}
