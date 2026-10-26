using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Components.Indexing.Models;
using AgentCore.Editor.Components.Indexing.Roots;
using AgentCore.Editor.Utils;

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
    ///   - IIndexStore 写操作由 IndexService 的单写线程串行执行。
    ///   - 同一进程内的索引运行（全量 / 增量 / 针对性增量 / Scope）通过 s_runGate 排队，
    ///     面板、后台服务与工具同时触发时依次执行，不会交错写同一批文件。
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

        /// <summary>元数据键：最后一次 Scope 重建时间（UTC ISO 8601）。Scope 重建不写 last_full_index_at。</summary>
        public const string MetaKeyLastScopeIndex = "last_scope_index_at";

        /// <summary>元数据键：最后一次 Scope 重建覆盖的 Root 描述。</summary>
        public const string MetaKeyLastScopeIndexRoots = "last_scope_index_roots";

        /// <summary>元数据键：最后一次任意类型索引运行时间（全量 / 增量 / Scope，UTC ISO 8601）。</summary>
        public const string MetaKeyLastIndex = "last_index_at";

        /// <summary>元数据键：最后一次索引运行类型（full / incremental / scope）。</summary>
        public const string MetaKeyLastIndexKind = "last_index_kind";

        /// <summary>元数据键：最后一次索引的版本号（用于强制重建）。</summary>
        public const string MetaKeyIndexVersion = "index_version";

        /// <summary>当前索引格式版本号。版本变更时触发强制全量重建。</summary>
        public const string CurrentIndexVersion = "1";

        // ── 字段 ────────────────────────────────────────────────────────────────

        private readonly IIndexStore _store;
        private readonly IndexRootResolver _rootResolver;
        private readonly long _maxFileSizeBytes;

        /// <summary>进程内索引运行互斥（排队等待，不拒绝）。</summary>
        private static readonly SemaphoreSlim s_runGate = new SemaphoreSlim(1, 1);

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
        public Task<IndexingProgress> RunFullIndexAsync(
            Action<IndexingProgress> onProgress = null,
            CancellationToken ct = default)
        {
            return RunExclusiveAsync(() => RunFullIndexCoreAsync(onProgress, ct), onProgress, ct);
        }

        private async Task<IndexingProgress> RunFullIndexCoreAsync(
            Action<IndexingProgress> onProgress,
            CancellationToken ct)
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
                    AgentCoreLog.Info($"[CodebaseIndexer] Index version changed ({storedVersion} → {CurrentIndexVersion}), forcing full rebuild.");
                }

                // 3. 清空旧索引
                await _store.ClearWorkspaceIndexAsync(workspaceId, ct);

                // 4. 解析 Roots
                ct.ThrowIfCancellationRequested();
                progress.Phase = IndexingPhase.Scanning;
                ReportProgress(onProgress, progress);

                var roots = _rootResolver.Resolve(workspace);
                var enabledRoots = GetParticipatingRoots(roots);

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

                await DisableStaleRootsAsync(workspaceId, roots, ct);

                // 6. 扫描所有文件
                var allFiles = ScanAllFiles(enabledRoots, progress, onProgress, ct, roots);
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
                        AgentCoreLog.Warning($"[CodebaseIndexer] Unhandled exception indexing '{filePath}': {ex.Message}");
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

                await IndexRunMetadata.RecordAsync(_store, workspaceId, IndexRunKind.Full, null, DateTime.UtcNow, ct);
                await _store.SetMetadataAsync(workspaceId, MetaKeyIndexVersion,
                    CurrentIndexVersion, ct);

                // 全量重建完成：清除存储的损坏标记（如有）
                if (_store is IIndexHealth health && health.RequiresRebuild)
                    await health.AcknowledgeRebuildAsync(ct);

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
                AgentCoreLog.Error($"[CodebaseIndexer] Full indexing failed: {ex}");
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
        public Task<IndexingProgress> RunIncrementalIndexAsync(
            Action<IndexingProgress> onProgress = null,
            CancellationToken ct = default)
        {
            return RunExclusiveAsync(() => RunIncrementalIndexCoreAsync(onProgress, ct), onProgress, ct);
        }

        private async Task<IndexingProgress> RunIncrementalIndexCoreAsync(
            Action<IndexingProgress> onProgress,
            CancellationToken ct)
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
                    AgentCoreLog.Info("[CodebaseIndexer] No full index found or version mismatch, falling back to full indexing.");
                    return await RunFullIndexCoreAsync(onProgress, ct);
                }

                // 3. 解析 Roots
                ct.ThrowIfCancellationRequested();
                progress.Phase = IndexingPhase.Scanning;
                ReportProgress(onProgress, progress);

                var roots = _rootResolver.Resolve(workspace);
                var enabledRoots = GetParticipatingRoots(roots);

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

                await DisableStaleRootsAsync(workspaceId, roots, ct);

                // 5. 扫描磁盘上的当前文件（扫描不完整的 Root 不参与删除判定）
                var incompleteRootIds = new HashSet<int>();
                var diskFiles = ScanAllFiles(enabledRoots, progress, onProgress, ct, roots, incompleteRootIds);
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
                if (incompleteRootIds.Count > 0)
                    toDelete.RemoveAll(f => incompleteRootIds.Contains(f.RootId));

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

                await IndexRunMetadata.RecordAsync(_store, workspaceId, IndexRunKind.Incremental, null, DateTime.UtcNow, ct);

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
                AgentCoreLog.Error($"[CodebaseIndexer] Incremental indexing failed: {ex}");
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
        public Task<IndexingProgress> RunTargetedIncrementalAsync(
            IReadOnlyList<string> changedPaths,
            IReadOnlyList<string> deletedPaths,
            int yieldEveryNFiles = 5,
            Action<IndexingProgress> onProgress = null,
            CancellationToken ct = default)
        {
            return RunExclusiveAsync(
                () => RunTargetedIncrementalCoreAsync(changedPaths, deletedPaths, yieldEveryNFiles, onProgress, ct),
                onProgress, ct);
        }

        private async Task<IndexingProgress> RunTargetedIncrementalCoreAsync(
            IReadOnlyList<string> changedPaths,
            IReadOnlyList<string> deletedPaths,
            int yieldEveryNFiles,
            Action<IndexingProgress> onProgress,
            CancellationToken ct)
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
                    if (matchedRoot != null && !IndexingSchedulePolicy.ParticipatesInBackgroundLoop(matchedRoot))
                    {
                        // OnDemand Root 只经 index_scope 更新
                        matchedRoot = null;
                    }
                    else if (matchedRoot != null && !IndexRootScanner.ShouldIndex(normalizedPath, matchedRoot, roots))
                    {
                        // 与全量扫描同一套规则：.asmdef / .json、排除目录下的 .cs 等不入库，之前误入库的记录顺带清掉
                        var excludedFile = await _store.GetFileByPathAsync(workspaceId, normalizedPath, ct);
                        if (excludedFile != null)
                        {
                            await _store.DeleteDependenciesByFileAsync(excludedFile.Id, ct);
                            await _store.DeleteSymbolsByFileAsync(excludedFile.Id, ct);
                            await _store.DeleteFileAsync(excludedFile.Id, ct);
                        }
                        matchedRoot = null;
                    }
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

                await IndexRunMetadata.RecordAsync(_store, workspaceId, IndexRunKind.Incremental, null, DateTime.UtcNow, ct);

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
                AgentCoreLog.Error($"[CodebaseIndexer] Targeted incremental indexing failed: {ex}");
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
                AgentCoreLog.Warning($"[CodebaseIndexer] GetStatsAsync failed: {ex.Message}");
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
        /// 获取全部索引时间戳（全量 / 增量 / Scope / 任意最近一次）。尚未建立索引时返回 null。
        /// </summary>
        public async Task<IndexTimestamps> GetIndexTimestampsAsync(CancellationToken ct = default)
        {
            try
            {
                var workspace = IndexWorkspaceResolver.ResolveFromCurrent();
                var stored = await _store.GetWorkspaceByFingerprintAsync(workspace.Fingerprint, ct);
                if (stored == null) return null;
                return await IndexRunMetadata.ReadAsync(_store, stored.Id, ct);
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[CodebaseIndexer] GetIndexTimestampsAsync failed: {ex.Message}");
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

        /// <summary>
        /// 只重建指定 Root：先删除这些 Root 已有的文件 / 符号 / 依赖，再扫描并索引，
        /// 其他 Root 的数据保持不动（不会清空整个 workspace）。
        /// <para>不写入 last_full_index_at：Scope 重建不等于全量索引；写入 last_scope_index_at / last_index_at。</para>
        /// </summary>
        /// <param name="targetRoots">目标 Root（Id 可为 0，内部 upsert 获取真实 Id）。</param>
        /// <param name="onProgress">进度回调（可为 null）。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>最终进度快照。</returns>
        public Task<IndexingProgress> RunScopeIndexAsync(
            IReadOnlyList<IndexRoot> targetRoots,
            Action<IndexingProgress> onProgress = null,
            CancellationToken ct = default)
        {
            return RunExclusiveAsync(() => RunScopeIndexCoreAsync(targetRoots, onProgress, ct), onProgress, ct);
        }

        private async Task<IndexingProgress> RunScopeIndexCoreAsync(
            IReadOnlyList<IndexRoot> targetRoots,
            Action<IndexingProgress> onProgress,
            CancellationToken ct)
        {
            var progress = IndexingProgress.CreateStarted(IndexingPhase.Initializing);
            ReportProgress(onProgress, progress);

            try
            {
                var roots = (targetRoots ?? Array.Empty<IndexRoot>()).Where(r => r != null).ToList();

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

                // 2. 持久化目标 Root，并只清除这些 Root 的旧数据
                foreach (var root in roots)
                {
                    ct.ThrowIfCancellationRequested();
                    root.Id = await _store.UpsertRootAsync(workspaceId, root, ct);
                    root.WorkspaceId = workspaceId;
                    await ClearRootAsync(workspaceId, root.Id, ct);
                }

                // 3. 扫描目标 Root
                progress.Phase = IndexingPhase.Scanning;
                ReportProgress(onProgress, progress);
                var allFiles = ScanAllFiles(roots, progress, onProgress, ct, _rootResolver.Resolve(workspace));
                progress.TotalFiles = allFiles.Count;

                // 4. 索引
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
                        AgentCoreLog.Warning($"[CodebaseIndexer] Unhandled exception indexing '{filePath}': {ex.Message}");
                        progress.ErrorFiles++;
                    }

                    progress.ProcessedFiles++;
                    ReportProgress(onProgress, progress);

                    if (i % yieldEveryNFiles == 0)
                        await Task.Yield();
                }

                // 5. 元数据：记录 Scope 重建时间与覆盖的 Root（及 last_index_at），不写 last_full_index_at
                progress.Phase = IndexingPhase.Persisting;
                ReportProgress(onProgress, progress);
                await IndexRunMetadata.RecordAsync(_store, workspaceId, IndexRunKind.Scope, roots, DateTime.UtcNow, ct);

                var completed = IndexingProgress.CreateCompleted(progress);
                ReportProgress(onProgress, completed);
                return completed;
            }
            catch (OperationCanceledException)
            {
                progress.Phase = IndexingPhase.Completed;
                var failed = IndexingProgress.CreateFailed(progress, "Scope indexing was cancelled.");
                ReportProgress(onProgress, failed);
                return failed;
            }
            catch (Exception ex)
            {
                var failed = IndexingProgress.CreateFailed(progress, $"Scope indexing failed: {ex.Message}");
                ReportProgress(onProgress, failed);
                AgentCoreLog.Error($"[CodebaseIndexer] Scope indexing failed: {ex}");
                return failed;
            }
        }

        // ── 私有方法 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 删除单个 Root 的全部索引数据。后端支持 <see cref="IScopedIndexStore"/> 时一次完成，否则逐文件删除。
        /// </summary>
        private async Task ClearRootAsync(int workspaceId, int rootId, CancellationToken ct)
        {
            if (_store is IScopedIndexStore scoped)
            {
                await scoped.ClearRootIndexAsync(workspaceId, rootId, ct);
                return;
            }

            var files = await _store.GetFilesForRootAsync(rootId, ct);
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                if (f.WorkspaceId != 0 && f.WorkspaceId != workspaceId) continue;
                await _store.DeleteDependenciesByFileAsync(f.Id, ct);
                await _store.DeleteSymbolsByFileAsync(f.Id, ct);
                await _store.DeleteFileAsync(f.Id, ct);
            }
        }

        /// <summary>
        /// 排队获取进程内索引运行锁后执行。等待期间被取消时返回“已取消”进度而不是抛出。
        /// </summary>
        private static async Task<IndexingProgress> RunExclusiveAsync(
            Func<Task<IndexingProgress>> body,
            Action<IndexingProgress> onProgress,
            CancellationToken ct)
        {
            try
            {
                await s_runGate.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                var cancelled = IndexingProgress.CreateFailed(
                    IndexingProgress.CreateStarted(IndexingPhase.Initializing), "Indexing was cancelled.");
                ReportProgress(onProgress, cancelled);
                return cancelled;
            }

            try
            {
                return await body();
            }
            finally
            {
                s_runGate.Release();
            }
        }

        /// <summary>
        /// 扫描所有启用 Root 下的文件，返回 (root, filePath) 列表。
        /// 应用 IncludePatterns / ExcludePatterns 过滤，跳过超大文件；<paramref name="allRoots"/> 中嵌套在某个 Root 里的
        /// 其他 Root 由其自身负责，不重复扫描。扫描不完整（目录不可读、整体失败）的 Root 记入 <paramref name="incompleteRootIds"/>。
        /// </summary>
        private List<(IndexRoot Root, string FilePath)> ScanAllFiles(
            IReadOnlyList<IndexRoot> roots,
            IndexingProgress progress,
            Action<IndexingProgress> onProgress,
            CancellationToken ct,
            IReadOnlyList<IndexRoot> allRoots = null,
            HashSet<int> incompleteRootIds = null)
        {
            var result = new List<(IndexRoot, string)>();

            foreach (var root in roots)
            {
                ct.ThrowIfCancellationRequested();

                if (!Directory.Exists(root.RootPath))
                {
                    AgentCoreLog.Warning($"[CodebaseIndexer] Root directory not found, skipping: {root.RootPath}");
                    continue;
                }

                progress.CurrentRoot = root.RootPath;
                ReportProgress(onProgress, progress);

                try
                {
                    var scan = IndexRootScanner.Scan(root, allRoots ?? roots, _maxFileSizeBytes, ct);
                    result.AddRange(scan.Files.Select(f => (root, f)));
                    if (!scan.Complete) incompleteRootIds?.Add(root.Id);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    incompleteRootIds?.Add(root.Id);
                    AgentCoreLog.Warning($"[CodebaseIndexer] Failed to scan root '{root.RootPath}': {ex.Message}");
                }
            }

            return result;
        }

        /// <summary>
        /// 参与全量 / 增量索引的 Root：已启用且不是 OnDemand（引擎、商业插件、生成代码、只读参考只经 index_scope 索引）。
        /// </summary>
        private static List<IndexRoot> GetParticipatingRoots(IReadOnlyList<IndexRoot> roots)
        {
            return roots.Where(r => r.IsEnabled && IndexingSchedulePolicy.ParticipatesInBackgroundLoop(r)).ToList();
        }

        /// <summary>
        /// 存储里仍启用、但已不在解析结果中（目录消失、被停用、Root 划分变化，如 Assets/Scripts → Assets）的 Root：
        /// 清掉它的文件与符号并标为停用，避免旧符号继续被搜到或与新 Root 下的同一文件重复。
        /// </summary>
        private async Task DisableStaleRootsAsync(int workspaceId, IReadOnlyList<IndexRoot> resolvedRoots, CancellationToken ct)
        {
            var activePaths = new HashSet<string>(
                resolvedRoots.Where(r => r.IsEnabled).Select(r => r.RootPath.Replace('\\', '/').TrimEnd('/')),
                StringComparer.OrdinalIgnoreCase);

            foreach (var stored in await _store.GetRootsAsync(workspaceId, ct))
            {
                ct.ThrowIfCancellationRequested();
                if (!stored.IsEnabled || string.IsNullOrEmpty(stored.RootPath)) continue;
                if (activePaths.Contains(stored.RootPath.Replace('\\', '/').TrimEnd('/'))) continue;

                await ClearRootAsync(workspaceId, stored.Id, ct);
                stored.IsEnabled = false;
                await _store.UpsertRootAsync(workspaceId, stored, ct);
                AgentCoreLog.Info($"[CodebaseIndexer] Root '{stored.RootPath}' is no longer active; cleared its index data and disabled it.");
            }
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
                AgentCoreLog.Warning($"[CodebaseIndexer] Cannot stat file '{filePath}': {ex.Message}");
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
                AgentCoreLog.Warning($"[CodebaseIndexer] Extraction timed out for '{filePath}' (>5s), skipping.");
                progress.ErrorFiles++;
                return;
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[CodebaseIndexer] Extraction failed for '{filePath}': {ex.Message}");
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
                    AgentCoreLog.Warning($"[CodebaseIndexer] Failed to persist error file record '{filePath}': {ex.Message}");
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
                AgentCoreLog.Warning($"[CodebaseIndexer] Failed to persist file record '{filePath}': {ex.Message}");
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
                    AgentCoreLog.Warning($"[CodebaseIndexer] Failed to persist symbols for '{filePath}': {ex.Message}");
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
                    AgentCoreLog.Warning($"[CodebaseIndexer] Failed to extract/persist dependencies for '{filePath}': {ex.Message}");
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
                AgentCoreLog.Warning($"[CodebaseIndexer] Progress callback threw: {ex.Message}");
            }
        }
    }
}
