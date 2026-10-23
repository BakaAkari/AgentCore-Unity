using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Components.VCS.Config;
using AgentCore.Editor.Components.VCS.Tools;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Components.VCS.UI
{
    /// <summary>
    /// 版本控制面板
    /// 显示 VCS 状态、变更列表、提交历史，并提供操作按钮
    /// </summary>
    public class VersionControlPanel : VisualElement
    {
        private const string UssClassName = "version-control-panel";
        private const string SectionClassName = "vcs-section";
        private const string HeaderClassName = "vcs-section-header";
        private const string ContentClassName = "vcs-section-content";
        private const string StatusItemClassName = "vcs-status-item";
        private const string CommitItemClassName = "vcs-commit-item";
        private const string ButtonClassName = "vcs-button";
        private const string StatusBadgeClassName = "vcs-status-badge";
        private const string OperationButtonClassName = "vcs-operation-button";
        private const string DangerButtonClassName = "vcs-danger-button";
        private const string ButtonRowClassName = "vcs-button-row";

        private const int MaxCommitMessageLines = 3;
        private const int MaxCommitMessageCharacters = 360;
        private const int InitialCommitDisplayCount = 10;
        private const int CommitLoadBatchSize = 20;
        private const int MaxCommitQueryCount = 500;

        private Label _vcsTypeLabel;
        private Label _branchLabel;
        private Label _revisionLabel;
        private Label _statusSummaryLabel;
        private ScrollView _statusScrollView;
        private VisualElement _commitList;
        private Label _commitHistorySummaryLabel;
        private Button _loadOlderCommitsButton;
        private Button _collapseCommitsButton;
        private Button _refreshButton;
        private int _detectVersion;
        private bool _isDisposed;
        private string _lastExternalToolError;
        private Button _cleanupButton;
        private Button _refreshCommitsButton;
        private Button _viewDiffButton;
        private VisualElement _messageContainer;
        private VisualElement _syncStatusBanner;
        private Label _syncStatusLabel;
        private Label _syncStatusTimeLabel;
        private Button _checkRemoteButton;
        private Button _updateRemoteButton;

        // Operations 区域已移除 - 所有操作通过右键菜单调用外部工具

        // Working Copy Status 扁平列表数据
        private readonly HashSet<string> _selectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, VisualElement> _statusItemByPath = new Dictionary<string, VisualElement>(StringComparer.OrdinalIgnoreCase);
        private List<string> _displayedFilePaths = new List<string>();
        private int _lastSelectedFileIndex = -1;
        private List<VcsFileStatus> _currentFiles = new List<VcsFileStatus>();
        
        private List<VcsCommit> _loadedCommits = new List<VcsCommit>();
        private int _visibleCommitCount = InitialCommitDisplayCount;
        private bool _isLoadingMoreCommits;

        private IVcsAdapter _adapter;
        private VcsType _currentVcsType = VcsType.None;
        private CancellationTokenSource _cts;

        // 后台静默轮询 commit 列表（仅 Git / P4）。SVN 不轮询：svn log -r HEAD:1 每次都访问服务器，
        // 改为按需刷新——面板打开、手动刷新、外部提交 / 更新窗口关闭后、远端检查报告 HEAD 版本号变化时
        private DateTime _lastCommitPollUtc = DateTime.MinValue;
        private bool _isBackgroundPolling;
        private bool _isPanelActive;

        // 提交历史拉取失败（离线 / 认证失败 / 超时）时按次数指数退避：
        // 自动刷新（面板打开、HEAD 变化、Git/P4 轮询）在退避期内跳过；手动刷新不受限
        private int _commitPollFailures;
        private DateTime _lastCommitLogFailureUtc = DateTime.MinValue;
        private const int MaxCommitPollBackoffSeconds = 600;
        private const int CommitLogFailureBaseBackoffSeconds = 30;

        // 最近一次拉取提交历史的错误（显示在提交历史摘要行），成功后清除
        private string _commitLogError;

        // 最近一次远端检查报告的仓库 HEAD 版本号（svn status -u），变化时刷新提交历史
        private string _lastSeenRemoteRevision;

        /// <summary>RefreshAllData 中提交历史的刷新方式。</summary>
        private enum LogRefresh
        {
            /// <summary>自动触发（面板打开 / 初始化）：失败退避期内跳过。</summary>
            Auto,
            /// <summary>用户手动刷新或外部提交 / 更新完成：总是拉取，失败时弹出消息。</summary>
            Force,
            /// <summary>本地操作（暂存 / 还原 / 删除）之后：提交历史不会变，不访问服务器。</summary>
            Skip
        }

        // Unity 工程根（Application.dataPath 的父目录，主线程构造时缓存）。
        // SVN 状态路径相对于工作副本根，工程位于工作副本子目录时需要按它换算成 Assets/... 路径
        private readonly string _unityRoot;

        public VersionControlPanel()
        {
            _unityRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            AddToClassList(UssClassName);
            VcsRemoteStatusMonitor.StatusChanged += UpdateSyncStatusBanner;
            VcsRemoteStatusMonitor.StatusChanged += OnRemoteStatusChangedForLog;
            _lastSeenRemoteRevision = VcsRemoteStatusMonitor.LastStatus?.RemoteRevision;
            EditorApplication.update += OnEditorUpdatePollCommits;
            RegisterCallback<DetachFromPanelEvent>(_ => Dispose());
            _isPanelActive = true;
            BuildUI();
            if (VcsSettings.AutoRefreshOnOpen)
            {
                DetectAndInitialize();
            }
            else
            {
                _vcsTypeLabel.text = "Auto refresh disabled. Click Refresh to detect repository.";
            }
        }

        private void BuildUI()
        {
            var mainScrollView = new ScrollView(ScrollViewMode.Vertical);
            mainScrollView.AddToClassList("vcs-main-scroll-view");
            mainScrollView.style.flexGrow = 1;
            mainScrollView.style.flexShrink = 1;
            Add(mainScrollView);

            // 标题栏
            var header = new VisualElement();
            header.AddToClassList("panel-header");

            var title = new Label("Version Control");
            title.AddToClassList("panel-title");
            header.Add(title);

            var headerActions = new VisualElement();
            headerActions.AddToClassList("vcs-header-actions");

            _cleanupButton = new Button(OnCleanupProjectClicked) { text = "Cleanup" };
            _cleanupButton.AddToClassList(ButtonClassName);
            _cleanupButton.tooltip = "Open the external VCS cleanup tool for the whole project working copy.";
            headerActions.Add(_cleanupButton);

            _refreshButton = new Button(OnRefreshClicked) { text = "Refresh" };
            _refreshButton.AddToClassList(ButtonClassName);
            headerActions.Add(_refreshButton);

            header.Add(headerActions);

            mainScrollView.Add(header);

            // 消息容器
            _messageContainer = new VisualElement();
            _messageContainer.AddToClassList("message-container");
            _messageContainer.style.display = DisplayStyle.None;
            mainScrollView.Add(_messageContainer);

            // VCS 信息区域
            var infoSection = CreateSection("Repository Info");

            var infoRow = new VisualElement();
            infoRow.AddToClassList("vcs-info-row");

            _vcsTypeLabel = new Label("Detecting...");
            _vcsTypeLabel.AddToClassList("vcs-info-label");
            _vcsTypeLabel.AddToClassList("vcs-info-type");
            infoRow.Add(_vcsTypeLabel);

            _branchLabel = new Label("Branch: -");
            _branchLabel.AddToClassList("vcs-info-label");
            _branchLabel.AddToClassList("vcs-info-branch");
            infoRow.Add(_branchLabel);

            _revisionLabel = new Label("Revision: -");
            _revisionLabel.AddToClassList("vcs-info-label");
            _revisionLabel.AddToClassList("vcs-info-revision");
            infoRow.Add(_revisionLabel);

            infoSection.Add(infoRow);
            infoSection.Add(BuildSyncStatusBanner());

            mainScrollView.Add(infoSection);

            // Operations 区域已移除 - 所有操作通过右键菜单调用外部工具

            // 工作区状态区域
            var statusSection = CreateSection("Working Copy Status");

            _statusSummaryLabel = new Label("No changes");
            _statusSummaryLabel.AddToClassList("vcs-summary-label");
            statusSection.Add(_statusSummaryLabel);

            _viewDiffButton = new Button(OnViewDiffClicked) { text = "View Diff" };
            _viewDiffButton.AddToClassList(ButtonClassName);
            _viewDiffButton.SetEnabled(false);
            statusSection.Add(_viewDiffButton);

            // SVN 风格扁平状态列表：直接显示状态 + 完整相对路径，避免目录折叠隐藏文件状态。
            _statusScrollView = new ScrollView(ScrollViewMode.Vertical);
            _statusScrollView.AddToClassList("vcs-list");
            _statusScrollView.AddToClassList("vcs-status-scroll-view");
            _statusScrollView.style.flexGrow = 1;
            _statusScrollView.style.minHeight = 96;
            statusSection.Add(_statusScrollView);

            mainScrollView.Add(statusSection);

            // 提交历史区域
            var historySection = new VisualElement();
            historySection.AddToClassList(SectionClassName);

            // 自定义 header 行：标题 + 刷新按钮
            var historyHeaderRow = new VisualElement();
            historyHeaderRow.AddToClassList("vcs-section-header-row");

            var historyHeaderLabel = new Label("Recent Commits");
            historyHeaderLabel.AddToClassList(HeaderClassName);
            historyHeaderLabel.style.marginBottom = 0;
            historyHeaderLabel.style.paddingBottom = 0;
            historyHeaderLabel.style.borderBottomWidth = 0;
            historyHeaderLabel.style.flexGrow = 1;
            historyHeaderRow.Add(historyHeaderLabel);

            _refreshCommitsButton = new Button(OnRefreshCommitsClicked) { text = "↻ Refresh" };
            _refreshCommitsButton.AddToClassList("vcs-refresh-commits-button");
            historyHeaderRow.Add(_refreshCommitsButton);

            historySection.Add(historyHeaderRow);

            var historyContent = new VisualElement();
            historyContent.AddToClassList(ContentClassName);
            historySection.Add(historyContent);

            _commitHistorySummaryLabel = new Label("No commit history loaded");
            _commitHistorySummaryLabel.AddToClassList("vcs-commit-summary-label");
            historyContent.Add(_commitHistorySummaryLabel);

            var commitScrollView = new ScrollView(ScrollViewMode.Vertical);
            commitScrollView.AddToClassList("vcs-list");
            commitScrollView.AddToClassList("vcs-commit-scroll-view");
            _commitList = commitScrollView.contentContainer;
            historyContent.Add(commitScrollView);

            mainScrollView.Add(historySection);
        }

        private VisualElement BuildSyncStatusBanner()
        {
            _syncStatusBanner = new VisualElement();
            _syncStatusBanner.AddToClassList("vcs-sync-banner");
            _syncStatusBanner.style.display = DisplayStyle.None;

            _syncStatusLabel = new Label("Remote status has not been checked.");
            _syncStatusLabel.AddToClassList("vcs-sync-banner-label");
            _syncStatusBanner.Add(_syncStatusLabel);

            // 最近一次远端检查时间；每 30 秒刷新一次 "N min ago"（只读内存时间戳，不触发检查）
            _syncStatusTimeLabel = new Label(VcsRemoteStatusMonitor.FormatLastChecked());
            _syncStatusTimeLabel.AddToClassList("vcs-sync-banner-label");
            _syncStatusTimeLabel.style.opacity = 0.7f;
            _syncStatusBanner.Add(_syncStatusTimeLabel);
            _syncStatusTimeLabel.schedule.Execute(UpdateSyncStatusTimeLabel).Every(30000);

            var buttonRow = new VisualElement();
            buttonRow.AddToClassList("vcs-sync-banner-actions");

            _checkRemoteButton = new Button(OnCheckRemoteClicked) { text = "Check Remote" };
            _checkRemoteButton.AddToClassList(ButtonClassName);
            buttonRow.Add(_checkRemoteButton);

            _updateRemoteButton = new Button(OnUpdateRemoteClicked) { text = "Update" };
            _updateRemoteButton.AddToClassList(ButtonClassName);
            _updateRemoteButton.AddToClassList("vcs-sync-update-button");
            buttonRow.Add(_updateRemoteButton);

            _syncStatusBanner.Add(buttonRow);
            return _syncStatusBanner;
        }

        private VisualElement CreateSection(string title)
        {
            var section = new VisualElement();
            section.AddToClassList(SectionClassName);

            var header = new Label(title);
            header.AddToClassList(HeaderClassName);
            section.Add(header);

            var content = new VisualElement();
            content.AddToClassList(ContentClassName);
            section.Add(content);

            return section;
        }

        /// <summary>
        /// 检测 VCS 并初始化适配器。类型检测走会话缓存（SessionState），命令可用性探测在后台线程执行，
        /// 主线程只 await，不等待子进程。
        /// </summary>
        /// <param name="forceRedetect">显式刷新：清空检测缓存后重新检测（用于未就绪状态下点击 Refresh）。</param>
        private async void DetectAndInitialize(bool forceRedetect = false)
        {
            var version = ++_detectVersion;
            _adapter = null;

            bool available;
            if (forceRedetect)
            {
                _vcsTypeLabel.text = "Detecting...";
                available = await VcsDetector.Refresh();
                _currentVcsType = VcsDetector.DetectVcs();
            }
            else
            {
                _currentVcsType = VcsDetector.DetectVcs();
                if (_currentVcsType == VcsType.None)
                {
                    available = false;
                }
                else
                {
                    _vcsTypeLabel.text = $"Type: {_currentVcsType} (checking command...)";
                    available = await VcsDetector.EnsureCommandAvailableAsync(_currentVcsType);
                }
            }

            // 窗口在等待期间关闭或又发起了新的检测
            if (version != _detectVersion || _isDisposed)
                return;

            // Refresh 始终保持可用：未检测到 VCS / 命令不可用时点击即重新检测
            _refreshButton.SetEnabled(true);

            if (_currentVcsType == VcsType.None)
            {
                _vcsTypeLabel.text = "No VCS detected";
                ShowMessage("No version control system detected in this project. Click Refresh to detect again.", true);
                _cleanupButton.SetEnabled(false);
                return;
            }

            var rootPath = VcsDetector.GetVcsRootPath();
            var adapter = CreateAdapter(_currentVcsType, rootPath);

            if (adapter == null || !available)
            {
                var probeError = VcsDetector.GetCommandProbeError(_currentVcsType);
                _vcsTypeLabel.text = $"{_currentVcsType} (command not available)";
                // TortoiseSVN 默认不安装命令行 svn.exe：明确告诉用户去安装器里勾选，而不是笼统的 "install CLI"
                var installHint = _currentVcsType == VcsType.Svn
                    ? "Install the svn command-line client (TortoiseSVN installer → Modify → enable \"command line client tools\"), make sure its bin folder is on PATH, restart Unity, then click Refresh."
                    : $"Please install {_currentVcsType} CLI, then click Refresh.";
                ShowMessage($"{_currentVcsType} detected at {rootPath} but the command-line tool is not available. {installHint}"
                            + (string.IsNullOrEmpty(probeError) ? string.Empty : $"\n{probeError}"), true);
                _cleanupButton.SetEnabled(false);
                return;
            }

            _adapter = adapter;
            _vcsTypeLabel.text = $"Type: {_currentVcsType}";
            _cleanupButton.SetEnabled(true);

            _ = RefreshAllData(LogRefresh.Auto);
        }

        private async void OnRefreshClicked()
        {
            if (_adapter == null || _currentVcsType == VcsType.None)
            {
                DetectAndInitialize(forceRedetect: true);
                return;
            }

            await RefreshAllData(LogRefresh.Force);
        }

        private void OnCleanupProjectClicked()
        {
            if (_adapter == null || _currentVcsType == VcsType.None)
            {
                ShowMessage("Cleanup failed: no version control adapter is active. Click Refresh to detect the repository first.", true);
                return;
            }

            // 不再弹二次确认、也不在编译 / 导入时拦截：这里只是打开外部工具（TortoiseSVN 的 Cleanup 对话框本身就带选项和确认），
            // Unity 是否在编译与 svn cleanup 无关
            if (TryOpenExternalCleanupWindow())
                return;

            ShowExternalToolUnavailable("Cleanup Project");
        }

        /// <summary>
        /// 手动刷新 Recent Commits 列表，获取最新的 svn log / git log 并更新显示
        /// </summary>
        private async void OnRefreshCommitsClicked()
        {
            if (_adapter == null || _currentVcsType == VcsType.None)
            {
                ShowMessage("No VCS detected. Please click Refresh to detect repository first.", true);
                return;
            }

            _refreshCommitsButton.SetEnabled(false);
            _refreshCommitsButton.text = "↻ Loading...";

            try
            {
                var queryCount = GetCommitQueryCount();
                var (commits, error) = await FetchLogAsync(_adapter, queryCount, _cts?.Token ?? CancellationToken.None);
                if (_isDisposed)
                    return;
                ApplyLogResult(commits, error, showMessageOnError: true);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                ShowMessage($"Failed to refresh commits: {ex.Message}", true);
            }
            finally
            {
                _refreshCommitsButton.SetEnabled(true);
                _refreshCommitsButton.text = "↻ Refresh";
            }
        }

        private async Task RefreshAllData(LogRefresh logRefresh = LogRefresh.Skip)
        {
            if (_adapter == null)
                return;

            _refreshButton.SetEnabled(false);
            _cleanupButton.SetEnabled(false);
            HideMessage();

            try
            {
                _cts?.Cancel();
                _cts = new CancellationTokenSource();
                var ct = _cts.Token;

                _visibleCommitCount = InitialCommitDisplayCount;

                // 并行获取所有数据
                var branchTask = _adapter.GetBranchInfoAsync(ct);
                var statusTask = _adapter.GetStatusAsync(ct);
                // 提交历史：本地操作后跳过；自动刷新在失败退避期内跳过（离线时不必每次打开面板都等 svn log 超时）
                var fetchLog = logRefresh == LogRefresh.Force
                               || (logRefresh == LogRefresh.Auto && !IsCommitLogInBackoff());
                var logTask = fetchLog
                    ? FetchLogAsync(_adapter, GetCommitQueryCount(), ct)
                    : null;

                // 远端检查（fetch）不在刷新路径上：由 VcsRemoteStatusMonitor 定时 / 手动在后台执行，
                // 这里只显示上次结果；开关开启时也只是排队一次后台检查，不等待。
                if (VcsSettings.CheckRemoteStatusOnRefresh)
                    VcsRemoteStatusMonitor.RequestCheck();

                // 本地数据（svn info / svn status 只读工作副本）先到先显示；
                // svn log -r HEAD:1 要访问服务器，离线时会拖到超时（最长 60 秒），不能让状态列表陪着等
                await Task.WhenAll(branchTask, statusTask);

                if (ct.IsCancellationRequested)
                    return;

                var errors = new List<string>();

                // 更新分支信息
                var branchInfo = await branchTask;
                if (branchInfo.Success)
                {
                    _branchLabel.text = $"Branch: {branchInfo.CurrentBranch ?? "unknown"}";
                    _branchLabel.tooltip = branchInfo.CurrentBranch ?? "unknown";
                    _revisionLabel.text = $"Revision: {branchInfo.CurrentRevision ?? "unknown"}";
                    _revisionLabel.tooltip = branchInfo.CurrentRevision ?? "unknown";
                }
                else if (!string.IsNullOrWhiteSpace(branchInfo.ErrorMessage))
                {
                    errors.Add(branchInfo.ErrorMessage);
                }

                // 更新状态列表
                var statusResult = await statusTask;
                if (statusResult.Success)
                {
                    _currentFiles = statusResult.Files ?? new List<VcsFileStatus>();
                    UpdateStatusList(_currentFiles);
                    if (statusResult.Truncated)
                        _statusSummaryLabel.text += " (output truncated — list may be incomplete)";
                    // View Diff 走外部图形 diff 工具（目前仅 SVN/TortoiseSVN 支持），
                    // 非 SVN 时禁用按钮，避免点击后只弹"需要外部客户端"的空操作。
                    _viewDiffButton.SetEnabled(_currentFiles.Count > 0 && SupportsExternalFileTool("diff"));
                }
                else
                {
                    // 以前失败时什么都不显示，列表停留在旧内容 / "No changes"，用户以为工作副本是干净的
                    _statusSummaryLabel.text = "Status unavailable (see message above)";
                    errors.Add(statusResult.ErrorMessage);
                }

                if (errors.Count > 0)
                    ShowMessage(BuildCommandErrorMessage("Working copy status failed", errors), true);

                // 本地部分已完成：先恢复按钮，不让远端 log 占着 Refresh / Cleanup
                _refreshButton.SetEnabled(true);
                _cleanupButton.SetEnabled(_adapter != null && _currentVcsType != VcsType.None);

                // 更新提交历史（远端，可能较慢；失败时保留已显示的旧列表，并在摘要行显示错误）
                if (logTask != null)
                {
                    var (commits, logError) = await logTask;
                    if (ct.IsCancellationRequested || _isDisposed)
                        return;
                    ApplyLogResult(commits, logError, showMessageOnError: logRefresh == LogRefresh.Force && errors.Count == 0);
                }

                UpdateSyncStatusBanner(VcsRemoteStatusMonitor.LastStatus);
            }
            catch (OperationCanceledException)
            {
                // 忽略取消异常
            }
            catch (Exception ex)
            {
                ShowMessage($"Error refreshing data: {ex.Message}", true);
                AgentCoreLog.Error($"[VersionControlPanel] Refresh error: {ex}");
            }
            finally
            {
                _refreshButton.SetEnabled(true);
                _cleanupButton.SetEnabled(_adapter != null && _currentVcsType != VcsType.None);
            }
        }

        private void UpdateStatusList(List<VcsFileStatus> files)
        {
            _currentFiles = SortStatusFiles(files ?? new List<VcsFileStatus>());
            _selectedFiles.Clear();
            _statusItemByPath.Clear();
            _displayedFilePaths = _currentFiles.Select(f => f.FilePath).ToList();
            _lastSelectedFileIndex = -1;

            _statusScrollView.contentContainer.Clear();

            if (_currentFiles.Count == 0)
            {
                _statusSummaryLabel.text = "No changes in working copy";
                return;
            }

            var summary = _currentFiles
                .GroupBy(f => f.State)
                .OrderBy(g => GetStateSortOrder(g.Key))
                .Select(g => $"{g.Count()} {g.Key}")
                .ToList();
            _statusSummaryLabel.text = string.Join(", ", summary);

            foreach (var file in _currentFiles)
            {
                var item = CreateStatusItem(file);
                _statusScrollView.contentContainer.Add(item);
                _statusItemByPath[file.FilePath] = item;
            }
        }

        /// <summary>
        /// 按完整相对路径排序，等价于按目录结构展开后的扁平列表自然顺序。
        /// 不按状态次排序，保持同目录下文件的路径字母顺序。
        /// </summary>
        private List<VcsFileStatus> SortStatusFiles(List<VcsFileStatus> files)
        {
            return files
                .Where(f => f != null && !string.IsNullOrWhiteSpace(f.FilePath))
                .OrderBy(f => NormalizeStatusPath(f.FilePath), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// 规范化状态路径，保证 Windows 与 SVN 输出路径都按统一分隔符排序和比较。
        /// </summary>
        private string NormalizeStatusPath(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/').Trim('/');
        }

        /// <summary>
        /// 创建 SVN 风格状态行：状态字符始终在左侧可见，右侧显示完整相对路径。
        /// </summary>
        private VisualElement CreateStatusItem(VcsFileStatus file)
        {
            var item = new VisualElement();
            item.AddToClassList(StatusItemClassName);
            item.style.flexDirection = FlexDirection.Row;
            item.style.alignItems = Align.Center;
            item.style.flexShrink = 0;
            item.userData = file.FilePath;
            item.tooltip = $"{file.State}: {file.FilePath}";

            var badge = new Label(GetStateBadge(file.State));
            badge.AddToClassList(StatusBadgeClassName);
            badge.style.backgroundColor = GetStateColor(file.State);
            badge.tooltip = file.StateDescription ?? file.State.ToString();
            item.Add(badge);

            var pathLabel = new Label(NormalizeStatusPath(file.FilePath));
            pathLabel.AddToClassList("vcs-status-path-label");
            pathLabel.style.flexGrow = 1;
            pathLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
            pathLabel.style.whiteSpace = WhiteSpace.NoWrap;
            pathLabel.style.overflow = Overflow.Hidden;
            pathLabel.style.textOverflow = TextOverflow.Ellipsis;
            pathLabel.tooltip = file.FilePath;
            item.Add(pathLabel);

            item.RegisterCallback<MouseDownEvent>(OnStatusItemMouseDown, TrickleDown.TrickleDown);
            return item;
        }

        /// <summary>
        /// 处理扁平状态行点击：左键选择，右键弹出文件操作菜单。
        /// </summary>
        private void OnStatusItemMouseDown(MouseDownEvent evt)
        {
            var target = evt.currentTarget as VisualElement;
            if (target == null || !(target.userData is string filePath) || string.IsNullOrWhiteSpace(filePath))
                return;

            if (evt.button == 0)
            {
                HandleStatusItemSelection(filePath, evt.ctrlKey || evt.commandKey, evt.shiftKey);
                evt.StopImmediatePropagation();
                evt.PreventDefault();
                return;
            }

            if (evt.button == 1)
            {
                if (!_selectedFiles.Contains(filePath))
                    SelectSingleStatusFile(filePath);

                ShowStatusItemGenericMenu(filePath);
                evt.StopImmediatePropagation();
                evt.PreventDefault();
            }
        }

        /// <summary>
        /// 处理单选、多选和范围选择。
        /// </summary>
        private void HandleStatusItemSelection(string filePath, bool additive, bool range)
        {
            var index = _displayedFilePaths.FindIndex(p => string.Equals(p, filePath, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return;

            if (range && _lastSelectedFileIndex >= 0)
            {
                if (!additive)
                    _selectedFiles.Clear();

                var start = Math.Min(_lastSelectedFileIndex, index);
                var end = Math.Max(_lastSelectedFileIndex, index);
                for (var i = start; i <= end; i++)
                    _selectedFiles.Add(_displayedFilePaths[i]);
            }
            else if (additive)
            {
                if (!_selectedFiles.Add(filePath))
                    _selectedFiles.Remove(filePath);
                _lastSelectedFileIndex = index;
            }
            else
            {
                _selectedFiles.Clear();
                _selectedFiles.Add(filePath);
                _lastSelectedFileIndex = index;
            }

            RefreshStatusSelectionVisuals();
        }

        /// <summary>
        /// 单选一个状态文件。
        /// </summary>
        private void SelectSingleStatusFile(string filePath)
        {
            _selectedFiles.Clear();
            _selectedFiles.Add(filePath);
            _lastSelectedFileIndex = _displayedFilePaths.FindIndex(p => string.Equals(p, filePath, StringComparison.OrdinalIgnoreCase));
            RefreshStatusSelectionVisuals();
        }

        /// <summary>
        /// 刷新扁平列表选中态样式。
        /// </summary>
        private void RefreshStatusSelectionVisuals()
        {
            foreach (var pair in _statusItemByPath)
            {
                if (_selectedFiles.Contains(pair.Key))
                    pair.Value.AddToClassList("selected");
                else
                    pair.Value.RemoveFromClassList("selected");
            }
        }

        /// <summary>
        /// 使用 GenericMenu 构建并显示扁平状态行右键菜单。
        /// </summary>
        private void ShowStatusItemGenericMenu(string clickedPath)
        {
            var selectedFiles = GetSelectedFilePaths();
            if (!selectedFiles.Contains(clickedPath, StringComparer.OrdinalIgnoreCase))
                selectedFiles = new List<string> { clickedPath };

            var menu = new GenericMenu();
            if (selectedFiles.Count == 1)
            {
                var file = _currentFiles.FirstOrDefault(f => string.Equals(f.FilePath, selectedFiles[0], StringComparison.OrdinalIgnoreCase));
                PopulateFileGenericMenu(menu, file);
            }
            else
            {
                var files = GetSelectedStatusFiles(selectedFiles);
                PopulateSelectionGenericMenu(menu, files);
            }

            menu.ShowAsContext();
        }

        /// <summary>
        /// 填充单文件 GenericMenu 菜单项。
        /// 顶部操作区：Commit → Add → Revert（无二级菜单）
        /// 辅助操作区：View / File（保留二级菜单）
        /// </summary>
        private void PopulateFileGenericMenu(GenericMenu menu, VcsFileStatus file)
        {
            if (file == null)
                return;

            var path = file.FilePath;
            var supportsExternalTools = SupportsExternalFileTool("any");

            // 顶部核心操作（无二级菜单）
            // Commit：排除 Untracked（未纳入版本控制）、Conflicted（冲突未解决）、Missing（磁盘不存在）
            var canCommit = supportsExternalTools
                && file.State != VcsFileState.Untracked
                && file.State != VcsFileState.Conflicted
                && file.State != VcsFileState.Missing;
            AddMenuItem(menu, "Commit This File", canCommit, () => OnCommitSingleFileClicked(path));
            AddMenuItem(menu, "Add or Track File", CanStage(file) && supportsExternalTools, () => OnStageSingleFileClicked(path));
            AddMenuItem(menu, "Revert File", CanRevert(file) && supportsExternalTools, () => OnRevertSingleFileClicked(path));
            menu.AddSeparator("");

            // 辅助操作（保留二级菜单）
            AddMenuItem(menu, "View/Show Differences", CanDiff(file) && supportsExternalTools, () => OnViewDiffForFileClicked(path));
            // Show File Log：排除 Untracked（无版本历史）
            AddMenuItem(menu, "View/Show File Log", file.State != VcsFileState.Untracked && supportsExternalTools, () => OnShowFileLogClicked(path));
            menu.AddItem(new GUIContent("View/Show File Info"), false, () => OnShowFileInfoClicked(file));
            menu.AddSeparator("View/");

            menu.AddItem(new GUIContent("File/Copy Relative Path"), false, () => CopyRelativePath(path));
            menu.AddItem(new GUIContent("File/Reveal In Explorer"), false, () => RevealInExplorer(path));
            AddMenuItem(menu, "File/Ping In Unity Project", IsUnityAssetPath(path), () => PingInUnityProject(path));
            // Delete：Untracked（纯磁盘删除）或 Added（磁盘删除 + 隐性清理 VCS 暂存记录）
            AddMenuItem(menu, "File/Delete From Working Copy",
                file.State == VcsFileState.Untracked || file.State == VcsFileState.Added,
                () => OnDeleteFromWorkingCopyClicked(path, file.State));
        }

        /// <summary>
        /// 填充多选 GenericMenu 菜单项，避免多选时只剩清除选择和还原入口。
        /// 顶部操作区：Commit → Add → Revert（无二级菜单）
        /// 辅助操作区：View / Selection / File（保留二级菜单）
        /// </summary>
        private void PopulateSelectionGenericMenu(GenericMenu menu, List<VcsFileStatus> files)
        {
            if ((files?.Count ?? 0) == 0)
            {
                menu.AddDisabledItem(new GUIContent("No Valid Files Selected"));
                return;
            }

            var supportsExternalTools = SupportsExternalFileTool("any");
            var selectedPaths = files.Select(f => f.FilePath).ToList();
            var stageablePaths = files.Where(CanStage).Select(f => f.FilePath).ToList();
            var revertablePaths = files.Where(CanRevert).Select(f => f.FilePath).ToList();
            // committablePaths：与单文件逻辑一致，排除 Untracked / Conflicted / Missing
            var committablePaths = files
                .Where(IsCommittable)
                .Select(f => f.FilePath).ToList();
            var unityAssetPaths = selectedPaths.Where(IsUnityAssetPath).ToList();

            // 顶部核心操作（无二级菜单）
            AddMenuItem(menu, $"Commit Selected Files ({committablePaths.Count})", committablePaths.Count > 0 && supportsExternalTools, () => OnCommitMultipleFilesClicked(committablePaths));
            AddMenuItem(menu, $"Add or Track Selected Files ({stageablePaths.Count})", stageablePaths.Count > 0 && supportsExternalTools, () => OnStageMultipleFilesClicked(stageablePaths));
            AddMenuItem(menu, $"Revert Selected Files ({revertablePaths.Count})", revertablePaths.Count > 0, () => OnRevertMultipleFilesClicked(revertablePaths));
            menu.AddSeparator("");

            // 辅助操作（保留二级菜单）
            AddMenuItem(menu, "View/Show Working Copy Diff", _adapter != null, OnViewDiffClicked);
            menu.AddSeparator("View/");

            AddMenuItem(menu, "File/Copy Selected Relative Paths", selectedPaths.Count > 0, () => CopyRelativePaths(selectedPaths));
            AddMenuItem(menu, $"File/Ping First Unity Asset ({unityAssetPaths.Count})", unityAssetPaths.Count > 0, () => PingInUnityProject(unityAssetPaths[0]));
        }

        /// <summary>
        /// 根据路径列表获取当前状态对象，保持列表显示顺序。
        /// </summary>
        private List<VcsFileStatus> GetSelectedStatusFiles(List<string> selectedPaths)
        {
            if (selectedPaths == null || selectedPaths.Count == 0)
                return new List<VcsFileStatus>();

            var selected = new HashSet<string>(selectedPaths, StringComparer.OrdinalIgnoreCase);
            return _currentFiles
                .Where(f => f != null && selected.Contains(f.FilePath))
                .ToList();
        }

        /// <summary>
        /// 只有 Untracked 文件才需要 Add/Track 操作。
        /// </summary>
        private bool CanStage(VcsFileStatus file)
        {
            return file != null && file.State == VcsFileState.Untracked;
        }

        /// <summary>
        /// Diff 排除 Untracked（无历史）、Ignored、Missing（磁盘不存在，工具会报错）。
        /// </summary>
        private bool CanDiff(VcsFileStatus file)
        {
            return file != null
                && file.State != VcsFileState.Untracked
                && file.State != VcsFileState.Ignored
                && file.State != VcsFileState.Missing;
        }

        /// <summary>
        /// Revert 仅对有版本历史且语义明确的状态启用。
        /// Renamed/Copied 语义不同（撤销重命名/复制），由外部工具处理。
        /// </summary>
        private bool CanRevert(VcsFileStatus file)
        {
            return file != null
                && file.State != VcsFileState.Untracked
                && file.State != VcsFileState.Ignored
                && file.State != VcsFileState.Renamed
                && file.State != VcsFileState.Copied;
        }

        /// <summary>
        /// 向 GenericMenu 添加可禁用菜单项。
        /// </summary>
        private void AddMenuItem(GenericMenu menu, string path, bool enabled, GenericMenu.MenuFunction action)
        {
            if (enabled)
                menu.AddItem(new GUIContent(path), false, action);
            else
                menu.AddDisabledItem(new GUIContent(path));
        }

        /// <summary>
        /// 获取当前扁平状态列表中选中的文件路径。
        /// </summary>
        private List<string> GetSelectedFilePaths()
        {
            return _displayedFilePaths
                .Where(path => _selectedFiles.Contains(path))
                .ToList();
        }

        /// <summary>
        /// 检查指定文件是否在扁平状态列表选中项中。
        /// </summary>
        private bool IsFileSelected(string filePath)
        {
            return _selectedFiles.Contains(filePath);
        }

        /// <summary>
        /// 获取状态在摘要中的排序权重。
        /// </summary>
        private int GetStateSortOrder(VcsFileState state)
        {
            switch (state)
            {
                case VcsFileState.Conflicted:
                    return 0;
                case VcsFileState.Missing:
                    return 1;
                case VcsFileState.Modified:
                    return 2;
                case VcsFileState.Deleted:
                    return 3;
                case VcsFileState.Added:
                    return 4;
                case VcsFileState.Renamed:
                    return 5;
                case VcsFileState.Copied:
                    return 6;
                case VcsFileState.Untracked:
                    return 7;
                case VcsFileState.Ignored:
                    return 8;
                default:
                    return 99;
            }
        }

        /// <summary>
        /// 获取状态对应的颜色。
        /// </summary>
        private Color GetStateColor(VcsFileState state)
        {
            switch (state)
            {
                case VcsFileState.Modified:
                    return new Color(0.8f, 0.6f, 0.2f, 0.8f);
                case VcsFileState.Added:
                    return new Color(0.2f, 0.8f, 0.2f, 0.8f);
                case VcsFileState.Deleted:
                case VcsFileState.Missing:
                    return new Color(0.8f, 0.2f, 0.2f, 0.8f);
                case VcsFileState.Conflicted:
                    return new Color(0.9f, 0.1f, 0.1f, 0.9f);
                case VcsFileState.Untracked:
                    return new Color(0.5f, 0.5f, 0.5f, 0.8f);
                case VcsFileState.Ignored:
                    return new Color(0.4f, 0.4f, 0.4f, 0.6f);
                default:
                    return new Color(0.3f, 0.3f, 0.3f, 0.8f);
            }
        }

        private void OnViewDiffForFileClicked(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;

            // Selection handled by Working Copy Status list
            
            // 尝试使用外部工具打开 Diff
            if (TryOpenExternalDiff(filePath))
                return;
            
            // 外部工具不可用时显示提示
            ShowExternalToolUnavailable("Diff");
        }

        private void OnRevertSingleFileClicked(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;

            // 资产与其 .meta 一起还原（只还原资产会留下与之不匹配的 .meta，反之亦然）
            if (TryOpenExternalRevert(WithMetaCompanions(new[] { filePath }, CanRevert)))
                return;

            // 外部工具不可用时显示提示
            ShowExternalToolUnavailable("Revert");
        }

        private void OnStageSingleFileClicked(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;

            // 新资产与其 .meta 一起加入版本控制；外部对话框关闭后自动刷新状态列表
            if (TryOpenExternalAdd(WithMetaCompanions(new[] { filePath }, CanStage)))
                return;

            // 外部工具不可用时显示提示
            ShowExternalToolUnavailable("Add");
        }

        private void OnCommitSingleFileClicked(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;

            // 资产与其 .meta 一起提交
            if (TryOpenExternalCommitFiles(WithMetaCompanions(new[] { filePath }, IsCommittable)))
                return;

            // 外部工具不可用时显示提示
            ShowExternalToolUnavailable("Commit File");
        }

        private void OnCommitMultipleFilesClicked(List<string> filePaths)
        {
            if (filePaths == null || filePaths.Count == 0)
                return;

            if (TryOpenExternalCommitFiles(WithMetaCompanions(filePaths, IsCommittable)))
                return;

            ShowExternalToolUnavailable("Commit Selected Files");
        }

        private void OnShowFileLogClicked(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;

            // 尝试使用外部工具打开 Log
            if (TryOpenExternalLog(filePath))
                return;
            
            // 外部工具不可用时显示提示
            ShowExternalToolUnavailable("Log");
        }

        private void OnShowFileInfoClicked(VcsFileStatus file)
        {
            var details = new List<string>
            {
                $"Path: {file.FilePath}",
                $"State: {file.State}",
                $"Description: {file.StateDescription}",
                $"VCS: {_currentVcsType}",
                $"Root: {VcsDetector.GetVcsRootPath()}"
            };

            AgentCore.Editor.Utils.AgentCoreLog.Info($"[Version Control][{_currentVcsType}][File Info]\n{string.Join("\n", details)}");
            ShowMessage($"File info for '{file.FilePath}' logged to Console.", false);
        }

        private async void OnDeleteFromWorkingCopyClicked(string filePath, VcsFileState fileState)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;

            var absolutePath = GetAbsolutePath(filePath);
            var confirmed = EditorUtility.DisplayDialog(
                "Delete From Working Copy",
                $"Delete this file from disk?\n\n{filePath}",
                "Delete", "Cancel");
            if (!confirmed)
                return;

            try
            {
                if (File.Exists(absolutePath))
                    File.Delete(absolutePath);
                else if (Directory.Exists(absolutePath))
                    Directory.Delete(absolutePath, true);
                else
                {
                    ShowMessage($"Path does not exist: {filePath}", true);
                    return;
                }

                // Added 状态：文件已被 VCS 记录为"待添加"，删除磁盘文件后隐性清理 VCS 暂存记录
                if (fileState == VcsFileState.Added && _adapter != null)
                {
                    var unstageResult = await _adapter.UnstageFilesAsync(new List<string> { filePath }, CancellationToken.None);
                    if (!unstageResult.Success)
                        AgentCoreLog.Warning($"[Version Control] Delete succeeded but failed to clean VCS staging record for '{filePath}': {unstageResult.ErrorMessage}");
                }

                AssetDatabase.Refresh();
                ShowMessage($"Deleted '{filePath}' from working copy.", false);
                await RefreshAllData();
            }
            catch (Exception ex)
            {
                ShowMessage($"Delete failed: {ex.Message}", true);
            }
        }


        /// <summary>
        /// 拼接命令失败信息；SVN 时附上可操作的提示（E155036 → svn upgrade、E155004 → cleanup、认证 / 离线等）。
        /// </summary>
        private string BuildCommandErrorMessage(string title, List<string> errors)
        {
            var distinct = errors
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Select(e => e.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var text = distinct.Count == 0 ? "Unknown error" : string.Join("\n", distinct);
            if (_currentVcsType == VcsType.Svn)
                text = AgentCore.Editor.Workspace.Resolution.SvnErrorHints.Describe(text, VcsDetector.GetVcsRootPath());
            return $"{title}: {text}";
        }

        private string GetAbsolutePath(string filePath)
        {
            var normalized = filePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.Combine(VcsDetector.GetVcsRootPath(), normalized));
        }

        /// <summary>
        /// 状态列表路径（相对工作副本根）→ Unity 资产路径（Assets/... / Packages/...）；不在 Unity 工程内返回 null。
        /// Unity 工程位于工作副本子目录时，状态路径形如 "client/unity/Assets/Foo.prefab"，不能直接交给 AssetDatabase。
        /// </summary>
        private string ToUnityAssetPath(string filePath)
        {
            return VcsAssetPaths.ToUnityAssetPath(filePath, VcsDetector.GetVcsRootPath(), _unityRoot);
        }

        private bool IsUnityAssetPath(string filePath)
        {
            return ToUnityAssetPath(filePath) != null;
        }

        /// <summary>
        /// 为选中的状态路径补上 .meta 伴随文件（资产 ↔ .meta），只补状态列表里同样满足 <paramref name="filter"/> 的条目。
        /// 只选中资产提交 / 还原时，其 .meta 的改动（GUID、导入设置）不会被漏掉，避免提交出 "资产与 meta 不一致" 的版本。
        /// </summary>
        private List<string> WithMetaCompanions(IEnumerable<string> filePaths, Func<VcsFileStatus, bool> filter)
        {
            var available = _currentFiles
                .Where(f => f != null && (filter == null || filter(f)))
                .Select(f => f.FilePath);
            return VcsAssetPaths.WithMetaCompanions(filePaths, available);
        }

        private bool IsCommittable(VcsFileStatus file)
        {
            return file != null
                && file.State != VcsFileState.Ignored
                && file.State != VcsFileState.Untracked
                && file.State != VcsFileState.Conflicted
                && file.State != VcsFileState.Missing;
        }

        private void CopyRelativePath(string filePath)
        {
            EditorGUIUtility.systemCopyBuffer = filePath ?? string.Empty;
            ShowMessage($"Copied path: {filePath}", false);
        }

        private void CopyRelativePaths(List<string> filePaths)
        {
            if (filePaths == null || filePaths.Count == 0)
                return;

            EditorGUIUtility.systemCopyBuffer = string.Join(Environment.NewLine, filePaths);
            ShowMessage($"Copied {filePaths.Count} selected path(s).", false);
        }

        private void RevealInExplorer(string filePath)
        {
            var absolutePath = GetAbsolutePath(filePath);
            EditorUtility.RevealInFinder(File.Exists(absolutePath) || Directory.Exists(absolutePath) ? absolutePath : Path.GetDirectoryName(absolutePath));
        }

        private void PingInUnityProject(string filePath)
        {
            var assetPath = ToUnityAssetPath(filePath);
            // .meta 本身不是资产：定位到它描述的资产
            if (assetPath != null && assetPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                assetPath = assetPath.Substring(0, assetPath.Length - ".meta".Length);
            var asset = assetPath == null ? null : AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset == null)
            {
                ShowMessage($"Unity asset not found: {assetPath ?? filePath}", true);
                return;
            }

            EditorGUIUtility.PingObject(asset);
            Selection.activeObject = asset;
        }

        /// <summary>
        /// 拉取提交历史并带回错误：SVN 用 <see cref="SvnAdapter.GetLogWithErrorAsync"/>
        /// （<see cref="IVcsAdapter.GetLogAsync"/> 失败时只返回空列表，认证失败 / 离线会被误报成"没有提交"）。
        /// </summary>
        private static async Task<(List<VcsCommit> commits, string error)> FetchLogAsync(IVcsAdapter adapter, int count, CancellationToken ct)
        {
            if (adapter is SvnAdapter svn)
                return await svn.GetLogWithErrorAsync(count, ct);
            return (await adapter.GetLogAsync(count, ct), null);
        }

        /// <summary>
        /// 应用一次提交历史拉取结果。失败：计入退避、保留旧列表、在摘要行显示错误（手动刷新时同时弹出消息）。
        /// Git / P4 适配器不返回错误，空列表按失败退避但不显示错误。
        /// </summary>
        private void ApplyLogResult(List<VcsCommit> commits, string error, bool showMessageOnError)
        {
            if (!string.IsNullOrEmpty(error))
            {
                RecordCommitLogFailure();
                _commitLogError = error;
                if (showMessageOnError)
                    ShowMessage(BuildCommandErrorMessage("Commit history failed", new List<string> { error }), true);
                RenderCommitList();
                return;
            }

            _commitLogError = null;
            if ((commits?.Count ?? 0) == 0 && !(_adapter is SvnAdapter))
            {
                // 非 SVN 适配器失败时只返回空列表：退避，但不清空已显示的列表
                RecordCommitLogFailure();
                if (_loadedCommits.Count > 0)
                    return;
            }

            UpdateCommitList(commits);
        }

        private void RecordCommitLogFailure()
        {
            _commitPollFailures = Math.Min(_commitPollFailures + 1, 16);
            _lastCommitLogFailureUtc = DateTime.UtcNow;
        }

        /// <summary>自动刷新的失败退避：基础 30 秒 × 2^(连续失败次数 - 1)，上限 <see cref="MaxCommitPollBackoffSeconds"/>。</summary>
        private bool IsCommitLogInBackoff()
        {
            if (_commitPollFailures <= 0)
                return false;
            var seconds = CommitLogFailureBaseBackoffSeconds * Math.Pow(2, Math.Min(_commitPollFailures - 1, 10));
            return DateTime.UtcNow - _lastCommitLogFailureUtc < TimeSpan.FromSeconds(Math.Min(seconds, MaxCommitPollBackoffSeconds));
        }

        private void UpdateCommitList(List<VcsCommit> commits)
        {
            _loadedCommits = commits ?? new List<VcsCommit>();
            if (_loadedCommits.Count > 0)
                _commitPollFailures = 0;
            _visibleCommitCount = Mathf.Clamp(_visibleCommitCount, InitialCommitDisplayCount, Mathf.Max(InitialCommitDisplayCount, _loadedCommits.Count));
            _lastCommitPollUtc = DateTime.UtcNow;
            RenderCommitList();
        }

        private void RenderCommitList()
        {
            _commitList.Clear();

            var errorSuffix = string.IsNullOrEmpty(_commitLogError)
                ? string.Empty
                : $"\nCould not refresh commit history: {AgentCore.Editor.Workspace.Resolution.SvnErrorHints.Describe(_commitLogError, VcsDetector.GetVcsRootPath())}";
            _commitHistorySummaryLabel.tooltip = _commitLogError ?? string.Empty;

            if (_loadedCommits == null || _loadedCommits.Count == 0)
            {
                _commitHistorySummaryLabel.text = (string.IsNullOrEmpty(_commitLogError) ? "No commit history available" : "Commit history unavailable") + errorSuffix;
                var noCommits = new Label(string.IsNullOrEmpty(_commitLogError) ? "No commit history available" : "Commit history unavailable");
                noCommits.style.color = new Color(0.6f, 0.6f, 0.6f);
                noCommits.style.paddingTop = 5;
                _commitList.Add(noCommits);
                return;
            }

            var visibleCount = Mathf.Min(_visibleCommitCount, _loadedCommits.Count);
            var hiddenLoadedCount = Mathf.Max(0, _loadedCommits.Count - visibleCount);
            _commitHistorySummaryLabel.text = hiddenLoadedCount > 0
                ? $"Showing latest {visibleCount} of {_loadedCommits.Count} loaded commits. {hiddenLoadedCount} older loaded commits are collapsed."
                : $"Showing {_loadedCommits.Count} loaded commits.";
            _commitHistorySummaryLabel.text += errorSuffix;

            foreach (var commit in _loadedCommits.Take(visibleCount))
            {
                var item = CreateCommitItem(commit);
                _commitList.Add(item);
            }

            _commitList.Add(CreateCommitHistoryControls(visibleCount, hiddenLoadedCount));
        }

        private VisualElement CreateCommitHistoryControls(int visibleCount, int hiddenLoadedCount)
        {
            var controls = new VisualElement();
            controls.AddToClassList("vcs-commit-history-controls");

            _loadOlderCommitsButton = new Button(OnLoadOlderCommitsClicked)
            {
                text = BuildLoadOlderCommitsText(visibleCount, hiddenLoadedCount)
            };
            _loadOlderCommitsButton.AddToClassList(ButtonClassName);
            _loadOlderCommitsButton.AddToClassList("vcs-commit-load-button");
            _loadOlderCommitsButton.SetEnabled(!_isLoadingMoreCommits && _loadedCommits.Count < MaxCommitQueryCount);
            controls.Add(_loadOlderCommitsButton);

            if (visibleCount > InitialCommitDisplayCount)
            {
                _collapseCommitsButton = new Button(OnCollapseCommitsClicked) { text = "Collapse Older Commits" };
                _collapseCommitsButton.AddToClassList(ButtonClassName);
                _collapseCommitsButton.AddToClassList("vcs-commit-collapse-button");
                controls.Add(_collapseCommitsButton);
            }
            else
            {
                _collapseCommitsButton = null;
            }

            return controls;
        }

        private string BuildLoadOlderCommitsText(int visibleCount, int hiddenLoadedCount)
        {
            if (_isLoadingMoreCommits)
                return "Loading older commits...";

            if (hiddenLoadedCount > 0)
                return $"Load {Mathf.Min(CommitLoadBatchSize, hiddenLoadedCount)} older loaded commits";

            if (_loadedCommits.Count >= MaxCommitQueryCount)
                return $"Loaded maximum {MaxCommitQueryCount} commits";

            return $"Load {CommitLoadBatchSize} older commits";
        }

        private async void OnLoadOlderCommitsClicked()
        {
            if (_adapter == null || _isLoadingMoreCommits)
                return;

            var hiddenLoadedCount = Mathf.Max(0, _loadedCommits.Count - _visibleCommitCount);
            if (hiddenLoadedCount > 0)
            {
                _visibleCommitCount = Mathf.Min(_visibleCommitCount + CommitLoadBatchSize, _loadedCommits.Count);
                RenderCommitList();
                return;
            }

            if (_loadedCommits.Count >= MaxCommitQueryCount)
                return;

            _isLoadingMoreCommits = true;
            RenderCommitList();

            try
            {
                var queryCount = Mathf.Min(MaxCommitQueryCount, Mathf.Max(GetCommitQueryCount() + CommitLoadBatchSize, _loadedCommits.Count + CommitLoadBatchSize));
                var (commits, error) = await FetchLogAsync(_adapter, queryCount, CancellationToken.None);
                if (!string.IsNullOrEmpty(error))
                {
                    _commitLogError = error;
                    ShowMessage(BuildCommandErrorMessage("Failed to load older commits", new List<string> { error }), true);
                }
                else
                {
                    _commitLogError = null;
                    if ((commits?.Count ?? 0) > 0)
                        _loadedCommits = commits;
                    _visibleCommitCount = Mathf.Min(_visibleCommitCount + CommitLoadBatchSize, _loadedCommits.Count);
                }
            }
            catch (Exception ex)
            {
                ShowMessage($"Failed to load older commits: {ex.Message}", true);
            }
            finally
            {
                _isLoadingMoreCommits = false;
                RenderCommitList();
            }
        }

        private void OnCollapseCommitsClicked()
        {
            _visibleCommitCount = InitialCommitDisplayCount;
            RenderCommitList();
        }

        private int GetCommitQueryCount()
        {
            var configuredCount = Mathf.Max(VcsSettings.MaxCommitEntries, InitialCommitDisplayCount);
            return Mathf.Min(MaxCommitQueryCount, Mathf.Max(configuredCount, _visibleCommitCount));
        }

        private VisualElement CreateCommitItem(VcsCommit commit)
        {
            var item = new VisualElement();
            item.AddToClassList(CommitItemClassName);

            var fullRevision = string.IsNullOrWhiteSpace(commit.Revision) ? "unknown" : commit.Revision.Trim();
            var fullAuthor = string.IsNullOrWhiteSpace(commit.Author) ? "unknown" : commit.Author.Trim();
            var fullDate = string.IsNullOrWhiteSpace(commit.Date) ? "unknown date" : commit.Date.Trim();
            var fullMessage = NormalizeCommitMessage(commit.Message);
            var previewMessage = BuildCommitMessagePreview(fullMessage);
            var tooltip = $"Rev: {fullRevision}\nAuthor: {fullAuthor}\nDate: {fullDate}\n\n{fullMessage}";
            item.tooltip = tooltip;

            var header = new VisualElement();
            header.AddToClassList("vcs-commit-header");

            var revision = new Label($"Rev: {fullRevision}");
            revision.AddToClassList("vcs-commit-revision");
            revision.tooltip = fullRevision;
            header.Add(revision);

            var author = new Label(fullAuthor);
            author.AddToClassList("vcs-commit-author");
            author.tooltip = fullAuthor;
            header.Add(author);

            item.Add(header);

            var date = new Label(fullDate);
            date.AddToClassList("vcs-commit-date");
            date.tooltip = fullDate;
            item.Add(date);

            var message = new Label(previewMessage);
            message.AddToClassList("vcs-commit-message");
            message.tooltip = fullMessage;
            item.Add(message);

            return item;
        }

        private string NormalizeCommitMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return "(no commit message)";

            var normalized = message.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            return string.IsNullOrWhiteSpace(normalized) ? "(no commit message)" : normalized;
        }

        private string BuildCommitMessagePreview(string message)
        {
            var normalized = NormalizeCommitMessage(message);
            var lines = normalized.Split(new[] { '\n' }, StringSplitOptions.None)
                .Select(line => line.TrimEnd())
                .ToList();

            var truncated = false;
            if (lines.Count > MaxCommitMessageLines)
            {
                lines = lines.Take(MaxCommitMessageLines).ToList();
                truncated = true;
            }

            var preview = string.Join("\n", lines);
            if (preview.Length > MaxCommitMessageCharacters)
            {
                preview = preview.Substring(0, MaxCommitMessageCharacters).TrimEnd();
                truncated = true;
            }

            return truncated ? $"{preview}..." : preview;
        }

        #region Operation Event Handlers

        private async void OnStageAllClicked()
        {
            if (_adapter == null) return;

            var selectedFiles = GetSelectedFilePaths();
            var filesToStage = selectedFiles.Count > 0
                ? selectedFiles
                : _currentFiles.Select(f => f.FilePath).ToList();

            await StageFilesAsync(filesToStage, selectedFiles.Count > 0 ? "Stage Selected" : "Stage All");
        }

        private async Task StageFilesAsync(List<string> filesToStage, string operationName)
        {
            if (_adapter == null) return;

            if (filesToStage == null || filesToStage.Count == 0)
            {
                ShowMessage("No files to stage.", false);
                return;
            }

            LogVcsOperation(operationName, $"Preparing {filesToStage.Count} file(s).");
            try
            {
                var result = await _adapter.StageFilesAsync(filesToStage, CancellationToken.None);
                LogVcsResult(operationName, result);
                if (result.Success)
                {
                    ShowMessage($"Staged {filesToStage.Count} file(s) successfully.", false);
                    await RefreshAllData();
                }
                else
                {
                    ShowMessage($"Stage failed: {GetOperationFailureMessage(result)}", true);
                }
            }
            catch (Exception ex)
            {
                ShowMessage($"Error staging files: {ex.Message}", true);
            }
        }

        private async void OnUnstageAllClicked()
        {
            if (_adapter == null) return;

            var selectedFiles = GetSelectedFilePaths();
            var filesToUnstage = selectedFiles.Count > 0
                ? selectedFiles
                : _currentFiles.Select(f => f.FilePath).ToList();

            await UnstageFilesAsync(filesToUnstage, selectedFiles.Count > 0 ? "Unstage Selected" : "Unstage All");
        }

        private async Task UnstageFilesAsync(List<string> filesToUnstage, string operationName)
        {
            if (_adapter == null) return;

            if (filesToUnstage == null || filesToUnstage.Count == 0)
            {
                ShowMessage("No files to unstage.", false);
                return;
            }

            LogVcsOperation(operationName, $"Preparing {filesToUnstage.Count} file(s).");
            try
            {
                var result = await _adapter.UnstageFilesAsync(filesToUnstage, CancellationToken.None);
                LogVcsResult(operationName, result);
                if (result.Success)
                {
                    ShowMessage($"Unstaged {filesToUnstage.Count} file(s) successfully.", false);
                    await RefreshAllData();
                }
                else
                {
                    ShowMessage($"Unstage failed: {GetOperationFailureMessage(result)}", true);
                }
            }
            catch (Exception ex)
            {
                ShowMessage($"Error unstaging files: {ex.Message}", true);
            }
        }

        // Operations 按钮事件处理方法已移除 - 所有操作通过右键菜单调用外部工具
        // Git 特有操作方法已移除 - Git 操作通过外部工具或命令行处理

        private bool TryOpenExternalCommitWindow(string message)
        {
            var rootPath = VcsDetector.GetVcsRootPath();
            switch (_currentVcsType)
            {
                case VcsType.Svn:
                    // 提交信息写入 /logmsgfile，避免引号 / 换行破坏 TortoiseProc 的参数解析
                    if (!VcsExternalToolLauncher.TryBuildTortoiseLogMessageArg(message, out var logMessageArg, out var logMessageError))
                    {
                        _lastExternalToolError = logMessageError;
                        return false;
                    }
                    return TryStartTortoise("commit", new[] { rootPath }, rootPath, "TortoiseSVN commit window", logMessageArg, refreshOnExit: true, refreshLogOnExit: true);
                case VcsType.Git:
                    return TryStartExternalProcess("git", "gui", rootPath, "Git GUI commit window", refreshOnExit: true, refreshLogOnExit: true);
                case VcsType.Perforce:
                    return TryStartExternalProcess("p4v", VcsArgs.Join(new[] { "-cmd", "submit", rootPath }), rootPath, "P4V submit window");
                default:
                    return false;
            }
        }

        private bool TryOpenExternalUpdateWindow()
        {
            var rootPath = VcsDetector.GetVcsRootPath();
            switch (_currentVcsType)
            {
                case VcsType.Svn:
                    return TryStartTortoise("update", new[] { rootPath }, rootPath, "TortoiseSVN update window", refreshOnExit: true, recheckRemoteOnExit: true, refreshLogOnExit: true);
                case VcsType.Git:
                    return TryStartExternalProcess("git", "gui", rootPath, "Git GUI window", refreshOnExit: true, refreshLogOnExit: true);
                case VcsType.Perforce:
                    return TryStartExternalProcess("p4v", VcsArgs.Join(new[] { "-cmd", "sync", rootPath }), rootPath, "P4V sync window");
                default:
                    return false;
            }
        }

        private bool TryOpenExternalCleanupWindow()
        {
            var rootPath = VcsDetector.GetVcsRootPath();
            switch (_currentVcsType)
            {
                case VcsType.Svn:
                    return TryStartTortoise("cleanup", new[] { rootPath }, rootPath, "TortoiseSVN cleanup options dialog", refreshOnExit: true);
                case VcsType.Git:
                    return TryStartExternalProcess("git", "gui", rootPath, "Git GUI window");
                case VcsType.Perforce:
                    return TryStartExternalProcess("p4v", VcsArgs.Join(new[] { "-cmd", "reconcile", rootPath }), rootPath, "P4V reconcile window");
                default:
                    return false;
            }
        }

        /// <summary>
        /// 启动外部工具（TortoiseProc 由 <see cref="VcsExternalToolLauncher"/> 按注册表 / Program Files / PATH 定位）。
        /// <paramref name="refreshOnExit"/> 为 true 时，外部窗口关闭后自动刷新状态列表与 Project 窗口角标：
        /// 以前 TortoiseSVN 提交 / 还原 / 更新完成后面板仍显示旧状态，需要用户手动 Refresh。
        /// </summary>
        private bool TryStartExternalProcess(string fileName, string arguments, string workingDirectory, string displayName,
            bool refreshOnExit = false, bool recheckRemoteOnExit = false, bool refreshLogOnExit = false)
        {
            if (!VcsExternalToolLauncher.TryStartProcess(fileName, arguments, workingDirectory, displayName, out var reason, out var process))
            {
                _lastExternalToolError = reason;
                return false;
            }

            ShowMessage($"Opened {displayName}.", false);
            LogVcsOperation("Open External Tool", $"Opened {displayName}: {fileName} {arguments}");

            if (refreshOnExit)
                WatchExternalToolExit(process, recheckRemoteOnExit, refreshLogOnExit);
            else
                process.Dispose();
            return true;
        }

        /// <summary>
        /// 外部工具进程退出后（线程池回调）投递到主线程刷新。只订阅事件，从不等待进程。
        /// </summary>
        private void WatchExternalToolExit(System.Diagnostics.Process process, bool recheckRemote, bool logChanged)
        {
            var fired = 0;
            void OnExit()
            {
                if (Interlocked.Exchange(ref fired, 1) != 0)
                    return;
                try { process.Dispose(); } catch { /* 忽略 */ }
                MainThread.Post(() => OnExternalToolClosed(recheckRemote, logChanged));
            }

            try
            {
                process.EnableRaisingEvents = true;
                process.Exited += (_, __) => OnExit();
                // 订阅前进程已退出时 Exited 可能不再触发
                if (process.HasExited)
                    OnExit();
            }
            catch
            {
                // 拿不到进程状态（例如由已运行的实例接管）：不刷新，用户可手动 Refresh
                try { process.Dispose(); } catch { /* 忽略 */ }
            }
        }

        private void OnExternalToolClosed(bool recheckRemote, bool logChanged)
        {
            VcsProjectWindowIntegration.RequestStatusRefresh();
            if (recheckRemote)
                VcsRemoteStatusMonitor.RequestCheck();

            if (_isDisposed || _adapter == null)
                return;
            // 提交 / 更新会改变提交历史或 BASE：强制刷新 log；还原 / 清理 / 添加只影响本地
            _ = RefreshAllData(logChanged ? LogRefresh.Force : LogRefresh.Skip);
        }

        /// <summary>
        /// 启动 TortoiseProc。参数由 <see cref="VcsExternalToolLauncher"/> 构造：路径含双引号时给出明确原因，
        /// 多路径走 /pathfile。失败原因记录在 <see cref="_lastExternalToolError"/>，由 ShowExternalToolUnavailable 显示。
        /// </summary>
        private bool TryStartTortoise(string command, IReadOnlyList<string> paths, string rootPath, string displayName, string extraArguments = null,
            bool refreshOnExit = false, bool recheckRemoteOnExit = false, bool refreshLogOnExit = false)
        {
            if (!VcsExternalToolLauncher.TryBuildTortoiseArgs(command, paths, out var arguments, out var error))
            {
                _lastExternalToolError = error;
                AgentCoreLog.Warning($"[Version Control][{_currentVcsType}][Open External Tool] Cannot open {displayName}: {error}");
                return false;
            }

            if (!string.IsNullOrEmpty(extraArguments))
                arguments += " " + extraArguments;

            return TryStartExternalProcess("TortoiseProc.exe", arguments, rootPath, displayName, refreshOnExit, recheckRemoteOnExit, refreshLogOnExit);
        }

        private void ShowExternalToolUnavailable(string operation)
        {
            var detail = _lastExternalToolError;
            _lastExternalToolError = null;
            ShowMessage($"{operation} requires an external {_currentVcsType} client. Please install or configure the corresponding desktop VCS tool."
                        + (string.IsNullOrEmpty(detail) ? string.Empty : $"\n{detail}"), true);
        }

        // ===== 文件级外部工具调用方法 =====

        private bool TryOpenExternalDiff(string filePath)
        {
            var rootPath = VcsDetector.GetVcsRootPath();
            // filePath 为空表示"整个工作副本 diff"（按钮无选中态），path 传 VCS 根目录；
            // 否则针对具体文件。TortoiseSVN 的 /command:diff 对目录与文件都适用。
            var targetPath = string.IsNullOrWhiteSpace(filePath) ? rootPath : GetAbsolutePath(filePath);

            switch (_currentVcsType)
            {
                case VcsType.Svn:
                    return TryStartTortoise("diff", new[] { targetPath }, rootPath, "TortoiseSVN diff window");
                case VcsType.Git:
                case VcsType.Perforce:
                    return false; // Git GUI 和 P4V 不支持单文件 diff 窗口
                default:
                    return false;
            }
        }

        private bool TryOpenExternalLog(string filePath)
        {
            var rootPath = VcsDetector.GetVcsRootPath();
            var absolutePath = GetAbsolutePath(filePath);

            switch (_currentVcsType)
            {
                case VcsType.Svn:
                    return TryStartTortoise("log", new[] { absolutePath }, rootPath, "TortoiseSVN log window");
                case VcsType.Git:
                case VcsType.Perforce:
                    return false;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 打开 TortoiseSVN Add 对话框（含 .meta 伴随文件），对话框关闭后自动刷新状态列表。
        /// 返回 true 表示外部工具已成功启动（无论用户在对话框中的操作结果）。
        /// </summary>
        private bool TryOpenExternalAdd(List<string> filePaths)
        {
            var rootPath = VcsDetector.GetVcsRootPath();
            var absolutePaths = ToAbsolutePaths(filePaths);
            if (absolutePaths.Count == 0)
                return false;

            switch (_currentVcsType)
            {
                case VcsType.Svn:
                    return TryStartTortoise("add", absolutePaths, rootPath, "TortoiseSVN add dialog", refreshOnExit: true);
                default:
                    return false;
            }
        }

        private bool TryOpenExternalRevert(List<string> filePaths)
        {
            var rootPath = VcsDetector.GetVcsRootPath();
            var absolutePaths = ToAbsolutePaths(filePaths);
            if (absolutePaths.Count == 0)
                return false;

            switch (_currentVcsType)
            {
                case VcsType.Svn:
                    return TryStartTortoise("revert", absolutePaths, rootPath, "TortoiseSVN revert dialog", refreshOnExit: true);
                case VcsType.Git:
                case VcsType.Perforce:
                    return false;
                default:
                    return false;
            }
        }

        private bool TryOpenExternalCommitFiles(List<string> filePaths)
        {
            var rootPath = VcsDetector.GetVcsRootPath();
            var absolutePaths = ToAbsolutePaths(filePaths);
            if (absolutePaths.Count == 0)
                return false;

            switch (_currentVcsType)
            {
                case VcsType.Svn:
                    // 多个文件通过 /pathfile 传递，不受命令行长度限制
                    return TryStartTortoise("commit", absolutePaths, rootPath, "TortoiseSVN commit window", refreshOnExit: true, refreshLogOnExit: true);
                case VcsType.Git:
                case VcsType.Perforce:
                    return false;
                default:
                    return false;
            }
        }

        private List<string> ToAbsolutePaths(List<string> filePaths)
        {
            return (filePaths ?? new List<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(GetAbsolutePath)
                .ToList();
        }

        private bool SupportsExternalFileTool(string operation)
        {
            // 目前只有 SVN/TortoiseSVN 支持完整的文件级外部工具
            return _currentVcsType == VcsType.Svn;
        }

        private async void OnCheckRemoteClicked()
        {
            if (_adapter == null)
                return;

            _checkRemoteButton.SetEnabled(false);
            try
            {
                if (_syncStatusTimeLabel != null)
                    _syncStatusTimeLabel.text = "Checking remote...";
                // 检查在线程池执行，这里只是等待结果，不阻塞编辑器
                await VcsRemoteStatusMonitor.CheckRemoteStatusAsync(true, CancellationToken.None);
                UpdateSyncStatusBanner(VcsRemoteStatusMonitor.LastStatus);
            }
            finally
            {
                _checkRemoteButton.SetEnabled(true);
            }
        }

        private void OnUpdateRemoteClicked()
        {
            if (_adapter == null || _currentVcsType == VcsType.None)
            {
                ShowMessage("Update failed: no version control adapter is active. Click Refresh to detect the repository.", true);
                return;
            }

            // 更新操作不再弹系统确认框：VCS 页面内触发即代表用户明确知道在操作 VCS。
            // 直接打开对应的外部 VCS 窗口，冲突由外部工具处理，Unity 侧不自动解决。
            if (TryOpenExternalUpdateWindow())
                return;

            ShowExternalToolUnavailable("Update");
        }

        private async void OnStageMultipleFilesClicked(List<string> filePaths)
        {
            await StageFilesAsync(WithMetaCompanions(filePaths, CanStage), "Add or Track Selected");
        }

        private async void OnRevertMultipleFilesClicked(List<string> filePaths)
        {
            if (filePaths == null || filePaths.Count == 0)
                return;

            filePaths = WithMetaCompanions(filePaths, CanRevert);
            await RevertFilesAsync(
                filePaths,
                $"Revert {filePaths.Count} Selected File(s)?",
                $"This will discard local changes in the selected {filePaths.Count} file(s).\n\nThis action cannot be undone.",
                "Revert Selected");
        }

        private async void OnRevertAllClicked()
        {
            if (_adapter == null)
                return;

            // 未版本控制 / 忽略的文件 svn revert 不会处理（也不会删除）：不传给适配器，在结果消息里报告为已跳过
            var (filesToRevert, skippedUnversioned) = PartitionRevertAll(_currentFiles);
            if (filesToRevert.Count == 0)
            {
                ShowMessage(skippedUnversioned.Count > 0
                    ? "No versioned changes to revert. " + DescribeSkippedUnversioned(skippedUnversioned)
                    : "No files to revert.", false);
                return;
            }

            var dialogMessage = "This will discard ALL local changes in the working copy.\n\nThis action cannot be undone.";
            if (skippedUnversioned.Count > 0)
                dialogMessage += $"\n\n{skippedUnversioned.Count} unversioned file(s) will be left untouched.";

            await RevertFilesAsync(
                filesToRevert,
                "Revert All Changes?",
                dialogMessage,
                "Revert All",
                skippedUnversioned);
        }

        /// <summary>
        /// Revert All 的文件划分：未版本控制（Untracked）与忽略（Ignored）的文件不参与还原，单独列为跳过；
        /// 其余（含 Renamed / Copied、Missing、Conflicted）都交给 svn revert。纯函数，供测试。
        /// </summary>
        public static (List<string> revertable, List<string> skippedUnversioned) PartitionRevertAll(IEnumerable<VcsFileStatus> files)
        {
            var revertable = new List<string>();
            var skipped = new List<string>();
            if (files == null)
                return (revertable, skipped);

            foreach (var file in files)
            {
                if (file == null || string.IsNullOrEmpty(file.FilePath))
                    continue;
                if (file.State == VcsFileState.Untracked || file.State == VcsFileState.Ignored)
                    skipped.Add(file.FilePath);
                else
                    revertable.Add(file.FilePath);
            }
            return (revertable, skipped);
        }

        /// <summary>结果消息中的"已跳过未版本控制文件"说明，最多列出前 5 个路径。</summary>
        public static string DescribeSkippedUnversioned(IReadOnlyList<string> skipped)
        {
            if (skipped == null || skipped.Count == 0)
                return string.Empty;
            const int maxListed = 5;
            var listed = string.Join(", ", skipped.Take(maxListed));
            var more = skipped.Count > maxListed ? $" and {skipped.Count - maxListed} more" : string.Empty;
            return $"Skipped {skipped.Count} unversioned file(s) (revert does not touch or delete them): {listed}{more}.";
        }

        private async Task RevertFilesAsync(List<string> filesToRevert, string title, string message, string okButton,
            IReadOnlyList<string> skippedUnversioned = null)
        {
            if (_adapter == null)
                return;

            if (filesToRevert == null || filesToRevert.Count == 0)
            {
                ShowMessage("No files to revert.", false);
                return;
            }

            bool confirmed = EditorUtility.DisplayDialog(title, message, okButton, "Cancel");
            if (!confirmed)
                return;

            try
            {
                LogVcsOperation("Revert", $"Reverting {filesToRevert.Count} file(s).");
                var result = await _adapter.RevertFilesAsync(filesToRevert, CancellationToken.None);
                LogVcsResult("Revert", result);
                // 适配器自身跳过的（例如状态刷新后才变成未版本控制的文件）也并入跳过说明
                var skipped = new List<string>(skippedUnversioned ?? Array.Empty<string>());
                if (result.SkippedFiles != null)
                {
                    foreach (var path in result.SkippedFiles.Keys)
                    {
                        if (!skipped.Contains(path, StringComparer.OrdinalIgnoreCase))
                            skipped.Add(path);
                    }
                }
                var skippedNote = skipped.Count > 0 ? "\n" + DescribeSkippedUnversioned(skipped) : string.Empty;

                if (result.Success)
                {
                    var revertedCount = Math.Max(0, filesToRevert.Count - (result.SkippedFiles?.Count ?? 0) - (result.FailedFiles?.Count ?? 0));
                    var failedNote = (result.FailedFiles?.Count ?? 0) > 0
                        ? $"\nFailed: {string.Join(", ", result.FailedFiles.Select(kv => $"{kv.Key} ({kv.Value})"))}"
                        : string.Empty;
                    ShowMessage($"Reverted {revertedCount} file(s) successfully.{skippedNote}{failedNote}", failedNote.Length > 0);
                    await RefreshAllData();
                }
                else
                {
                    ShowMessage($"Revert failed: {GetOperationFailureMessage(result)}{skippedNote}", true);
                }
            }
            catch (Exception ex)
            {
                ShowMessage($"Error reverting: {ex.Message}", true);
            }
        }

        #endregion

        #region View Diff

        private void OnViewDiffClicked()
        {
            // 呼出对应 VCS 软件的图形 diff 功能，不把 diff 文本写入 Console
            //（Console 输出受日志分级开关影响、且极易被淹没忽略）。
            // 目前仅维护 SVN（TortoiseSVN）；Git / Perforce 不做，按钮在这两种 VCS 下已被禁用
            //（见 RefreshAllData 中 _viewDiffButton.SetEnabled 的 SupportsExternalFileTool 判定）。
            // 有单选文件则 diff 该文件，否则对整个工作副本做 diff（filePath 传 null）。
            var selectedFiles = GetSelectedFilePaths();
            string filePath = selectedFiles.Count == 1 ? selectedFiles.First() : null;

            if (TryOpenExternalDiff(filePath))
                return;

            // SVN 但外部工具不可用（TortoiseSVN 未安装/未配置）时提示
            ShowExternalToolUnavailable("Diff");
        }

        #endregion

        #region UI Helpers

        private string GetOperationFailureMessage(VcsOperationResult result)
        {
            if (result == null)
                return "Unknown error";

            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                return result.ErrorMessage.Trim();

            if (!string.IsNullOrWhiteSpace(result.Message))
                return result.Message.Trim();

            if (!string.IsNullOrWhiteSpace(result.RawOutput))
                return result.RawOutput.Trim();

            return "Unknown error";
        }

        private void LogVcsOperation(string operation, string message)
        {
            AgentCore.Editor.Utils.AgentCoreLog.Info($"[Version Control][{_currentVcsType}][{operation}] {message}");
        }

        private void LogVcsResult(string operation, VcsOperationResult result)
        {
            if (result == null)
            {
                AgentCoreLog.Warning($"[Version Control][{_currentVcsType}][{operation}] No operation result returned.");
                return;
            }

            var details = new List<string>
            {
                $"Operation: {result.OperationName ?? operation}",
                $"Success: {result.Success}"
            };

            if (!string.IsNullOrWhiteSpace(result.CommandLine))
                details.Add($"Command: {result.CommandLine}");

            if (!string.IsNullOrWhiteSpace(result.Message))
                details.Add($"Message: {result.Message}");

            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                details.Add($"Error: {result.ErrorMessage.Trim()}");

            if (result.AffectedFiles != null && result.AffectedFiles.Count > 0)
                details.Add($"AffectedFiles: {string.Join(", ", result.AffectedFiles)}");

            if (result.ConflictedFiles != null && result.ConflictedFiles.Count > 0)
                details.Add($"ConflictedFiles: {string.Join(", ", result.ConflictedFiles)}");

            if (result.LogLines != null && result.LogLines.Count > 0)
                details.Add($"Log:\n{string.Join("\n", result.LogLines)}");

            if (!string.IsNullOrWhiteSpace(result.RawOutput))
                details.Add($"RawOutput:\n{result.RawOutput.Trim()}");

            var logText = $"[Version Control][{_currentVcsType}][{operation}]\n{string.Join("\n", details)}";
            if (result.Success)
                AgentCore.Editor.Utils.AgentCoreLog.Info(logText);
            else
                AgentCoreLog.Warning(logText);
        }

        private void ShowMessage(string message, bool isError)
        {
            _messageContainer.Clear();
            _messageContainer.style.display = DisplayStyle.Flex;

            var label = new Label(message);
            label.style.color = isError ? new Color(1f, 0.4f, 0.4f) : new Color(0.4f, 1f, 0.4f);
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.paddingTop = 5;
            label.style.paddingBottom = 5;
            label.style.paddingLeft = 5;
            label.style.paddingRight = 5;

            _messageContainer.Add(label);

            // 自动隐藏成功消息
            if (!isError)
            {
                schedule.Execute(() => HideMessage()).ExecuteLater(5000);
            }
        }

        private void HideMessage()
        {
            _messageContainer.style.display = DisplayStyle.None;
        }

        private void UpdateSyncStatusBanner(VcsSyncStatus status)
        {
            if (_syncStatusBanner == null || _syncStatusLabel == null)
                return;

            // 适配器就绪后横幅始终可见，保证 Check Remote 按钮与 "上次检查时间" 随时可用
            if (_adapter == null)
            {
                _syncStatusBanner.style.display = DisplayStyle.None;
                return;
            }

            _syncStatusBanner.style.display = DisplayStyle.Flex;
            _syncStatusBanner.RemoveFromClassList("has-remote-changes");
            _syncStatusBanner.RemoveFromClassList("is-clean");
            _syncStatusBanner.RemoveFromClassList("has-error");
            UpdateSyncStatusTimeLabel();

            if (status == null)
            {
                _syncStatusLabel.text = VcsRemoteStatusMonitor.LastError is string checkError && !string.IsNullOrEmpty(checkError)
                    ? $"Remote check failed: {DescribeRemoteError(checkError)}"
                    : "Remote status has not been checked yet. Click Check Remote to fetch now.";
                // 未检查过时不禁用 Update：用户想更新就直接执行
                _updateRemoteButton?.SetEnabled(true);
                return;
            }

            // Update 按钮始终可用：远端检查失败（离线 / 认证）或结果过期都不应阻止用户自己打开更新窗口
            _updateRemoteButton?.SetEnabled(true);

            if (!status.Success)
            {
                _syncStatusBanner.AddToClassList("has-error");
                _syncStatusLabel.text = $"Remote check failed: {DescribeRemoteError(status.ErrorMessage)}";
                return;
            }

            if (status.HasRemoteChanges)
            {
                _syncStatusBanner.AddToClassList("has-remote-changes");
                _syncStatusLabel.text = status.Summary;
            }
            else
            {
                _syncStatusBanner.AddToClassList("is-clean");
                _syncStatusLabel.text = status.Summary ?? "Working copy is up to date.";
            }

            // 最近一次检查失败但保留了上次成功结果：附上失败原因
            var lastError = VcsRemoteStatusMonitor.LastError;
            if (!string.IsNullOrEmpty(lastError))
                _syncStatusLabel.text += $"\n(Latest check failed: {DescribeRemoteError(lastError)})";
        }

        /// <summary>远端检查失败原因 + SVN 处理提示（认证 / 离线 / 需要升级等）。</summary>
        private string DescribeRemoteError(string error)
        {
            if (_currentVcsType != VcsType.Svn || string.IsNullOrEmpty(error))
                return error;
            // Describe 幂等：LastError / 命令层已附带提示时不重复
            return AgentCore.Editor.Workspace.Resolution.SvnErrorHints.Describe(error, VcsDetector.GetVcsRootPath());
        }

        private void UpdateSyncStatusTimeLabel()
        {
            if (_syncStatusTimeLabel != null)
                _syncStatusTimeLabel.text = VcsRemoteStatusMonitor.FormatLastChecked();
        }

        private string GetStateBadge(VcsFileState state)
        {
            return state switch
            {
                VcsFileState.Modified => "M",
                VcsFileState.Added => "A",
                VcsFileState.Deleted => "D",
                VcsFileState.Renamed => "R",
                VcsFileState.Copied => "C",
                VcsFileState.Untracked => "?",
                VcsFileState.Ignored => "I",
                VcsFileState.Conflicted => "!",
                VcsFileState.Missing => "!",
                _ => " "
            };
        }

        #endregion

        #region Lifecycle

        private IVcsAdapter CreateAdapter(VcsType vcsType, string rootPath)
        {
            return vcsType switch
            {
                VcsType.Svn => new SvnAdapter(rootPath),
                VcsType.Perforce => new PerforceAdapter(rootPath),
                VcsType.Git => new GitAdapter(rootPath),
                _ => null
            };
        }

        /// <summary>
        /// 面板激活时刷新数据
        /// </summary>
        public void OnActivated()
        {
            _isPanelActive = true;

            if (!VcsSettings.AutoRefreshOnOpen)
                return;

            if (_adapter != null && _currentVcsType != VcsType.None)
            {
                _ = RefreshAllData(LogRefresh.Auto);
            }
        }

        /// <summary>
        /// 面板停用时取消正在进行的操作
        /// </summary>
        public void OnDeactivated()
        {
            _isPanelActive = false;
            _cts?.Cancel();
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            _isDisposed = true;
            _isPanelActive = false;
            EditorApplication.update -= OnEditorUpdatePollCommits;
            VcsRemoteStatusMonitor.StatusChanged -= UpdateSyncStatusBanner;
            VcsRemoteStatusMonitor.StatusChanged -= OnRemoteStatusChangedForLog;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        /// <summary>
        /// EditorApplication.update 回调 — 周期性静默轮询 commit 列表。
        /// 完全不阻断用户操作：不禁用按钮、不显示 loading、不改变滚动位置。
        /// </summary>
        private void OnEditorUpdatePollCommits()
        {
            if (!_isPanelActive)
                return;

            if (_adapter == null || _currentVcsType == VcsType.None)
                return;

            // SVN 不定时轮询 svn log（每次都访问服务器）：由远端检查报告的 HEAD 变化触发，见 OnRemoteStatusChangedForLog
            if (_currentVcsType == VcsType.Svn)
                return;

            if (_isBackgroundPolling || _isLoadingMoreCommits)
                return;

            if (DateTime.UtcNow - _lastCommitPollUtc < GetCommitPollInterval())
                return;

            _ = PollCommitListSilentlyAsync();
        }

        /// <summary>
        /// 远端检查（<see cref="VcsRemoteStatusMonitor"/>，主线程回调）报告了新的 HEAD 版本号时静默刷新提交历史。
        /// 离线退避期内跳过；面板不活动时只记下版本号，下次打开时按 Auto 刷新。
        /// </summary>
        private void OnRemoteStatusChangedForLog(VcsSyncStatus status)
        {
            if (_isDisposed || status == null || !status.Success || string.IsNullOrEmpty(status.RemoteRevision))
                return;
            if (string.Equals(status.RemoteRevision, _lastSeenRemoteRevision, StringComparison.Ordinal))
                return;
            _lastSeenRemoteRevision = status.RemoteRevision;

            if (!_isPanelActive || _currentVcsType != VcsType.Svn || _adapter == null)
                return;
            if (_isBackgroundPolling || _isLoadingMoreCommits || IsCommitLogInBackoff())
                return;
            // 已显示的最新提交就是这个版本：无需再访问服务器
            if (_loadedCommits.Count > 0 && string.Equals(_loadedCommits[0].Revision, status.RemoteRevision, StringComparison.Ordinal))
                return;

            _ = PollCommitListSilentlyAsync();
        }

        /// <summary>
        /// 轮询间隔：基础间隔 × 2^连续失败次数，上限 <see cref="MaxCommitPollBackoffSeconds"/>。
        /// </summary>
        private TimeSpan GetCommitPollInterval()
        {
            var baseSeconds = Math.Max(1, VcsSettings.CommitListRefreshIntervalSeconds);
            var seconds = baseSeconds * Math.Pow(2, Math.Min(_commitPollFailures, 10));
            return TimeSpan.FromSeconds(Math.Min(seconds, Math.Max(baseSeconds, MaxCommitPollBackoffSeconds)));
        }

        /// <summary>
        /// 静默拉取最新 commit 列表，仅在检测到新提交时更新 UI。
        /// 不触发任何 loading 指示器，不改变用户当前的滚动位置或选择状态。
        /// </summary>
        private async Task PollCommitListSilentlyAsync()
        {
            _isBackgroundPolling = true;
            _lastCommitPollUtc = DateTime.UtcNow;

            try
            {
                var adapter = _adapter;
                if (adapter == null)
                    return;
                var queryCount = GetCommitQueryCount();
                var (freshCommits, error) = await FetchLogAsync(adapter, queryCount, CancellationToken.None);
                if (_isDisposed)
                    return;

                if (!string.IsNullOrEmpty(error) || freshCommits == null || freshCommits.Count == 0)
                {
                    // 失败（离线 / 认证失败 / 超时）：退避，下次间隔翻倍；静默刷新不弹消息，只在摘要行显示错误
                    RecordCommitLogFailure();
                    if (!string.IsNullOrEmpty(error))
                    {
                        _commitLogError = error;
                        RenderCommitList();
                    }
                    return;
                }
                _commitPollFailures = 0;
                if (_commitLogError != null)
                {
                    _commitLogError = null;
                    RenderCommitList();
                }

                // 对比最新 revision — 只有真正有新提交时才更新 UI
                var currentLatestRevision = _loadedCommits.Count > 0 ? _loadedCommits[0].Revision : null;
                var freshLatestRevision = freshCommits[0].Revision;

                if (string.Equals(currentLatestRevision, freshLatestRevision, StringComparison.Ordinal))
                    return; // 没有新提交，跳过 UI 更新

                // 有新提交 — 静默更新列表（保持当前可见数量不变）
                var previousVisibleCount = _visibleCommitCount;
                _loadedCommits = freshCommits;
                _visibleCommitCount = previousVisibleCount;
                RenderCommitList();
            }
            catch (OperationCanceledException)
            {
                // 忽略取消
            }
            catch (Exception)
            {
                // 静默失败 — 后台轮询不应打扰用户
            }
            finally
            {
                _isBackgroundPolling = false;
            }
        }

        #endregion
    }
}
