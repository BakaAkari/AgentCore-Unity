using System.Collections.Generic;
using AgentCore.Editor.Utils;
using UnityEditor;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI
{
    /// <summary>
    /// ChatWindow 的 ask_user 交互实现（Agent 任务中途主动提问，挂起-唤醒模型）。
    /// <para>
    /// 与旧的同步 TaskCompletionSource 模型不同：loop 已在工具层截断结束（AgentState.WaitingForUserInput），
    /// 本面板只负责**渲染问题 + 收集答案**，用户应答后调 <c>_agentLoop.ResumeFromUserInput</c> 唤醒 loop。
    /// </para>
    /// <para>
    /// 关键行为（按用户设计）：**无超时、不自动拒绝、永久阻断**——用户可能只是没看到窗口，
    /// 面板一直保持到用户应答为止。与 SelfChallenge 模块完全无关。
    /// </para>
    /// <para>
    /// 交互：问题渲染为独立卡片 + N 个候选选项按钮 + 末尾固定「我自己描述」按钮。
    /// 点候选 → 直接以该选项文本唤醒；点「我自己描述」→ 收起选项、聚焦输入框，
    /// 用户下一条输入被拦截作为答案（而非当成新消息发送）。
    /// </para>
    /// </summary>
    public partial class ChatWindow
    {
        private VisualElement _askUserPanel;

        /// <summary>当前是否有活跃的 ask_user 提问（面板正在阻断）。</summary>
        private bool _askUserActive;

        /// <summary>当前挂起提问的问题文本（用于「我自己描述」提示 / reload 重建）。</summary>
        private string _askUserQuestion;

        /// <summary>「我自己描述」激活后，标记下一条用户输入应作为 ask_user 的答案而非新消息。</summary>
        private bool _awaitingFreeTextAnswer;

        /// <summary>
        /// domain reload 后：若 loop 有挂起的 ask_user 提问，恢复挂起标志并重建面板。
        /// 在 CreateGUI 的 TryRestoreSession 之后调用。
        /// </summary>
        private void TryRestorePendingAskUser()
        {
            if (_agentLoop == null || _askUserPanel == null) return;

            if (!_agentLoop.RestorePendingUserInputFromReload())
            {
                return; // 无挂起提问
            }

            // 重建面板：从 loop 恢复的问题/选项
            var question = _agentLoop.PendingUserInputQuestion;
            var options = _agentLoop.PendingUserInputOptions != null
                ? new List<string>(_agentLoop.PendingUserInputOptions)
                : null;
            AgentCoreLog.Info("[AgentCore][ask_user] Rebuilding panel after domain reload.");
            ShowUserQuery(question, options);
        }

        /// <summary>
        /// 创建 ask_user 面板容器（插在输入区上方）。在 CreateGUI 内调用，与 InitializeToolConfirmationPanel 相邻。
        /// </summary>
        private void InitializeAskUserPanel(VisualElement chatArea, VisualElement inputArea)
        {
            if (chatArea == null || inputArea == null)
            {
                return;
            }

            _askUserPanel = new VisualElement { name = "ask-user-panel" };
            _askUserPanel.AddToClassList("tool-confirmation-panel"); // 复用确认面板的样式
            _askUserPanel.AddToClassList("ask-user-panel");
            _askUserPanel.style.display = DisplayStyle.None;

            var inputIndex = chatArea.IndexOf(inputArea);
            if (inputIndex >= 0)
            {
                chatArea.Insert(inputIndex, _askUserPanel);
            }
            else
            {
                chatArea.Add(_askUserPanel);
            }
        }

        /// <summary>
        /// AgentLoop.OnUserQueryRaised 事件处理器：Agent 调用 ask_user、loop 已截断挂起，弹出选项面板。
        /// 事件可能在非主线程触发，marshal 回主线程渲染。
        /// </summary>
        private void HandleUserQueryRaised(string toolCallId, string question, List<string> options)
        {
            // marshal 回主线程（UI 操作必须在主线程）
            EditorApplication.delayCall += () => ShowUserQuery(question, options);
        }

        /// <summary>
        /// 渲染 ask_user 面板。question/options 来自 loop 事件或 reload 重建。
        /// </summary>
        private void ShowUserQuery(string question, List<string> options)
        {
            if (_askUserPanel == null)
            {
                AgentCoreLog.Warning("[AgentCore][ask_user] Panel not initialized; cannot show query.");
                return;
            }

            _askUserActive = true;
            _askUserQuestion = question ?? "";
            _awaitingFreeTextAnswer = false;

            _askUserPanel.Clear();
            _askUserPanel.style.display = DisplayStyle.Flex;

            var header = new VisualElement();
            header.AddToClassList("tool-confirmation-header");
            var title = new Label(AgentCore.Editor.L10n.Loc.Tr("askUser.title", "AI 需要你的决定"));
            title.AddToClassList("tool-confirmation-title");
            header.Add(title);
            _askUserPanel.Add(header);

            var questionLabel = new Label(_askUserQuestion);
            questionLabel.AddToClassList("tool-confirmation-description");
            _askUserPanel.Add(questionLabel);

            var buttons = new VisualElement();
            buttons.AddToClassList("ask-user-options");

            // 候选选项按钮（逐个以原文唤醒）
            if (options != null)
            {
                foreach (var opt in options)
                {
                    var optionText = opt; // 闭包捕获局部
                    if (string.IsNullOrEmpty(optionText)) continue;
                    var btn = new Button(() => AnswerWithPreset(optionText))
                    {
                        text = optionText
                    };
                    btn.AddToClassList("tool-confirmation-button");
                    btn.AddToClassList("ask-user-option-button");
                    buttons.Add(btn);
                }
            }

            // 末尾固定「我自己描述」按钮
            var freeText = new Button(BeginFreeTextAnswer)
            {
                text = AgentCore.Editor.L10n.Loc.Tr("askUser.customOption", "我自己描述...")
            };
            freeText.AddToClassList("tool-confirmation-button");
            freeText.AddToClassList("ask-user-freetext-button");
            buttons.Add(freeText);

            _askUserPanel.Add(buttons);

            // 无超时、永久阻断：明确告知用户面板会一直等
            var meta = new Label(AgentCore.Editor.L10n.Loc.Tr(
                "askUser.waitingMeta",
                "等待你的决定 · 面板会一直保留直到你回答（不会超时）"));
            meta.AddToClassList("ask-user-meta");
            _askUserPanel.Add(meta);

            UpdateStatusLabel(AgentCore.Editor.L10n.Loc.Tr("chat.status.waitingUserDecision", "AI 正在等待你的决定..."));
            ScrollToBottom(force: true);
        }

        /// <summary>点候选选项：以该选项文本唤醒 loop。</summary>
        private void AnswerWithPreset(string optionText)
        {
            if (!_askUserActive) return;
            FinishUserQuery();
            _agentLoop?.ResumeFromUserInput(optionText, wasPresetOption: true);
        }

        /// <summary>
        /// 点击「我自己描述」：收起选项按钮，聚焦输入框，拦截下一条输入作为答案。
        /// 面板保留问题文本作为提示。
        /// </summary>
        private void BeginFreeTextAnswer()
        {
            if (!_askUserActive) return;

            _awaitingFreeTextAnswer = true;

            _askUserPanel.Clear();

            var header = new VisualElement();
            header.AddToClassList("tool-confirmation-header");
            var title = new Label(AgentCore.Editor.L10n.Loc.Tr("askUser.title", "AI 需要你的决定"));
            title.AddToClassList("tool-confirmation-title");
            header.Add(title);
            _askUserPanel.Add(header);

            if (!string.IsNullOrEmpty(_askUserQuestion))
            {
                var q = new Label(_askUserQuestion);
                q.AddToClassList("tool-confirmation-description");
                _askUserPanel.Add(q);
            }

            var hint = new Label(AgentCore.Editor.L10n.Loc.Tr("askUser.freeTextHint", "请在下方输入框输入你的答案并发送"));
            hint.AddToClassList("ask-user-meta");
            _askUserPanel.Add(hint);

            _inputField?.Focus();
        }

        /// <summary>
        /// 由 OnSendClicked 在 _awaitingFreeTextAnswer=true 时调用：把输入文本作为答案唤醒 loop，
        /// 拦截正常的 SendMessageAsync 流程。返回 true 表示已消费该输入。
        /// </summary>
        private bool TryConsumeFreeTextAnswer(string text)
        {
            if (!_awaitingFreeTextAnswer || !_askUserActive)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                // 空输入不消费，让用户重新输入（保持等待态）
                return true;
            }

            FinishUserQuery();
            _agentLoop?.ResumeFromUserInput(text.Trim(), wasPresetOption: false);
            return true;
        }

        /// <summary>清理面板与状态（应答完成 / 放弃时调用）。不唤醒 loop。</summary>
        private void FinishUserQuery()
        {
            _askUserActive = false;
            _awaitingFreeTextAnswer = false;
            _askUserQuestion = null;
            HideAskUserPanel();
        }

        private void HideAskUserPanel()
        {
            if (_askUserPanel == null)
            {
                return;
            }

            _askUserPanel.Clear();
            _askUserPanel.style.display = DisplayStyle.None;
        }

        /// <summary>
        /// 窗口销毁 / 会话切换时调用：放弃当前挂起提问（清 UI + 通知 loop 放弃挂起状态）。
        /// </summary>
        private void ClearPendingUserQuery()
        {
            if (_askUserActive)
            {
                _agentLoop?.AbandonPendingUserInput();
            }
            FinishUserQuery();
        }
    }
}
