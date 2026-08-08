using AgentCore.Editor.L10n;
using AgentCore.Editor.L10n.UI;
using AgentCore.Editor.Core;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI
{
    /// <summary>
    /// ChatWindow 分部类 — 多语言 (L10n) 支持.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 职责:
    /// <list type="bullet">
    ///   <item>把 <see cref="LanguageSelector"/> 挂到工具栏右侧.</item>
    ///   <item>把 UXML 里硬编码的中文静态标签, 用 <see cref="Loc.Tr"/> 覆盖.</item>
    ///   <item>订阅 <see cref="LanguageManager.LanguageChanged"/>, 语言切换时刷新静态标签, 并按当前 AgentState 重新触发一次状态标签更新.</item>
    /// </list>
    /// </para>
    /// </remarks>
    public partial class ChatWindow
    {
        private LanguageSelector _languageSelector;

        /// <summary>
        /// 挂载语言选择器到工具栏右侧.
        /// </summary>
        private void MountLanguageSelector()
        {
            var toolbar = rootVisualElement.Q<VisualElement>("toolbar");
            if (toolbar == null) return;

            // 避免重复挂载 (CreateGUI 可能因 domain reload 多次调用)
            var existing = toolbar.Q<LanguageSelector>();
            if (existing != null)
            {
                _languageSelector = existing;
                return;
            }

            _languageSelector = new LanguageSelector();
            // 用负 marginRight 抵消 toolbar 的 padding-right (8px), 让下拉贴到 toolbar 最右缘.
            // 视觉一致性: toolbar 内其他左侧元素受 padding 保护, 只有右侧这个"工具类"控件贴边.
            _languageSelector.style.marginRight = -8;
            _languageSelector.style.marginLeft = 6;
            toolbar.Add(_languageSelector);
        }

        /// <summary>
        /// 用当前语言的 L10n 值覆盖 UXML 里的硬编码中文静态标签.
        /// UXML 里 uxml 硬编码的中文不能自动本地化, 需要在 C# 层显式覆盖.
        /// </summary>
        private void ApplyLocalizedStaticLabels()
        {
            // toolbar 标题
            var toolbarTitle = rootVisualElement.Q<Label>("title-label");
            if (toolbarTitle != null)
                toolbarTitle.text = Loc.Tr("chat.toolbar.title", "AgentCore");

            // 会话侧栏
            var sidebarTitle = rootVisualElement.Q<Label>("sidebar-title");
            if (sidebarTitle != null)
                sidebarTitle.text = Loc.Tr("session.sidebar.title", "会话列表");

            var newSessionBtn = rootVisualElement.Q<Button>("new-session-button");
            if (newSessionBtn != null)
            {
                newSessionBtn.text = Loc.Tr("session.button.new", "+ 新建");
                newSessionBtn.tooltip = Loc.Tr("session.button.newTooltip", "新建会话");
            }

            // 消息区跳到最新
            var scrollToBottomBtn = rootVisualElement.Q<Button>("scroll-to-bottom-button");
            if (scrollToBottomBtn != null)
            {
                scrollToBottomBtn.text = Loc.Tr("chat.input.scrollToLatest", "跳到最新");
                scrollToBottomBtn.tooltip = Loc.Tr("chat.input.scrollToLatestTooltip", "回到最新消息");
            }

            // 输入栏按钮
            var sendBtn = rootVisualElement.Q<Button>("send-button");
            if (sendBtn != null)
                sendBtn.text = Loc.Tr("chat.input.send", "发送");

            var cancelBtn = rootVisualElement.Q<Button>("cancel-button");
            if (cancelBtn != null)
                cancelBtn.text = Loc.Tr("chat.input.cancel", "取消");

            // v1.14.10: 思考强度下拉的选项文案（Auto/Off/Low/Med/High）随语言刷新。
            _reasoningLevelSelector?.RefreshDisplayNames();
        }

        /// <summary>
        /// 语言切换事件回调. 刷新静态标签 + 按当前状态重放一次状态标签更新.
        /// </summary>
        /// <param name="newLanguage">新的语言 code.</param>
        private void OnLanguageChanged(string newLanguage)
        {
            ApplyLocalizedStaticLabels();

            // 让状态标签立即用新语言重绘 — 复用 UpdateUIState 的 switch 逻辑
            if (_agentLoop != null)
            {
                UpdateUIState(_agentLoop.CurrentState);
            }

            // 会话列表里的时间戳 / 空列表提示等也需要重绘
            try
            {
                RefreshSessionList();
            }
            catch { /* 早期未初始化时忽略 */ }
        }
    }
}
