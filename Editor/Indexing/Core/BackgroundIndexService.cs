using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Components.Indexing.Config;
using AgentCore.Editor.Components.Indexing.Models;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Components.Indexing.Core
{
    /// <summary>
    /// Coordinates background incremental indexing from accumulated dirty paths.
    /// </summary>
    [InitializeOnLoad]
    public static class BackgroundIndexService
    {
        private static readonly int[] BackoffSeconds = { 5, 30, 120 };
        private static CancellationTokenSource _cts;
        private static bool _running;
        private static double _nextRunAt;
        private static double _backoffUntil;
        private static int _consecutiveFailures;
        private static string _lastError;
        private static DateTime? _lastSuccessAt;
        private static bool _sessionPaused;

        // v1.4.0 — burst backoff (set by IndexingDirtyTracker when a large batch arrives)
        private static double _burstBackoffUntil;
        private static string _burstReason;

        static BackgroundIndexService()
        {
            IndexingDirtyTracker.DirtyChanged += RequestRun;
            AssemblyReloadEvents.beforeAssemblyReload += CancelCurrentRun;
            EditorApplication.update += OnEditorUpdate;
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
        /// Requests a background indexing pass after the configured quiet delay.
        /// </summary>
        public static void RequestRun()
        {
            var settings = GetSettings();
            if (!settings.AutoIndexEnabled || _sessionPaused)
            {
                PublishDisabled(IndexingDirtyTracker.Count, _sessionPaused ? "Auto-index paused for this editor session." : null);
                return;
            }

            if (!IndexingDirtyTracker.HasDirtyPaths)
            {
                PublishIdle();
                return;
            }

            _nextRunAt = EditorApplication.timeSinceStartup + Math.Max(0, settings.QuietDelayMs) / 1000.0;
            PublishPending(IndexingDirtyTracker.Count);
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
                Debug.Log($"[AgentCore] {_burstReason}");
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

        private static void OnEditorUpdate()
        {
            if (_running || !IndexingDirtyTracker.HasDirtyPaths)
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

            if (_consecutiveFailures >= Math.Max(1, settings.MaxConsecutiveFailures))
            {
                PublishDisabled(IndexingDirtyTracker.Count, _lastError);
                return;
            }

            var now = EditorApplication.timeSinceStartup;
            if (_backoffUntil > now)
            {
                PublishFailed(IndexingDirtyTracker.Count, _lastError);
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

            if (_nextRunAt <= 0 || now < _nextRunAt)
            {
                PublishPending(IndexingDirtyTracker.Count);
                return;
            }

            _ = RunOnceAsync(settings);
        }

        private static async Task RunOnceAsync(IndexingAutoSettings settings)
        {
            if (_running)
            {
                return;
            }

            _running = true;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            var snapshot = IndexingDirtyTracker.Snapshot(settings.MaxBatchFiles);

            if (snapshot.Count == 0)
            {
                _running = false;
                PublishIdle();
                return;
            }

            try
            {
                PublishRunning(snapshot.Count, 0, snapshot.Count, null);
                using (var store = IndexStoreFactory.CreateFromCurrent())
                {
                    if (store == null)
                    {
                        throw new InvalidOperationException("Index store is not available for current workspace.");
                    }

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
                        Debug.Log($"[AgentCore] Skipped {skippedOnDemand} dirty paths belonging to OnDemand roots.");
                    }

                    if (filteredChanged.Count == 0 && filteredDeleted.Count == 0)
                    {
                        // All dirty paths belonged to OnDemand roots — drop them from the queue
                        // so we don't spin. LLM/user can trigger index_scope manually.
                        IndexingDirtyTracker.MarkProcessed(snapshot.ChangedPaths, snapshot.DeletedPaths);
                        _running = false;
                        _consecutiveFailures = 0;
                        _lastError = null;
                        PublishIdle();
                        return;
                    }

                    var stateStore = new IndexRootStateStore(store, workspaceId);
                    foreach (var rootId in affectedRoots)
                    {
                        await stateStore.SetStateAsync(rootId, IndexRootState.Indexing, ct);
                    }

                    var maxFileSizeBytes = Math.Max(1, settings.MaxFileSizeKB) * 1024L;
                    var indexer = new CodebaseIndexer(store, maxFileSizeBytes);
                    var result = await indexer.RunTargetedIncrementalAsync(
                        filteredChanged,
                        filteredDeleted,
                        Math.Max(1, settings.YieldEveryNFiles),
                        progress => PublishRunning(
                            snapshot.Count,
                            Math.Max(0, progress.ProcessedFiles),
                            Math.Max(filteredChanged.Count + filteredDeleted.Count, progress.TotalFiles),
                            progress.CurrentFile),
                        ct);

                    if (!result.IsSuccess)
                    {
                        if (string.Equals(result.ErrorMessage, "NO_FULL_INDEX", StringComparison.Ordinal))
                        {
                            _lastError = "Full index is required before background incremental indexing can run.";
                            _nextRunAt = EditorApplication.timeSinceStartup + 30;
                            PublishPending(IndexingDirtyTracker.Count);
                            return;
                        }

                        // Persist failure on affected roots so LLM/user can see which roots broke
                        foreach (var rootId in affectedRoots)
                        {
                            try { await stateStore.MarkFailedAsync(rootId, result.ErrorMessage, ct); }
                            catch { /* best-effort */ }
                        }

                        throw new InvalidOperationException(result.ErrorMessage ?? "Background incremental indexing failed.");
                    }

                    // v1.4.0 — refresh per-root counts and mark Ready
                    foreach (var rootId in affectedRoots)
                    {
                        try { await stateStore.RefreshAndMarkReadyAsync(rootId, ct); }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[AgentCore] Failed to refresh state for root {rootId}: {ex.Message}");
                        }
                    }
                }

                IndexingDirtyTracker.MarkProcessed(snapshot.ChangedPaths, snapshot.DeletedPaths);
                _consecutiveFailures = 0;
                _lastError = null;
                _lastSuccessAt = DateTime.UtcNow;
                _backoffUntil = 0;

                if (settings.VerboseLogging)
                {
                    Debug.Log($"[AgentCore] Background indexed {snapshot.Count} files.");
                }

                if (IndexingDirtyTracker.HasDirtyPaths)
                {
                    _nextRunAt = EditorApplication.timeSinceStartup + Math.Max(0, settings.QuietDelayMs) / 1000.0;
                    PublishPending(IndexingDirtyTracker.Count);
                }
                else
                {
                    PublishIdle();
                }
            }
            catch (OperationCanceledException)
            {
                PublishPending(IndexingDirtyTracker.Count);
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _lastError = ex.Message;
                var maxFailures = Math.Max(1, settings.MaxConsecutiveFailures);
                if (_consecutiveFailures >= maxFailures)
                {
                    PublishDisabled(IndexingDirtyTracker.Count, _lastError);
                    Debug.LogError($"[AgentCore] Background indexing disabled after {_consecutiveFailures} consecutive failures: {_lastError}");
                }
                else
                {
                    var index = Math.Min(_consecutiveFailures - 1, BackoffSeconds.Length - 1);
                    _backoffUntil = EditorApplication.timeSinceStartup + BackoffSeconds[index];
                    PublishFailed(IndexingDirtyTracker.Count, _lastError);
                    Debug.LogWarning($"[AgentCore] Background indexing failed, retry in {BackoffSeconds[index]}s: {_lastError}");
                }
            }
            finally
            {
                _running = false;
            }
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
