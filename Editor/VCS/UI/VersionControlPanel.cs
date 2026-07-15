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
        private Button _cleanupButton;
        private Button _refreshCommitsButton;
        private Button _viewDiffButton;
        private VisualElement _messageContainer;
        private VisualElement _syncStatusBanner;
        private Label _syncStatusLabel;
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

        // 后台静默轮询 commit 列表
        private DateTime _lastCommitPollUtc = DateTime.MinValue;
        private bool _isBackgroundPolling;
        private bool _isPanelActive;

        public VersionControlPanel()
        {
            AddToClassList(UssClassName);
            VcsRemoteStatusMonitor.StatusChanged += UpdateSyncStatusBanner;
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

        private void DetectAndInitialize()
        {
            _currentVcsType = VcsDetector.DetectVcs();

            if (_currentVcsType == VcsType.None)
            {
                _vcsTypeLabel.text = "No VCS detected";
                ShowMessage("No version control system detected in this project.", true);
                _refreshButton.SetEnabled(false);
                _cleanupButton.SetEnabled(false);
                return;
            }

            var rootPath = VcsDetector.GetVcsRootPath();
            _adapter = CreateAdapter(_currentVcsType, rootPath);

            if (_adapter == null || !_adapter.IsAvailable())
            {
                _vcsTypeLabel.text = $"{_currentVcsType} (command not available)";
                ShowMessage($"{_currentVcsType} detected but command-line tool is not available. Please install {_currentVcsType} CLI.", true);
                _refreshButton.SetEnabled(false);
                _cleanupButton.SetEnabled(false);
                return;
            }

            _vcsTypeLabel.text = $"Type: {_currentVcsType}";
            _refreshButton.SetEnabled(true);
            _cleanupButton.SetEnabled(true);

            _ = RefreshAllData();
        }

        private async void OnRefreshClicked()
        {
            if (_adapter == null || _currentVcsType == VcsType.None)
            {
                DetectAndInitialize();
                return;
            }

            await RefreshAllData();
        }

        private void OnCleanupProjectClicked()
        {
            if (_adapter == null || _currentVcsType == VcsType.None)
            {
                ShowMessage("Cleanup failed: no version control adapter is active. Click Refresh to detect the repository first.", true);
                return;
            }

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                ShowMessage("Cleanup is blocked while Unity is compiling or importing assets. Please wait until Unity is idle.", true);
                return;
            }

            var rootPath = VcsDetector.GetVcsRootPath();
            var confirmed = EditorUtility.DisplayDialog(
                "Cleanup Project Working Copy?",
                $"This will open the external {_currentVcsType} cleanup tool for the whole project working copy.\n\nRoot:\n{rootPath}\n\nUse this when the working copy is locked, interrupted, or needs metadata cleanup.",
                "Open Cleanup",
                "Cancel");

            if (!confirmed)
                return;

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
                var commits = await _adapter.GetLogAsync(queryCount, _cts?.Token ?? CancellationToken.None);
                UpdateCommitList(commits);
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

        private async Task RefreshAllData()
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
                var logTask = _adapter.GetLogAsync(GetCommitQueryCount(), ct);
                var syncStatusTask = VcsSettings.CheckRemoteStatusOnRefresh
                    ? VcsRemoteStatusMonitor.CheckRemoteStatusAsync(true, ct)
                    : Task.FromResult(VcsRemoteStatusMonitor.LastStatus);

                await Task.WhenAll(branchTask, statusTask, logTask, syncStatusTask);

                if (ct.IsCancellationRequested)
                    return;

                // 更新分支信息
                var branchInfo = await branchTask;
                if (branchInfo.Success)
                {
                    _branchLabel.text = $"Branch: {branchInfo.CurrentBranch ?? "unknown"}";
                    _branchLabel.tooltip = branchInfo.CurrentBranch ?? "unknown";
                    _revisionLabel.text = $"Revision: {branchInfo.CurrentRevision ?? "unknown"}";
                    _revisionLabel.tooltip = branchInfo.CurrentRevision ?? "unknown";
                }

                // 更新状态列表
                var statusResult = await statusTask;
                if (statusResult.Success)
                {
                    _currentFiles = statusResult.Files ?? new List<VcsFileStatus>();
                    UpdateStatusList(_currentFiles);
                    _viewDiffButton.SetEnabled(_currentFiles.Count > 0);
                }

                // 更新提交历史
                var commits = await logTask;
                UpdateCommitList(commits);

                UpdateSyncStatusBanner(await syncStatusTask);
            }
            catch (OperationCanceledException)
            {
                // 忽略取消异常
            }
            catch (Exception ex)
            {
                ShowMessage($"Error refreshing data: {ex.Message}", true);
                Debug.LogError($"[VersionControlPanel] Refresh error: {ex}");
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
                .Where(f => f.State != VcsFileState.Ignored
                         && f.State != VcsFileState.Untracked
                         && f.State != VcsFileState.Conflicted
                         && f.State != VcsFileState.Missing)
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

            // Selection handled by Working Copy Status list
            
            // 尝试使用外部工具 Revert
            if (TryOpenExternalRevert(filePath))
                return;
            
            // 外部工具不可用时显示提示
            ShowExternalToolUnavailable("Revert");
        }

        private async void OnStageSingleFileClicked(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;

            // 尝试使用外部工具添加文件，等待外部进程退出后自动刷新状态列表
            if (await TryOpenExternalAddAsync(filePath))
                return;

            // 外部工具不可用时显示提示
            ShowExternalToolUnavailable("Add");
        }

        private void OnCommitSingleFileClicked(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;

            // Selection handled by Working Copy Status list
            
            // 尝试使用外部工具提交单个文件
            if (TryOpenExternalCommitFile(filePath))
                return;
            
            // 外部工具不可用时显示提示
            ShowExternalToolUnavailable("Commit File");
        }

        private void OnCommitMultipleFilesClicked(List<string> filePaths)
        {
            if (filePaths == null || filePaths.Count == 0)
                return;

            if (TryOpenExternalCommitFiles(filePaths))
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
                        Debug.LogWarning($"[Version Control] Delete succeeded but failed to clean VCS staging record for '{filePath}': {unstageResult.ErrorMessage}");
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


        private async Task RunFileCommandAsync(string operation, string command, string arguments, string filePath, bool refreshAfterSuccess = false)
        {
            try
            {
                LogVcsOperation(operation, $"Running {command} {arguments}");
                var result = await VcsCommandExecutor.ExecuteAsync(command, arguments, VcsDetector.GetVcsRootPath(), ct: CancellationToken.None);
                var output = string.Join("\n", new[] { result.Output, result.Error }.Where(text => !string.IsNullOrWhiteSpace(text))).Trim();

                if (result.Success)
                {
                    AgentCore.Editor.Utils.AgentCoreLog.Info($"[Version Control][{_currentVcsType}][{operation}] {filePath}\nCommand: {command} {arguments}\n{output}");
                    ShowMessage($"{operation} for '{filePath}' logged to Console.", false);
                    if (refreshAfterSuccess)
                        await RefreshAllData();
                }
                else
                {
                    Debug.LogWarning($"[Version Control][{_currentVcsType}][{operation}] {filePath}\nCommand: {command} {arguments}\nError: {result.ErrorMessage}\n{output}");
                    ShowMessage($"{operation} failed: {result.ErrorMessage}", true);
                }
            }
            catch (Exception ex)
            {
                ShowMessage($"{operation} failed: {ex.Message}", true);
            }
        }

        private async Task<CommandResult> ExecuteSvnWithCleanupRetryAsync(string operation, string arguments, string rootPath)
        {
            var result = await VcsCommandExecutor.ExecuteAsync("svn", arguments, rootPath, ct: CancellationToken.None);
            if (!IsSvnWorkingCopyLocked(result))
                return result;

            LogVcsOperation(operation, "SVN working copy is locked. Running svn cleanup and retrying once.");
            var cleanupResult = await VcsCommandExecutor.ExecuteAsync("svn", "cleanup", rootPath, ct: CancellationToken.None);
            if (!cleanupResult.Success)
            {
                Debug.LogWarning($"[Version Control][{_currentVcsType}][{operation}] SVN cleanup failed.\nCommand: svn cleanup\nError: {cleanupResult.ErrorMessage}\n{cleanupResult.Output}\n{cleanupResult.Error}");
                return cleanupResult;
            }

            return await VcsCommandExecutor.ExecuteAsync("svn", arguments, rootPath, ct: CancellationToken.None);
        }

        private bool IsSvnWorkingCopyLocked(CommandResult result)
        {
            if (result == null)
                return false;

            var combined = $"{result.ErrorMessage}\n{result.Output}\n{result.Error}";
            return combined.IndexOf("E155004", StringComparison.OrdinalIgnoreCase) >= 0
                || combined.IndexOf("working copy", StringComparison.OrdinalIgnoreCase) >= 0 && combined.IndexOf("locked", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private string BuildSvnFailureMessage(CommandResult result)
        {
            if (IsSvnWorkingCopyLocked(result))
                return $"{result.ErrorMessage} Please run svn cleanup on the working copy root and retry.";

            return result.ErrorMessage;
        }

        private string GetAbsolutePath(string filePath)
        {
            var normalized = filePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.Combine(VcsDetector.GetVcsRootPath(), normalized));
        }

        private bool IsUnityAssetPath(string filePath)
        {
            return !string.IsNullOrWhiteSpace(filePath) && filePath.Replace('\\', '/').StartsWith("Assets/", StringComparison.Ordinal);
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
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(filePath);
            if (asset == null)
            {
                ShowMessage($"Unity asset not found: {filePath}", true);
                return;
            }

            EditorGUIUtility.PingObject(asset);
            Selection.activeObject = asset;
        }

        private void UpdateCommitList(List<VcsCommit> commits)
        {
            _loadedCommits = commits ?? new List<VcsCommit>();
            _visibleCommitCount = Mathf.Clamp(_visibleCommitCount, InitialCommitDisplayCount, Mathf.Max(InitialCommitDisplayCount, _loadedCommits.Count));
            _lastCommitPollUtc = DateTime.UtcNow;
            RenderCommitList();
        }

        private void RenderCommitList()
        {
            _commitList.Clear();

            if (_loadedCommits == null || _loadedCommits.Count == 0)
            {
                _commitHistorySummaryLabel.text = "No commit history available";
                var noCommits = new Label("No commit history available");
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
                var commits = await _adapter.GetLogAsync(queryCount, CancellationToken.None);
                _loadedCommits = commits ?? _loadedCommits;
                _visibleCommitCount = Mathf.Min(_visibleCommitCount + CommitLoadBatchSize, _loadedCommits.Count);
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
                    return TryStartExternalProcess("TortoiseProc.exe", $"/command:commit /path:\"{rootPath}\" /logmsg:\"{EscapeExternalArgument(message)}\"", rootPath, "TortoiseSVN commit window");
                case VcsType.Git:
                    return TryStartExternalProcess("git", "gui", rootPath, "Git GUI commit window");
                case VcsType.Perforce:
                    return TryStartExternalProcess("p4v", $"-cmd submit \"{rootPath}\"", rootPath, "P4V submit window");
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
                    return TryStartExternalProcess("TortoiseProc.exe", $"/command:update /path:\"{rootPath}\"", rootPath, "TortoiseSVN update window");
                case VcsType.Git:
                    return TryStartExternalProcess("git", "gui", rootPath, "Git GUI window");
                case VcsType.Perforce:
                    return TryStartExternalProcess("p4v", $"-cmd sync \"{rootPath}\"", rootPath, "P4V sync window");
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
                    return TryStartExternalProcess("TortoiseProc.exe", $"/command:cleanup /path:\"{rootPath}\"", rootPath, "TortoiseSVN cleanup options dialog");
                case VcsType.Git:
                    return TryStartExternalProcess("git", "gui", rootPath, "Git GUI window");
                case VcsType.Perforce:
                    return TryStartExternalProcess("p4v", $"-cmd reconcile \"{rootPath}\"", rootPath, "P4V reconcile window");
                default:
                    return false;
            }
        }

        private bool TryStartExternalProcess(string fileName, string arguments, string workingDirectory, string displayName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(workingDirectory))
                    return false;

                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = true,
                    WorkingDirectory = workingDirectory
                };
                System.Diagnostics.Process.Start(startInfo);
                ShowMessage($"Opened {displayName}.", false);
                LogVcsOperation("Open External Tool", $"Opened {displayName}: {fileName} {arguments}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Version Control][{_currentVcsType}][Open External Tool] Failed to open {displayName}. Command: {fileName} {arguments}. Error: {ex.Message}");
                return false;
            }
        }

        private string EscapeExternalArgument(string value)
        {
            return (value ?? string.Empty).Replace("\"", "\\\"");
        }

        private void ShowExternalToolUnavailable(string operation)
        {
            ShowMessage($"{operation} requires an external {_currentVcsType} client. Please install or configure the corresponding desktop VCS tool.", true);
        }

        // ===== 文件级外部工具调用方法 =====

        private bool TryOpenExternalDiff(string filePath)
        {
            var rootPath = VcsDetector.GetVcsRootPath();
            var absolutePath = GetAbsolutePath(filePath);
            
            switch (_currentVcsType)
            {
                case VcsType.Svn:
                    return TryStartExternalProcess("TortoiseProc.exe", $"/command:diff /path:\"{absolutePath}\"", rootPath, "TortoiseSVN diff window");
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
                    return TryStartExternalProcess("TortoiseProc.exe", $"/command:log /path:\"{absolutePath}\"", rootPath, "TortoiseSVN log window");
                case VcsType.Git:
                case VcsType.Perforce:
                    return false;
                default:
                    return false;
            }
        }

        private bool TryOpenExternalAdd(string filePath)
        {
            var rootPath = VcsDetector.GetVcsRootPath();
            var absolutePath = GetAbsolutePath(filePath);
            
            switch (_currentVcsType)
            {
                case VcsType.Svn:
                    return TryStartExternalProcess("TortoiseProc.exe", $"/command:add /path:\"{absolutePath}\"", rootPath, "TortoiseSVN add dialog");
                case VcsType.Git:
                case VcsType.Perforce:
                    return false;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 启动外部 Add 工具，等待外部进程退出后自动刷新文件状态列表。
        /// 返回 true 表示外部工具已成功启动（无论用户在对话框中的操作结果）。
        /// </summary>
        private async Task<bool> TryOpenExternalAddAsync(string filePath)
        {
            var rootPath = VcsDetector.GetVcsRootPath();
            var absolutePath = GetAbsolutePath(filePath);

            string fileName;
            string arguments;

            switch (_currentVcsType)
            {
                case VcsType.Svn:
                    fileName = "TortoiseProc.exe";
                    arguments = $"/command:add /path:\"{absolutePath}\"";
                    break;
                default:
                    return false;
            }

            if (string.IsNullOrWhiteSpace(rootPath))
                return false;

            try
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = true,
                    WorkingDirectory = rootPath
                };

                var process = System.Diagnostics.Process.Start(startInfo);
                ShowMessage("Opened TortoiseSVN add dialog.", false);
                LogVcsOperation("Open External Tool", $"Opened TortoiseSVN add dialog: {fileName} {arguments}");

                if (process != null)
                {
                    // 等待外部进程退出，然后自动刷新文件状态列表
                    var tcs = new TaskCompletionSource<bool>();
                    process.EnableRaisingEvents = true;
                    process.Exited += (_, __) => tcs.TrySetResult(true);

                    // 如果进程在订阅事件前已经退出，直接完成
                    if (process.HasExited)
                        tcs.TrySetResult(true);

                    await tcs.Task;
                    await RefreshAllData();
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Version Control][{_currentVcsType}][Open External Tool] Failed to open TortoiseSVN add dialog. Error: {ex.Message}");
                return false;
            }
        }

        private bool TryOpenExternalRevert(string filePath)
        {
            var rootPath = VcsDetector.GetVcsRootPath();
            var absolutePath = GetAbsolutePath(filePath);
            
            switch (_currentVcsType)
            {
                case VcsType.Svn:
                    return TryStartExternalProcess("TortoiseProc.exe", $"/command:revert /path:\"{absolutePath}\"", rootPath, "TortoiseSVN revert dialog");
                case VcsType.Git:
                case VcsType.Perforce:
                    return false;
                default:
                    return false;
            }
        }

        private bool TryOpenExternalRemove(string filePath)
        {
            var rootPath = VcsDetector.GetVcsRootPath();
            var absolutePath = GetAbsolutePath(filePath);
            
            switch (_currentVcsType)
            {
                case VcsType.Svn:
                    return TryStartExternalProcess("TortoiseProc.exe", $"/command:remove /path:\"{absolutePath}\"", rootPath, "TortoiseSVN remove dialog");
                case VcsType.Git:
                case VcsType.Perforce:
                    return false;
                default:
                    return false;
            }
        }

        private bool TryOpenExternalCommitFile(string filePath)
        {
            var rootPath = VcsDetector.GetVcsRootPath();
            var absolutePath = GetAbsolutePath(filePath);
            
            switch (_currentVcsType)
            {
                case VcsType.Svn:
                    return TryStartExternalProcess("TortoiseProc.exe",
                        $"/command:commit /path:\"{absolutePath}\"",
                        rootPath,
                        "TortoiseSVN commit window");
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
            if (filePaths == null || filePaths.Count == 0)
                return false;

            var absolutePaths = filePaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(GetAbsolutePath)
                .ToList();

            if (absolutePaths.Count == 0)
                return false;

            switch (_currentVcsType)
            {
                case VcsType.Svn:
                    return TryStartExternalProcess("TortoiseProc.exe",
                        $"/command:commit /path:\"{string.Join("*", absolutePaths)}\"",
                        rootPath,
                        "TortoiseSVN commit window");
                case VcsType.Git:
                case VcsType.Perforce:
                    return false;
                default:
                    return false;
            }
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
                UpdateSyncStatusBanner(new VcsSyncStatus { Success = true, Summary = "Checking remote status..." });
                var status = await VcsRemoteStatusMonitor.CheckRemoteStatusAsync(true, CancellationToken.None);
                UpdateSyncStatusBanner(status);
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

            var status = VcsRemoteStatusMonitor.LastStatus;
            if (status != null && status.HasRemoteChanges)
            {
                var message = $"{status.Summary}\n\nThis will open the corresponding external VCS update/sync window. Local conflicts are not auto-resolved in Unity.";
                if (!EditorUtility.DisplayDialog("Version Control Update", message, "Open Update Window", "Cancel"))
                    return;
            }

            if (TryOpenExternalUpdateWindow())
                return;

            ShowExternalToolUnavailable("Update");
        }

        private async void OnStageMultipleFilesClicked(List<string> filePaths)
        {
            await StageFilesAsync(filePaths, "Add or Track Selected");
        }

        private async void OnRevertMultipleFilesClicked(List<string> filePaths)
        {
            if (filePaths == null || filePaths.Count == 0)
                return;

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

            var filesToRevert = _currentFiles?.Select(f => f.FilePath).ToList();
            if (filesToRevert == null || filesToRevert.Count == 0)
            {
                ShowMessage("No files to revert.", false);
                return;
            }

            await RevertFilesAsync(
                filesToRevert,
                "Revert All Changes?",
                "This will discard ALL local changes in the working copy.\n\nThis action cannot be undone.",
                "Revert All");
        }

        private async Task RevertFilesAsync(List<string> filesToRevert, string title, string message, string okButton)
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
                if (result.Success)
                {
                    ShowMessage($"Reverted {filesToRevert.Count} file(s) successfully.", false);
                    await RefreshAllData();
                }
                else
                {
                    ShowMessage($"Revert failed: {GetOperationFailureMessage(result)}", true);
                }
            }
            catch (Exception ex)
            {
                ShowMessage($"Error reverting: {ex.Message}", true);
            }
        }

        #endregion

        #region View Diff

        private async void OnViewDiffClicked()
        {
            var selectedFiles = GetSelectedFilePaths();
            string filePath = selectedFiles.Count == 1 ? selectedFiles.First() : null;
            await ShowDiffAsync(filePath);
        }

        private async Task ShowDiffAsync(string filePath)
        {
            if (_adapter == null)
                return;

            _viewDiffButton.SetEnabled(false);

            try
            {
                var diff = await _adapter.GetDiffAsync(filePath, CancellationToken.None);

                if (string.IsNullOrEmpty(diff))
                {
                    ShowMessage("No differences to display.", false);
                }
                else
                {
                    AgentCore.Editor.Utils.AgentCoreLog.Info($"[Version Control] Diff Output{(string.IsNullOrEmpty(filePath) ? string.Empty : $" ({filePath})")}:\n{diff}");
                    ShowMessage(string.IsNullOrEmpty(filePath) ? "Diff output logged to Console." : $"Diff for '{filePath}' logged to Console.", false);
                }
            }
            catch (Exception ex)
            {
                ShowMessage($"Error getting diff: {ex.Message}", true);
            }
            finally
            {
                _viewDiffButton.SetEnabled(true);
            }
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
                Debug.LogWarning($"[Version Control][{_currentVcsType}][{operation}] No operation result returned.");
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
                Debug.LogWarning(logText);
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

            if (status == null)
            {
                _syncStatusBanner.style.display = DisplayStyle.None;
                return;
            }

            _syncStatusBanner.style.display = DisplayStyle.Flex;
            _syncStatusBanner.RemoveFromClassList("has-remote-changes");
            _syncStatusBanner.RemoveFromClassList("is-clean");
            _syncStatusBanner.RemoveFromClassList("has-error");

            if (!status.Success)
            {
                _syncStatusBanner.AddToClassList("has-error");
                _syncStatusLabel.text = $"Remote check failed: {status.ErrorMessage}";
                _updateRemoteButton?.SetEnabled(false);
                return;
            }

            if (status.HasRemoteChanges)
            {
                _syncStatusBanner.AddToClassList("has-remote-changes");
                _syncStatusLabel.text = status.Summary;
                _updateRemoteButton?.SetEnabled(true);
            }
            else
            {
                _syncStatusBanner.AddToClassList("is-clean");
                _syncStatusLabel.text = status.Summary ?? "Working copy is up to date.";
                _updateRemoteButton?.SetEnabled(false);
            }
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
                _ = RefreshAllData();
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
            _isPanelActive = false;
            EditorApplication.update -= OnEditorUpdatePollCommits;
            VcsRemoteStatusMonitor.StatusChanged -= UpdateSyncStatusBanner;
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

            if (_isBackgroundPolling || _isLoadingMoreCommits)
                return;

            var interval = TimeSpan.FromSeconds(VcsSettings.CommitListRefreshIntervalSeconds);
            if (DateTime.UtcNow - _lastCommitPollUtc < interval)
                return;

            _ = PollCommitListSilentlyAsync();
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
                var queryCount = GetCommitQueryCount();
                var freshCommits = await _adapter.GetLogAsync(queryCount, CancellationToken.None);

                if (freshCommits == null || freshCommits.Count == 0)
                    return;

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
