using System;
using System.Collections.Generic;
using System.Linq;
using AgentCore.Editor.Components.Indexing.Models;
using AgentCore.Editor.Components.Indexing.Roots;

namespace AgentCore.Editor.Components.Indexing.Core
{
    /// <summary>
    /// 聚合所有 <see cref="IIndexRootProvider"/> 的发现结果，
    /// 去重合并后返回最终的 <see cref="IndexRoot"/> 列表。
    ///
    /// 合并规则：
    /// 1. 按 Provider Priority 升序执行（数值越小越先执行）。
    /// 2. 同一 RootPath 只保留第一个发现的条目（高优先级 Provider 优先）。
    /// 3. 路径规范化后比较（大小写不敏感，统一正斜杠）。
    /// 4. 过滤掉不存在的目录（Provider 实现应已过滤，此处作为安全兜底）。
    /// </summary>
    public sealed class IndexRootResolver
    {
        private readonly List<IIndexRootProvider> _providers;

        /// <summary>
        /// 使用指定的 Provider 列表初始化 Resolver。
        /// </summary>
        /// <param name="providers">参与发现的 Provider 列表（顺序不影响结果，内部按 Priority 排序）。</param>
        public IndexRootResolver(IEnumerable<IIndexRootProvider> providers)
        {
            _providers = providers != null
                ? providers.OrderBy(p => p.Priority).ToList()
                : new List<IIndexRootProvider>();
        }

        /// <summary>
        /// 使用默认 Provider 集合初始化 Resolver（不含用户配置和额外授权根）。
        /// 适用于无 Settings 依赖的场景（如单元测试）。
        /// </summary>
        public IndexRootResolver()
        {
            _providers = new List<IIndexRootProvider>
            {
                new UnityRootProvider(),
                new VcsWorkspaceRootProvider(),
                new WorkspaceChildRootProvider(),
                new UserConfiguredScopeRootProvider(),
                new ResourcePackageMetadataProvider(),
                new ExtraAuthorizedRootProvider(),
            };
            _providers.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        }

        /// <summary>
        /// 执行所有 Provider，聚合并去重返回最终 IndexRoot 列表。
        /// </summary>
        /// <param name="workspace">当前 IndexWorkspace 上下文。</param>
        /// <returns>去重后的 IndexRoot 列表（按 Priority 顺序，高优先级在前）。</returns>
        public IReadOnlyList<IndexRoot> Resolve(IndexWorkspace workspace)
        {
            if (workspace == null) return Array.Empty<IndexRoot>();

            var result = new List<IndexRoot>();
            // 用于去重的路径集合（规范化小写）
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var provider in _providers)
            {
                IReadOnlyList<IndexRoot> discovered;
                try
                {
                    discovered = provider.DiscoverRoots(workspace);
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning(
                        $"[AgentCore.Indexing] Provider '{provider.ProviderId}' threw an exception: {ex.Message}");
                    continue;
                }

                if (discovered == null) continue;

                foreach (var root in discovered)
                {
                    if (root == null) continue;
                    if (string.IsNullOrEmpty(root.RootPath)) continue;

                    var normalizedPath = NormalizePath(root.RootPath);
                    if (seenPaths.Contains(normalizedPath)) continue;

                    seenPaths.Add(normalizedPath);

                    // v1.4.0 — assign scheduling priority based on role/scope.
                    // Runtime state (IndexState/LastIndexedAt/counts) will be populated
                    // by IndexRootStateStore when the store is accessible.
                    root.Priority = IndexingSchedulePolicy.ResolvePriority(root);

                    result.Add(root);
                }
            }

            return result;
        }

        /// <summary>
        /// 获取当前已注册的 Provider 列表（按 Priority 升序）。
        /// </summary>
        public IReadOnlyList<IIndexRootProvider> Providers => _providers;

        /// <summary>
        /// 规范化路径：统一正斜杠、去除末尾斜杠、转小写（用于去重比较）。
        /// </summary>
        private static string NormalizePath(string path)
        {
            return path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        }
    }
}
