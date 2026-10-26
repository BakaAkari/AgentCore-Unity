using System;
using System.IO;
using AgentCore.Editor.Components.Indexing.Models;
using AgentCore.Editor.Workspace;

namespace AgentCore.Editor.Components.Indexing.Core
{
    /// <summary>
    /// 将 <see cref="WorkspaceContext"/> 转换为 <see cref="IndexWorkspace"/> 快照。
    /// 负责从 v0.9.0 Workspace 基础设施读取 WorkspaceRoot、UnityRoot、VCS 元数据，
    /// 并构建用于索引数据库隔离的 Fingerprint。
    /// <para>
    /// 索引的工作区身份只取规范化后的 WorkspaceRoot：SVN URL / 分支 / 修订号只作为属性保存，
    /// svn switch 或切换分支不会产生新的工作区（旧版指纹含 VCS 信息，由存储层按根路径迁移）。
    /// </para>
    /// </summary>
    public static class IndexWorkspaceResolver
    {
        /// <summary>
        /// 从当前 <see cref="WorkspaceContextService"/> 解析 <see cref="IndexWorkspace"/>。
        /// </summary>
        /// <returns>
        /// 解析成功时返回 <see cref="IndexWorkspace"/> 实例；
        /// WorkspaceContext 无效时返回 null。
        /// </returns>
        public static IndexWorkspace ResolveFromCurrent()
        {
            var ctx = WorkspaceContextService.GetCurrent();
            return ctx != null ? ResolveFromContext(ctx) : null;
        }

        /// <summary>
        /// 从指定 <see cref="WorkspaceContext"/> 解析 <see cref="IndexWorkspace"/>。
        /// </summary>
        /// <param name="context">已解析的 WorkspaceContext 快照。</param>
        /// <returns>
        /// 解析成功时返回 <see cref="IndexWorkspace"/> 实例；
        /// context 无效时返回 null。
        /// </returns>
        public static IndexWorkspace ResolveFromContext(WorkspaceContext context)
        {
            if (context == null) return null;
            if (!context.IsValid) return null;
            if (string.IsNullOrEmpty(context.WorkspaceRoot)) return null;
            if (string.IsNullOrEmpty(context.UnityRoot)) return null;

            var vcs = context.Vcs;
            var vcsType = vcs != null ? vcs.Type.ToString() : "None";
            var vcsRoot = vcs?.RootPath ?? string.Empty;
            var svnUrl = vcs?.Url ?? string.Empty;
            var repositoryRoot = vcs?.RepositoryRoot ?? string.Empty;
            var revision = vcs?.Revision ?? string.Empty;
            var branchId = vcs?.BranchId ?? string.Empty;

            // 不使用 WorkspaceContext.Fingerprint（含 VCS URL / 分支 / 启用的 Root），只按根路径计算
            var fingerprint = ComputeWorkspaceFingerprint(context.WorkspaceRoot);

            return IndexWorkspace.Create(
                fingerprint: fingerprint,
                workspaceRoot: context.WorkspaceRoot,
                unityRoot: context.UnityRoot,
                unityRootRelativePath: context.UnityRootRelativePath ?? string.Empty,
                vcsType: vcsType,
                vcsRoot: vcsRoot,
                svnUrl: svnUrl,
                repositoryRoot: repositoryRoot,
                revision: revision,
                branchId: branchId
            );
        }

        /// <summary>
        /// 索引工作区指纹：规范化 WorkspaceRoot 的 FNV-1a 64-bit hash（不依赖 System.Security.Cryptography）。
        /// </summary>
        public static string ComputeWorkspaceFingerprint(string workspaceRoot)
        {
            var normalized = NormalizeWorkspaceRoot(workspaceRoot);
            if (string.IsNullOrEmpty(normalized)) return "0000000000000000";

            ulong hash = 14695981039346656037UL;
            foreach (var c in normalized)
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }
            return hash.ToString("x16");
        }

        /// <summary>
        /// 规范化工作区根路径：绝对路径、正斜杠、去掉末尾斜杠、小写（Windows 路径大小写不敏感）。
        /// </summary>
        public static string NormalizeWorkspaceRoot(string workspaceRoot)
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot)) return string.Empty;

            var path = workspaceRoot.Trim();
            try { path = Path.GetFullPath(path); }
            catch { /* 非法路径按原样比较 */ }
            return path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        }
    }
}
