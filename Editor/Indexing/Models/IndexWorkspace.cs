using System;

namespace AgentCore.Editor.Components.Indexing.Models
{
    /// <summary>
    /// 索引工作区快照，对应一个 workspace fingerprint 隔离的数据库实例。
    /// </summary>
    public sealed class IndexWorkspace
    {
        /// <summary>数据库自增主键（0 表示尚未持久化）。</summary>
        public int Id { get; set; }

        /// <summary>
        /// Workspace 指纹（16 位十六进制 SHA256 短 hash）。
        /// 由 WorkspaceRoot + VCS 分线 + UnityRoot 相对路径 + 启用的 Scope Root 列表共同决定。
        /// </summary>
        public string Fingerprint { get; set; }

        /// <summary>WorkspaceRoot 绝对路径（规范化正斜杠）。</summary>
        public string WorkspaceRoot { get; set; }

        /// <summary>UnityRoot 绝对路径（规范化正斜杠）。</summary>
        public string UnityRoot { get; set; }

        /// <summary>UnityRoot 相对于 WorkspaceRoot 的路径。FallbackToUnityRoot 时为空字符串。</summary>
        public string UnityRootRelativePath { get; set; }

        /// <summary>VCS 类型字符串（None / Svn / Git / Perforce）。</summary>
        public string VcsType { get; set; }

        /// <summary>VCS 根目录绝对路径。</summary>
        public string VcsRoot { get; set; }

        /// <summary>SVN URL（仅 SVN 场景）。</summary>
        public string SvnUrl { get; set; }

        /// <summary>VCS 仓库根 URL（SVN repository root）。</summary>
        public string RepositoryRoot { get; set; }

        /// <summary>当前 SVN revision 或 Git commit hash。</summary>
        public string Revision { get; set; }

        /// <summary>分线标识（SVN URL 中提取的 branch/tag 段）。</summary>
        public string BranchId { get; set; }

        /// <summary>首次创建时间（UTC Unix 时间戳秒）。</summary>
        public long CreatedAt { get; set; }

        /// <summary>最后更新时间（UTC Unix 时间戳秒）。</summary>
        public long UpdatedAt { get; set; }

        /// <summary>
        /// 创建一个新的 IndexWorkspace，自动填充时间戳。
        /// </summary>
        public static IndexWorkspace Create(
            string fingerprint,
            string workspaceRoot,
            string unityRoot,
            string unityRootRelativePath,
            string vcsType,
            string vcsRoot,
            string svnUrl = null,
            string repositoryRoot = null,
            string revision = null,
            string branchId = null)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return new IndexWorkspace
            {
                Fingerprint = fingerprint,
                WorkspaceRoot = workspaceRoot,
                UnityRoot = unityRoot,
                UnityRootRelativePath = unityRootRelativePath ?? string.Empty,
                VcsType = vcsType ?? "None",
                VcsRoot = vcsRoot ?? workspaceRoot,
                SvnUrl = svnUrl,
                RepositoryRoot = repositoryRoot,
                Revision = revision,
                BranchId = branchId,
                CreatedAt = now,
                UpdatedAt = now
            };
        }
    }
}
