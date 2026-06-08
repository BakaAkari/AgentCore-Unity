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
