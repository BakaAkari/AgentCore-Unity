using System;
using System.Collections.Generic;
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

        private Label _vcsTypeLabel;
        private Label _branchLabel;
        private Label _revisionLabel;
        private Label _statusSummaryLabel;
        private VisualElement _statusList;
        private VisualElement _commitList;
        private Button _refreshButton;
        private Button _viewDiffButton;
        private VisualElement _messageContainer;

        // 操作按钮
        private VisualElement _operationsSection;
        private Button _stageAllButton;
        private Button _unstageAllButton;
        private Button _commitButton;
        private Button _syncButton;
        private Button _revertButton;
        private TextField _commitMessageField;

        // Git 特有按钮
        private VisualElement _gitOperationsSection;
        private Button _createBranchButton;
        private Button _switchBranchButton;
        private Button _stashButton;
        private Button _stashPopButton;
        private TextField _branchNameField;

        // 选中的文件
        private HashSet<string> _selectedFiles = new HashSet<string>();
        private List<VcsFileStatus> _currentFiles = new List<VcsFileStatus>();
        private List<string> _displayedFilePaths = new List<string>();
        private Dictionary<string, VisualElement> _statusItemByPath = new Dictionary<string, VisualElement>();
        private Dictionary<string, Toggle> _statusToggleByPath = new Dictionary<string, Toggle>();
        private int _lastSelectedFileIndex = -1;

        private IVcsAdapter _adapter;
        private VcsType _currentVcsType = VcsType.None;
        private CancellationTokenSource _cts;

        public VersionControlPanel()
        {
            AddToClassList(UssClassName);
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

            _refreshButton = new Button(OnRefreshClicked) { text = "⟳ Refresh" };
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

            _vcsTypeLabel = new Label("Detecting...");
            _vcsTypeLabel.AddToClassList("vcs-info-label");
            infoSection.Add(_vcsTypeLabel);

            _branchLabel = new Label("Branch: -");
            _branchLabel.AddToClassList("vcs-info-label");
            infoSection.Add(_branchLabel);

            _revisionLabel = new Label("Revision: -");
            _revisionLabel.AddToClassList("vcs-info-label");
            infoSection.Add(_revisionLabel);

            Add(infoSection);

            // 操作区域
            _operationsSection = CreateSection("Operations");
            BuildOperationsUI(_operationsSection);
            _operationsSection.style.display = DisplayStyle.None;
            Add(_operationsSection);

            // Git 特有操作区域
            _gitOperationsSection = CreateSection("Git Operations");
            BuildGitOperationsUI(_gitOperationsSection);
            _gitOperationsSection.style.display = DisplayStyle.None;
            Add(_gitOperationsSection);

            // 工作区状态区域
            var statusSection = CreateSection("Working Copy Status");

            _statusSummaryLabel = new Label("No changes");
            _statusSummaryLabel.AddToClassList("vcs-summary-label");
            statusSection.Add(_statusSummaryLabel);

            _viewDiffButton = new Button(OnViewDiffClicked) { text = "View Diff" };
            _viewDiffButton.AddToClassList(ButtonClassName);
            _viewDiffButton.SetEnabled(false);
            statusSection.Add(_viewDiffButton);

            var statusScrollView = new ScrollView(ScrollViewMode.Vertical);
            statusScrollView.AddToClassList("vcs-list");
            statusScrollView.AddToClassList("vcs-status-scroll-view");
            _statusList = statusScrollView.contentContainer;
            statusSection.Add(statusScrollView);

            Add(statusSection);

            // 提交历史区域
            var historySection = CreateSection("Recent Commits");

            _commitList = new VisualElement();
            _commitList.AddToClassList("vcs-list");
            historySection.Add(_commitList);

            Add(historySection);
        }

        /// <summary>
        /// 构建通用操作 UI（Stage, Unstage, Commit, Sync, Revert）
        /// </summary>
        private void BuildOperationsUI(VisualElement parent)
        {
            // 提交消息输入
            var commitRow = new VisualElement();
            commitRow.AddToClassList("vcs-commit-row");

            _commitMessageField = new TextField("Commit Message");
            _commitMessageField.AddToClassList("vcs-commit-input");
            _commitMessageField.multiline = true;
            _commitMessageField.style.minHeight = 40;
            _commitMessageField.style.flexGrow = 1;
            commitRow.Add(_commitMessageField);

            parent.Add(commitRow);

            // 操作按钮行
            var buttonRow = new VisualElement();
            buttonRow.AddToClassList(ButtonRowClassName);

            _stageAllButton = new Button(OnStageAllClicked) { text = "Stage All" };
            _stageAllButton.AddToClassList(OperationButtonClassName);
            _stageAllButton.tooltip = "Stage all modified files for commit";
            buttonRow.Add(_stageAllButton);

            _unstageAllButton = new Button(OnUnstageAllClicked) { text = "Unstage All" };
            _unstageAllButton.AddToClassList(OperationButtonClassName);
            _unstageAllButton.tooltip = "Unstage all staged files";
            buttonRow.Add(_unstageAllButton);

            _commitButton = new Button(OnCommitClicked) { text = " Commit" };
            _commitButton.AddToClassList(OperationButtonClassName);
            _commitButton.AddToClassList("vcs-primary-button");
            _commitButton.tooltip = "Commit staged changes with the message above";
            buttonRow.Add(_commitButton);

            parent.Add(buttonRow);

            // 第二行按钮
            var buttonRow2 = new VisualElement();
            buttonRow2.AddToClassList(ButtonRowClassName);

            _syncButton = new Button(OnSyncClicked) { text = " Sync/Pull" };
            _syncButton.AddToClassList(OperationButtonClassName);
            _syncButton.tooltip = "Pull/Sync latest changes from remote";
            buttonRow2.Add(_syncButton);

            _revertButton = new Button(OnRevertAllClicked) { text = "⟲ Revert All" };
            _revertButton.AddToClassList(DangerButtonClassName);
            _revertButton.tooltip = "Revert all local changes (DESTRUCTIVE)";
            buttonRow2.Add(_revertButton);

            parent.Add(buttonRow2);
        }

        /// <summary>
        /// 构建 Git 特有操作 UI
        /// </summary>
        private void BuildGitOperationsUI(VisualElement parent)
        {
            // 分支名输入
            var branchRow = new VisualElement();
            branchRow.AddToClassList("vcs-branch-row");

            _branchNameField = new TextField("Branch Name");
            _branchNameField.AddToClassList("vcs-branch-input");
            _branchNameField.style.flexGrow = 1;
            branchRow.Add(_branchNameField);

            parent.Add(branchRow);

            // Git 操作按钮
            var buttonRow = new VisualElement();
            buttonRow.AddToClassList(ButtonRowClassName);

            _createBranchButton = new Button(OnCreateBranchClicked) { text = "⑂ Create Branch" };
            _createBranchButton.AddToClassList(OperationButtonClassName);
            _createBranchButton.tooltip = "Create a new branch with the name above";
            buttonRow.Add(_createBranchButton);

            _switchBranchButton = new Button(OnSwitchBranchClicked) { text = "⇄ Switch Branch" };
            _switchBranchButton.AddToClassList(OperationButtonClassName);
            _switchBranchButton.tooltip = "Switch to the branch named above";
            buttonRow.Add(_switchBranchButton);

            parent.Add(buttonRow);

            // Stash 按钮行
            var stashRow = new VisualElement();
            stashRow.AddToClassList(ButtonRowClassName);

            _stashButton = new Button(OnStashClicked) { text = " Stash" };
            _stashButton.AddToClassList(OperationButtonClassName);
            _stashButton.tooltip = "Stash current changes";
            stashRow.Add(_stashButton);

            _stashPopButton = new Button(OnStashPopClicked) { text = " Stash Pop" };
            _stashPopButton.AddToClassList(OperationButtonClassName);
            _stashPopButton.tooltip = "Pop the most recent stash";
            stashRow.Add(_stashPopButton);

            parent.Add(stashRow);
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

            // 显示操作区域
            _operationsSection.style.display = DisplayStyle.Flex;

            // Git 特有操作仅在 Git 模式下显示
            if (_currentVcsType == VcsType.Git)
            {
                _gitOperationsSection.style.display = DisplayStyle.Flex;
            }

            // 根据 VCS 类型调整按钮文本
            UpdateButtonLabelsForVcsType();

            RefreshAllData();
        }

        /// <summary>
        /// 根据 VCS 类型调整按钮标签
        /// </summary>
        private void UpdateButtonLabelsForVcsType()
        {
            switch (_currentVcsType)
            {
                case VcsType.Perforce:
                    _stageAllButton.text = "Edit/Add All";
                    _stageAllButton.tooltip = "Open all files for edit (p4 edit)";
                    _unstageAllButton.text = "Revert Unchanged";
                    _unstageAllButton.tooltip = "Revert files that haven't been modified";
                    _commitButton.text = " Submit";
                    _commitButton.tooltip = "Submit a changelist with the description above";
                    _syncButton.text = " Sync";
                    _syncButton.tooltip = "Sync workspace to latest (p4 sync)";
                    _revertButton.text = "⟲ Revert All";
                    _revertButton.tooltip = "Revert all opened files (p4 revert)";
                    break;

                case VcsType.Svn:
                    _stageAllButton.text = "Add Unversioned";
                    _stageAllButton.tooltip = "Add all unversioned files (svn add)";
                    _unstageAllButton.text = "Revert";
                    _unstageAllButton.tooltip = "Revert local modifications (svn revert)";
                    _commitButton.text = " Commit";
                    _commitButton.tooltip = "Commit changes with the message above (svn commit)";
                    _syncButton.text = " Update";
                    _syncButton.tooltip = "Update working copy (svn update)";
                    _revertButton.text = "⟲ Revert All";
                    _revertButton.tooltip = "Revert all local changes (svn revert)";
                    break;

                default: // Git
                    // 默认标签已在 BuildOperationsUI 中设置
                    break;
            }
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

                // 并行获取所有数据
                var branchTask = _adapter.GetBranchInfoAsync(ct);
                var statusTask = _adapter.GetStatusAsync(ct);
                var logTask = _adapter.GetLogAsync(VcsSettings.MaxCommitEntries, ct);

                await Task.WhenAll(branchTask, statusTask, logTask);

                if (ct.IsCancellationRequested)
                    return;

                // 更新分支信息
                var branchInfo = await branchTask;
                if (branchInfo.Success)
                {
                    _branchLabel.text = $"Branch: {branchInfo.CurrentBranch ?? "unknown"}";
                    _revisionLabel.text = $"Revision: {branchInfo.CurrentRevision ?? "unknown"}";
                }

                // 更新状态列表
                var statusResult = await statusTask;
                if (statusResult.Success)
                {
                    _currentFiles = statusResult.Files ?? new List<VcsFileStatus>();
                    UpdateStatusList(_currentFiles);
                    _viewDiffButton.SetEnabled(_currentFiles.Count > 0);
                    UpdateOperationButtonStates();
                }

                // 更新提交历史
                var commits = await logTask;
                UpdateCommitList(commits);
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

        /// <summary>
        /// 根据当前文件状态更新操作按钮的启用状态
        /// </summary>
        private void UpdateOperationButtonStates()
        {
            bool hasChanges = _currentFiles.Count > 0;
            _stageAllButton.SetEnabled(hasChanges);
            _revertButton.SetEnabled(hasChanges);

            // Commit 按钮需要有消息
            bool hasMessage = !string.IsNullOrWhiteSpace(_commitMessageField?.value);
            _commitButton.SetEnabled(hasChanges && hasMessage);
        }

        private void UpdateStatusList(List<VcsFileStatus> files)
        {
            _statusList.Clear();
            _selectedFiles.Clear();
            _displayedFilePaths.Clear();
            _statusItemByPath.Clear();
            _statusToggleByPath.Clear();
            _lastSelectedFileIndex = -1;

            if (files == null || files.Count == 0)
            {
                _statusSummaryLabel.text = "No changes in working copy";
                var noChanges = new Label("Working copy is clean");
                noChanges.style.color = new Color(0.6f, 0.6f, 0.6f);
                noChanges.style.paddingTop = 5;
                _statusList.Add(noChanges);
                return;
            }

            // 统计各状态数量
            var summary = files.GroupBy(f => f.State)
                .Select(g => $"{g.Count()} {g.Key}")
                .ToList();
            _statusSummaryLabel.text = string.Join(", ", summary);

            // 按状态分组显示
            var grouped = files.GroupBy(f => f.State).OrderBy(g => g.Key.ToString());

            foreach (var group in grouped)
            {
                var groupHeader = new Label($"{group.Key} ({group.Count()})");
                groupHeader.style.unityFontStyleAndWeight = FontStyle.Bold;
                groupHeader.style.marginTop = 5;
                groupHeader.style.marginBottom = 2;
                _statusList.Add(groupHeader);

                foreach (var file in group)
                {
                    _displayedFilePaths.Add(file.FilePath);
                    var item = CreateStatusItem(file);
                    _statusList.Add(item);
                }
            }
        }

        private VisualElement CreateStatusItem(VcsFileStatus file)
        {
            var item = new VisualElement();
            item.AddToClassList(StatusItemClassName);
            item.style.flexDirection = FlexDirection.Row;
            item.style.alignItems = Align.Center;
            item.style.paddingLeft = 10;
            item.style.paddingTop = 4;
            item.style.paddingBottom = 4;
            item.style.minHeight = 22;
            item.userData = file.FilePath;

            item.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.button == 1)
                {
                    if (!_selectedFiles.Contains(file.FilePath))
                    {
                        _selectedFiles.Clear();
                        _selectedFiles.Add(file.FilePath);
                        _lastSelectedFileIndex = GetDisplayedFileIndex(file.FilePath);
                        RefreshStatusSelectionVisuals();
                    }
                    return;
                }

                if (evt.button != 0)
                    return;

                HandleStatusItemClicked(file.FilePath, evt.shiftKey, evt.ctrlKey || evt.commandKey);
                evt.StopPropagation();
            });

            item.AddManipulator(new ContextualMenuManipulator(evt => BuildStatusItemContextMenu(evt, file)));

            // 复选框用于选择文件
            var toggle = new Toggle();
            toggle.style.marginRight = 6;
            toggle.style.flexShrink = 0;
            toggle.RegisterCallback<MouseDownEvent>(evt => evt.StopPropagation());
            toggle.RegisterValueChangedCallback(evt =>
            {
                SetFileSelected(file.FilePath, evt.newValue);
                _lastSelectedFileIndex = GetDisplayedFileIndex(file.FilePath);
                RefreshStatusSelectionVisuals();
            });
            item.Add(toggle);

            var badge = new Label(GetStateBadge(file.State));
            badge.AddToClassList(StatusBadgeClassName);
            badge.AddToClassList($"state-{file.State.ToString().ToLowerInvariant()}");
            badge.style.flexShrink = 0;
            item.Add(badge);

            var path = new Label(file.FilePath);
            path.style.flexGrow = 1;
            path.style.flexShrink = 1;
            path.style.unityTextAlign = TextAnchor.MiddleLeft;
            path.style.overflow = Overflow.Hidden;
            path.style.textOverflow = TextOverflow.Ellipsis;
            item.Add(path);

            _statusItemByPath[file.FilePath] = item;
            _statusToggleByPath[file.FilePath] = toggle;

            return item;
        }

        private void BuildStatusItemContextMenu(ContextualMenuPopulateEvent evt, VcsFileStatus file)
        {
            evt.menu.AppendAction($"Show Differences: {file.FilePath}", _ => OnViewDiffForFileClicked(file.FilePath));
            evt.menu.AppendSeparator();
            evt.menu.AppendAction($"Revert: {file.FilePath}", _ => OnRevertSingleFileClicked(file.FilePath), DropdownMenuAction.AlwaysEnabled);
        }

        private async void OnViewDiffForFileClicked(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;

            _selectedFiles.Clear();
            _selectedFiles.Add(filePath);
            _lastSelectedFileIndex = GetDisplayedFileIndex(filePath);
            RefreshStatusSelectionVisuals();
            await ShowDiffAsync(filePath);
        }

        private async void OnRevertSingleFileClicked(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;

            _selectedFiles.Clear();
            _selectedFiles.Add(filePath);
            _lastSelectedFileIndex = GetDisplayedFileIndex(filePath);
            RefreshStatusSelectionVisuals();
            await RevertFilesAsync(new List<string> { filePath }, $"Revert '{filePath}'?", $"This will discard local changes in:\n\n{filePath}\n\nThis action cannot be undone.", "Revert");
        }

        private void HandleStatusItemClicked(string filePath, bool shiftPressed, bool ctrlPressed)
        {
            int clickedIndex = GetDisplayedFileIndex(filePath);
            if (clickedIndex < 0)
                return;

            if (shiftPressed && _lastSelectedFileIndex >= 0)
            {
                if (!ctrlPressed)
                    _selectedFiles.Clear();

                int start = Mathf.Min(_lastSelectedFileIndex, clickedIndex);
                int end = Mathf.Max(_lastSelectedFileIndex, clickedIndex);
                for (int i = start; i <= end; i++)
                {
                    _selectedFiles.Add(_displayedFilePaths[i]);
                }
            }
            else if (ctrlPressed)
            {
                ToggleFileSelection(filePath);
                _lastSelectedFileIndex = clickedIndex;
            }
            else
            {
                bool isOnlySelected = _selectedFiles.Count == 1 && _selectedFiles.Contains(filePath);
                _selectedFiles.Clear();
                if (!isOnlySelected)
                    _selectedFiles.Add(filePath);

                _lastSelectedFileIndex = clickedIndex;
            }

            RefreshStatusSelectionVisuals();
        }

        private void ToggleFileSelection(string filePath)
        {
            if (!_selectedFiles.Remove(filePath))
                _selectedFiles.Add(filePath);
        }

        private void SetFileSelected(string filePath, bool selected)
        {
            if (selected)
                _selectedFiles.Add(filePath);
            else
                _selectedFiles.Remove(filePath);
        }

        private int GetDisplayedFileIndex(string filePath)
        {
            return _displayedFilePaths.IndexOf(filePath);
        }

        private void RefreshStatusSelectionVisuals()
        {
            foreach (var pair in _statusItemByPath)
            {
                bool selected = _selectedFiles.Contains(pair.Key);
                pair.Value.EnableInClassList("selected", selected);

                if (_statusToggleByPath.TryGetValue(pair.Key, out var toggle))
                    toggle.SetValueWithoutNotify(selected);
            }
        }

        private void UpdateCommitList(List<VcsCommit> commits)
        {
            _commitList.Clear();

            if (commits == null || commits.Count == 0)
            {
                var noCommits = new Label("No commit history available");
                noCommits.style.color = new Color(0.6f, 0.6f, 0.6f);
                noCommits.style.paddingTop = 5;
                _commitList.Add(noCommits);
                return;
            }

            foreach (var commit in commits.Take(10)) // 显示最近10条
            {
                var item = CreateCommitItem(commit);
                _commitList.Add(item);
            }
        }

        private VisualElement CreateCommitItem(VcsCommit commit)
        {
            var item = new VisualElement();
            item.AddToClassList(CommitItemClassName);
            item.style.marginBottom = 8;
            item.style.paddingLeft = 5;
            item.style.paddingRight = 5;
            item.style.paddingTop = 5;
            item.style.paddingBottom = 5;
            item.style.backgroundColor = new Color(0.2f, 0.2f, 0.2f, 0.3f);
            item.style.borderBottomLeftRadius = 3;
            item.style.borderBottomRightRadius = 3;
            item.style.borderTopLeftRadius = 3;
            item.style.borderTopRightRadius = 3;

            // 第一行：版本号和作者
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.justifyContent = Justify.SpaceBetween;

            var revision = new Label($"Rev: {commit.Revision}");
            revision.style.unityFontStyleAndWeight = FontStyle.Bold;
            revision.style.color = new Color(0.4f, 0.8f, 1.0f);
            header.Add(revision);

            var author = new Label(commit.Author);
            author.style.color = new Color(0.8f, 0.8f, 0.8f);
            header.Add(author);

            item.Add(header);

            // 第二行：日期
            var date = new Label(commit.Date);
            date.style.fontSize = 10;
            date.style.color = new Color(0.6f, 0.6f, 0.6f);
            date.style.marginTop = 2;
            item.Add(date);

            // 第三行：提交消息
            var message = new Label(commit.Message);
            message.style.whiteSpace = WhiteSpace.Normal;
            message.style.marginTop = 4;
            message.style.color = new Color(0.9f, 0.9f, 0.9f);
            item.Add(message);

            return item;
        }

        #region Operation Event Handlers

        private async void OnStageAllClicked()
        {
            if (_adapter == null) return;

            var filesToStage = _selectedFiles.Count > 0
                ? _selectedFiles.ToList()
                : _currentFiles.Select(f => f.FilePath).ToList();

            if (filesToStage.Count == 0)
            {
                ShowMessage("No files to stage.", false);
                return;
            }

            SetOperationButtonsEnabled(false);
            try
            {
                var result = await _adapter.StageFilesAsync(filesToStage, CancellationToken.None);
                if (result.Success)
                {
                    ShowMessage($"Staged {filesToStage.Count} file(s) successfully.", false);
                    await RefreshAllData();
                }
                else
                {
                    ShowMessage($"Stage failed: {result.Message}", true);
                }
            }
            catch (Exception ex)
            {
                ShowMessage($"Error staging files: {ex.Message}", true);
            }
            finally
            {
                SetOperationButtonsEnabled(true);
            }
        }

        private async void OnUnstageAllClicked()
        {
            if (_adapter == null) return;

            var filesToUnstage = _selectedFiles.Count > 0
                ? _selectedFiles.ToList()
                : _currentFiles.Select(f => f.FilePath).ToList();

            if (filesToUnstage.Count == 0)
            {
                ShowMessage("No files to unstage.", false);
                return;
            }

            SetOperationButtonsEnabled(false);
            try
            {
                var result = await _adapter.UnstageFilesAsync(filesToUnstage, CancellationToken.None);
                if (result.Success)
                {
                    ShowMessage($"Unstaged {filesToUnstage.Count} file(s) successfully.", false);
                    await RefreshAllData();
                }
                else
                {
                    ShowMessage($"Unstage failed: {result.Message}", true);
                }
            }
            catch (Exception ex)
            {
                ShowMessage($"Error unstaging files: {ex.Message}", true);
            }
            finally
            {
                SetOperationButtonsEnabled(true);
            }
        }

        private async void OnCommitClicked()
        {
            if (_adapter == null) return;

            var message = _commitMessageField?.value;
            if (string.IsNullOrWhiteSpace(message))
            {
                ShowMessage("Please enter a commit message.", true);
                return;
            }

            SetOperationButtonsEnabled(false);
            try
            {
                var result = await _adapter.CommitAsync(message, CancellationToken.None);
                if (result.Success)
                {
                    ShowMessage($"Committed successfully: {result.Message}", false);
                    _commitMessageField.value = "";
                    await RefreshAllData();
                }
                else
                {
                    ShowMessage($"Commit failed: {result.Message}", true);
                }
            }
            catch (Exception ex)
            {
                ShowMessage($"Error committing: {ex.Message}", true);
            }
            finally
            {
                SetOperationButtonsEnabled(true);
            }
        }

        private async void OnSyncClicked()
        {
            if (_adapter == null) return;

            SetOperationButtonsEnabled(false);
            try
            {
                var result = await _adapter.SyncAsync(CancellationToken.None);
                if (result.Success)
                {
                    ShowMessage($"Sync completed: {result.Message}", false);
                    await RefreshAllData();
                }
                else
                {
                    ShowMessage($"Sync failed: {result.Message}", true);
                }
            }
            catch (Exception ex)
            {
                ShowMessage($"Error syncing: {ex.Message}", true);
            }
            finally
            {
                SetOperationButtonsEnabled(true);
            }
        }

        private async void OnRevertAllClicked()
        {
            if (_adapter == null) return;

            var filesToRevert = _selectedFiles.Count > 0
                ? _selectedFiles.ToList()
                : _currentFiles.Select(f => f.FilePath).ToList();

            await RevertFilesAsync(
                filesToRevert,
                _selectedFiles.Count > 0 ? $"Revert {filesToRevert.Count} Selected File(s)?" : "Revert All Changes?",
                _selectedFiles.Count > 0
                    ? $"This will discard local changes in the selected {filesToRevert.Count} file(s).\n\nThis action cannot be undone."
                    : "This will discard ALL local changes in the working copy.\n\nThis action cannot be undone.",
                _selectedFiles.Count > 0 ? "Revert Selected" : "Revert All");
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

            SetOperationButtonsEnabled(false);
            try
            {
                var result = await _adapter.RevertFilesAsync(filesToRevert, CancellationToken.None);
                if (result.Success)
                {
                    ShowMessage($"Reverted {filesToRevert.Count} file(s) successfully.", false);
                    await RefreshAllData();
                }
                else
                {
                    ShowMessage($"Revert failed: {result.Message}", true);
                }
            }
            catch (Exception ex)
            {
                ShowMessage($"Error reverting: {ex.Message}", true);
            }
            finally
            {
                SetOperationButtonsEnabled(true);
            }
        }

        private async void OnCreateBranchClicked()
        {
            if (!(_adapter is GitAdapter gitAdapter)) return;

            var branchName = _branchNameField?.value;
            if (string.IsNullOrWhiteSpace(branchName))
            {
                ShowMessage("Please enter a branch name.", true);
                return;
            }

            SetOperationButtonsEnabled(false);
            try
            {
                var result = await gitAdapter.CreateBranchAsync(branchName, CancellationToken.None);
                if (result.Success)
                {
                    ShowMessage($"Branch '{branchName}' created successfully.", false);
                    _branchNameField.value = "";
                    await RefreshAllData();
                }
                else
                {
                    ShowMessage($"Create branch failed: {result.Message}", true);
                }
            }
            catch (Exception ex)
            {
                ShowMessage($"Error creating branch: {ex.Message}", true);
            }
            finally
            {
                SetOperationButtonsEnabled(true);
            }
        }

        private async void OnSwitchBranchClicked()
        {
            if (!(_adapter is GitAdapter gitAdapter)) return;

            var branchName = _branchNameField?.value;
            if (string.IsNullOrWhiteSpace(branchName))
            {
                ShowMessage("Please enter a branch name to switch to.", true);
                return;
            }

            SetOperationButtonsEnabled(false);
            try
            {
                var result = await gitAdapter.SwitchBranchAsync(branchName, CancellationToken.None);
                if (result.Success)
                {
                    ShowMessage($"Switched to branch '{branchName}'.", false);
                    _branchNameField.value = "";
                    await RefreshAllData();
                }
                else
                {
                    ShowMessage($"Switch branch failed: {result.Message}", true);
                }
            }
            catch (Exception ex)
            {
                ShowMessage($"Error switching branch: {ex.Message}", true);
            }
            finally
            {
                SetOperationButtonsEnabled(true);
            }
        }

        private async void OnStashClicked()
        {
            if (!(_adapter is GitAdapter gitAdapter)) return;

            SetOperationButtonsEnabled(false);
            try
            {
                var result = await gitAdapter.StashAsync(null, CancellationToken.None);
                if (result.Success)
                {
                    ShowMessage("Changes stashed successfully.", false);
                    await RefreshAllData();
                }
                else
                {
                    ShowMessage($"Stash failed: {result.Message}", true);
                }
            }
            catch (Exception ex)
            {
                ShowMessage($"Error stashing: {ex.Message}", true);
            }
            finally
            {
                SetOperationButtonsEnabled(true);
            }
        }

        private async void OnStashPopClicked()
        {
            if (!(_adapter is GitAdapter gitAdapter)) return;

            SetOperationButtonsEnabled(false);
            try
            {
                var result = await gitAdapter.StashPopAsync(CancellationToken.None);
                if (result.Success)
                {
                    ShowMessage("Stash popped successfully.", false);
                    await RefreshAllData();
                }
                else
                {
                    ShowMessage($"Stash pop failed: {result.Message}", true);
                }
            }
            catch (Exception ex)
            {
                ShowMessage($"Error popping stash: {ex.Message}", true);
            }
            finally
            {
                SetOperationButtonsEnabled(true);
            }
        }

        #endregion

        #region View Diff

        private async void OnViewDiffClicked()
        {
            string filePath = _selectedFiles.Count == 1 ? _selectedFiles.First() : null;
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

        /// <summary>
        /// 启用/禁用所有操作按钮
        /// </summary>
        private void SetOperationButtonsEnabled(bool enabled)
        {
            _stageAllButton?.SetEnabled(enabled);
            _unstageAllButton?.SetEnabled(enabled);
            _commitButton?.SetEnabled(enabled);
            _syncButton?.SetEnabled(enabled);
            _revertButton?.SetEnabled(enabled);
            _createBranchButton?.SetEnabled(enabled);
            _switchBranchButton?.SetEnabled(enabled);
            _stashButton?.SetEnabled(enabled);
            _stashPopButton?.SetEnabled(enabled);
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
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        #endregion
    }
}
