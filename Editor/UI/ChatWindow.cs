using System.Collections.Generic;
using AgentCore.Editor.Config;
using AgentCore.Editor.Core;
using AgentCore.Editor.LLM;
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
    /// 支持流式文本显示、消息历史、取消操作和对话重置。
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

        #endregion

        #region 核心字段

        /// <summary>Agent Loop 实例，管理对话逻辑</summary>
        private AgentLoop _agentLoop;

        /// <summary>消息气泡字典，按 MessageId 索引</summary>
        private readonly Dictionary<string, MessageBubble> _messageBubbles = new();

        /// <summary>活跃的工具调用卡片字典，按工具名索引</summary>
        private readonly Dictionary<string, ToolCallCard> _activeToolCards = new();

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

        /// <summary>重置按钮</summary>
        private Button _resetButton;

        /// <summary>状态标签</summary>
        private Label _statusLabel;

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

            // 2.5 设置系统字体（黑体加粗）
            var font = Font.CreateDynamicFontFromOSFont("Microsoft YaHei", 14);
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
            _resetButton = rootVisualElement.Q<Button>("reset-button");
            _statusLabel = rootVisualElement.Q<Label>("status-label");

            // 4. 绑定按钮事件
            _sendButton?.RegisterCallback<ClickEvent>(_ => OnSendClicked());
            _cancelButton?.RegisterCallback<ClickEvent>(_ => OnCancelClicked());
            _resetButton?.RegisterCallback<ClickEvent>(_ => OnResetClicked());

            // 5. 绑定输入框键盘事件（Enter 发送，Shift+Enter 换行，Escape 取消）
            _inputField?.RegisterCallback<KeyDownEvent>(OnInputFieldKeyDown);

            // 6. 初始状态：取消按钮隐藏
            if (_cancelButton != null)
            {
                _cancelButton.style.display = DisplayStyle.None;
            }

            // 7. 创建并初始化 AgentLoop
            InitializeAgentLoop();
        }

        /// <summary>
        /// 窗口销毁时清理资源。
        /// 取消订阅事件，取消进行中的操作。
        /// </summary>
        private void OnDestroy()
        {
            if (_agentLoop != null)
            {
                _agentLoop.OnAgentEvent -= HandleAgentEvent;
                _agentLoop.Cancel();
                _agentLoop = null;
            }

            _messageBubbles.Clear();
            _activeToolCards.Clear();
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
            catch (System.Exception ex)
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
        /// 重置按钮点击处理。
        /// 清空 UI 消息容器并重置 AgentLoop 对话历史。
        /// </summary>
        private void OnResetClicked()
        {
            ClearMessages();
            _agentLoop?.ResetConversation();
            Debug.Log("[AgentCore] Conversation reset by user.");
        }

        /// <summary>
        /// 输入框键盘事件处理。
        /// Enter 发送消息，Shift+Enter 换行，Escape 取消操作。
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
                    break;

                case AgentEventType.Error:
                    ShowError(evt.Content);
                    break;

                case AgentEventType.ConversationReset:
                    ClearMessages();
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
            var messageId = System.Guid.NewGuid().ToString();
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
            var messageId = System.Guid.NewGuid().ToString();
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

        #region 工具调用 UI 处理

        /// <summary>
        /// 处理工具调用开始事件：创建 ToolCallCard 并添加到聊天区域。
        /// </summary>
        /// <param name="evt">工具调用开始事件</param>
        private void HandleToolCallStarted(AgentEvent evt)
        {
            var card = new ToolCallCard(evt.ToolName, evt.ToolArguments);
            card.SetStatus(ToolCallStatus.Running, "执行中...");
            _messageContainer?.Add(card);

            // 用工具名作为 key（同一工具名的后续调用会覆盖前一个引用）
            _activeToolCards[evt.ToolName] = card;
            ScrollToBottom();
        }

        /// <summary>
        /// 处理工具调用完成事件：更新对应的 ToolCallCard 为完成状态。
        /// </summary>
        /// <param name="evt">工具调用完成事件</param>
        private void HandleToolCallCompleted(AgentEvent evt)
        {
            if (_activeToolCards.TryGetValue(evt.ToolName, out var card))
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

                _activeToolCards.Remove(evt.ToolName);
            }
        }

        /// <summary>
        /// 处理工具调用失败事件：更新对应的 ToolCallCard 为失败状态。
        /// </summary>
        /// <param name="evt">工具调用失败事件</param>
        private void HandleToolCallFailed(AgentEvent evt)
        {
            if (_activeToolCards.TryGetValue(evt.ToolName, out var card))
            {
                card.SetStatus(ToolCallStatus.Failed, "失败");

                if (!string.IsNullOrEmpty(evt.ToolResult))
                {
                    card.SetDetails(evt.ToolResult);
                }

                _activeToolCards.Remove(evt.ToolName);
            }
        }

        /// <summary>
        /// 处理循环轮次开始事件：在聊天区域添加轮次分隔线。
        /// </summary>
        /// <param name="evt">循环轮次开始事件</param>
        private void HandleLoopRoundStarted(AgentEvent evt)
        {
            // 第 1 轮不显示分隔线（避免冗余）
            if (evt.CurrentRound <= 1) return;

            var separator = new VisualElement();
            separator.style.flexDirection = FlexDirection.Row;
            separator.style.alignItems = Align.Center;
            separator.style.marginTop = 6;
            separator.style.marginBottom = 6;
            separator.style.marginLeft = 12;
            separator.style.marginRight = 12;

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

            _messageContainer?.Add(separator);
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

        #endregion
    }
}
