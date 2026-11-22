using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Extensions;
using AgentCore.Editor.Workspace;
using AgentCore.Editor.Workspace.Resolution;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Components.VCS.Tools
{
    /// <summary>
    /// VCS 命令行可用性。
    /// </summary>
    public enum VcsCommandAvailability
    {
        /// <summary>尚未探测（或探测进行中）。</summary>
        Unknown,
        Available,
        Unavailable
    }

    /// <summary>
    /// 版本控制系统检测器。
    /// 按优先级 SVN > Perforce > Git 检测项目使用的 VCS。
    /// SVN 检测优先复用 <see cref="WorkspaceContextService"/> 的缓存结果，避免重复执行 svn 命令。
    /// <para>
    /// 缓存策略：VCS 类型 / 根目录与命令行可用性（含失败结果）都写入 <see cref="SessionState"/>，
    /// 域重载后直接复用，不重复探测；只有 <see cref="Refresh"/>（设置页 / 面板手动刷新 / detect_vcs）才会重新检测。
    /// 命令可用性探测（<c>git --version</c> 等）在后台线程执行，主线程永不等待子进程。
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    public static class VcsDetector
    {
        private const string SessionKeyPrefix = "AgentCore.VCS.Detector.";
        private const string TypeKey = SessionKeyPrefix + "Type";
        private const string RootKey = SessionKeyPrefix + "Root";
        private const string AvailabilityKeyPrefix = SessionKeyPrefix + "Available.";
        private const string AvailabilityErrorKeyPrefix = SessionKeyPrefix + "AvailableError.";

        /// <summary>探测命令超时；Windows 冷启动 git / 杀毒扫描可能较慢。</summary>
        private const int ProbeTimeoutSeconds = 10;

        private static readonly object _lock = new object();
        private static readonly Dictionary<VcsType, VcsCommandAvailability> _availability = new Dictionary<VcsType, VcsCommandAvailability>();
        private static readonly Dictionary<VcsType, string> _availabilityError = new Dictionary<VcsType, string>();
        private static readonly Dictionary<VcsType, Task<bool>> _probes = new Dictionary<VcsType, Task<bool>>();

        private static VcsType? _cachedType;
        private static string _cachedRootPath;

        // SessionState 是 Unity API，只能在主线程读写；后台探测完成后置脏标记，由 update 回调落盘
        private static readonly int _mainThreadId;
        private static readonly string _projectRootPath;
        private static int _persistPending;

        /// <summary>
        /// 检测结果（类型或命令可用性）变化时触发，总是在主线程回调。
        /// </summary>
        public static event Action DetectionChanged;

        static VcsDetector()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            // Application.dataPath 只能在主线程访问，这里预先缓存
            var dataPath = Application.dataPath;
            _projectRootPath = Directory.GetParent(dataPath)?.FullName ?? dataPath;

            RestoreFromSession();
            EditorApplication.update += FlushPendingPersist;
        }

        private static bool IsMainThread => Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        /// <summary>
        /// 检测当前项目使用的版本控制系统（结果缓存，域重载后从 SessionState 恢复）。
        /// </summary>
        /// <returns>检测到的 VCS 类型。</returns>
        public static VcsType DetectVcs()
        {
            // 组件禁用时不检测（DetectCore 可能触发 svn 等子进程），也不污染缓存
            if (!OptionalComponentManager.IsVcsEnabled())
                return VcsType.None;

            lock (_lock)
            {
                if (_cachedType.HasValue)
                    return _cachedType.Value;
            }

            var (type, root) = DetectCore();
            lock (_lock)
            {
                _cachedType = type;
                _cachedRootPath = root;
            }
            RequestPersist();
            return type;
        }

        /// <summary>
        /// 获取 VCS 根目录路径。
        /// 对于 SVN，返回 working copy root（由 WorkspaceContextService 解析）。
        /// </summary>
        public static string GetVcsRootPath()
        {
            lock (_lock)
            {
                if (_cachedRootPath != null)
                    return _cachedRootPath;
            }
            DetectVcs();
            lock (_lock)
                return _cachedRootPath;
        }

        /// <summary>
        /// 清除缓存（含 SessionState），下次调用时重新检测。
        /// </summary>
        public static void ClearCache()
        {
            lock (_lock)
            {
                _cachedType = null;
                _cachedRootPath = null;
                _availability.Clear();
                _availabilityError.Clear();
                _probes.Clear();
            }

            if (IsMainThread)
            {
                SessionState.EraseInt(TypeKey);
                SessionState.EraseString(RootKey);
                foreach (VcsType t in Enum.GetValues(typeof(VcsType)))
                {
                    SessionState.EraseInt(AvailabilityKeyPrefix + t);
                    SessionState.EraseString(AvailabilityErrorKeyPrefix + t);
                }
            }
            else
            {
                RequestPersist();
            }
        }

        /// <summary>
        /// 显式重新检测：清空缓存、让 WorkspaceContext 下次重新解析、重新检测类型并在后台探测命令可用性。
        /// 用于设置页 "Re-detect"、面板在未就绪状态下的 Refresh、以及 detect_vcs 工具。
        /// </summary>
        public static Task<bool> Refresh()
        {
            ClearCache();
            try { WorkspaceContextService.InvalidateCache(); } catch { /* 服务不可用时忽略，走本地检测 */ }

            var type = DetectVcs();
            var task = type == VcsType.None ? Task.FromResult(false) : EnsureCommandAvailableAsync(type);
            NotifyDetectionChanged();
            return task;
        }

        /// <summary>
        /// 检查指定 VCS 命令是否可用。<b>不阻塞</b>：
        /// 返回缓存结果；尚未探测时在后台启动探测并乐观返回 true（真正执行命令时若不可用会得到明确错误，不在这里拦截）。
        /// </summary>
        public static bool IsVcsCommandAvailable(VcsType vcsType)
        {
            if (vcsType == VcsType.None)
                return false;

            switch (GetCommandAvailability(vcsType))
            {
                case VcsCommandAvailability.Available:
                    return true;
                case VcsCommandAvailability.Unavailable:
                    return false;
                default:
                    _ = EnsureCommandAvailableAsync(vcsType);
                    return true;
            }
        }

        /// <summary>
        /// 当前缓存的命令可用性（不触发探测）。
        /// </summary>
        public static VcsCommandAvailability GetCommandAvailability(VcsType vcsType)
        {
            lock (_lock)
                return _availability.TryGetValue(vcsType, out var value) ? value : VcsCommandAvailability.Unknown;
        }

        /// <summary>
        /// 最近一次探测失败的原因（可用或未探测时为 null）。
        /// </summary>
        public static string GetCommandProbeError(VcsType vcsType)
        {
            lock (_lock)
                return _availabilityError.TryGetValue(vcsType, out var value) ? value : null;
        }

        /// <summary>
        /// 确保已探测命令可用性：已有结果直接返回；否则在后台线程执行一次探测（并发调用共享同一次探测）。
        /// </summary>
        /// <param name="recheckIfUnavailable">缓存为"不可用"时是否重新探测（显式刷新场景使用）。</param>
        public static Task<bool> EnsureCommandAvailableAsync(VcsType vcsType, bool recheckIfUnavailable = false)
        {
            if (vcsType == VcsType.None || !OptionalComponentManager.IsVcsEnabled())
                return Task.FromResult(false);

            lock (_lock)
            {
                if (_availability.TryGetValue(vcsType, out var known))
                {
                    if (known == VcsCommandAvailability.Available)
                        return Task.FromResult(true);
                    if (known == VcsCommandAvailability.Unavailable && !recheckIfUnavailable)
                        return Task.FromResult(false);
                }

                if (_probes.TryGetValue(vcsType, out var running) && !running.IsCompleted)
                    return running;

                var probe = Task.Run(() => ProbeAsync(vcsType));
                _probes[vcsType] = probe;
                return probe;
            }
        }

        /// <summary>
        /// 在后台线程运行 <c>git --version</c> / <c>svn --version --quiet</c> / <c>p4 -V</c>。
        /// 通过 VcsCommandExecutor 执行：有超时、超时杀进程树、只在进程确实退出后才读取 ExitCode。
        /// </summary>
        private static async Task<bool> ProbeAsync(VcsType vcsType)
        {
            string exe;
            string[] args;
            switch (vcsType)
            {
                case VcsType.Svn:
                    exe = "svn";
                    args = new[] { "--version", "--quiet" };
                    break;
                case VcsType.Perforce:
                    exe = "p4";
                    args = new[] { "-V" };
                    break;
                case VcsType.Git:
                    exe = "git";
                    args = new[] { "--version" };
                    break;
                default:
                    return false;
            }

            bool available;
            string error = null;
            try
            {
                var workingDirectory = GetVcsRootPath();
                if (string.IsNullOrEmpty(workingDirectory) || !Directory.Exists(workingDirectory))
                    workingDirectory = _projectRootPath;

                var result = await VcsCommandExecutor.ExecuteAsync(exe, args, workingDirectory, ProbeTimeoutSeconds).ConfigureAwait(false);
                available = result.Success;
                if (!available)
                    error = string.IsNullOrWhiteSpace(result.ErrorMessage)
                        ? $"'{exe}' exited with code {result.ExitCode}."
                        : result.ErrorMessage.Trim();
            }
            catch (Exception ex)
            {
                available = false;
                error = ex.Message;
            }

            lock (_lock)
            {
                _availability[vcsType] = available ? VcsCommandAvailability.Available : VcsCommandAvailability.Unavailable;
                if (available)
                    _availabilityError.Remove(vcsType);
                else
                    _availabilityError[vcsType] = error;
            }

            RequestPersist();
            NotifyDetectionChanged();
            return available;
        }

        // ── 检测核心（纯文件系统 + WorkspaceContext 缓存，不启动 VCS 子进程） ──

        private static (VcsType type, string root) DetectCore()
        {
            // ── 优先级 1: 复用 WorkspaceContextService 的结果 ──────────
            // WorkspaceContextService 已经运行过 svn info，直接读取结果，避免再次启动子进程。
            // 它依赖 Unity API，只在主线程使用。
            if (IsMainThread)
            {
                try
                {
                    var wsCtx = WorkspaceContextService.GetCurrent();
                    if (wsCtx != null && wsCtx.Vcs != null)
                    {
                        switch (wsCtx.Vcs.Type)
                        {
                            case WorkspaceVcsType.Svn:
                                // 优先使用 WorkspaceRoot（SVN working copy root），回退到 Unity 项目根目录
                                return (VcsType.Svn, !string.IsNullOrEmpty(wsCtx.WorkspaceRoot) ? wsCtx.WorkspaceRoot : _projectRootPath);

                            case WorkspaceVcsType.Git:
                                return (VcsType.Git, !string.IsNullOrEmpty(wsCtx.WorkspaceRoot) ? wsCtx.WorkspaceRoot : FindGitRoot(_projectRootPath));

                            case WorkspaceVcsType.Perforce:
                                return (VcsType.Perforce, !string.IsNullOrEmpty(wsCtx.WorkspaceRoot) ? wsCtx.WorkspaceRoot : _projectRootPath);

                            // WorkspaceVcsType.None 只表示"未找到 SVN 工作副本"（WorkspaceRootResolver 只识别 SVN），
                            // 不能据此判定无 VCS；与 Unknown 一样继续本地检测，否则 Git/P4 项目会被误判为无 VCS
                        }
                    }
                }
                catch
                {
                    // WorkspaceContextService 不可用时，降级到本地检测
                }
            }

            // ── 本地检测（WorkspaceContext 不可用或 VcsType.Unknown 时） ────
            var projectPath = _projectRootPath;

            // 优先级 1: SVN（向上查找最近的 .svn：SVN 1.7+ 只在工作副本根有 .svn，
            // Unity 工程常位于大工作副本的深层子目录，只看工程目录本身会漏判为 Git / None）
            var svnRoot = FindSvnWorkingCopyRoot(projectPath);
            if (!string.IsNullOrEmpty(svnRoot))
                return (VcsType.Svn, svnRoot);

            // 优先级 2: Perforce
            if (IsPerforceWorkspace(projectPath))
                return (VcsType.Perforce, projectPath);

            // 优先级 3: Git
            if (IsGitRepository(projectPath))
                return (VcsType.Git, FindGitRoot(projectPath));

            return (VcsType.None, projectPath);
        }

        // ── SessionState 持久化 ──

        private static void RestoreFromSession()
        {
            var typeValue = SessionState.GetInt(TypeKey, -1);
            if (typeValue >= 0 && Enum.IsDefined(typeof(VcsType), typeValue))
            {
                _cachedType = (VcsType)typeValue;
                var root = SessionState.GetString(RootKey, string.Empty);
                _cachedRootPath = string.IsNullOrEmpty(root) ? _projectRootPath : root;
            }

            foreach (VcsType t in Enum.GetValues(typeof(VcsType)))
            {
                var availability = SessionState.GetInt(AvailabilityKeyPrefix + t, (int)VcsCommandAvailability.Unknown);
                if (availability == (int)VcsCommandAvailability.Available || availability == (int)VcsCommandAvailability.Unavailable)
                {
                    _availability[t] = (VcsCommandAvailability)availability;
                    var error = SessionState.GetString(AvailabilityErrorKeyPrefix + t, string.Empty);
                    if (!string.IsNullOrEmpty(error))
                        _availabilityError[t] = error;
                }
            }
        }

        private static void RequestPersist()
        {
            Interlocked.Exchange(ref _persistPending, 1);
            if (IsMainThread)
                FlushPendingPersist();
        }

        private static void FlushPendingPersist()
        {
            // 每帧只是读一个 int，不做任何探测
            if (Interlocked.Exchange(ref _persistPending, 0) == 0)
                return;

            VcsType? type;
            string root;
            KeyValuePair<VcsType, VcsCommandAvailability>[] availability;
            Dictionary<VcsType, string> errors;
            lock (_lock)
            {
                type = _cachedType;
                root = _cachedRootPath;
                availability = new List<KeyValuePair<VcsType, VcsCommandAvailability>>(_availability).ToArray();
                errors = new Dictionary<VcsType, string>(_availabilityError);
            }

            if (type.HasValue)
            {
                SessionState.SetInt(TypeKey, (int)type.Value);
                SessionState.SetString(RootKey, root ?? string.Empty);
            }
            else
            {
                SessionState.EraseInt(TypeKey);
                SessionState.EraseString(RootKey);
            }

            foreach (VcsType t in Enum.GetValues(typeof(VcsType)))
            {
                SessionState.EraseInt(AvailabilityKeyPrefix + t);
                SessionState.EraseString(AvailabilityErrorKeyPrefix + t);
            }
            foreach (var pair in availability)
            {
                SessionState.SetInt(AvailabilityKeyPrefix + pair.Key, (int)pair.Value);
                if (errors.TryGetValue(pair.Key, out var error) && !string.IsNullOrEmpty(error))
                    SessionState.SetString(AvailabilityErrorKeyPrefix + pair.Key, error);
            }
        }

        private static void NotifyDetectionChanged()
        {
            // delayCall 保证回调在主线程执行
            EditorApplication.delayCall += () => DetectionChanged?.Invoke();
        }

        // ── 文件系统检测 ──

        /// <summary>
        /// 向上查找最近的含 .svn 的目录作为工作副本根（与 svn info 的 wcroot-abspath 一致，含 externals 场景）。
        /// 纯文件系统，任意线程可用；未找到返回 null。
        /// </summary>
        public static string FindSvnWorkingCopyRoot(string path)
        {
            var root = SvnWorkspaceInfoResolver.FindWorkingCopyRoot(path);
            // 与 WorkspaceContext.WorkspaceRoot 保持同一格式（正斜杠），避免两条检测路径得出"不同"的根
            return string.IsNullOrEmpty(root) ? null : root;
        }

        private static bool IsPerforceWorkspace(string path)
        {
            // Perforce 通过环境变量或配置文件识别
            // 检查 P4CONFIG 环境变量指定的配置文件
            var p4Config = Environment.GetEnvironmentVariable("P4CONFIG");
            if (!string.IsNullOrEmpty(p4Config))
            {
                var configPath = Path.Combine(path, p4Config);
                if (File.Exists(configPath))
                    return true;
            }

            // 检查常见的 Perforce 配置文件
            var commonConfigs = new[] { ".p4config", "p4config.txt", ".p4ignore" };
            foreach (var config in commonConfigs)
            {
                if (File.Exists(Path.Combine(path, config)))
                    return true;
            }

            // 检查是否设置了 P4CLIENT 环境变量
            var p4Client = Environment.GetEnvironmentVariable("P4CLIENT");
            if (!string.IsNullOrEmpty(p4Client))
                return true;

            return false;
        }

        private static bool IsGitRepository(string path)
        {
            // 向上查找 .git 目录
            var current = new DirectoryInfo(path);
            while (current != null)
            {
                var gitDir = Path.Combine(current.FullName, ".git");
                if (Directory.Exists(gitDir) || File.Exists(gitDir)) // .git 可能是文件（submodule / worktree）
                    return true;

                current = current.Parent;
            }
            return false;
        }

        private static string FindGitRoot(string path)
        {
            var current = new DirectoryInfo(path);
            while (current != null)
            {
                var gitDir = Path.Combine(current.FullName, ".git");
                if (Directory.Exists(gitDir) || File.Exists(gitDir))
                    return current.FullName;

                current = current.Parent;
            }
            return path;
        }
    }
}
