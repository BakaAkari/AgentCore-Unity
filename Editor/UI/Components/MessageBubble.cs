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

        #endregion

        #region 私有字段

        /// <summary>内容文本 Label（非流式消息使用）</summary>
        private Label _contentLabel;

        /// <summary>流式文本元素（仅助手消息使用）</summary>
        private StreamingTextElement _streamingText;

        /// <summary>气泡内容容器</summary>
        private VisualElement _bubbleContent;

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
            var bubbleRoot = this.Q<VisualElement>("bubble-root");
            var roleLabel = this.Q<Label>("role-label");
            var timeLabel = this.Q<Label>("time-label");
            _contentLabel = this.Q<Label>("content-label");
            _bubbleContent = this.Q<VisualElement>("bubble-content");

            // 设置角色样式类
            if (bubbleRoot != null)
            {
                bubbleRoot.AddToClassList($"message-bubble--{role}");
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
            // 最终化时过滤 tool_call/tool_result 标签
            var filtered = ContentFilter.FilterCompleted(fullContent ?? "");

            if (_streamingText != null)
            {
                _streamingText.SetFinalText(filtered);
            }
            else if (_contentLabel != null)
            {
                _contentLabel.text = filtered;
            }

            _isStreaming = false;
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
            content.Add(contentLabel);

            bubbleRoot.Add(content);
            Add(bubbleRoot);
        }

        #endregion
    }
}
