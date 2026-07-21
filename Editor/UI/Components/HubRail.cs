using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
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

            // 帮助气泡（马里奥问号方块，hover 显示技巧面板）
            var helpBubble = new HelpBubble();
            Add(helpBubble);

            // Settings 按钮（底部固定）
            _settingsButton = new Button(OnSettingsClicked);
            _settingsButton.name = "hub-rail-settings";
            _settingsButton.text = "Set";
            _settingsButton.tooltip = "打开 AgentCore 设置";
            _settingsButton.AddToClassList("hub-rail__button");
            _settingsButton.AddToClassList("hub-rail__settings-button");
            // 齿轮图标，失败回退到 "Set" 文字
            TryApplyIcon(_settingsButton, "settings");
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
            // 已知模块用 Unity 内置图标（窄栏 52px 下比文字更清晰）；未知/扩展模块保留文字回退。
            TryApplyIcon(button, moduleId);
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

        /// <summary>
        /// 运行时覆盖某个模块导航按钮的显示文字（例如 VCS 模块按检测到的类型显示 SVN/GIT/P4）。
        /// <para>
        /// 仅对保留文字标签的按钮生效；已应用内置图标的按钮（如 Settings）文字为空，覆盖无视觉效果。
        /// 传入空字符串等价于不覆盖（忽略）。找不到对应按钮时静默返回。
        /// </para>
        /// </summary>
        /// <param name="moduleId">目标模块 ID。</param>
        /// <param name="label">新的显示文字。</param>
        public void SetModuleLabel(string moduleId, string label)
        {
            if (string.IsNullOrWhiteSpace(moduleId) || string.IsNullOrWhiteSpace(label))
                return;

            if (_moduleButtons.TryGetValue(moduleId, out var button) && button != null)
            {
                button.text = label;
            }
        }

        /// <summary>
        /// 设置某个模块导航按钮的告警高亮状态（例如 VCS 远端有更新时按钮变黄）。
        /// <para>
        /// 通过增删 <c>hub-rail__button--alert</c> USS class 实现，具体配色由 ChatWindow.uss 定义。
        /// 找不到对应按钮时静默返回。
        /// </para>
        /// </summary>
        /// <param name="moduleId">目标模块 ID。</param>
        /// <param name="alert">是否高亮告警。</param>
        public void SetModuleAlert(string moduleId, bool alert)
        {
            if (string.IsNullOrWhiteSpace(moduleId))
                return;

            if (_moduleButtons.TryGetValue(moduleId, out var button) && button != null)
            {
                if (alert)
                    button.AddToClassList("hub-rail__button--alert");
                else
                    button.RemoveFromClassList("hub-rail__button--alert");
            }
        }

        /// <summary>
        /// 已知模块 ID → Unity 内置编辑器图标名的映射。
        /// 目前仅 settings 用齿轮图标；其余模块（chat/vcs/knowledge 等）一律返回 null 保留文字标签，
        /// 避免"部分按钮有图标、部分是文字"的不一致视觉。
        /// </summary>
        private static string ResolveBuiltinIconName(string moduleId)
        {
            if (string.IsNullOrWhiteSpace(moduleId)) return null;
            switch (moduleId.ToLowerInvariant())
            {
                case "settings":   return "d_SettingsIcon"; // 齿轮
                default:           return null;             // 其余模块保留文字标签
            }
        }

        /// <summary>
        /// 尝试给按钮应用 Unity 内置图标。成功则清空按钮文字，改用一个固定 16x16 的
        /// 居中 <see cref="Image"/> 子元素显示图标。
        /// <para>
        /// 关键：不能用 <c>button.style.backgroundImage</c> —— 内置编辑器图标是 16x16 小位图，
        /// backgroundImage 默认拉伸铺满 44x36 的按钮，会把小图放大 2~3 倍导致像素化模糊。
        /// 改用固定尺寸的 Image 子元素（ScaleToFit 保持比例、不放大），图标按原始清晰度显示。
        /// </para>
        /// 任何失败（图标名在当前 Unity 版本不存在、取到空贴图、异常）都保留原文字回退，
        /// 保证窄栏在任何 Unity 版本下都不会出现"既无图标又无文字"的空按钮。
        /// </summary>
        private static void TryApplyIcon(Button button, string moduleId)
        {
            var iconName = ResolveBuiltinIconName(moduleId);
            if (string.IsNullOrEmpty(iconName)) return; // 未知模块：保留文字

            try
            {
                var content = EditorGUIUtility.IconContent(iconName);
                var tex = content?.image as Texture2D;
                if (tex == null) return; // 取不到贴图：保留文字

                button.text = string.Empty; // 有图标了，清空文字避免图文重叠

                var icon = new Image
                {
                    image = tex,
                    scaleMode = ScaleMode.ScaleToFit, // 保持比例，不放大到失真
                    pickingMode = PickingMode.Ignore  // 点击穿透到按钮
                };
                // 固定 16x16 原生尺寸并居中，避免被父按钮拉伸
                icon.style.width = 16;
                icon.style.height = 16;
                icon.style.alignSelf = Align.Center;
                button.style.alignItems = Align.Center;
                button.style.justifyContent = Justify.Center;
                button.Add(icon);
            }
            catch
            {
                // 图标 API 在极端版本差异下抛异常 —— 静默回退到文字，不影响功能
            }
        }
    }
}
