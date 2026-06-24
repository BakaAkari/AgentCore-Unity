using System;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Components.Indexing.Config;
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

                    var maxFileSizeBytes = Math.Max(1, settings.MaxFileSizeKB) * 1024L;
                    var indexer = new CodebaseIndexer(store, maxFileSizeBytes);
                    var result = await indexer.RunTargetedIncrementalAsync(
                        snapshot.ChangedPaths,
                        snapshot.DeletedPaths,
                        Math.Max(1, settings.YieldEveryNFiles),
                        progress => PublishRunning(
                            snapshot.Count,
                            Math.Max(0, progress.ProcessedFiles),
                            Math.Max(snapshot.Count, progress.TotalFiles),
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

                        throw new InvalidOperationException(result.ErrorMessage ?? "Background incremental indexing failed.");
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
            IndexingStatusBus.Publish(new IndexingStatusSnapshot
            {
                State = IndexingBackgroundState.Pending,
                DirtyFileCount = dirtyCount,
                LastError = _lastError,
                LastSuccessAt = _lastSuccessAt,
                ConsecutiveFailures = _consecutiveFailures
            });
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
