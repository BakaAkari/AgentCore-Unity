using System;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Components.VCS.Config;
using UnityEditor;
using UnityEngine;
using AgentCore.Editor.Utils;

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

        // v1.12.0-alpha.6 (#6): bool → volatile int + Interlocked.CompareExchange，
        // 消除 "检查判定 vs 设置为真" 之间的竞态（两个并发调用都能通过 IsChecking 判定后重复运行）。
        // 0 = 空闲, 1 = 运行中。
        private static int _isCheckingFlag;
        private static int _isSyncingFlag;

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
        public static bool IsChecking => Volatile.Read(ref _isCheckingFlag) != 0;

        /// <summary>
        /// Gets whether a sync/update operation is currently running.
        /// </summary>
        public static bool IsSyncing => Volatile.Read(ref _isSyncingFlag) != 0;

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
            // #6: 原子判定 "空闲→运行中"。若旧值非 0 说明已有检查在跑，直接返回。
            if (Interlocked.CompareExchange(ref _isCheckingFlag, 1, 0) != 0)
                return LastStatus;

            // 到这里说明本调用拿到了独占运行权，出错也要还回去（finally）。
            try
            {
                if (!force && !ShouldRunPeriodicCheck())
                    return LastStatus;

                var adapter = CreateAdapter();
                if (adapter == null)
                    return null;

                _lastError = null;

                // #6: Interlocked.Exchange 原子换 _cts，返回旧引用；旧的独占后再 Cancel/Dispose，
                // 避免两个线程都在读同一个 _cts 时一个 Dispose 另一个 Cancel 抛 ObjectDisposedException。
                var newCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var oldCts = Interlocked.Exchange(ref _cts, newCts);
                if (oldCts != null)
                {
                    try { oldCts.Cancel(); } catch (ObjectDisposedException) { /* 已被别处 dispose，忽略 */ }
                    try { oldCts.Dispose(); } catch (ObjectDisposedException) { /* 幂等 */ }
                }

                try
                {
                    var status = await adapter.GetSyncStatusAsync(newCts.Token);
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
                    AgentCoreLog.Warning($"[Version Control] Remote status check failed: {ex.Message}");
                    return LastStatus;
                }
            }
            finally
            {
                // #6: 无论正常/异常路径都要把 flag 归 0，让下一次调用能继续。
                Interlocked.Exchange(ref _isCheckingFlag, 0);
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

            // #6: 同 CheckRemoteStatusAsync 的 flag 处理；SyncAsync 拿不到独占运行权则直接返回失败。
            if (Interlocked.CompareExchange(ref _isSyncingFlag, 1, 0) != 0)
            {
                return new VcsOperationResult
                {
                    Success = false,
                    ErrorMessage = "Another sync operation is already in progress.",
                    Message = "Another sync operation is already in progress."
                };
            }

            try
            {
                var result = await adapter.SyncAsync(ct);
                await CheckRemoteStatusAsync(true, ct);
                return result;
            }
            finally
            {
                Interlocked.Exchange(ref _isSyncingFlag, 0);
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
            // #6: 用 Volatile.Read 而不是直接读 int 字段，确保跨线程可见性。
            if (Volatile.Read(ref _isCheckingFlag) != 0 || Volatile.Read(ref _isSyncingFlag) != 0)
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
