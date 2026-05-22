using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Components
{
    /// <summary>
    /// Describes one Hub navigation module entry.
    /// </summary>
    public sealed class HubModuleDefinition
    {
        /// <summary>
        /// Creates a Hub module definition.
        /// </summary>
        /// <param name="id">Stable module identifier.</param>
        /// <param name="label">Short navigation label.</param>
        /// <param name="tooltip">Navigation tooltip.</param>
        /// <param name="order">Sorting order.</param>
        public HubModuleDefinition(string id, string label, string tooltip, int order)
        {
            Id = id;
            Label = label;
            Tooltip = tooltip;
            Order = order;
        }

        /// <summary>
        /// Gets the stable module identifier.
        /// </summary>
        public string Id { get; }

        /// <summary>
        /// Gets the short navigation label.
        /// </summary>
        public string Label { get; }

        /// <summary>
        /// Gets the navigation tooltip.
        /// </summary>
        public string Tooltip { get; }

        /// <summary>
        /// Gets the sorting order.
        /// </summary>
        public int Order { get; }
    }

    /// <summary>
    /// Hub Rail 导航组件。
    /// <para>
    /// 位于主窗口最左侧的窄栏（~52px），提供模块切换导航。
    /// 顶部为模块导航按钮，底部固定 Settings 按钮。
    /// </para>
    /// </summary>
    public class HubRail : VisualElement
    {
        /// <summary>EditorPrefs key：上次激活的 Hub 模块</summary>
        private const string ActiveModuleKey = "AgentCore_ActiveHubModule";

        /// <summary>当前激活的模块 ID</summary>
        private string _activeModuleId;

        /// <summary>模块定义列表</summary>
        private readonly List<HubModuleDefinition> _modules;

        /// <summary>模块按钮字典</summary>
        private readonly Dictionary<string, Button> _moduleButtons = new Dictionary<string, Button>();

        /// <summary>Settings 按钮</summary>
        private Button _settingsButton;

        /// <summary>
        /// 模块切换事件。
        /// 当用户点击不同模块按钮时触发，参数为新激活的模块 ID。
        /// </summary>
        public event Action<string> OnModuleChanged;

        /// <summary>
        /// 当前激活的模块 ID。
        /// </summary>
        public string ActiveModuleId => _activeModuleId;

        /// <summary>
        /// 创建 HubRail 实例并构建 UI。
        /// </summary>
        /// <param name="modules">可显示的 Hub 模块定义。</param>
        /// <param name="defaultModuleId">默认激活的模块 ID。</param>
        public HubRail(IEnumerable<HubModuleDefinition> modules, string defaultModuleId)
        {
            name = "hub-rail";
            AddToClassList("hub-rail");

            _modules = modules?
                .Where(module => module != null && !string.IsNullOrWhiteSpace(module.Id))
                .GroupBy(module => module.Id)
                .Select(group => group.First())
                .OrderBy(module => module.Order)
                .ThenBy(module => module.Id, StringComparer.Ordinal)
                .ToList() ?? new List<HubModuleDefinition>();

            if (_modules.Count == 0)
            {
                _modules.Add(new HubModuleDefinition("chat", "Chat", "Chat", 0));
            }

            var fallbackModuleId = !string.IsNullOrWhiteSpace(defaultModuleId) ? defaultModuleId : _modules[0].Id;
            var savedModuleId = EditorPrefs.GetString(ActiveModuleKey, fallbackModuleId);
            _activeModuleId = _modules.Any(module => module.Id == savedModuleId) ? savedModuleId : fallbackModuleId;

            if (_modules.All(module => module.Id != _activeModuleId))
            {
                _activeModuleId = _modules[0].Id;
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

            foreach (var module in _modules)
            {
                AddModuleButton(navContainer, module);
            }

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
        /// <param name="module">模块定义</param>
        private void AddModuleButton(VisualElement container, HubModuleDefinition module)
        {
            var moduleId = module.Id;
            var button = new Button(() => OnModuleButtonClicked(moduleId));
            button.name = $"hub-rail-{moduleId.ToLowerInvariant()}";
            button.text = module.Label;
            button.tooltip = string.IsNullOrWhiteSpace(module.Tooltip) ? module.Id : module.Tooltip;
            button.AddToClassList("hub-rail__button");
            button.AddToClassList("hub-rail__module-button");
            container.Add(button);
            _moduleButtons[moduleId] = button;
        }

        /// <summary>
        /// 模块按钮点击处理。
        /// </summary>
        /// <param name="moduleId">被点击的模块 ID</param>
        private void OnModuleButtonClicked(string moduleId)
        {
            if (_activeModuleId == moduleId)
            {
                // 点击已激活的模块 → 不切换，但可以通知外部（用于折叠 Context Sidebar）
                return;
            }

            _activeModuleId = moduleId;
            EditorPrefs.SetString(ActiveModuleKey, moduleId);
            UpdateActiveState();
            OnModuleChanged?.Invoke(moduleId);
        }

        /// <summary>
        /// 更新所有按钮的激活状态样式。
        /// </summary>
        private void UpdateActiveState()
        {
            foreach (var kvp in _moduleButtons)
            {
                if (kvp.Key == _activeModuleId)
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
        /// <param name="moduleId">目标模块 ID</param>
        public void SetActiveModule(string moduleId)
        {
            if (string.IsNullOrWhiteSpace(moduleId) || _activeModuleId == moduleId)
                return;

            if (_modules.All(module => module.Id != moduleId))
                return;

            _activeModuleId = moduleId;
            EditorPrefs.SetString(ActiveModuleKey, moduleId);
            UpdateActiveState();
            OnModuleChanged?.Invoke(moduleId);
        }
    }
}
