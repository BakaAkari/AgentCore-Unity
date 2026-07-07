using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Components.Indexing.Models;

namespace AgentCore.Editor.Components.Indexing.Core
{
    /// <summary>
    /// v1.4.0 — Per-root runtime state persistence.
    /// <para>
    /// Piggybacks on <see cref="IIndexStore.SetMetadataAsync"/> / <see cref="IIndexStore.GetMetadataAsync"/>
    /// so no schema migration is required. State keys follow the pattern
    /// <c>root:{rootId}:{field}</c> and are scoped by workspace.
    /// </para>
    /// <para>
    /// This store is intentionally lightweight: writes are async but not batched (state updates
    /// happen at most a handful of times per index run). In-memory cache keeps subsequent reads
    /// free of I/O.
    /// </para>
    /// </summary>
    public sealed class IndexRootStateStore
    {
        // Metadata key format:
        //   root:{rootId}:state          → IndexRootState enum name
        //   root:{rootId}:last_indexed   → ISO 8601 UTC timestamp
        //   root:{rootId}:last_error     → string (optional)
        //   root:{rootId}:file_count     → int (invariant culture)
        //   root:{rootId}:symbol_count   → int (invariant culture)
        private const string KeyPrefix = "root:";
        private const string KeyState = ":state";
        private const string KeyLastIndexed = ":last_indexed";
        private const string KeyLastError = ":last_error";
        private const string KeyFileCount = ":file_count";
        private const string KeySymbolCount = ":symbol_count";

        private readonly IIndexStore _store;
        private readonly int _workspaceId;
        private readonly ConcurrentDictionary<int, IndexRootStatus> _cache =
            new ConcurrentDictionary<int, IndexRootStatus>();

        /// <summary>
        /// Create a state store scoped to a single workspace.
        /// </summary>
        /// <param name="store">Non-null index store providing metadata KV.</param>
        /// <param name="workspaceId">Workspace database id.</param>
        public IndexRootStateStore(IIndexStore store, int workspaceId)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _workspaceId = workspaceId;
        }

        /// <summary>
        /// Load state for a single root. Returns a default <see cref="IndexRootStatus"/> with
        /// <c>NotIndexed</c> when no metadata is stored.
        /// </summary>
        public async Task<IndexRootStatus> LoadAsync(int rootId, CancellationToken ct = default)
        {
            if (_cache.TryGetValue(rootId, out var cached))
            {
                return cached.Clone();
            }

            var status = new IndexRootStatus { RootId = rootId };

            var stateStr = await _store.GetMetadataAsync(_workspaceId, KeyRoot(rootId, KeyState), ct);
            if (!string.IsNullOrEmpty(stateStr)
                && Enum.TryParse<IndexRootState>(stateStr, ignoreCase: true, out var parsedState))
            {
                status.State = parsedState;
            }

            var lastIndexedStr = await _store.GetMetadataAsync(_workspaceId, KeyRoot(rootId, KeyLastIndexed), ct);
            if (!string.IsNullOrEmpty(lastIndexedStr)
                && DateTime.TryParse(lastIndexedStr, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var parsedTs))
            {
                status.LastIndexedAt = parsedTs;
            }

            status.LastError = await _store.GetMetadataAsync(_workspaceId, KeyRoot(rootId, KeyLastError), ct);

            var fileCountStr = await _store.GetMetadataAsync(_workspaceId, KeyRoot(rootId, KeyFileCount), ct);
            if (int.TryParse(fileCountStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fileCount))
            {
                status.FileCount = fileCount;
            }

            var symbolCountStr = await _store.GetMetadataAsync(_workspaceId, KeyRoot(rootId, KeySymbolCount), ct);
            if (int.TryParse(symbolCountStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var symbolCount))
            {
                status.SymbolCount = symbolCount;
            }

            _cache[rootId] = status.Clone();
            return status;
        }

        /// <summary>
        /// Load state for many roots in one call (short-lived transient cache is per-instance,
        /// so this method is convenience only — I/O still happens per root when the cache misses).
        /// </summary>
        public async Task<Dictionary<int, IndexRootStatus>> LoadManyAsync(
            IEnumerable<int> rootIds, CancellationToken ct = default)
        {
            var result = new Dictionary<int, IndexRootStatus>();
            if (rootIds == null)
            {
                return result;
            }

            foreach (var id in rootIds)
            {
                result[id] = await LoadAsync(id, ct);
            }

            return result;
        }

        /// <summary>
        /// Persist a complete <see cref="IndexRootStatus"/>. Missing fields are cleared.
        /// </summary>
        public async Task SaveAsync(IndexRootStatus status, CancellationToken ct = default)
        {
            if (status == null) throw new ArgumentNullException(nameof(status));

            var rootId = status.RootId;
            await _store.SetMetadataAsync(_workspaceId, KeyRoot(rootId, KeyState), status.State.ToString(), ct);
            await _store.SetMetadataAsync(_workspaceId, KeyRoot(rootId, KeyLastIndexed),
                status.LastIndexedAt?.ToString("O"), ct);
            await _store.SetMetadataAsync(_workspaceId, KeyRoot(rootId, KeyLastError), status.LastError, ct);
            await _store.SetMetadataAsync(_workspaceId, KeyRoot(rootId, KeyFileCount),
                status.FileCount.ToString(CultureInfo.InvariantCulture), ct);
            await _store.SetMetadataAsync(_workspaceId, KeyRoot(rootId, KeySymbolCount),
                status.SymbolCount.ToString(CultureInfo.InvariantCulture), ct);

            _cache[rootId] = status.Clone();
        }

        /// <summary>
        /// Update just the lifecycle <see cref="IndexRootState"/> of a root without touching
        /// other fields. Convenience method for state transitions during an index run.
        /// </summary>
        public async Task SetStateAsync(int rootId, IndexRootState state, CancellationToken ct = default)
        {
            var status = await LoadAsync(rootId, ct);
            status.State = state;
            if (state != IndexRootState.Failed)
            {
                status.LastError = null;
            }
            await SaveAsync(status, ct);
        }

        /// <summary>
        /// Mark a root as <see cref="IndexRootState.Ready"/> and update the timestamp / counts.
        /// </summary>
        public async Task MarkReadyAsync(int rootId, int fileCount, int symbolCount, CancellationToken ct = default)
        {
            await SaveAsync(new IndexRootStatus
            {
                RootId = rootId,
                State = IndexRootState.Ready,
                LastIndexedAt = DateTime.UtcNow,
                LastError = null,
                FileCount = fileCount,
                SymbolCount = symbolCount
            }, ct);
        }

        /// <summary>
        /// Mark a root as <see cref="IndexRootState.Failed"/> and record the error message.
        /// </summary>
        public async Task MarkFailedAsync(int rootId, string error, CancellationToken ct = default)
        {
            var status = await LoadAsync(rootId, ct);
            status.State = IndexRootState.Failed;
            status.LastError = error;
            await SaveAsync(status, ct);
        }

        /// <summary>
        /// Populate <see cref="IndexRoot"/> objects in place with cached / persisted state.
        /// Used by list_roots / diagnose / IndexingStatusBlockBuilder.
        /// </summary>
        public async Task ApplyStatesToRootsAsync(IEnumerable<IndexRoot> roots, CancellationToken ct = default)
        {
            if (roots == null) return;

            foreach (var root in roots)
            {
                if (root == null || root.Id <= 0) continue;
                var status = await LoadAsync(root.Id, ct);
                root.IndexState = status.State;
                root.LastIndexedAt = status.LastIndexedAt;
                root.LastIndexError = status.LastError;
                root.IndexedFileCount = status.FileCount;
                root.IndexedSymbolCount = status.SymbolCount;
            }
        }

        /// <summary>
        /// Recompute file / symbol counts for a root by querying the store, then persist
        /// <see cref="IndexRootState.Ready"/> with the fresh counts.
        /// </summary>
        public async Task RefreshAndMarkReadyAsync(int rootId, CancellationToken ct = default)
        {
            var files = await _store.GetFilesForRootAsync(rootId, ct);
            int fileCount = files?.Count ?? 0;
            int symbolCount = 0;
            if (files != null)
            {
                foreach (var f in files)
                {
                    if (f == null) continue;
                    symbolCount += f.SymbolCount;
                }
            }

            await MarkReadyAsync(rootId, fileCount, symbolCount, ct);
        }

        /// <summary>
        /// Compose the metadata key for a root field.
        /// </summary>
        private static string KeyRoot(int rootId, string suffix) => $"{KeyPrefix}{rootId}{suffix}";

        /// <summary>
        /// Invalidate the in-memory cache (call after external metadata mutations).
        /// </summary>
        public void InvalidateCache()
        {
            _cache.Clear();
        }
    }

    /// <summary>
    /// v1.4.0 — Snapshot of a single root's runtime state.
    /// </summary>
    public sealed class IndexRootStatus
    {
        public int RootId;
        public IndexRootState State = IndexRootState.NotIndexed;
        public DateTime? LastIndexedAt;
        public string LastError;
        public int FileCount;
        public int SymbolCount;

        public IndexRootStatus Clone()
        {
            return new IndexRootStatus
            {
                RootId = RootId,
                State = State,
                LastIndexedAt = LastIndexedAt,
                LastError = LastError,
                FileCount = FileCount,
                SymbolCount = SymbolCount
            };
        }
    }
}
