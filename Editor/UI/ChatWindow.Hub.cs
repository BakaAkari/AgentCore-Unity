using AgentCore.Editor.UI.Components;
using UnityEditor;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI
{
    /// <summary>
    /// ChatWindow 分部类 — Hub 模块切换逻辑。
    /// 包含 Hub Rail 模块切换、Knowledge ask-agent 请求处理和侧边栏可见性控制。
    /// </summary>
    public partial class ChatWindow
    {
        #region Hub 模块切换

        /// <summary>
        /// Hub 模块切换事件处理。
        /// 当用户在 Hub Rail 中点击不同模块按钮时调用。
        /// </summary>
        /// <param name="module">新激活的模块</param>
        private void OnHubModuleChanged(HubModule module)
        {
            SwitchToModule(module);
        }

        /// <summary>
        /// 切换到指定的 Hub 模块。
        /// 控制 Main Content 面板和 Context Sidebar 的显示/隐藏。
        /// </summary>
        /// <param name="module">目标模块</param>
        private void SwitchToModule(HubModule module)
        {
            // 1. 切换 Main Content 面板可见性
            if (_chatPanel != null)
                _chatPanel.style.display = module == HubModule.Chat ? DisplayStyle.Flex : DisplayStyle.None;
            if (_knowledgePanel != null)
                _knowledgePanel.style.display = module == HubModule.Knowledge ? DisplayStyle.Flex : DisplayStyle.None;
            if (_memoryPanel != null)
                _memoryPanel.style.display = module == HubModule.Memory ? DisplayStyle.Flex : DisplayStyle.None;

            // 2. 通知 KnowledgeBasePanel 激活/停用
            if (_knowledgeBasePanel != null)
            {
                if (module == HubModule.Knowledge)
                    _knowledgeBasePanel.OnActivated();
                else
                    _knowledgeBasePanel.OnDeactivated();
            }

            // 2.5 通知 MemoryPanel 激活/停用
            if (_memoryPanelComponent != null)
            {
                if (module == HubModule.Memory)
                    _memoryPanelComponent.OnActivated();
                else
                    _memoryPanelComponent.OnDeactivated();
            }

            // 3. 控制 Context Sidebar 可见性
            // Chat 模块：显示会话列表侧边栏（根据 _sidebarExpanded 状态）
            // Knowledge / Memory 模块：暂时隐藏侧边栏（后续 Phase 可扩展各模块的上下文面板）
            UpdateContextSidebarVisibility(module);

            // 4. Chat 模块激活时刷新会话列表
            if (module == HubModule.Chat && _sidebarExpanded)
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
            _hubRail?.SetActiveModule(HubModule.Chat);
            SwitchToModule(HubModule.Chat);

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
        /// <param name="module">当前激活的模块</param>
        private void UpdateContextSidebarVisibility(HubModule module)
        {
            if (_contextSidebar == null) return;

            // Chat 模块且侧边栏展开时显示
            bool shouldShow = module == HubModule.Chat && _sidebarExpanded;

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
                UpdateContextSidebarVisibility(_hubRail.ActiveModule);
            }

            if (_sidebarExpanded)
            {
                RefreshSessionList();
            }
        }

        #endregion
    }
}
