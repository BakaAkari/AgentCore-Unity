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
        private TreeView _statusTreeView;
        private VisualElement _commitList;
        private Label _commitHistorySummaryLabel;
        private Button _loadOlderCommitsButton;
        private Button _collapseCommitsButton;
        private Button _refreshButton;
        private Button _viewDiffButton;
        private VisualElement _messageContainer;
        private VisualElement _syncStatusBanner;
        private Label _syncStatusLabel;
        private Button _checkRemoteButton;
        private Button _updateRemoteButton;

        // Operations 区域已移除 - 所有操作通过右键菜单调用外部工具

        // TreeView 数据
        private List<VcsTreeNode> _treeRoots = new List<VcsTreeNode>();
        private Dictionary<int, VcsTreeNode> _nodeById = new Dictionary<int, VcsTreeNode>();
        private HashSet<int> _selectedNodeIds = new HashSet<int>();
        private List<VcsFileStatus> _currentFiles = new List<VcsFileStatus>();
        
        private List<VcsCommit> _loadedCommits = new List<VcsCommit>();
        private int _visibleCommitCount = InitialCommitDisplayCount;
        private bool _isLoadingMoreCommits;

        private IVcsAdapter _adapter;
        private VcsType _currentVcsType = VcsType.None;
        private CancellationTokenSource _cts;

        private enum IgnoreTarget
        {
            File,
            Folder,
            Extension
        }

        public VersionControlPanel()
        {
            AddToClassList(UssClassName);
            VcsRemoteStatusMonitor.StatusChanged += UpdateSyncStatusBanner;
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
            // 标题栏
            var header = new VisualElement();
            header.AddToClassList("panel-header");

            var title = new Label("Version Control");
            title.AddToClassList("panel-title");
            header.Add(title);

            _refreshButton = new Button(OnRefreshClicked) { text = "Refresh" };
            _refreshButton.AddToClassList(ButtonClassName);
            header.Add(_refreshButton);

            Add(header);

            // 消息容器
            _messageContainer = new VisualElement();
            _messageContainer.AddToClassList("message-container");
            _messageContainer.style.display = DisplayStyle.None;
            Add(_messageContainer);

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

            Add(infoSection);

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

            // 创建 TreeView
            _statusTreeView = new TreeView();
            _statusTreeView.AddToClassList("vcs-tree-view");
            _statusTreeView.AddToClassList("vcs-status-tree-view");
            _statusTreeView.style.flexGrow = 1;
            _statusTreeView.style.minHeight = 200;
            
            // 设置 TreeView 的 makeItem 和 bindItem 回调
            _statusTreeView.makeItem = MakeTreeItem;
            _statusTreeView.bindItem = BindTreeItem;
            _statusTreeView.unbindItem = UnbindTreeItem;
            
            // 设置选择模式为多选
            _statusTreeView.selectionType = SelectionType.Multiple;
            
            // 注册选择变化事件
            _statusTreeView.selectedIndicesChanged += OnTreeSelectionChanged;
            
            statusSection.Add(_statusTreeView);

            Add(statusSection);

            // 提交历史区域
            var historySection = CreateSection("Recent Commits");

            _commitHistorySummaryLabel = new Label("No commit history loaded");
            _commitHistorySummaryLabel.AddToClassList("vcs-commit-summary-label");
            historySection.Add(_commitHistorySummaryLabel);

            var commitScrollView = new ScrollView(ScrollViewMode.Vertical);
            commitScrollView.AddToClassList("vcs-list");
            commitScrollView.AddToClassList("vcs-commit-scroll-view");
            _commitList = commitScrollView.contentContainer;
            historySection.Add(commitScrollView);

            Add(historySection);
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
                return;
            }

            var rootPath = VcsDetector.GetVcsRootPath();
            _adapter = CreateAdapter(_currentVcsType, rootPath);

            if (_adapter == null || !_adapter.IsAvailable())
            {
                _vcsTypeLabel.text = $"{_currentVcsType} (command not available)";
                ShowMessage($"{_currentVcsType} detected but command-line tool is not available. Please install {_currentVcsType} CLI.", true);
                _refreshButton.SetEnabled(false);
                return;
            }

            _vcsTypeLabel.text = $"Type: {_currentVcsType}";

            RefreshAllData();
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

        private async Task RefreshAllData()
        {
            if (_adapter == null)
                return;

            _refreshButton.SetEnabled(false);
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
            }
        }

        private void UpdateStatusList(List<VcsFileStatus> files)
        {
            _currentFiles = files ?? new List<VcsFileStatus>();
            _selectedNodeIds.Clear();
            _nodeById.Clear();

            if (files == null || files.Count == 0)
            {
                _statusSummaryLabel.text = "No changes in working copy";
                _treeRoots = new List<VcsTreeNode>();
                _statusTreeView.SetRootItems(new List<TreeViewItemData<VcsTreeNode>>());
                _statusTreeView.Rebuild();
                return;
            }

            // 统计各状态数量
            var summary = files.GroupBy(f => f.State)
                .Select(g => $"{g.Count()} {g.Key}")
                .ToList();
            _statusSummaryLabel.text = string.Join(", ", summary);

            // 构建树结构
            _treeRoots = VcsTreeBuilder.BuildTree(files);

            // 构建 ID 映射
            BuildNodeIdMap(_treeRoots);

            // 转换为 TreeViewItemData
            var treeItems = ConvertToTreeViewItems(_treeRoots);

            // 设置 TreeView 数据
            _statusTreeView.SetRootItems(treeItems);
            _statusTreeView.Rebuild();
        }

        /// <summary>
        /// 构建节点 ID 映射表
        /// </summary>
        private void BuildNodeIdMap(List<VcsTreeNode> roots)
        {
            foreach (var root in roots)
            {
                BuildNodeIdMapRecursive(root);
            }
        }

        private void BuildNodeIdMapRecursive(VcsTreeNode node)
        {
            _nodeById[node.Id] = node;
            foreach (var child in node.Children)
            {
                BuildNodeIdMapRecursive(child);
            }
        }

        /// <summary>
        /// 将 VcsTreeNode 转换为 TreeViewItemData
        /// </summary>
        private List<TreeViewItemData<VcsTreeNode>> ConvertToTreeViewItems(List<VcsTreeNode> nodes)
        {
            var items = new List<TreeViewItemData<VcsTreeNode>>();
            foreach (var node in nodes)
            {
                items.Add(ConvertNodeToTreeViewItem(node));
            }
            return items;
        }

        private TreeViewItemData<VcsTreeNode> ConvertNodeToTreeViewItem(VcsTreeNode node)
        {
            if (node.Children.Count == 0)
            {
                // 叶子节点
                return new TreeViewItemData<VcsTreeNode>(node.Id, node);
            }
            else
            {
                // 有子节点的节点
                var children = ConvertToTreeViewItems(node.Children);
                return new TreeViewItemData<VcsTreeNode>(node.Id, node, children);
            }
        }

        #region TreeView Item Rendering

        /// <summary>
        /// 创建 TreeView 项的 UI 模板
        /// </summary>
        private VisualElement MakeTreeItem()
        {
            var container = new VisualElement();
            container.AddToClassList("vcs-tree-item");
            container.style.flexDirection = FlexDirection.Row;
            container.style.alignItems = Align.Center;
            container.style.paddingLeft = 4;
            container.style.paddingTop = 2;
            container.style.paddingBottom = 2;
            container.style.minHeight = 20;

            // 图标
            var icon = new Label();
            icon.name = "icon";
            icon.style.marginRight = 4;
            icon.style.minWidth = 16;
            container.Add(icon);

            // 名称
            var nameLabel = new Label();
            nameLabel.name = "name";
            nameLabel.style.flexGrow = 1;
            nameLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
            container.Add(nameLabel);

            // 状态徽章
            var badge = new Label();
            badge.name = "badge";
            badge.AddToClassList(StatusBadgeClassName);
            badge.style.marginLeft = 4;
            container.Add(badge);

            // TreeView 使用虚拟化 item，直接监听右键并使用 GenericMenu 弹出菜单，避免 TreeView 内部吞掉 ContextualMenuPopulateEvent。
            container.RegisterCallback<MouseDownEvent>(OnTreeItemMouseDown, TrickleDown.TrickleDown);

            return container;
        }

        /// <summary>
        /// 绑定数据到 TreeView 项
        /// </summary>
        private void BindTreeItem(VisualElement element, int index)
        {
            var itemData = _statusTreeView.GetItemDataForIndex<VcsTreeNode>(index);
            if (itemData == null)
                return;

            var node = itemData;
            var icon = element.Q<Label>("icon");
            var nameLabel = element.Q<Label>("name");
            var badge = element.Q<Label>("badge");

            if (node.IsDirectory)
            {
                // 目录节点 - Unity TreeView 自带展开/折叠箭头，不需要额外图标
                icon.text = "";
                nameLabel.text = node.Name;
                badge.text = $"{node.ChangeCount}";
                badge.style.display = DisplayStyle.Flex;
                badge.style.backgroundColor = new Color(0.3f, 0.5f, 0.7f, 0.8f);
            }
            else
            {
                // 文件节点 - 不需要图标，TreeView 会自动处理缩进
                icon.text = "";
                nameLabel.text = node.Name;
                
                if (node.FileStatus != null)
                {
                    badge.text = GetStateBadge(node.FileStatus.State);
                    badge.style.display = DisplayStyle.Flex;
                    badge.style.backgroundColor = GetStateColor(node.FileStatus.State);
                }
                else
                {
                    badge.style.display = DisplayStyle.None;
                }
            }

            // 存储节点 ID 到元素的 userData，供右键菜单使用
            element.userData = node.Id;
        }

        /// <summary>
        /// 解绑 TreeView 项
        /// </summary>
        private void UnbindTreeItem(VisualElement element, int index)
        {
            // 清理 userData
            element.userData = null;
        }

        /// <summary>
        /// 处理 TreeView item 右键点击，直接使用 UnityEditor.GenericMenu 弹出菜单，避免 TreeView 内部事件吞掉 ContextualMenuPopulateEvent。
        /// </summary>
        private void OnTreeItemMouseDown(MouseDownEvent evt)
        {
            if (evt.button != 1)
                return;

            var target = evt.currentTarget as VisualElement;
            if (target == null || !(target.userData is int nodeId))
                return;

            if (!_nodeById.TryGetValue(nodeId, out var clickedNode))
                return;

            ShowTreeItemGenericMenu(clickedNode);
            evt.StopImmediatePropagation();
            evt.PreventDefault();
        }

        /// <summary>
        /// 使用 GenericMenu 构建并显示 TreeView item 右键菜单。
        /// </summary>
        private void ShowTreeItemGenericMenu(VcsTreeNode clickedNode)
        {
            var selectedNodes = _selectedNodeIds
                .Where(id => _nodeById.ContainsKey(id))
                .Select(id => _nodeById[id])
                .ToList();

            if (!_selectedNodeIds.Contains(clickedNode.Id))
            {
                selectedNodes = new List<VcsTreeNode> { clickedNode };
            }

            var menu = new GenericMenu();
            var hasFiles = selectedNodes.Any(n => !n.IsDirectory);
            var hasDirs = selectedNodes.Any(n => n.IsDirectory);
            var isSingleFile = selectedNodes.Count == 1 && !selectedNodes[0].IsDirectory;
            var isSingleDir = selectedNodes.Count == 1 && selectedNodes[0].IsDirectory;

            if (isSingleFile)
            {
                PopulateFileGenericMenu(menu, selectedNodes[0].FileStatus);
            }
            else if (isSingleDir)
            {
                PopulateDirectoryGenericMenu(menu, selectedNodes[0]);
            }
            else if (hasFiles && !hasDirs)
            {
                var filePaths = selectedNodes.Select(n => n.FullPath).ToList();
                menu.AddItem(new GUIContent($"Selection/Revert Selected Files ({filePaths.Count})"), false, () => OnRevertMultipleFilesClicked(filePaths));
                menu.AddItem(new GUIContent("Selection/Clear Selection"), false, () => _statusTreeView.ClearSelection());
            }
            else if (hasDirs && !hasFiles)
            {
                menu.AddItem(new GUIContent($"Selection/Clear Selection ({selectedNodes.Count} directories)"), false, () => _statusTreeView.ClearSelection());
            }
            else
            {
                var fileCount = selectedNodes.Count(n => !n.IsDirectory);
                var dirCount = selectedNodes.Count(n => n.IsDirectory);
                menu.AddItem(new GUIContent($"Selection/Clear Selection ({fileCount} files, {dirCount} directories)"), false, () => _statusTreeView.ClearSelection());
            }

            menu.ShowAsContext();
        }

        /// <summary>
        /// 填充单文件 GenericMenu 菜单项。
        /// </summary>
        private void PopulateFileGenericMenu(GenericMenu menu, VcsFileStatus file)
        {
            if (file == null)
                return;

            var path = file.FilePath;
            var canStage = file.State == VcsFileState.Untracked
                || file.State == VcsFileState.Modified
                || file.State == VcsFileState.Missing
                || file.State == VcsFileState.Deleted;
            var canDiff = file.State != VcsFileState.Untracked && file.State != VcsFileState.Ignored;
            var canRevert = file.State != VcsFileState.Untracked && file.State != VcsFileState.Ignored;
            var canIgnore = file.State == VcsFileState.Untracked || file.State == VcsFileState.Ignored;
            var canResolve = file.State == VcsFileState.Conflicted;
            var supportsExternalTools = SupportsExternalFileTool("any");

            AddMenuItem(menu, "View/Show Differences", canDiff && supportsExternalTools, () => OnViewDiffForFileClicked(path));
            AddMenuItem(menu, "View/Show File Log", supportsExternalTools, () => OnShowFileLogClicked(path));
            AddMenuItem(menu, "View/Blame / Annotate", file.State != VcsFileState.Untracked && supportsExternalTools, () => OnShowBlameClicked(path));
            menu.AddItem(new GUIContent("View/Show File Info"), false, () => OnShowFileInfoClicked(file));
            menu.AddSeparator("View/");

            AddMenuItem(menu, "Version Control/Add or Track File", canStage && supportsExternalTools, () => OnStageSingleFileClicked(path));
            AddMenuItem(menu, "Version Control/Revert File", canRevert && supportsExternalTools, () => OnRevertSingleFileClicked(path));
            AddMenuItem(menu, "Version Control/Mark Resolved", canResolve && supportsExternalTools, () => OnMarkResolvedClicked(path));
            menu.AddSeparator("Version Control/");

            AddMenuItem(menu, "Commit/Commit This File", supportsExternalTools, () => OnCommitSingleFileClicked(path));
            menu.AddSeparator("Commit/");

            AddMenuItem(menu, "Ignore/Ignore This File", canIgnore, () => OnIgnoreFileClicked(path));
            AddMenuItem(menu, "Ignore/Ignore This Folder", canIgnore, () => OnIgnoreFolderClicked(path));
            AddMenuItem(menu, "Ignore/Ignore Same Extension", canIgnore && !string.IsNullOrWhiteSpace(Path.GetExtension(path)), () => OnIgnoreExtensionClicked(path));
            menu.AddSeparator("Ignore/");

            menu.AddItem(new GUIContent("File/Copy Relative Path"), false, () => CopyRelativePath(path));
            menu.AddItem(new GUIContent("File/Reveal In Explorer"), false, () => RevealInExplorer(path));
            AddMenuItem(menu, "File/Ping In Unity Project", IsUnityAssetPath(path), () => PingInUnityProject(path));
            AddMenuItem(menu, "File/Delete From Working Copy", file.State == VcsFileState.Untracked || file.State == VcsFileState.Added, () => OnDeleteFromWorkingCopyClicked(path));
        }

        /// <summary>
        /// 填充单目录 GenericMenu 菜单项。
        /// </summary>
        private void PopulateDirectoryGenericMenu(GenericMenu menu, VcsTreeNode dirNode)
        {
            var dirPath = dirNode.FullPath;
            var supportsExternalTools = SupportsExternalFileTool("any");

            AddMenuItem(menu, "Directory/Commit This Directory", supportsExternalTools, () => OnCommitDirectoryClicked(dirPath));
            AddMenuItem(menu, "Directory/Update This Directory", supportsExternalTools, () => OnUpdateDirectoryClicked(dirPath));
            AddMenuItem(menu, "Directory/Show Directory Log", supportsExternalTools, () => OnShowDirectoryLogClicked(dirPath));
            AddMenuItem(menu, "Directory/Show Directory Diff", supportsExternalTools, () => OnShowDirectoryDiffClicked(dirPath));
            AddMenuItem(menu, "Directory/Revert This Directory", supportsExternalTools, () => OnRevertDirectoryClicked(dirPath));
            menu.AddSeparator("Directory/");

            menu.AddItem(new GUIContent("File/Copy Relative Path"), false, () => CopyRelativePath(dirPath));
            menu.AddItem(new GUIContent("File/Reveal In Explorer"), false, () => RevealInExplorer(dirPath));
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
        /// TreeView 选择变化事件处理
        /// </summary>
        private void OnTreeSelectionChanged(IEnumerable<int> selectedIndices)
        {
            _selectedNodeIds.Clear();
            foreach (var index in selectedIndices)
            {
                var itemData = _statusTreeView.GetItemDataForIndex<VcsTreeNode>(index);
                if (itemData != null)
                {
                    _selectedNodeIds.Add(itemData.Id);
                }
            }
        }
        /// <summary>
        /// 获取当前 TreeView 中选中的文件路径列表（不包括目录）
        /// </summary>
        private List<string> GetSelectedFilePaths()
        {
            var selectedNodes = _selectedNodeIds
                .Where(id => _nodeById.ContainsKey(id))
                .Select(id => _nodeById[id])
                .Where(node => !node.IsDirectory)
                .ToList();
            
            return selectedNodes.Select(n => n.FullPath).ToList();
        }

        /// <summary>
        /// 检查指定文件是否在 TreeView 选中项中
        /// </summary>
        private bool IsFileSelected(string filePath)
        {
            return GetSelectedFilePaths().Contains(filePath);
        }

        /// <summary>
        /// 获取状态对应的颜色
        /// </summary>
        private Color GetStateColor(VcsFileState state)
        {
            switch (state)
            {
                case VcsFileState.Modified:
                    return new Color(0.8f, 0.6f, 0.2f, 0.8f); // 橙色
                case VcsFileState.Added:
                    return new Color(0.2f, 0.8f, 0.2f, 0.8f); // 绿色
                case VcsFileState.Deleted:
                case VcsFileState.Missing:
                    return new Color(0.8f, 0.2f, 0.2f, 0.8f); // 红色
                case VcsFileState.Conflicted:
                    return new Color(0.9f, 0.1f, 0.1f, 0.9f); // 深红色
                case VcsFileState.Untracked:
                    return new Color(0.5f, 0.5f, 0.5f, 0.8f); // 灰色
                case VcsFileState.Ignored:
                    return new Color(0.4f, 0.4f, 0.4f, 0.6f); // 深灰色
                default:
                    return new Color(0.3f, 0.3f, 0.3f, 0.8f);
            }
        }

        #endregion

        private void OnViewDiffForFileClicked(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;

            // Selection handled by TreeView
            
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

            // Selection handled by TreeView
            
            // 尝试使用外部工具 Revert
            if (TryOpenExternalRevert(filePath))
                return;
            
            // 外部工具不可用时显示提示
            ShowExternalToolUnavailable("Revert");
        }

        private void OnStageSingleFileClicked(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;

            // Selection handled by TreeView
            
            // 尝试使用外部工具添加文件
            if (TryOpenExternalAdd(filePath))
                return;
            
            // 外部工具不可用时显示提示
            ShowExternalToolUnavailable("Add");
        }

        private void OnUnstageSingleFileClicked(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;

            // Selection handled by TreeView
            
            // 对于 SVN，Unstage 实际上是删除已添加的文件（保留本地副本）
            // 使用 TortoiseSVN 的 remove 命令
            if (TryOpenExternalRemove(filePath))
                return;
            
            // 外部工具不可用时显示提示
            ShowExternalToolUnavailable("Unstage");
        }


        private void OnCommitSingleFileClicked(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;

            // Selection handled by TreeView
            
            // 尝试使用外部工具提交单个文件
            if (TryOpenExternalCommitFile(filePath))
                return;
            
            // 外部工具不可用时显示提示
            ShowExternalToolUnavailable("Commit File");
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

        private void OnShowBlameClicked(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;

            // 尝试使用外部工具打开 Blame
            if (TryOpenExternalBlame(filePath))
                return;
            
            // 外部工具不可用时显示提示
            ShowExternalToolUnavailable("Blame");
        }

        private void OnShowFileInfoClicked(VcsFileStatus file)
        {
            var details = new List<string>
            {
                $"Path: {file.FilePath}",
                $"State: {file.State}",
                $"Description: {file.StateDescription}",
                $"Selected: {IsFileSelected(file.FilePath)}",
                $"VCS: {_currentVcsType}",
                $"Root: {VcsDetector.GetVcsRootPath()}"
            };

            Debug.Log($"[Version Control][{_currentVcsType}][File Info]\n{string.Join("\n", details)}");
            ShowMessage($"File info for '{file.FilePath}' logged to Console.", false);
        }

        private void OnMarkResolvedClicked(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;

            // 尝试使用外部工具 Resolve
            if (TryOpenExternalResolve(filePath))
                return;
            
            // 外部工具不可用时显示提示
            ShowExternalToolUnavailable("Resolve");
        }

        private async void OnIgnoreFileClicked(string filePath)
        {
            await IgnorePathAsync(filePath, IgnoreTarget.File);
        }

        private async void OnIgnoreFolderClicked(string filePath)
        {
            await IgnorePathAsync(filePath, IgnoreTarget.Folder);
        }

        private async void OnIgnoreExtensionClicked(string filePath)
        {
            await IgnorePathAsync(filePath, IgnoreTarget.Extension);
        }

        private async void OnDeleteFromWorkingCopyClicked(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;

            var absolutePath = GetAbsolutePath(filePath);
            var confirmed = EditorUtility.DisplayDialog("Delete From Working Copy", $"Delete this path from disk?\n\n{filePath}\n\nVersioned files should be reverted or scheduled delete through VCS actions.", "Delete", "Cancel");
            if (!confirmed)
                return;

            try
            {
                if (Directory.Exists(absolutePath))
                    Directory.Delete(absolutePath, true);
                else if (File.Exists(absolutePath))
                    File.Delete(absolutePath);
                else
                    ShowMessage($"Path does not exist: {filePath}", true);

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
                    Debug.Log($"[Version Control][{_currentVcsType}][{operation}] {filePath}\nCommand: {command} {arguments}\n{output}");
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

        private async Task IgnorePathAsync(string filePath, IgnoreTarget target)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;

            var pattern = BuildIgnorePattern(filePath, target);
            if (string.IsNullOrWhiteSpace(pattern))
            {
                ShowMessage($"Cannot build ignore pattern for '{filePath}'.", true);
                return;
            }

            var confirmed = EditorUtility.DisplayDialog("Ignore Path", $"Add this ignore rule?\n\n{pattern}\n\nSource: {filePath}", "Ignore", "Cancel");
            if (!confirmed)
                return;

            try
            {
                if (_currentVcsType == VcsType.Svn)
                    await IgnoreWithSvnAsync(filePath, pattern, target);
                else if (_currentVcsType == VcsType.Git)
                    IgnoreWithGit(pattern);
                else
                    ShowMessage("Ignore rules are currently supported for SVN and Git only.", true);
            }
            catch (Exception ex)
            {
                ShowMessage($"Ignore failed: {ex.Message}", true);
            }
        }

        private string BuildIgnorePattern(string filePath, IgnoreTarget target)
        {
            var normalized = filePath.Replace('\\', '/').Trim('/');
            switch (target)
            {
                case IgnoreTarget.File:
                    return _currentVcsType == VcsType.Git ? normalized : Path.GetFileName(normalized);
                case IgnoreTarget.Folder:
                    var folderPath = GetIgnoreFolderPath(normalized);
                    return _currentVcsType == VcsType.Git ? folderPath : Path.GetFileName(folderPath);
                case IgnoreTarget.Extension:
                    var extension = Path.GetExtension(normalized);
                    return string.IsNullOrWhiteSpace(extension) ? null : $"*{extension}";
                default:
                    return null;
            }
        }

        private string GetIgnoreFolderPath(string filePath)
        {
            var normalized = filePath.Replace('\\', '/').Trim('/');
            if (Directory.Exists(GetAbsolutePath(normalized)))
                return normalized;

            var parent = Path.GetDirectoryName(normalized)?.Replace('\\', '/');
            return string.IsNullOrWhiteSpace(parent) ? normalized : parent;
        }

        private async Task IgnoreWithSvnAsync(string filePath, string pattern, IgnoreTarget target)
        {
            var propertyTarget = GetSvnIgnorePropertyTarget(filePath, target);
            var rootPath = VcsDetector.GetVcsRootPath();
            var existingResult = await ExecuteSvnWithCleanupRetryAsync("Read SVN Ignore", $"propget svn:ignore \"{propertyTarget}\"", rootPath);
            var existingPatterns = (existingResult.Output ?? string.Empty)
                .Replace("\r\n", "\n")
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();

            if (!existingPatterns.Contains(pattern))
                existingPatterns.Add(pattern);

            var tempFile = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tempFile, string.Join(Environment.NewLine, existingPatterns) + Environment.NewLine);
                var result = await ExecuteSvnWithCleanupRetryAsync("Set SVN Ignore", $"propset svn:ignore -F \"{tempFile}\" \"{propertyTarget}\"", rootPath);
                if (!result.Success)
                {
                    ShowMessage($"SVN ignore failed: {BuildSvnFailureMessage(result)}", true);
                    return;
                }

                ShowMessage($"Added SVN ignore rule '{pattern}' on '{propertyTarget}'.", false);
                await RefreshAllData();
            }
            finally
            {
                try { File.Delete(tempFile); } catch { /* ignore cleanup */ }
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

        private void IgnoreWithGit(string pattern)
        {
            var gitignorePath = Path.Combine(VcsDetector.GetVcsRootPath(), ".gitignore");
            var lines = File.Exists(gitignorePath)
                ? File.ReadAllLines(gitignorePath).ToList()
                : new List<string>();

            if (!lines.Contains(pattern))
                lines.Add(pattern);

            File.WriteAllLines(gitignorePath, lines);
            AssetDatabase.Refresh();
            ShowMessage($"Added Git ignore rule '{pattern}' to .gitignore.", false);
        }

        private string GetSvnIgnorePropertyTarget(string filePath, IgnoreTarget target)
        {
            var normalized = filePath.Replace('\\', '/').Trim('/');
            var parent = target == IgnoreTarget.Folder
                ? Path.GetDirectoryName(GetIgnoreFolderPath(normalized))
                : Path.GetDirectoryName(normalized);

            return string.IsNullOrWhiteSpace(parent) ? "." : parent.Replace('\\', '/');
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

        private bool TryOpenExternalBlame(string filePath)
        {
            var rootPath = VcsDetector.GetVcsRootPath();
            var absolutePath = GetAbsolutePath(filePath);
            
            switch (_currentVcsType)
            {
                case VcsType.Svn:
                    return TryStartExternalProcess("TortoiseProc.exe", $"/command:blame /path:\"{absolutePath}\"", rootPath, "TortoiseSVN blame window");
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

        private bool TryOpenExternalResolve(string filePath)
        {
            var rootPath = VcsDetector.GetVcsRootPath();
            var absolutePath = GetAbsolutePath(filePath);
            
            switch (_currentVcsType)
            {
                case VcsType.Svn:
                    return TryStartExternalProcess("TortoiseProc.exe", $"/command:resolve /path:\"{absolutePath}\"", rootPath, "TortoiseSVN resolve dialog");
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
                    Debug.Log($"[Version Control] Diff Output{(string.IsNullOrEmpty(filePath) ? string.Empty : $" ({filePath})")}:\n{diff}");
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
            Debug.Log($"[Version Control][{_currentVcsType}][{operation}] {message}");
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
                Debug.Log(logText);
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

        #region Directory Support

        /// <summary>
        /// 从文件列表中提取所有有修改的目录及其文件数量
        /// </summary>

        /// <summary>
        /// 对目录执行 Commit 操作
        /// </summary>
        private void OnCommitDirectoryClicked(string directoryPath)
        {
            if (!TryOpenExternalDirectoryOperation("commit", directoryPath))
                ShowExternalToolUnavailable("Commit Folder");
        }

        /// <summary>
        /// 对目录执行 Update 操作
        /// </summary>
        private void OnUpdateDirectoryClicked(string directoryPath)
        {
            if (!TryOpenExternalDirectoryOperation("update", directoryPath))
                ShowExternalToolUnavailable("Update Folder");
        }

        /// <summary>
        /// 显示目录的 Log
        /// </summary>
        private void OnShowDirectoryLogClicked(string directoryPath)
        {
            if (!TryOpenExternalDirectoryOperation("log", directoryPath))
                ShowExternalToolUnavailable("Folder Log");
        }

        /// <summary>
        /// 显示目录的 Diff
        /// </summary>
        private void OnShowDirectoryDiffClicked(string directoryPath)
        {
            if (!TryOpenExternalDirectoryOperation("diff", directoryPath))
                ShowExternalToolUnavailable("Folder Diff");
        }

        /// <summary>
        /// 对目录执行 Revert 操作
        /// </summary>
        private void OnRevertDirectoryClicked(string directoryPath)
        {
            if (!TryOpenExternalDirectoryOperation("revert", directoryPath))
                ShowExternalToolUnavailable("Revert Folder");
        }

        /// <summary>
        /// 尝试对目录执行外部工具操作
        /// </summary>
        private bool TryOpenExternalDirectoryOperation(string command, string directoryPath)
        {
            var rootPath = VcsDetector.GetVcsRootPath();
            var absolutePath = Path.GetFullPath(directoryPath);

            switch (_currentVcsType)
            {
                case VcsType.Svn:
                    return TryStartExternalProcess("TortoiseProc.exe",
                        $"/command:{command} /path:\"{absolutePath}\"",
                        rootPath, $"TortoiseSVN {command} window");
                case VcsType.Git:
                case VcsType.Perforce:
                    return false; // 暂不支持
                default:
                    return false;
            }
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
            _cts?.Cancel();
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            VcsRemoteStatusMonitor.StatusChanged -= UpdateSyncStatusBanner;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        #endregion
    }
}
