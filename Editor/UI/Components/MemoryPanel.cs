using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Cloud;
using AgentCore.Editor.Config;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Components
{
    /// <summary>
    /// Memory 面板组件。
    /// 提供 mem0 记忆服务的状态显示、连接测试、用户创建、记忆添加、搜索、列表刷新和删除功能。
    /// </summary>
    public class MemoryPanel : VisualElement
    {
        private const int DefaultListLimit = 50;
        private const int DefaultSearchLimit = 20;
        private const int PreviewMaxLength = 80;

        internal const string PanelCacheSessionKey = "AgentCore.Memory.Panel.Cache";
        internal const int MaxCachedMemories = 200;
        internal const int MaxCachedContentLength = 2000;

        internal const int ConnectionStateNone = 0;
        internal const int ConnectionStateOk = 1;
        internal const int ConnectionStateWarn = 2;
        internal const int ConnectionStateError = 3;

        private Label _statusEnabledLabel;
        private Label _statusEndpointLabel;
        private Label _statusUserLabel;
        private Label _statusConnectionLabel;
        private Button _testConnectionButton;
        private Button _createUserButton;
        private Button _openSettingsButton;

        private TextField _addMemoryField;
        private Button _addMemoryButton;
        private Label _addResultLabel;

        private VisualElement _memoriesSection;
        private Button _refreshMemoriesButton;
        private ScrollView _memoriesScrollView;
        private Label _memoriesSummaryLabel;

        private CancellationTokenSource _statusCts;
        private CancellationTokenSource _addCts;
        private CancellationTokenSource _listCts;
        private CancellationTokenSource _deleteCts;

        private bool _hasCachedConnectionStatus;
        private string _cachedConnectionText;
        private int _cachedConnectionState;
        private bool _cachedShowCreateUserButton;
        private string _cachedEndpoint;
        private string _cachedUserId;
        private bool _hasLoadedMemories;
        private List<Mem0Memory> _cachedMemories = new List<Mem0Memory>();
        private string _searchQuery;
        private string _selectedMemoryId;
        private bool _suppressCacheSave;

        /// <summary>
        /// 创建 MemoryPanel 实例并构建 UI。
        /// </summary>
        public MemoryPanel()
        {
            AddToClassList("memory-panel-content");
            BuildUI();
            RestoreFromSessionCache();
            RefreshStatus();
        }

        private void BuildUI()
        {
            var titleLabel = new Label("Memory");
            titleLabel.AddToClassList("memory-panel__title");
            Add(titleLabel);

            var statusSection = CreateSection(AgentCore.Editor.L10n.Loc.Tr("memory.status.section", "状态"));
            Add(statusSection);

            _statusEnabledLabel = new Label();
            _statusEnabledLabel.AddToClassList("memory-panel__status-row");
            statusSection.Add(_statusEnabledLabel);

            _statusEndpointLabel = new Label();
            _statusEndpointLabel.AddToClassList("memory-panel__status-row");
            _statusEndpointLabel.AddToClassList("memory-panel__status-row--muted");
            statusSection.Add(_statusEndpointLabel);

            _statusUserLabel = new Label();
            _statusUserLabel.AddToClassList("memory-panel__status-row");
            _statusUserLabel.AddToClassList("memory-panel__status-row--muted");
            statusSection.Add(_statusUserLabel);

            _statusConnectionLabel = new Label();
            _statusConnectionLabel.AddToClassList("memory-panel__status-row");
            statusSection.Add(_statusConnectionLabel);

            var statusButtonRow = new VisualElement();
            statusButtonRow.AddToClassList("memory-panel__button-row");
            statusSection.Add(statusButtonRow);

            _testConnectionButton = new Button(OnTestConnectionClicked) { text = AgentCore.Editor.L10n.Loc.Tr("memory.button.testConnection", "测试连接") };
            _testConnectionButton.AddToClassList("memory-panel__button");
            statusButtonRow.Add(_testConnectionButton);

            _createUserButton = new Button(OnCreateUserClicked) { text = AgentCore.Editor.L10n.Loc.Tr("memory.button.createUser", "创建用户") };
            _createUserButton.AddToClassList("memory-panel__button");
            _createUserButton.AddToClassList("memory-panel__button--secondary");
            _createUserButton.style.display = DisplayStyle.None;
            statusButtonRow.Add(_createUserButton);

            _openSettingsButton = new Button(OnOpenSettingsClicked) { text = AgentCore.Editor.L10n.Loc.Tr("memory.button.openSettings", "打开设置") };
            _openSettingsButton.AddToClassList("memory-panel__button");
            _openSettingsButton.AddToClassList("memory-panel__button--secondary");
            statusButtonRow.Add(_openSettingsButton);

            var addSection = CreateSection(AgentCore.Editor.L10n.Loc.Tr("memory.section.addMemory", "添加记忆"));
            Add(addSection);

            _addMemoryField = new TextField { multiline = true };
            _addMemoryField.AddToClassList("memory-panel__text-field");
            _addMemoryField.RegisterValueChangedCallback(_ =>
            {
                UpdateActionButtons();
                if (!_suppressCacheSave)
                    SaveToSessionCache();
            });
            addSection.Add(_addMemoryField);

            var addHint = new Label(AgentCore.Editor.L10n.Loc.Tr("memory.hint.addMemory", "写入需要长期保存的偏好、约定、架构决策或技术发现。请输入明确、完整的一句话或一段话。"));
            addHint.AddToClassList("memory-panel__hint");
            addSection.Add(addHint);

            _addMemoryButton = new Button(OnAddMemoryClicked) { text = AgentCore.Editor.L10n.Loc.Tr("memory.button.addMemory", "+ 添加记忆") };
            _addMemoryButton.AddToClassList("memory-panel__button");
            _addMemoryButton.AddToClassList("memory-panel__button--primary");
            addSection.Add(_addMemoryButton);

            _addResultLabel = new Label();
            _addResultLabel.AddToClassList("memory-panel__result-label");
            _addResultLabel.style.display = DisplayStyle.None;
            addSection.Add(_addResultLabel);

            _memoriesSection = new VisualElement();
            _memoriesSection.AddToClassList("memory-panel__section");
            _memoriesSection.style.flexGrow = 1;
            _memoriesSection.style.flexShrink = 1;
            _memoriesSection.style.minHeight = 0;
            Add(_memoriesSection);

            var memoriesTitleRow = new VisualElement();
            memoriesTitleRow.AddToClassList("memory-panel__list-title-row");
            _memoriesSection.Add(memoriesTitleRow);

            var memoriesTitle = new Label(AgentCore.Editor.L10n.Loc.Tr("memory.section.memoryList", "记忆列表"));
            memoriesTitle.AddToClassList("memory-panel__section-title");
            memoriesTitle.style.flexGrow = 1;
            memoriesTitle.style.marginBottom = 0;
            memoriesTitleRow.Add(memoriesTitle);

            _refreshMemoriesButton = new Button(OnRefreshMemoriesClicked) { text = AgentCore.Editor.L10n.Loc.Tr("memory.button.refresh", "刷新") };
            _refreshMemoriesButton.AddToClassList("memory-panel__button");
            _refreshMemoriesButton.AddToClassList("memory-panel__button--secondary");
            _refreshMemoriesButton.AddToClassList("memory-panel__button--small");
            memoriesTitleRow.Add(_refreshMemoriesButton);

            _memoriesScrollView = new ScrollView(ScrollViewMode.Vertical);
            _memoriesScrollView.AddToClassList("memory-panel__memories-scroll");
            _memoriesSection.Add(_memoriesScrollView);

            var placeholder = new Label(AgentCore.Editor.L10n.Loc.Tr("memory.list.placeholder", "点击'刷新'加载记忆列表。"));
            placeholder.AddToClassList("memory-panel__hint");
            placeholder.name = "memory-placeholder";
            _memoriesScrollView.Add(placeholder);

            _memoriesSummaryLabel = new Label();
            _memoriesSummaryLabel.AddToClassList("memory-panel__hint");
            _memoriesSection.Add(_memoriesSummaryLabel);

            UpdateActionButtons();
        }

        private static VisualElement CreateSection(string title)
        {
            var section = new VisualElement();
            section.AddToClassList("memory-panel__section");

            var titleLabel = new Label(title);
            titleLabel.AddToClassList("memory-panel__section-title");
            section.Add(titleLabel);

            return section;
        }

        /// <summary>
        /// 根据当前设置刷新状态区域显示。
        /// </summary>
        public void RefreshStatus()
        {
            var settings = AgentCoreSettings.instance;
            bool enabled = settings.mem0Enabled;
            string endpoint = string.IsNullOrEmpty(settings.mem0Endpoint) ? AgentCore.Editor.L10n.Loc.Tr("memory.status.endpoint.notConfigured", "未配置") : settings.mem0Endpoint;
            string userId = settings.EffectiveUserId;

            _statusEnabledLabel.text = enabled ? AgentCore.Editor.L10n.Loc.Tr("memory.status.mem0.enabled", "mem0：已启用") : AgentCore.Editor.L10n.Loc.Tr("memory.status.mem0.disabled", "mem0：未启用");
            _statusEnabledLabel.EnableInClassList("memory-panel__status--ok", enabled);
            _statusEnabledLabel.EnableInClassList("memory-panel__status--warn", !enabled);

            _statusEndpointLabel.text = $"Endpoint：{endpoint}";
            _statusUserLabel.text = $"User ID：{userId}";

            if (!enabled)
            {
                _statusConnectionLabel.text = AgentCore.Editor.L10n.Loc.Tr("memory.status.connection.disabled", "连接状态：未启用");
                SetConnectionStatusClass(false, true, false);
                _createUserButton.style.display = DisplayStyle.None;
            }
            else if (_hasCachedConnectionStatus && IsCacheMatchingCurrentSettings(settings))
            {
                _statusConnectionLabel.text = _cachedConnectionText;
                SetConnectionStatusClass(
                    _cachedConnectionState == ConnectionStateOk,
                    _cachedConnectionState == ConnectionStateWarn,
                    _cachedConnectionState == ConnectionStateError);
                _createUserButton.style.display = _cachedShowCreateUserButton ? DisplayStyle.Flex : DisplayStyle.None;
            }
            else
            {
                _statusConnectionLabel.text = AgentCore.Editor.L10n.Loc.Tr("memory.status.connection.notTested", "连接状态：未测试");
                SetConnectionStatusClass(false, false, false);
                _createUserButton.style.display = DisplayStyle.None;
            }

            UpdateActionButtons();
        }

        private async void OnTestConnectionClicked()
        {
            var settings = AgentCoreSettings.instance;
            if (!settings.mem0Enabled)
            {
                EditorUtility.DisplayDialog(
                    AgentCore.Editor.L10n.Loc.Tr("memory.dialog.tipTitle", "提示"),
                    AgentCore.Editor.L10n.Loc.Tr("memory.dialog.needEnable", "请先在 AgentCore Settings 中启用 mem0 服务。"),
                    AgentCore.Editor.L10n.Loc.Tr("common.ok", "确定"));
                return;
            }

            if (string.IsNullOrEmpty(settings.mem0Endpoint))
            {
                EditorUtility.DisplayDialog(
                    AgentCore.Editor.L10n.Loc.Tr("memory.dialog.tipTitle", "提示"),
                    AgentCore.Editor.L10n.Loc.Tr("memory.dialog.needConfigure", "请先在 AgentCore Settings 中配置 mem0 Endpoint。"),
                    AgentCore.Editor.L10n.Loc.Tr("common.ok", "确定"));
                return;
            }

            _statusCts?.Cancel();
            _statusCts = new CancellationTokenSource();

            _testConnectionButton.SetEnabled(false);
            _createUserButton.style.display = DisplayStyle.None;
            _statusConnectionLabel.text = AgentCore.Editor.L10n.Loc.Tr("memory.status.connection.testing", "连接状态：测试中...");
            SetConnectionStatusClass(false, false, false);

            try
            {
                var client = Mem0Client.FromSettings();
                if (client == null)
                {
                    ApplyAndCacheConnectionStatus(
                        AgentCore.Editor.L10n.Loc.Tr("memory.status.connection.mem0Disabled", "连接状态：mem0 未启用"),
                        ConnectionStateWarn,
                        showCreateUser: false);
                    return;
                }

                var connection = await client.TestConnectionAsync(_statusCts.Token);
                if (!connection.success)
                {
                    ApplyAndCacheConnectionStatus(
                        AgentCore.Editor.L10n.Loc.Tr("memory.status.connection.failed", "连接状态：失败 - {0}", connection.message),
                        ConnectionStateError,
                        showCreateUser: false);
                    return;
                }

                var user = await client.CheckUserExistsAsync(_statusCts.Token);
                if (user.exists)
                {
                    ApplyAndCacheConnectionStatus(
                        AgentCore.Editor.L10n.Loc.Tr("memory.status.connection.userExists", "连接状态：已连接，用户存在"),
                        ConnectionStateOk,
                        showCreateUser: false);
                }
                else if (user.status == Mem0ConnectionStatus.UserNotFound)
                {
                    ApplyAndCacheConnectionStatus(
                        AgentCore.Editor.L10n.Loc.Tr("memory.status.connection.userMissing", "连接状态：已连接，但用户不存在"),
                        ConnectionStateWarn,
                        showCreateUser: true);
                }
                else
                {
                    ApplyAndCacheConnectionStatus(
                        AgentCore.Editor.L10n.Loc.Tr("memory.status.connection.result", "连接状态：{0}", user.message),
                        ConnectionStateError,
                        showCreateUser: false);
                }
            }
            catch (OperationCanceledException)
            {
                ApplyAndCacheConnectionStatus(
                    AgentCore.Editor.L10n.Loc.Tr("memory.status.connection.canceled", "连接状态：已取消"),
                    ConnectionStateWarn,
                    showCreateUser: false);
            }
            catch (Exception ex)
            {
                ApplyAndCacheConnectionStatus(
                    AgentCore.Editor.L10n.Loc.Tr("memory.status.connection.error", "连接状态：错误 - {0}", ex.Message),
                    ConnectionStateError,
                    showCreateUser: false);
            }
            finally
            {
                _testConnectionButton.SetEnabled(true);
                UpdateActionButtons();
            }
        }

        private async void OnCreateUserClicked()
        {
            var settings = AgentCoreSettings.instance;
            if (!settings.mem0Enabled)
            {
                EditorUtility.DisplayDialog(
                    AgentCore.Editor.L10n.Loc.Tr("memory.dialog.tipTitle", "提示"),
                    AgentCore.Editor.L10n.Loc.Tr("memory.dialog.needEnable", "请先在 AgentCore Settings 中启用 mem0 服务。"),
                    AgentCore.Editor.L10n.Loc.Tr("common.ok", "确定"));
                return;
            }

            _statusCts?.Cancel();
            _statusCts = new CancellationTokenSource();

            _createUserButton.SetEnabled(false);
            _statusConnectionLabel.text = AgentCore.Editor.L10n.Loc.Tr("memory.status.connection.creatingUser", "连接状态：正在创建用户...");
            SetConnectionStatusClass(false, false, false);

            try
            {
                var client = Mem0Client.FromSettings();
                if (client == null)
                {
                    _statusConnectionLabel.text = AgentCore.Editor.L10n.Loc.Tr("memory.status.connection.mem0Disabled", "连接状态：mem0 未启用");
                    SetConnectionStatusClass(false, true, false);
                    return;
                }

                var result = await client.CreateUserAsync(_statusCts.Token);
                if (result.success)
                {
                    ApplyAndCacheConnectionStatus(
                        AgentCore.Editor.L10n.Loc.Tr("memory.status.connection.result", "连接状态：{0}", result.message),
                        ConnectionStateOk,
                        showCreateUser: false);
                }
                else
                {
                    ApplyAndCacheConnectionStatus(
                        AgentCore.Editor.L10n.Loc.Tr("memory.status.connection.createUserFailed", "连接状态：创建用户失败 - {0}", result.message),
                        ConnectionStateError,
                        showCreateUser: true);
                }
            }
            catch (OperationCanceledException)
            {
                ApplyAndCacheConnectionStatus(
                    AgentCore.Editor.L10n.Loc.Tr("memory.status.connection.canceled", "连接状态：已取消"),
                    ConnectionStateWarn,
                    showCreateUser: false);
            }
            catch (Exception ex)
            {
                ApplyAndCacheConnectionStatus(
                    AgentCore.Editor.L10n.Loc.Tr("memory.status.connection.error", "连接状态：错误 - {0}", ex.Message),
                    ConnectionStateError,
                    showCreateUser: false);
            }
            finally
            {
                _createUserButton.SetEnabled(true);
                UpdateActionButtons();
            }
        }

        private static void OnOpenSettingsClicked()
        {
            AgentCore.Editor.Config.AgentCoreSettingsProvider.OpenPreferences(AgentCore.Editor.Config.AgentCoreSettingsProvider.ContextMemorySectionId);
        }

        private async void OnAddMemoryClicked()
        {
            string content = _addMemoryField?.value?.Trim();
            if (string.IsNullOrEmpty(content))
            {
                EditorUtility.DisplayDialog(
                    AgentCore.Editor.L10n.Loc.Tr("memory.dialog.tipTitle", "提示"),
                    AgentCore.Editor.L10n.Loc.Tr("memory.dialog.inputEmpty", "请输入要添加的记忆内容。"),
                    AgentCore.Editor.L10n.Loc.Tr("common.ok", "确定"));
                return;
            }

            var settings = AgentCoreSettings.instance;
            if (!settings.mem0Enabled)
            {
                EditorUtility.DisplayDialog(
                    AgentCore.Editor.L10n.Loc.Tr("memory.dialog.tipTitle", "提示"),
                    AgentCore.Editor.L10n.Loc.Tr("memory.dialog.needEnable", "请先在 AgentCore Settings 中启用 mem0 服务。"),
                    AgentCore.Editor.L10n.Loc.Tr("common.ok", "确定"));
                return;
            }

            _addCts?.Cancel();
            _addCts = new CancellationTokenSource();

            _addMemoryButton.SetEnabled(false);
            ShowAddResult(AgentCore.Editor.L10n.Loc.Tr("memory.status.addingMemory", "正在添加记忆..."), false, false);

            try
            {
                var client = Mem0Client.FromSettings();
                if (client == null)
                {
                    ShowAddResult(AgentCore.Editor.L10n.Loc.Tr("memory.status.addFailed.notEnabled", "添加失败：mem0 未启用或配置无效。"), false, true);
                    return;
                }

                var metadata = new Dictionary<string, string>
                {
                    ["source"] = "memory_panel",
                    ["unity_project"] = UnityEngine.Application.productName
                };

                var result = await client.AddMemoryAsync(content, null, metadata, _addCts.Token);
                if (result.Success)
                {
                    _addMemoryField.value = string.Empty;
                    ShowAddResult(AgentCore.Editor.L10n.Loc.Tr("memory.status.added", "记忆已添加。"), true, false);
                    await RefreshMemoriesAsync();
                }
                else
                {
                    ShowAddResult(AgentCore.Editor.L10n.Loc.Tr("memory.status.addFailed", "添加失败：{0}", result.Message), false, true);
                }
            }
            catch (OperationCanceledException)
            {
                ShowAddResult(AgentCore.Editor.L10n.Loc.Tr("memory.status.addCanceled", "添加已取消。"), false, false);
            }
            catch (Exception ex)
            {
                ShowAddResult(AgentCore.Editor.L10n.Loc.Tr("memory.status.addFailed", "添加失败：{0}", ex.Message), false, true);
            }
            finally
            {
                _addMemoryButton.SetEnabled(true);
                UpdateActionButtons();
            }
        }

        private async void OnRefreshMemoriesClicked()
        {
            await RefreshMemoriesAsync();
        }

        private async Task RefreshMemoriesAsync()
        {
            var settings = AgentCoreSettings.instance;
            if (!settings.mem0Enabled)
            {
                RenderMessage(AgentCore.Editor.L10n.Loc.Tr("memory.status.mem0NotEnabled", "mem0 未启用。请先打开设置并启用 mem0 服务。"), true);
                return;
            }

            _listCts?.Cancel();
            _listCts = new CancellationTokenSource();

            _refreshMemoriesButton.SetEnabled(false);
            if (!_hasLoadedMemories || _cachedMemories.Count == 0)
                RenderMessage(AgentCore.Editor.L10n.Loc.Tr("memory.status.loading", "正在加载记忆列表..."), false);

            try
            {
                var client = Mem0Client.FromSettings();
                if (client == null)
                {
                    RenderMessage(AgentCore.Editor.L10n.Loc.Tr("memory.dialog.deleteFailedBody.notEnabled", "mem0 未启用或配置无效。"), true);
                    return;
                }

                var memories = await client.ListMemoriesAsync(null, DefaultListLimit, _listCts.Token);
                RenderMemoryList(memories);
            }
            catch (OperationCanceledException)
            {
                RenderMessage(AgentCore.Editor.L10n.Loc.Tr("memory.status.loadCanceled", "加载已取消。"), false);
            }
            catch (Exception ex)
            {
                RenderMessage(AgentCore.Editor.L10n.Loc.Tr("memory.status.loadFailed", "加载记忆列表失败：{0}", ex.Message), true);
            }
            finally
            {
                _refreshMemoriesButton.SetEnabled(true);
                UpdateActionButtons();
            }
        }

        private void RenderMemoryList(List<Mem0Memory> memories, bool persist = true)
        {
            var list = memories ?? new List<Mem0Memory>();
            _hasLoadedMemories = true;
            _cachedMemories = list;
            if (persist)
            {
                var settings = AgentCoreSettings.instance;
                _cachedEndpoint = settings.mem0Endpoint;
                _cachedUserId = settings.EffectiveUserId;
                SaveToSessionCache();
            }

            _memoriesScrollView.Clear();

            if (list.Count == 0)
            {
                RenderMessage(AgentCore.Editor.L10n.Loc.Tr("memory.status.empty", "暂无记忆。"), false);
                _memoriesSummaryLabel.text = AgentCore.Editor.L10n.Loc.Tr("memory.summary.total", "记忆总数：0 条。");
                return;
            }

            foreach (var memory in list)
            {
                _memoriesScrollView.Add(BuildMemoryItem(memory));
            }

            _memoriesSummaryLabel.text = AgentCore.Editor.L10n.Loc.Tr("memory.summary.loaded", "已加载 {0} 条记忆。", list.Count);
        }

        private VisualElement BuildMemoryItem(Mem0Memory memory)
        {
            var item = new VisualElement();
            item.AddToClassList("memory-panel__memory-item");

            var header = new VisualElement();
            header.AddToClassList("memory-panel__memory-header");
            item.Add(header);

            var title = new Label(GetMemoryTitle(memory));
            title.AddToClassList("memory-panel__memory-title");
            header.Add(title);

            var deleteButton = new Button(() => OnDeleteMemoryClicked(memory?.Id, memory?.Content)) { text = AgentCore.Editor.L10n.Loc.Tr("common.delete", "删除") };
            deleteButton.AddToClassList("memory-panel__button");
            deleteButton.AddToClassList("memory-panel__button--small");
            deleteButton.AddToClassList("memory-panel__button--danger");
            deleteButton.SetEnabled(!string.IsNullOrEmpty(memory?.Id));
            header.Add(deleteButton);

            var content = new Label(string.IsNullOrEmpty(memory?.Content) ? AgentCore.Editor.L10n.Loc.Tr("memory.item.noContent", "无内容") : memory.Content);
            content.AddToClassList("memory-panel__memory-content");
            item.Add(content);

            var meta = new Label(BuildMemoryMeta(memory));
            meta.AddToClassList("memory-panel__memory-meta");
            item.Add(meta);

            return item;
        }

        private async void OnDeleteMemoryClicked(string memoryId, string previewText)
        {
            if (string.IsNullOrEmpty(memoryId))
            {
                EditorUtility.DisplayDialog(
                    AgentCore.Editor.L10n.Loc.Tr("memory.dialog.tipTitle", "提示"),
                    AgentCore.Editor.L10n.Loc.Tr("memory.dialog.idEmpty", "无法删除：记忆 ID 为空。"),
                    AgentCore.Editor.L10n.Loc.Tr("common.ok", "确定"));
                return;
            }

            string preview = Truncate(previewText, PreviewMaxLength);
            bool confirm = EditorUtility.DisplayDialog(
                AgentCore.Editor.L10n.Loc.Tr("memory.dialog.deleteTitle", "删除记忆"),
                AgentCore.Editor.L10n.Loc.Tr("memory.dialog.deleteBody", "确定要删除这条记忆吗？\n\n{0}", preview),
                AgentCore.Editor.L10n.Loc.Tr("common.delete", "删除"),
                AgentCore.Editor.L10n.Loc.Tr("common.cancel", "取消"));

            if (!confirm)
                return;

            _deleteCts?.Cancel();
            _deleteCts = new CancellationTokenSource();

            try
            {
                var client = Mem0Client.FromSettings();
                if (client == null)
                {
                    EditorUtility.DisplayDialog(
                        AgentCore.Editor.L10n.Loc.Tr("memory.dialog.deleteFailedTitle", "删除失败"),
                        AgentCore.Editor.L10n.Loc.Tr("memory.dialog.deleteFailedBody.notEnabled", "mem0 未启用或配置无效。"),
                        AgentCore.Editor.L10n.Loc.Tr("common.ok", "确定"));
                    return;
                }

                bool deleted = await client.DeleteMemoryAsync(memoryId, null, _deleteCts.Token);
                if (!deleted)
                {
                    EditorUtility.DisplayDialog(
                        AgentCore.Editor.L10n.Loc.Tr("memory.dialog.deleteFailedTitle", "删除失败"),
                        AgentCore.Editor.L10n.Loc.Tr("memory.dialog.deleteFailedBody.serviceReject", "mem0 服务未确认删除成功。"),
                        AgentCore.Editor.L10n.Loc.Tr("common.ok", "确定"));
                    return;
                }

                await RefreshMemoriesAsync();
            }
            catch (OperationCanceledException)
            {
                RenderMessage(AgentCore.Editor.L10n.Loc.Tr("memory.status.deleteCanceled", "删除已取消。"), false);
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog(
                    AgentCore.Editor.L10n.Loc.Tr("memory.dialog.deleteFailedTitle", "删除失败"),
                    ex.Message,
                    AgentCore.Editor.L10n.Loc.Tr("common.ok", "确定"));
            }
        }

        private void RenderMessage(string message, bool isError)
        {
            _memoriesScrollView.Clear();
            var label = new Label(message);
            label.AddToClassList("memory-panel__hint");
            label.EnableInClassList("memory-panel__result--failed", isError);
            _memoriesScrollView.Add(label);
            _memoriesSummaryLabel.text = string.Empty;
        }

        private void ShowAddResult(string message, bool success, bool failed)
        {
            _addResultLabel.style.display = DisplayStyle.Flex;
            _addResultLabel.text = message;
            _addResultLabel.EnableInClassList("memory-panel__result--success", success);
            _addResultLabel.EnableInClassList("memory-panel__result--failed", failed);
        }

        private void SetConnectionStatusClass(bool ok, bool warn, bool error)
        {
            _statusConnectionLabel.EnableInClassList("memory-panel__status--ok", ok);
            _statusConnectionLabel.EnableInClassList("memory-panel__status--warn", warn);
            _statusConnectionLabel.EnableInClassList("memory-panel__status--error", error);
        }

        private void UpdateActionButtons()
        {
            var settings = AgentCoreSettings.instance;
            bool enabled = settings.mem0Enabled;
            bool hasAddContent = !string.IsNullOrWhiteSpace(_addMemoryField?.value);

            if (_testConnectionButton != null)
                _testConnectionButton.SetEnabled(enabled);

            if (_addMemoryButton != null)
                _addMemoryButton.SetEnabled(enabled && hasAddContent);

            if (_refreshMemoriesButton != null)
                _refreshMemoriesButton.SetEnabled(enabled);
        }

        private static string GetMemoryTitle(Mem0Memory memory)
        {
            if (memory == null)
                return "Memory";

            if (!string.IsNullOrEmpty(memory.Id))
                return $"Memory {memory.Id}";

            return "Memory";
        }

        private static string BuildMemoryMeta(Mem0Memory memory)
        {
            if (memory == null)
                return string.Empty;

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(memory.CreatedAt))
                parts.Add(AgentCore.Editor.L10n.Loc.Tr("memory.item.created", "创建：{0}", FormatDateTime(memory.CreatedAt)));
            if (!string.IsNullOrEmpty(memory.UpdatedAt) && memory.UpdatedAt != memory.CreatedAt)
                parts.Add(AgentCore.Editor.L10n.Loc.Tr("memory.item.updated", "更新：{0}", FormatDateTime(memory.UpdatedAt)));
            if (!string.IsNullOrEmpty(memory.State))
                parts.Add(AgentCore.Editor.L10n.Loc.Tr("memory.item.state", "状态：{0}", memory.State));

            return parts.Count > 0 ? string.Join("  ", parts) : AgentCore.Editor.L10n.Loc.Tr("memory.item.emptyMeta", "无元数据");
        }

        private static string FormatDateTime(string value)
        {
            if (DateTime.TryParse(value, out var dateTime))
                return dateTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            return value;
        }

        private static string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text))
                return AgentCore.Editor.L10n.Loc.Tr("memory.item.noContent", "无内容");
            if (text.Length <= maxLength)
                return text;
            return text.Substring(0, maxLength) + "...";
        }

        /// <summary>
        /// 面板激活时刷新设置状态。
        /// </summary>
        public void OnActivated()
        {
            if (!_hasLoadedMemories && !_hasCachedConnectionStatus)
                RestoreFromSessionCache();
            RefreshStatus();
        }

        /// <summary>
        /// 面板停用时取消非必要后台请求。
        /// </summary>
        public void OnDeactivated()
        {
            _statusCts?.Cancel();
            _listCts?.Cancel();
        }

        /// <summary>
        /// 释放面板持有的取消令牌资源。
        /// </summary>
        public void Dispose()
        {
            _statusCts?.Cancel();
            _statusCts?.Dispose();
            _statusCts = null;

            _addCts?.Cancel();
            _addCts?.Dispose();
            _addCts = null;

            _listCts?.Cancel();
            _listCts?.Dispose();
            _listCts = null;

            _deleteCts?.Cancel();
            _deleteCts?.Dispose();
            _deleteCts = null;
        }

        // ── Reload persistence cache ─────────────────────────────────────────────

        [Serializable]
        internal sealed class MemoryPanelCacheDto
        {
            public int SchemaVersion;
            public string Endpoint;
            public string UserId;
            public bool HasConnectionStatus;
            public string ConnectionStatusText;
            public int ConnectionState;
            public bool ShowCreateUserButton;
            public bool HasLoadedMemories;
            public List<CachedMemoryItem> Memories = new List<CachedMemoryItem>();
            public string SearchQuery;
            public string SelectedMemoryId;
            public string AddMemoryDraft;
        }

        [Serializable]
        internal sealed class CachedMemoryItem
        {
            public string Id;
            public string Content;
            public string UserId;
            public string CreatedAt;
            public string UpdatedAt;
            public string State;
        }

        internal static MemoryPanelCacheDto BuildCacheDto(
            string endpoint,
            string userId,
            bool hasConnectionStatus,
            string connectionStatusText,
            int connectionState,
            bool showCreateUserButton,
            bool hasLoadedMemories,
            IReadOnlyList<Mem0Memory> memories,
            string searchQuery = null,
            string selectedMemoryId = null,
            string addMemoryDraft = null,
            int maxItems = MaxCachedMemories,
            int maxContentLength = MaxCachedContentLength)
        {
            var limit = Math.Max(1, maxItems);
            var contentLimit = Math.Max(16, maxContentLength);
            var dto = new MemoryPanelCacheDto
            {
                SchemaVersion = 1,
                Endpoint = endpoint,
                UserId = userId,
                HasConnectionStatus = hasConnectionStatus && !string.IsNullOrEmpty(connectionStatusText),
                ConnectionStatusText = hasConnectionStatus ? connectionStatusText : null,
                ConnectionState = NormalizeConnectionState(connectionState),
                ShowCreateUserButton = showCreateUserButton,
                HasLoadedMemories = hasLoadedMemories,
                SearchQuery = searchQuery,
                SelectedMemoryId = selectedMemoryId,
                AddMemoryDraft = TruncateCacheText(addMemoryDraft, contentLimit)
            };

            if (memories != null)
            {
                foreach (var m in memories)
                {
                    if (m == null)
                        continue;
                    if (dto.Memories.Count >= limit)
                        break;

                    dto.Memories.Add(new CachedMemoryItem
                    {
                        Id = m.Id,
                        Content = TruncateCacheText(m.Content, contentLimit),
                        UserId = m.UserId,
                        CreatedAt = m.CreatedAt,
                        UpdatedAt = m.UpdatedAt,
                        State = m.State
                    });
                }
            }

            return dto;
        }

        internal static string SerializeCache(
            MemoryPanelCacheDto cache,
            int maxItems = MaxCachedMemories,
            int maxContentLength = MaxCachedContentLength)
        {
            if (cache == null)
                return null;

            var limit = Math.Max(1, maxItems);
            var contentLimit = Math.Max(16, maxContentLength);
            var normalized = new MemoryPanelCacheDto
            {
                SchemaVersion = 1,
                Endpoint = cache.Endpoint,
                UserId = cache.UserId,
                HasConnectionStatus = cache.HasConnectionStatus && !string.IsNullOrEmpty(cache.ConnectionStatusText),
                ConnectionStatusText = cache.HasConnectionStatus ? cache.ConnectionStatusText : null,
                ConnectionState = NormalizeConnectionState(cache.ConnectionState),
                ShowCreateUserButton = cache.ShowCreateUserButton,
                HasLoadedMemories = cache.HasLoadedMemories,
                SearchQuery = cache.SearchQuery,
                SelectedMemoryId = cache.SelectedMemoryId,
                AddMemoryDraft = TruncateCacheText(cache.AddMemoryDraft, contentLimit)
            };

            if (cache.Memories != null)
            {
                foreach (var item in cache.Memories)
                {
                    if (item == null)
                        continue;
                    if (normalized.Memories.Count >= limit)
                        break;

                    normalized.Memories.Add(new CachedMemoryItem
                    {
                        Id = item.Id,
                        Content = TruncateCacheText(item.Content, contentLimit),
                        UserId = item.UserId,
                        CreatedAt = item.CreatedAt,
                        UpdatedAt = item.UpdatedAt,
                        State = item.State
                    });
                }
            }

            return JsonUtility.ToJson(normalized);
        }

        internal static MemoryPanelCacheDto DeserializeCache(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                var dto = JsonUtility.FromJson<MemoryPanelCacheDto>(json);
                if (dto == null || dto.SchemaVersion <= 0)
                    return null;

                dto.Endpoint = NullIfEmpty(dto.Endpoint);
                dto.UserId = NullIfEmpty(dto.UserId);
                dto.ConnectionStatusText = NullIfEmpty(dto.ConnectionStatusText);
                dto.ConnectionState = NormalizeConnectionState(dto.ConnectionState);
                dto.SearchQuery = NullIfEmpty(dto.SearchQuery);
                dto.SelectedMemoryId = NullIfEmpty(dto.SelectedMemoryId);
                dto.AddMemoryDraft = NullIfEmpty(dto.AddMemoryDraft);
                dto.Memories = dto.Memories ?? new List<CachedMemoryItem>();
                return dto;
            }
            catch
            {
                return null;
            }
        }

        internal static List<Mem0Memory> ToMem0Memories(MemoryPanelCacheDto cache)
        {
            var result = new List<Mem0Memory>();
            if (cache?.Memories == null)
                return result;

            foreach (var item in cache.Memories)
            {
                if (item == null)
                    continue;
                result.Add(new Mem0Memory
                {
                    Id = NullIfEmpty(item.Id),
                    Content = NullIfEmpty(item.Content),
                    UserId = NullIfEmpty(item.UserId),
                    CreatedAt = NullIfEmpty(item.CreatedAt),
                    UpdatedAt = NullIfEmpty(item.UpdatedAt),
                    State = NullIfEmpty(item.State)
                });
            }

            return result;
        }

        internal static void SaveCacheToSession(MemoryPanelCacheDto cache, string sessionKey = PanelCacheSessionKey)
        {
            if (string.IsNullOrEmpty(sessionKey))
                return;

            try
            {
                var json = SerializeCache(cache);
                if (string.IsNullOrEmpty(json))
                    SessionState.EraseString(sessionKey);
                else
                    SessionState.SetString(sessionKey, json);
            }
            catch
            {
                // Ignore SessionState errors.
            }
        }

        internal static MemoryPanelCacheDto LoadCacheFromSession(string sessionKey = PanelCacheSessionKey)
        {
            if (string.IsNullOrEmpty(sessionKey))
                return null;

            try
            {
                return DeserializeCache(SessionState.GetString(sessionKey, string.Empty));
            }
            catch
            {
                return null;
            }
        }

        private void ApplyAndCacheConnectionStatus(string text, int connectionState, bool showCreateUser)
        {
            var settings = AgentCoreSettings.instance;
            _cachedEndpoint = settings.mem0Endpoint;
            _cachedUserId = settings.EffectiveUserId;
            _hasCachedConnectionStatus = true;
            _cachedConnectionText = text;
            _cachedConnectionState = NormalizeConnectionState(connectionState);
            _cachedShowCreateUserButton = showCreateUser;

            _statusConnectionLabel.text = text;
            SetConnectionStatusClass(
                _cachedConnectionState == ConnectionStateOk,
                _cachedConnectionState == ConnectionStateWarn,
                _cachedConnectionState == ConnectionStateError);
            _createUserButton.style.display = showCreateUser ? DisplayStyle.Flex : DisplayStyle.None;

            SaveToSessionCache();
        }

        private bool IsCacheMatchingCurrentSettings(AgentCoreSettings settings)
        {
            if (settings == null)
                return false;

            var endpointMatch = string.IsNullOrEmpty(_cachedEndpoint)
                || string.Equals(_cachedEndpoint, settings.mem0Endpoint, StringComparison.Ordinal);
            var userMatch = string.IsNullOrEmpty(_cachedUserId)
                || string.Equals(_cachedUserId, settings.EffectiveUserId, StringComparison.Ordinal);
            return endpointMatch && userMatch;
        }

        private void SaveToSessionCache()
        {
            var settings = AgentCoreSettings.instance;
            var dto = BuildCacheDto(
                _cachedEndpoint ?? settings.mem0Endpoint,
                _cachedUserId ?? settings.EffectiveUserId,
                _hasCachedConnectionStatus,
                _cachedConnectionText,
                _cachedConnectionState,
                _cachedShowCreateUserButton,
                _hasLoadedMemories,
                _cachedMemories,
                _searchQuery,
                _selectedMemoryId,
                _addMemoryField?.value);
            SaveCacheToSession(dto);
        }

        private void RestoreFromSessionCache()
        {
            var dto = LoadCacheFromSession();
            if (dto == null)
                return;

            _cachedEndpoint = dto.Endpoint;
            _cachedUserId = dto.UserId;
            _searchQuery = dto.SearchQuery;
            _selectedMemoryId = dto.SelectedMemoryId;

            if (_addMemoryField != null && !string.IsNullOrEmpty(dto.AddMemoryDraft))
            {
                _suppressCacheSave = true;
                try
                {
                    _addMemoryField.SetValueWithoutNotify(dto.AddMemoryDraft);
                }
                finally
                {
                    _suppressCacheSave = false;
                }
            }

            var settings = AgentCoreSettings.instance;
            if (!IsCacheMatchingCurrentSettings(settings))
                return;

            if (dto.HasConnectionStatus && !string.IsNullOrEmpty(dto.ConnectionStatusText))
            {
                _hasCachedConnectionStatus = true;
                _cachedConnectionText = dto.ConnectionStatusText;
                _cachedConnectionState = dto.ConnectionState;
                _cachedShowCreateUserButton = dto.ShowCreateUserButton;
            }

            if (dto.HasLoadedMemories)
            {
                var restoredMemories = ToMem0Memories(dto);
                RenderMemoryList(restoredMemories, persist: false);
            }
        }

        private static int NormalizeConnectionState(int state)
        {
            return state >= ConnectionStateNone && state <= ConnectionStateError
                ? state
                : ConnectionStateNone;
        }

        private static string NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

        private static string TruncateCacheText(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
                return value;
            return value.Substring(0, maxLength);
        }
    }
}
