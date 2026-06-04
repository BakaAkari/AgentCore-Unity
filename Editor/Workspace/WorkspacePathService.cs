using System;
using AgentCore.Editor.Workspace.Resolution;

namespace AgentCore.Editor.Workspace
{
    /// <summary>
    /// 统一路径解析服务。
    /// 基于 WorkspaceContextService 提供 WorkspaceRoot-aware 路径操作。
    /// P0 阶段定义接口，后续 FileSystem/RAG/Memory/Indexing 工具逐步接入。
    /// </summary>
    public static class WorkspacePathService
    {
        /// <summary>
        /// 将 WorkspaceRoot-relative 路径解析为绝对路径。
        /// </summary>
        public static string ResolveWorkspacePath(string workspaceRelativePath)
        {
            var ctx = WorkspaceContextService.GetCurrent();
            if (ctx == null || string.IsNullOrEmpty(ctx.WorkspaceRoot))
                return null;

            if (string.IsNullOrEmpty(workspaceRelativePath))
                return ctx.WorkspaceRoot;

            return UnityRootResolver.NormalizePath(
                System.IO.Path.Combine(ctx.WorkspaceRoot, workspaceRelativePath));
        }

        /// <summary>
        /// 将 Unity Asset 路径（如 "Assets/Scripts/Foo.cs"）解析为绝对路径。
        /// </summary>
        public static string ResolveUnityAssetPath(string assetPath)
        {
            var ctx = WorkspaceContextService.GetCurrent();
            if (ctx == null || string.IsNullOrEmpty(ctx.UnityRoot))
                return null;

            if (string.IsNullOrEmpty(assetPath))
                return ctx.UnityRoot;

            return UnityRootResolver.NormalizePath(
                System.IO.Path.Combine(ctx.UnityRoot, assetPath));
        }

        /// <summary>
        /// 根据绝对路径查找对应的 WorkspaceRootInfo。
        /// </summary>
        public static WorkspaceRootInfo TryGetRootInfo(string absolutePath)
        {
            var ctx = WorkspaceContextService.GetCurrent();
            if (ctx?.Roots == null || string.IsNullOrEmpty(absolutePath))
                return null;

            var normPath = UnityRootResolver.NormalizePath(absolutePath).TrimEnd('/') + "/";

            WorkspaceRootInfo bestMatch = null;
            int bestLen = 0;

            foreach (var root in ctx.Roots)
            {
                if (string.IsNullOrEmpty(root.AbsolutePath)) continue;
                var rootNorm = root.AbsolutePath.TrimEnd('/') + "/";
                if (normPath.StartsWith(rootNorm, StringComparison.OrdinalIgnoreCase) && rootNorm.Length > bestLen)
                {
                    bestMatch = root;
                    bestLen = rootNorm.Length;
                }
            }

            return bestMatch;
        }

        /// <summary>
        /// 检查绝对路径是否在 WorkspaceRoot 内。
        /// </summary>
        public static bool IsInsideWorkspace(string absolutePath)
        {
            var ctx = WorkspaceContextService.GetCurrent();
            if (ctx == null || string.IsNullOrEmpty(ctx.WorkspaceRoot) || string.IsNullOrEmpty(absolutePath))
                return false;

            var normPath = UnityRootResolver.NormalizePath(absolutePath).TrimEnd('/') + "/";
            var rootNorm = ctx.WorkspaceRoot.TrimEnd('/') + "/";
            return normPath.StartsWith(rootNorm, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 检查绝对路径是否在 UnityRoot 内。
        /// </summary>
        public static bool IsInsideUnityRoot(string absolutePath)
        {
            var ctx = WorkspaceContextService.GetCurrent();
            if (ctx == null || string.IsNullOrEmpty(ctx.UnityRoot) || string.IsNullOrEmpty(absolutePath))
                return false;

            var normPath = UnityRootResolver.NormalizePath(absolutePath).TrimEnd('/') + "/";
            var rootNorm = ctx.UnityRoot.TrimEnd('/') + "/";
            return normPath.StartsWith(rootNorm, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 获取绝对路径相对于 WorkspaceRoot 的相对路径。
        /// </summary>
        public static string GetRelativeToWorkspace(string absolutePath)
        {
            var ctx = WorkspaceContextService.GetCurrent();
            if (ctx == null || string.IsNullOrEmpty(ctx.WorkspaceRoot) || string.IsNullOrEmpty(absolutePath))
                return null;

            return ComputeRelative(ctx.WorkspaceRoot, absolutePath);
        }

        /// <summary>
        /// 获取绝对路径相对于 UnityRoot 的相对路径。
        /// </summary>
        public static string GetRelativeToUnityRoot(string absolutePath)
        {
            var ctx = WorkspaceContextService.GetCurrent();
            if (ctx == null || string.IsNullOrEmpty(ctx.UnityRoot) || string.IsNullOrEmpty(absolutePath))
                return null;

            return ComputeRelative(ctx.UnityRoot, absolutePath);
        }

        // ── 私有辅助 ──────────────────────────────────────────────────────────

        private static string ComputeRelative(string basePath, string fullPath)
        {
            var baseNorm = basePath.TrimEnd('/') + "/";
            var fullNorm = UnityRootResolver.NormalizePath(fullPath).TrimEnd('/');

            if (fullNorm.StartsWith(baseNorm, StringComparison.OrdinalIgnoreCase))
                return fullNorm.Substring(baseNorm.Length);

            return null;
        }
    }
}
