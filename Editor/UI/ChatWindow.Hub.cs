using System;
using System.Collections.Generic;
using System.Linq;
using AgentCore.Editor.Config;
using AgentCore.Editor.Extensions;
using AgentCore.Editor.UI.Components;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI
{
    /// <summary>
    /// ChatWindow 分部类 — Hub 模块切换逻辑。
    /// 包含 Hub Rail 模块切换、Knowledge ask-agent 请求处理和侧边栏可见性控制。
    /// </summary>
    public partial class ChatWindow
    {
        #region Hub 模块常量

        /// <summary>Chat 模块 ID。</summary>
        private const string ChatModuleId = "chat";

        #endregion

        #region Hub 生命周期

        /// <summary>缓存的面板相关设置快照，用于判断是否需要重建 Hub。</summary>
        private (bool mem0, bool lightrag) _hubPanelStateSnapshot;

        /// <summary>
        /// 订阅设置变更事件，用于在服务启用/禁用时动态重建 Hub 面板。
        /// </summary>
        private void SubscribeHubSettingsChanged()
        {
            _hubPanelStateSnapshot = CaptureHubPanelState();
            AgentCoreSettings.OnSettingsChanged += OnSettingsChangedRebuildHub;
        }

        /// <summary>
        /// 取消订阅设置变更事件。
        /// </summary>
        private void UnsubscribeHubSettingsChanged()
        {
            AgentCoreSettings.OnSettingsChanged -= OnSettingsChangedRebuildHub;
        }

        /// <summary>
        /// 捕获当前影响 Hub 面板可见性的设置状态。
        /// </summary>
        private static (bool mem0, bool lightrag) CaptureHubPanelState()
        {
            var s = AgentCoreSettings.instance;
            return (s.mem0Enabled, s.lightragEnabled);
        }

        /// <summary>
        /// 设置变更回调：仅当面板相关设置变化时才重建 Hub。
        /// 避免每次保存设置都执行昂贵的面板重建。
        /// </summary>
        private void OnSettingsChangedRebuildHub()
        {
            var current = CaptureHubPanelState();
            if (current == _hubPanelStateSnapshot)
                return;

            _hubPanelStateSnapshot = current;
            RebuildHubModules();
        }

        /// <summary>
        /// 重建 Hub 模块面板和 Rail 导航。
        /// 在服务启用/禁用状态变化后调用，动态更新可用面板列表。
        /// </summary>
        private void RebuildHubModules()
        {
            if (rootVisualElement == null) return;

            // 1. 记住当前激活的模块
            var previousModuleId = _hubRail?.ActiveModuleId ?? ChatModuleId;

            // 2. 释放旧面板和 Rail
            DisposeHubPanels();
            if (_hubRail != null)
            {
                _hubRail.OnModuleChanged -= OnHubModuleChanged;
                _hubRail.RemoveFromHierarchy();
                _hubRail = null;
            }

            // 3. 重建面板
            InitializeHubPanels();

            // 4. 重建 Hub Rail
            _hubRail = new HubRail(CreateHubModuleDefinitions(), previousModuleId);
            var mainBody = rootVisualElement.Q<VisualElement>("main-body");
            mainBody?.Insert(0, _hubRail);
            _hubRail.OnModuleChanged += OnHubModuleChanged;

            // 5. 切换到之前的模块（如果仍存在），否则回退到 Chat
            var targetModule = _hubPanels.ContainsKey(previousModuleId) ? previousModuleId : ChatModuleId;
            SwitchToModule(targetModule);
        }

        #endregion

        #region Hub 模块切换

        /// <summary>
        /// 挂载工具栏状态扩展。
        /// </summary>
        private void MountToolbarStatusContributions()
        {
            DisposeToolbarStatusContributions();

            var toolbar = rootVisualElement.Q<VisualElement>("toolbar");
            if (toolbar == null)
                return;

            var insertIndex = toolbar.childCount;
            foreach (var contribution in AgentCoreExtensionRegistry.Statuses)
            {
                if (contribution == null)
                    continue;

                try
                {
                    var element = contribution.CreateStatusElement();
                    if (element == null)
                        continue;

                    element.name = string.IsNullOrWhiteSpace(element.name)
                        ? $"{contribution.Id}-status"
                        : element.name;
                    toolbar.Insert(Math.Max(0, insertIndex), element);
                    insertIndex++;
                    _toolbarStatusElements.Add(element);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[AgentCore] Failed to create toolbar status contribution '{contribution.Id}': {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 释放工具栏状态扩展资源。
        /// </summary>
        private void DisposeToolbarStatusContributions()
        {
            foreach (var element in _toolbarStatusElements)
            {
                if (element is IDisposable disposable)
                {
                    disposable.Dispose();
                }

                element?.RemoveFromHierarchy();
            }

            _toolbarStatusElements.Clear();
        }

        /// <summary>
        /// 创建当前窗口可用的 Hub 模块定义。
        /// </summary>
        /// <returns>Hub 模块定义列表。</returns>
        private List<HubModuleDefinition> CreateHubModuleDefinitions()
        {
            var modules = new List<HubModuleDefinition>
            {
                new HubModuleDefinition(ChatModuleId, "Chat", "Chat", 0)
            };

            modules.AddRange(_hubPanelContributions.Values
                .OrderBy(contribution => contribution.Order)
                .ThenBy(contribution => contribution.Id, StringComparer.Ordinal)
                .Select(contribution => new HubModuleDefinition(
                    contribution.Id,
                    contribution.Label,
                    contribution.Tooltip,
                    contribution.Order)));

            return modules;
        }

        /// <summary>
        /// 初始化 Hub 动态扩展面板。
        /// </summary>
        private void InitializeHubPanels()
        {
            _hubPanels.Clear();
            _hubPanelContributions.Clear();
            _extensionPanelHost?.Clear();

            if (_chatPanel != null)
            {
                _hubPanels[ChatModuleId] = _chatPanel;
            }

            foreach (var contribution in AgentCoreExtensionRegistry.Panels)
            {
                if (contribution == null || string.IsNullOrWhiteSpace(contribution.Id))
                    continue;

                if (_hubPanelContributions.ContainsKey(contribution.Id))
                    continue;

                try
                {
                    var panel = contribution.CreatePanel();
                    if (panel == null)
                        continue;

                    panel.name = string.IsNullOrWhiteSpace(panel.name)
                        ? $"{contribution.Id}-panel"
                        : panel.name;
                    panel.style.display = DisplayStyle.None;

                    _hubPanelContributions[contribution.Id] = contribution;
                    _hubPanels[contribution.Id] = panel;
                    _extensionPanelHost?.Add(panel);

                    if (panel is KnowledgeBasePanel knowledgePanel)
                    {
                        knowledgePanel.OnAskAgentRequested += OnKnowledgeAskAgentRequested;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[AgentCore] Failed to create Hub panel contribution '{contribution.Id}': {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 释放 Hub 动态扩展面板资源。
        /// </summary>
        private void DisposeHubPanels()
        {
            foreach (var panel in _hubPanels.Values)
            {
                if (panel is KnowledgeBasePanel knowledgePanel)
                {
                    knowledgePanel.OnAskAgentRequested -= OnKnowledgeAskAgentRequested;
                }

                if (panel is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }

            _hubPanels.Clear();
            _hubPanelContributions.Clear();
            _extensionPanelHost?.Clear();
        }

        /// <summary>
        /// Hub 模块切换事件处理。
        /// 当用户在 Hub Rail 中点击不同模块按钮时调用。
        /// </summary>
        /// <param name="moduleId">新激活的模块 ID。</param>
        private void OnHubModuleChanged(string moduleId)
        {
            SwitchToModule(moduleId);
        }

        /// <summary>
        /// 切换到指定的 Hub 模块。
        /// 控制 Main Content 面板和 Context Sidebar 的显示/隐藏。
        /// </summary>
        /// <param name="moduleId">目标模块 ID。</param>
        private void SwitchToModule(string moduleId)
        {
            foreach (var kvp in _hubPanels)
            {
                var isActive = kvp.Key == moduleId;
                kvp.Value.style.display = isActive ? DisplayStyle.Flex : DisplayStyle.None;

                if (_hubPanelContributions.TryGetValue(kvp.Key, out var contribution))
                {
                    try
                    {
                        if (isActive)
                            contribution.OnActivated(kvp.Value);
                        else
                            contribution.OnDeactivated(kvp.Value);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[AgentCore] Hub panel contribution '{kvp.Key}' lifecycle callback failed: {ex.Message}");
                    }
                }
            }

            // Chat 模块：显示会话列表侧边栏（根据 _sidebarExpanded 状态）
            // 其他模块：暂时隐藏侧边栏（后续 Phase 可扩展各模块的上下文面板）
            UpdateContextSidebarVisibility(moduleId);

            // Chat 模块激活时刷新会话列表
            if (moduleId == ChatModuleId && _sidebarExpanded)
            {
                RefreshSessionList();
            }
        }

        /// <summary>
        /// 处理 KnowledgeBasePanel 的"向 Agent 询问此文档"请求。
        /// 切换到 Chat 模块，并将建议的提示词填入输入框。
        /// </summary>
        /// <param name="prompt">建议的提示词文本</param>
        private void OnKnowledgeAskAgentRequested(string prompt)
        {
            // 切换到 Chat 模块
            _hubRail?.SetActiveModule(ChatModuleId);
            SwitchToModule(ChatModuleId);

            // 填入提示词并聚焦输入框
            if (_inputField != null)
            {
                _inputField.value = prompt;
                _inputField.Focus();
                // 将光标移到末尾
                _inputField.SelectRange(prompt.Length, prompt.Length);
            }
        }

        /// <summary>
        /// 根据当前模块和侧边栏状态更新 Context Sidebar 的显示/隐藏。
        /// </summary>
        /// <param name="moduleId">当前激活的模块 ID。</param>
        private void UpdateContextSidebarVisibility(string moduleId)
        {
            if (_contextSidebar == null) return;

            // Chat 模块且侧边栏展开时显示
            bool shouldShow = moduleId == ChatModuleId && _sidebarExpanded;

            if (shouldShow)
            {
                _contextSidebar.AddToClassList("sidebar-visible");
            }
            else
            {
                _contextSidebar.RemoveFromClassList("sidebar-visible");
            }
        }

        /// <summary>
        /// 切换 Chat 模块侧边栏的展开/折叠状态。
        /// </summary>
        private void ToggleSidebar()
        {
            _sidebarExpanded = !_sidebarExpanded;
            EditorPrefs.SetBool(SidebarExpandedKey, _sidebarExpanded);

            // 仅在 Chat 模块时更新侧边栏可见性
            if (_hubRail != null)
            {
                UpdateContextSidebarVisibility(_hubRail.ActiveModuleId);
            }

            if (_sidebarExpanded)
            {
                RefreshSessionList();
            }
        }

        #endregion
    }
}
