using System;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Components
{
    /// <summary>
    /// 消息气泡组件。
    /// <para>
    /// 用于在聊天窗口中显示单条消息，支持用户消息和助手消息两种样式。
    /// 助手消息支持流式文本显示，通过内嵌的 <see cref="StreamingTextElement"/> 实现。
    /// </para>
    /// </summary>
    public class MessageBubble : VisualElement
    {
        #region 常量

        /// <summary>MessageBubble UXML 模板在包内的路径</summary>
        private const string UxmlPath = "Packages/com.agentcore.unity/Editor/UI/Components/MessageBubble.uxml";

        /// <summary>MessageBubble USS 样式在包内的路径</summary>
        private const string UssPath = "Packages/com.agentcore.unity/Editor/UI/Components/MessageBubble.uss";

        #endregion

        #region 公开属性

        /// <summary>
        /// 消息唯一标识，用于关联 AgentEvent 中的 MessageId。
        /// </summary>
        public string MessageId { get; }

        /// <summary>
        /// 消息角色：&quot;user&quot; / &quot;assistant&quot; / &quot;error&quot;。
        /// </summary>
        public string Role { get; }

        /// <summary>
        /// 重试按钮点击回调。设置后会在错误消息气泡中显示重试按钮。
        /// </summary>
        public Action OnRetryClicked { get; set; }

        #endregion

        #region 私有字段

        /// <summary>内容文本 Label（非流式消息使用）</summary>
        private Label _contentLabel;

        /// <summary>流式文本元素（仅助手消息使用）</summary>
        private StreamingTextElement _streamingText;

        /// <summary>气泡内容容器</summary>
        private VisualElement _bubbleContent;

        /// <summary>气泡根元素</summary>
        private VisualElement _bubbleRoot;

        /// <summary>是否处于流式输出模式</summary>
        private bool _isStreaming;

        #endregion

        #region 构造函数

        /// <summary>
        /// 创建消息气泡组件。
        /// </summary>
        /// <param name="messageId">消息唯一标识</param>
        /// <param name="role">角色标识：&quot;user&quot; / &quot;assistant&quot; / &quot;error&quot;</param>
        /// <param name="content">初始消息内容（可为空）</param>
        /// <param name="isStreaming">是否为流式输出模式（仅助手消息有效）</param>
        public MessageBubble(string messageId, string role, string content = "", bool isStreaming = false)
        {
            MessageId = messageId;
            Role = role;
            _isStreaming = isStreaming;

            // 加载 UXML 模板
            var visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            if (visualTree != null)
            {
                visualTree.CloneTree(this);
            }
            else
            {
                Debug.LogWarning($"[AgentCore] MessageBubble UXML not found at: {UxmlPath}, using fallback layout.");
                CreateFallbackLayout();
            }

            // 加载 USS 样式
            var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
            if (styleSheet != null)
            {
                this.styleSheets.Add(styleSheet);
            }

            // 查询 UI 元素引用
            _bubbleRoot = this.Q<VisualElement>("bubble-root");
            var roleLabel = this.Q<Label>("role-label");
            var timeLabel = this.Q<Label>("time-label");
            _contentLabel = this.Q<Label>("content-label");
            _bubbleContent = this.Q<VisualElement>("bubble-content");

            // 启用内容文本选择，允许用户选中和复制文本（Unity 2022.2+）
            if (_contentLabel != null)
            {
                _contentLabel.selection.isSelectable = true;
            }

            // 设置角色样式类
            if (_bubbleRoot != null)
            {
                _bubbleRoot.AddToClassList($"message-bubble--{role}");
            }

            // 设置角色标签
            if (roleLabel != null)
            {
                roleLabel.text = GetRoleDisplayName(role);
            }

            // 设置时间标签
            if (timeLabel != null)
            {
                timeLabel.text = DateTime.Now.ToString("HH:mm");
            }

            // 根据模式设置内容
            if (isStreaming && role == "assistant")
            {
                SetupStreamingMode(content);
            }
            else
            {
                SetupStaticMode(content);
            }
        }

        #endregion

        #region 公开方法

        /// <summary>
        /// 追加流式 token 文本。
        /// 仅在流式输出模式下有效，非流式模式调用将被忽略。
        /// </summary>
        /// <param name="token">要追加的 token 文本</param>
        public void AppendStreamToken(string token)
        {
            if (!_isStreaming || _streamingText == null) return;
            _streamingText.AppendText(token);
        }

        /// <summary>
        /// 最终化消息内容。
        /// 将流式输出模式切换为静态模式，设置完整的最终文本。
        /// </summary>
        /// <param name="fullContent">完整的消息内容</param>
        public void FinalizeContent(string fullContent)
        {
            var content = fullContent ?? "";

            if (_streamingText != null)
            {
                // SetFinalText 内部会调用 FilterCompleted（含 FormatMarkdown），不要重复过滤
                _streamingText.SetFinalText(content);
            }
            else if (_contentLabel != null)
            {
                // 静态 Label 需要手动过滤
                _contentLabel.text = ContentFilter.FilterCompleted(content);
            }

            _isStreaming = false;
        }

        /// <summary>
        /// 添加重试按钮到错误消息气泡底部。
        /// 仅对 role=&quot;error&quot; 的消息有效。
        /// </summary>
        /// <param name="onRetry">重试按钮点击回调</param>
        public void AddRetryButton(Action onRetry)
        {
            if (Role != "error" || onRetry == null) return;

            OnRetryClicked = onRetry;

            var container = _bubbleContent ?? _bubbleRoot ?? this;

            // 创建重试按钮容器
            var retryContainer = new VisualElement();
            retryContainer.style.flexDirection = FlexDirection.Row;
            retryContainer.style.justifyContent = Justify.FlexEnd;
            retryContainer.style.marginTop = 6;

            // 先声明按钮引用，供闭包捕获
            Button btn = null;
            btn = new Button(() =>
            {
                // 禁用按钮防止重复点击
                btn.SetEnabled(false);
                btn.text = "重试中...";
                OnRetryClicked?.Invoke();
            });
            btn.text = "🔄 重试";
            btn.style.paddingLeft = 8;
            btn.style.paddingRight = 8;
            btn.style.paddingTop = 3;
            btn.style.paddingBottom = 3;
            btn.style.fontSize = 11;
            btn.style.borderTopLeftRadius = 4;
            btn.style.borderTopRightRadius = 4;
            btn.style.borderBottomLeftRadius = 4;
            btn.style.borderBottomRightRadius = 4;
            btn.style.backgroundColor = new Color(0.3f, 0.3f, 0.3f, 0.8f);
            btn.style.color = new Color(0.9f, 0.9f, 0.9f);
            btn.style.borderTopWidth = 1;
            btn.style.borderBottomWidth = 1;
            btn.style.borderLeftWidth = 1;
            btn.style.borderRightWidth = 1;
            btn.style.borderTopColor = new Color(0.5f, 0.5f, 0.5f, 0.5f);
            btn.style.borderBottomColor = new Color(0.5f, 0.5f, 0.5f, 0.5f);
            btn.style.borderLeftColor = new Color(0.5f, 0.5f, 0.5f, 0.5f);
            btn.style.borderRightColor = new Color(0.5f, 0.5f, 0.5f, 0.5f);

            retryContainer.Add(btn);
            container.Add(retryContainer);
        }

        /// <summary>
        /// 添加可展开/折叠的详情区域到消息气泡底部。
        /// 用于显示堆栈信息等长文本，默认折叠状态。
        /// </summary>
        /// <param name="title">折叠标题（如"堆栈信息"）</param>
        /// <param name="content">详情内容文本</param>
        public void AddExpandableDetail(string title, string content)
        {
            if (string.IsNullOrEmpty(content)) return;

            var container = _bubbleContent ?? _bubbleRoot ?? this;

            // 外层容器
            var detailContainer = new VisualElement();
            detailContainer.AddToClassList("error-detail-container");

            // 分隔线
            var separator = new VisualElement();
            separator.AddToClassList("error-detail-separator");
            detailContainer.Add(separator);

            // 折叠标题按钮
            var isExpanded = false;
            var headerBtn = new Button();
            headerBtn.AddToClassList("error-detail-header");
            headerBtn.text = $"▶ {title}";

            // 内容区域（默认隐藏）
            var contentLabel = new Label();
            contentLabel.AddToClassList("error-detail-content");
            contentLabel.text = content;
            contentLabel.selection.isSelectable = true;
            contentLabel.style.display = DisplayStyle.None;

            headerBtn.clicked += () =>
            {
                isExpanded = !isExpanded;
                headerBtn.text = isExpanded ? $"▼ {title}" : $"▶ {title}";
                contentLabel.style.display = isExpanded ? DisplayStyle.Flex : DisplayStyle.None;
            };

            detailContainer.Add(headerBtn);
            detailContainer.Add(contentLabel);
            container.Add(detailContainer);
        }

        #endregion

        #region 私有方法

        /// <summary>
        /// 设置流式输出模式。
        /// 隐藏静态 Label，创建 StreamingTextElement 替代。
        /// </summary>
        /// <param name="initialContent">初始内容（通常为空）</param>
        private void SetupStreamingMode(string initialContent)
        {
            // 隐藏静态内容 Label
            if (_contentLabel != null)
            {
                _contentLabel.style.display = DisplayStyle.None;
            }

            // 创建流式文本元素
            _streamingText = new StreamingTextElement();

            if (_bubbleContent != null)
            {
                _bubbleContent.Add(_streamingText);
            }
            else
            {
                Add(_streamingText);
            }

            // 如果有初始内容，追加显示
            if (!string.IsNullOrEmpty(initialContent))
            {
                _streamingText.AppendText(initialContent);
            }
        }

        /// <summary>
        /// 设置静态内容模式。
        /// 直接在 Label 中显示完整文本。
        /// </summary>
        /// <param name="content">消息内容</param>
        private void SetupStaticMode(string content)
        {
            if (_contentLabel != null)
            {
                // 静态模式也过滤 tool_call/tool_result 标签
                _contentLabel.text = ContentFilter.FilterCompleted(content ?? "");
            }
        }

        /// <summary>
        /// 获取角色的显示名称。
        /// </summary>
        /// <param name="role">角色标识</param>
        /// <returns>中文显示名称</returns>
        private static string GetRoleDisplayName(string role)
        {
            return role switch
            {
                "user" => "用户",
                "assistant" => "助手",
                "error" => "错误",
                _ => role
            };
        }

        /// <summary>
        /// 当 UXML 模板加载失败时，创建兜底布局。
        /// </summary>
        private void CreateFallbackLayout()
        {
            var bubbleRoot = new VisualElement { name = "bubble-root" };
            bubbleRoot.AddToClassList("message-bubble");

            var header = new VisualElement { name = "bubble-header" };
            header.style.flexDirection = FlexDirection.Row;
            header.style.marginBottom = 4;

            var roleLabel = new Label { name = "role-label" };
            roleLabel.style.fontSize = 10;
            header.Add(roleLabel);

            var spacer = new VisualElement { name = "header-spacer" };
            spacer.style.flexGrow = 1;
            header.Add(spacer);

            var timeLabel = new Label { name = "time-label" };
            timeLabel.style.fontSize = 9;
            header.Add(timeLabel);

            bubbleRoot.Add(header);

            var content = new VisualElement { name = "bubble-content" };
            var contentLabel = new Label { name = "content-label" };
            contentLabel.style.whiteSpace = WhiteSpace.Normal;
            contentLabel.style.fontSize = 13;
            // 启用文本选择，允许用户选中和复制文本（Unity 2022.2+）
            contentLabel.selection.isSelectable = true;
            content.Add(contentLabel);

            bubbleRoot.Add(content);
            Add(bubbleRoot);
        }

        #endregion
    }
}
