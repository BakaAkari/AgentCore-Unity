using System;

namespace AgentCore.Editor.Workspace
{
    /// <summary>
    /// Workspace 层轻量 VCS 元数据快照。
    /// 由 SvnWorkspaceInfoResolver（或其他 VCS 解析器）填充，
    /// 主程序集内独立定义，不依赖可选 VCS 组件程序集。
    /// </summary>
    [Serializable]
    public sealed class WorkspaceVcsInfo
    {
        /// <summary>检测到的 VCS 类型。</summary>
        public WorkspaceVcsType Type { get; set; }

        /// <summary>VCS 工作副本根目录绝对路径（规范化正斜杠）。</summary>
        public string RootPath { get; set; }

        /// <summary>SVN URL（当前工作副本的 URL）。</summary>
        public string Url { get; set; }

        /// <summary>SVN 仓库根 URL。</summary>
        public string RepositoryRoot { get; set; }

        /// <summary>
        /// 分支标识符，从 SVN URL 提取：
        /// - URL 含 /branches/&lt;name&gt; → "branches/&lt;name&gt;"
        /// - URL 含 /trunk → "trunk"
        /// - 无法识别 → URL 短 hash 或空字符串
        /// </summary>
        public string BranchId { get; set; }

        /// <summary>当前工作副本 SVN Revision（字符串，如 "123456"）。</summary>
        public string Revision { get; set; }

        /// <summary>是否为有效的 VCS 工作副本（命令执行成功且数据完整）。</summary>
        public bool IsWorkingCopy { get; set; }

        /// <summary>VCS 命令是否可用（svn/git/p4 命令行工具存在）。</summary>
        public bool IsCommandAvailable { get; set; }

        /// <summary>解析失败时的错误信息。</summary>
        public string ErrorMessage { get; set; }
    }
}
