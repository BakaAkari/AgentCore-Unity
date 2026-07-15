using System;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Components.VCS.Config;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Components.VCS.Tools
{
    /// <summary>
    /// Monitors remote VCS update status and exposes it to VCS UI surfaces.
    /// </summary>
    [InitializeOnLoad]
    public static class VcsRemoteStatusMonitor
    {
        private static readonly object _lock = new object();
        private static VcsSyncStatus _lastStatus;
        private static DateTime _lastCheckedUtc = DateTime.UtcNow;
        private static bool _isChecking;
        private static bool _isSyncing;
        private static string _lastError;
        private static CancellationTokenSource _cts;

        /// <summary>
        /// Occurs when remote status changes or a check completes.
        /// </summary>
        public static event Action<VcsSyncStatus> StatusChanged;

        /// <summary>
        /// Gets the latest known remote sync status.
        /// </summary>
        public static VcsSyncStatus LastStatus
        {
            get
            {
                lock (_lock)
                {
                    return _lastStatus;
                }
            }
        }

        /// <summary>
        /// Gets whether a remote status check is currently running.
        /// </summary>
        public static bool IsChecking => _isChecking;

        /// <summary>
        /// Gets whether a sync/update operation is currently running.
        /// </summary>
        public static bool IsSyncing => _isSyncing;

        /// <summary>
        /// Gets the last remote check error message.
        /// </summary>
        public static string LastError => _lastError;

        static VcsRemoteStatusMonitor()
        {
            EditorApplication.update += OnEditorUpdate;
        }

        /// <summary>
        /// Requests an immediate remote status check.
        /// </summary>
        public static void RequestCheck()
        {
            _ = CheckRemoteStatusAsync(false);
        }

        /// <summary>
        /// Checks remote status and returns the latest result.
        /// </summary>
        public static async Task<VcsSyncStatus> CheckRemoteStatusAsync(bool force, CancellationToken ct = default)
        {
            if (_isChecking)
                return LastStatus;

            if (!force && !ShouldRunPeriodicCheck())
                return LastStatus;

            var adapter = CreateAdapter();
            if (adapter == null)
                return null;

            _isChecking = true;
            _lastError = null;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            try
            {
                var status = await adapter.GetSyncStatusAsync(_cts.Token);
                lock (_lock)
                {
                    _lastStatus = status;
                    _lastCheckedUtc = DateTime.UtcNow;
                }

                if (status != null && !status.Success)
                    _lastError = status.ErrorMessage;

                NotifyStatusChanged(status);
                return status;
            }
            catch (OperationCanceledException)
            {
                return LastStatus;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Debug.LogWarning($"[Version Control] Remote status check failed: {ex.Message}");
                return LastStatus;
            }
            finally
            {
                _isChecking = false;
            }
        }

        /// <summary>
        /// Runs a user-confirmed sync/update operation and refreshes remote status afterwards.
        /// </summary>
        public static async Task<VcsOperationResult> SyncAsync(CancellationToken ct = default)
        {
            var adapter = CreateAdapter();
            if (adapter == null)
            {
                return new VcsOperationResult
                {
                    Success = false,
                    ErrorMessage = "No version control system detected or command not available.",
                    Message = "No version control system detected or command not available."
                };
            }

            _isSyncing = true;
            try
            {
                var result = await adapter.SyncAsync(ct);
                await CheckRemoteStatusAsync(true, ct);
                return result;
            }
            finally
            {
                _isSyncing = false;
            }
        }

        private static void OnEditorUpdate()
        {
            if (!ShouldRunPeriodicCheck())
                return;

            _ = CheckRemoteStatusAsync(false);
        }

        private static bool ShouldRunPeriodicCheck()
        {
            if (_isChecking || _isSyncing)
                return false;

            var interval = TimeSpan.FromMinutes(VcsSettings.RemoteStatusCheckIntervalMinutes);
            return DateTime.UtcNow - _lastCheckedUtc >= interval;
        }

        private static IVcsAdapter CreateAdapter()
        {
            var vcsType = VcsDetector.DetectVcs();
            if (vcsType == VcsType.None)
                return null;

            var rootPath = VcsDetector.GetVcsRootPath();
            IVcsAdapter adapter = vcsType switch
            {
                VcsType.Svn => new SvnAdapter(rootPath),
                VcsType.Perforce => new PerforceAdapter(rootPath),
                VcsType.Git => new GitAdapter(rootPath),
                _ => null
            };

            if (adapter == null || !adapter.IsAvailable())
                return null;

            return adapter;
        }

        private static void NotifyStatusChanged(VcsSyncStatus status)
        {
            EditorApplication.delayCall += () =>
            {
                StatusChanged?.Invoke(status);
            };
        }
    }
}
