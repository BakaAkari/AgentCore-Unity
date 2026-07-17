using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Components
{
    /// <summary>
    /// 帮助浮窗 — 马里奥问号方块风格。
    /// <para>
    /// 在 HubRail 底部显示一个黄色问号方块按钮，hover 时弹出独立
    /// <see cref="EditorWindow"/>（ShowAsDropDown）展示插件快捷键和使用技巧。
    /// 点击窗口外部自动关闭，不受父容器层级 / overflow 限制。
    /// </para>
    /// </summary>
    public class HelpBubble : VisualElement
    {
        /// <summary>问号方块按钮</summary>
        private readonly Button _questionButton;

        /// <summary>当前打开的帮助窗口实例（null = 未打开）</summary>
        private static HelpBubbleWindow _openWindow;

        /// <summary>hover 显示延迟（ms）</summary>
        private const int ShowDelayMs = 250;

        /// <summary>鼠标是否在按钮上</summary>
        private bool _mouseOnButton;

        /// <summary>
        /// 创建 HelpBubble 实例。
        /// </summary>
        public HelpBubble()
        {
            name = "help-bubble";
            AddToClassList("help-bubble");

            _questionButton = new Button();
            _questionButton.name = "help-bubble-button";
            _questionButton.text = "?";
            _questionButton.AddToClassList("help-bubble__button");
            _questionButton.tooltip = "快捷键 & 使用技巧";

            _questionButton.RegisterCallback<MouseEnterEvent>(OnButtonMouseEnter);
            _questionButton.RegisterCallback<MouseLeaveEvent>(OnButtonMouseLeave);
            _questionButton.RegisterCallback<ClickEvent>(OnButtonClick);

            Add(_questionButton);
        }

        // ────────────────── 事件处理 ──────────────────

        /// <summary>
        /// 鼠标进入按钮 → 延迟打开窗口（hover 模式）。
        /// </summary>
        private void OnButtonMouseEnter(MouseEnterEvent evt)
        {
            _mouseOnButton = true;
            schedule.Execute(() =>
            {
                if (_mouseOnButton && _openWindow == null)
                    OpenWindow();
            }).StartingIn(ShowDelayMs);
        }

        /// <summary>
        /// 鼠标离开按钮 → 不主动关闭（窗口自己处理关闭）。
        /// </summary>
        private void OnButtonMouseLeave(MouseLeaveEvent evt)
        {
            _mouseOnButton = false;
        }

        /// <summary>
        /// 点击按钮 → 立即切换窗口（点击打开 / 再点击关闭）。
        /// </summary>
        private void OnButtonClick(ClickEvent evt)
        {
            if (_openWindow != null)
            {
                _openWindow.Close();
                _openWindow = null;
            }
            else
            {
                OpenWindow();
            }
        }

        /// <summary>
        /// 打开帮助窗口，定位到按钮旁边。
        /// </summary>
        private void OpenWindow()
        {
            // 用按钮的世界坐标矩形作为 activator，ShowAsDropDown 会自动定位到附近
            var btnRect = _questionButton.worldBound;
            _openWindow = ScriptableObject.CreateInstance<HelpBubbleWindow>();
            _openWindow.ShowAsDropDown(btnRect, new Vector2(HelpBubbleWindow.WindowWidth, HelpBubbleWindow.WindowHeight));
        }

        // ────────────────── 帮助窗口 ──────────────────

        /// <summary>
        /// 独立 EditorWindow — ShowAsDropDown 模式，
        /// 点击外部自动关闭，不受父容器限制。
        /// </summary>
        private class HelpBubbleWindow : EditorWindow
        {
            /// <summary>窗口宽度</summary>
            public const float WindowWidth = 420f;

            /// <summary>窗口高度</summary>
            public const float WindowHeight = 520f;

            private void OnEnable()
            {
                minSize = new Vector2(WindowWidth, WindowHeight);
                maxSize = new Vector2(WindowWidth, WindowHeight);
            }

            /// <summary>
            /// 窗口关闭时清除静态引用，避免按钮点击逻辑误判。
            /// </summary>
            private void OnDisable()
            {
                _openWindow = null;
            }

            private void CreateGUI()
            {
                rootVisualElement.style.backgroundColor = new Color(0.18f, 0.18f, 0.18f);

                // 加载 USS 样式（与 ChatWindow 共用同一份）
                const string ussPath = "Packages/com.agentcore.unity/Editor/UI/ChatWindow.uss";
                var uss = AssetDatabase.LoadAssetAtPath<StyleSheet>(ussPath);
                if (uss != null)
                    rootVisualElement.styleSheets.Add(uss);

                // 设置 CJK 字体 — 独立窗口不继承 ChatWindow 的字体，
                // 必须显式设置，否则 Unity 默认字体对中文 bold 是合成模拟的，
                // 会出现同一字笔画多的偏细、笔画少的偏粗的问题。
                SetCJKFont(rootVisualElement);

                BuildContent(rootVisualElement);
            }

            /// <summary>
            /// 设置 CJK 字体（与 ChatWindow 使用相同的跨平台回退链）。
            /// </summary>
            private static void SetCJKFont(VisualElement root)
            {
#if UNITY_EDITOR_WIN
                string[] fontCandidates = { "Microsoft YaHei", "SimHei", "Arial" };
#elif UNITY_EDITOR_OSX
                string[] fontCandidates = { "PingFang SC", "Hiragino Sans GB", "Arial" };
#else
                string[] fontCandidates = { "Noto Sans CJK SC", "WenQuanYi Micro Hei", "Arial" };
#endif
                Font font = null;
                foreach (var fontName in fontCandidates)
                {
                    font = Font.CreateDynamicFontFromOSFont(fontName, 14);
                    if (font != null) break;
                }
                if (font != null)
                {
                    root.style.unityFont = font;
                    root.style.unityFontStyleAndWeight = FontStyle.Bold;
                }
            }

            /// <summary>
            /// 构建窗口内容。
            /// </summary>
            private static void BuildContent(VisualElement root)
            {
                root.style.paddingTop = 14;
                root.style.paddingBottom = 14;
                root.style.paddingLeft = 16;
                root.style.paddingRight = 16;

                // ── 标题 ──
                var title = new Label("AgentCore 使用技巧");
                title.AddToClassList("help-bubble__panel-title");
                root.Add(title);

                // ── 快捷键 ──
                root.Add(MakeSectionHeader("快捷键"));
                root.Add(MakeShortcutItem("Ctrl+Shift+Q", "打开 AgentCore 窗口"));
                root.Add(MakeShortcutItem("Ctrl+Shift+X",
                    "全局上下文注入\n（选中任意 Unity 物体后按此键，自动采集上下文注入聊天）"));
                root.Add(MakeShortcutItem("Ctrl+Shift+E", "导出当前会话"));

                // ── 使用技巧 ──
                root.Add(MakeSectionHeader("使用技巧"));
                root.Add(MakeTipItem("选中 Hierarchy / Project / Console 中的物体后按 Ctrl+Shift+X，Agent 会自动理解上下文"));
                root.Add(MakeTipItem("对任何不认识的 Unity 面板元素按 Ctrl+Shift+X，会自动采集窗口信息"));
                root.Add(MakeTipItem("多轮对话中 Agent 会自动压缩旧消息，无需手动清理"));

                // ── 会话级信任 ──
                root.Add(MakeSectionHeader("会话级信任"));
                root.Add(MakeTipItem("Trust Low/Med — 本会话 ReadOnly/Low/Medium 风险工具直通，High/破坏性操作仍弹窗"));
                root.Add(MakeTipItem("YOLO (All) — 本会话所有工具直通，含删除/推送/编译等破坏性操作，慎用"));
            }

            private static Label MakeSectionHeader(string text)
            {
                var label = new Label(text);
                label.AddToClassList("help-bubble__section-header");
                return label;
            }

            private static VisualElement MakeShortcutItem(string keys, string desc)
            {
                var row = new VisualElement();
                row.AddToClassList("help-bubble__shortcut-row");

                var key = new Label(keys);
                key.AddToClassList("help-bubble__shortcut-key");
                row.Add(key);

                var d = new Label(desc);
                d.AddToClassList("help-bubble__shortcut-desc");
                row.Add(d);

                return row;
            }

            private static Label MakeTipItem(string text)
            {
                var label = new Label(text);
                label.AddToClassList("help-bubble__tip-item");
                return label;
            }
        }
    }
}
