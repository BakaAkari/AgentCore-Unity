using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Components.Indexing.Models;
using AgentCore.Editor.Components.Indexing.Query;

namespace AgentCore.Editor.Components.Indexing.Core
{
    /// <summary>
    /// 索引存储后端抽象。默认实现为 JSONL，可升级为 SQLite。
    /// 所有写操作应在单一后台线程串行执行；读操作可并发。
    /// </summary>
    public interface IIndexStore : IDisposable
    {
        /// <summary>当前存储后端类型标识（"jsonl" / "sqlite"）。</summary>
        string BackendType { get; }

        // ── Workspace ──────────────────────────────────────────────────────────

        /// <summary>
        /// 插入或更新 workspace 记录（按 fingerprint 唯一）。
        /// 返回 workspace 的数据库 ID。
        /// </summary>
        Task<int> UpsertWorkspaceAsync(IndexWorkspace workspace, CancellationToken ct = default);

        /// <summary>
        /// 按 fingerprint 查询 workspace。不存在时返回 null。
        /// </summary>
        Task<IndexWorkspace> GetWorkspaceByFingerprintAsync(string fingerprint, CancellationToken ct = default);

        // ── Roots ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 插入或更新 index root 记录（按 workspaceId + rootPath 唯一）。
        /// 返回 root 的数据库 ID。
        /// </summary>
        Task<int> UpsertRootAsync(int workspaceId, IndexRoot root, CancellationToken ct = default);

        /// <summary>
        /// 获取指定 workspace 下的所有 root 列表。
        /// </summary>
        Task<IReadOnlyList<IndexRoot>> GetRootsAsync(int workspaceId, CancellationToken ct = default);

        // ── Files ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 插入或更新文件记录（按 workspaceId + filePath 唯一）。
        /// 返回文件的数据库 ID。
        /// </summary>
        Task<int> UpsertFileAsync(int workspaceId, int rootId, IndexedFile file, CancellationToken ct = default);

        /// <summary>
        /// 删除指定文件记录（同时级联删除其符号）。
        /// </summary>
        Task DeleteFileAsync(int fileId, CancellationToken ct = default);

        /// <summary>
        /// 获取指定 root 下的所有已索引文件列表。
        /// </summary>
        Task<IReadOnlyList<IndexedFile>> GetFilesForRootAsync(int rootId, CancellationToken ct = default);

        /// <summary>
        /// 按文件路径查询文件记录。不存在时返回 null。
        /// </summary>
        Task<IndexedFile> GetFileByPathAsync(int workspaceId, string filePath, CancellationToken ct = default);

        // ── Symbols ────────────────────────────────────────────────────────────

        /// <summary>
        /// 批量插入符号记录。
        /// </summary>
        Task BulkInsertSymbolsAsync(IEnumerable<SymbolInfo> symbols, CancellationToken ct = default);

        /// <summary>
        /// 删除指定文件的所有符号记录。
        /// </summary>
        Task DeleteSymbolsByFileAsync(int fileId, CancellationToken ct = default);

        /// <summary>
        /// 按查询条件搜索符号。
        /// </summary>
        Task<IReadOnlyList<SymbolInfo>> SearchSymbolsAsync(int workspaceId, SearchQuery query, CancellationToken ct = default);

        /// <summary>
        /// 获取指定文件的所有符号列表。
        /// </summary>
        Task<IReadOnlyList<SymbolInfo>> GetSymbolsByFileAsync(int fileId, CancellationToken ct = default);

        /// <summary>
        /// 获取指定 workspace 下所有命名空间列表（去重，按 scope 过滤）。
        /// </summary>
        Task<IReadOnlyList<NamespaceEntry>> GetNamespacesAsync(int workspaceId, IndexScopeType? scopeType = null, CancellationToken ct = default);

        // ── Stats ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 获取指定 workspace 的索引统计信息。
        /// </summary>
        Task<IndexingStats> GetStatsAsync(int workspaceId, CancellationToken ct = default);

        /// <summary>
        /// 清空指定 workspace 的所有文件和符号记录（保留 workspace 和 root 元数据）。
        /// </summary>
        Task ClearWorkspaceIndexAsync(int workspaceId, CancellationToken ct = default);

        // ── Metadata ───────────────────────────────────────────────────────────

        /// <summary>
        /// 设置 KV 元数据（如 last_full_index_at、last_incremental_at）。
        /// </summary>
        Task SetMetadataAsync(int workspaceId, string key, string value, CancellationToken ct = default);

        /// <summary>
        /// 获取 KV 元数据。不存在时返回 null。
        /// </summary>
        Task<string> GetMetadataAsync(int workspaceId, string key, CancellationToken ct = default);

        // ── Dependencies ───────────────────────────────────────────────────────

        /// <summary>
        /// 批量插入依赖关系记录。
        /// </summary>
        Task BulkInsertDependenciesAsync(IEnumerable<SymbolDependency> deps, CancellationToken ct = default);

        /// <summary>
        /// 删除指定文件的所有依赖关系记录（重新索引前清理）。
        /// </summary>
        Task DeleteDependenciesByFileAsync(int fileId, CancellationToken ct = default);

        /// <summary>
        /// 获取指定文件（或符号）的正向依赖列表。
        /// <para>symbolId 为 null 时返回文件级所有依赖；不为 null 时只返回该符号的依赖。</para>
        /// </summary>
        Task<IReadOnlyList<SymbolDependency>> GetDependenciesAsync(
            int workspaceId, int fileId, int? symbolId = null, CancellationToken ct = default);

        /// <summary>
        /// 查找所有引用了指定类型名称的依赖记录（反向引用 / find usages）。
        /// </summary>
        Task<IReadOnlyList<SymbolDependency>> FindUsagesAsync(
            int workspaceId, string typeName, CancellationToken ct = default);

        // ── Full-Text Search ───────────────────────────────────────────────────

        /// <summary>
        /// 使用全文索引（FTS5）搜索符号。
        /// <para>JSONL 后端降级为 LIKE 模糊匹配；SQLite 后端使用 FTS5 虚拟表。</para>
        /// </summary>
        Task<IReadOnlyList<SymbolInfo>> SearchSymbolsByTextAsync(
            int workspaceId, string text, int maxResults = 50, CancellationToken ct = default);
    }

    /// <summary>
    /// 可选能力：存储健康状态（损坏行统计 / 是否需要重建）。
    /// 后端检测到数据损坏超过阈值时应标记 <see cref="RequiresRebuild"/>，
    /// 而不是静默返回残缺数据。
    /// </summary>
    public interface IIndexHealth
    {
        /// <summary>加载时跳过的损坏行数（未加载时为 0）。</summary>
        int CorruptLineCount { get; }

        /// <summary>损坏超过阈值，索引需要全量重建。</summary>
        bool RequiresRebuild { get; }

        /// <summary>人类可读的健康说明（健康时为 null）。</summary>
        string HealthMessage { get; }

        /// <summary>全量重建完成后调用：清除损坏标记并整理存储。</summary>
        Task AcknowledgeRebuildAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// 可选能力：按 Root 清除索引数据（index_scope 只重建目标 Root，不清空整个 workspace）。
    /// 未实现该接口的后端由调用方逐文件删除。
    /// </summary>
    public interface IScopedIndexStore
    {
        /// <summary>删除指定 Root 下的全部文件、符号与依赖记录，返回删除的文件数。</summary>
        Task<int> ClearRootIndexAsync(int workspaceId, int rootId, CancellationToken ct = default);
    }

    /// <summary>
    /// 可选能力：原子替换单个文件的索引（文件记录 + 符号一次提交）。
    /// 未实现该接口的后端由 <see cref="IndexFileReplace"/> 按“待定记录 → 符号 → 最终记录”的顺序降级写入。
    /// </summary>
    public interface IAtomicFileIndexStore
    {
        /// <summary>
        /// 用新的文件记录与符号整体替换该路径的旧记录与旧符号（旧依赖由调用方另行处理），返回文件 Id。
        /// 符号的 Id / FileId / WorkspaceId / RootId 由存储回写。
        /// </summary>
        Task<int> ReplaceFileIndexAsync(int workspaceId, int rootId, IndexedFile file, IReadOnlyList<SymbolInfo> symbols, CancellationToken ct = default);
    }

    /// <summary>
    /// 单文件索引替换：后端支持 <see cref="IAtomicFileIndexStore"/> 时一次提交，否则按安全顺序降级。
    /// </summary>
    public static class IndexFileReplace
    {
        /// <summary>替换单个文件的文件记录与符号，返回文件 Id。</summary>
        public static Task<int> ReplaceAsync(IIndexStore store, int workspaceId, int rootId, IndexedFile file, IReadOnlyList<SymbolInfo> symbols, CancellationToken ct = default)
        {
            if (store is IAtomicFileIndexStore atomic)
                return atomic.ReplaceFileIndexAsync(workspaceId, rootId, file, symbols, ct);
            return FallbackAsync(store, workspaceId, rootId, file, symbols, ct);
        }

        /// <summary>
        /// 降级顺序：先写 LastModified=0 的待定记录（中途失败时增量核对会把该文件视为已变更而重试），
        /// 再替换符号，最后写入真实文件记录。符号写入失败直接抛出，由调用方计为该文件失败。
        /// </summary>
        public static async Task<int> FallbackAsync(IIndexStore store, int workspaceId, int rootId, IndexedFile file, IReadOnlyList<SymbolInfo> symbols, CancellationToken ct = default)
        {
            var lastModified = file.LastModified;
            file.LastModified = 0;
            var fileId = await store.UpsertFileAsync(workspaceId, rootId, file, ct).ConfigureAwait(false);
            file.Id = fileId;

            await store.DeleteSymbolsByFileAsync(fileId, ct).ConfigureAwait(false);
            if (symbols != null && symbols.Count > 0)
            {
                foreach (var sym in symbols)
                {
                    sym.FileId = fileId;
                    sym.WorkspaceId = workspaceId;
                    sym.RootId = rootId;
                }
                await store.BulkInsertSymbolsAsync(symbols, ct).ConfigureAwait(false);
            }

            file.LastModified = lastModified;
            return await store.UpsertFileAsync(workspaceId, rootId, file, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 可选能力：返回带总数与截断信息的符号搜索结果。
    /// </summary>
    public interface IPagedSymbolSearch
    {
        /// <summary>同 <see cref="IIndexStore.SearchSymbolsAsync"/>，附带 totalMatches / truncated。</summary>
        Task<SymbolSearchPage> SearchSymbolsPageAsync(int workspaceId, SearchQuery query, CancellationToken ct = default);

        /// <summary>同 <see cref="IIndexStore.SearchSymbolsByTextAsync"/>，附带 totalMatches / truncated。</summary>
        Task<SymbolSearchPage> SearchSymbolsByTextPageAsync(int workspaceId, string text, int maxResults, CancellationToken ct = default);
    }

    /// <summary>
    /// 可选能力：按符号 ID 直接查找（get_symbol_context 的 symbol_id 路径）。
    /// 未实现该接口的后端，调用方应如实返回“不支持”，而不是返回空结果。
    /// </summary>
    public interface ISymbolLookup
    {
        /// <summary>返回指定 workspace 中 ID 对应的符号，不存在时返回 null。</summary>
        Task<SymbolInfo> GetSymbolByIdAsync(int workspaceId, int symbolId, CancellationToken ct = default);
    }

    /// <summary>
    /// 截断原因常量（返回给模型的 truncatedReason）。
    /// </summary>
    public static class SearchTruncation
    {
        /// <summary>匹配数超过 max_results / limit。</summary>
        public const string MaxResults = "max_results";

        /// <summary>整体时间预算耗尽，扫描未完成。</summary>
        public const string Timeout = "timeout";

        /// <summary>文件数 / 字节预算耗尽，扫描未完成。</summary>
        public const string FileCap = "file_cap";
    }

    /// <summary>
    /// 一页符号搜索结果。
    /// </summary>
    public sealed class SymbolSearchPage
    {
        /// <summary>本次返回的匹配（已排序、已按 limit 截断）。</summary>
        public IReadOnlyList<SymbolInfo> Matches { get; set; } = Array.Empty<SymbolInfo>();

        /// <summary>匹配总数；<see cref="TotalIsLowerBound"/> 为 true 时只是已扫描部分的计数。</summary>
        public int TotalMatches { get; set; }

        /// <summary>扫描未完成（超时等），TotalMatches 只是下界。</summary>
        public bool TotalIsLowerBound { get; set; }

        /// <summary>结果是否被截断。</summary>
        public bool Truncated { get; set; }

        /// <summary>截断原因（见 <see cref="SearchTruncation"/>），未截断时为 null。</summary>
        public string TruncatedReason { get; set; }
    }

    /// <summary>
    /// 命名空间条目（用于 list_namespaces action）。
    /// </summary>
    public sealed class NamespaceEntry
    {
        /// <summary>命名空间全名。</summary>
        public string Namespace { get; set; }

        /// <summary>所属 Scope 类型。</summary>
        public IndexScopeType ScopeType { get; set; }

        /// <summary>所属 Scope 名称。</summary>
        public string ScopeName { get; set; }

        /// <summary>该命名空间下的文件数。</summary>
        public int FileCount { get; set; }

        /// <summary>该命名空间下的符号数。</summary>
        public int SymbolCount { get; set; }
    }
}
