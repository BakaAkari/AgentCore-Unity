using System;
using System.Collections.Generic;
using AgentCore.Editor.Config;
using AgentCore.Editor.Core;
using AgentCore.Editor.LLM;
using AgentCore.Editor.Session;
using AgentCore.Editor.UI.Components;
using AgentCore.Editor.Utils;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI
{
    /// <summary>
    /// AgentCore 主聊天窗口。
    /// <para>
    /// 提供与 AI 助手对话的 Editor 窗口界面，基于 UI Toolkit 实现。
    /// 支持流式文本显示、消息历史、取消操作和多会话管理。
    /// </para>
    /// <para>
    /// 通过菜单 Window -> AgentCore -> Chat（快捷键 Ctrl+Shift+A）打开。
    /// </para>
    /// </summary>
    public class ChatWindow : EditorWindow
    {
        #region 常量

        /// <summary>ChatWindow UXML 模板在包内的路径</summary>
        private const string UxmlPath = "Packages/com.agentcore.unity/Editor/UI/ChatWindow.uxml";

        /// <summary>ChatWindow USS 样式在包内的路径</summary>
        private const string UssPath = "Packages/com.agentcore.unity/Editor/UI/ChatWindow.uss";

        /// <summary>窗口最小尺寸</summary>
        private static readonly Vector2 MinWindowSize = new(360, 480);

        /// <summary>EditorPrefs key：侧边栏展开状态</summary>
        private const string SidebarExpandedKey = "AgentCore_SidebarExpanded";

        /// <summary>会话标题最大显示字符数</summary>
        private const int MaxTitleDisplayLength = 20;

        #endregion

        #region 核心字段

        /// <summary>Agent Loop 实例，管理对话逻辑</summary>
        private AgentLoop _agentLoop;

        /// <summary>消息气泡字典，按 MessageId 索引</summary>
        private readonly Dictionary<string, MessageBubble> _messageBubbles = new();

        /// <summary>活跃的工具调用卡片字典，按 ToolCallId 索引（支持同名工具多次调用）</summary>
        private readonly Dictionary<string, ToolCallCard> _activeToolCards = new();

        /// <summary>工具调用计数器，用于在 ToolCallId 缺失时生成唯一 key</summary>
        private int _toolCallCounter;

        /// <summary>当前工具调用分组容器（一次 Agent 交互中的所有工具调用共享一个分组）</summary>
        private ToolCallGroup _currentToolCallGroup;

        #endregion

        #region UI 元素引用

        /// <summary>消息滚动视图</summary>
        private ScrollView _messageScrollView;

        /// <summary>消息容器（ScrollView 内部）</summary>
        private VisualElement _messageContainer;

        /// <summary>文本输入框</summary>
        private TextField _inputField;

        /// <summary>发送按钮</summary>
        private Button _sendButton;

        /// <summary>取消按钮</summary>
        private Button _cancelButton;

        /// <summary>状态标签</summary>
        private Label _statusLabel;

        #endregion

        #region 侧边栏 UI 元素引用

        /// <summary>侧边栏切换按钮</summary>
        private Button _sidebarToggleButton;

        /// <summary>会话侧边栏面板</summary>
        private VisualElement _sessionSidebar;

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
        /// 通过 Unity 菜单打开聊天窗口。
        /// 快捷键：Ctrl+Shift+A (Windows) / Cmd+Shift+A (macOS)。
        /// </summary>
        [MenuItem("Window/AgentCore/Chat %#a")]
        public static void ShowWindow()
        {
            var window = GetWindow<ChatWindow>();
            window.titleContent = new GUIContent("AgentCore Chat", EditorGUIUtility.IconContent("d_console.infoicon.sml").image);
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
                Debug.LogError($"[AgentCore] ChatWindow UXML not found at: {UxmlPath}");
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
            // P2-1 fix: 使用平台感知的字体回退链，避免硬编码 "Microsoft YaHei"
            string[] fontCandidates;
#if UNITY_EDITOR_WIN
            fontCandidates = new[] { "Microsoft YaHei", "SimHei", "Arial" };
#elif UNITY_EDITOR_OSX
            fontCandidates = new[] { "PingFang SC", "Hiragino Sans GB", "Arial" };
#else
            fontCandidates = new[] { "Noto Sans CJK SC", "WenQuanYi Micro Hei", "Arial" };
#endif
            Font font = null;
            foreach (var fontName in fontCandidates)
            {
                font = Font.CreateDynamicFontFromOSFont(fontName, 14);
                if (font != null) break;
            }
            if (font != null)
            {
                rootVisualElement.style.unityFont = font;
                rootVisualElement.style.unityFontStyleAndWeight = FontStyle.Bold;
            }

            // 3. 查询 UI 元素引用
            _messageScrollView = rootVisualElement.Q<ScrollView>("message-scroll-view");
            _messageContainer = rootVisualElement.Q<VisualElement>("message-container");
            _inputField = rootVisualElement.Q<TextField>("input-field");
            _sendButton = rootVisualElement.Q<Button>("send-button");
            _cancelButton = rootVisualElement.Q<Button>("cancel-button");
            _statusLabel = rootVisualElement.Q<Label>("status-label");

            // 3.5 查询侧边栏 UI 元素引用
            _sidebarToggleButton = rootVisualElement.Q<Button>("sidebar-toggle-button");
            _sessionSidebar = rootVisualElement.Q<VisualElement>("session-sidebar");
            _newSessionButton = rootVisualElement.Q<Button>("new-session-button");
            _sessionListScrollView = rootVisualElement.Q<ScrollView>("session-list-scroll");
            _sessionListContainer = rootVisualElement.Q<VisualElement>("session-list-container");

            // 4. 绑定按钮事件
            _sendButton?.RegisterCallback<ClickEvent>(_ => OnSendClicked());
            _cancelButton?.RegisterCallback<ClickEvent>(_ => OnCancelClicked());

            // 4.5 绑定侧边栏按钮事件
            _sidebarToggleButton?.RegisterCallback<ClickEvent>(_ => ToggleSidebar());
            _newSessionButton?.RegisterCallback<ClickEvent>(_ => OnNewSessionClicked());

            // 5. 绑定输入框键盘事件（Enter 发送，Shift+Enter 换行，Escape 取消）
            _inputField?.RegisterCallback<KeyDownEvent>(OnInputFieldKeyDown);

            // 6. 初始状态：取消按钮隐藏
            if (_cancelButton != null)
            {
                _cancelButton.style.display = DisplayStyle.None;
            }

            // 6.5 恢复侧边栏展开状态
            _sidebarExpanded = EditorPrefs.GetBool(SidebarExpandedKey, false);
            UpdateSidebarVisibility();

            // 7. 创建并初始化 AgentLoop
            InitializeAgentLoop();

            // 8. Phase 3: 尝试恢复上一次的会话
            TryRestoreSession();

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
                    Debug.LogWarning($"[AgentCore] Failed to save session on window close: {ex.Message}");
                }

                _agentLoop.OnAgentEvent -= HandleAgentEvent;
                _agentLoop.Dispose(); // P1-1 fix: 调用 Dispose() 释放 ConsoleErrorCapture、CompilationWatcher 等资源
                _agentLoop = null;
            }

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
                _agentLoop = new AgentLoop(llmClient);
                _agentLoop.OnAgentEvent += HandleAgentEvent;

                // 初始化（加载 Bootstrap 上下文）
                _agentLoop.Initialize();

                Debug.Log("[AgentCore] ChatWindow initialized successfully.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AgentCore] Failed to initialize ChatWindow: {ex.Message}");
                UpdateStatusLabel("初始化失败", true);
            }
        }

        #endregion

        #region 用户操作

        /// <summary>
        /// 发送按钮点击处理。
        /// 获取输入文本，清空输入框，添加用户消息气泡，调用 AgentLoop 发送消息。
        /// </summary>
        private void OnSendClicked()
        {
            var text = _inputField?.value?.Trim();
            if (string.IsNullOrEmpty(text)) return;

            if (_agentLoop == null)
            {
                Debug.LogError("[AgentCore] AgentLoop is not initialized.");
                return;
            }

            if (_agentLoop.CurrentState != AgentState.Idle)
            {
                Debug.LogWarning("[AgentCore] Cannot send message while agent is busy.");
                return;
            }

            // 清空输入框
            _inputField.value = "";
            _inputField.Focus();

            // 添加用户消息气泡
            AddUserMessage(text);

            // 异步发送消息
            AsyncHelper.RunAsync(
                () => _agentLoop.SendMessageAsync(text),
                onError: ex => Debug.LogError($"[AgentCore] SendMessage error: {ex.Message}")
            );
        }

        /// <summary>
        /// 取消按钮点击处理。
        /// 取消当前正在进行的 LLM 操作。
        /// </summary>
        private void OnCancelClicked()
        {
            _agentLoop?.Cancel();
            Debug.Log("[AgentCore] User cancelled current operation.");
        }

        /// <summary>
        /// 输入框键盘事件处理。
        /// <list type="bullet">
        ///   <item>Enter — 发送消息</item>
        ///   <item>Shift+Enter — 换行</item>
        ///   <item>Escape — 取消当前操作</item>
        ///   <item>Ctrl+N — 新建会话</item>
        ///   <item>Ctrl+Shift+E — 导出当前会话</item>
        /// </list>
        /// </summary>
        /// <param name="evt">键盘事件</param>
        private void OnInputFieldKeyDown(KeyDownEvent evt)
        {
            switch (evt.keyCode)
            {
                case KeyCode.Return or KeyCode.KeypadEnter when !evt.shiftKey:
                    // Enter（不含 Shift）-> 发送消息
                    evt.PreventDefault();
                    evt.StopPropagation();
                    OnSendClicked();
                    break;

                case KeyCode.Escape:
                    // Escape -> 取消当前操作
                    if (_agentLoop?.CurrentState != AgentState.Idle)
                    {
                        evt.PreventDefault();
                        OnCancelClicked();
                    }
                    break;

                case KeyCode.N when evt.ctrlKey && !evt.shiftKey:
                    // Ctrl+N -> 新建会话
                    evt.PreventDefault();
                    evt.StopPropagation();
                    OnNewSessionClicked();
                    break;

                case KeyCode.E when evt.ctrlKey && evt.shiftKey:
                    // Ctrl+Shift+E -> 导出当前会话
                    evt.PreventDefault();
                    evt.StopPropagation();
                    ShowExportMenu();
                    break;
            }
        }

        #endregion

        #region 事件处理

        /// <summary>
        /// 处理 AgentLoop 派发的事件。
        /// 根据事件类型更新 UI 状态、追加流式文本、显示错误等。
        /// </summary>
        /// <param name="evt">Agent 事件</param>
        private void HandleAgentEvent(AgentEvent evt)
        {
            // 诊断日志：追踪工具调用相关事件
            if (evt.Type == AgentEventType.ToolCallStarted ||
                evt.Type == AgentEventType.ToolCallCompleted ||
                evt.Type == AgentEventType.ToolCallFailed ||
                evt.Type == AgentEventType.LoopRoundStarted ||
                evt.Type == AgentEventType.LoopCompleted)
            {
                Debug.Log($"[AgentCore.UI] HandleAgentEvent 收到事件: {evt.Type}, tool={evt.ToolName ?? "(none)"}, toolCallId={evt.ToolCallId ?? "(none)"}");
            }

            switch (evt.Type)
            {
                case AgentEventType.StateChanged:
                    UpdateUIState(evt.State);
                    break;

                case AgentEventType.StreamToken:
                    AppendStreamToken(evt.Content, evt.MessageId);
                    break;

                case AgentEventType.AssistantMessage:
                    FinalizeAssistantMessage(evt.Content, evt.MessageId);
                    // 助手消息完成后，结束当前工具调用分组（下次工具调用创建新分组）
                    _currentToolCallGroup = null;
                    // 消息完成后精准更新当前会话标题（避免重建整个列表导致排序跳动）
                    UpdateCurrentSessionTitle();
                    break;

                case AgentEventType.Error:
                    ShowError(evt.Content);
                    break;

                // Phase 2: 工具调用事件
                case AgentEventType.ToolCallStarted:
                    HandleToolCallStarted(evt);
                    break;

                case AgentEventType.ToolCallCompleted:
                    HandleToolCallCompleted(evt);
                    break;

                case AgentEventType.ToolCallFailed:
                    HandleToolCallFailed(evt);
                    break;

                case AgentEventType.LoopRoundStarted:
                    HandleLoopRoundStarted(evt);
                    break;

                case AgentEventType.LoopCompleted:
                    // 循环结束，无需特殊 UI 处理
                    break;
            }
        }

        /// <summary>
        /// 根据 Agent 状态更新 UI 元素（状态标签、按钮可用性）。
        /// </summary>
        /// <param name="state">新的 Agent 状态</param>
        private void UpdateUIState(AgentState state)
        {
            switch (state)
            {
                case AgentState.Idle:
                    UpdateStatusLabel("就绪");
                    SetSendEnabled(true);
                    SetCancelVisible(false);
                    break;

                case AgentState.Thinking:
                    UpdateStatusLabel("思考中...");
                    SetSendEnabled(false);
                    SetCancelVisible(true);
                    // 创建助手消息气泡占位（流式模式）
                    EnsureAssistantBubbleExists();
                    break;

                case AgentState.Streaming:
                    UpdateStatusLabel("回复中...");
                    SetSendEnabled(false);
                    SetCancelVisible(true);
                    break;

                case AgentState.ExecutingTool:
                    UpdateStatusLabel("执行工具...");
                    SetSendEnabled(false);
                    SetCancelVisible(true);
                    break;

                case AgentState.Error:
                    UpdateStatusLabel("错误", true);
                    break;
            }
        }

        #endregion

        #region 消息 UI 管理

        /// <summary>
        /// 添加用户消息气泡到消息容器。
        /// </summary>
        /// <param name="content">用户消息内容</param>
        private void AddUserMessage(string content)
        {
            var messageId = Guid.NewGuid().ToString();
            var bubble = new MessageBubble(messageId, "user", content);
            _messageBubbles[messageId] = bubble;
            _messageContainer?.Add(bubble);
            ScrollToBottom();
        }

        /// <summary>
        /// 确保当前存在一个助手消息气泡（流式模式）。
        /// 在 Thinking 状态时预创建，以便后续 StreamToken 事件可以追加文本。
        /// </summary>
        private void EnsureAssistantBubbleExists()
        {
            // 查找最新的助手对话轮次
            if (_agentLoop == null) return;

            var history = _agentLoop.ConversationHistory;
            for (int i = history.Count - 1; i >= 0; i--)
            {
                var turn = history[i];
                if (turn.Role == "assistant" && turn.IsStreaming)
                {
                    if (!_messageBubbles.ContainsKey(turn.Id))
                    {
                        AddAssistantMessageBubble(turn.Id);
                    }
                    return;
                }
            }
        }

        /// <summary>
        /// 创建助手消息气泡（流式模式）并添加到消息容器。
        /// </summary>
        /// <param name="messageId">消息唯一标识</param>
        private void AddAssistantMessageBubble(string messageId)
        {
            var bubble = new MessageBubble(messageId, "assistant", "", isStreaming: true);
            _messageBubbles[messageId] = bubble;
            _messageContainer?.Add(bubble);
            ScrollToBottom();
        }

        /// <summary>
        /// 追加流式 token 到对应的助手消息气泡。
        /// 如果气泡尚不存在，会自动创建。
        /// </summary>
        /// <param name="token">token 文本</param>
        /// <param name="messageId">消息唯一标识</param>
        private void AppendStreamToken(string token, string messageId)
        {
            if (string.IsNullOrEmpty(messageId)) return;

            // 如果气泡不存在，创建一个
            if (!_messageBubbles.TryGetValue(messageId, out var bubble))
            {
                AddAssistantMessageBubble(messageId);
                bubble = _messageBubbles[messageId];
            }

            bubble.AppendStreamToken(token);
            ScrollToBottom();
        }

        /// <summary>
        /// 最终化助手消息内容。
        /// 流式输出完成后调用，设置完整文本并结束流式模式。
        /// </summary>
        /// <param name="fullContent">完整的消息内容</param>
        /// <param name="messageId">消息唯一标识</param>
        private void FinalizeAssistantMessage(string fullContent, string messageId)
        {
            if (string.IsNullOrEmpty(messageId)) return;

            if (_messageBubbles.TryGetValue(messageId, out var bubble))
            {
                bubble.FinalizeContent(fullContent);
            }
        }

        /// <summary>
        /// 显示错误消息气泡。
        /// </summary>
        /// <param name="errorMessage">错误信息</param>
        private void ShowError(string errorMessage)
        {
            var messageId = Guid.NewGuid().ToString();
            var bubble = new MessageBubble(messageId, "error", errorMessage ?? "未知错误");
            _messageBubbles[messageId] = bubble;
            _messageContainer?.Add(bubble);
            ScrollToBottom();
        }

        /// <summary>
        /// 清空所有消息气泡。
        /// </summary>
        private void ClearMessages()
        {
            _messageContainer?.Clear();
            _messageBubbles.Clear();
            _activeToolCards.Clear();
            _currentToolCallGroup = null;
            _toolCallCounter = 0;
        }

        /// <summary>
        /// 在聊天区域添加 Domain Reload 恢复通知卡片。
        /// 显示中断原因、阶段、编译结果和恢复状态等详细信息。
        /// </summary>
        /// <param name="phase">中断阶段</param>
        /// <param name="toolName">中断时正在执行的工具名（可为 null）</param>
        /// <param name="compilationSucceeded">编译是否成功</param>
        /// <param name="compilationErrors">编译错误信息（可为 null）</param>
        /// <returns>通知卡片 VisualElement 引用，用于后续状态更新</returns>
        private VisualElement AddDomainReloadNotification(
            InterruptPhase phase,
            string toolName,
            bool compilationSucceeded,
            string compilationErrors)
        {
            if (_messageContainer == null) return null;

            // === 通知卡片容器 ===
            var card = new VisualElement();
            card.AddToClassList("domain-reload-notification");

            // === 标题行 ===
            var header = new VisualElement();
            header.AddToClassList("domain-reload-notification__header");

            var headerIcon = new Label("⚡");
            headerIcon.AddToClassList("domain-reload-notification__header-icon");
            header.Add(headerIcon);

            var headerText = new Label("检测到 Domain Reload 中断");
            headerText.AddToClassList("domain-reload-notification__header-text");
            header.Add(headerText);

            card.Add(header);

            // === 详情行：中断原因 ===
            var reasonRow = CreateDetailRow("中断原因：", "编译触发 Domain Reload");
            card.Add(reasonRow);

            // === 详情行：中断阶段 ===
            string phaseText = phase switch
            {
                InterruptPhase.Streaming => "流式响应中 (Streaming)",
                InterruptPhase.ExecutingTool => "工具执行中 (ExecutingTool)",
                InterruptPhase.WaitingCompilation => "等待编译 (WaitingCompilation)",
                _ => "未知阶段"
            };
            if (!string.IsNullOrEmpty(toolName))
            {
                phaseText += $" — {toolName}";
            }
            var phaseRow = CreateDetailRow("中断阶段：", phaseText);
            card.Add(phaseRow);

            // === 详情行：编译结果 ===
            string compileIcon = compilationSucceeded ? "✅" : "❌";
            string compileText = compilationSucceeded ? "编译成功" : "编译失败";
            if (!compilationSucceeded && !string.IsNullOrEmpty(compilationErrors))
            {
                // 截断过长的错误信息
                var errMsg = compilationErrors.Length > 100
                    ? compilationErrors.Substring(0, 100) + "..."
                    : compilationErrors;
                compileText += $" — {errMsg}";
            }
            var compileRow = CreateDetailRow("编译结果：", $"{compileIcon} {compileText}");
            // 为编译结果值添加颜色修饰
            var compileValue = compileRow.Q<Label>(className: "domain-reload-notification__detail-value");
            if (compileValue != null)
            {
                compileValue.AddToClassList(compilationSucceeded
                    ? "domain-reload-notification__compile-success"
                    : "domain-reload-notification__compile-error");
            }
            card.Add(compileRow);

            // === 状态行（初始为"恢复中..."） ===
            var statusRow = new VisualElement();
            statusRow.AddToClassList("domain-reload-notification__status");
            statusRow.name = "reload-notification-status";

            var statusIcon = new Label("⏳");
            statusIcon.AddToClassList("domain-reload-notification__status-icon");
            statusIcon.name = "reload-status-icon";
            statusRow.Add(statusIcon);

            var statusText = new Label("正在恢复会话...");
            statusText.AddToClassList("domain-reload-notification__status-text");
            statusText.name = "reload-status-text";
            statusRow.Add(statusText);

            card.Add(statusRow);

            _messageContainer.Add(card);
            ScrollToBottom();

            Debug.Log($"[AgentCore] Domain Reload notification added: phase={phase}, tool={toolName}, " +
                      $"compilationOk={compilationSucceeded}");

            return card;
        }

        /// <summary>
        /// 创建通知卡片的详情行（标签 + 值）。
        /// </summary>
        /// <param name="label">标签文本</param>
        /// <param name="value">值文本</param>
        /// <returns>详情行 VisualElement</returns>
        private static VisualElement CreateDetailRow(string label, string value)
        {
            var row = new VisualElement();
            row.AddToClassList("domain-reload-notification__detail");

            var labelElem = new Label(label);
            labelElem.AddToClassList("domain-reload-notification__detail-label");
            row.Add(labelElem);

            var valueElem = new Label(value);
            valueElem.AddToClassList("domain-reload-notification__detail-value");
            row.Add(valueElem);

            return row;
        }

        /// <summary>
        /// 更新 Domain Reload 通知卡片的恢复状态。
        /// </summary>
        /// <param name="card">通知卡片 VisualElement（由 AddDomainReloadNotification 返回）</param>
        /// <param name="success">恢复是否成功</param>
        /// <param name="errorMessage">失败时的错误信息（可为 null）</param>
        private static void UpdateDomainReloadNotificationStatus(
            VisualElement card,
            bool success,
            string errorMessage = null)
        {
            if (card == null) return;

            // 查找状态行元素
            var statusIcon = card.Q<Label>("reload-status-icon");
            var statusText = card.Q<Label>("reload-status-text");

            if (success)
            {
                // 恢复成功
                card.AddToClassList("domain-reload-notification--success");
                if (statusIcon != null) statusIcon.text = "✅";
                if (statusText != null) statusText.text = "会话已恢复，继续执行中";
            }
            else
            {
                // 恢复失败
                card.AddToClassList("domain-reload-notification--error");
                if (statusIcon != null) statusIcon.text = "❌";

                var failText = "恢复失败";
                if (!string.IsNullOrEmpty(errorMessage))
                {
                    failText += $"：{errorMessage}";
                }
                failText += "\n💡 建议：请手动重新发送消息继续操作";

                if (statusText != null) statusText.text = failText;
            }

            Debug.Log($"[AgentCore] Domain Reload notification status updated: success={success}" +
                      (string.IsNullOrEmpty(errorMessage) ? "" : $", error={errorMessage}"));
        }

        #endregion

        #region 会话恢复

        /// <summary>
        /// 尝试恢复上一次的会话。
        /// 在窗口创建时调用，从 SessionManager 加载上一次的会话并重建 UI。
        /// </summary>
        private void TryRestoreSession()
        {
            if (_agentLoop == null) return;

            try
            {
                var session = SessionManager.Instance.TryRestoreLastSession();
                if (session == null || session.Turns == null || session.Turns.Count == 0)
                {
                    Debug.Log("[AgentCore] No previous session to restore, starting fresh.");
                    // 即使没有会话可恢复，也要清除可能残留的中断标记
                    DomainReloadState.instance.ClearInterruption();
                    // 修复 #5: Domain Reload 路径中延迟了会话创建，如果恢复失败则在此补创建
                    EnsureSessionExists();
                    return;
                }

                // 通过 AgentLoop.LoadSession 恢复对话状态
                if (!_agentLoop.LoadSession(session.Id))
                {
                    Debug.LogWarning("[AgentCore] Failed to restore session via AgentLoop.");
                    DomainReloadState.instance.ClearInterruption();
                    // 修复 #5: 恢复失败时也需要确保有活动会话
                    EnsureSessionExists();
                    return;
                }

                // 重建 UI 消息气泡
                RebuildMessageBubbles();

                Debug.Log($"[AgentCore] Session restored: {session.Id} ({session.Title}, {session.Turns.Count} turns)");

                // Domain Reload Resilience Phase 2 & 3: 检查是否有中断标记并自动恢复
                var reloadState = DomainReloadState.instance;
                if (reloadState.WasInterrupted)
                {
                    Debug.Log($"[AgentCore] Domain Reload detected: session {reloadState.InterruptedSessionId} " +
                              $"was interrupted during {reloadState.InterruptPhase}" +
                              (string.IsNullOrEmpty(reloadState.LastToolName) ? "" : $" (tool: {reloadState.LastToolName})") +
                              (reloadState.HadPendingToolCalls ? " [had pending tool calls]" : "") +
                              $" at {reloadState.InterruptTimestamp}");

                    // Phase 2: 设置编译结果（Domain Reload 完成意味着编译已结束）
                    // 如果 Domain Reload 成功完成且我们的代码正在运行，说明编译通过。
                    // Unity 在编译失败时不会完成 Domain Reload（会停留在错误状态）。
                    bool compilationSucceeded = !EditorUtility.scriptCompilationFailed;
                    string compilationErrors = compilationSucceeded
                        ? null
                        : "编译失败，请检查 Unity Console 中的错误信息";
                    Debug.Log($"[AgentCore] Post-reload compilation check: succeeded={compilationSucceeded}");

                    reloadState.SetCompilationResult(compilationSucceeded, compilationErrors);

                    // Phase 3: 在聊天区域显示恢复通知卡片（带"恢复中..."状态）
                    var notificationCard = AddDomainReloadNotification(
                        reloadState.InterruptPhase,
                        reloadState.LastToolName,
                        compilationSucceeded,
                        compilationErrors);

                    // 调用 AgentLoop.TryResumeAfterReload() 触发自动恢复
                    bool resumed = _agentLoop.TryResumeAfterReload();

                    // Phase 3: 根据恢复结果更新通知卡片状态
                    if (resumed)
                    {
                        Debug.Log("[AgentCore] Domain Reload recovery initiated successfully.");
                        UpdateDomainReloadNotificationStatus(notificationCard, success: true);
                    }
                    else
                    {
                        Debug.Log("[AgentCore] Domain Reload recovery skipped or failed, continuing normally.");
                        UpdateDomainReloadNotificationStatus(notificationCard, success: false,
                            errorMessage: "恢复未执行，可能是中断阶段不支持自动恢复");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentCore] Failed to restore session: {ex.Message}");
                // 修复 #5: 异常时也需要确保有活动会话
                EnsureSessionExists();
            }
        }

        /// <summary>
        /// 确保 SessionManager 有活动会话。
        /// 修复 #5: Domain Reload 路径中 Initialize() 延迟了会话创建，
        /// 如果 TryRestoreSession() 未能恢复会话，则在此补创建新会话。
        /// </summary>
        private static void EnsureSessionExists()
        {
            if (string.IsNullOrEmpty(SessionManager.Instance.CurrentSessionId))
            {
                Debug.Log("[AgentCore] No active session after restore attempt, creating new session.");
                SessionManager.Instance.CreateNewSession();
            }
        }

        /// <summary>
        /// 从 AgentLoop 的 ConversationHistory 重建所有消息气泡。
        /// 用于会话恢复时重建 UI。
        /// </summary>
        private void RebuildMessageBubbles()
        {
            if (_agentLoop == null || _messageContainer == null)
            {
                Debug.LogWarning($"[AgentCore.UI] RebuildMessageBubbles 中止: _agentLoop={(_agentLoop != null ? "OK" : "null")}, _messageContainer={(_messageContainer != null ? "OK" : "null")}");
                return;
            }

            // 清空现有 UI
            _messageContainer.Clear();
            _messageBubbles.Clear();
            _activeToolCards.Clear();

            var history = _agentLoop.ConversationHistory;
            Debug.Log($"[AgentCore.UI] RebuildMessageBubbles: 历史记录共 {history?.Count ?? 0} 条");
            ToolCallGroup restoreGroup = null;

            for (int i = 0; i < history.Count; i++)
            {
                var turn = history[i];

                if (turn.Role == "user")
                {
                    // 用户消息前，结束上一个工具调用分组
                    restoreGroup = null;

                    // 用户消息气泡
                    var bubble = new MessageBubble(turn.Id, "user", turn.Content);
                    _messageBubbles[turn.Id] = bubble;
                    _messageContainer.Add(bubble);
                }
                else if (turn.Role == "assistant")
                {
                    // 助手消息气泡（已完成状态，非流式）
                    var bubble = new MessageBubble(turn.Id, "assistant", turn.Content);
                    _messageBubbles[turn.Id] = bubble;
                    _messageContainer.Add(bubble);

                    // 恢复工具调用卡片（统一放入分组容器）
                    if (turn.ToolCalls != null && turn.ToolCalls.Count > 0)
                    {
                        Debug.Log($"[AgentCore.UI] RebuildMessageBubbles: 恢复 {turn.ToolCalls.Count} 个工具调用 (turn={turn.Id})");
                        restoreGroup = new ToolCallGroup();

                        foreach (var tc in turn.ToolCalls)
                        {
                            var card = new ToolCallCard(tc.ToolName, tc.Arguments);
                            var status = tc.Success ? ToolCallStatus.Completed : ToolCallStatus.Failed;
                            var statusText = tc.Success
                                ? $"完成 ({tc.ExecutionTimeMs:F0}ms)"
                                : "失败";
                            card.SetStatus(status, statusText);

                            if (!string.IsNullOrEmpty(tc.Result))
                            {
                                var result = tc.Result.Length > 200
                                    ? tc.Result.Substring(0, 200) + "..."
                                    : tc.Result;
                                card.SetDetails(result);
                            }

                            restoreGroup.AddToolCard(card);
                        }

                        // 历史工具调用全部完成，通知分组更新统计并折叠
                        restoreGroup.NotifyToolStatusChanged();
                        _messageContainer.Add(restoreGroup);
                        Debug.Log($"[AgentCore.UI] RebuildMessageBubbles: ToolCallGroup 已添加到 _messageContainer (childCount={_messageContainer.childCount})");

                        // 助手消息后结束分组
                        restoreGroup = null;
                    }
                }
            }

            // 清除临时分组引用
            _currentToolCallGroup = null;

            // 滚动到底部
            ScrollToBottom();
        }

        /// <summary>
        /// 滚动消息列表到底部。
        /// 延迟一帧执行，确保布局已更新。
        /// </summary>
        private void ScrollToBottom()
        {
            _messageScrollView?.schedule.Execute(() =>
            {
                if (_messageScrollView != null)
                {
                    _messageScrollView.scrollOffset = new Vector2(0, float.MaxValue);
                }
            });
        }

        #endregion

        #region 会话侧边栏

        /// <summary>
        /// 切换侧边栏的展开/折叠状态。
        /// </summary>
        private void ToggleSidebar()
        {
            _sidebarExpanded = !_sidebarExpanded;
            EditorPrefs.SetBool(SidebarExpandedKey, _sidebarExpanded);
            UpdateSidebarVisibility();

            if (_sidebarExpanded)
            {
                RefreshSessionList();
            }
        }

        /// <summary>
        /// 根据当前状态更新侧边栏的显示/隐藏。
        /// </summary>
        private void UpdateSidebarVisibility()
        {
            if (_sessionSidebar == null) return;

            if (_sidebarExpanded)
            {
                _sessionSidebar.AddToClassList("sidebar-visible");
                _sidebarToggleButton?.AddToClassList("sidebar-active");
            }
            else
            {
                _sessionSidebar.RemoveFromClassList("sidebar-visible");
                _sidebarToggleButton?.RemoveFromClassList("sidebar-active");
            }
        }

        /// <summary>
        /// 仅更新当前活动会话在侧边栏列表中的标题文本，避免重建整个列表。
        /// 如果找不到对应元素（例如会话刚创建还没在列表中），则 fallback 到 RefreshSessionList()。
        /// </summary>
        private void UpdateCurrentSessionTitle()
        {
            if (_sessionListContainer == null)
            {
                return;
            }

            var currentId = SessionManager.Instance.CurrentSessionId;
            if (string.IsNullOrEmpty(currentId))
            {
                RefreshSessionList();
                return;
            }

            // 尝试通过 name 属性找到当前会话的标题 Label
            var titleLabel = _sessionListContainer.Q<Label>($"session-title-{currentId}");
            if (titleLabel == null)
            {
                // 找不到对应元素，fallback 到完整刷新
                RefreshSessionList();
                return;
            }

            // 从 SessionManager 获取最新标题
            var newTitle = SessionManager.Instance.CurrentSessionTitle;
            if (string.IsNullOrEmpty(newTitle))
            {
                newTitle = "新会话";
            }
            if (newTitle.Length > MaxTitleDisplayLength)
            {
                newTitle = newTitle.Substring(0, MaxTitleDisplayLength) + "...";
            }

            // 如果标题没有变化，跳过更新
            if (titleLabel.text == newTitle)
            {
                return;
            }

            titleLabel.text = newTitle;
        }

        /// <summary>
        /// 刷新会话列表 UI。
        /// 从 SessionManager 获取所有会话摘要并重建列表项。
        /// </summary>
        private void RefreshSessionList()
        {
            if (_sessionListContainer == null) return;

            // 重建列表时清理重命名状态，防止旧的 TextField 被销毁后
            // _renamingSessionId 残留导致所有点击被拦截
            _renamingSessionId = null;

            // 保存滚动位置，重建后恢复（避免列表跳回顶部）
            var savedScrollOffset = _sessionListScrollView?.scrollOffset ?? Vector2.zero;

            _sessionListContainer.Clear();

            var sessions = SessionManager.Instance.GetSessionList();
            var currentId = SessionManager.Instance.CurrentSessionId;

            if (sessions == null || sessions.Count == 0)
            {
                var emptyLabel = new Label("暂无会话");
                emptyLabel.style.color = new StyleColor(new Color(0.5f, 0.5f, 0.5f));
                emptyLabel.style.fontSize = 12;
                emptyLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
                emptyLabel.style.paddingTop = 20;
                _sessionListContainer.Add(emptyLabel);
                // 列表为空时不需要恢复滚动位置
                return;
            }

            foreach (var session in sessions)
            {
                var item = CreateSessionItem(session, session.Id == currentId);
                _sessionListContainer.Add(item);
            }

            // 恢复滚动位置（延迟一帧，确保布局已更新）
            if (_sessionListScrollView != null)
            {
                _sessionListScrollView.schedule.Execute(() =>
                {
                    if (_sessionListScrollView != null)
                    {
                        _sessionListScrollView.scrollOffset = savedScrollOffset;
                    }
                });
            }
        }

        /// <summary>
        /// 创建单个会话列表项 VisualElement。
        /// </summary>
        /// <param name="session">会话摘要数据</param>
        /// <param name="isActive">是否为当前活动会话</param>
        /// <returns>会话列表项 VisualElement</returns>
        private VisualElement CreateSessionItem(SessionSummary session, bool isActive)
        {
            var item = new VisualElement();
            item.name = $"session-item-{session.Id}";
            item.AddToClassList("session-item");
            item.userData = session.Id;

            if (isActive)
            {
                item.AddToClassList("session-active");
            }

            // 会话标题
            var title = session.Title;
            if (string.IsNullOrEmpty(title))
            {
                title = "新会话";
            }
            if (title.Length > MaxTitleDisplayLength)
            {
                title = title.Substring(0, MaxTitleDisplayLength) + "...";
            }

            var titleLabel = new Label(title);
            titleLabel.name = $"session-title-{session.Id}";
            titleLabel.AddToClassList("session-item-title");
            item.Add(titleLabel);

            // 最后更新时间（相对时间）
            var timeLabel = new Label(FormatRelativeTime(session.UpdatedAt));
            timeLabel.AddToClassList("session-item-time");
            item.Add(timeLabel);

            // 点击切换会话
            item.RegisterCallback<ClickEvent>(evt =>
            {
                // 如果正在重命名，不处理点击
                if (!string.IsNullOrEmpty(_renamingSessionId))
                {
                    return;
                }

                var sessionId = item.userData as string;
                if (!string.IsNullOrEmpty(sessionId))
                {
                    SwitchToSession(sessionId);
                }
            });

            // 右键上下文菜单
            item.RegisterCallback<ContextClickEvent>(evt =>
            {
                evt.StopPropagation();
                var sessionId = item.userData as string;
                if (!string.IsNullOrEmpty(sessionId))
                {
                    ShowSessionContextMenu(sessionId, session.Title, item);
                }
            });

            return item;
        }

        /// <summary>
        /// 切换到指定会话。
        /// </summary>
        /// <param name="sessionId">目标会话 ID</param>
        private void SwitchToSession(string sessionId)
        {
            if (_agentLoop == null) return;

            // 如果已经是当前会话，不做任何操作
            if (SessionManager.Instance.CurrentSessionId == sessionId)
            {
                return;
            }

            // 如果 Agent 正忙，不允许切换
            if (_agentLoop.CurrentState != AgentState.Idle)
            {
                Debug.LogWarning("[AgentCore] Cannot switch session while agent is busy.");
                return;
            }

            try
            {
                // 1. 保存当前会话（ForceSave 内部会跳过无用户消息的空会话）
                SessionManager.Instance.ForceSave(
                    new List<ChatMessage>(_agentLoop.Messages),
                    new List<ConversationTurn>(_agentLoop.ConversationTurns));

                // 1.5 触发自动记忆（fire-and-forget，仅在有实际对话内容时生效）
                try
                {
                    SessionManager.Instance.TriggerAutoMemory(_agentLoop.LLMClient);
                }
                catch (Exception amEx)
                {
                    Debug.LogWarning($"[AgentCore] Auto-memory trigger on session switch failed (non-fatal): {amEx.Message}");
                }

                // 2. 加载目标会话（AgentLoop.LoadSession 不再重复保存）
                if (!_agentLoop.LoadSession(sessionId))
                {
                    Debug.LogWarning($"[AgentCore] Failed to switch to session: {sessionId}");
                    return;
                }

                // 3. 重建消息气泡
                RebuildMessageBubbles();

                // 4. 刷新会话列表（更新高亮）
                RefreshSessionList();

                Debug.Log($"[AgentCore] Switched to session: {sessionId}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AgentCore] Error switching session: {ex.Message}");
            }
        }

        /// <summary>
        /// 新建会话按钮点击处理。
        /// </summary>
        private void OnNewSessionClicked()
        {
            if (_agentLoop == null) return;

            if (_agentLoop.CurrentState != AgentState.Idle)
            {
                Debug.LogWarning("[AgentCore] Cannot create new session while agent is busy.");
                return;
            }

            try
            {
                // 1. 重置对话（ResetConversation 内部已包含 ForceSave + TriggerAutoMemory + 创建新会话）
                ClearMessages();
                _agentLoop.ResetConversation();

                // 2. 刷新会话列表
                RefreshSessionList();

                Debug.Log("[AgentCore] New session created.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AgentCore] Error creating new session: {ex.Message}");
            }
        }

        /// <summary>
        /// 显示会话右键上下文菜单。
        /// </summary>
        /// <param name="sessionId">会话 ID</param>
        /// <param name="currentTitle">当前标题</param>
        /// <param name="itemElement">会话列表项 VisualElement</param>
        private void ShowSessionContextMenu(string sessionId, string currentTitle, VisualElement itemElement)
        {
            var menu = new GenericMenu();

            menu.AddItem(new GUIContent("重命名"), false, () =>
            {
                BeginRenameSession(sessionId, currentTitle, itemElement);
            });

            menu.AddSeparator("");

            menu.AddItem(new GUIContent("导出/Markdown (.md)"), false, () =>
            {
                ExportSession(sessionId, SessionExporter.ExportFormat.Markdown);
            });

            menu.AddItem(new GUIContent("导出/JSON (.json)"), false, () =>
            {
                ExportSession(sessionId, SessionExporter.ExportFormat.Json);
            });

            menu.AddSeparator("");

            menu.AddItem(new GUIContent("删除"), false, () =>
            {
                DeleteSessionWithConfirm(sessionId);
            });

            menu.ShowAsContext();
        }

        /// <summary>
        /// 开始内联重命名会话。
        /// 将标题 Label 替换为可编辑的 TextField。
        /// </summary>
        /// <param name="sessionId">会话 ID</param>
        /// <param name="currentTitle">当前标题</param>
        /// <param name="itemElement">会话列表项 VisualElement</param>
        private void BeginRenameSession(string sessionId, string currentTitle, VisualElement itemElement)
        {
            if (!string.IsNullOrEmpty(_renamingSessionId)) return;
            _renamingSessionId = sessionId;

            // 查找标题 Label 并隐藏
            var titleLabel = itemElement.Q<Label>(className: "session-item-title");
            if (titleLabel != null)
            {
                titleLabel.style.display = DisplayStyle.None;
            }

            // 创建内联编辑 TextField
            var renameField = new TextField();
            renameField.AddToClassList("session-rename-field");
            renameField.value = currentTitle ?? "";
            renameField.selectAllOnFocus = true;

            // 插入到标题 Label 的位置
            int insertIndex = titleLabel != null ? itemElement.IndexOf(titleLabel) : 0;
            itemElement.Insert(insertIndex + 1, renameField);

            // 延迟聚焦（确保元素已布局）
            renameField.schedule.Execute(() => renameField.Focus());

            // 确认重命名（Enter 或失去焦点）
            Action commitRename = () =>
            {
                if (_renamingSessionId != sessionId) return;

                var newTitle = renameField.value?.Trim();
                if (!string.IsNullOrEmpty(newTitle) && newTitle != currentTitle)
                {
                    SessionManager.Instance.RenameSession(sessionId, newTitle);
                }

                // 清理：移除 TextField，恢复 Label
                _renamingSessionId = null;
                renameField.RemoveFromHierarchy();
                if (titleLabel != null)
                {
                    titleLabel.style.display = DisplayStyle.Flex;
                }

                RefreshSessionList();
            };

            renameField.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                {
                    evt.PreventDefault();
                    evt.StopPropagation();
                    commitRename();
                }
                else if (evt.keyCode == KeyCode.Escape)
                {
                    evt.PreventDefault();
                    evt.StopPropagation();
                    // 取消重命名
                    _renamingSessionId = null;
                    renameField.RemoveFromHierarchy();
                    if (titleLabel != null)
                    {
                        titleLabel.style.display = DisplayStyle.Flex;
                    }
                }
            });

            renameField.RegisterCallback<FocusOutEvent>(_ =>
            {
                // 失去焦点时提交
                if (_renamingSessionId == sessionId)
                {
                    commitRename();
                }
            });
        }

        /// <summary>
        /// 删除会话（带确认对话框）。
        /// </summary>
        /// <param name="sessionId">要删除的会话 ID</param>
        private void DeleteSessionWithConfirm(string sessionId)
        {
            var confirmed = EditorUtility.DisplayDialog(
                "删除会话",
                "确定要删除此会话吗？此操作不可撤销。",
                "删除",
                "取消");

            if (!confirmed) return;

            var isCurrentSession = SessionManager.Instance.CurrentSessionId == sessionId;

            // 执行删除
            SessionManager.Instance.DeleteSession(sessionId);

            if (isCurrentSession)
            {
                // 删除的是当前活动会话，需要切换到其他会话或创建新会话
                var sessions = SessionManager.Instance.GetSessionList();
                if (sessions != null && sessions.Count > 0)
                {
                    // 切换到最近的会话
                    SwitchToSession(sessions[0].Id);
                }
                else
                {
                    // 没有其他会话，创建新会话
                    ClearMessages();
                    _agentLoop?.ResetConversation();
                }
            }

            RefreshSessionList();
            Debug.Log($"[AgentCore] Session deleted: {sessionId}");
        }

        /// <summary>
        /// 将 UTC 时间格式化为相对时间字符串。
        /// </summary>
        /// <param name="utcTime">UTC 时间</param>
        /// <returns>相对时间字符串（如"刚刚"、"5分钟前"、"昨天"）</returns>
        private static string FormatRelativeTime(DateTime utcTime)
        {
            var now = DateTime.UtcNow;
            var diff = now - utcTime;

            if (diff.TotalSeconds < 60)
                return "刚刚";
            if (diff.TotalMinutes < 60)
                return $"{(int)diff.TotalMinutes}分钟前";
            if (diff.TotalHours < 24)
                return $"{(int)diff.TotalHours}小时前";
            if (diff.TotalDays < 2)
                return "昨天";
            if (diff.TotalDays < 7)
                return $"{(int)diff.TotalDays}天前";
            if (diff.TotalDays < 30)
                return $"{(int)(diff.TotalDays / 7)}周前";

            return utcTime.ToLocalTime().ToString("MM/dd");
        }

        #endregion

        #region 工具调用 UI 处理

        /// <summary>
        /// 确保当前存在一个工具调用分组容器。
        /// 如果不存在，创建一个新的并添加到消息容器。
        /// </summary>
        /// <returns>当前的工具调用分组容器</returns>
        private ToolCallGroup EnsureToolCallGroup()
        {
            if (_currentToolCallGroup == null)
            {
                _currentToolCallGroup = new ToolCallGroup();
                _messageContainer?.Add(_currentToolCallGroup);
                Debug.Log($"[AgentCore.UI] EnsureToolCallGroup: 新建 ToolCallGroup, _messageContainer.childCount={_messageContainer?.childCount}");
            }
            return _currentToolCallGroup;
        }

        /// <summary>
        /// 获取工具调用的唯一 key。优先使用 ToolCallId，缺失时用计数器生成。
        /// 仅在 HandleToolCallStarted 中调用（会递增计数器）。
        /// </summary>
        private string GetToolCallKey(AgentEvent evt)
        {
            if (!string.IsNullOrEmpty(evt.ToolCallId))
                return evt.ToolCallId;
            // fallback: 用工具名+计数器生成唯一 key
            return $"{evt.ToolName}_{_toolCallCounter++}";
        }

        /// <summary>
        /// 在 _activeToolCards 中查找与事件匹配的 key。
        /// 优先精确匹配 ToolCallId，找不到则按 ToolName 前缀匹配（兼容 toolName_{N} 格式）。
        /// 用于 HandleToolCallCompleted / HandleToolCallFailed。
        /// </summary>
        private string FindToolCardKey(AgentEvent evt)
        {
            // 1. 优先用 ToolCallId 精确匹配
            if (!string.IsNullOrEmpty(evt.ToolCallId) && _activeToolCards.ContainsKey(evt.ToolCallId))
                return evt.ToolCallId;

            // 2. fallback: 按 ToolName 前缀匹配（key 可能是 "toolName_0", "toolName_1" 等）
            //    取最后一个匹配项（最近添加的）
            string matched = null;
            if (!string.IsNullOrEmpty(evt.ToolName))
            {
                var prefix = evt.ToolName + "_";
                foreach (var key in _activeToolCards.Keys)
                {
                    if (key == evt.ToolName || key.StartsWith(prefix))
                        matched = key;
                }
            }

            return matched;
        }

        /// <summary>
        /// 处理工具调用开始事件：创建 ToolCallCard 并添加到分组容器。
        /// </summary>
        /// <param name="evt">工具调用开始事件</param>
        private void HandleToolCallStarted(AgentEvent evt)
        {
            Debug.Log($"[AgentCore.UI] HandleToolCallStarted: tool={evt.ToolName}, toolCallId={evt.ToolCallId ?? "(null)"}, messageId={evt.MessageId ?? "(null)"}");

            var group = EnsureToolCallGroup();

            var card = new ToolCallCard(evt.ToolName, evt.ToolArguments);
            card.SetStatus(ToolCallStatus.Running, "执行中...");
            group.AddToolCard(card);

            // 用 ToolCallId 作为 key（支持同名工具多次调用）
            var key = GetToolCallKey(evt);
            _activeToolCards[key] = card;
            Debug.Log($"[AgentCore.UI] HandleToolCallStarted: card 已添加, key={key}, _activeToolCards.Count={_activeToolCards.Count}");
            ScrollToBottom();
        }

        /// <summary>
        /// 处理工具调用完成事件：更新对应的 ToolCallCard 为完成状态。
        /// </summary>
        /// <param name="evt">工具调用完成事件</param>
        private void HandleToolCallCompleted(AgentEvent evt)
        {
            var key = FindToolCardKey(evt);
            Debug.Log($"[AgentCore.UI] HandleToolCallCompleted: tool={evt.ToolName}, key={key ?? "(no match)"}, found={key != null}");

            if (key != null && _activeToolCards.TryGetValue(key, out var card))
            {
                var timeText = evt.ExecutionTimeMs > 0
                    ? $" ({evt.ExecutionTimeMs:F0}ms)"
                    : "";
                card.SetStatus(ToolCallStatus.Completed, $"完成{timeText}");

                if (!string.IsNullOrEmpty(evt.ToolResult))
                {
                    // 截断过长的结果
                    var result = evt.ToolResult.Length > 200
                        ? evt.ToolResult.Substring(0, 200) + "..."
                        : evt.ToolResult;
                    card.SetDetails(result);
                }

                _activeToolCards.Remove(key);

                // 通知分组容器更新统计和折叠状态
                _currentToolCallGroup?.NotifyToolStatusChanged();
            }
            else
            {
                Debug.LogWarning($"[AgentCore.UI] HandleToolCallCompleted: 未找到 key={key} 的卡片, 当前 keys=[{string.Join(", ", _activeToolCards.Keys)}]");
            }
        }

        /// <summary>
        /// 处理工具调用失败事件：更新对应的 ToolCallCard 为失败状态。
        /// </summary>
        /// <param name="evt">工具调用失败事件</param>
        private void HandleToolCallFailed(AgentEvent evt)
        {
            var key = FindToolCardKey(evt);
            Debug.Log($"[AgentCore.UI] HandleToolCallFailed: tool={evt.ToolName}, key={key ?? "(no match)"}, found={key != null}");

            if (key != null && _activeToolCards.TryGetValue(key, out var card))
            {
                card.SetStatus(ToolCallStatus.Failed, "失败");

                if (!string.IsNullOrEmpty(evt.ToolResult))
                {
                    card.SetDetails(evt.ToolResult);
                }

                _activeToolCards.Remove(key);

                // 通知分组容器更新统计和折叠状态
                _currentToolCallGroup?.NotifyToolStatusChanged();
            }
            else
            {
                Debug.LogWarning($"[AgentCore.UI] HandleToolCallFailed: 未找到 key={key} 的卡片, 当前 keys=[{string.Join(", ", _activeToolCards.Keys)}]");
            }
        }

        /// <summary>
        /// 处理循环轮次开始事件：更新分组容器的轮次信息，并在容器内添加轮次分隔线。
        /// </summary>
        /// <param name="evt">循环轮次开始事件</param>
        private void HandleLoopRoundStarted(AgentEvent evt)
        {
            var group = EnsureToolCallGroup();

            // 更新分组容器的轮次信息
            group.UpdateRoundInfo(evt.CurrentRound, evt.MaxRounds);

            // 第 1 轮不显示分隔线（避免冗余）
            if (evt.CurrentRound <= 1) return;

            var separator = new VisualElement();
            separator.style.flexDirection = FlexDirection.Row;
            separator.style.alignItems = Align.Center;
            separator.style.marginTop = 6;
            separator.style.marginBottom = 6;
            separator.style.marginLeft = 4;
            separator.style.marginRight = 4;

            // 左侧线条
            var leftLine = new VisualElement();
            leftLine.style.flexGrow = 1;
            leftLine.style.height = 1;
            leftLine.style.backgroundColor = new Color(0.3f, 0.3f, 0.3f);
            separator.Add(leftLine);

            // 轮次文本
            var roundLabel = new Label($" 第 {evt.CurrentRound}/{evt.MaxRounds} 轮 ");
            roundLabel.style.fontSize = 10;
            roundLabel.style.color = new Color(0.533f, 0.533f, 0.533f);
            roundLabel.style.flexShrink = 0;
            separator.Add(roundLabel);

            // 右侧线条
            var rightLine = new VisualElement();
            rightLine.style.flexGrow = 1;
            rightLine.style.height = 1;
            rightLine.style.backgroundColor = new Color(0.3f, 0.3f, 0.3f);
            separator.Add(rightLine);

            // 分隔线添加到分组容器内部
            group.AddSeparator(separator);
            ScrollToBottom();
        }

        #endregion

        #region UI 辅助方法

        /// <summary>
        /// 更新状态标签文本和样式。
        /// </summary>
        /// <param name="text">状态文本</param>
        /// <param name="isError">是否为错误状态（红色显示）</param>
        private void UpdateStatusLabel(string text, bool isError = false)
        {
            if (_statusLabel == null) return;
            _statusLabel.text = text;
            _statusLabel.style.color = isError
                ? new StyleColor(new Color(0.9f, 0.4f, 0.4f))
                : new StyleColor(new Color(0.53f, 0.53f, 0.53f));
        }

        /// <summary>
        /// 设置发送按钮的启用/禁用状态。
        /// </summary>
        /// <param name="enabled">是否启用</param>
        private void SetSendEnabled(bool enabled)
        {
            if (_sendButton != null)
            {
                _sendButton.SetEnabled(enabled);
            }
        }

        /// <summary>
        /// 设置取消按钮的显示/隐藏状态。
        /// </summary>
        /// <param name="visible">是否显示</param>
        private void SetCancelVisible(bool visible)
        {
            if (_cancelButton != null)
            {
                _cancelButton.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        /// <summary>
        /// 显示导出格式选择菜单（由 Ctrl+Shift+E 快捷键触发）。
        /// </summary>
        private void ShowExportMenu()
        {
            var sessionId = SessionManager.Instance?.CurrentSessionId;
            if (string.IsNullOrEmpty(sessionId))
            {
                Debug.LogWarning("[AgentCore] No active session to export.");
                return;
            }

            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("导出为 Markdown (.md)"), false, () =>
            {
                ExportSession(sessionId, SessionExporter.ExportFormat.Markdown);
            });
            menu.AddItem(new GUIContent("导出为 JSON (.json)"), false, () =>
            {
                ExportSession(sessionId, SessionExporter.ExportFormat.Json);
            });
            menu.ShowAsContext();
        }

        /// <summary>
        /// 导出指定会话到文件。弹出文件保存对话框让用户选择路径。
        /// </summary>
        /// <param name="sessionId">要导出的会话 ID</param>
        /// <param name="format">导出格式</param>
        private void ExportSession(string sessionId, SessionExporter.ExportFormat format)
        {
            try
            {
                var session = SessionStorage.Load(sessionId);
                if (session == null)
                {
                    Debug.LogError($"[AgentCore] Failed to load session: {sessionId}");
                    return;
                }

                var defaultName = SessionExporter.GetDefaultFileName(session, format);
                var extension = format == SessionExporter.ExportFormat.Markdown ? "md" : "json";
                var filterDisplay = format == SessionExporter.ExportFormat.Markdown ? "Markdown files" : "JSON files";

                var path = EditorUtility.SaveFilePanel(
                    "导出会话",
                    "",
                    defaultName,
                    extension
                );

                if (string.IsNullOrEmpty(path))
                    return; // 用户取消

                SessionExporter.ExportToFile(session, path, format);
                Debug.Log($"[AgentCore] Session exported to: {path}");
                EditorUtility.RevealInFinder(path);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AgentCore] Export failed: {ex.Message}");
            }
        }

        #endregion
    }
}
