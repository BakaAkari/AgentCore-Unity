using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Components.Indexing.Models;

namespace AgentCore.Editor.Components.Indexing.Core
{
    /// <summary>
    /// 一次成功索引运行的类型。
    /// </summary>
    public enum IndexRunKind
    {
        /// <summary>全量重建（所有 enabled Root）。</summary>
        Full,

        /// <summary>增量 / 针对性增量（后台服务、index_incremental）。</summary>
        Incremental,

        /// <summary>只重建部分 Root（index_scope）。</summary>
        Scope,
    }

    /// <summary>
    /// 索引时间戳快照（search_code status / get_stats 与面板使用）。
    /// </summary>
    public sealed class IndexTimestamps
    {
        /// <summary>最后一次全量重建完成时间（只有全量重建会更新）。</summary>
        public DateTime? LastFullIndexAt;

        /// <summary>最后一次增量索引完成时间。</summary>
        public DateTime? LastIncrementalAt;

        /// <summary>最后一次 Scope 重建完成时间。</summary>
        public DateTime? LastScopeIndexAt;

        /// <summary>最后一次 Scope 重建覆盖的 Root（"#id Scope/Name"，逗号分隔）。</summary>
        public string LastScopeIndexRoots;

        /// <summary>最后一次任意类型索引运行完成时间（全量 / 增量 / Scope 都会更新）。</summary>
        public DateTime? LastIndexAt;

        /// <summary>最后一次索引运行的类型（"full" / "incremental" / "scope"），未知时为 null。</summary>
        public string LastIndexKind;
    }

    /// <summary>
    /// 索引运行元数据的读写。
    /// <para>
    /// 语义：<c>last_full_index_at</c> 只由全量重建写入（Scope 重建不等于全量）；
    /// 每次成功运行都写 <c>last_index_at</c> + <c>last_index_kind</c>；
    /// 增量写 <c>last_incremental_at</c>，Scope 写 <c>last_scope_index_at</c> + <c>last_scope_index_roots</c>。
    /// 每个 Root 的时间戳另由 <see cref="IndexRootStateStore"/> 维护（root:{id}:last_indexed）。
    /// </para>
    /// </summary>
    public static class IndexRunMetadata
    {
        /// <summary>记录一次成功的索引运行。<paramref name="scopeRoots"/> 仅在 <see cref="IndexRunKind.Scope"/> 时使用。</summary>
        public static async Task RecordAsync(
            IIndexStore store,
            int workspaceId,
            IndexRunKind kind,
            IReadOnlyList<IndexRoot> scopeRoots,
            DateTime utcNow,
            CancellationToken ct = default)
        {
            var stamp = utcNow.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

            switch (kind)
            {
                case IndexRunKind.Full:
                    await store.SetMetadataAsync(workspaceId, CodebaseIndexer.MetaKeyLastFullIndex, stamp, ct);
                    break;
                case IndexRunKind.Incremental:
                    await store.SetMetadataAsync(workspaceId, CodebaseIndexer.MetaKeyLastIncrementalIndex, stamp, ct);
                    break;
                case IndexRunKind.Scope:
                    await store.SetMetadataAsync(workspaceId, CodebaseIndexer.MetaKeyLastScopeIndex, stamp, ct);
                    await store.SetMetadataAsync(workspaceId, CodebaseIndexer.MetaKeyLastScopeIndexRoots,
                        DescribeRoots(scopeRoots), ct);
                    break;
            }

            await store.SetMetadataAsync(workspaceId, CodebaseIndexer.MetaKeyLastIndex, stamp, ct);
            await store.SetMetadataAsync(workspaceId, CodebaseIndexer.MetaKeyLastIndexKind, KindToString(kind), ct);
        }

        /// <summary>读取全部索引时间戳。</summary>
        public static async Task<IndexTimestamps> ReadAsync(IIndexStore store, int workspaceId, CancellationToken ct = default)
        {
            return new IndexTimestamps
            {
                LastFullIndexAt = ParseTime(await store.GetMetadataAsync(workspaceId, CodebaseIndexer.MetaKeyLastFullIndex, ct)),
                LastIncrementalAt = ParseTime(await store.GetMetadataAsync(workspaceId, CodebaseIndexer.MetaKeyLastIncrementalIndex, ct)),
                LastScopeIndexAt = ParseTime(await store.GetMetadataAsync(workspaceId, CodebaseIndexer.MetaKeyLastScopeIndex, ct)),
                LastScopeIndexRoots = NullIfEmpty(await store.GetMetadataAsync(workspaceId, CodebaseIndexer.MetaKeyLastScopeIndexRoots, ct)),
                LastIndexAt = ParseTime(await store.GetMetadataAsync(workspaceId, CodebaseIndexer.MetaKeyLastIndex, ct)),
                LastIndexKind = NullIfEmpty(await store.GetMetadataAsync(workspaceId, CodebaseIndexer.MetaKeyLastIndexKind, ct)),
            };
        }

        /// <summary>
        /// 时间戳解读提示：Scope 重建晚于最后一次全量时说明 last_full_index_at 并未因此刷新。
        /// </summary>
        public static string Describe(IndexTimestamps t)
        {
            if (t == null)
                return null;

            if (!t.LastFullIndexAt.HasValue)
                return t.LastIndexAt.HasValue
                    ? "尚未执行过全量重建；last_index_at 来自增量或 Scope 运行，未索引的 Root 可执行 index_full 补齐。"
                    : null;

            if (t.LastScopeIndexAt.HasValue && t.LastScopeIndexAt.Value > t.LastFullIndexAt.Value)
                return $"最近一次 Scope 重建（{t.LastScopeIndexRoots}）晚于最后一次全量重建；last_full_index_at 只反映全量重建，其他 Root 的新鲜度见 search_code status 的 per_root_state。";

            return null;
        }

        /// <summary>
        /// 解析元数据时间戳：索引器写 ISO 8601（"O"），旧数据可能是 Unix 秒。无法解析时返回 null。
        /// </summary>
        public static DateTime? ParseTime(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                return parsed.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
                    : parsed.ToUniversalTime();

            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds))
            {
                try { return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime; }
                catch (ArgumentOutOfRangeException) { return null; }
            }

            return null;
        }

        /// <summary>运行类型的元数据取值。</summary>
        public static string KindToString(IndexRunKind kind)
        {
            switch (kind)
            {
                case IndexRunKind.Full: return "full";
                case IndexRunKind.Scope: return "scope";
                default: return "incremental";
            }
        }

        private static string DescribeRoots(IReadOnlyList<IndexRoot> roots)
        {
            if (roots == null || roots.Count == 0)
                return string.Empty;

            return string.Join(", ", roots.Where(r => r != null).Select(r => $"#{r.Id} {r.ScopeType}/{r.ScopeName}"));
        }

        private static string NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;
    }
}
