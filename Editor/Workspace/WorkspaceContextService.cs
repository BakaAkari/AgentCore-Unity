using System;
using AgentCore.Editor.Workspace.Config;
using AgentCore.Editor.Workspace.Resolution;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Workspace
{
    /// <summary>
    /// 提供统一的 WorkspaceContext 快照。
    /// 单例服务，供所有模块（Bootstrap、VCS、Tools、RAG、Memory、Indexing）消费。
    ///
    /// Domain Reload 安全：
    ///   - 静态缓存在 Domain Reload 后自动失效（_cached = null）。
    ///   - 首次调用 GetCurrent() 时重新解析，不依赖跨 Domain Reload 的持久状态。
    ///   - 不使用 [InitializeOnLoad] 自动初始化，避免 Editor 启动时阻塞。
    /// </summary>
    public static class WorkspaceContextService
    {
        private static WorkspaceContext _cached;
        private static bool _isResolving;

        /// <summary>
        /// 获取当前 WorkspaceContext 快照。
        /// 首次调用时触发解析；后续调用返回缓存值。
        /// 若解析正在进行中，返回 null（避免重入）。
        /// </summary>
        /// <returns>WorkspaceContext 快照，或 null（解析中/失败）。</returns>
        public static WorkspaceContext GetCurrent()
        {
            if (_cached != null)
                return _cached;

            if (_isResolving)
                return null;

            _cached = Resolve();
            return _cached;
        }

        /// <summary>
        /// 强制重新解析 WorkspaceContext，清除缓存。
        /// 用于用户手动刷新（Settings 页面 Refresh 按钮）或 WorkspaceRoot 变化后。
        /// </summary>
        /// <returns>新解析的 WorkspaceContext 快照。</returns>
        public static WorkspaceContext Refresh()
        {
            _cached = null;
            _cached = Resolve();
            return _cached;
        }

        /// <summary>
        /// 清除缓存，下次 GetCurrent() 时重新解析。
        /// </summary>
        public static void InvalidateCache()
        {
            _cached = null;
        }

        /// <summary>
        /// 检查缓存是否有效（已解析且非 Error 状态）。
        /// </summary>
        public static bool IsCacheValid => _cached != null && _cached.IsValid;

        // ── 私有解析逻辑 ──────────────────────────────────────────────────────

        private static WorkspaceContext Resolve()
        {
            _isResolving = true;
            try
            {
                // Step 1: 解析 UnityRoot
                var unityRoot = UnityRootResolver.Resolve();
                if (string.IsNullOrEmpty(unityRoot))
                {
                    return WorkspaceContext.CreateError("UnityRootResolver returned null — cannot determine Unity project root.");
                }

                // Step 2: 解析 WorkspaceRoot（含 VCS 元数据）
                var (workspaceRoot, status, vcsInfo) = WorkspaceRootResolver.Resolve(unityRoot);
                if (string.IsNullOrEmpty(workspaceRoot))
                {
                    return WorkspaceContext.CreateError("WorkspaceRootResolver returned null workspace root.");
                }

                // Step 3: 计算 UnityRootRelativePath
                var unityRelPath = ComputeRelativePath(workspaceRoot, unityRoot);

                // Step 4: 加载 workspace.json 配置
                WorkspaceConfig config = null;
                try { config = WorkspaceConfigStorage.Load(workspaceRoot); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[AgentCore] WorkspaceContextService: failed to load workspace.json: {ex.Message}");
                }

                // Step 5: 解析 Scope Roots
                var roots = ScopeRootResolver.Resolve(workspaceRoot, unityRoot, config);

                // Step 6: 构建 Fingerprint
                var enabledPaths = new System.Collections.Generic.List<string>();
                foreach (var r in roots)
                    if (r.IsEnabled) enabledPaths.Add(r.RelativePath ?? string.Empty);

                var fingerprint = WorkspaceFingerprintBuilder.Build(
                    workspaceRoot,
                    vcsInfo?.Url,
                    vcsInfo?.RepositoryRoot,
                    vcsInfo?.BranchId,
                    unityRelPath,
                    enabledPaths);

                // Step 7: 组装 WorkspaceContext
                var context = new WorkspaceContext
                {
                    WorkspaceRoot = workspaceRoot,
                    UnityRoot = unityRoot,
                    UnityRootRelativePath = unityRelPath ?? string.Empty,
                    Fingerprint = fingerprint,
                    Vcs = vcsInfo,
                    Roots = roots,
                    Status = status,
                    ResolvedAt = DateTime.UtcNow
                };

                return context;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AgentCore] WorkspaceContextService.Resolve() failed: {ex}");
                return WorkspaceContext.CreateError($"Unexpected error during workspace resolution: {ex.Message}");
            }
            finally
            {
                _isResolving = false;
            }
        }

        private static string ComputeRelativePath(string workspaceRoot, string fullPath)
        {
            if (string.IsNullOrEmpty(workspaceRoot) || string.IsNullOrEmpty(fullPath))
                return string.Empty;

            var baseNorm = workspaceRoot.TrimEnd('/') + "/";
            var fullNorm = fullPath.TrimEnd('/');

            if (fullNorm.StartsWith(baseNorm, StringComparison.OrdinalIgnoreCase))
                return fullNorm.Substring(baseNorm.Length);

            // UnityRoot 与 WorkspaceRoot 相同（FallbackToUnityRoot 场景）
            if (string.Equals(fullNorm, workspaceRoot.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            return string.Empty;
        }
    }
}
