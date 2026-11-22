using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Components.Indexing.Config;
using AgentCore.Editor.Components.Indexing.Models;
using AgentCore.Editor.Extensions;
using UnityEditor;
using UnityEngine;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Components.Indexing.Core
{
    /// <summary>
    /// Coordinates background incremental indexing from accumulated dirty paths.
    /// <para>
    /// Besides dirty paths it also runs a whole-project reconcile (<see cref="CodebaseIndexer.RunIncrementalIndexAsync"/>,
    /// mtime comparison that falls back to a full rebuild when no complete full index exists): once at a low
    /// priority after editor start / domain reload, and as soon as possible when a targeted run reports
    /// NO_FULL_INDEX. Every kind of run goes through the same gates (auto-index setting, session pause,
    /// compiling, play mode, failure / burst backoff, quiet delay).
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    public static class BackgroundIndexService
    {
        private static readonly int[] BackoffSeconds = { 5, 30, 120 };

        // After MaxConsecutiveFailures the service backs off for this long instead of stopping for the session.
        private const int DisabledRetrySeconds = 600;

        // A path that fails to index this many times in a row is dropped from the dirty queue (the next edit re-adds it).
        private const int MaxPathAttempts = 3;

        // Low-priority reconcile after editor start / domain reload waits at least this long.
        private const double StartupReconcileDelaySeconds = 30;

        // Running progress is published from the main thread at most this often.
        private const double ProgressPublishIntervalSeconds = 0.5;

        private static CancellationTokenSource _cts;
        private static bool _running;
        private static double _nextRunAt;
        private static double _backoffUntil;
        private static int _consecutiveFailures;
        private static string _lastError;
        private static DateTime? _lastSuccessAt;
        private static bool _sessionPaused;

        // A damaged store (IIndexHealth.RequiresRebuild) triggers at most one automatic full rebuild per session.
        private static bool _rebuildAttempted;

        // Whole-project reconcile requests: missing full index (high priority) / startup check (low priority).
        private static bool _fullIndexMissing;
        private static bool _startupReconcilePending = true;
        private static double _startupReconcileAt;

        // Consecutive indexing failures per path (in-memory only).
        private static readonly Dictionary<string, int> _pathFailures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Progress of the current run, written by the indexer on a pool thread and published from OnEditorUpdate.
        private static volatile IndexingProgress _runProgress;
        private static int _runDirtyCount;
        private static int _runTotalHint;
        private static double _lastProgressPublishAt;

        private const string ComponentDisabledReason = "Code Indexing component is disabled.";

        // v1.4.0 — burst backoff (set by IndexingDirtyTracker when a large batch arrives)
        private static double _burstBackoffUntil;
        private static string _burstReason;

        static BackgroundIndexService()
        {
            // 批处理模式（命令行测试、CI 构建）不做后台自动索引：进程随时退出，只会留下被中断的全量；手动 index_full 等不受影响
            if (Application.isBatchMode) return;
            IndexingDirtyTracker.DirtyChanged += OnDirtyChanged;
            AssemblyReloadEvents.beforeAssemblyReload += CancelCurrentRun;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.update += OnEditorUpdate;
            OptionalComponentManager.ComponentStateChanged += OnComponentStateChanged;
            _startupReconcileAt = EditorApplication.timeSinceStartup + StartupReconcileDelaySeconds;
            OnDirtyChanged();
        }

        private static void OnComponentStateChanged(string componentId, bool enabled)
        {
            if (componentId != OptionalComponentManager.IndexingComponentId)
                return;

            if (!enabled)
            {
                CancelCurrentRun();
                PublishDisabled(IndexingDirtyTracker.Count, ComponentDisabledReason);
                return;
            }

            // Edits made while disabled were not tracked: reconcile the whole project after the startup delay.
            _startupReconcilePending = true;
            _startupReconcileAt = EditorApplication.timeSinceStartup + StartupReconcileDelaySeconds;
            RequestRun();
        }

        /// <summary>
        /// Gets whether automatic indexing is paused for the current editor session.
        /// </summary>
        public static bool SessionPaused => _sessionPaused;

        /// <summary>
        /// Temporarily pauses or resumes automatic indexing for the current editor session.
        /// </summary>
        public static void SetSessionPaused(bool paused)
        {
            _sessionPaused = paused;
            if (paused)
            {
                CancelCurrentRun();
                PublishDisabled(IndexingDirtyTracker.Count, "Auto-index paused for this editor session.");
                return;
            }

            RequestRun();
        }

        /// <summary>
        /// Requests a background indexing pass after the configured quiet delay. An explicit request also
        /// clears the failure counter and backoff, so a service that backed off after repeated failures
        /// tries again right away.
        /// </summary>
        public static void RequestRun()
        {
            _consecutiveFailures = 0;
            _backoffUntil = 0;
            _pathFailures.Clear();
            OnDirtyChanged();
        }

        /// <summary>
        /// v1.4.0 — Notified by <see cref="IndexingDirtyTracker"/> when a burst of dirty files
        /// is detected. Delays the next background run so the Editor can settle.
        /// </summary>
        /// <param name="dirtyBatchCount">Number of files added in the triggering batch.</param>
        /// <param name="backoffSeconds">Duration to pause. Uses configured default when &lt;= 0.</param>
        public static void NotifyBurstDetected(int dirtyBatchCount, int backoffSeconds)
        {
            var settings = GetSettings();
            var seconds = backoffSeconds > 0 ? backoffSeconds : Math.Max(1, settings.BurstBackoffSeconds);

            var newUntil = EditorApplication.timeSinceStartup + seconds;
            if (newUntil > _burstBackoffUntil)
            {
                _burstBackoffUntil = newUntil;
            }

            _burstReason = $"burst detected: {dirtyBatchCount} files in one batch, pausing {seconds}s";
            _nextRunAt = Math.Max(_nextRunAt, _burstBackoffUntil);

            if (settings.VerboseLogging)
            {
                AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] {_burstReason}");
            }

            PublishPending(IndexingDirtyTracker.Count);
        }

        /// <summary>
        /// Cancels the current background indexing task, if any.
        /// </summary>
        public static void CancelCurrentRun()
        {
            try
            {
                _cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Ignore stale token source during domain reload.
            }
        }

        private static bool HasPendingWork =>
            IndexingDirtyTracker.HasDirtyPaths || _fullIndexMissing || _startupReconcilePending;

        /// <summary>Dirty set changed: schedule after the quiet delay (does not reset failure backoff).</summary>
        private static void OnDirtyChanged()
        {
            if (!OptionalComponentManager.IsIndexingEnabled())
            {
                PublishDisabled(IndexingDirtyTracker.Count, ComponentDisabledReason);
                return;
            }

            var settings = GetSettings();
            if (!settings.AutoIndexEnabled || _sessionPaused)
            {
                PublishDisabled(IndexingDirtyTracker.Count, _sessionPaused ? "Auto-index paused for this editor session." : null);
                return;
            }

            if (_running)
            {
                return;
            }

            if (!IndexingDirtyTracker.HasDirtyPaths)
            {
                if (!HasPendingWork)
                {
                    PublishIdle();
                }

                return;
            }

            _nextRunAt = Math.Max(_nextRunAt, EditorApplication.timeSinceStartup + Math.Max(0, settings.QuietDelayMs) / 1000.0);
            PublishPending(IndexingDirtyTracker.Count);
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            // Entering play mode: stop the current batch; cancelled paths stay dirty and run after play mode.
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                CancelCurrentRun();
            }
        }

        private static void OnEditorUpdate()
        {
            if (_running)
            {
                PublishRunProgress();
                return;
            }

            if (!HasPendingWork || !OptionalComponentManager.IsIndexingEnabled())
            {
                return;
            }

            // Only the startup reconcile is pending: wait quietly until its delay has passed.
            if (!IndexingDirtyTracker.HasDirtyPaths && !_fullIndexMissing
                && EditorApplication.timeSinceStartup < _startupReconcileAt)
            {
                return;
            }

            // Events are still arriving (debounce window): wait for them to settle.
            if (IndexingDirtyTracker.IsSettling)
            {
                return;
            }

            var settings = GetSettings();
            if (!settings.AutoIndexEnabled || _sessionPaused)
            {
                PublishDisabled(IndexingDirtyTracker.Count, _sessionPaused ? "Auto-index paused for this editor session." : null);
                return;
            }

            if (settings.RespectIsCompiling && (EditorApplication.isCompiling || EditorApplication.isUpdating))
            {
                PublishPending(IndexingDirtyTracker.Count);
                return;
            }

            if (settings.RespectIsPlaying && EditorApplication.isPlayingOrWillChangePlaymode)
            {
                PublishPending(IndexingDirtyTracker.Count);
                return;
            }

            var now = EditorApplication.timeSinceStartup;
            if (_backoffUntil > now)
            {
                if (_consecutiveFailures >= Math.Max(1, settings.MaxConsecutiveFailures))
                {
                    PublishDisabled(IndexingDirtyTracker.Count, _lastError);
                }
                else
                {
                    PublishFailed(IndexingDirtyTracker.Count, _lastError);
                }

                return;
            }

            // v1.4.0 — burst backoff (higher priority than nextRunAt because it signals a hot spike)
            if (_burstBackoffUntil > now)
            {
                PublishPending(IndexingDirtyTracker.Count);
                return;
            }

            // Clear burst reason once the window has passed
            if (_burstBackoffUntil > 0 && _burstBackoffUntil <= now)
            {
                _burstBackoffUntil = 0;
                _burstReason = null;
            }

            if (now < _nextRunAt)
            {
                PublishPending(IndexingDirtyTracker.Count);
                return;
            }

            _ = RunOnceAsync(settings);
        }

        private static async Task RunOnceAsync(IndexingAutoSettings settings)
        {
            if (_running || !OptionalComponentManager.IsIndexingEnabled())
            {
                return;
            }

            _running = true;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            var snapshot = IndexingDirtyTracker.Snapshot(settings.MaxBatchFiles);

            // Dirty paths first; a missing full index outranks them (targeted runs cannot proceed without it);
            // the startup reconcile only runs once the dirty queue is empty.
            var reconcile = _fullIndexMissing || (snapshot.Count == 0 && _startupReconcilePending);
            if (!reconcile && snapshot.Count == 0)
            {
                _running = false;
                PublishIdle();
                return;
            }

            var settled = false;
            IndexRootStateStore stateStore = null;
            var priorStates = new Dictionary<int, IndexRootState>();

            try
            {
                _runProgress = null;
                _runDirtyCount = snapshot.Count;
                _runTotalHint = snapshot.Count;
                PublishRunning(snapshot.Count, 0, Math.Max(1, snapshot.Count), null);
                using (var store = IndexStoreFactory.CreateFromCurrent())
                {
                    if (store == null)
                    {
                        throw new InvalidOperationException("Index store is not available for current workspace.");
                    }

                    var maxFileSizeBytes = Math.Max(1, settings.MaxFileSizeKB) * 1024L;
                    var indexer = new CodebaseIndexer(store, maxFileSizeBytes);

                    // The store found too many corrupt lines on load: rebuild once per session
                    // instead of incrementally patching an index that is known to be incomplete.
                    if (!_rebuildAttempted && store is IIndexHealth health && health.RequiresRebuild)
                    {
                        _rebuildAttempted = true;
                        AgentCoreLog.Warning($"[AgentCore] {health.HealthMessage ?? "Index store is damaged."} Running a full rebuild.");
                        var rebuild = await indexer.RunFullIndexAsync(OnRunProgress, ct);
                        ct.ThrowIfCancellationRequested();
                        if (!rebuild.IsSuccess)
                        {
                            throw new InvalidOperationException(rebuild.ErrorMessage ?? "Full index rebuild failed.");
                        }

                        // The full rebuild already covered every dirty path in this snapshot
                        // and marked every enabled root Ready / Failed.
                        _fullIndexMissing = false;
                        _startupReconcilePending = false;
                    }
                    else if (reconcile)
                    {
                        // Whole-project mtime reconcile; falls back to a full rebuild when no complete full index exists.
                        // CodebaseIndexer writes the per-root states itself.
                        if (settings.VerboseLogging)
                        {
                            AgentCoreLog.Info(_fullIndexMissing
                                ? "[AgentCore] No complete full index: running a background full index."
                                : "[AgentCore] Running the startup index reconcile.");
                        }

                        var result = await indexer.RunIncrementalIndexAsync(OnRunProgress, ct);
                        ct.ThrowIfCancellationRequested();
                        if (!result.IsSuccess)
                        {
                            throw new InvalidOperationException(result.ErrorMessage ?? "Background index reconcile failed.");
                        }

                        // The reconcile scanned every enabled root, so it covers the dirty paths of this snapshot.
                        _fullIndexMissing = false;
                        _startupReconcilePending = false;
                    }
                    else
                    {
                        // v1.4.0 — resolve workspace / roots up-front so we can:
                        //   1. filter dirty paths by IndexRootPriority (skip OnDemand)
                        //   2. persist per-root state after the run completes
                        var workspace = IndexWorkspaceResolver.ResolveFromCurrent();
                        var workspaceId = await store.UpsertWorkspaceAsync(workspace, ct);
                        workspace = CloneWorkspaceWithId(workspace, workspaceId);
                        var rootResolver = new IndexRootResolver();
                        var allRoots = rootResolver.Resolve(workspace);
                        var enabledRoots = allRoots.Where(r => r.IsEnabled).ToList();
                        foreach (var r in enabledRoots)
                        {
                            ct.ThrowIfCancellationRequested();
                            r.Id = await store.UpsertRootAsync(workspaceId, r, ct);
                            r.WorkspaceId = workspaceId;
                        }

                        var (filteredChanged, filteredDeleted, skippedOnDemand, affectedRoots) =
                            FilterByPriority(snapshot, enabledRoots);

                        if (settings.VerboseLogging && skippedOnDemand > 0)
                        {
                            AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] Skipped {skippedOnDemand} dirty paths belonging to OnDemand roots.");
                        }

                        if (filteredChanged.Count == 0 && filteredDeleted.Count == 0)
                        {
                            // All dirty paths belonged to OnDemand roots — drop them from the queue
                            // so we don't spin. LLM/user can trigger index_scope manually.
                            IndexingDirtyTracker.MarkProcessed(snapshot);
                            _consecutiveFailures = 0;
                            _lastError = null;
                            PublishIdle();
                            return;
                        }

                        stateStore = new IndexRootStateStore(store, workspaceId);
                        foreach (var rootId in affectedRoots)
                        {
                            priorStates[rootId] = (await stateStore.LoadAsync(rootId, ct)).State;
                            await stateStore.SetStateAsync(rootId, IndexRootState.Indexing, ct);
                        }

                        _runTotalHint = filteredChanged.Count + filteredDeleted.Count;
                        var result = await indexer.RunTargetedIncrementalAsync(
                            filteredChanged,
                            filteredDeleted,
                            Math.Max(1, settings.YieldEveryNFiles),
                            OnRunProgress,
                            ct);

                        // Cancelled (domain reload / pause / play mode): not a failure, the paths stay dirty.
                        ct.ThrowIfCancellationRequested();

                        if (!result.IsSuccess)
                        {
                            if (string.Equals(result.ErrorMessage, "NO_FULL_INDEX", StringComparison.Ordinal))
                            {
                                // No usable full index (never built, cleared, or an interrupted rebuild):
                                // schedule a background full index right away instead of polling every 30s.
                                // The roots get their previous state back in finally.
                                _fullIndexMissing = true;
                                _lastError = null;
                                _nextRunAt = 0;
                                PublishPending(IndexingDirtyTracker.Count);
                                return;
                            }

                            // Persist failure on affected roots so LLM/user can see which roots broke
                            foreach (var rootId in affectedRoots)
                            {
                                try { await stateStore.MarkFailedAsync(rootId, result.ErrorMessage, CancellationToken.None); }
                                catch { /* best-effort */ }
                            }

                            settled = true;
                            throw new InvalidOperationException(result.ErrorMessage ?? "Background incremental indexing failed.");
                        }

                        // v1.4.0 — refresh per-root counts and mark Ready
                        foreach (var rootId in affectedRoots)
                        {
                            try { await stateStore.RefreshAndMarkReadyAsync(rootId, CancellationToken.None); }
                            catch (Exception ex)
                            {
                                AgentCoreLog.Warning($"[AgentCore] Failed to refresh state for root {rootId}: {ex.Message}");
                            }
                        }

                        settled = true;
                        KeepFailedPathsDirty(snapshot, result.FailedPaths);
                    }
                }

                IndexingDirtyTracker.MarkProcessed(snapshot);
                _consecutiveFailures = 0;
                _lastError = null;
                _lastSuccessAt = DateTime.UtcNow;
                _backoffUntil = 0;

                if (settings.VerboseLogging)
                {
                    AgentCore.Editor.Utils.AgentCoreLog.Info(reconcile
                        ? "[AgentCore] Background index reconcile finished."
                        : $"[AgentCore] Background indexed {snapshot.Count} files.");
                }

                if (IndexingDirtyTracker.HasDirtyPaths)
                {
                    _nextRunAt = EditorApplication.timeSinceStartup + Math.Max(0, settings.QuietDelayMs) / 1000.0;
                    PublishPending(IndexingDirtyTracker.Count);
                }
                else if (HasPendingWork)
                {
                    PublishPending(0);
                }
                else
                {
                    PublishIdle();
                }
            }
            catch (OperationCanceledException)
            {
                if (OptionalComponentManager.IsIndexingEnabled())
                    PublishPending(IndexingDirtyTracker.Count);
                else
                    PublishDisabled(IndexingDirtyTracker.Count, ComponentDisabledReason);
            }
            catch (Exception) when (!OptionalComponentManager.IsIndexingEnabled())
            {
                // Disabling releases the index store mid-run; the resulting errors are not indexing failures.
                PublishDisabled(IndexingDirtyTracker.Count, ComponentDisabledReason);
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _lastError = ex.Message;
                var maxFailures = Math.Max(1, settings.MaxConsecutiveFailures);
                if (_consecutiveFailures >= maxFailures)
                {
                    // Long backoff instead of stopping for the session; RequestRun / resume clears it earlier.
                    _backoffUntil = EditorApplication.timeSinceStartup + DisabledRetrySeconds;
                    PublishDisabled(IndexingDirtyTracker.Count, _lastError);
                    AgentCoreLog.Error($"[AgentCore] Background indexing failed {_consecutiveFailures} times in a row, retrying in {DisabledRetrySeconds / 60} min: {_lastError}");
                }
                else
                {
                    var index = Math.Min(_consecutiveFailures - 1, BackoffSeconds.Length - 1);
                    _backoffUntil = EditorApplication.timeSinceStartup + BackoffSeconds[index];
                    PublishFailed(IndexingDirtyTracker.Count, _lastError);
                    AgentCoreLog.Warning($"[AgentCore] Background indexing failed, retry in {BackoffSeconds[index]}s: {_lastError}");
                }
            }
            finally
            {
                // NO_FULL_INDEX / cancel / unexpected exit: give the roots their state back instead of leaving them
                // stuck in Indexing. A root that was Ready has unprocessed dirty files now, so it becomes Stale.
                if (!settled && stateStore != null)
                {
                    foreach (var kvp in priorStates)
                    {
                        var restore = kvp.Value == IndexRootState.Ready || kvp.Value == IndexRootState.Indexing
                            ? IndexRootState.Stale
                            : kvp.Value;
                        try { await stateStore.SetStateAsync(kvp.Key, restore, CancellationToken.None); }
                        catch (Exception ex)
                        {
                            AgentCoreLog.Warning($"[AgentCore] Failed to restore state for root {kvp.Key}: {ex.Message}");
                        }
                    }
                }

                _runProgress = null;
                _running = false;
            }
        }

        /// <summary>
        /// Leaves paths that failed to index in the dirty queue (their old symbols were kept) so a later run retries
        /// them. A path that keeps failing is dropped after <see cref="MaxPathAttempts"/> attempts; the next edit re-adds it.
        /// </summary>
        private static void KeepFailedPathsDirty(IndexingDirtySnapshot snapshot, IReadOnlyList<string> failedPaths)
        {
            var failed = new HashSet<string>(failedPaths ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var path in snapshot.ChangedPaths)
            {
                if (!failed.Contains(path))
                {
                    _pathFailures.Remove(path);
                    continue;
                }

                _pathFailures.TryGetValue(path, out var attempts);
                attempts++;
                if (attempts < MaxPathAttempts)
                {
                    _pathFailures[path] = attempts;
                    snapshot.Versions?.Remove(path);
                }
                else
                {
                    _pathFailures.Remove(path);
                    AgentCoreLog.Warning($"[AgentCore] Indexing '{path}' failed {attempts} times, keeping its previous index entry until it changes again.");
                }
            }
        }

        /// <summary>Progress callback from the indexer (pool thread): only records, OnEditorUpdate publishes.</summary>
        private static void OnRunProgress(IndexingProgress progress)
        {
            _runProgress = progress;
        }

        private static void PublishRunProgress()
        {
            var progress = _runProgress;
            var now = EditorApplication.timeSinceStartup;
            if (progress == null || now - _lastProgressPublishAt < ProgressPublishIntervalSeconds)
            {
                return;
            }

            _lastProgressPublishAt = now;
            PublishRunning(
                _runDirtyCount,
                Math.Max(0, progress.ProcessedFiles),
                Math.Max(Math.Max(1, _runTotalHint), progress.TotalFiles),
                progress.CurrentFile);
        }

        private static IndexingAutoSettings GetSettings()
        {
            return IndexingSettings.instance.EffectiveAutoSettings;
        }

        /// <summary>
        /// v1.4.0 — Split a dirty snapshot by <see cref="IndexRootPriority"/>. Paths owned by
        /// <c>OnDemand</c> roots are dropped from the batch (they will still be marked processed
        /// so they don't accumulate; the user/LLM must invoke index_scope to catch them up).
        /// </summary>
        /// <returns>
        /// Tuple of (paths kept for indexing, paths deleted for indexing, count skipped, affected root ids).
        /// </returns>
        private static (
            IReadOnlyList<string> Changed,
            IReadOnlyList<string> Deleted,
            int SkippedOnDemand,
            HashSet<int> AffectedRootIds
        ) FilterByPriority(IndexingDirtySnapshot snapshot, IReadOnlyList<IndexRoot> enabledRoots)
        {
            var kept = new List<string>(snapshot.ChangedPaths.Count);
            var keptDeleted = new List<string>(snapshot.DeletedPaths.Count);
            var affectedRoots = new HashSet<int>();
            var skipped = 0;

            foreach (var path in snapshot.ChangedPaths)
            {
                var owner = FindOwnerRoot(path, enabledRoots);
                if (owner == null)
                {
                    // No owning root — CodebaseIndexer will skip it too. Keep in list so it can be
                    // marked processed downstream.
                    kept.Add(path);
                    continue;
                }

                if (owner.Priority == IndexRootPriority.OnDemand)
                {
                    skipped++;
                    continue;
                }

                affectedRoots.Add(owner.Id);
                kept.Add(path);
            }

            foreach (var path in snapshot.DeletedPaths)
            {
                var owner = FindOwnerRoot(path, enabledRoots);
                if (owner == null || owner.Priority != IndexRootPriority.OnDemand)
                {
                    if (owner != null) affectedRoots.Add(owner.Id);
                    keptDeleted.Add(path);
                }
                else
                {
                    skipped++;
                }
            }

            return (kept, keptDeleted, skipped, affectedRoots);
        }

        /// <summary>
        /// v1.4.0 — Locate the enabled root that owns a given path (longest-prefix match).
        /// </summary>
        private static IndexRoot FindOwnerRoot(string filePath, IReadOnlyList<IndexRoot> enabledRoots)
        {
            if (string.IsNullOrEmpty(filePath) || enabledRoots == null || enabledRoots.Count == 0)
            {
                return null;
            }

            var normalized = filePath.Replace('\\', '/');
            IndexRoot best = null;
            int bestLen = -1;
            foreach (var root in enabledRoots)
            {
                if (root == null || string.IsNullOrEmpty(root.RootPath)) continue;
                var rootPath = root.RootPath.Replace('\\', '/').TrimEnd('/');
                if (normalized.StartsWith(rootPath + "/", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(normalized, rootPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (rootPath.Length > bestLen)
                    {
                        best = root;
                        bestLen = rootPath.Length;
                    }
                }
            }

            return best;
        }

        /// <summary>
        /// v1.4.0 — Clone an <see cref="IndexWorkspace"/> substituting the resolved Id.
        /// </summary>
        private static IndexWorkspace CloneWorkspaceWithId(IndexWorkspace src, int id)
        {
            return new IndexWorkspace
            {
                Id = id,
                Fingerprint = src.Fingerprint,
                WorkspaceRoot = src.WorkspaceRoot,
                UnityRoot = src.UnityRoot,
                UnityRootRelativePath = src.UnityRootRelativePath,
                DisplayName = src.DisplayName,
                VcsType = src.VcsType,
                VcsRootPath = src.VcsRootPath,
                VcsUrl = src.VcsUrl,
                RepositoryRoot = src.RepositoryRoot,
                BranchId = src.BranchId,
                Revision = src.Revision,
            };
        }

        private static void PublishIdle()
        {
            IndexingStatusBus.Publish(new IndexingStatusSnapshot
            {
                State = IndexingBackgroundState.Idle,
                DirtyFileCount = 0,
                LastError = _lastError,
                LastSuccessAt = _lastSuccessAt,
                ConsecutiveFailures = _consecutiveFailures
            });
        }

        private static void PublishPending(int dirtyCount)
        {
            var (nextRunAt, reasonPaused) = ComputePauseState();
            IndexingStatusBus.Publish(new IndexingStatusSnapshot
            {
                State = IndexingBackgroundState.Pending,
                DirtyFileCount = dirtyCount,
                LastError = _lastError,
                LastSuccessAt = _lastSuccessAt,
                ConsecutiveFailures = _consecutiveFailures,
                NextRunAt = nextRunAt,
                ReasonPaused = reasonPaused
            });
        }

        /// <summary>
        /// v1.4.0 — Compute the effective pause state for snapshot publishing.
        /// Returns the earliest future run time and a human-readable reason (burst / backoff / quiet).
        /// </summary>
        private static (DateTime? nextRunAt, string reason) ComputePauseState()
        {
            var now = EditorApplication.timeSinceStartup;

            // Priority: burst > failure backoff > quiet delay
            if (_burstBackoffUntil > now)
            {
                var remaining = _burstBackoffUntil - now;
                return (DateTime.UtcNow.AddSeconds(remaining), _burstReason);
            }

            if (_backoffUntil > now)
            {
                var remaining = _backoffUntil - now;
                return (DateTime.UtcNow.AddSeconds(remaining), $"failure backoff: retry in {remaining:F0}s");
            }

            if (_nextRunAt > now)
            {
                var remaining = _nextRunAt - now;
                return (DateTime.UtcNow.AddSeconds(remaining), $"quiet delay: run in {remaining:F0}s");
            }

            return (null, null);
        }

        private static void PublishRunning(int dirtyCount, int processedFiles, int totalFiles, string currentFile)
        {
            IndexingStatusBus.Publish(new IndexingStatusSnapshot
            {
                State = IndexingBackgroundState.Running,
                DirtyFileCount = dirtyCount,
                ProcessedFiles = processedFiles,
                TotalFiles = totalFiles,
                CurrentFile = currentFile,
                LastError = _lastError,
                LastSuccessAt = _lastSuccessAt,
                ConsecutiveFailures = _consecutiveFailures
            });
        }

        private static void PublishFailed(int dirtyCount, string error)
        {
            IndexingStatusBus.Publish(new IndexingStatusSnapshot
            {
                State = IndexingBackgroundState.Failed,
                DirtyFileCount = dirtyCount,
                LastError = error,
                LastSuccessAt = _lastSuccessAt,
                ConsecutiveFailures = _consecutiveFailures
            });
        }

        private static void PublishDisabled(int dirtyCount, string error)
        {
            IndexingStatusBus.Publish(new IndexingStatusSnapshot
            {
                State = IndexingBackgroundState.Disabled,
                DirtyFileCount = dirtyCount,
                LastError = error,
                LastSuccessAt = _lastSuccessAt,
                ConsecutiveFailures = _consecutiveFailures
            });
        }
    }
}
