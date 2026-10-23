using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Components.VCS.Config;
using UnityEditor;
using UnityEngine;
using AgentCore.Editor.Utils;
using AgentCore.Editor.Workspace.Resolution;

namespace AgentCore.Editor.Components.VCS.Tools
{
    /// <summary>
    /// Monitors remote VCS update status and exposes it to VCS UI surfaces.
    /// <para>
    /// 远端检查（git fetch / svn status -u / p4 sync -n）只在两种情况下运行：
    /// 定时器（<see cref="VcsSettings.RemoteStatusCheckIntervalMinutes"/>）和手动触发（Check Remote / get_sync_status）。
    /// 面板刷新不再触发 fetch。检查在线程池执行，主线程只接收结果。
    /// 最近一次检查 / 成功时间与结果摘要写入 SessionState，域重载后不丢失、也不会立刻重新 fetch。
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    public static class VcsRemoteStatusMonitor
    {
        private const string SessionKeyPrefix = "AgentCore.VCS.RemoteStatus.";
        private const string LastCheckedKey = SessionKeyPrefix + "LastCheckedUtc";
        private const string LastSuccessKey = SessionKeyPrefix + "LastSuccessUtc";
        private const string LastStatusKey = SessionKeyPrefix + "LastStatus";

        /// <summary>会话内首次检查的延迟：避开编辑器启动 / 域重载高峰。</summary>
        private static readonly TimeSpan InitialCheckDelay = TimeSpan.FromMinutes(1);

        private static readonly object _lock = new object();
        private static VcsSyncStatus _lastStatus;
        private static DateTime _lastCheckedUtc;
        private static DateTime? _lastSuccessUtc;
        private static DateTime _nextCheckUtc;

        // v1.12.0-alpha.6 (#6): bool → volatile int + Interlocked.CompareExchange，
        // 消除 "检查判定 vs 设置为真" 之间的竞态（两个并发调用都能通过 IsChecking 判定后重复运行）。
        // 0 = 空闲, 1 = 运行中。
        private static int _isCheckingFlag;
        private static int _isSyncingFlag;

        private static string _lastError;

        /// <summary>连续失败次数（离线 / 认证失败 / 命令缺失），用于退避与"只记录一次警告"。受 _lock 保护。</summary>
        private static int _consecutiveFailures;
        private static CancellationTokenSource _cts;

        /// <summary>
        /// Occurs when remote status changes or a check completes (always raised on the main thread).
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
        /// 最近一次远端检查完成的时间（UTC，成功或失败均计）；本会话从未检查过时为 null。
        /// </summary>
        public static DateTime? LastCheckedUtc
        {
            get
            {
                lock (_lock)
                {
                    return _lastCheckedUtc == DateTime.MinValue ? (DateTime?)null : _lastCheckedUtc;
                }
            }
        }

        /// <summary>
        /// 最近一次成功获取远端状态（即成功 fetch）的时间（UTC）；从未成功时为 null。
        /// </summary>
        public static DateTime? LastFetchUtc
        {
            get
            {
                lock (_lock)
                {
                    return _lastSuccessUtc;
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
            RestoreFromSession();
            EditorApplication.update += OnEditorUpdate;
        }

        /// <summary>
        /// Requests an immediate remote status check (runs in the background, does not wait).
        /// </summary>
        public static void RequestCheck()
        {
            _ = CheckRemoteStatusAsync(true);
        }

        /// <summary>
        /// 格式化最近检查时间，供 UI 显示，例如 "Last checked 14:05 (3 min ago)"。
        /// </summary>
        public static string FormatLastChecked()
        {
            if (IsChecking)
                return "Checking remote...";

            var lastChecked = LastCheckedUtc;
            if (!lastChecked.HasValue)
                return "Remote not checked yet";

            var text = "Last checked " + FormatTime(lastChecked.Value);
            var lastFetch = LastFetchUtc;
            if (lastFetch.HasValue && lastFetch.Value < lastChecked.Value)
                text += " · last successful " + FormatTime(lastFetch.Value);
            else if (!lastFetch.HasValue)
                text += " · never succeeded";
            return text;
        }

        private static string FormatTime(DateTime utc)
        {
            var local = utc.ToLocalTime();
            var ago = DateTime.UtcNow - utc;
            string agoText;
            if (ago.TotalMinutes < 1)
                agoText = "just now";
            else if (ago.TotalHours < 1)
                agoText = $"{(int)ago.TotalMinutes} min ago";
            else if (ago.TotalDays < 1)
                agoText = $"{(int)ago.TotalHours} h ago";
            else
                agoText = $"{(int)ago.TotalDays} d ago";

            var clock = local.Date == DateTime.Now.Date
                ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
                : local.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
            return $"{clock} ({agoText})";
        }

        /// <summary>
        /// Checks remote status and returns the latest result.
        /// </summary>
        /// <param name="force">true = 立即检查（手动触发）；false = 仅在定时间隔到期时检查。</param>
        public static async Task<VcsSyncStatus> CheckRemoteStatusAsync(bool force, CancellationToken ct = default)
        {
            // 先判定定时间隔，再抢运行权（旧实现先抢运行权再判定，导致定时检查永远被自己的 flag 拦下）
            if (!force && !IsPeriodicCheckDue())
                return LastStatus;

            // #6: 原子判定 "空闲→运行中"。若旧值非 0 说明已有检查在跑，直接返回。
            if (Interlocked.CompareExchange(ref _isCheckingFlag, 1, 0) != 0)
                return LastStatus;

            // 到这里说明本调用拿到了独占运行权，出错也要还回去（finally）。
            try
            {
                // 无论本次结果如何，都推迟下一次定时检查，避免失败时每帧重试
                lock (_lock)
                    _nextCheckUtc = DateTime.UtcNow + TimeSpan.FromMinutes(VcsSettings.RemoteStatusCheckIntervalMinutes);

                NotifyStatusChanged(LastStatus); // 让 UI 显示 "Checking remote..."

                var adapter = await CreateAdapterAsync().ConfigureAwait(false);
                if (adapter == null)
                {
                    // 以前直接 return：UI 停在 "Checking remote..."，也不记录检查时间。
                    // 现在记一次失败（退避）并通知 UI，错误原因（命令缺失等）由 LastError 给出。
                    RecordFailure(_lastError, logWarning: !string.IsNullOrEmpty(_lastError));
                    return LastStatus;
                }

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
                    // fetch / svn status -u 可能耗时数十秒，整体放到线程池，主线程不参与任何一步
                    var token = newCts.Token;
                    var status = await Task.Run(() => adapter.GetSyncStatusAsync(token), token).ConfigureAwait(false);
                    RecordStatus(status);
                    return status;
                }
                catch (OperationCanceledException)
                {
                    return LastStatus;
                }
                catch (Exception ex)
                {
                    RecordFailure(ex.Message, logWarning: true);
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
        /// 记录一次由外部（例如 get_sync_status 工具）取得的远端状态，更新时间戳与 UI。
        /// </summary>
        public static void RecordStatus(VcsSyncStatus status)
        {
            if (status == null)
                return;

            var now = DateTime.UtcNow;
            int failures;
            lock (_lock)
            {
                _lastCheckedUtc = now;
                if (status.Success)
                {
                    // 失败时保留上一次成功的结果（含"远端有更新"的告警），只更新错误信息
                    _lastStatus = status;
                    _lastSuccessUtc = now;
                    _consecutiveFailures = 0;
                }
                else
                {
                    if (_lastStatus == null || !_lastStatus.Success)
                        _lastStatus = status;
                    _consecutiveFailures = Math.Min(_consecutiveFailures + 1, 16);
                }
                failures = _consecutiveFailures;
                _nextCheckUtc = now + GetCheckInterval(VcsSettings.RemoteStatusCheckIntervalMinutes, failures);
            }

            _lastError = status.Success ? null : DescribeError(status.ErrorMessage);
            // 离线 / 认证失败时 adapter 返回 Success=false（不抛异常）：只在连续失败的第一次记录警告
            if (!status.Success && failures == 1)
                AgentCoreLog.Warning($"[Version Control] Remote status check failed: {_lastError}");
            NotifyStatusChanged(status.Success ? status : LastStatus);
        }

        /// <summary>
        /// 连续失败时的检查间隔：基础间隔 × 2^失败次数（最多 ×8），且不超过 max(基础间隔, 60 分钟)。
        /// 离线 / 服务器不可达 / 认证失败时避免每个间隔都去连服务器、刷日志。手动 Check Remote 不受影响。
        /// </summary>
        public static TimeSpan GetCheckInterval(double baseIntervalMinutes, int consecutiveFailures)
        {
            var baseMinutes = Math.Max(baseIntervalMinutes, 0.1);
            if (consecutiveFailures <= 0)
                return TimeSpan.FromMinutes(baseMinutes);
            var multiplier = 1 << Math.Min(consecutiveFailures, 3);
            var capMinutes = Math.Max(baseMinutes, 60);
            return TimeSpan.FromMinutes(Math.Min(baseMinutes * multiplier, capMinutes));
        }

        private static void RecordFailure(string error, bool logWarning)
        {
            int failures;
            lock (_lock)
            {
                _lastCheckedUtc = DateTime.UtcNow;
                _consecutiveFailures = Math.Min(_consecutiveFailures + 1, 16);
                failures = _consecutiveFailures;
                _nextCheckUtc = _lastCheckedUtc + GetCheckInterval(VcsSettings.RemoteStatusCheckIntervalMinutes, failures);
            }

            if (!string.IsNullOrEmpty(error))
                _lastError = DescribeError(error);
            if (logWarning && failures == 1)
                AgentCoreLog.Warning($"[Version Control] Remote status check failed: {_lastError}");
            NotifyStatusChanged(LastStatus);
        }

        /// <summary>SVN 错误附带处理建议（svn upgrade / cleanup / 认证 / 离线）。</summary>
        private static string DescribeError(string error)
        {
            if (string.IsNullOrEmpty(error))
                return error;
            if (error.IndexOf("svn", StringComparison.OrdinalIgnoreCase) < 0 && SvnErrorHints.FirstErrorCode(error) == null)
                return error;
            var hint = SvnErrorHints.GetHint(error, VcsDetector.GetVcsRootPath());
            if (hint == null || error.Contains(hint))
                return error; // 无法识别或已附加过
            return error.Trim() + "\n" + hint;
        }

        /// <summary>
        /// Runs a user-confirmed sync/update operation and refreshes remote status afterwards.
        /// </summary>
        public static async Task<VcsOperationResult> SyncAsync(CancellationToken ct = default)
        {
            var adapter = await CreateAdapterAsync();
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
            // 每帧只比较时间戳，不做任何 VCS 探测
            if (!IsPeriodicCheckDue())
                return;

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;

            _ = CheckRemoteStatusAsync(false);
        }

        private static bool IsPeriodicCheckDue()
        {
            // #6: 用 Volatile.Read 而不是直接读 int 字段，确保跨线程可见性。
            if (Volatile.Read(ref _isCheckingFlag) != 0 || Volatile.Read(ref _isSyncingFlag) != 0)
                return false;

            lock (_lock)
                return DateTime.UtcNow >= _nextCheckUtc;
        }

        private static async Task<IVcsAdapter> CreateAdapterAsync()
        {
            var vcsType = VcsDetector.DetectVcs();
            if (vcsType == VcsType.None)
                return null;

            // 命令可用性在后台探测（会话内缓存），不在主线程启动子进程
            if (!await VcsDetector.EnsureCommandAvailableAsync(vcsType).ConfigureAwait(false))
            {
                _lastError = VcsDetector.GetCommandProbeError(vcsType) ?? $"{vcsType} command-line tool is not available.";
                return null;
            }

            var rootPath = VcsDetector.GetVcsRootPath();
            return vcsType switch
            {
                VcsType.Svn => new SvnAdapter(rootPath),
                VcsType.Perforce => new PerforceAdapter(rootPath),
                VcsType.Git => new GitAdapter(rootPath),
                _ => null
            };
        }

        private static void NotifyStatusChanged(VcsSyncStatus status)
        {
            // delayCall 在主线程执行：顺带把状态写入 SessionState（Unity API 只能在主线程调用）
            EditorApplication.delayCall += () =>
            {
                PersistToSession();
                StatusChanged?.Invoke(status);
            };
        }

        // ── SessionState 持久化 ──

        /// <summary>
        /// 可序列化的状态快照（JsonUtility 不支持属性，单独定义字段）。只保存 UI / 角标需要的摘要信息。
        /// </summary>
        [Serializable]
        private class StatusSnapshot
        {
            public bool Success;
            public string ErrorMessage;
            public bool HasRemoteChanges;
            public bool HasLocalChanges;
            public bool HasConflicts;
            public int RemoteChangeCount;
            public int LocalChangeCount;
            public int BehindCount;
            public int AheadCount;
            public string Summary;
        }

        private static void RestoreFromSession()
        {
            var lastChecked = ReadUtc(LastCheckedKey);
            _lastCheckedUtc = lastChecked ?? DateTime.MinValue;
            _lastSuccessUtc = ReadUtc(LastSuccessKey);

            var json = SessionState.GetString(LastStatusKey, string.Empty);
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    var s = JsonUtility.FromJson<StatusSnapshot>(json);
                    if (s != null)
                    {
                        _lastStatus = new VcsSyncStatus
                        {
                            Success = s.Success,
                            ErrorMessage = s.ErrorMessage,
                            HasRemoteChanges = s.HasRemoteChanges,
                            HasLocalChanges = s.HasLocalChanges,
                            HasConflicts = s.HasConflicts,
                            RemoteChangeCount = s.RemoteChangeCount,
                            LocalChangeCount = s.LocalChangeCount,
                            BehindCount = s.BehindCount,
                            AheadCount = s.AheadCount,
                            Summary = s.Summary
                        };
                    }
                }
                catch
                {
                    // 快照损坏时忽略，等待下一次检查
                }
            }

            var interval = TimeSpan.FromMinutes(VcsSettings.RemoteStatusCheckIntervalMinutes);
            var now = DateTime.UtcNow;
            // 域重载不重置计时：按上次检查时间续算；本会话从未检查过则在启动 1 分钟后做第一次检查
            _nextCheckUtc = lastChecked.HasValue
                ? lastChecked.Value + interval
                : now + InitialCheckDelay;
        }

        private static void PersistToSession()
        {
            VcsSyncStatus status;
            DateTime lastChecked;
            DateTime? lastSuccess;
            lock (_lock)
            {
                status = _lastStatus;
                lastChecked = _lastCheckedUtc;
                lastSuccess = _lastSuccessUtc;
            }

            WriteUtc(LastCheckedKey, lastChecked == DateTime.MinValue ? (DateTime?)null : lastChecked);
            WriteUtc(LastSuccessKey, lastSuccess);

            if (status == null)
            {
                SessionState.EraseString(LastStatusKey);
                return;
            }

            SessionState.SetString(LastStatusKey, JsonUtility.ToJson(new StatusSnapshot
            {
                Success = status.Success,
                ErrorMessage = status.ErrorMessage,
                HasRemoteChanges = status.HasRemoteChanges,
                HasLocalChanges = status.HasLocalChanges,
                HasConflicts = status.HasConflicts,
                RemoteChangeCount = status.RemoteChangeCount,
                LocalChangeCount = status.LocalChangeCount,
                BehindCount = status.BehindCount,
                AheadCount = status.AheadCount,
                Summary = status.Summary
            }));
        }

        private static DateTime? ReadUtc(string key)
        {
            var raw = SessionState.GetString(key, string.Empty);
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
                && ticks > 0 && ticks <= DateTime.MaxValue.Ticks)
                return new DateTime(ticks, DateTimeKind.Utc);
            return null;
        }

        private static void WriteUtc(string key, DateTime? value)
        {
            if (value.HasValue)
                SessionState.SetString(key, value.Value.Ticks.ToString(CultureInfo.InvariantCulture));
            else
                SessionState.EraseString(key);
        }
    }
}
