using System;
using AgentCore.Editor.Components.Indexing.Models;
using AgentCore.Editor.Workspace;

namespace AgentCore.Editor.Components.Indexing.Core
{
    /// <summary>
    /// 将 <see cref="WorkspaceContext"/> 转换为 <see cref="IndexWorkspace"/> 快照。
    /// 负责从 v0.9.0 Workspace 基础设施读取 WorkspaceRoot、UnityRoot、VCS 元数据，
    /// 并构建用于索引数据库隔离的 Fingerprint。
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

            // 使用 WorkspaceContext 中已计算好的 Fingerprint
            // 若 Fingerprint 为空（降级场景），则基于 WorkspaceRoot 生成一个简单 hash
            var fingerprint = !string.IsNullOrEmpty(context.Fingerprint)
                ? context.Fingerprint
                : ComputeFallbackFingerprint(context.WorkspaceRoot);

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
        /// 当 WorkspaceContext.Fingerprint 为空时，基于 WorkspaceRoot 路径生成降级 Fingerprint。
        /// </summary>
        private static string ComputeFallbackFingerprint(string workspaceRoot)
        {
            if (string.IsNullOrEmpty(workspaceRoot)) return "00000000";

            // 简单 FNV-1a 32-bit hash，不依赖 System.Security.Cryptography
            uint hash = 2166136261u;
            foreach (var c in workspaceRoot.ToLowerInvariant())
            {
                hash ^= (byte)c;
                hash *= 16777619u;
            }
            return hash.ToString("x8");
        }
    }
}
