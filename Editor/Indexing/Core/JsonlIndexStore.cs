using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Components.Indexing.Models;
using AgentCore.Editor.Components.Indexing.Query;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.Components.Indexing.Core
{
    /// <summary>
    /// JSONL 文件存储后端。
    /// <para>
    /// 磁盘布局（{dbDir}/）：
    /// <list type="bullet">
    /// <item><c>manifest.json</c> — 当前代（generation）编号，经 <see cref="AgentCore.Editor.Utils.AtomicFile"/> 原子替换，是整次整理的唯一提交点。</item>
    /// <item><c>gen-N/{workspaces,roots,files,symbols,metadata}.jsonl</c> — 第 N 代的基线快照。</item>
    /// <item><c>gen-N/oplog.jsonl</c> — 基线之后的追加式操作日志（upsert / delete），每次写只追加一行。</item>
    /// </list>
    /// 加载 = 读基线 + 重放操作日志。操作日志超过 max(基线大小, 阈值) 时整理：
    /// 写出 gen-(N+1) 基线 → 原子替换 manifest → 删除旧代。整理过程中崩溃时旧代完整可用。
    /// 旧版（无 manifest、根目录下直接放 *.jsonl）首次加载时自动迁移。
    /// </para>
    /// <para>
    /// 线程模型：写操作由 <see cref="IndexService"/> 的单写线程串行调用；读操作可并发，
    /// 内部用 <see cref="_gate"/> 保护内存结构，长时间扫描在锁外进行。
    /// </para>
    /// </summary>
    public sealed class JsonlIndexStore : IIndexStore, IIndexHealth, IScopedIndexStore, IPagedSymbolSearch
    {
        // ── 常量 ────────────────────────────────────────────────────────────────

        /// <summary>允许的损坏行数（崩溃时操作日志最后一行可能写了一半）。超过即要求重建。</summary>
        public const int CorruptLineTolerance = 1;

        /// <summary>操作日志整理的最小字节阈值（默认 4 MB）。</summary>
        public const long DefaultCompactMinBytes = 4L * 1024 * 1024;

        /// <summary>单次符号搜索的整体时间预算（毫秒）。</summary>
        public const int SearchTimeBudgetMs = 3000;

        private const int FormatVersion = 2;
        private const string ManifestFile = "manifest.json";
        private const string GenerationDirPrefix = "gen-";
        private const string OpLogFile = "oplog.jsonl";
        private const string WorkspacesFile = "workspaces.jsonl";
        private const string RootsFile = "roots.jsonl";
        private const string FilesFile = "files.jsonl";
        private const string SymbolsFile = "symbols.jsonl";
        private const string MetadataFile = "metadata.jsonl";

        private static readonly string[] BaseFiles = { WorkspacesFile, RootsFile, FilesFile, SymbolsFile, MetadataFile };
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        // 操作日志 op 名
        private const string OpWorkspace = "ws";
        private const string OpRoot = "root";
        private const string OpFile = "file";
        private const string OpDeleteFile = "delfile";
        private const string OpSymbols = "syms";
        private const string OpDeleteSymbols = "delsyms";
        private const string OpClearWorkspace = "clearws";
        private const string OpClearRoot = "clearroot";
        private const string OpMeta = "meta";

        // ── 字段 ────────────────────────────────────────────────────────────────

        private readonly string _dbDir;
        private readonly long _compactMinBytes;
        private readonly object _gate = new object();

        // 内存结构（Domain Reload 后重新加载）
        private readonly Dictionary<int, IndexWorkspace> _workspaces = new Dictionary<int, IndexWorkspace>();
        private readonly Dictionary<int, IndexRoot> _roots = new Dictionary<int, IndexRoot>();
        private readonly Dictionary<int, IndexedFile> _files = new Dictionary<int, IndexedFile>();
        private readonly Dictionary<string, int> _fileIdByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, List<SymbolInfo>> _symbolsByFile = new Dictionary<int, List<SymbolInfo>>();
        private readonly Dictionary<string, Dictionary<string, string>> _metadata = new Dictionary<string, Dictionary<string, string>>(); // workspaceId -> key -> value
        private int _symbolCount;
        private int _maxWorkspaceId, _maxRootId, _maxFileId, _maxSymbolId;

        // 持久化状态
        private int _generation;
        private FileStream _opLog;
        private bool _opLogNeedsNewline;
        private long _opLogBytes;
        private long _baseBytes;
        private int _opCount;

        // 健康状态
        private volatile bool _loaded;
        private int _corruptLines;
        private bool _requiresRebuild;
        private string _healthMessage;
        private bool _disposed;

        /// <inheritdoc/>
        public string BackendType => "jsonl";

        /// <summary>
        /// 创建 JSONL 存储实例。
        /// </summary>
        /// <param name="dbDir">数据库目录路径（将自动创建）。</param>
        public JsonlIndexStore(string dbDir) : this(dbDir, DefaultCompactMinBytes)
        {
        }

        /// <summary>
        /// 创建 JSONL 存储实例并指定整理阈值（测试用）。
        /// </summary>
        public JsonlIndexStore(string dbDir, long compactMinBytes)
        {
            _dbDir = dbDir ?? throw new ArgumentNullException(nameof(dbDir));
            _compactMinBytes = Math.Max(0, compactMinBytes);
        }

        // ── 诊断属性（测试 / 面板） ──────────────────────────────────────────────

        // 诊断属性先确保已加载：否则在首次读写之前访问会得到“健康 / 第 0 代”的假象。

        /// <summary>当前代编号（0 表示尚未写过新格式）。</summary>
        public int Generation { get { EnsureLoadedForDiagnostics(); lock (_gate) return _generation; } }

        /// <summary>当前操作日志中的操作条数。</summary>
        public int PendingOpCount { get { EnsureLoadedForDiagnostics(); lock (_gate) return _opCount; } }

        /// <inheritdoc/>
        public int CorruptLineCount { get { EnsureLoadedForDiagnostics(); lock (_gate) return _corruptLines; } }

        /// <inheritdoc/>
        public bool RequiresRebuild { get { EnsureLoadedForDiagnostics(); lock (_gate) return _requiresRebuild; } }

        /// <inheritdoc/>
        public string HealthMessage { get { EnsureLoadedForDiagnostics(); lock (_gate) return _healthMessage; } }

        private void EnsureLoadedForDiagnostics()
        {
            try
            {
                EnsureLoaded();
            }
            catch (ObjectDisposedException)
            {
                // 已释放：返回最后已知状态
            }
            catch (Exception ex)
            {
                AgentCore.Editor.Utils.AgentCoreLog.Warning($"[JsonlIndexStore] Loading index for diagnostics failed: {ex.Message}");
            }
        }

        // ── 初始化 ─────────────────────────────────────────────────────────────

        private void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_gate)
            {
                if (_loaded) return;
                ThrowIfDisposedLocked();
                Directory.CreateDirectory(_dbDir);
                LoadLocked();
                _loaded = true;
                EvaluateHealthLocked();
            }
        }

        private void LoadLocked()
        {
            var manifestPath = Path.Combine(_dbDir, ManifestFile);
            if (File.Exists(manifestPath))
            {
                int generation = -1;
                try
                {
                    var manifest = JsonConvert.DeserializeObject<Manifest>(File.ReadAllText(manifestPath, Encoding.UTF8));
                    if (manifest != null && manifest.Generation >= 1)
                        generation = manifest.Generation;
                }
                catch
                {
                    // manifest 是原子写入的，读不出来说明被外部破坏
                }

                if (generation < 1)
                {
                    // 无法确定哪一代可信：从空索引开始并要求重建，下一次整理写到已有最大代之后
                    _corruptLines += CorruptLineTolerance + 1;
                    _healthMessage = "manifest.json 损坏，无法确定有效的索引快照。";
                    _generation = FindMaxGenerationOnDisk();
                    return;
                }

                _generation = generation;
                var genDir = GetGenerationDir(generation);
                _baseBytes = LoadBaseLocked(genDir);
                ReplayOpLogLocked(Path.Combine(genDir, OpLogFile));
                return;
            }

            // 旧版布局：根目录下直接放 *.jsonl
            _generation = 0;
            _baseBytes = LoadBaseLocked(_dbDir);
            if (BaseFiles.Any(f => File.Exists(Path.Combine(_dbDir, f))))
            {
                // 迁移到分代布局（写出 gen-1 并删除旧文件）
                CompactLocked();
            }
        }

        private long LoadBaseLocked(string dir)
        {
            long bytes = 0;
            bytes += LoadLinesLocked(Path.Combine(dir, WorkspacesFile), line =>
                ApplyWorkspaceLocked(JsonConvert.DeserializeObject<IndexWorkspace>(line)));
            bytes += LoadLinesLocked(Path.Combine(dir, RootsFile), line =>
                ApplyRootLocked(JsonConvert.DeserializeObject<IndexRoot>(line)));
            bytes += LoadLinesLocked(Path.Combine(dir, FilesFile), line =>
                ApplyFileLocked(JsonConvert.DeserializeObject<IndexedFile>(line)));
            bytes += LoadLinesLocked(Path.Combine(dir, SymbolsFile), line =>
            {
                var sym = JsonConvert.DeserializeObject<SymbolInfo>(line);
                if (sym == null) throw new InvalidDataException("null symbol");
                ApplySymbolLocked(sym);
            });
            bytes += LoadLinesLocked(Path.Combine(dir, MetadataFile), line =>
            {
                var entry = JsonConvert.DeserializeObject<MetadataEntry>(line);
                if (entry == null || entry.Key == null) throw new InvalidDataException("bad metadata entry");
                ApplyMetaLocked(entry.WorkspaceId, entry.Key, entry.Value);
            });
            return bytes;
        }

        /// <summary>逐行读取并应用，解析失败的行计入损坏行数。返回文件字节数。</summary>
        private long LoadLinesLocked(string path, Action<string> apply)
        {
            if (!File.Exists(path)) return 0;

            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    apply(line);
                }
                catch
                {
                    _corruptLines++;
                }
            }

            try { return new FileInfo(path).Length; }
            catch { return 0; }
        }

        private void ReplayOpLogLocked(string path)
        {
            if (!File.Exists(path)) return;

            _opLogBytes = LoadLinesLocked(path, line =>
            {
                var obj = JObject.Parse(line);
                ApplyOpLocked((string)obj["op"], obj["d"]);
                _opCount++;
            });

            // 最后一行写了一半时文件不以换行结尾，下次追加前先补换行，避免把新操作粘到残行上
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fs.Length > 0)
                    {
                        fs.Seek(-1, SeekOrigin.End);
                        _opLogNeedsNewline = fs.ReadByte() != '\n';
                    }
                }
            }
            catch
            {
                _opLogNeedsNewline = true;
            }
        }

        private void ApplyOpLocked(string op, JToken d)
        {
            if (d == null) throw new InvalidDataException("missing payload");
            switch (op)
            {
                case OpWorkspace:
                    ApplyWorkspaceLocked(d.ToObject<IndexWorkspace>());
                    break;
                case OpRoot:
                    ApplyRootLocked(d.ToObject<IndexRoot>());
                    break;
                case OpFile:
                    ApplyFileLocked(d.ToObject<IndexedFile>());
                    break;
                case OpDeleteFile:
                    ApplyDeleteFileLocked((int)d["id"]);
                    break;
                case OpSymbols:
                    foreach (var sym in d.ToObject<List<SymbolInfo>>())
                        ApplySymbolLocked(sym);
                    break;
                case OpDeleteSymbols:
                    ApplyDeleteSymbolsLocked((int)d["fileId"]);
                    break;
                case OpClearWorkspace:
                    ApplyClearWorkspaceLocked((int)d["wid"]);
                    break;
                case OpClearRoot:
                    ApplyClearRootLocked((int)d["wid"], (int)d["rootId"]);
                    break;
                case OpMeta:
                    ApplyMetaLocked((string)d["wid"], (string)d["k"], (string)d["v"]);
                    break;
                default:
                    throw new InvalidDataException($"unknown op '{op}'");
            }
        }

        /// <summary>
        /// 损坏行超过阈值时要求重建：清除 last_full_index_at，使增量入口自动降级为全量重建，
        /// 搜索结果附带健康警告，而不是把残缺数据当作完整结果。
        /// </summary>
        private void EvaluateHealthLocked()
        {
            if (_corruptLines <= CorruptLineTolerance) return;

            _requiresRebuild = true;
            if (string.IsNullOrEmpty(_healthMessage))
                _healthMessage = $"索引文件中有 {_corruptLines} 行损坏（容忍 {CorruptLineTolerance} 行），数据可能不完整，需要全量重建。";

            AgentCore.Editor.Utils.AgentCoreLog.Warning($"[JsonlIndexStore] {_healthMessage} ({_dbDir})");

            try
            {
                foreach (var wid in _metadata.Keys.ToList())
                {
                    if (_metadata[wid].ContainsKey(CodebaseIndexer.MetaKeyLastFullIndex))
                        WriteOpLocked(OpMeta, new { wid, k = CodebaseIndexer.MetaKeyLastFullIndex, v = (string)null },
                            () => ApplyMetaLocked(wid, CodebaseIndexer.MetaKeyLastFullIndex, null));
                }
            }
            catch (Exception ex)
            {
                AgentCore.Editor.Utils.AgentCoreLog.Warning($"[JsonlIndexStore] Failed to mark index dirty: {ex.Message}");
            }
        }

        /// <inheritdoc/>
        public Task AcknowledgeRebuildAsync(CancellationToken ct = default)
        {
            EnsureLoaded();
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                _corruptLines = 0;
                _requiresRebuild = false;
                _healthMessage = null;
                CompactLocked();
            }
            return Task.CompletedTask;
        }

        // ── Workspace ──────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public Task<int> UpsertWorkspaceAsync(IndexWorkspace workspace, CancellationToken ct = default)
        {
            EnsureLoaded();
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                var existing = _workspaces.Values.FirstOrDefault(w => w.Fingerprint == workspace.Fingerprint);
                if (existing != null)
                {
                    existing.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    existing.Revision = workspace.Revision;
                    existing.BranchId = workspace.BranchId;
                    WriteOpLocked(OpWorkspace, existing, () => ApplyWorkspaceLocked(existing));
                    return Task.FromResult(existing.Id);
                }

                workspace.Id = _maxWorkspaceId + 1;
                WriteOpLocked(OpWorkspace, workspace, () => ApplyWorkspaceLocked(workspace));
                return Task.FromResult(workspace.Id);
            }
        }

        /// <inheritdoc/>
        public Task<IndexWorkspace> GetWorkspaceByFingerprintAsync(string fingerprint, CancellationToken ct = default)
        {
            EnsureLoaded();
            lock (_gate)
            {
                return Task.FromResult(_workspaces.Values.FirstOrDefault(w => w.Fingerprint == fingerprint));
            }
        }

        // ── Roots ──────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public Task<int> UpsertRootAsync(int workspaceId, IndexRoot root, CancellationToken ct = default)
        {
            EnsureLoaded();
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                root.WorkspaceId = workspaceId;
                var existing = _roots.Values.FirstOrDefault(r =>
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
                    WriteOpLocked(OpRoot, existing, () => ApplyRootLocked(existing));
                    return Task.FromResult(existing.Id);
                }

                root.Id = _maxRootId + 1;
                WriteOpLocked(OpRoot, root, () => ApplyRootLocked(root));
                return Task.FromResult(root.Id);
            }
        }

        /// <inheritdoc/>
        public Task<IReadOnlyList<IndexRoot>> GetRootsAsync(int workspaceId, CancellationToken ct = default)
        {
            EnsureLoaded();
            lock (_gate)
            {
                IReadOnlyList<IndexRoot> result = _roots.Values.Where(r => r.WorkspaceId == workspaceId).OrderBy(r => r.Id).ToList();
                return Task.FromResult(result);
            }
        }

        // ── Files ──────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public Task<int> UpsertFileAsync(int workspaceId, int rootId, IndexedFile file, CancellationToken ct = default)
        {
            EnsureLoaded();
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                file.WorkspaceId = workspaceId;
                file.RootId = rootId;

                if (_fileIdByKey.TryGetValue(FileKey(workspaceId, file.FilePath), out var existingId)
                    && _files.TryGetValue(existingId, out var existing))
                {
                    existing.RootId = rootId;
                    existing.ContentHash = file.ContentHash;
                    existing.LastModified = file.LastModified;
                    existing.LastIndexed = file.LastIndexed;
                    existing.FileSize = file.FileSize;
                    existing.HasErrors = file.HasErrors;
                    existing.ErrorMessage = file.ErrorMessage;
                    existing.SymbolCount = file.SymbolCount;
                    existing.RelativeToRoot = file.RelativeToRoot;
                    WriteOpLocked(OpFile, existing, () => ApplyFileLocked(existing));
                    return Task.FromResult(existing.Id);
                }

                file.Id = _maxFileId + 1;
                WriteOpLocked(OpFile, file, () => ApplyFileLocked(file));
                return Task.FromResult(file.Id);
            }
        }

        /// <inheritdoc/>
        public Task DeleteFileAsync(int fileId, CancellationToken ct = default)
        {
            EnsureLoaded();
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                if (_files.ContainsKey(fileId) || _symbolsByFile.ContainsKey(fileId))
                    WriteOpLocked(OpDeleteFile, new { id = fileId }, () => ApplyDeleteFileLocked(fileId));
            }
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task<IReadOnlyList<IndexedFile>> GetFilesForRootAsync(int rootId, CancellationToken ct = default)
        {
            EnsureLoaded();
            lock (_gate)
            {
                IReadOnlyList<IndexedFile> result = _files.Values.Where(f => f.RootId == rootId).ToList();
                return Task.FromResult(result);
            }
        }

        /// <inheritdoc/>
        public Task<IndexedFile> GetFileByPathAsync(int workspaceId, string filePath, CancellationToken ct = default)
        {
            EnsureLoaded();
            lock (_gate)
            {
                IndexedFile file = null;
                if (_fileIdByKey.TryGetValue(FileKey(workspaceId, filePath), out var id))
                    _files.TryGetValue(id, out file);
                return Task.FromResult(file);
            }
        }

        /// <inheritdoc/>
        public Task<int> ClearRootIndexAsync(int workspaceId, int rootId, CancellationToken ct = default)
        {
            EnsureLoaded();
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                var count = _files.Values.Count(f => f.WorkspaceId == workspaceId && f.RootId == rootId);
                WriteOpLocked(OpClearRoot, new { wid = workspaceId, rootId }, () => ApplyClearRootLocked(workspaceId, rootId));
                return Task.FromResult(count);
            }
        }

        // ── Symbols ────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public Task BulkInsertSymbolsAsync(IEnumerable<SymbolInfo> symbols, CancellationToken ct = default)
        {
            EnsureLoaded();
            var list = symbols?.Where(s => s != null).ToList() ?? new List<SymbolInfo>();
            if (list.Count == 0) return Task.CompletedTask;

            lock (_gate)
            {
                ThrowIfDisposedLocked();
                var nextId = _maxSymbolId;
                foreach (var sym in list)
                    sym.Id = ++nextId;

                WriteOpLocked(OpSymbols, list, () =>
                {
                    foreach (var sym in list)
                        ApplySymbolLocked(sym);
                });
            }
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task DeleteSymbolsByFileAsync(int fileId, CancellationToken ct = default)
        {
            EnsureLoaded();
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                if (_symbolsByFile.ContainsKey(fileId))
                    WriteOpLocked(OpDeleteSymbols, new { fileId }, () => ApplyDeleteSymbolsLocked(fileId));
            }
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<SymbolInfo>> SearchSymbolsAsync(int workspaceId, SearchQuery query, CancellationToken ct = default)
        {
            var page = await SearchSymbolsPageAsync(workspaceId, query, ct);
            return page.Matches;
        }

        /// <inheritdoc/>
        public Task<SymbolSearchPage> SearchSymbolsPageAsync(int workspaceId, SearchQuery query, CancellationToken ct = default)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            EnsureLoaded();

            // 名称匹配器先构造：正则无效 / 过长直接抛 ArgumentException 给调用方（不再静默降级为模糊匹配）
            var nameMatch = BuildNameMatcher(query);

            HashSet<int> enabledRootIds;
            List<SymbolInfo> candidates;
            lock (_gate)
            {
                enabledRootIds = new HashSet<int>(
                    _roots.Values.Where(r => r.WorkspaceId == workspaceId && r.IsEnabled).Select(r => r.Id));
                candidates = SnapshotSymbolsLocked(workspaceId);
            }

            int limit = Math.Min(Math.Max(1, query.Limit), 200);
            var page = ScanSymbols(candidates, s => PassesFilters(s, query, enabledRootIds) && (nameMatch == null || nameMatch(s)),
                limit, ordered: true, ct);
            return Task.FromResult(page);
        }

        /// <inheritdoc/>
        public Task<IReadOnlyList<SymbolInfo>> GetSymbolsByFileAsync(int fileId, CancellationToken ct = default)
        {
            EnsureLoaded();
            lock (_gate)
            {
                IReadOnlyList<SymbolInfo> result = _symbolsByFile.TryGetValue(fileId, out var list)
                    ? list.ToList()
                    : new List<SymbolInfo>();
                return Task.FromResult(result);
            }
        }

        /// <inheritdoc/>
        public Task<IReadOnlyList<NamespaceEntry>> GetNamespacesAsync(int workspaceId, IndexScopeType? scopeType = null, CancellationToken ct = default)
        {
            EnsureLoaded();
            List<SymbolInfo> symbols;
            lock (_gate)
            {
                symbols = SnapshotSymbolsLocked(workspaceId);
            }

            var query = symbols.Where(s => !string.IsNullOrEmpty(s.Namespace));
            if (scopeType.HasValue)
                query = query.Where(s => s.ScopeType == scopeType.Value);

            IReadOnlyList<NamespaceEntry> result = query
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
            return Task.FromResult(result);
        }

        // ── Stats ──────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public Task<IndexingStats> GetStatsAsync(int workspaceId, CancellationToken ct = default)
        {
            EnsureLoaded();
            lock (_gate)
            {
                _workspaces.TryGetValue(workspaceId, out var ws);
                var roots = _roots.Values.Where(r => r.WorkspaceId == workspaceId && r.IsEnabled).OrderBy(r => r.Id).ToList();
                var files = _files.Values.Where(f => f.WorkspaceId == workspaceId).ToList();

                var filesByRoot = new Dictionary<int, int>();
                foreach (var f in files)
                    filesByRoot[f.RootId] = filesByRoot.TryGetValue(f.RootId, out var c) ? c + 1 : 1;

                var symbolsByRoot = new Dictionary<int, int>();
                var totalSymbols = 0;
                foreach (var list in _symbolsByFile.Values)
                {
                    foreach (var s in list)
                    {
                        if (s.WorkspaceId != workspaceId) continue;
                        totalSymbols++;
                        symbolsByRoot[s.RootId] = symbolsByRoot.TryGetValue(s.RootId, out var c) ? c + 1 : 1;
                    }
                }

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
                    TotalSymbols = totalSymbols,
                    ErrorFileCount = files.Count(f => f.HasErrors)
                };

                // 解析时间戳
                var fullAt = GetMetadataLocked(workspaceId, "last_full_index_at");
                if (fullAt != null && long.TryParse(fullAt, out var fullTs))
                    stats.LastFullIndexAt = DateTimeOffset.FromUnixTimeSeconds(fullTs).UtcDateTime;

                var incrAt = GetMetadataLocked(workspaceId, "last_incremental_at");
                if (incrAt != null && long.TryParse(incrAt, out var incrTs))
                    stats.LastIncrementalIndexAt = DateTimeOffset.FromUnixTimeSeconds(incrTs).UtcDateTime;

                var durationStr = GetMetadataLocked(workspaceId, "last_full_index_duration_seconds");
                if (durationStr != null && double.TryParse(durationStr, out var dur))
                    stats.LastFullIndexDurationSeconds = dur;

                // Root 分组统计
                foreach (var root in roots)
                {
                    stats.RootBreakdown.Add(new IndexingStats.RootStats
                    {
                        RootId = root.Id,
                        DisplayName = root.DisplayName,
                        ScopeType = root.ScopeType,
                        ScopeName = root.ScopeName,
                        ReadOnly = root.ReadOnly,
                        FileCount = filesByRoot.TryGetValue(root.Id, out var fc) ? fc : 0,
                        SymbolCount = symbolsByRoot.TryGetValue(root.Id, out var sc) ? sc : 0
                    });
                }

                return Task.FromResult(stats);
            }
        }

        /// <inheritdoc/>
        public Task ClearWorkspaceIndexAsync(int workspaceId, CancellationToken ct = default)
        {
            EnsureLoaded();
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                WriteOpLocked(OpClearWorkspace, new { wid = workspaceId }, () => ApplyClearWorkspaceLocked(workspaceId));
            }
            return Task.CompletedTask;
        }

        // ── Metadata ───────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public Task SetMetadataAsync(int workspaceId, string key, string value, CancellationToken ct = default)
        {
            EnsureLoaded();
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                var wid = workspaceId.ToString(CultureInfo.InvariantCulture);
                if (GetMetadataLocked(workspaceId, key) == value)
                    return Task.CompletedTask;
                WriteOpLocked(OpMeta, new { wid, k = key, v = value }, () => ApplyMetaLocked(wid, key, value));
            }
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task<string> GetMetadataAsync(int workspaceId, string key, CancellationToken ct = default)
        {
            EnsureLoaded();
            lock (_gate)
            {
                return Task.FromResult(GetMetadataLocked(workspaceId, key));
            }
        }

        // ── Dependencies（JSONL 后端不持久化依赖图） ───────────────────────────

        /// <summary>依赖图查询不支持时的说明（返回给模型）。</summary>
        public const string DependenciesUnsupportedMessage =
            "unsupported: 当前索引后端为 JSONL，不存储依赖图，无法回答 get_dependencies / find_usages。" +
            "这不代表“没有依赖/引用”。可改用 manage_file search_content 做文本搜索，或启用 SQLite 后端（AGENTCORE_SQLITE）。";

        /// <inheritdoc/>
        public Task BulkInsertDependenciesAsync(IEnumerable<SymbolDependency> deps, CancellationToken ct = default)
        {
            // 写入侧无需报错：索引流程照常进行，查询侧会明确返回 unsupported。
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task DeleteDependenciesByFileAsync(int fileId, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task<IReadOnlyList<SymbolDependency>> GetDependenciesAsync(
            int workspaceId, int fileId, int? symbolId = null, CancellationToken ct = default)
        {
            throw new NotSupportedException(DependenciesUnsupportedMessage);
        }

        /// <inheritdoc/>
        public Task<IReadOnlyList<SymbolDependency>> FindUsagesAsync(
            int workspaceId, string typeName, CancellationToken ct = default)
        {
            throw new NotSupportedException(DependenciesUnsupportedMessage);
        }

        // ── Full-Text Search（JSONL 降级：包含匹配） ───────────────────────────

        /// <inheritdoc/>
        public async Task<IReadOnlyList<SymbolInfo>> SearchSymbolsByTextAsync(
            int workspaceId, string text, int maxResults = 50, CancellationToken ct = default)
        {
            var page = await SearchSymbolsByTextPageAsync(workspaceId, text, maxResults, ct);
            return page.Matches;
        }

        /// <inheritdoc/>
        public Task<SymbolSearchPage> SearchSymbolsByTextPageAsync(
            int workspaceId, string text, int maxResults, CancellationToken ct = default)
        {
            EnsureLoaded();
            if (string.IsNullOrWhiteSpace(text))
                return Task.FromResult(new SymbolSearchPage());

            List<SymbolInfo> candidates;
            lock (_gate)
            {
                candidates = SnapshotSymbolsLocked(workspaceId);
            }

            var page = ScanSymbols(candidates, s =>
                    Contains(s.Name, text) || Contains(s.FullName, text) ||
                    Contains(s.Namespace, text) || Contains(s.Signature, text),
                Math.Max(1, maxResults), ordered: false, ct);
            return Task.FromResult(page);
        }

        // ── IDisposable ────────────────────────────────────────────────────────

        /// <summary>
        /// 把操作日志刷到磁盘。
        /// </summary>
        public void Flush()
        {
            lock (_gate)
            {
                try
                {
                    _opLog?.Flush(true);
                }
                catch (Exception ex)
                {
                    AgentCore.Editor.Utils.AgentCoreLog.Warning($"[JsonlIndexStore] Flush failed: {ex.Message}");
                }
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                CloseOpLogLocked();
            }
        }

        // ── 写入：操作日志 + 整理 ───────────────────────────────────────────────

        /// <summary>
        /// 先追加操作日志（WAL），再修改内存；必要时整理。必须持有 <see cref="_gate"/>。
        /// </summary>
        private void WriteOpLocked(string op, object payload, Action apply)
        {
            var line = "{\"op\":\"" + op + "\",\"d\":" + JsonConvert.SerializeObject(payload, Formatting.None) + "}\n";
            AppendOpLineLocked(line);
            apply();
            MaybeCompactLocked();
        }

        private void AppendOpLineLocked(string line)
        {
            EnsureOpLogOpenLocked();
            var bytes = Utf8NoBom.GetBytes(line);
            _opLog.Write(bytes, 0, bytes.Length);
            _opLog.Flush();
            _opLogBytes += bytes.Length;
            _opCount++;
        }

        private void EnsureOpLogOpenLocked()
        {
            if (_opLog != null) return;

            if (_generation < 1)
            {
                // 尚无分代目录（新索引或 manifest 损坏）：先落一份基线
                CompactLocked();
            }

            var path = Path.Combine(GetGenerationDir(_generation), OpLogFile);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            _opLog = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            if (_opLogNeedsNewline)
            {
                _opLog.WriteByte((byte)'\n');
                _opLogBytes++;
                _opLogNeedsNewline = false;
            }
        }

        private void CloseOpLogLocked()
        {
            if (_opLog == null) return;
            try
            {
                _opLog.Flush(true);
                _opLog.Dispose();
            }
            catch (Exception ex)
            {
                AgentCore.Editor.Utils.AgentCoreLog.Warning($"[JsonlIndexStore] Closing op log failed: {ex.Message}");
            }
            _opLog = null;
        }

        private void MaybeCompactLocked()
        {
            if (_opLogBytes >= Math.Max(_compactMinBytes, _baseBytes))
                CompactLocked();
        }

        /// <summary>
        /// 立即整理：把当前内存状态写成新一代基线并清空操作日志。
        /// </summary>
        public void Compact()
        {
            EnsureLoaded();
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                CompactLocked();
            }
        }

        private void CompactLocked()
        {
            var oldGeneration = _generation;
            var newGeneration = Math.Max(oldGeneration, FindMaxGenerationOnDisk()) + 1;
            var newDir = GetGenerationDir(newGeneration);

            // 上次整理中途崩溃留下的残目录：manifest 尚未指向它，可直接覆盖
            if (Directory.Exists(newDir))
                Directory.Delete(newDir, recursive: true);
            Directory.CreateDirectory(newDir);

            long bytes = 0;
            bytes += WriteBaseFile(newDir, WorkspacesFile, _workspaces.Values.OrderBy(w => w.Id));
            bytes += WriteBaseFile(newDir, RootsFile, _roots.Values.OrderBy(r => r.Id));
            bytes += WriteBaseFile(newDir, FilesFile, _files.Values.OrderBy(f => f.Id));
            bytes += WriteBaseFile(newDir, SymbolsFile, _symbolsByFile.Values.SelectMany(l => l));
            bytes += WriteBaseFile(newDir, MetadataFile, _metadata.SelectMany(kvp => kvp.Value.Select(e => new MetadataEntry
            {
                WorkspaceId = kvp.Key,
                Key = e.Key,
                Value = e.Value
            })));

            CloseOpLogLocked();

            // 提交点：manifest 原子替换后新一代才生效
            AgentCore.Editor.Utils.AtomicFile.WriteAllText(
                Path.Combine(_dbDir, ManifestFile),
                JsonConvert.SerializeObject(new Manifest { Format = FormatVersion, Generation = newGeneration }),
                Utf8NoBom);

            _generation = newGeneration;
            _baseBytes = bytes;
            _opLogBytes = 0;
            _opCount = 0;
            _opLogNeedsNewline = false;

            // 清理旧代 / 旧版文件（失败不影响正确性，下次整理会再试）
            try
            {
                if (oldGeneration >= 1)
                {
                    var oldDir = GetGenerationDir(oldGeneration);
                    if (Directory.Exists(oldDir))
                        Directory.Delete(oldDir, recursive: true);
                }
                else
                {
                    foreach (var f in BaseFiles)
                    {
                        var legacy = Path.Combine(_dbDir, f);
                        if (File.Exists(legacy))
                            File.Delete(legacy);
                    }
                }
            }
            catch (Exception ex)
            {
                AgentCore.Editor.Utils.AgentCoreLog.Warning($"[JsonlIndexStore] Cleaning previous generation failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 写基线文件。所在代目录在 manifest 指向前不会被读取，因此直接流式写入并落盘即可，
        /// 不必整份拼成字符串。
        /// </summary>
        private static long WriteBaseFile<T>(string dir, string fileName, IEnumerable<T> items)
        {
            var path = Path.Combine(dir, fileName);
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
            {
                using (var writer = new StreamWriter(fs, Utf8NoBom, 64 * 1024))
                {
                    writer.NewLine = "\n";
                    foreach (var item in items)
                        writer.WriteLine(JsonConvert.SerializeObject(item, Formatting.None));
                    writer.Flush();
                    fs.Flush(true);
                    return fs.Length;
                }
            }
        }

        private int FindMaxGenerationOnDisk()
        {
            var max = 0;
            try
            {
                if (!Directory.Exists(_dbDir)) return 0;
                foreach (var dir in Directory.GetDirectories(_dbDir, GenerationDirPrefix + "*"))
                {
                    var name = Path.GetFileName(dir);
                    if (int.TryParse(name.Substring(GenerationDirPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                        max = Math.Max(max, n);
                }
            }
            catch
            {
                // 忽略
            }
            return max;
        }

        private string GetGenerationDir(int generation)
        {
            return Path.Combine(_dbDir, GenerationDirPrefix + generation.ToString(CultureInfo.InvariantCulture));
        }

        // ── 内存结构变更（加载 / 重放 / 实时写共用） ────────────────────────────

        private void ApplyWorkspaceLocked(IndexWorkspace workspace)
        {
            if (workspace == null) throw new InvalidDataException("null workspace");
            _workspaces[workspace.Id] = workspace;
            _maxWorkspaceId = Math.Max(_maxWorkspaceId, workspace.Id);
        }

        private void ApplyRootLocked(IndexRoot root)
        {
            if (root == null) throw new InvalidDataException("null root");
            _roots[root.Id] = root;
            _maxRootId = Math.Max(_maxRootId, root.Id);
        }

        private void ApplyFileLocked(IndexedFile file)
        {
            if (file == null) throw new InvalidDataException("null file");
            if (_files.TryGetValue(file.Id, out var previous) && !ReferenceEquals(previous, file))
                _fileIdByKey.Remove(FileKey(previous.WorkspaceId, previous.FilePath));
            _files[file.Id] = file;
            _fileIdByKey[FileKey(file.WorkspaceId, file.FilePath)] = file.Id;
            _maxFileId = Math.Max(_maxFileId, file.Id);
        }

        private void ApplyDeleteFileLocked(int fileId)
        {
            if (_files.TryGetValue(fileId, out var file))
            {
                _files.Remove(fileId);
                var key = FileKey(file.WorkspaceId, file.FilePath);
                if (_fileIdByKey.TryGetValue(key, out var mapped) && mapped == fileId)
                    _fileIdByKey.Remove(key);
            }
            ApplyDeleteSymbolsLocked(fileId);
        }

        private void ApplySymbolLocked(SymbolInfo symbol)
        {
            if (!_symbolsByFile.TryGetValue(symbol.FileId, out var list))
            {
                list = new List<SymbolInfo>();
                _symbolsByFile[symbol.FileId] = list;
            }
            list.Add(symbol);
            _symbolCount++;
            _maxSymbolId = Math.Max(_maxSymbolId, symbol.Id);
        }

        private void ApplyDeleteSymbolsLocked(int fileId)
        {
            if (_symbolsByFile.TryGetValue(fileId, out var list))
            {
                _symbolCount -= list.Count;
                _symbolsByFile.Remove(fileId);
            }
        }

        private void ApplyClearWorkspaceLocked(int workspaceId)
        {
            foreach (var id in _files.Values.Where(f => f.WorkspaceId == workspaceId).Select(f => f.Id).ToList())
                ApplyDeleteFileLocked(id);
            RemoveSymbolsWhereLocked(s => s.WorkspaceId == workspaceId);
        }

        private void ApplyClearRootLocked(int workspaceId, int rootId)
        {
            foreach (var id in _files.Values.Where(f => f.WorkspaceId == workspaceId && f.RootId == rootId).Select(f => f.Id).ToList())
                ApplyDeleteFileLocked(id);
            RemoveSymbolsWhereLocked(s => s.WorkspaceId == workspaceId && s.RootId == rootId);
        }

        private void RemoveSymbolsWhereLocked(Func<SymbolInfo, bool> predicate)
        {
            foreach (var fileId in _symbolsByFile.Keys.ToList())
            {
                var list = _symbolsByFile[fileId];
                var removed = list.RemoveAll(s => predicate(s));
                _symbolCount -= removed;
                if (list.Count == 0)
                    _symbolsByFile.Remove(fileId);
            }
        }

        private void ApplyMetaLocked(string workspaceId, string key, string value)
        {
            if (key == null) throw new InvalidDataException("null metadata key");
            workspaceId ??= string.Empty;
            if (value == null)
            {
                if (_metadata.TryGetValue(workspaceId, out var existing))
                    existing.Remove(key);
                return;
            }

            if (!_metadata.TryGetValue(workspaceId, out var dict))
            {
                dict = new Dictionary<string, string>();
                _metadata[workspaceId] = dict;
            }
            dict[key] = value;
        }

        // ── 查询辅助 ───────────────────────────────────────────────────────────

        private List<SymbolInfo> SnapshotSymbolsLocked(int workspaceId)
        {
            var result = new List<SymbolInfo>(_symbolCount);
            foreach (var list in _symbolsByFile.Values)
            {
                foreach (var s in list)
                {
                    if (s.WorkspaceId == workspaceId)
                        result.Add(s);
                }
            }
            return result;
        }

        /// <summary>
        /// 在锁外扫描候选符号：逐条检查取消，超过整体时间预算时停止并标记 timeout。
        /// 正则超时（<see cref="RegexMatchTimeoutException"/>）直接向上抛出。
        /// </summary>
        private static SymbolSearchPage ScanSymbols(
            List<SymbolInfo> candidates, Func<SymbolInfo, bool> predicate, int limit, bool ordered, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var matched = new List<SymbolInfo>();
            var timedOut = false;

            for (var i = 0; i < candidates.Count; i++)
            {
                if ((i & 255) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    if (sw.ElapsedMilliseconds > SearchTimeBudgetMs)
                    {
                        timedOut = true;
                        break;
                    }
                }

                var s = candidates[i];
                if (predicate(s))
                    matched.Add(s);
            }

            IEnumerable<SymbolInfo> result = matched;
            if (ordered)
            {
                // 排序：当前 Scope > Shared > Project > Tools > Engine > Plugin
                result = matched.OrderBy(s => GetScopePriority(s.ScopeType)).ThenBy(s => s.Name, StringComparer.Ordinal);
            }

            var page = new SymbolSearchPage
            {
                Matches = result.Take(limit).ToList(),
                TotalMatches = matched.Count,
                TotalIsLowerBound = timedOut,
            };

            if (timedOut)
            {
                page.Truncated = true;
                page.TruncatedReason = SearchTruncation.Timeout;
            }
            else if (matched.Count > limit)
            {
                page.Truncated = true;
                page.TruncatedReason = SearchTruncation.MaxResults;
            }

            return page;
        }

        private static Func<SymbolInfo, bool> BuildNameMatcher(SearchQuery query)
        {
            if (string.IsNullOrEmpty(query.Query))
                return null;

            var text = query.Query;
            if (query.Regex)
            {
                var rx = SafeRegex.Create(text, ignoreCase: true);
                return s => rx.IsMatch(s.Name ?? string.Empty) || rx.IsMatch(s.FullName ?? string.Empty);
            }

            if (query.Fuzzy)
                return s => Contains(s.Name, text) || Contains(s.FullName, text);

            return s => string.Equals(s.Name, text, StringComparison.OrdinalIgnoreCase);
        }

        private static bool PassesFilters(SymbolInfo s, SearchQuery query, HashSet<int> enabledRootIds)
        {
            if (!enabledRootIds.Contains(s.RootId)) return false;

            // Scope 过滤
            if (query.ScopeType.HasValue && s.ScopeType != query.ScopeType.Value) return false;
            if (!string.IsNullOrEmpty(query.ScopeName) &&
                !string.Equals(s.ScopeName, query.ScopeName, StringComparison.OrdinalIgnoreCase)) return false;

            // Role / 只读过滤
            if (query.Role.HasValue && s.Role != query.Role.Value) return false;
            if (query.ReadOnly.HasValue && s.ReadOnly != query.ReadOnly.Value) return false;

            // Plugin / Engine / Generated 过滤
            if (!query.IncludePlugins && s.ScopeType == IndexScopeType.Plugin) return false;
            if (!query.IncludeEngine && s.ScopeType == IndexScopeType.Engine) return false;
            if (!query.IncludeGenerated && s.ScopeType == IndexScopeType.Generated) return false;

            // 符号类型 / 命名空间 / Root ID 过滤
            if (!string.IsNullOrEmpty(query.SymbolType) &&
                !string.Equals(s.SymbolType, query.SymbolType, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(query.Namespace) &&
                (s.Namespace == null || !s.Namespace.StartsWith(query.Namespace, StringComparison.OrdinalIgnoreCase))) return false;
            if (query.RootId > 0 && s.RootId != query.RootId) return false;

            return true;
        }

        private static bool Contains(string haystack, string needle)
        {
            return haystack != null && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private string GetMetadataLocked(int workspaceId, string key)
        {
            if (!_metadata.TryGetValue(workspaceId.ToString(CultureInfo.InvariantCulture), out var dict)) return null;
            dict.TryGetValue(key, out var value);
            return value;
        }

        private void ThrowIfDisposedLocked()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(JsonlIndexStore), "索引存储已关闭（编辑器正在重载或退出）。");
        }

        private static string FileKey(int workspaceId, string path)
        {
            return workspaceId.ToString(CultureInfo.InvariantCulture) + "|" + NormalizePath(path);
        }

        private static string NormalizePath(string path)
        {
            return path?.Replace('\\', '/') ?? string.Empty;
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

        private sealed class Manifest
        {
            [JsonProperty("format")] public int Format { get; set; }
            [JsonProperty("generation")] public int Generation { get; set; }
        }

        private sealed class MetadataEntry
        {
            [JsonProperty("wid")] public string WorkspaceId { get; set; }
            [JsonProperty("k")] public string Key { get; set; }
            [JsonProperty("v")] public string Value { get; set; }
        }
    }
}
