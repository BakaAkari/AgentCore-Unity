using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using AgentCore.Editor.Components.VCS.Tools;
using AgentCore.Editor.Utils;
using AgentCore.Editor.Workspace.Resolution;

namespace AgentCore.Editor.Components.VCS.UI
{
    /// <summary>
    /// Unity Project 窗口 VCS 集成：右键菜单（多选、自动带上 .meta）与 SVN 状态角标
    /// （改动 / 新增 / 冲突 / 未版本化 / needs-lock / 本人锁定 / 他人锁定）。
    /// <para>
    /// 角标绘制回调只做字典查找，不做 IO、不启动进程。状态在线程池刷新：
    /// 防抖（<see cref="DebounceSeconds"/>）+ 单飞 + 最小间隔，触发源为工程变更、编辑器重新获得焦点
    /// （从 TortoiseSVN / 资源管理器切回来）、外部工具关闭与远端检查完成。
    /// svn 命令从 Unity 工程根执行，只扫描工程本身，不扫描整个（可能很大的）工作副本。
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    public static class VcsProjectWindowIntegration
    {
        private const string MenuRoot = "Assets/Version Control/";
        private const int MenuPriority = 2000;
        private const string OverlayMenuPath = MenuRoot + "Show Status Overlays";
        private const string OverlayPrefKey = "AgentCore.VCS.ShowProjectOverlays";

        private const double DebounceSeconds = 1.5;
        private const double MinIntervalSeconds = 5;
        private const int SvnTimeoutSeconds = 60;

        private static readonly string _unityRoot;
        private static bool _overlaysEnabled;

        // 以下字段只在主线程读写（后台结果经 MainThread.Post 回到主线程）
        private static SvnOverlaySnapshot _snapshot;
        private static double _refreshDueTime = -1;
        private static double _lastRefreshFinishedTime = -1000;
        private static bool _refreshRunning;
        private static string _remoteStatusXml;
        private static string _remoteStatusBase;
        private static string _lastReportedError;

        static VcsProjectWindowIntegration()
        {
            var dataPath = Application.dataPath;
            _unityRoot = Directory.GetParent(dataPath)?.FullName ?? dataPath;
            _overlaysEnabled = EditorPrefs.GetBool(OverlayPrefKey, true);

            EditorApplication.projectWindowItemOnGUI += OnProjectWindowItemGUI;
            EditorApplication.projectChanged += RequestStatusRefresh;
            EditorApplication.focusChanged += OnFocusChanged;
            EditorApplication.update += OnEditorUpdate;
            VcsDetector.DetectionChanged += RequestStatusRefresh;
            VcsRemoteStatusMonitor.StatusChanged += OnRemoteStatusChanged;

            // 域重载后远端快照（不含 RawOutput）无法恢复锁信息，等待下一次远端检查
            EditorApplication.delayCall += RequestStatusRefresh;
        }

        /// <summary>
        /// 请求刷新角标（防抖，任意线程可调用的入口请走 MainThread.Post；本方法需在主线程调用）。
        /// 版本控制面板在 TortoiseSVN 对话框关闭后调用。
        /// </summary>
        public static void RequestStatusRefresh()
        {
            if (!_overlaysEnabled)
                return;
            _refreshDueTime = EditorApplication.timeSinceStartup + DebounceSeconds;
        }

        private static void OnFocusChanged(bool focused)
        {
            if (focused)
                RequestStatusRefresh();
        }

        private static void OnRemoteStatusChanged(VcsSyncStatus status)
        {
            if (status == null || !status.Success || string.IsNullOrEmpty(status.RawOutput))
                return;
            if (VcsDetector.DetectVcs() != VcsType.Svn)
                return;
            if (ReferenceEquals(status.RawOutput, _remoteStatusXml))
                return;

            // svn status -u 在工作副本根执行，路径相对于工作副本根
            _remoteStatusXml = status.RawOutput;
            _remoteStatusBase = VcsDetector.GetVcsRootPath();
            RequestStatusRefresh();
        }

        private static void OnEditorUpdate()
        {
            // 每帧只比较时间戳
            if (_refreshDueTime < 0 || _refreshRunning)
                return;
            var now = EditorApplication.timeSinceStartup;
            if (now < _refreshDueTime || now - _lastRefreshFinishedTime < MinIntervalSeconds)
                return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;

            _refreshDueTime = -1;
            StartRefresh();
        }

        private static void StartRefresh()
        {
            if (!_overlaysEnabled || VcsDetector.DetectVcs() != VcsType.Svn
                || VcsDetector.GetCommandAvailability(VcsType.Svn) == VcsCommandAvailability.Unavailable)
            {
                ClearOverlays();
                return;
            }

            _refreshRunning = true;
            var unityRoot = _unityRoot;
            var remoteXml = _remoteStatusXml;
            var remoteBase = _remoteStatusBase;

            Task.Run(() => BuildSnapshotAsync(unityRoot, remoteXml, remoteBase)).ContinueWith(task =>
            {
                MainThread.Post(() =>
                {
                    _refreshRunning = false;
                    _lastRefreshFinishedTime = EditorApplication.timeSinceStartup;
                    if (task.IsFaulted || task.IsCanceled)
                    {
                        ReportError(task.Exception?.GetBaseException().Message ?? "cancelled");
                        return;
                    }

                    var (snapshot, error) = task.Result;
                    if (error != null)
                    {
                        ReportError(error);
                        // 状态拿不到时清掉旧角标，避免显示过期信息
                        snapshot = null;
                    }
                    else
                    {
                        _lastReportedError = null;
                    }

                    _snapshot = _overlaysEnabled ? snapshot : null;
                    EditorApplication.RepaintProjectWindow();
                });
            }, TaskScheduler.Default);
        }

        private static async Task<(SvnOverlaySnapshot snapshot, string error)> BuildSnapshotAsync(string unityRoot, string remoteXml, string remoteBase)
        {
            if (!await VcsDetector.EnsureCommandAvailableAsync(VcsType.Svn).ConfigureAwait(false))
                return (null, null); // 命令缺失由面板 / 检测器提示，这里不重复报错

            var statusTask = VcsCommandExecutor.ExecuteAsync(
                "svn", new List<string> { "status", "--xml", "--non-interactive", "." }, unityRoot, SvnTimeoutSeconds);
            var needsLockTask = VcsCommandExecutor.ExecuteAsync(
                "svn", new List<string> { "propget", "svn:needs-lock", "-R", "--xml", "--non-interactive", "." }, unityRoot, SvnTimeoutSeconds);
            await Task.WhenAll(statusTask, needsLockTask).ConfigureAwait(false);

            var status = statusTask.Result;
            if (!status.Success)
                return (null, string.IsNullOrEmpty(status.ErrorMessage) ? status.Error : status.ErrorMessage);

            var snapshot = new SvnOverlaySnapshot();
            SvnOverlayStatusParser.ApplyStatusXml(status.Output, unityRoot, unityRoot, snapshot);

            // propget 失败（极少见）只影响 needs-lock 角标
            var needsLock = needsLockTask.Result;
            if (needsLock.Success)
                SvnOverlayStatusParser.ApplyNeedsLockXml(needsLock.Output, unityRoot, unityRoot, snapshot);

            if (!string.IsNullOrEmpty(remoteXml) && !string.IsNullOrEmpty(remoteBase))
                SvnOverlayStatusParser.ApplyRemoteLocksXml(remoteXml, remoteBase, unityRoot, snapshot);

            SvnOverlayStatusParser.PropagateToFolders(snapshot);
            return (snapshot, null);
        }

        private static void ReportError(string error)
        {
            // 同一错误只记录一次，避免每次切回编辑器都刷屏
            var code = SvnErrorHints.FirstErrorCode(error) ?? error;
            if (code == _lastReportedError)
                return;
            _lastReportedError = code;
            AgentCoreLog.Warning("[Version Control] Project window SVN overlays unavailable: "
                                 + SvnErrorHints.Describe(error, VcsDetector.GetVcsRootPath()));
        }

        private static void ClearOverlays()
        {
            if (_snapshot == null)
                return;
            _snapshot = null;
            EditorApplication.RepaintProjectWindow();
        }

        // ── 角标绘制 ──

        private static GUIStyle _badgeStyle;
        private static GUIContent _lockContent;

        private static readonly Color ModifiedColor = new Color(0.95f, 0.6f, 0.15f);
        private static readonly Color AddedColor = new Color(0.3f, 0.8f, 0.35f);
        private static readonly Color ErrorColor = new Color(0.9f, 0.25f, 0.25f);
        private static readonly Color MutedColor = new Color(0.6f, 0.6f, 0.6f);
        private static readonly Color MyLockColor = new Color(0.35f, 0.65f, 1f);

        private static void OnProjectWindowItemGUI(string guid, Rect rect)
        {
            var snapshot = _snapshot;
            if (snapshot == null || snapshot.IsEmpty || Event.current.type != EventType.Repaint)
                return;

            var assetPath = AssetDatabase.GUIDToAssetPath(guid);
            var flags = snapshot.Get(assetPath);
            if (flags == SvnOverlayFlags.None)
                return;

            EnsureStyles();
            var isListView = rect.height <= 20f;
            var size = isListView ? rect.height : Mathf.Clamp(rect.width * 0.28f, 10f, 18f);

            // 状态字母：列表视图画在行尾，网格视图画在图标左下角
            if (TryGetStateBadge(flags, out var letter, out var color, out var tooltip))
            {
                var badgeRect = isListView
                    ? new Rect(rect.xMax - size, rect.y, size, size)
                    : new Rect(rect.x, rect.y + rect.width - size, size, size);
                DrawLetterBadge(badgeRect, letter, color, tooltip);
            }

            // 锁：列表视图画在状态字母左侧，网格视图画在图标右上角
            if (TryGetLockBadge(flags, snapshot, assetPath, out var lockColor, out var lockTooltip))
            {
                var lockRect = isListView
                    ? new Rect(rect.xMax - size * 2f - 2f, rect.y, size, size)
                    : new Rect(rect.x + rect.width - size, rect.y, size, size);
                var previous = GUI.color;
                GUI.color = lockColor;
                GUI.Label(lockRect, new GUIContent(_lockContent.image, lockTooltip));
                GUI.color = previous;
            }
        }

        private static bool TryGetStateBadge(SvnOverlayFlags flags, out string letter, out Color color, out string tooltip)
        {
            if ((flags & SvnOverlayFlags.Conflicted) != 0) { letter = "C"; color = ErrorColor; tooltip = "SVN: conflicted"; return true; }
            if ((flags & SvnOverlayFlags.Missing) != 0) { letter = "!"; color = ErrorColor; tooltip = "SVN: missing"; return true; }
            if ((flags & SvnOverlayFlags.Deleted) != 0) { letter = "D"; color = ErrorColor; tooltip = "SVN: scheduled for deletion"; return true; }
            if ((flags & SvnOverlayFlags.Added) != 0) { letter = "A"; color = AddedColor; tooltip = "SVN: added"; return true; }
            if ((flags & SvnOverlayFlags.Modified) != 0) { letter = "M"; color = ModifiedColor; tooltip = "SVN: modified (asset or .meta)"; return true; }
            if ((flags & SvnOverlayFlags.Unversioned) != 0) { letter = "?"; color = MutedColor; tooltip = "SVN: not under version control"; return true; }
            if ((flags & SvnOverlayFlags.ChildChanged) != 0) { letter = "•"; color = ModifiedColor; tooltip = "SVN: contains changes"; return true; }
            letter = null; color = default; tooltip = null;
            return false;
        }

        private static bool TryGetLockBadge(SvnOverlayFlags flags, SvnOverlaySnapshot snapshot, string assetPath, out Color color, out string tooltip)
        {
            if ((flags & SvnOverlayFlags.LockedByOther) != 0)
            {
                snapshot.LockOwners.TryGetValue(assetPath, out var owner);
                color = ErrorColor;
                tooltip = string.IsNullOrEmpty(owner)
                    ? "SVN: locked by someone else (as of the last remote check)"
                    : $"SVN: locked by {owner} (as of the last remote check)";
                return true;
            }
            if ((flags & SvnOverlayFlags.LockedByMe) != 0)
            {
                color = MyLockColor;
                tooltip = "SVN: locked by you in this working copy";
                return true;
            }
            if ((flags & SvnOverlayFlags.NeedsLock) != 0)
            {
                color = MutedColor;
                tooltip = "SVN: needs lock (svn:needs-lock) — use Version Control > Get Lock before editing";
                return true;
            }
            color = default;
            tooltip = null;
            return false;
        }

        private static void DrawLetterBadge(Rect rect, string letter, Color color, string tooltip)
        {
            var previous = GUI.color;
            GUI.color = color;
            GUI.Label(rect, new GUIContent(letter, tooltip), _badgeStyle);
            GUI.color = previous;
        }

        private static void EnsureStyles()
        {
            if (_badgeStyle == null)
            {
                _badgeStyle = new GUIStyle(EditorStyles.miniBoldLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    padding = new RectOffset(0, 0, 0, 0)
                };
                _badgeStyle.normal.textColor = Color.white;
            }
            if (_lockContent == null)
                _lockContent = EditorGUIUtility.IconContent("IN LockButton on") ?? new GUIContent();
        }

        // ── 菜单 ──

        [MenuItem(OverlayMenuPath, false, MenuPriority + 20)]
        private static void ToggleOverlays()
        {
            _overlaysEnabled = !_overlaysEnabled;
            EditorPrefs.SetBool(OverlayPrefKey, _overlaysEnabled);
            if (_overlaysEnabled)
            {
                _refreshDueTime = EditorApplication.timeSinceStartup;
                _lastRefreshFinishedTime = -1000;
            }
            else
            {
                ClearOverlays();
            }
        }

        [MenuItem(OverlayMenuPath, true, MenuPriority + 20)]
        private static bool ValidateToggleOverlays()
        {
            Menu.SetChecked(OverlayMenuPath, _overlaysEnabled);
            return true;
        }

        [MenuItem(MenuRoot + "Commit", true, MenuPriority)]
        [MenuItem(MenuRoot + "Update", true, MenuPriority + 1)]
        [MenuItem(MenuRoot + "Show Log", true, MenuPriority + 2)]
        [MenuItem(MenuRoot + "Show Diff", true, MenuPriority + 3)]
        [MenuItem(MenuRoot + "Revert Changes", true, MenuPriority + 4)]
        [MenuItem(MenuRoot + "Cleanup", true, MenuPriority + 5)]
        private static bool ValidateSelection() => HasSelection() && VcsDetector.DetectVcs() != VcsType.None;

        [MenuItem(MenuRoot + "Add", true, MenuPriority + 6)]
        [MenuItem(MenuRoot + "Get Lock", true, MenuPriority + 7)]
        [MenuItem(MenuRoot + "Release Lock", true, MenuPriority + 8)]
        private static bool ValidateSvnSelection() => HasSelection() && VcsDetector.DetectVcs() == VcsType.Svn;

        [MenuItem(MenuRoot + "Commit", false, MenuPriority)]
        private static void CommitSelected() => LaunchExternal("commit", "Commit", PathScope.MultipleWithMeta, refresh: true);

        [MenuItem(MenuRoot + "Update", false, MenuPriority + 1)]
        private static void UpdateSelected() => LaunchExternal("update", "Update", PathScope.MultipleWithMeta, refresh: true, recheckRemote: true);

        [MenuItem(MenuRoot + "Show Log", false, MenuPriority + 2)]
        private static void ShowLogSelected() => LaunchExternal("log", "Log", PathScope.Single);

        [MenuItem(MenuRoot + "Show Diff", false, MenuPriority + 3)]
        private static void ShowDiffSelected() => LaunchExternal("diff", "Diff", PathScope.Single);

        [MenuItem(MenuRoot + "Revert Changes", false, MenuPriority + 4)]
        private static void RevertSelected() => LaunchExternal("revert", "Revert", PathScope.MultipleWithMeta, refresh: true);

        [MenuItem(MenuRoot + "Cleanup", false, MenuPriority + 5)]
        private static void CleanupSelected() => LaunchExternal("cleanup", "Cleanup", PathScope.Root, refresh: true);

        [MenuItem(MenuRoot + "Add", false, MenuPriority + 6)]
        private static void AddSelected() => LaunchExternal("add", "Add", PathScope.MultipleWithMeta, refresh: true);

        // needs-lock 资产只锁资产本身；.meta 一般不设 needs-lock，锁它会被服务器拒绝或无意义
        [MenuItem(MenuRoot + "Get Lock", false, MenuPriority + 7)]
        private static void LockSelected() => LaunchExternal("lock", "Get Lock", PathScope.Multiple, refresh: true);

        [MenuItem(MenuRoot + "Release Lock", false, MenuPriority + 8)]
        private static void UnlockSelected() => LaunchExternal("unlock", "Release Lock", PathScope.Multiple, refresh: true);

        private enum PathScope
        {
            Single,
            Multiple,
            MultipleWithMeta,
            Root
        }

        private static bool HasSelection()
        {
            var guids = Selection.assetGUIDs;
            return guids != null && guids.Length > 0;
        }

        /// <summary>
        /// 当前选中资产的绝对路径（支持多选；FileUtil.GetPhysicalPath 处理 Packages/ 下的本地包）。
        /// </summary>
        private static List<string> GetSelectedAbsolutePaths()
        {
            var result = new List<string>();
            var guids = Selection.assetGUIDs;
            if (guids == null)
                return result;

            foreach (var guid in guids)
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(assetPath))
                    continue;
                var physical = FileUtil.GetPhysicalPath(assetPath);
                if (string.IsNullOrEmpty(physical))
                    continue;
                try
                {
                    result.Add(Path.GetFullPath(Path.IsPathRooted(physical) ? physical : Path.Combine(_unityRoot, physical)));
                }
                catch (Exception)
                {
                    // 非法路径跳过
                }
            }

            return result;
        }

        /// <summary>
        /// 启动对应 VCS 的外部 GUI 工具。SVN 走 TortoiseProc（多路径经 /pathfile 传递），
        /// 对话框关闭后刷新角标与面板。
        /// </summary>
        private static void LaunchExternal(string operation, string displayName, PathScope scope, bool refresh = false, bool recheckRemote = false)
        {
            var vcsType = VcsDetector.DetectVcs();
            if (vcsType == VcsType.None)
            {
                AgentCoreLog.Warning($"[VCS] No version control system detected for {displayName}.");
                return;
            }

            var rootPath = VcsDetector.GetVcsRootPath();
            var selected = GetSelectedAbsolutePaths();
            List<string> paths;
            switch (scope)
            {
                case PathScope.Root:
                    paths = new List<string> { rootPath };
                    break;
                case PathScope.Single:
                    paths = selected.Take(1).ToList();
                    break;
                case PathScope.MultipleWithMeta:
                    paths = VcsAssetPaths.WithMetaCompanionsOnDisk(selected, File.Exists);
                    break;
                default:
                    paths = selected;
                    break;
            }
            if (paths.Count == 0)
                paths.Add(rootPath);

            if (!TryLaunchExternalTool(vcsType, operation, paths, rootPath, out var reason, out var process))
            {
                AgentCoreLog.Warning($"[VCS] {displayName}: {reason}");
                return;
            }

            if (refresh && process != null)
                WatchExit(process, recheckRemote);
            else
                process?.Dispose();
        }

        private static void WatchExit(System.Diagnostics.Process process, bool recheckRemote)
        {
            var fired = 0;
            void OnExit()
            {
                if (Interlocked.Exchange(ref fired, 1) != 0)
                    return;
                try { process.Dispose(); } catch { /* 忽略 */ }
                MainThread.Post(() =>
                {
                    RequestStatusRefresh();
                    if (recheckRemote)
                        VcsRemoteStatusMonitor.RequestCheck();
                });
            }

            try
            {
                process.EnableRaisingEvents = true;
                process.Exited += (_, __) => OnExit();
                if (process.HasExited)
                    OnExit();
            }
            catch
            {
                try { process.Dispose(); } catch { /* 忽略 */ }
            }
        }

        private static bool TryLaunchExternalTool(VcsType vcsType, string operation, IReadOnlyList<string> paths, string rootPath,
            out string reason, out System.Diagnostics.Process process)
        {
            process = null;
            var targetPath = paths[0];

            // TortoiseProc 用自己的 /key:"value" 解析规则（不支持 \" 转义），由 launcher 统一构造并校验；
            // git / p4v 走标准 argv 规则，用 VcsArgs.Join 转义路径。
            string tortoiseArgs = null;
            if (vcsType == VcsType.Svn
                && !VcsExternalToolLauncher.TryBuildTortoiseArgs(operation, paths, out tortoiseArgs, out reason))
            {
                return false;
            }

            var (fileName, arguments) = vcsType switch
            {
                VcsType.Svn => ("TortoiseProc.exe", tortoiseArgs),
                VcsType.Git => operation switch
                {
                    "commit" => ("git", "gui"),
                    "update" => ("git", "pull"),
                    "log" => ("git", "log --oneline -20"),
                    "diff" => ("git", "difftool"),
                    "revert" => ("git", VcsArgs.Join(new[] { "checkout", "--" }.Concat(paths))),
                    "cleanup" => ("git", "gc --auto"),
                    _ => (null, null)
                },
                VcsType.Perforce => operation switch
                {
                    "commit" => ("p4v", VcsArgs.Join(new[] { "-cmd", "submit", rootPath })),
                    "update" => ("p4v", VcsArgs.Join(new[] { "-cmd", "sync //...", rootPath })),
                    "log" => ("p4v", VcsArgs.Join(new[] { "-cmd", "history", targetPath })),
                    "diff" => ("p4v", VcsArgs.Join(new[] { "-cmd", "diff", targetPath })),
                    "revert" => ("p4v", VcsArgs.Join(new[] { "-cmd", "revert", targetPath })),
                    "cleanup" => ("p4v", null),
                    _ => (null, null)
                },
                _ => (null, null)
            };

            if (fileName == null)
            {
                reason = $"Operation '{operation}' is not supported for {vcsType}.";
                return false;
            }

            return VcsExternalToolLauncher.TryStartProcess(
                fileName,
                arguments ?? string.Empty,
                rootPath,
                $"{vcsType} {operation}",
                out reason,
                out process);
        }
    }
}
