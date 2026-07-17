using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Components
{
    /// <summary>
    /// 帮助浮窗 — 马里奥问号方块风格。
    /// <para>
    /// 在 HubRail 底部显示一个黄色问号方块按钮，hover 时弹出浮窗面板，
    /// 展示插件快捷键、使用技巧和会话级信任说明。光标离开按钮和面板后自动隐藏。
    /// </para>
    /// <para>
    /// 一致性设计（对抗型审查后）：
    /// 面板以 <c>position:absolute</c> 添加到 <b>加载了 ChatWindow.uss 的
    /// rootVisualElement</b>（不是最顶层 unity-panel-container，也不是 52px 的
    /// HubRail）。这一个决策同时保证了四件事互相一致：
    /// (1) USS 样式作用域命中；(2) CJK 字体继承；(3) 不被窄栏裁剪；
    /// (4) worldBound 定位与挂载坐标系对齐。
    /// 此外所有关键视觉属性均有 <b>inline 兜底</b>，即使 USS 未命中也能正常显示。
    /// </para>
    /// </summary>
    public class HelpBubble : VisualElement
    {
        // ────────────────── 字段 ──────────────────

        private readonly Button _questionButton;
        private VisualElement _panel;
        private VisualElement _mountRoot;   // 面板挂载的 root（USS 作用域内）
        private bool _isShown;
        private bool _mouseOnButton;
        private bool _mouseOnPanel;

        private const int ShowDelayMs = 200;
        private const int HideDelayMs = 200;

        private const float PanelWidth = 420f;
        private const float DesiredHeight = 540f;
        private const float MinVisibleHeight = 200f;
        private const float Gap = 6f;

        private static Font _cachedFont;

        // ── 配色（马里奥问号方块 + 面板深色） ──
        private static readonly Color PanelBg = Hex(0x2e2e2e);
        private static readonly Color PanelBorder = Hex(0x555555);
        private static readonly Color TitleColor = Hex(0xfbc02d);
        private static readonly Color SectionColor = Hex(0x999999);
        private static readonly Color KeyBg = Hex(0x3a3520);
        private static readonly Color KeyText = Hex(0xfbc02d);
        private static readonly Color DescColor = Hex(0xd8d8d8);
        private static readonly Color TipColor = Hex(0xcccccc);
        private static readonly Color DividerColor = Hex(0x444444);

        // ────────────────── 构造 ──────────────────

        public HelpBubble()
        {
            name = "help-bubble";
            AddToClassList("help-bubble");

            _questionButton = new Button
            {
                name = "help-bubble-button",
                text = "?"
            };
            _questionButton.AddToClassList("help-bubble__button");

            _questionButton.RegisterCallback<PointerEnterEvent>(OnButtonPointerEnter);
            _questionButton.RegisterCallback<PointerLeaveEvent>(OnButtonPointerLeave);

            Add(_questionButton);

            RegisterCallback<DetachFromPanelEvent>(OnDetachedFromPanel);

            BuildPanel();
        }

        // ────────────────── 面板构建 ──────────────────

        private void BuildPanel()
        {
            _panel = new VisualElement { name = "help-bubble-panel" };
            _panel.AddToClassList("help-bubble__panel");

            // 布局 / 定位（inline — 与挂载点无关，永远生效）
            _panel.style.position = Position.Absolute;
            _panel.style.display = DisplayStyle.None;
            _panel.style.width = PanelWidth;

            // 视觉兜底（inline）— 即使 USS 作用域未命中也能正确显示
            _panel.style.backgroundColor = PanelBg;
            SetBorder(_panel, 1, PanelBorder, 6);
            _panel.style.overflow = Overflow.Hidden;

            _panel.RegisterCallback<PointerEnterEvent>(OnPanelPointerEnter);
            _panel.RegisterCallback<PointerLeaveEvent>(OnPanelPointerLeave);

            // ScrollView — 内容超出面板高度时纵向滚动
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            scroll.style.flexShrink = 1;

            var content = new VisualElement { name = "help-bubble-content" };
            content.style.flexShrink = 0;
            content.style.paddingTop = 14;
            content.style.paddingBottom = 14;
            content.style.paddingLeft = 16;
            content.style.paddingRight = 16;

            content.Add(MakeTitle("AgentCore 使用技巧"));

            content.Add(MakeSectionHeader("快捷键"));
            content.Add(MakeShortcutItem("Ctrl+Shift+Q", "打开 AgentCore 窗口"));
            content.Add(MakeShortcutItem("Ctrl+Shift+X",
                "全局上下文注入（选中任意 Unity 物体后按此键，自动采集上下文注入聊天）"));
            content.Add(MakeShortcutItem("Ctrl+Shift+E", "导出当前会话"));
            // 输入框内快捷键（此前帮助面板遗漏，用户误以为只有上面 3 个全局键）
            content.Add(MakeShortcutItem("Enter", "发送消息"));
            content.Add(MakeShortcutItem("Shift+Enter", "输入框内换行"));
            content.Add(MakeShortcutItem("Ctrl+N", "新建会话"));
            content.Add(MakeShortcutItem("Escape", "取消当前进行中的操作"));

            content.Add(MakeSectionHeader("使用技巧"));
            content.Add(MakeTip("选中 Hierarchy / Project / Console 中的物体后按 Ctrl+Shift+X，Agent 会自动理解上下文"));
            content.Add(MakeTip("对任何不认识的 Unity 面板元素按 Ctrl+Shift+X，会自动采集窗口信息"));
            content.Add(MakeTip("多轮对话中 Agent 会自动压缩旧消息，无需手动清理"));

            content.Add(MakeSectionHeader("会话级信任"));
            content.Add(MakeTip("Trust Low/Med — 本会话 ReadOnly/Low/Medium 风险工具直通，High/破坏性操作仍弹窗"));
            content.Add(MakeTip("YOLO (All) — 本会话所有工具直通，含删除/推送/编译等破坏性操作，慎用"));

            scroll.Add(content);
            _panel.Add(scroll);
        }

        // ── 内容元素工厂（USS class + inline 兜底 双保险） ──

        private Label MakeTitle(string text)
        {
            var label = new Label(text);
            label.AddToClassList("help-bubble__panel-title");
            label.style.flexShrink = 0;
            label.style.fontSize = 17;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.color = TitleColor;
            label.style.marginBottom = 14;
            label.style.paddingBottom = 10;
            label.style.borderBottomWidth = 1;
            label.style.borderBottomColor = DividerColor;
            label.style.whiteSpace = WhiteSpace.Normal;
            return label;
        }

        private Label MakeSectionHeader(string text)
        {
            var label = new Label(text);
            label.AddToClassList("help-bubble__section-header");
            label.style.flexShrink = 0;
            label.style.fontSize = 13;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.color = SectionColor;
            label.style.marginTop = 14;
            label.style.marginBottom = 8;
            label.style.whiteSpace = WhiteSpace.Normal;
            return label;
        }

        private VisualElement MakeShortcutItem(string keys, string desc)
        {
            var row = new VisualElement();
            row.AddToClassList("help-bubble__shortcut-row");
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexShrink = 0;
            row.style.marginBottom = 8;
            row.style.alignItems = Align.FlexStart;

            var key = new Label(keys);
            key.AddToClassList("help-bubble__shortcut-key");
            key.style.flexShrink = 0;
            key.style.minWidth = 104;
            key.style.fontSize = 12;
            key.style.unityFontStyleAndWeight = FontStyle.Bold;
            key.style.color = KeyText;
            key.style.backgroundColor = KeyBg;
            SetBorder(key, 1, Hex(0x5a5020), 4);
            key.style.paddingTop = 3;
            key.style.paddingBottom = 3;
            key.style.paddingLeft = 8;
            key.style.paddingRight = 8;
            key.style.marginRight = 10;
            key.style.unityTextAlign = TextAnchor.MiddleCenter;
            key.style.whiteSpace = WhiteSpace.Normal;
            row.Add(key);

            var d = new Label(desc);
            d.AddToClassList("help-bubble__shortcut-desc");
            d.style.flexShrink = 1;
            d.style.flexGrow = 1;
            d.style.fontSize = 13;
            d.style.color = DescColor;
            d.style.whiteSpace = WhiteSpace.Normal;
            d.style.overflow = Overflow.Hidden;
            row.Add(d);

            return row;
        }

        private Label MakeTip(string text)
        {
            var label = new Label(text);
            label.AddToClassList("help-bubble__tip-item");
            label.style.flexShrink = 0;
            label.style.fontSize = 13;
            label.style.color = TipColor;
            label.style.marginBottom = 8;
            label.style.whiteSpace = WhiteSpace.Normal;
            return label;
        }

        // ────────────────── 事件处理 ──────────────────

        private void OnButtonPointerEnter(PointerEnterEvent evt)
        {
            _mouseOnButton = true;
            schedule.Execute(() =>
            {
                if (_mouseOnButton && !_isShown)
                    ShowPanel();
            }).StartingIn(ShowDelayMs);
        }

        private void OnButtonPointerLeave(PointerLeaveEvent evt)
        {
            _mouseOnButton = false;
            ScheduleHide();
        }

        private void OnPanelPointerEnter(PointerEnterEvent evt)
        {
            _mouseOnPanel = true;
        }

        private void OnPanelPointerLeave(PointerLeaveEvent evt)
        {
            _mouseOnPanel = false;
            ScheduleHide();
        }

        /// <summary>
        /// 延迟隐藏 — 覆盖按钮↔面板之间的间隙，防止移动光标时面板闪烁消失。
        /// </summary>
        private void ScheduleHide()
        {
            schedule.Execute(() =>
            {
                if (!_mouseOnButton && !_mouseOnPanel)
                    HidePanel();
            }).StartingIn(HideDelayMs);
        }

        // ────────────────── 显示 / 隐藏 ──────────────────

        private void ShowPanel()
        {
            if (_isShown) return;

            var root = ResolveMountRoot();
            if (root == null) return;

            _mountRoot = root;
            if (_panel.parent != root)
                root.Add(_panel);

            // 字体：优先继承挂载 root 的 unityFont；缺失时用自己的 CJK 候选链兜底
            EnsureCJKFont(_panel);

            PositionPanel(root);

            _panel.style.display = DisplayStyle.Flex;
            _panel.BringToFront();
            _isShown = true;
        }

        private void HidePanel()
        {
            if (!_isShown) return;
            _panel.style.display = DisplayStyle.None;
            _isShown = false;
        }

        // ────────────────── 定位 ──────────────────

        /// <summary>
        /// 用 worldBound 计算按钮在挂载 root 坐标系中的位置，
        /// 面板定位在按钮右侧、优先向上展开。worldBound 与挂载 root 同坐标系，
        /// 无需任何坐标转换 API。
        /// </summary>
        private void PositionPanel(VisualElement root)
        {
            var btnWorld = _questionButton.worldBound;
            var rootWorld = root.worldBound;

            float panelX, panelTop, panelHeight;

            if (btnWorld.width > 0 && rootWorld.width > 0)
            {
                float btnX = btnWorld.x - rootWorld.x;
                float btnY = btnWorld.y - rootWorld.y;
                float btnW = btnWorld.width;
                float btnH = btnWorld.height;
                float rootH = rootWorld.height;
                float rootW = rootWorld.width;

                // X: 按钮右侧 + gap
                panelX = btnX + btnW + Gap;

                // Y: 优先向上展开（面板底边对齐按钮底边）
                float panelBottom = btnY + btnH;
                panelTop = panelBottom - DesiredHeight;
                if (panelTop < 8f) panelTop = 8f;
                panelHeight = panelBottom - panelTop;

                // 向上空间不足 → 向下展开
                if (panelHeight < MinVisibleHeight)
                {
                    panelTop = btnY;
                    panelHeight = Mathf.Min(DesiredHeight, rootH - panelTop - 8f);
                }

                // 高度不能超出窗口底部
                if (panelTop + panelHeight > rootH - 8f)
                    panelHeight = rootH - panelTop - 8f;

                // 右侧超出窗口 → 翻到按钮左侧
                if (panelX + PanelWidth > rootW - 8f)
                    panelX = Mathf.Max(8f, btnX - PanelWidth - Gap);
            }
            else
            {
                panelX = 60f;
                panelTop = 20f;
                panelHeight = DesiredHeight;
            }

            _panel.style.left = panelX;
            _panel.style.top = panelTop;
            _panel.style.height = Mathf.Max(MinVisibleHeight, panelHeight);
        }

        /// <summary>
        /// 找到加载了 ChatWindow.uss 的 rootVisualElement —— 即挂了 styleSheets
        /// 的那一层。策略：从按钮向上遍历，返回最后一个持有 styleSheets 的元素；
        /// 若找不到（防御性），退回 panel.visualTree。
        /// </summary>
        private VisualElement ResolveMountRoot()
        {
            VisualElement styledRoot = null;
            VisualElement current = _questionButton;
            while (current != null)
            {
                if (current.styleSheets != null && current.styleSheets.count > 0)
                    styledRoot = current;
                if (current.parent == null)
                    break;
                current = current.parent;
            }
            // styledRoot = 最靠上、仍持有 styleSheets 的层（USS 作用域根）
            // current   = 绝对顶层（panel.visualTree）
            return styledRoot ?? current;
        }

        // ────────────────── CJK 字体 ──────────────────

        /// <summary>
        /// 保证面板用 CJK 字体渲染。UI Toolkit 子元素会继承 root 的 unityFont，
        /// 但面板是 absolute 挂到 root 下、字体继承可能因作用域时序而缺失，
        /// 故显式设一次（与 ChatWindow 同款平台候选链），杜绝合成 bold 导致的
        /// 中文粗细不均。
        /// </summary>
        private static void EnsureCJKFont(VisualElement target)
        {
            if (_cachedFont == null)
            {
                string[] candidates;
#if UNITY_EDITOR_WIN
                candidates = new[] { "Microsoft YaHei", "SimHei", "Arial" };
#elif UNITY_EDITOR_OSX
                candidates = new[] { "PingFang SC", "Hiragino Sans GB", "Arial" };
#else
                candidates = new[] { "Noto Sans CJK SC", "WenQuanYi Micro Hei", "Arial" };
#endif
                foreach (var fontName in candidates)
                {
                    try
                    {
                        _cachedFont = Font.CreateDynamicFontFromOSFont(fontName, 14);
                    }
                    catch
                    {
                        _cachedFont = null;
                    }
                    if (_cachedFont != null) break;
                }
            }

            if (_cachedFont != null)
                target.style.unityFont = _cachedFont;
        }

        // ────────────────── 生命周期 ──────────────────

        private void OnDetachedFromPanel(DetachFromPanelEvent evt)
        {
            HidePanel();
            _panel?.RemoveFromHierarchy();
            _mountRoot = null;
        }

        // ────────────────── 工具方法 ──────────────────

        private static Color Hex(int rgb)
        {
            return new Color(
                ((rgb >> 16) & 0xFF) / 255f,
                ((rgb >> 8) & 0xFF) / 255f,
                (rgb & 0xFF) / 255f,
                1f);
        }

        private static void SetBorder(VisualElement e, float width, Color color, float radius)
        {
            e.style.borderTopWidth = width;
            e.style.borderBottomWidth = width;
            e.style.borderLeftWidth = width;
            e.style.borderRightWidth = width;
            e.style.borderTopColor = color;
            e.style.borderBottomColor = color;
            e.style.borderLeftColor = color;
            e.style.borderRightColor = color;
            e.style.borderTopLeftRadius = radius;
            e.style.borderTopRightRadius = radius;
            e.style.borderBottomLeftRadius = radius;
            e.style.borderBottomRightRadius = radius;
        }
    }
}
