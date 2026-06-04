using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Components.Indexing.Models;
using AgentCore.Editor.Components.Indexing.Query;
using Newtonsoft.Json;

namespace AgentCore.Editor.Components.Indexing.Core
{
    /// <summary>
    /// JSONL 文件存储后端（MVP 实现）。
    /// 数据存储在 {dbDir}/ 下的多个 .jsonl 文件中。
    /// 适合小型项目或 SQLite DLL 不可用时的降级场景。
    /// 所有写操作应在单一后台线程串行调用，读操作可并发。
    /// </summary>
    public sealed class JsonlIndexStore : IIndexStore
    {
        private readonly string _dbDir;
        private readonly object _writeLock = new object();

        // 内存缓存（Domain Reload 后重新加载）
        private List<IndexWorkspace> _workspaces;
        private List<IndexRoot> _roots;
        private List<IndexedFile> _files;
        private List<SymbolInfo> _symbols;
        private Dictionary<string, Dictionary<string, string>> _metadata; // workspaceId_key -> value

        private bool _loaded;
        private bool _disposed;

        // 文件名常量
        private const string WorkspacesFile = "workspaces.jsonl";
        private const string RootsFile = "roots.jsonl";
        private const string FilesFile = "files.jsonl";
        private const string SymbolsFile = "symbols.jsonl";
        private const string MetadataFile = "metadata.jsonl";

        /// <inheritdoc/>
        public string BackendType => "jsonl";

        /// <summary>
        /// 创建 JSONL 存储实例。
        /// </summary>
        /// <param name="dbDir">数据库目录路径（将自动创建）。</param>
        public JsonlIndexStore(string dbDir)
        {
            _dbDir = dbDir ?? throw new ArgumentNullException(nameof(dbDir));
        }

        // ── 初始化 ─────────────────────────────────────────────────────────────

        private void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_writeLock)
            {
                if (_loaded) return;
                Directory.CreateDirectory(_dbDir);
                _workspaces = LoadJsonl<IndexWorkspace>(WorkspacesFile);
                _roots = LoadJsonl<IndexRoot>(RootsFile);
                _files = LoadJsonl<IndexedFile>(FilesFile);
                _symbols = LoadJsonl<SymbolInfo>(SymbolsFile);
                _metadata = LoadMetadata();
                _loaded = true;
            }
        }

        // ── Workspace ──────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public Task<int> UpsertWorkspaceAsync(IndexWorkspace workspace, CancellationToken ct = default)
        {
            EnsureLoaded();
            lock (_writeLock)
            {
                var existing = _workspaces.FirstOrDefault(w => w.Fingerprint == workspace.Fingerprint);
                if (existing != null)
                {
                    existing.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    existing.Revision = workspace.Revision;
                    existing.BranchId = workspace.BranchId;
                    SaveJsonl(_workspaces, WorkspacesFile);
                    return Task.FromResult(existing.Id);
                }

                workspace.Id = NextId(_workspaces.Count > 0 ? _workspaces.Max(w => w.Id) : 0);
                _workspaces.Add(workspace);
                SaveJsonl(_workspaces, WorkspacesFile);
                return Task.FromResult(workspace.Id);
            }
        }

        /// <inheritdoc/>
        public Task<IndexWorkspace> GetWorkspaceByFingerprintAsync(string fingerprint, CancellationToken ct = default)
        {
            EnsureLoaded();
            var ws = _workspaces.FirstOrDefault(w => w.Fingerprint == fingerprint);
            return Task.FromResult(ws);
        }

        // ── Roots ──────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public Task<int> UpsertRootAsync(int workspaceId, IndexRoot root, CancellationToken ct = default)
        {
            EnsureLoaded();
            lock (_writeLock)
            {
                root.WorkspaceId = workspaceId;
                var existing = _roots.FirstOrDefault(r =>
                    r.WorkspaceId == workspaceId &&
                    string.Equals(r.RootPath, root.RootPath, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    existing.DisplayName = root.DisplayName;
                    existing.ScopeType = root.ScopeType;
                    existing.ScopeName = root.ScopeName;
                    existing.Role = root.Role;
                    existing.ReadOnly = root.ReadOnly;
                    existing.IsEnabled = root.IsEnabled;
                    existing.IncludePatterns = root.IncludePatterns;
                    existing.ExcludePatterns = root.ExcludePatterns;
                    existing.IsDefaultSearchScope = root.IsDefaultSearchScope;
                    SaveJsonl(_roots, RootsFile);
                    return Task.FromResult(existing.Id);
                }

                root.Id = NextId(_roots.Count > 0 ? _roots.Max(r => r.Id) : 0);
                _roots.Add(root);
                SaveJsonl(_roots, RootsFile);
                return Task.FromResult(root.Id);
            }
        }

        /// <inheritdoc/>
        public Task<IReadOnlyList<IndexRoot>> GetRootsAsync(int workspaceId, CancellationToken ct = default)
        {
            EnsureLoaded();
            IReadOnlyList<IndexRoot> result = _roots.Where(r => r.WorkspaceId == workspaceId).ToList();
            return Task.FromResult(result);
        }

        // ── Files ──────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public Task<int> UpsertFileAsync(int workspaceId, int rootId, IndexedFile file, CancellationToken ct = default)
        {
            EnsureLoaded();
            lock (_writeLock)
            {
                file.WorkspaceId = workspaceId;
                file.RootId = rootId;
                var existing = _files.FirstOrDefault(f =>
                    f.WorkspaceId == workspaceId &&
                    string.Equals(f.FilePath, file.FilePath, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    existing.ContentHash = file.ContentHash;
                    existing.LastModified = file.LastModified;
                    existing.LastIndexed = file.LastIndexed;
                    existing.FileSize = file.FileSize;
                    existing.HasErrors = file.HasErrors;
                    existing.ErrorMessage = file.ErrorMessage;
                    existing.SymbolCount = file.SymbolCount;
                    existing.RelativeToRoot = file.RelativeToRoot;
                    SaveJsonl(_files, FilesFile);
                    return Task.FromResult(existing.Id);
                }

                file.Id = NextId(_files.Count > 0 ? _files.Max(f => f.Id) : 0);
                _files.Add(file);
                SaveJsonl(_files, FilesFile);
                return Task.FromResult(file.Id);
            }
        }

        /// <inheritdoc/>
        public Task DeleteFileAsync(int fileId, CancellationToken ct = default)
        {
            EnsureLoaded();
            lock (_writeLock)
            {
                _files.RemoveAll(f => f.Id == fileId);
                _symbols.RemoveAll(s => s.FileId == fileId);
                SaveJsonl(_files, FilesFile);
                SaveJsonl(_symbols, SymbolsFile);
            }
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task<IReadOnlyList<IndexedFile>> GetFilesForRootAsync(int rootId, CancellationToken ct = default)
        {
            EnsureLoaded();
            IReadOnlyList<IndexedFile> result = _files.Where(f => f.RootId == rootId).ToList();
            return Task.FromResult(result);
        }

        /// <inheritdoc/>
        public Task<IndexedFile> GetFileByPathAsync(int workspaceId, string filePath, CancellationToken ct = default)
        {
            EnsureLoaded();
            var normalized = NormalizePath(filePath);
            var file = _files.FirstOrDefault(f =>
                f.WorkspaceId == workspaceId &&
                string.Equals(NormalizePath(f.FilePath), normalized, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(file);
        }

        // ── Symbols ────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public Task BulkInsertSymbolsAsync(IEnumerable<SymbolInfo> symbols, CancellationToken ct = default)
        {
            EnsureLoaded();
            lock (_writeLock)
            {
                int maxId = _symbols.Count > 0 ? _symbols.Max(s => s.Id) : 0;
                foreach (var sym in symbols)
                {
                    sym.Id = ++maxId;
                    _symbols.Add(sym);
                }
                SaveJsonl(_symbols, SymbolsFile);
            }
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task DeleteSymbolsByFileAsync(int fileId, CancellationToken ct = default)
        {
            EnsureLoaded();
            lock (_writeLock)
            {
                _symbols.RemoveAll(s => s.FileId == fileId);
                SaveJsonl(_symbols, SymbolsFile);
            }
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task<IReadOnlyList<SymbolInfo>> SearchSymbolsAsync(int workspaceId, SearchQuery query, CancellationToken ct = default)
        {
            EnsureLoaded();

            // 获取该 workspace 下启用的 root ID 集合
            var enabledRootIds = new HashSet<int>(
                _roots.Where(r => r.WorkspaceId == workspaceId && r.IsEnabled).Select(r => r.Id));

            var results = _symbols.Where(s => s.WorkspaceId == workspaceId && enabledRootIds.Contains(s.RootId));

            // Scope 过滤
            if (query.ScopeType.HasValue)
                results = results.Where(s => s.ScopeType == query.ScopeType.Value);

            if (!string.IsNullOrEmpty(query.ScopeName))
                results = results.Where(s => string.Equals(s.ScopeName, query.ScopeName, StringComparison.OrdinalIgnoreCase));

            // Role 过滤
            if (query.Role.HasValue)
                results = results.Where(s => s.Role == query.Role.Value);

            // 只读过滤
            if (query.ReadOnly.HasValue)
                results = results.Where(s => s.ReadOnly == query.ReadOnly.Value);

            // Plugin / Engine / Generated 过滤
            if (!query.IncludePlugins)
                results = results.Where(s => s.ScopeType != IndexScopeType.Plugin);
            if (!query.IncludeEngine)
                results = results.Where(s => s.ScopeType != IndexScopeType.Engine);
            if (!query.IncludeGenerated)
                results = results.Where(s => s.ScopeType != IndexScopeType.Generated);

            // 符号类型过滤
            if (!string.IsNullOrEmpty(query.SymbolType))
                results = results.Where(s => string.Equals(s.SymbolType, query.SymbolType, StringComparison.OrdinalIgnoreCase));

            // 命名空间过滤
            if (!string.IsNullOrEmpty(query.Namespace))
                results = results.Where(s => s.Namespace != null && s.Namespace.StartsWith(query.Namespace, StringComparison.OrdinalIgnoreCase));

            // Root ID 过滤
            if (query.RootId > 0)
                results = results.Where(s => s.RootId == query.RootId);

            // 名称搜索
            if (!string.IsNullOrEmpty(query.Query))
            {
                if (query.Regex)
                {
                    try
                    {
                        var rx = new Regex(query.Query, RegexOptions.IgnoreCase);
                        results = results.Where(s => rx.IsMatch(s.Name) || rx.IsMatch(s.FullName ?? string.Empty));
                    }
                    catch
                    {
                        // 正则无效时降级为模糊匹配
                        results = results.Where(s => s.Name.IndexOf(query.Query, StringComparison.OrdinalIgnoreCase) >= 0);
                    }
                }
                else if (query.Fuzzy)
                {
                    results = results.Where(s =>
                        s.Name.IndexOf(query.Query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (s.FullName != null && s.FullName.IndexOf(query.Query, StringComparison.OrdinalIgnoreCase) >= 0));
                }
                else
                {
                    results = results.Where(s => string.Equals(s.Name, query.Query, StringComparison.OrdinalIgnoreCase));
                }
            }

            // 排序：当前 Scope > Shared > Project > Tools > Engine > Plugin
            var ordered = results.OrderBy(s => GetScopePriority(s.ScopeType))
                                 .ThenBy(s => s.Name);

            int limit = Math.Min(Math.Max(1, query.Limit), 200);
            IReadOnlyList<SymbolInfo> list = ordered.Take(limit).ToList();
            return Task.FromResult(list);
        }

        /// <inheritdoc/>
        public Task<IReadOnlyList<SymbolInfo>> GetSymbolsByFileAsync(int fileId, CancellationToken ct = default)
        {
            EnsureLoaded();
            IReadOnlyList<SymbolInfo> result = _symbols.Where(s => s.FileId == fileId).ToList();
            return Task.FromResult(result);
        }

        /// <inheritdoc/>
        public Task<IReadOnlyList<NamespaceEntry>> GetNamespacesAsync(int workspaceId, IndexScopeType? scopeType = null, CancellationToken ct = default)
        {
            EnsureLoaded();

            var query = _symbols.Where(s => s.WorkspaceId == workspaceId && !string.IsNullOrEmpty(s.Namespace));
            if (scopeType.HasValue)
                query = query.Where(s => s.ScopeType == scopeType.Value);

            var grouped = query
                .GroupBy(s => new { s.Namespace, s.ScopeType, s.ScopeName })
                .Select(g => new NamespaceEntry
                {
                    Namespace = g.Key.Namespace,
                    ScopeType = g.Key.ScopeType,
                    ScopeName = g.Key.ScopeName,
                    SymbolCount = g.Count(),
                    FileCount = g.Select(s => s.FileId).Distinct().Count()
                })
                .OrderByDescending(n => n.FileCount)
                .ToList();

            IReadOnlyList<NamespaceEntry> result = grouped;
            return Task.FromResult(result);
        }

        // ── Stats ──────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public Task<IndexingStats> GetStatsAsync(int workspaceId, CancellationToken ct = default)
        {
            EnsureLoaded();

            var ws = _workspaces.FirstOrDefault(w => w.Id == workspaceId);
            var roots = _roots.Where(r => r.WorkspaceId == workspaceId && r.IsEnabled).ToList();
            var files = _files.Where(f => f.WorkspaceId == workspaceId).ToList();
            var symbols = _symbols.Where(s => s.WorkspaceId == workspaceId).ToList();

            var stats = new IndexingStats
            {
                WorkspaceId = workspaceId,
                Fingerprint = ws?.Fingerprint,
                WorkspaceRoot = ws?.WorkspaceRoot,
                UnityRoot = ws?.UnityRoot,
                BranchId = ws?.BranchId,
                StoreBackend = BackendType,
                EnabledRootCount = roots.Count,
                TotalFiles = files.Count,
                TotalSymbols = symbols.Count,
                ErrorFileCount = files.Count(f => f.HasErrors)
            };

            // 解析时间戳
            var fullAt = GetMetadataSync(workspaceId, "last_full_index_at");
            if (fullAt != null && long.TryParse(fullAt, out var fullTs))
                stats.LastFullIndexAt = DateTimeOffset.FromUnixTimeSeconds(fullTs).UtcDateTime;

            var incrAt = GetMetadataSync(workspaceId, "last_incremental_at");
            if (incrAt != null && long.TryParse(incrAt, out var incrTs))
                stats.LastIncrementalIndexAt = DateTimeOffset.FromUnixTimeSeconds(incrTs).UtcDateTime;

            var durationStr = GetMetadataSync(workspaceId, "last_full_index_duration_seconds");
            if (durationStr != null && double.TryParse(durationStr, out var dur))
                stats.LastFullIndexDurationSeconds = dur;

            // Root 分组统计
            foreach (var root in roots)
            {
                var rootFiles = files.Where(f => f.RootId == root.Id).ToList();
                var rootSymbols = symbols.Where(s => s.RootId == root.Id).ToList();
                stats.RootBreakdown.Add(new IndexingStats.RootStats
                {
                    RootId = root.Id,
                    DisplayName = root.DisplayName,
                    ScopeType = root.ScopeType,
                    ScopeName = root.ScopeName,
                    ReadOnly = root.ReadOnly,
                    FileCount = rootFiles.Count,
                    SymbolCount = rootSymbols.Count
                });
            }

            return Task.FromResult(stats);
        }

        /// <inheritdoc/>
        public Task ClearWorkspaceIndexAsync(int workspaceId, CancellationToken ct = default)
        {
            EnsureLoaded();
            lock (_writeLock)
            {
                _files.RemoveAll(f => f.WorkspaceId == workspaceId);
                _symbols.RemoveAll(s => s.WorkspaceId == workspaceId);
                SaveJsonl(_files, FilesFile);
                SaveJsonl(_symbols, SymbolsFile);
            }
            return Task.CompletedTask;
        }

        // ── Metadata ───────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public Task SetMetadataAsync(int workspaceId, string key, string value, CancellationToken ct = default)
        {
            EnsureLoaded();
            lock (_writeLock)
            {
                var dictKey = workspaceId.ToString();
                if (!_metadata.TryGetValue(dictKey, out var dict))
                {
                    dict = new Dictionary<string, string>();
                    _metadata[dictKey] = dict;
                }
                dict[key] = value;
                SaveMetadata();
            }
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task<string> GetMetadataAsync(int workspaceId, string key, CancellationToken ct = default)
        {
            EnsureLoaded();
            return Task.FromResult(GetMetadataSync(workspaceId, key));
        }

        // ── IDisposable ────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public void Dispose()
        {
            _disposed = true;
        }

        // ── 私有辅助方法 ───────────────────────────────────────────────────────

        private string GetMetadataSync(int workspaceId, string key)
        {
            if (_metadata == null) return null;
            var dictKey = workspaceId.ToString();
            if (!_metadata.TryGetValue(dictKey, out var dict)) return null;
            dict.TryGetValue(key, out var value);
            return value;
        }

        private static int NextId(int currentMax) => currentMax + 1;

        private static string NormalizePath(string path)
        {
            return path?.Replace('\\', '/');
        }

        private static int GetScopePriority(IndexScopeType scopeType)
        {
            switch (scopeType)
            {
                case IndexScopeType.Project: return 1;
                case IndexScopeType.Shared: return 2;
                case IndexScopeType.Mode: return 3;
                case IndexScopeType.Map: return 4;
                case IndexScopeType.UI: return 5;
                case IndexScopeType.Localization: return 6;
                case IndexScopeType.Package: return 7;
                case IndexScopeType.Tools: return 8;
                case IndexScopeType.Engine: return 9;
                case IndexScopeType.Plugin: return 10;
                case IndexScopeType.Generated: return 11;
                default: return 12;
            }
        }

        private List<T> LoadJsonl<T>(string fileName)
        {
            var path = Path.Combine(_dbDir, fileName);
            var list = new List<T>();
            if (!File.Exists(path)) return list;

            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var item = JsonConvert.DeserializeObject<T>(line);
                    if (item != null) list.Add(item);
                }
                catch
                {
                    // 跳过损坏的行
                }
            }
            return list;
        }

        private void SaveJsonl<T>(List<T> list, string fileName)
        {
            var path = Path.Combine(_dbDir, fileName);
            var sb = new StringBuilder();
            foreach (var item in list)
            {
                sb.AppendLine(JsonConvert.SerializeObject(item, Formatting.None));
            }
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        private Dictionary<string, Dictionary<string, string>> LoadMetadata()
        {
            var path = Path.Combine(_dbDir, MetadataFile);
            var result = new Dictionary<string, Dictionary<string, string>>();
            if (!File.Exists(path)) return result;

            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonConvert.DeserializeObject<MetadataEntry>(line);
                    if (entry != null)
                    {
                        if (!result.TryGetValue(entry.WorkspaceId, out var dict))
                        {
                            dict = new Dictionary<string, string>();
                            result[entry.WorkspaceId] = dict;
                        }
                        dict[entry.Key] = entry.Value;
                    }
                }
                catch { }
            }
            return result;
        }

        private void SaveMetadata()
        {
            var path = Path.Combine(_dbDir, MetadataFile);
            var sb = new StringBuilder();
            foreach (var kvp in _metadata)
            {
                foreach (var entry in kvp.Value)
                {
                    sb.AppendLine(JsonConvert.SerializeObject(new MetadataEntry
                    {
                        WorkspaceId = kvp.Key,
                        Key = entry.Key,
                        Value = entry.Value
                    }, Formatting.None));
                }
            }
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        private sealed class MetadataEntry
        {
            [JsonProperty("wid")] public string WorkspaceId { get; set; }
            [JsonProperty("k")] public string Key { get; set; }
            [JsonProperty("v")] public string Value { get; set; }
        }
    }
}
