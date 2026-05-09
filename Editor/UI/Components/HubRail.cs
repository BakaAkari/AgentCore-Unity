using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Components
{
    /// <summary>
    /// Hub 模块标识枚举。
    /// 定义主窗口中可切换的功能模块。
    /// </summary>
    public enum HubModule
    {
        /// <summary>对话模块（默认）</summary>
        Chat,

        /// <summary>知识库模块（LightRAG）</summary>
        Knowledge,

        /// <summary>记忆模块（mem0）</summary>
        Memory
    }

    /// <summary>
    /// Hub Rail 导航组件。
    /// <para>
    /// 位于主窗口最左侧的窄栏（~52px），提供模块切换导航。
    /// 顶部为模块导航按钮（Chat / Know / Mem），底部固定 Settings 按钮。
    /// </para>
    /// </summary>
    public class HubRail : VisualElement
    {
        /// <summary>EditorPrefs key：上次激活的 Hub 模块</summary>
        private const string ActiveModuleKey = "AgentCore_ActiveHubModule";

        /// <summary>当前激活的模块</summary>
        private HubModule _activeModule;

        /// <summary>模块按钮字典</summary>
        private readonly Dictionary<HubModule, Button> _moduleButtons = new();

        /// <summary>Settings 按钮</summary>
        private Button _settingsButton;

        /// <summary>
        /// 模块切换事件。
        /// 当用户点击不同模块按钮时触发，参数为新激活的模块。
        /// </summary>
        public event Action<HubModule> OnModuleChanged;

        /// <summary>
        /// 当前激活的模块。
        /// </summary>
        public HubModule ActiveModule => _activeModule;

        /// <summary>
        /// 创建 HubRail 实例并构建 UI。
        /// </summary>
        public HubRail()
        {
            name = "hub-rail";
            AddToClassList("hub-rail");

            // 恢复上次激活的模块
            var savedModule = EditorPrefs.GetString(ActiveModuleKey, HubModule.Chat.ToString());
            if (Enum.TryParse<HubModule>(savedModule, out var parsed))
            {
                _activeModule = parsed;
            }
            else
            {
                _activeModule = HubModule.Chat;
            }

            BuildUI();
            UpdateActiveState();
        }

        /// <summary>
        /// 构建 Hub Rail 的 UI 结构。
        /// </summary>
        private void BuildUI()
        {
            // 模块导航区域（顶部对齐）
            var navContainer = new VisualElement();
            navContainer.name = "hub-rail-nav";
            navContainer.AddToClassList("hub-rail__nav");

            AddModuleButton(navContainer, HubModule.Chat, "Chat");
            AddModuleButton(navContainer, HubModule.Knowledge, "Know");
            AddModuleButton(navContainer, HubModule.Memory, "Mem");

            Add(navContainer);

            // 弹性间隔（撑开中间空间）
            var spacer = new VisualElement();
            spacer.AddToClassList("hub-rail__spacer");
            Add(spacer);

            // Settings 按钮（底部固定）
            _settingsButton = new Button(OnSettingsClicked);
            _settingsButton.name = "hub-rail-settings";
            _settingsButton.text = "Set";
            _settingsButton.tooltip = "打开 AgentCore 设置";
            _settingsButton.AddToClassList("hub-rail__button");
            _settingsButton.AddToClassList("hub-rail__settings-button");
            Add(_settingsButton);
        }

        /// <summary>
        /// 添加一个模块导航按钮。
        /// </summary>
        /// <param name="container">父容器</param>
        /// <param name="module">模块标识</param>
        /// <param name="label">按钮文字</param>
        private void AddModuleButton(VisualElement container, HubModule module, string label)
        {
            var button = new Button(() => OnModuleButtonClicked(module));
            button.name = $"hub-rail-{module.ToString().ToLowerInvariant()}";
            button.text = label;
            button.tooltip = module.ToString();
            button.AddToClassList("hub-rail__button");
            button.AddToClassList("hub-rail__module-button");
            container.Add(button);
            _moduleButtons[module] = button;
        }

        /// <summary>
        /// 模块按钮点击处理。
        /// </summary>
        /// <param name="module">被点击的模块</param>
        private void OnModuleButtonClicked(HubModule module)
        {
            if (_activeModule == module)
            {
                // 点击已激活的模块 → 不切换，但可以通知外部（用于折叠 Context Sidebar）
                return;
            }

            _activeModule = module;
            EditorPrefs.SetString(ActiveModuleKey, module.ToString());
            UpdateActiveState();
            OnModuleChanged?.Invoke(module);
        }

        /// <summary>
        /// 更新所有按钮的激活状态样式。
        /// </summary>
        private void UpdateActiveState()
        {
            foreach (var kvp in _moduleButtons)
            {
                if (kvp.Key == _activeModule)
                {
                    kvp.Value.AddToClassList("hub-rail__button--active");
                }
                else
                {
                    kvp.Value.RemoveFromClassList("hub-rail__button--active");
                }
            }
        }

        /// <summary>
        /// Settings 按钮点击处理。
        /// 打开 AgentCore 的 Project Settings 页面。
        /// </summary>
        private static void OnSettingsClicked()
        {
            SettingsService.OpenProjectSettings("Project/AgentCore");
        }

        /// <summary>
        /// 以编程方式切换到指定模块。
        /// </summary>
        /// <param name="module">目标模块</param>
        public void SetActiveModule(HubModule module)
        {
            if (_activeModule == module) return;

            _activeModule = module;
            EditorPrefs.SetString(ActiveModuleKey, module.ToString());
            UpdateActiveState();
            OnModuleChanged?.Invoke(module);
        }
    }
}
