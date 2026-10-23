using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Components.Indexing.Models;
using AgentCore.Editor.Components.Indexing.Query;
using AgentCore.Editor.Utils;
using UnityEditor;

namespace AgentCore.Editor.Components.Indexing.Core
{
    /// <summary>
    /// 索引服务单例：持有进程内唯一的索引存储实例与唯一的写线程。
    /// <para>
    /// 面板、后台增量服务、search_code 工具都通过 <see cref="IndexStoreFactory"/> 拿到同一个
    /// <see cref="IIndexStore"/> 句柄；句柄的写操作经 <see cref="IndexWriteQueue"/> 串行执行，
    /// 读操作直接访问后端。句柄的 <c>Dispose</c> 不做任何事，生命周期由本服务管理：
    /// 域重载前 / 编辑器退出时排空写队列、刷盘并关闭后端。
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    public sealed class IndexService
    {
        /// <summary>关闭时等待写队列排空的最长时间。</summary>
        private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(10);

        private readonly object _gate = new object();
        private IIndexStore _backend;
        private SerializedIndexStore _handle;
        private IndexWriteQueue _writer;
        private string _indexDir;
        private bool _shutdown;

        static IndexService()
        {
            Instance = new IndexService();
            AssemblyReloadEvents.beforeAssemblyReload += Instance.Shutdown;
            EditorApplication.quitting += Instance.Shutdown;
        }

        private IndexService()
        {
        }

        /// <summary>全局唯一实例。</summary>
        public static IndexService Instance { get; }

        /// <summary>当前后端是否报告需要全量重建（后端尚未创建时为 false，不会触发创建）。</summary>
        public bool RequiresRebuild
        {
            get
            {
                lock (_gate)
                {
                    return (_backend as IIndexHealth)?.RequiresRebuild ?? false;
                }
            }
        }

        /// <summary>写队列中尚未执行的写操作数。</summary>
        public int PendingWrites
        {
            get
            {
                lock (_gate)
                {
                    return _writer?.PendingCount ?? 0;
                }
            }
        }

        /// <summary>
        /// 获取当前 Workspace 的共享存储句柄。WorkspaceRoot 无法解析时返回 null。
        /// </summary>
        public IIndexStore GetStoreForCurrent()
        {
            try
            {
                var workspace = IndexWorkspaceResolver.ResolveFromCurrent();
                if (string.IsNullOrEmpty(workspace?.WorkspaceRoot))
                    return null;

                return GetStore(workspace.WorkspaceRoot);
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[IndexService] ResolveFromCurrent failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 获取指定 WorkspaceRoot 的共享存储句柄。WorkspaceRoot 变化时关闭旧后端并切换。
        /// 句柄无需 Dispose（Dispose 为空操作）。
        /// </summary>
        public IIndexStore GetStore(string workspaceRoot)
        {
            if (string.IsNullOrEmpty(workspaceRoot))
                throw new ArgumentNullException(nameof(workspaceRoot));

            var indexDir = IndexStoreFactory.GetIndexDir(workspaceRoot);

            lock (_gate)
            {
                if (_shutdown)
                    throw new ObjectDisposedException(nameof(IndexService), "索引服务已关闭（编辑器正在重载或退出）。");

                if (_handle != null && string.Equals(_indexDir, indexDir, StringComparison.OrdinalIgnoreCase))
                    return _handle;

                // Workspace 切换：旧后端排空后关闭
                CloseLocked();

                _backend = IndexStoreFactory.CreateBackend(workspaceRoot);
                _writer = new IndexWriteQueue();
                _handle = new SerializedIndexStore(_backend, _writer);
                _indexDir = indexDir;
                return _handle;
            }
        }

        /// <summary>
        /// 排空写队列、刷盘并关闭后端。域重载前 / 编辑器退出时自动调用。
        /// </summary>
        public void Shutdown()
        {
            lock (_gate)
            {
                if (_shutdown) return;
                _shutdown = true;
                CloseLocked();
            }
        }

        private void CloseLocked()
        {
            if (_writer != null)
            {
                if (!_writer.Complete(ShutdownDrainTimeout))
                    AgentCoreLog.Warning("[IndexService] Index writer did not drain in time; pending writes were dropped and will be redone by the next index run.");
                _writer = null;
            }

            if (_backend != null)
            {
                try
                {
                    (_backend as JsonlIndexStore)?.Flush();
                    _backend.Dispose();
                }
                catch (Exception ex)
                {
                    AgentCoreLog.Warning($"[IndexService] Closing index store failed: {ex.Message}");
                }
                _backend = null;
            }

            _handle?.Detach();
            _handle = null;
            _indexDir = null;
        }

        /// <summary>
        /// 共享存储句柄：写操作排入单写线程，读操作直通后端；Dispose 为空操作。
        /// </summary>
        private sealed class SerializedIndexStore : IIndexStore, IIndexHealth, IScopedIndexStore, IPagedSymbolSearch
        {
            private IIndexStore _inner;
            private IndexWriteQueue _writer;

            public SerializedIndexStore(IIndexStore inner, IndexWriteQueue writer)
            {
                _inner = inner;
                _writer = writer;
            }

            /// <summary>服务关闭或切换 Workspace 后断开，之后的调用明确报错而不是写到已关闭的后端。</summary>
            public void Detach()
            {
                _inner = null;
                _writer = null;
            }

            private IIndexStore Inner => _inner ?? throw new ObjectDisposedException(
                nameof(IIndexStore), "索引存储句柄已失效（编辑器重载、退出或 Workspace 已切换），请重新获取。");

            private IndexWriteQueue Writer => _writer ?? throw new ObjectDisposedException(
                nameof(IIndexStore), "索引存储句柄已失效（编辑器重载、退出或 Workspace 已切换），请重新获取。");

            public string BackendType => Inner.BackendType;

            // ── 写：排队 ──

            public Task<int> UpsertWorkspaceAsync(IndexWorkspace workspace, CancellationToken ct = default)
            {
                var inner = Inner;
                return Writer.EnqueueAsync(() => inner.UpsertWorkspaceAsync(workspace, ct), ct);
            }

            public Task<int> UpsertRootAsync(int workspaceId, IndexRoot root, CancellationToken ct = default)
            {
                var inner = Inner;
                return Writer.EnqueueAsync(() => inner.UpsertRootAsync(workspaceId, root, ct), ct);
            }

            public Task<int> UpsertFileAsync(int workspaceId, int rootId, IndexedFile file, CancellationToken ct = default)
            {
                var inner = Inner;
                return Writer.EnqueueAsync(() => inner.UpsertFileAsync(workspaceId, rootId, file, ct), ct);
            }

            public Task DeleteFileAsync(int fileId, CancellationToken ct = default)
            {
                var inner = Inner;
                return Writer.EnqueueAsync(() => inner.DeleteFileAsync(fileId, ct), ct);
            }

            public Task BulkInsertSymbolsAsync(IEnumerable<SymbolInfo> symbols, CancellationToken ct = default)
            {
                var inner = Inner;
                return Writer.EnqueueAsync(() => inner.BulkInsertSymbolsAsync(symbols, ct), ct);
            }

            public Task DeleteSymbolsByFileAsync(int fileId, CancellationToken ct = default)
            {
                var inner = Inner;
                return Writer.EnqueueAsync(() => inner.DeleteSymbolsByFileAsync(fileId, ct), ct);
            }

            public Task ClearWorkspaceIndexAsync(int workspaceId, CancellationToken ct = default)
            {
                var inner = Inner;
                return Writer.EnqueueAsync(() => inner.ClearWorkspaceIndexAsync(workspaceId, ct), ct);
            }

            public Task SetMetadataAsync(int workspaceId, string key, string value, CancellationToken ct = default)
            {
                var inner = Inner;
                return Writer.EnqueueAsync(() => inner.SetMetadataAsync(workspaceId, key, value, ct), ct);
            }

            public Task BulkInsertDependenciesAsync(IEnumerable<SymbolDependency> deps, CancellationToken ct = default)
            {
                var inner = Inner;
                return Writer.EnqueueAsync(() => inner.BulkInsertDependenciesAsync(deps, ct), ct);
            }

            public Task DeleteDependenciesByFileAsync(int fileId, CancellationToken ct = default)
            {
                var inner = Inner;
                return Writer.EnqueueAsync(() => inner.DeleteDependenciesByFileAsync(fileId, ct), ct);
            }

            public Task<int> ClearRootIndexAsync(int workspaceId, int rootId, CancellationToken ct = default)
            {
                var inner = Inner;
                if (inner is IScopedIndexStore scoped)
                    return Writer.EnqueueAsync(() => scoped.ClearRootIndexAsync(workspaceId, rootId, ct), ct);

                // 后端不支持按 Root 清除：逐文件删除（同样只影响该 Root）
                return Writer.EnqueueAsync(async () =>
                {
                    var files = await inner.GetFilesForRootAsync(rootId, ct).ConfigureAwait(false);
                    foreach (var f in files)
                    {
                        if (f.WorkspaceId != workspaceId) continue;
                        await inner.DeleteDependenciesByFileAsync(f.Id, ct).ConfigureAwait(false);
                        await inner.DeleteSymbolsByFileAsync(f.Id, ct).ConfigureAwait(false);
                        await inner.DeleteFileAsync(f.Id, ct).ConfigureAwait(false);
                    }
                    return files.Count;
                }, ct);
            }

            public Task AcknowledgeRebuildAsync(CancellationToken ct = default)
            {
                if (Inner is IIndexHealth health)
                    return Writer.EnqueueAsync(() => health.AcknowledgeRebuildAsync(ct), ct);
                return Task.CompletedTask;
            }

            // ── 读：直通 ──

            public Task<IndexWorkspace> GetWorkspaceByFingerprintAsync(string fingerprint, CancellationToken ct = default)
                => Inner.GetWorkspaceByFingerprintAsync(fingerprint, ct);

            public Task<IReadOnlyList<IndexRoot>> GetRootsAsync(int workspaceId, CancellationToken ct = default)
                => Inner.GetRootsAsync(workspaceId, ct);

            public Task<IReadOnlyList<IndexedFile>> GetFilesForRootAsync(int rootId, CancellationToken ct = default)
                => Inner.GetFilesForRootAsync(rootId, ct);

            public Task<IndexedFile> GetFileByPathAsync(int workspaceId, string filePath, CancellationToken ct = default)
                => Inner.GetFileByPathAsync(workspaceId, filePath, ct);

            public Task<IReadOnlyList<SymbolInfo>> SearchSymbolsAsync(int workspaceId, SearchQuery query, CancellationToken ct = default)
                => Inner.SearchSymbolsAsync(workspaceId, query, ct);

            public Task<IReadOnlyList<SymbolInfo>> GetSymbolsByFileAsync(int fileId, CancellationToken ct = default)
                => Inner.GetSymbolsByFileAsync(fileId, ct);

            public Task<IReadOnlyList<NamespaceEntry>> GetNamespacesAsync(int workspaceId, IndexScopeType? scopeType = null, CancellationToken ct = default)
                => Inner.GetNamespacesAsync(workspaceId, scopeType, ct);

            public Task<IndexingStats> GetStatsAsync(int workspaceId, CancellationToken ct = default)
                => Inner.GetStatsAsync(workspaceId, ct);

            public Task<string> GetMetadataAsync(int workspaceId, string key, CancellationToken ct = default)
                => Inner.GetMetadataAsync(workspaceId, key, ct);

            public Task<IReadOnlyList<SymbolDependency>> GetDependenciesAsync(int workspaceId, int fileId, int? symbolId = null, CancellationToken ct = default)
                => Inner.GetDependenciesAsync(workspaceId, fileId, symbolId, ct);

            public Task<IReadOnlyList<SymbolDependency>> FindUsagesAsync(int workspaceId, string typeName, CancellationToken ct = default)
                => Inner.FindUsagesAsync(workspaceId, typeName, ct);

            public Task<IReadOnlyList<SymbolInfo>> SearchSymbolsByTextAsync(int workspaceId, string text, int maxResults = 50, CancellationToken ct = default)
                => Inner.SearchSymbolsByTextAsync(workspaceId, text, maxResults, ct);

            public async Task<SymbolSearchPage> SearchSymbolsPageAsync(int workspaceId, SearchQuery query, CancellationToken ct = default)
            {
                var inner = Inner;
                if (inner is IPagedSymbolSearch paged)
                    return await paged.SearchSymbolsPageAsync(workspaceId, query, ct);

                // 后端不支持分页统计：多取一条判断是否截断
                var limit = Math.Min(Math.Max(1, query.Limit), 200);
                query.Limit = limit + 1;
                var list = await inner.SearchSymbolsAsync(workspaceId, query, ct);
                query.Limit = limit;
                return ToPage(list, limit);
            }

            public async Task<SymbolSearchPage> SearchSymbolsByTextPageAsync(int workspaceId, string text, int maxResults, CancellationToken ct = default)
            {
                var inner = Inner;
                if (inner is IPagedSymbolSearch paged)
                    return await paged.SearchSymbolsByTextPageAsync(workspaceId, text, maxResults, ct);

                var limit = Math.Max(1, maxResults);
                var list = await inner.SearchSymbolsByTextAsync(workspaceId, text, limit + 1, ct);
                return ToPage(list, limit);
            }

            private static SymbolSearchPage ToPage(IReadOnlyList<SymbolInfo> list, int limit)
            {
                var truncated = list.Count > limit;
                var matches = new List<SymbolInfo>(Math.Min(list.Count, limit));
                for (var i = 0; i < list.Count && i < limit; i++)
                    matches.Add(list[i]);
                return new SymbolSearchPage
                {
                    Matches = matches,
                    TotalMatches = list.Count,
                    TotalIsLowerBound = truncated,
                    Truncated = truncated,
                    TruncatedReason = truncated ? SearchTruncation.MaxResults : null,
                };
            }

            public int CorruptLineCount => (_inner as IIndexHealth)?.CorruptLineCount ?? 0;

            public bool RequiresRebuild => (_inner as IIndexHealth)?.RequiresRebuild ?? false;

            public string HealthMessage => (_inner as IIndexHealth)?.HealthMessage;

            /// <summary>生命周期由 <see cref="IndexService"/> 管理，调用方的 using / Dispose 不关闭共享实例。</summary>
            public void Dispose()
            {
            }
        }
    }
}
