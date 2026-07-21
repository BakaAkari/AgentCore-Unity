using System;
using System.Collections.Generic;
using AgentCore.Editor.Config;
using AgentCore.Editor.Core;
using AgentCore.Editor.Extensions;
using AgentCore.Editor.LLM;
using AgentCore.Editor.Session;
using AgentCore.Editor.UI.Components;
using AgentCore.Editor.Tools.Safety;
using AgentCore.Editor.Utils;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI
{
    /// <summary>
    /// AgentCore 主窗口（Hub 架构）。
    /// <para>
    /// 提供 Chat / Knowledge / Memory 三大模块的统一 Editor 窗口界面，基于 UI Toolkit 实现。
    /// 左侧 Hub Rail 导航栏切换模块，中间 Context Sidebar 显示模块上下文，右侧 Main Content 显示模块内容。
    /// </para>
    /// <para>
    /// 通过菜单 Window -> AgentCore（快捷键 Ctrl+Shift+Q）打开。
    /// </para>
    /// </summary>
    public partial class ChatWindow : EditorWindow
    {
        #region 常量

        /// <summary>ChatWindow UXML 模板在包内的路径</summary>
        private const string UxmlPath = "Packages/com.agentcore.unity/Editor/UI/ChatWindow.uxml";

        /// <summary>ChatWindow USS 样式在包内的路径</summary>
        private const string UssPath = "Packages/com.agentcore.unity/Editor/UI/ChatWindow.uss";

        /// <summary>窗口最小尺寸
        /// <para>
        /// 宽度下限 420（原 360）：过窄会让消息气泡内文本频繁换行，
        /// 触发 layout 高度抖动。虽然 MessageBubble.SyncBubbleContentHeight
        /// 已加防重入 + 8px 容差根治了反馈循环，此处再抬高下限作为纵深防御，
        /// 保证正常内容不会逼近换行抖动的临界宽度。
        /// </para>
        /// </summary>
        private static readonly Vector2 MinWindowSize = new(420, 480);

        /// <summary>EditorPrefs key：侧边栏展开状态</summary>
        private const string SidebarExpandedKey = "AgentCore_SidebarExpanded";

        /// <summary>会话标题最大显示字符数</summary>
        private const int MaxTitleDisplayLength = 20;

        #endregion

        #region 静态缓存（跨 CreateGUI 调用保持）

        /// <summary>P0: 缓存动态字体，避免每次 CreateGUI 都重新创建（resize 时触发字形光栅化）</summary>
        private static Font _cachedFont;

        /// <summary>P0: 字体是否已初始化</summary>
        private static bool _fontInitialized;

        #endregion

        #region 核心字段

        /// <summary>Agent Loop 实例，管理对话逻辑</summary>
        private AgentLoop _agentLoop;

        /// <summary>最后一条用户消息（用于错误重试）</summary>
        private string _lastUserMessage;

        /// <summary>消息气泡字典，按 MessageId 索引</summary>
        private readonly Dictionary<string, MessageBubble> _messageBubbles = new();

        /// <summary>assistant turn 视图字典，按 MessageId 索引</summary>
        private readonly Dictionary<string, AssistantTurnView> _assistantTurnViews = new();

        /// <summary>当前正在流式输出的 assistant turn ID。</summary>
        private string _currentAssistantTurnId;

        /// <summary>活跃的工具调用卡片字典，按 ToolCallId 索引（支持同名工具多次调用）</summary>
        private readonly Dictionary<string, ToolCallCard> _activeToolCards = new();

        /// <summary>工具调用计数器，用于在 ToolCallId 缺失时生成唯一 key</summary>
        private int _toolCallCounter;

        /// <summary>当前工具调用分组容器（一次 Agent 交互中的所有工具调用共享一个分组）</summary>
        private ToolCallGroup _currentToolCallGroup;

        /// <summary>
        /// 用户点击发送后、真正 assistant turn 出现前的 pending 占位气泡。
        /// 由 <see cref="OnSendClicked"/> 创建；真正的 assistant turn 出现或错误时被 <see cref="DismissPendingIndicator"/> 移除。
        /// </summary>
        private Components.PendingIndicator _pendingIndicator;

        #endregion

        #region UI 元素引用

        /// <summary>消息滚动视图</summary>
        private ScrollView _messageScrollView;

        /// <summary>消息列表管理器（负责 DOM 池化，解决长上下文卡顿）</summary>
        private MessageListManager _messageListManager;

        /// <summary>消息容器（ScrollView 内部）</summary>
        private VisualElement _messageContainer;

        /// <summary>"跳到最新"浮动按钮（用户上翻时显示）</summary>
        private Button _scrollToBottomButton;

        /// <summary>用户是否手动上翻（true 时禁用自动追底）</summary>
        private bool _userScrolledUp;

        /// <summary>输入框滚动包装</summary>
        private ScrollView _inputScrollView;

        /// <summary>文本输入框</summary>
        private TextField _inputField;

        /// <summary>发送按钮</summary>
        private Button _sendButton;

        /// <summary>取消按钮</summary>
        private Button _cancelButton;

        /// <summary>Agent 状态行（消息流底部，文件变更面板上方）</summary>
        private AgentStatusLine _agentStatusLine;

        /// <summary>工具栏扩展状态元素。</summary>
        private readonly List<VisualElement> _toolbarStatusElements = new List<VisualElement>();

        /// <summary>工具确认面板。</summary>
        private VisualElement _toolConfirmationPanel;

        /// <summary>待处理工具确认请求队列。</summary>
        private readonly Queue<PendingToolConfirmation> _pendingToolConfirmations = new Queue<PendingToolConfirmation>();

        /// <summary>当前展示中的工具确认请求。</summary>
        private PendingToolConfirmation _activeToolConfirmation;

        /// <summary>文件变更汇总面板</summary>
        private FileChangeSummaryPanel _fileChangeSummaryPanel;

        /// <summary>上下文使用情况面板</summary>
        private ContextUsagePanel _contextUsagePanel;

        #endregion

        #region Hub 导航与面板引用

        /// <summary>Hub Rail 导航组件</summary>
        private HubRail _hubRail;

        /// <summary>Context Sidebar 容器</summary>
        private VisualElement _contextSidebar;

        /// <summary>Chat 面板</summary>
        private VisualElement _chatPanel;

        /// <summary>动态扩展面板宿主容器</summary>
        private VisualElement _extensionPanelHost;

        /// <summary>Hub 模块面板字典</summary>
        private readonly Dictionary<string, VisualElement> _hubPanels = new Dictionary<string, VisualElement>();

        /// <summary>Hub 扩展贡献字典</summary>
        private readonly Dictionary<string, IAgentCorePanelContribution> _hubPanelContributions = new Dictionary<string, IAgentCorePanelContribution>();

        /// <summary>新建会话按钮</summary>
        private Button _newSessionButton;

        /// <summary>会话列表滚动视图</summary>
        private ScrollView _sessionListScrollView;

        /// <summary>会话列表容器</summary>
        private VisualElement _sessionListContainer;

        /// <summary>侧边栏是否展开</summary>
        private bool _sidebarExpanded;

        /// <summary>当前正在重命名的会话项（用于内联编辑）</summary>
        private string _renamingSessionId;

        #endregion

        #region 菜单入口

        /// <summary>
        /// 通过 Unity 菜单打开 AgentCore 主窗口。
        /// 快捷键：Ctrl+Shift+Q (Windows) / Cmd+Shift+Q (macOS)。
        /// </summary>
        [MenuItem("Window/AgentCore %#q")]
        public static void ShowWindow()
        {
            var window = GetWindow<ChatWindow>();
            window.titleContent = new GUIContent("AgentCore", EditorGUIUtility.IconContent("d_console.infoicon.sml").image);
            window.minSize = MinWindowSize;
            window.Show();
        }

        #endregion

        #region 生命周期

        /// <summary>
        /// 创建窗口 GUI。
        /// 加载 UXML/USS 资源，绑定 UI 元素引用和事件处理器，初始化 AgentLoop。
        /// </summary>
        private void CreateGUI()
        {
            // 1. 加载 UXML 模板
            var visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            if (visualTree != null)
            {
                visualTree.CloneTree(rootVisualElement);
            }
            else
            {
                AgentCoreLog.Error($"[AgentCore] ChatWindow UXML not found at: {UxmlPath}");
                rootVisualElement.Add(new Label("Error: ChatWindow.uxml not found."));
                return;
            }

            // 2. 加载 USS 样式
            var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
            if (styleSheet != null)
            {
                rootVisualElement.styleSheets.Add(styleSheet);
            }

            // 2.5 设置系统字体（跨平台回退链）
            // P0: 缓存动态字体，避免每次 CreateGUI 都重新创建（resize 时触发字形光栅化）
            if (!_fontInitialized)
            {
                _fontInitialized = true;
#if UNITY_EDITOR_WIN
                string[] fontCandidates = { "Microsoft YaHei", "SimHei", "Arial" };
#elif UNITY_EDITOR_OSX
                string[] fontCandidates = { "PingFang SC", "Hiragino Sans GB", "Arial" };
#else
                string[] fontCandidates = { "Noto Sans CJK SC", "WenQuanYi Micro Hei", "Arial" };
#endif
                foreach (var fontName in fontCandidates)
                {
                    _cachedFont = Font.CreateDynamicFontFromOSFont(fontName, 14);
                    if (_cachedFont != null) break;
                }
            }
            if (_cachedFont != null)
            {
                rootVisualElement.style.unityFont = _cachedFont;
                rootVisualElement.style.unityFontStyleAndWeight = FontStyle.Bold;
            }

            // 3. 查询 UI 元素引用
            _messageScrollView = rootVisualElement.Q<ScrollView>("message-scroll-view");
            _messageContainer = rootVisualElement.Q<VisualElement>("message-container");

            // 3.1 初始化消息列表管理器（DOM 池化，解决长上下文卡顿）
            if (_messageContainer != null)
            {
                _messageListManager = new MessageListManager(_messageContainer);
                // 连接 ScrollView，启用滚动到底部时自动折叠旧消息
                if (_messageScrollView != null)
                    _messageListManager.AttachScrollView(_messageScrollView);
            }

            _inputScrollView = rootVisualElement.Q<ScrollView>("input-scroll-view");
            _inputField = rootVisualElement.Q<TextField>("input-field");
            _sendButton = rootVisualElement.Q<Button>("send-button");
            _cancelButton = rootVisualElement.Q<Button>("cancel-button");
            _scrollToBottomButton = rootVisualElement.Q<Button>("scroll-to-bottom-button");

            // 3.5 查询 Hub 导航与面板 UI 元素引用
            _contextSidebar = rootVisualElement.Q<VisualElement>("context-sidebar");
            _chatPanel = rootVisualElement.Q<VisualElement>("chat-panel");
            _extensionPanelHost = rootVisualElement.Q<VisualElement>("extension-panel-host");
            _newSessionButton = rootVisualElement.Q<Button>("new-session-button");
            _sessionListScrollView = rootVisualElement.Q<ScrollView>("session-list-scroll");
            _sessionListContainer = rootVisualElement.Q<VisualElement>("session-list-container");

            // 3.6 初始化 Hub 动态扩展面板
            InitializeHubPanels();
            MountToolbarStatusContributions();

            // 3.7 创建 Hub Rail 并插入到 main-body 首位
            _hubRail = new HubRail(CreateHubModuleDefinitions(), ChatModuleId);
            var mainBody = rootVisualElement.Q<VisualElement>("main-body");
            mainBody?.Insert(0, _hubRail);

            // 3.8 订阅 Hub 模块切换事件
            _hubRail.OnModuleChanged += OnHubModuleChanged;

            // 3.9 订阅设置变更事件（用于动态重建 Hub 面板）+ 导航角标事件
            SubscribeHubSettingsChanged();

            // 3.10 同步导航角标状态：模块驱动器（如 VCS）可能在窗口打开前就已推送状态，
            //      先订阅（3.9）再拉取快照，避免遗漏早期事件导致按钮外观不同步。
            SyncHubNavBadges();

            // 4. 绑定按钮事件
            _sendButton?.RegisterCallback<ClickEvent>(_ => OnSendClicked());
            _cancelButton?.RegisterCallback<ClickEvent>(_ => OnCancelClicked());
            _scrollToBottomButton?.RegisterCallback<ClickEvent>(_ => OnScrollToBottomClicked());

            // 4.5 绑定会话侧边栏按钮事件
            _newSessionButton?.RegisterCallback<ClickEvent>(_ => OnNewSessionClicked());

            // 4.6 消息滚动区域检测用户手动上翻（禁用自动追底）
            if (_messageScrollView != null)
            {
                _messageScrollView.verticalScroller.valueChanged += OnMessageScrollValueChanged;
                _messageScrollView.RegisterCallback<WheelEvent>(_ => CheckUserScrolled(force: true), TrickleDown.TrickleDown);
            }

            // 5. 绑定输入框键盘事件（Enter 发送，Ctrl+Enter 换行，Escape 取消）
            _inputField?.RegisterCallback<KeyDownEvent>(OnInputFieldKeyDown);

            // 5.5 绑定窗口级键盘事件（输入框未聚焦时也能响应快捷键）
            rootVisualElement.RegisterCallback<KeyDownEvent>(OnWindowKeyDown);

            // 6. 初始状态：取消按钮隐藏
            if (_cancelButton != null)
            {
                _cancelButton.style.display = DisplayStyle.None;
            }

            // 6.5 初始化 Hub 模块面板可见性
            _sidebarExpanded = EditorPrefs.GetBool(SidebarExpandedKey, true);
            SwitchToModule(_hubRail.ActiveModuleId);

            // 6.7 Phase 4.5: 创建文件变更汇总面板并插入到 input-area 之前
            // 先创建 AgentStatusLine，插入到文件变更面板之前（即消息流底部）
            _agentStatusLine = new AgentStatusLine();
            _fileChangeSummaryPanel = new FileChangeSummaryPanel();
            var chatArea = rootVisualElement.Q<VisualElement>("chat-area");
            var inputArea = rootVisualElement.Q<VisualElement>("input-area");
            if (chatArea != null && inputArea != null)
            {
                var inputIndex = chatArea.IndexOf(inputArea);
                if (inputIndex >= 0)
                {
                    // 插入顺序：statusLine → fileChangePanel → inputArea
                    chatArea.Insert(inputIndex, _fileChangeSummaryPanel);
                    chatArea.Insert(inputIndex, _agentStatusLine);
                }

                // 6.75 创建内嵌工具确认面板，避免系统弹窗依赖 Unity 前台窗口。
                // v1.6.5: 从 SessionState 恢复 YOLO 会话信任状态 (跨 Domain Reload)
                // 必须在此处(CreateGUI 生命周期内)调用,不能在字段初始化器/构造器中调用
                LoadSessionTrustScopesFromState();
                InitializeToolConfirmationPanel(chatArea, inputArea);
                InitializeAskUserPanel(chatArea, inputArea);

                // 6.8 Phase 6.0.4: 创建上下文使用情况面板并插入到 input-area 之前（在文件变更面板之后）
                _contextUsagePanel = new ContextUsagePanel();
                // 加载 ContextUsagePanel 的样式表
                var contextPanelUss = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                    "Packages/com.agentcore.unity/Editor/UI/Components/ContextUsagePanel.uss");
                if (contextPanelUss != null)
                {
                    _contextUsagePanel.styleSheets.Add(contextPanelUss);
                }
                inputIndex = chatArea.IndexOf(inputArea);
                if (inputIndex >= 0)
                {
                    chatArea.Insert(inputIndex, _contextUsagePanel);
                }
                else
                {
                    chatArea.Add(_fileChangeSummaryPanel);
                }
            }

            // 7. 创建并初始化 AgentLoop
            InitializeAgentLoop();

            // 8. Phase 3: 尝试恢复上一次的会话
            TryRestoreSession();

            // 8.4 ask_user：若 domain reload 前有挂起的提问，恢复挂起状态并重建面板
            TryRestorePendingAskUser();

            // 8.5 刷新会话列表
            RefreshSessionList();
        }

        /// <summary>
        /// 窗口销毁时清理资源。
        /// 取消订阅事件，取消进行中的操作，保存当前会话。
        /// </summary>
        private void OnDestroy()
        {
            // Phase 3: 窗口关闭时强制保存当前会话
            if (_agentLoop != null)
            {
                try
                {
                    SessionManager.Instance.ForceSave(
                        new List<ChatMessage>(_agentLoop.Messages),
                        new List<ConversationTurn>(_agentLoop.ConversationTurns));
                }
                catch (Exception ex)
                {
                    AgentCoreLog.Warning($"[AgentCore] Failed to save session on window close: {ex.Message}");
                }

                _agentLoop.OnAgentEvent -= HandleAgentEvent;
                _agentLoop.OnUserQueryRaised -= HandleUserQueryRaised;
                _agentLoop.Dispose(); // P1-1 fix: 调用 Dispose() 释放 ConsoleErrorCapture、CompilationWatcher 等资源
                _agentLoop = null;
            }

            // 取消订阅设置变更事件
            UnsubscribeHubSettingsChanged();

            // 取消订阅 Hub Rail 事件
            if (_hubRail != null)
            {
                _hubRail.OnModuleChanged -= OnHubModuleChanged;
                _hubRail = null;
            }

            // 断开 ScrollView 监听，防止内存泄漏
            _messageListManager?.DetachScrollView();
            _messageListManager = null;

            ClearPendingToolConfirmations();
            ClearPendingUserQuery();
            DisposeToolbarStatusContributions();
            DisposeHubPanels();

            _messageBubbles.Clear();
            _activeToolCards.Clear();
            _currentToolCallGroup = null;
        }

        #endregion

        #region AgentLoop 初始化

        /// <summary>
        /// 创建 LLM 客户端和 AgentLoop 实例，订阅事件并初始化。
        /// </summary>
        private void InitializeAgentLoop()
        {
            try
            {
                // 创建 LLM 客户端
                var llmClient = new OpenAICompatibleClient();

                // 创建 AgentLoop
                var confirmationProvider = new DelegatingToolConfirmationProvider(RequestEmbeddedToolConfirmationAsync);
                _agentLoop = new AgentLoop(llmClient, confirmationProvider);
                _agentLoop.OnAgentEvent += HandleAgentEvent;
                _agentLoop.OnUserQueryRaised += HandleUserQueryRaised;

                // 初始化（加载 Bootstrap 上下文）
                _agentLoop.Initialize();

                AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] ChatWindow initialized successfully.");
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"[AgentCore] Failed to initialize ChatWindow: {ex.Message}");
                UpdateStatusLabel("初始化失败", true);
            }
        }

        #endregion
    }
}
