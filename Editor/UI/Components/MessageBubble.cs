using System;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using AgentCore.Editor.Utils;

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

        #region 静态缓存

        /// <summary>缓存的 UXML 模板</summary>
        private static VisualTreeAsset _cachedUxml;

        /// <summary>缓存的 USS 样式</summary>
        private static StyleSheet _cachedUss;

        /// <summary>是否已加载静态资源</summary>
        private static bool _assetsLoaded;

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

        /// <summary>
        /// 最新一次的完整文本内容（未经 ContentFilter / Markdown 转换的原始文本）。
        /// 供"复制气泡内容"按钮读取；用 StreamingTextElement 的话 UI 里保留的是已渲染 block，
        /// 无法反向抠出 markdown，因此本地缓存原始文本是最可靠的做法。
        /// </summary>
        private string _lastFullContent = string.Empty;
        private StringBuilder _lastFullContentBuilder;

        /// <summary>复制按钮引用（如果 UXML 中存在）</summary>
        private Button _copyButton;

        /// <summary>底部资源引用栏（仅 assistant 消息使用，含消息里出现的文件/GO 引用 chip）</summary>
        private MessageReferenceBar _referenceBar;

        /// <summary>复制按钮闪回原文的定时任务句柄</summary>
        private IVisualElementScheduledItem _copyResetTask;

        #endregion

        #region 公开属性 (扩展)

        /// <summary>
        /// 当前气泡显示的完整原始文本内容（供外部读取或复制使用）。
        /// </summary>
        public string RawContent => _lastFullContentBuilder?.ToString() ?? _lastFullContent ?? string.Empty;

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

            // 加载 UXML 模板（静态缓存）
            if (!_assetsLoaded)
            {
                _cachedUxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
                _cachedUss = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
                _assetsLoaded = true;
            }
            
            if (_cachedUxml != null)
            {
                _cachedUxml.CloneTree(this);
            }
            else
            {
                AgentCoreLog.Warning($"[AgentCore] MessageBubble UXML not found at: {UxmlPath}, using fallback layout.");
                CreateFallbackLayout();
            }

            // 加载 USS 样式（静态缓存）
            if (_cachedUss != null)
            {
                this.styleSheets.Add(_cachedUss);
            }

            // 查询 UI 元素引用
            _bubbleRoot = this.Q<VisualElement>("bubble-root");
            var roleLabel = this.Q<Label>("role-label");
            var timeLabel = this.Q<Label>("time-label");
            _contentLabel = this.Q<Label>("content-label");
            _bubbleContent = this.Q<VisualElement>("bubble-content");
            _copyButton = this.Q<Button>("copy-button");

            // 启用内容文本选择，允许用户选中和复制文本（Unity 2022.2+）
            if (_contentLabel != null)
            {
                _contentLabel.selection.isSelectable = true;
            }

            // 初始化复制按钮（user 角色不显示——用户已经知道自己输入了什么）
            SetupCopyButton();

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
            // v1.6.5: 用 StringBuilder 避免 O(n) 字符串拼接
            _lastFullContentBuilder ??= new StringBuilder();
            _lastFullContentBuilder.Append(token ?? string.Empty);
        }

        /// <summary>
        /// 最终化消息内容。
        /// 将流式输出模式切换为静态模式，设置完整的最终文本。
        /// </summary>
        /// <param name="fullContent">完整的消息内容</param>
        public void FinalizeContent(string fullContent)
        {
            var content = fullContent ?? "";
            _lastFullContent = content;
            _lastFullContentBuilder?.Clear();
            _lastFullContentBuilder = null;

            if (_streamingText != null)
            {
                // SetFinalText 内部会调用 FilterCompleted（含 FormatMarkdown），不要重复过滤
                _streamingText.SetFinalText(content);
            }
            else if (_contentLabel != null)
            {
                // 静态 Label 需要手动过滤（FilterCompleted 是 fallback string 路径）
#pragma warning disable CS0618 // FilterCompleted is intentional for plain-text Label
                _contentLabel.text = ContentFilter.FilterCompleted(content);
#pragma warning restore CS0618
            }

            _isStreaming = false;

            // D2: 提取消息中的文件/GameObject 引用并渲染为可点击 chip 栏
            EnsureReferenceBar();
            _referenceBar?.Rebuild(content);

            // v1.4.0 fix: SetFinalText 动态添加 block 元素（表格、列表等）后，Unity UI Toolkit
            // 的 layout 计算有时会出现"父容器 background 只覆盖到初始 height"的问题，导致后添加
            // 的 block 溢出 bubble 灰色背景。强制标记 layout 重算 + 下一帧再刷一次以确保 resolvedStyle
            // 更新。
            ForceLayoutRefresh();
        }

        /// <summary>
        /// v1.4.0 — Force parent bubble height to match child block container height.
        /// <para>
        /// Root cause (confirmed via UI Debugger on Unity 2022.3.50f1):
        /// After <see cref="StreamingTextElement.SetFinalText"/> dynamically adds block
        /// elements (table/list/code), the <c>#bubble-content</c> resolved height gets
        /// stuck at its initial flex layout value (~377px) while its child block
        /// container's real height is 450+px. USS <c>align-items:stretch + flex-shrink:0
        /// + height:auto</c> only partially mitigates this — Unity 2022.3's UI Toolkit
        /// layout engine has a known issue where GeometryChangedEvent does not properly
        /// bubble up multi-level dynamic additions.
        /// </para>
        /// <para>
        /// Fix: explicitly forward the child block container's resolvedStyle.height to
        /// bubble-content via inline style. This bypasses the layout cache entirely.
        /// Runs once after SetFinalText and re-runs on subsequent GeometryChangedEvent
        /// (covers late layout passes and window resize).
        /// </para>
        /// </summary>
        private void ForceLayoutRefresh()
        {
            if (_streamingText == null || _bubbleContent == null)
            {
                _bubbleRoot?.MarkDirtyRepaint();
                return;
            }

            // Register a persistent listener on the streaming element:
            // whenever its geometry changes (block layout completed, or window resized),
            // sync the parent bubble-content's height to fit the streaming content.
            _streamingText.UnregisterCallback<GeometryChangedEvent>(OnStreamingGeometryChanged);
            _streamingText.RegisterCallback<GeometryChangedEvent>(OnStreamingGeometryChanged);

            // Fire the sync immediately (in case geometry is already resolved) and
            // again after a short delay to cover the multi-frame layout settle window.
            SyncBubbleContentHeight();
            schedule.Execute(SyncBubbleContentHeight).StartingIn(16);
            schedule.Execute(SyncBubbleContentHeight).StartingIn(64);
        }

        /// <summary>
        /// v1.4.0 — GeometryChangedEvent handler on _streamingText. Fired whenever the
        /// streaming element's layout changes (e.g. block added, text wrapped).
        /// </summary>
        private void OnStreamingGeometryChanged(GeometryChangedEvent evt)
        {
            SyncBubbleContentHeight();
        }

        /// <summary>
        /// v1.6.5 — Read the streaming element's resolved height and sync it to
        /// bubble-content's minHeight. Bidirectional: grows AND shrinks.
        /// <para>
        /// v1.4.0 only grew (streamHeight > current → set minHeight). When block mode
        /// re-rendered content more compactly than the streaming peak, minHeight stayed
        /// stuck at the peak → large empty space at bubble bottom.
        /// </para>
        /// <para>
        /// v1.7.x FIX — 死循环根治：此方法在 GeometryChangedEvent 里反向写
        /// minHeight，写样式又会触发 layout → 可能再次触发 GeometryChangedEvent。
        /// 在极窄窗口宽度下，文本换行导致 streamHeight 在相邻两值间因亚像素舍入
        /// 反复横跳（如 448↔450），旧的 1px 容差永远满足 → 无限回调 → 主线程卡死。
        /// 修复：(1) 防重入 flag；(2) 容差放大到 8px 吸收亚像素/单行抖动；
        /// (3) 只在"真正显著变化"时写入，且写入后短暂抑制再次响应。
        /// </para>
        /// </summary>
        private bool _syncingHeight;
        private float _lastSyncedHeight = -1f;

        private void SyncBubbleContentHeight()
        {
            if (_streamingText == null || _bubbleContent == null) return;

            // 防重入：写 minHeight 引发的 layout 不应递归回到这里
            if (_syncingHeight) return;

            float streamHeight = _streamingText.resolvedStyle.height;
            if (float.IsNaN(streamHeight) || streamHeight <= 0f) return;

            // 与上次已写入的目标值比较（而不是当前 resolvedStyle），
            // 避免"写入→resolvedStyle 更新→再次比较→再写"的自激振荡。
            float reference = _lastSyncedHeight >= 0f
                ? _lastSyncedHeight
                : _bubbleContent.resolvedStyle.height;

            float diff = Mathf.Abs(streamHeight - reference);

            // 容差放大到 8px：吸收单行文本换行 / 亚像素舍入造成的高度抖动，
            // 这是打破反馈循环的关键——低于此阈值的变化一律忽略。
            const float tolerance = 8f;
            if (diff <= tolerance) return;

            _syncingHeight = true;
            try
            {
                _bubbleContent.style.minHeight = streamHeight;
                _lastSyncedHeight = streamHeight;
                _bubbleContent.MarkDirtyRepaint();
                _bubbleRoot?.MarkDirtyRepaint();
            }
            finally
            {
                _syncingHeight = false;
            }
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
            btn.text = " 重试";
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
            headerBtn.text = $"> {title}";

            // 内容区域（默认隐藏）
            var contentLabel = new Label();
            contentLabel.AddToClassList("error-detail-content");
            contentLabel.text = content;
            contentLabel.selection.isSelectable = true;
            contentLabel.style.display = DisplayStyle.None;

            headerBtn.clicked += () =>
            {
                isExpanded = !isExpanded;
                headerBtn.text = isExpanded ? $"v {title}" : $"> {title}";
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
                _lastFullContent = initialContent;
            }
        }

        /// <summary>
        /// 设置静态内容模式。
        /// 助手消息使用 StreamingTextElement.SetFinalText 进行 block rendering（标题、代码块、表格等）。
        /// 其他角色直接在 Label 中显示纯文本。
        /// </summary>
        /// <param name="content">消息内容</param>
        private void SetupStaticMode(string content)
        {
            var text = content ?? "";
            _lastFullContent = text;

            // 助手消息：使用 block rendering 保留富文本格式（标题、代码块、表格等）
            if (Role == "assistant" && !string.IsNullOrEmpty(text))
            {
                // 隐藏静态 Label，使用 StreamingTextElement 的 block 模式渲染
                if (_contentLabel != null)
                {
                    _contentLabel.style.display = DisplayStyle.None;
                }

                _streamingText = new StreamingTextElement();

                if (_bubbleContent != null)
                {
                    _bubbleContent.Add(_streamingText);
                }
                else
                {
                    Add(_streamingText);
                }

                // 直接调用 SetFinalText 触发 block rendering
                _streamingText.SetFinalText(text);

                // v1.4.0 fix: 同 FinalizeContent，强制刷 layout 保证 bubble background 覆盖所有 block
                ForceLayoutRefresh();

                // D2: 静态渲染场景下同样构建引用栏（例如从会话恢复的历史消息）
                EnsureReferenceBar();
                _referenceBar?.Rebuild(text);
                return;
            }

            // 非助手消息或空内容：保持纯文本 Label
            if (_contentLabel != null)
            {
#pragma warning disable CS0618 // FilterCompleted is intentional for plain-text Label
                _contentLabel.text = ContentFilter.FilterCompleted(text);
#pragma warning restore CS0618
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
        /// 确保 _referenceBar 存在。只对 assistant 消息创建（用户消息通常不含引用）。
        /// </summary>
        private void EnsureReferenceBar()
        {
            if (Role != "assistant") return;
            if (_referenceBar != null) return;

            _referenceBar = new MessageReferenceBar();
            _referenceBar.style.display = DisplayStyle.None;

            if (_bubbleContent != null)
                _bubbleContent.Add(_referenceBar);
            else
                Add(_referenceBar);
        }

        /// <summary>
        /// 初始化气泡右上角的复制按钮。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 仅对 <c>assistant</c> 和 <c>error</c> 角色显示；<c>user</c> 角色隐藏
        /// （用户已经知道自己输入了什么，一键复制价值低）。
        /// </para>
        /// <para>
        /// 复制内容来源：<see cref="_lastFullContent"/> 缓存的原始文本（未经 Markdown 渲染），
        /// 保证复制粘贴到别处仍是 markdown 源码。
        /// </para>
        /// </remarks>
        private void SetupCopyButton()
        {
            if (_copyButton == null) return;

            // 只对 assistant 和 error 显示（user 消息用户已知道内容）
            if (Role != "assistant" && Role != "error")
            {
                _copyButton.style.display = DisplayStyle.None;
                return;
            }

            _copyButton.text = "复制";
            _copyButton.focusable = false; // 避免抢焦点导致文本选中丢失

            _copyButton.clicked += HandleCopyClicked;
        }

        /// <summary>
        /// 复制按钮点击处理：写入系统剪贴板 + 短暂显示"已复制"反馈。
        /// </summary>
        private void HandleCopyClicked()
        {
            if (_copyButton == null) return;

            var payload = _lastFullContent ?? string.Empty;
            try
            {
                EditorGUIUtility.systemCopyBuffer = payload;
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] Copy to clipboard failed: {ex.Message}");
                _copyButton.text = "失败";
                schedule.Execute(() => { if (_copyButton != null) _copyButton.text = "复制"; }).StartingIn(1200);
                return;
            }

            _copyButton.text = "已复制";

            // 1.2 秒后恢复原文
            _copyResetTask?.Pause();
            _copyResetTask = schedule.Execute(() =>
            {
                if (_copyButton != null) _copyButton.text = "复制";
            }).StartingIn(1200);
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

            var copyBtn = new Button { name = "copy-button" };
            copyBtn.AddToClassList("bubble-copy-button");
            header.Add(copyBtn);

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
