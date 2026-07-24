using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Cloud;
using AgentCore.Editor.Config;
using UnityEditor;
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

        /// <summary>
        /// 创建 MemoryPanel 实例并构建 UI。
        /// </summary>
        public MemoryPanel()
        {
            AddToClassList("memory-panel-content");
            BuildUI();
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
            _addMemoryField.RegisterValueChangedCallback(_ => UpdateActionButtons());
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
                _statusConnectionLabel.EnableInClassList("memory-panel__status--warn", true);
                _statusConnectionLabel.EnableInClassList("memory-panel__status--ok", false);
                _statusConnectionLabel.EnableInClassList("memory-panel__status--error", false);
            }
            else
            {
                _statusConnectionLabel.text = AgentCore.Editor.L10n.Loc.Tr("memory.status.connection.notTested", "连接状态：未测试");
                _statusConnectionLabel.EnableInClassList("memory-panel__status--warn", false);
                _statusConnectionLabel.EnableInClassList("memory-panel__status--ok", false);
                _statusConnectionLabel.EnableInClassList("memory-panel__status--error", false);
            }

            _createUserButton.style.display = DisplayStyle.None;
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
                    _statusConnectionLabel.text = AgentCore.Editor.L10n.Loc.Tr("memory.status.connection.mem0Disabled", "连接状态：mem0 未启用");
                    SetConnectionStatusClass(false, true, false);
                    return;
                }

                var connection = await client.TestConnectionAsync(_statusCts.Token);
                if (!connection.success)
                {
                    _statusConnectionLabel.text = AgentCore.Editor.L10n.Loc.Tr("memory.status.connection.failed", "连接状态：失败 - {0}", connection.message);
                    SetConnectionStatusClass(false, false, true);
                    return;
                }

                var user = await client.CheckUserExistsAsync(_statusCts.Token);
                if (user.exists)
                {
                    _statusConnectionLabel.text = AgentCore.Editor.L10n.Loc.Tr("memory.status.connection.userExists", "连接状态：已连接，用户存在");
                    SetConnectionStatusClass(true, false, false);
                }
                else if (user.status == Mem0ConnectionStatus.UserNotFound)
                {
                    _statusConnectionLabel.text = AgentCore.Editor.L10n.Loc.Tr("memory.status.connection.userMissing", "连接状态：已连接，但用户不存在");
                    SetConnectionStatusClass(false, true, false);
                    _createUserButton.style.display = DisplayStyle.Flex;
                }
                else
                {
                    _statusConnectionLabel.text = AgentCore.Editor.L10n.Loc.Tr("memory.status.connection.result", "连接状态：{0}", user.message);
                    SetConnectionStatusClass(false, false, true);
                }
            }
            catch (OperationCanceledException)
            {
                _statusConnectionLabel.text = AgentCore.Editor.L10n.Loc.Tr("memory.status.connection.canceled", "连接状态：已取消");
                SetConnectionStatusClass(false, true, false);
            }
            catch (Exception ex)
            {
                _statusConnectionLabel.text = AgentCore.Editor.L10n.Loc.Tr("memory.status.connection.error", "连接状态：错误 - {0}", ex.Message);
                SetConnectionStatusClass(false, false, true);
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
                    _statusConnectionLabel.text = AgentCore.Editor.L10n.Loc.Tr("memory.status.connection.result", "连接状态：{0}", result.message);
                    SetConnectionStatusClass(true, false, false);
                    _createUserButton.style.display = DisplayStyle.None;
                }
                else
                {
                    _statusConnectionLabel.text = AgentCore.Editor.L10n.Loc.Tr("memory.status.connection.createUserFailed", "连接状态：创建用户失败 - {0}", result.message);
                    SetConnectionStatusClass(false, false, true);
                    _createUserButton.style.display = DisplayStyle.Flex;
                }
            }
            catch (OperationCanceledException)
            {
                _statusConnectionLabel.text = AgentCore.Editor.L10n.Loc.Tr("memory.status.connection.canceled", "连接状态：已取消");
                SetConnectionStatusClass(false, true, false);
            }
            catch (Exception ex)
            {
                _statusConnectionLabel.text = AgentCore.Editor.L10n.Loc.Tr("memory.status.connection.error", "连接状态：错误 - {0}", ex.Message);
                SetConnectionStatusClass(false, false, true);
            }
            finally
            {
                _createUserButton.SetEnabled(true);
                UpdateActionButtons();
            }
        }

        private static void OnOpenSettingsClicked()
        {
            SettingsService.OpenProjectSettings("Project/AgentCore");
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

        private void RenderMemoryList(List<Mem0Memory> memories)
        {
            _memoriesScrollView.Clear();

            if (memories == null || memories.Count == 0)
            {
                RenderMessage(AgentCore.Editor.L10n.Loc.Tr("memory.status.empty", "暂无记忆。"), false);
                _memoriesSummaryLabel.text = AgentCore.Editor.L10n.Loc.Tr("memory.summary.total", "记忆总数：0 条。");
                return;
            }

            foreach (var memory in memories)
            {
                _memoriesScrollView.Add(BuildMemoryItem(memory));
            }

            _memoriesSummaryLabel.text = AgentCore.Editor.L10n.Loc.Tr("memory.summary.loaded", "已加载 {0} 条记忆。", memories.Count);
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
    }
}
