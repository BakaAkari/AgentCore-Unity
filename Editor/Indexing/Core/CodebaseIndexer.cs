using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Components.Indexing.Models;
using AgentCore.Editor.Components.Indexing.Roots;

namespace AgentCore.Editor.Components.Indexing.Core
{
    /// <summary>
    /// 代码库索引引擎，负责编排全量索引和增量索引流程。
    ///
    /// 全量索引：清空当前 workspace 的所有文件/符号记录，重新扫描所有 Root 并提取符号。
    /// 增量索引：对比文件的 ContentHash 和 LastModified，仅重新索引发生变化的文件，
    ///           同时删除已不存在的文件记录。
    ///
    /// 线程模型：
    ///   - 文件扫描和 Roslyn 解析在后台线程执行（非主线程）。
    ///   - IIndexStore 写操作通过 await 串行化，不需要额外锁。
    ///   - 进度回调在调用线程上同步触发（调用方负责线程安全）。
    /// </summary>
    public sealed class CodebaseIndexer
    {
        // ── 常量 ────────────────────────────────────────────────────────────────

        /// <summary>默认单文件最大大小（字节）：1 MB。超过此大小的文件将被跳过。</summary>
        public const long DefaultMaxFileSizeBytes = 1 * 1024 * 1024;

        /// <summary>元数据键：最后一次全量索引时间（UTC ISO 8601）。</summary>
        public const string MetaKeyLastFullIndex = "last_full_index_at";

        /// <summary>元数据键：最后一次增量索引时间（UTC ISO 8601）。</summary>
        public const string MetaKeyLastIncrementalIndex = "last_incremental_at";

        /// <summary>元数据键：最后一次索引的版本号（用于强制重建）。</summary>
        public const string MetaKeyIndexVersion = "index_version";

        /// <summary>当前索引格式版本号。版本变更时触发强制全量重建。</summary>
        public const string CurrentIndexVersion = "1";

        // ── 字段 ────────────────────────────────────────────────────────────────

        private readonly IIndexStore _store;
        private readonly IndexRootResolver _rootResolver;
        private readonly long _maxFileSizeBytes;

        // ── 构造函数 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 使用指定存储后端和默认 Root 解析器创建索引器。
        /// </summary>
        /// <param name="store">索引存储后端（调用方负责生命周期管理）。</param>
        /// <param name="maxFileSizeBytes">单文件最大大小限制（字节），超过则跳过。</param>
        public CodebaseIndexer(IIndexStore store, long maxFileSizeBytes = DefaultMaxFileSizeBytes)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _rootResolver = new IndexRootResolver();
            _maxFileSizeBytes = maxFileSizeBytes;
        }

        /// <summary>
        /// 使用指定存储后端和自定义 Root 解析器创建索引器。
        /// </summary>
        /// <param name="store">索引存储后端。</param>
        /// <param name="rootResolver">自定义 Root 解析器（用于测试或特殊场景）。</param>
        /// <param name="maxFileSizeBytes">单文件最大大小限制（字节）。</param>
        public CodebaseIndexer(
            IIndexStore store,
            IndexRootResolver rootResolver,
            long maxFileSizeBytes = DefaultMaxFileSizeBytes)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _rootResolver = rootResolver ?? throw new ArgumentNullException(nameof(rootResolver));
            _maxFileSizeBytes = maxFileSizeBytes;
        }

        // ── 公共 API ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 执行全量索引。
        /// 清空当前 workspace 的所有文件/符号记录，重新扫描所有 Root 并提取符号。
        /// </summary>
        /// <param name="onProgress">进度回调（可为 null）。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>最终进度快照。</returns>
        public async Task<IndexingProgress> RunFullIndexAsync(
            Action<IndexingProgress> onProgress = null,
            CancellationToken ct = default)
        {
            var progress = IndexingProgress.CreateStarted(IndexingPhase.Initializing);
            ReportProgress(onProgress, progress);

            try
            {
                // 1. 解析 workspace
                ct.ThrowIfCancellationRequested();
                var workspace = IndexWorkspaceResolver.ResolveFromCurrent();
                var workspaceId = await _store.UpsertWorkspaceAsync(workspace, ct);
                workspace = new IndexWorkspace
                {
                    Id = workspaceId,
                    Fingerprint = workspace.Fingerprint,
                    WorkspaceRoot = workspace.WorkspaceRoot,
                    UnityRoot = workspace.UnityRoot,
                    UnityRootRelativePath = workspace.UnityRootRelativePath,
                    DisplayName = workspace.DisplayName,
                    VcsType = workspace.VcsType,
                    VcsRootPath = workspace.VcsRootPath,
                    VcsUrl = workspace.VcsUrl,
                    RepositoryRoot = workspace.RepositoryRoot,
                    BranchId = workspace.BranchId,
                    Revision = workspace.Revision,
                };

                // 2. 检查是否需要强制全量重建（版本号变更）
                var storedVersion = await _store.GetMetadataAsync(workspaceId, MetaKeyIndexVersion, ct);
                if (storedVersion != CurrentIndexVersion)
                {
                    UnityEngine.Debug.Log($"[CodebaseIndexer] Index version changed ({storedVersion} → {CurrentIndexVersion}), forcing full rebuild.");
                }

                // 3. 清空旧索引
                await _store.ClearWorkspaceIndexAsync(workspaceId, ct);

                // 4. 解析 Roots
                ct.ThrowIfCancellationRequested();
                progress.Phase = IndexingPhase.Scanning;
                ReportProgress(onProgress, progress);

                var roots = _rootResolver.Resolve(workspace);
                var enabledRoots = roots.Where(r => r.IsEnabled).ToList();

                // 5. 持久化 Roots，获取 rootId
                var rootIdMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var root in enabledRoots)
                {
                    ct.ThrowIfCancellationRequested();
                    var rootId = await _store.UpsertRootAsync(workspaceId, root, ct);
                    rootIdMap[root.RootPath] = rootId;
                    root.Id = rootId;
                    root.WorkspaceId = workspaceId;
                }

                // 6. 扫描所有文件
                var allFiles = ScanAllFiles(enabledRoots, progress, onProgress, ct);
                progress.TotalFiles = allFiles.Count;
                ReportProgress(onProgress, progress);

                // 7. 全量索引
                progress.Phase = IndexingPhase.FullIndexing;
                ReportProgress(onProgress, progress);

                const int yieldEveryNFiles = 10;
                for (var i = 0; i < allFiles.Count; i++)
                {
                    var (root, filePath) = allFiles[i];
                    ct.ThrowIfCancellationRequested();
                    progress.CurrentRoot = root.RootPath;
                    progress.CurrentFile = filePath;

                    try
                    {
                        await IndexFileAsync(workspaceId, root, filePath, workspace, progress, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"[CodebaseIndexer] Unhandled exception indexing '{filePath}': {ex.Message}");
                        progress.ErrorFiles++;
                    }

                    progress.ProcessedFiles++;
                    ReportProgress(onProgress, progress);

                    if (i % yieldEveryNFiles == 0)
                        await Task.Yield();
                }

                // 8. 持久化元数据
                progress.Phase = IndexingPhase.Persisting;
                ReportProgress(onProgress, progress);

                await _store.SetMetadataAsync(workspaceId, MetaKeyLastFullIndex,
                    DateTime.UtcNow.ToString("O"), ct);
                await _store.SetMetadataAsync(workspaceId, MetaKeyIndexVersion,
                    CurrentIndexVersion, ct);

                // 9. 完成
                var completed = IndexingProgress.CreateCompleted(progress);
                ReportProgress(onProgress, completed);
                return completed;
            }
            catch (OperationCanceledException)
            {
                progress.Phase = IndexingPhase.Completed;
                var failed = IndexingProgress.CreateFailed(progress, "Indexing was cancelled.");
                ReportProgress(onProgress, failed);
                return failed;
            }
            catch (Exception ex)
            {
                var failed = IndexingProgress.CreateFailed(progress, $"Full indexing failed: {ex.Message}");
                ReportProgress(onProgress, failed);
                UnityEngine.Debug.LogError($"[CodebaseIndexer] Full indexing failed: {ex}");
                return failed;
            }
        }

        /// <summary>
        /// 执行增量索引。
        /// 对比文件的 ContentHash 和 LastModified，仅重新索引发生变化的文件，
        /// 同时删除已不存在的文件记录。
        ///
        /// 如果尚未建立全量索引（无 last_full_index_at 元数据），则自动降级为全量索引。
        /// </summary>
        /// <param name="onProgress">进度回调（可为 null）。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>最终进度快照。</returns>
        public async Task<IndexingProgress> RunIncrementalIndexAsync(
            Action<IndexingProgress> onProgress = null,
            CancellationToken ct = default)
        {
            var progress = IndexingProgress.CreateStarted(IndexingPhase.Initializing);
            ReportProgress(onProgress, progress);

            try
            {
                // 1. 解析 workspace
                ct.ThrowIfCancellationRequested();
                var workspace = IndexWorkspaceResolver.ResolveFromCurrent();
                var workspaceId = await _store.UpsertWorkspaceAsync(workspace, ct);
                workspace = new IndexWorkspace
                {
                    Id = workspaceId,
                    Fingerprint = workspace.Fingerprint,
                    WorkspaceRoot = workspace.WorkspaceRoot,
                    UnityRoot = workspace.UnityRoot,
                    UnityRootRelativePath = workspace.UnityRootRelativePath,
                    DisplayName = workspace.DisplayName,
                    VcsType = workspace.VcsType,
                    VcsRootPath = workspace.VcsRootPath,
                    VcsUrl = workspace.VcsUrl,
                    RepositoryRoot = workspace.RepositoryRoot,
                    BranchId = workspace.BranchId,
                    Revision = workspace.Revision,
                };

                // 2. 检查是否需要降级为全量索引
                var lastFullIndex = await _store.GetMetadataAsync(workspaceId, MetaKeyLastFullIndex, ct);
                var storedVersion = await _store.GetMetadataAsync(workspaceId, MetaKeyIndexVersion, ct);

                if (string.IsNullOrEmpty(lastFullIndex) || storedVersion != CurrentIndexVersion)
                {
                    UnityEngine.Debug.Log("[CodebaseIndexer] No full index found or version mismatch, falling back to full indexing.");
                    return await RunFullIndexAsync(onProgress, ct);
                }

                // 3. 解析 Roots
                ct.ThrowIfCancellationRequested();
                progress.Phase = IndexingPhase.Scanning;
                ReportProgress(onProgress, progress);

                var roots = _rootResolver.Resolve(workspace);
                var enabledRoots = roots.Where(r => r.IsEnabled).ToList();

                // 4. 持久化 Roots（新增的 Root 会被自动创建）
                var rootIdMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var root in enabledRoots)
                {
                    ct.ThrowIfCancellationRequested();
                    var rootId = await _store.UpsertRootAsync(workspaceId, root, ct);
                    rootIdMap[root.RootPath] = rootId;
                    root.Id = rootId;
                    root.WorkspaceId = workspaceId;
                }

                // 5. 扫描磁盘上的当前文件
                var diskFiles = ScanAllFiles(enabledRoots, progress, onProgress, ct);
                var diskFileSet = new HashSet<string>(
                    diskFiles.Select(f => f.FilePath),
                    StringComparer.OrdinalIgnoreCase);

                // 6. 加载已索引文件记录（用于变更检测）
                var indexedFilesMap = new Dictionary<string, IndexedFile>(StringComparer.OrdinalIgnoreCase);
                foreach (var root in enabledRoots)
                {
                    ct.ThrowIfCancellationRequested();
                    var storedFiles = await _store.GetFilesForRootAsync(root.Id, ct);
                    foreach (var f in storedFiles)
                        indexedFilesMap[f.FilePath] = f;
                }

                // 7. 计算变更集
                var (toAdd, toUpdate, toDelete) = ComputeChangeset(diskFiles, indexedFilesMap, diskFileSet);

                progress.TotalFiles = toAdd.Count + toUpdate.Count;
                ReportProgress(onProgress, progress);

                // 8. 增量索引
                progress.Phase = IndexingPhase.IncrementalIndexing;
                ReportProgress(onProgress, progress);

                // 8a. 删除已消失的文件
                foreach (var deletedFile in toDelete)
                {
                    ct.ThrowIfCancellationRequested();
                    await _store.DeleteDependenciesByFileAsync(deletedFile.Id, ct);
                    await _store.DeleteSymbolsByFileAsync(deletedFile.Id, ct);
                    await _store.DeleteFileAsync(deletedFile.Id, ct);
                }

                // 8b. 新增文件
                foreach (var (root, filePath) in toAdd)
                {
                    ct.ThrowIfCancellationRequested();
                    progress.CurrentRoot = root.RootPath;
                    progress.CurrentFile = filePath;

                    await IndexFileAsync(workspaceId, root, filePath, workspace, progress, ct);

                    progress.ProcessedFiles++;
                    ReportProgress(onProgress, progress);
                }

                // 8c. 更新变更文件
                foreach (var (root, filePath) in toUpdate)
                {
                    ct.ThrowIfCancellationRequested();
                    progress.CurrentRoot = root.RootPath;
                    progress.CurrentFile = filePath;

                    // 先删除旧符号和依赖
                    if (indexedFilesMap.TryGetValue(filePath, out var oldFile))
                    {
                        await _store.DeleteDependenciesByFileAsync(oldFile.Id, ct);
                        await _store.DeleteSymbolsByFileAsync(oldFile.Id, ct);
                    }

                    await IndexFileAsync(workspaceId, root, filePath, workspace, progress, ct);

                    progress.ProcessedFiles++;
                    ReportProgress(onProgress, progress);
                }

                // 9. 持久化元数据
                progress.Phase = IndexingPhase.Persisting;
                ReportProgress(onProgress, progress);

                await _store.SetMetadataAsync(workspaceId, MetaKeyLastIncrementalIndex,
                    DateTime.UtcNow.ToString("O"), ct);

                // 10. 完成
                var completed = IndexingProgress.CreateCompleted(progress);
                ReportProgress(onProgress, completed);
                return completed;
            }
            catch (OperationCanceledException)
            {
                progress.Phase = IndexingPhase.Completed;
                var failed = IndexingProgress.CreateFailed(progress, "Incremental indexing was cancelled.");
                ReportProgress(onProgress, failed);
                return failed;
            }
            catch (Exception ex)
            {
                var failed = IndexingProgress.CreateFailed(progress, $"Incremental indexing failed: {ex.Message}");
                ReportProgress(onProgress, failed);
                UnityEngine.Debug.LogError($"[CodebaseIndexer] Incremental indexing failed: {ex}");
                return failed;
            }
        }

        /// <summary>
        /// 执行针对性增量索引（Targeted Incremental）。
        /// 仅处理调用方显式提供的脏文件和删除文件路径，不执行全盘扫描。
        /// 适用于 BackgroundIndexService 从 DirtyTracker 接收到具体变更列表的场景。
        ///
        /// 如果尚未建立全量索引（无 last_full_index_at 元数据），返回失败进度且
        /// <see cref="IndexingProgress.ErrorMessage"/> 为 "NO_FULL_INDEX"，
        /// 调用方应据此决定是否触发全量索引。
        /// </summary>
        /// <param name="changedPaths">新增或修改的文件绝对路径列表（可为空集合）。</param>
        /// <param name="deletedPaths">已删除的文件绝对路径列表（可为空集合）。</param>
        /// <param name="yieldEveryNFiles">每处理 N 个文件后执行一次 Task.Yield()，
        /// 实现协作式调度，避免长时间阻塞。0 表示不让步。</param>
        /// <param name="onProgress">进度回调（可为 null）。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>最终进度快照。</returns>
        public async Task<IndexingProgress> RunTargetedIncrementalAsync(
            IReadOnlyList<string> changedPaths,
            IReadOnlyList<string> deletedPaths,
            int yieldEveryNFiles = 5,
            Action<IndexingProgress> onProgress = null,
            CancellationToken ct = default)
        {
            var progress = IndexingProgress.CreateStarted(IndexingPhase.Initializing);
            ReportProgress(onProgress, progress);

            try
            {
                // 0. 参数校验
                changedPaths ??= Array.Empty<string>();
                deletedPaths ??= Array.Empty<string>();

                if (changedPaths.Count == 0 && deletedPaths.Count == 0)
                {
                    // 无变更，直接返回成功
                    var empty = IndexingProgress.CreateCompleted(progress);
                    ReportProgress(onProgress, empty);
                    return empty;
                }

                // 1. 解析 workspace
                ct.ThrowIfCancellationRequested();
                var workspace = IndexWorkspaceResolver.ResolveFromCurrent();
                var workspaceId = await _store.UpsertWorkspaceAsync(workspace, ct);
                workspace = new IndexWorkspace
                {
                    Id = workspaceId,
                    Fingerprint = workspace.Fingerprint,
                    WorkspaceRoot = workspace.WorkspaceRoot,
                    UnityRoot = workspace.UnityRoot,
                    UnityRootRelativePath = workspace.UnityRootRelativePath,
                    DisplayName = workspace.DisplayName,
                    VcsType = workspace.VcsType,
                    VcsRootPath = workspace.VcsRootPath,
                    VcsUrl = workspace.VcsUrl,
                    RepositoryRoot = workspace.RepositoryRoot,
                    BranchId = workspace.BranchId,
                    Revision = workspace.Revision,
                };

                // 2. 检查是否已有全量索引（无则拒绝执行）
                var lastFullIndex = await _store.GetMetadataAsync(workspaceId, MetaKeyLastFullIndex, ct);
                var storedVersion = await _store.GetMetadataAsync(workspaceId, MetaKeyIndexVersion, ct);

                if (string.IsNullOrEmpty(lastFullIndex) || storedVersion != CurrentIndexVersion)
                {
                    var noIndex = IndexingProgress.CreateFailed(progress, "NO_FULL_INDEX");
                    ReportProgress(onProgress, noIndex);
                    return noIndex;
                }

                // 3. 解析 Roots（用于确定每个文件归属哪个 Root）
                ct.ThrowIfCancellationRequested();
                var roots = _rootResolver.Resolve(workspace);
                var enabledRoots = roots.Where(r => r.IsEnabled).ToList();

                foreach (var root in enabledRoots)
                {
                    ct.ThrowIfCancellationRequested();
                    var rootId = await _store.UpsertRootAsync(workspaceId, root, ct);
                    root.Id = rootId;
                    root.WorkspaceId = workspaceId;
                }

                // 4. 处理删除的文件
                progress.Phase = IndexingPhase.IncrementalIndexing;
                progress.TotalFiles = changedPaths.Count + deletedPaths.Count;
                ReportProgress(onProgress, progress);

                int processedCount = 0;

                foreach (var deletedPath in deletedPaths)
                {
                    ct.ThrowIfCancellationRequested();
                    var normalizedPath = deletedPath.Replace('\\', '/');

                    var existingFile = await _store.GetFileByPathAsync(workspaceId, normalizedPath, ct);
                    if (existingFile != null)
                    {
                        await _store.DeleteDependenciesByFileAsync(existingFile.Id, ct);
                        await _store.DeleteSymbolsByFileAsync(existingFile.Id, ct);
                        await _store.DeleteFileAsync(existingFile.Id, ct);
                    }

                    processedCount++;
                    progress.ProcessedFiles = processedCount;
                    ReportProgress(onProgress, progress);

                    // 协作式让步
                    if (yieldEveryNFiles > 0 && processedCount % yieldEveryNFiles == 0)
                        await Task.Yield();
                }

                // 5. 处理新增/修改的文件
                foreach (var changedPath in changedPaths)
                {
                    ct.ThrowIfCancellationRequested();
                    var normalizedPath = changedPath.Replace('\\', '/');
                    progress.CurrentFile = normalizedPath;

                    // 确定文件所属 Root
                    var matchedRoot = FindRootForPath(normalizedPath, enabledRoots);
                    if (matchedRoot == null)
                    {
                        // 文件不属于任何启用的 Root，跳过
                        progress.SkippedFiles++;
                        processedCount++;
                        progress.ProcessedFiles = processedCount;
                        ReportProgress(onProgress, progress);
                        continue;
                    }

                    progress.CurrentRoot = matchedRoot.RootPath;

                    // 如果文件已存在于索引中，先删除旧记录
                    var oldFile = await _store.GetFileByPathAsync(workspaceId, normalizedPath, ct);
                    if (oldFile != null)
                    {
                        await _store.DeleteDependenciesByFileAsync(oldFile.Id, ct);
                        await _store.DeleteSymbolsByFileAsync(oldFile.Id, ct);
                    }

                    // 重新索引（IndexFileAsync 内部会检查文件大小和存在性）
                    await IndexFileAsync(workspaceId, matchedRoot, normalizedPath, workspace, progress, ct);

                    processedCount++;
                    progress.ProcessedFiles = processedCount;
                    ReportProgress(onProgress, progress);

                    // 协作式让步
                    if (yieldEveryNFiles > 0 && processedCount % yieldEveryNFiles == 0)
                        await Task.Yield();
                }

                // 6. 持久化元数据
                progress.Phase = IndexingPhase.Persisting;
                ReportProgress(onProgress, progress);

                await _store.SetMetadataAsync(workspaceId, MetaKeyLastIncrementalIndex,
                    DateTime.UtcNow.ToString("O"), ct);

                // 7. 完成
                var completed = IndexingProgress.CreateCompleted(progress);
                ReportProgress(onProgress, completed);
                return completed;
            }
            catch (OperationCanceledException)
            {
                progress.Phase = IndexingPhase.Completed;
                var failed = IndexingProgress.CreateFailed(progress, "Targeted incremental indexing was cancelled.");
                ReportProgress(onProgress, failed);
                return failed;
            }
            catch (Exception ex)
            {
                var failed = IndexingProgress.CreateFailed(progress, $"Targeted incremental indexing failed: {ex.Message}");
                ReportProgress(onProgress, failed);
                UnityEngine.Debug.LogError($"[CodebaseIndexer] Targeted incremental indexing failed: {ex}");
                return failed;
            }
        }

        /// <summary>
        /// 获取当前 workspace 的索引统计信息。
        /// </summary>
        /// <param name="ct">取消令牌。</param>
        /// <returns>统计信息，如果尚未建立索引则返回 null。</returns>
        public async Task<IndexingStats> GetStatsAsync(CancellationToken ct = default)
        {
            try
            {
                var workspace = IndexWorkspaceResolver.ResolveFromCurrent();
                var stored = await _store.GetWorkspaceByFingerprintAsync(workspace.Fingerprint, ct);
                if (stored == null) return null;
                return await _store.GetStatsAsync(stored.Id, ct);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[CodebaseIndexer] GetStatsAsync failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 获取最后一次全量索引时间（UTC）。不存在时返回 null。
        /// </summary>
        public async Task<DateTime?> GetLastFullIndexTimeAsync(CancellationToken ct = default)
        {
            try
            {
                var workspace = IndexWorkspaceResolver.ResolveFromCurrent();
                var stored = await _store.GetWorkspaceByFingerprintAsync(workspace.Fingerprint, ct);
                if (stored == null) return null;

                var val = await _store.GetMetadataAsync(stored.Id, MetaKeyLastFullIndex, ct);
                if (string.IsNullOrEmpty(val)) return null;
                return DateTime.Parse(val, null, System.Globalization.DateTimeStyles.RoundtripKind);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 获取最后一次增量索引时间（UTC）。不存在时返回 null。
        /// </summary>
        public async Task<DateTime?> GetLastIncrementalIndexTimeAsync(CancellationToken ct = default)
        {
            try
            {
                var workspace = IndexWorkspaceResolver.ResolveFromCurrent();
                var stored = await _store.GetWorkspaceByFingerprintAsync(workspace.Fingerprint, ct);
                if (stored == null) return null;

                var val = await _store.GetMetadataAsync(stored.Id, MetaKeyLastIncrementalIndex, ct);
                if (string.IsNullOrEmpty(val)) return null;
                return DateTime.Parse(val, null, System.Globalization.DateTimeStyles.RoundtripKind);
            }
            catch
            {
                return null;
            }
        }

        // ── 私有方法 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 扫描所有启用 Root 下的文件，返回 (root, filePath) 列表。
        /// 应用 IncludePatterns / ExcludePatterns 过滤，跳过超大文件。
        /// </summary>
        private List<(IndexRoot Root, string FilePath)> ScanAllFiles(
            IReadOnlyList<IndexRoot> roots,
            IndexingProgress progress,
            Action<IndexingProgress> onProgress,
            CancellationToken ct)
        {
            var result = new List<(IndexRoot, string)>();

            foreach (var root in roots)
            {
                ct.ThrowIfCancellationRequested();

                if (!Directory.Exists(root.RootPath))
                {
                    UnityEngine.Debug.LogWarning($"[CodebaseIndexer] Root directory not found, skipping: {root.RootPath}");
                    continue;
                }

                progress.CurrentRoot = root.RootPath;
                ReportProgress(onProgress, progress);

                try
                {
                    var files = ScanRootFiles(root, ct);
                    result.AddRange(files.Select(f => (root, f)));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[CodebaseIndexer] Failed to scan root '{root.RootPath}': {ex.Message}");
                }
            }

            return result;
        }

        /// <summary>
        /// 扫描单个 Root 目录下的所有匹配文件。
        /// </summary>
        private List<string> ScanRootFiles(IndexRoot root, CancellationToken ct)
        {
            var result = new List<string>();
            var rootPath = root.RootPath;

            // 构建包含模式（默认 *.cs）
            var includePatterns = root.IncludePatterns != null && root.IncludePatterns.Count > 0
                ? root.IncludePatterns
                : new List<string> { "*.cs" };

            // 递归枚举所有文件
            foreach (var pattern in includePatterns)
            {
                ct.ThrowIfCancellationRequested();

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(rootPath, pattern, SearchOption.AllDirectories);
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[CodebaseIndexer] EnumerateFiles failed for '{rootPath}' pattern '{pattern}': {ex.Message}");
                    continue;
                }

                foreach (var filePath in files)
                {
                    ct.ThrowIfCancellationRequested();

                    // 规范化路径
                    var normalizedPath = filePath.Replace('\\', '/');

                    // 应用排除规则
                    if (IsExcluded(normalizedPath, rootPath, root.ExcludePatterns))
                    {
                        continue;
                    }

                    // 检查文件大小
                    try
                    {
                        var fileInfo = new FileInfo(filePath);
                        if (fileInfo.Length > _maxFileSizeBytes)
                        {
                            continue;
                        }
                    }
                    catch
                    {
                        continue;
                    }

                    result.Add(normalizedPath);
                }
            }

            // 去重（多个 pattern 可能匹配同一文件）
            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// 判断文件是否应被排除。
        /// 排除规则支持：目录前缀（以 / 结尾）、文件名通配符（*.generated.cs）、精确路径片段。
        /// </summary>
        private static bool IsExcluded(string normalizedFilePath, string rootPath, List<string> excludePatterns)
        {
            if (excludePatterns == null || excludePatterns.Count == 0)
                return false;

            // 获取相对于 root 的路径（用于模式匹配）
            var normalizedRoot = rootPath.Replace('\\', '/').TrimEnd('/') + '/';
            var relativePath = normalizedFilePath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                ? normalizedFilePath.Substring(normalizedRoot.Length)
                : normalizedFilePath;

            foreach (var pattern in excludePatterns)
            {
                if (string.IsNullOrEmpty(pattern)) continue;

                // 目录排除（以 / 结尾，如 "bin/"、"obj/"）
                if (pattern.EndsWith("/"))
                {
                    var dirSegment = pattern.TrimEnd('/');
                    // 检查路径中是否包含该目录段
                    if (relativePath.IndexOf('/' + dirSegment + '/', StringComparison.OrdinalIgnoreCase) >= 0 ||
                        relativePath.StartsWith(dirSegment + '/', StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                // 通配符模式（如 "*.generated.cs"）
                else if (pattern.Contains('*') || pattern.Contains('?'))
                {
                    var fileName = Path.GetFileName(normalizedFilePath);
                    if (MatchesWildcard(fileName, pattern))
                        return true;
                }
                // 精确路径片段
                else
                {
                    if (relativePath.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 简单通配符匹配（支持 * 和 ?）。
        /// </summary>
        private static bool MatchesWildcard(string input, string pattern)
        {
            // 将通配符转换为正则表达式
            var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(
                input, regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// 对单个文件执行索引（提取符号并持久化）。
        /// 超大文件、读取失败的文件会被记录为 SkippedFiles 或 ErrorFiles。
        /// </summary>
        private async Task IndexFileAsync(
            int workspaceId,
            IndexRoot root,
            string filePath,
            IndexWorkspace workspace,
            IndexingProgress progress,
            CancellationToken ct)
        {
            // 检查文件大小
            long fileSize;
            try
            {
                fileSize = new FileInfo(filePath).Length;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[CodebaseIndexer] Cannot stat file '{filePath}': {ex.Message}");
                progress.SkippedFiles++;
                return;
            }

            if (fileSize > _maxFileSizeBytes)
            {
                progress.SkippedFiles++;
                return;
            }

            // 提取符号（Roslyn 解析），对单个文件设置超时保护，避免极端文件卡死主线程
            ExtractionResult extraction;
            try
            {
                const int perFileTimeoutMs = 5000;
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(perFileTimeoutMs);
                extraction = await Task.Run(() => RoslynSymbolExtractor.ExtractFromFile(filePath, 0, root, workspace), cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                UnityEngine.Debug.LogWarning($"[CodebaseIndexer] Extraction timed out for '{filePath}' (>5s), skipping.");
                progress.ErrorFiles++;
                return;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[CodebaseIndexer] Extraction failed for '{filePath}': {ex.Message}");
                progress.ErrorFiles++;
                return;
            }

            if (!extraction.IsSuccess)
            {
                // 即使解析失败，也持久化文件记录（标记 HasErrors）
                var errorFile = extraction.File ?? new IndexedFile
                {
                    FilePath = filePath,
                    RelativeToRoot = MakeRelativeToRoot(filePath, root.RootPath),
                    ContentHash = string.Empty,
                    LastModified = GetLastModifiedTicks(filePath),
                    LastIndexed = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    FileSize = fileSize,
                    HasErrors = true,
                    ErrorMessage = extraction.ErrorMessage,
                    SymbolCount = 0,
                };

                errorFile.WorkspaceId = workspaceId;
                errorFile.RootId = root.Id;

                try
                {
                    await _store.UpsertFileAsync(workspaceId, root.Id, errorFile, ct);
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[CodebaseIndexer] Failed to persist error file record '{filePath}': {ex.Message}");
                }

                progress.ErrorFiles++;
                return;
            }

            // 持久化文件记录
            var indexedFile = extraction.File;
            indexedFile.WorkspaceId = workspaceId;
            indexedFile.RootId = root.Id;
            indexedFile.LastIndexed = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            int fileId;
            try
            {
                fileId = await _store.UpsertFileAsync(workspaceId, root.Id, indexedFile, ct);
                indexedFile.Id = fileId;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[CodebaseIndexer] Failed to persist file record '{filePath}': {ex.Message}");
                progress.ErrorFiles++;
                return;
            }

            // 更新符号的 FileId
            var symbols = extraction.Symbols;
            if (symbols != null && symbols.Count > 0)
            {
                foreach (var sym in symbols)
                {
                    sym.FileId = fileId;
                    sym.WorkspaceId = workspaceId;
                    sym.RootId = root.Id;
                }

                try
                {
                    await _store.BulkInsertSymbolsAsync(symbols, ct);
                    progress.ExtractedSymbols += symbols.Count;
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[CodebaseIndexer] Failed to persist symbols for '{filePath}': {ex.Message}");
                }

                // 提取并持久化依赖关系
                try
                {
                    // 构建 symbolName → dbId 映射（用于关联 FromSymbolId / ToSymbolId）
                    var symbolIdMap = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (var sym in symbols)
                    {
                        if (!string.IsNullOrEmpty(sym.FullName) && sym.Id > 0)
                            symbolIdMap[sym.FullName] = sym.Id;
                    }

                    var deps = DependencyExtractor.ExtractFromTree(
                        extraction.SyntaxTree, workspaceId, fileId, symbolIdMap);

                    if (deps != null && deps.Count > 0)
                        await _store.BulkInsertDependenciesAsync(deps, ct);
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[CodebaseIndexer] Failed to extract/persist dependencies for '{filePath}': {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 计算增量索引的变更集：新增、更新、删除。
        /// </summary>
        private static (
            List<(IndexRoot Root, string FilePath)> ToAdd,
            List<(IndexRoot Root, string FilePath)> ToUpdate,
            List<IndexedFile> ToDelete
        ) ComputeChangeset(
            List<(IndexRoot Root, string FilePath)> diskFiles,
            Dictionary<string, IndexedFile> indexedFilesMap,
            HashSet<string> diskFileSet)
        {
            var toAdd = new List<(IndexRoot, string)>();
            var toUpdate = new List<(IndexRoot, string)>();
            var toDelete = new List<IndexedFile>();

            // 检查磁盘文件：新增 or 变更
            foreach (var (root, filePath) in diskFiles)
            {
                if (!indexedFilesMap.TryGetValue(filePath, out var indexed))
                {
                    // 新文件
                    toAdd.Add((root, filePath));
                }
                else
                {
                    // 检查是否变更（LastModified 或 ContentHash）
                    var currentLastModified = GetLastModifiedTicks(filePath);
                    if (currentLastModified != indexed.LastModified)
                    {
                        // LastModified 变化，需要重新索引
                        toUpdate.Add((root, filePath));
                    }
                    // 注意：ContentHash 的精确比较在 RoslynSymbolExtractor 内部完成
                    // 这里只用 LastModified 作为快速检测，避免读取所有文件内容
                }
            }

            // 检查已索引文件：是否已从磁盘删除
            foreach (var kvp in indexedFilesMap)
            {
                if (!diskFileSet.Contains(kvp.Key))
                {
                    toDelete.Add(kvp.Value);
                }
            }

            return (toAdd, toUpdate, toDelete);
        }

        /// <summary>
        /// 获取文件的最后修改时间（UTC Ticks）。文件不存在时返回 0。
        /// </summary>
        private static long GetLastModifiedTicks(string filePath)
        {
            try
            {
                return new FileInfo(filePath).LastWriteTimeUtc.Ticks;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 计算文件相对于 Root 的路径。
        /// </summary>
        private static string MakeRelativeToRoot(string filePath, string rootPath)
        {
            var normalizedRoot = rootPath.Replace('\\', '/').TrimEnd('/') + '/';
            var normalizedFile = filePath.Replace('\\', '/');
            return normalizedFile.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                ? normalizedFile.Substring(normalizedRoot.Length)
                : normalizedFile;
        }

        /// <summary>
        /// 为给定文件路径找到其所属的 IndexRoot。
        /// 匹配规则：文件路径以 Root 路径为前缀（忽略大小写、统一正斜杠）。
        /// 当多个 Root 匹配时，返回最长前缀匹配（最具体的 Root）。
        /// </summary>
        private static IndexRoot FindRootForPath(string normalizedFilePath, IReadOnlyList<IndexRoot> roots)
        {
            IndexRoot bestMatch = null;
            int bestLength = 0;

            foreach (var root in roots)
            {
                var normalizedRoot = root.RootPath.Replace('\\', '/').TrimEnd('/') + '/';
                if (normalizedFilePath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    if (normalizedRoot.Length > bestLength)
                    {
                        bestLength = normalizedRoot.Length;
                        bestMatch = root;
                    }
                }
            }

            return bestMatch;
        }

        /// <summary>
        /// 触发进度回调（null 安全）。
        /// </summary>
        private static void ReportProgress(Action<IndexingProgress> onProgress, IndexingProgress progress)
        {
            try
            {
                onProgress?.Invoke(progress);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[CodebaseIndexer] Progress callback threw: {ex.Message}");
            }
        }
    }
}
