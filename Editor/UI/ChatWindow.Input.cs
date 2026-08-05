using System;
using AgentCore.Editor.Core;
using AgentCore.Editor.Utils;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI
{
    public partial class ChatWindow
    {
        #region 用户操作

        /// <summary>
        /// 发送按钮点击处理。
        /// 获取输入文本，清空输入框，添加用户消息气泡，调用 AgentLoop 发送消息。
        /// </summary>
        private void OnSendClicked()
        {
            var text = _inputField?.value?.Trim();
            if (string.IsNullOrEmpty(text)) return;

            // ask_user 自由文本答案拦截：Agent 正等待用户"自己描述"的回答时，
            // 这条输入应作为 ask_user 的答案回传，而非当成新一轮消息发送。
            if (_awaitingFreeTextAnswer)
            {
                if (TryConsumeFreeTextAnswer(text))
                {
                    _inputField.value = "";
                    _inputField.Focus();
                    return;
                }
            }

            if (_agentLoop == null)
            {
                AgentCoreLog.Error("[AgentCore] AgentLoop is not initialized.");
                return;
            }

            // #10：初始化改异步，Bootstrap 加载完成前拦截发送，避免 system prompt 尚未就绪即发起对话。
            if (!_agentLoop.IsInitialized)
            {
                // [DIAG-BUG1] 记录"点击发送但被初始化未完成拦截"的确切时刻，
                // 与 InitializeAsync/BootstrapLoader 的 [DIAG] 耗时日志时间戳对照，
                // 可判断当时卡在哪个阶段。
                AgentCoreLog.Warning($"[AgentCore][DIAG] OnSendClicked: BLOCKED because AgentLoop not initialized yet, at {DateTime.Now:HH:mm:ss.fff}");
                UpdateStatusLabel(AgentCore.Editor.L10n.Loc.Tr("chat.status.initializing", "初始化中…"), false);
                return;
            }

            // ADR: self-challenge-model-tier-escape §3.4 B1 — 与 AgentLoop.SendMessageAsync gate 对齐
            //   Idle → 正常新一轮
            //   WaitingForClarification → 走 Node A Continuation
            //   ReviewingAnswer → 拒绝(Node B 运行中, 需隔离本轮数据)
            if (_agentLoop.CurrentState != AgentState.Idle &&
                _agentLoop.CurrentState != AgentState.WaitingForClarification)
            {
                AgentCoreLog.Warning($"[AgentCore] Cannot send message while agent is in {_agentLoop.CurrentState} state.");
                return;
            }

            // 记录最后一条用户消息（用于错误重试）
            _lastUserMessage = text;

            // 清空输入框
            _inputField.value = "";
            _inputField.Focus();

            // 添加用户消息气泡：ID 先在 UI 层生成，再传给 SendMessageAsync 让它复用同一个 ID
            // 作为真实 turn.Id——否则气泡 MessageId 和 turn.Id 不一致，Fork 按钮找不到对应的 turn。
            var userTurnId = Guid.NewGuid().ToString();
            AddUserMessage(text, userTurnId);

            // 用户主动发送新消息 → 强制回到底部并恢复自动追底
            ScrollToBottom(force: true);

            // 立刻显示 pending 占位气泡（解决"点击发送 → 5-30 秒 UI 无反应"的感知问题）
            ShowPendingIndicator(AgentCore.Editor.L10n.Loc.Tr("chat.pending.thinking", "思考中"));

            // 异步发送消息
            AsyncHelper.RunAsync(
                () => _agentLoop.SendMessageAsync(text, userTurnId),
                onError: ex =>
                {
                    AgentCoreLog.Error($"[AgentCore] SendMessage error: {ex.Message}");
                    DismissPendingIndicator();
                }
            );
        }

        /// <summary>
        /// 取消按钮点击处理。
        /// 取消当前正在进行的 LLM 操作。
        /// </summary>
        private void OnCancelClicked()
        {
            _agentLoop?.Cancel();
            AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] User cancelled current operation.");
        }

        /// <summary>
        /// 输入框键盘事件处理。
        /// <list type="bullet">
        ///   <item>Enter — 发送消息（IME 组字时不发送，交给输入法上屏）</item>
        ///   <item>Ctrl+Enter — 输入框内换行</item>
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
                case KeyCode.Return or KeyCode.KeypadEnter when evt.ctrlKey:
                    // Ctrl+Enter -> 换行。
                    // Ctrl+Enter 不是 TextField 内建 KeyboardTextEditor 会响应的组合键
                    //（内建只处理 Enter 换行 / Shift+Enter），因此这里自己插入换行不会与
                    // 内建的 default action 冲突，也不会触发 Shift+Enter 那种拦不住的全选。
                    evt.PreventDefault();
                    evt.StopPropagation();
                    InsertNewlineAtCursor();
                    break;

                case KeyCode.Return or KeyCode.KeypadEnter when !evt.ctrlKey:
                    // Enter（不含 Ctrl）-> 发送消息。
                    // IME 守卫：中文/日文/韩文输入法在候选框按 Enter 是"确认选词/上屏"，
                    // 不应触发发送。UnityEngine.Input.compositionString 在组字未提交时非空，
                    // 用它拦截 IME 确认阶段的 Enter —— 此时直接 break，既不发送也不拦截默认行为，
                    // 让输入法把候选内容上屏到输入框。组字提交后 compositionString 清空，
                    // 用户再按 Enter 才真正发送。
                    if (IsImeComposing())
                    {
                        break;
                    }
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

        /// <summary>
        /// 判断当前是否处于输入法（IME）组字状态。
        /// <para>
        /// 中日韩输入法在候选词未确认时，<see cref="UnityEngine.Input.compositionString"/>
        /// 会保存正在组字的临时串（未提交）。此时用户按 Enter 是"确认选词"而非"发送消息"，
        /// 必须拦截，否则会把半句话误发出去 —— 这是中文用户最高频的输入痛点。
        /// </para>
        /// <para>
        /// 组字提交后 compositionString 立即清空，用户下一次按 Enter 才真正发送，符合直觉。
        /// UnityEngine.Input 在 Editor 环境下可用；异常时保守返回 false（不拦截，退回原有发送行为）。
        /// </para>
        /// </summary>
        /// <returns>正在组字返回 true，否则 false。</returns>
        private static bool IsImeComposing()
        {
            try
            {
                return !string.IsNullOrEmpty(UnityEngine.Input.compositionString);
            }
            catch
            {
                // 某些平台/上下文下访问 Input 可能抛异常，保守返回 false
                return false;
            }
        }

        /// <summary>
        /// 窗口级键盘事件处理（输入框未聚焦时也能响应）。
        /// 仅处理非文本输入类快捷键，避免干扰输入框的正常输入。
        /// 当事件已被输入框处理（StopPropagation）时，此处不会再收到。
        /// <list type="bullet">
        ///   <item>Escape — 取消当前操作</item>
        ///   <item>Ctrl+N — 新建会话</item>
        ///   <item>Ctrl+Shift+E — 导出当前会话</item>
        ///   <item>Ctrl+/ 或 Ctrl+? — 聚焦输入框</item>
        /// </list>
        /// </summary>
        /// <param name="evt">键盘事件</param>
        private void OnWindowKeyDown(KeyDownEvent evt)
        {
            // 如果输入框已聚焦，由 OnInputFieldKeyDown 处理，此处跳过
            // （输入框的 StopPropagation 会阻止事件冒泡到 rootVisualElement）
            // 但 rootVisualElement 注册的是捕获阶段之后的冒泡阶段，
            // 输入框 StopPropagation 后此处仍可能收到，需要手动判断

            switch (evt.keyCode)
            {
                case KeyCode.Escape:
                    // Escape -> 取消当前操作（全局有效）
                    if (_agentLoop?.CurrentState != AgentState.Idle)
                    {
                        evt.PreventDefault();
                        evt.StopPropagation();
                        OnCancelClicked();
                    }
                    break;

                case KeyCode.N when evt.ctrlKey && !evt.shiftKey:
                    // Ctrl+N -> 新建会话（全局有效）
                    evt.PreventDefault();
                    evt.StopPropagation();
                    OnNewSessionClicked();
                    break;

                case KeyCode.E when evt.ctrlKey && evt.shiftKey:
                    // Ctrl+Shift+E -> 导出当前会话（全局有效）
                    evt.PreventDefault();
                    evt.StopPropagation();
                    ShowExportMenu();
                    break;

                case KeyCode.Slash when evt.ctrlKey:
                case KeyCode.Question when evt.ctrlKey:
                    // Ctrl+/ 或 Ctrl+? -> 聚焦输入框
                    evt.PreventDefault();
                    evt.StopPropagation();
                    _inputField?.Focus();
                    break;
            }
        }

        #endregion

        #region 外部注入 API (ContextIngest / 扩展)

        /// <summary>
        /// 将文本追加到输入框光标位置。不会清空已有输入。
        /// 主要供 <see cref="ContextIngestEntry"/> 全局快捷键注入 Context 使用。
        /// </summary>
        /// <param name="text">要注入的文本（通常是已格式化的 markdown 块）</param>
        public void AppendToInputField(string text)
        {
            if (_inputField == null || string.IsNullOrEmpty(text)) return;

            var current = _inputField.value ?? string.Empty;
            var cursor = _inputField.cursorIndex;

            // 边界修正（cursor 可能超出当前 value 长度）
            if (cursor < 0 || cursor > current.Length) cursor = current.Length;

            var head = current.Substring(0, cursor);
            var tail = current.Substring(cursor);

            // 头部如果非空且不以换行结尾，追加一个换行避免粘连
            if (head.Length > 0 && !head.EndsWith("\n")) head += "\n";

            var newValue = head + text + tail;
            _inputField.value = newValue;
            _inputField.Focus();

            // 将光标定位到注入内容之后（用户可以直接继续输入）
            var newCursor = head.Length + text.Length;
            try
            {
                _inputField.cursorIndex = newCursor;
                _inputField.selectIndex = newCursor;
            }
            catch
            {
                // 某些 Unity 版本上 cursorIndex 只读或延迟生效，忽略即可
            }
        }

        /// <summary>
        /// 聚焦输入框（用于快捷键触发但无内容注入的场景）。
        /// </summary>
        public void FocusInputField()
        {
            _inputField?.Focus();
        }

        /// <summary>
        /// 在当前光标处插入换行符（供 Ctrl+Enter 使用）。
        /// <para>
        /// 若存在选区（cursorIndex ≠ selectIndex），用换行替换整个选区，符合"选中后按键覆盖"的直觉；
        /// 插入后光标定位到换行之后，用户可直接续写下一行。
        /// </para>
        /// <para>
        /// 用 <see cref="TextInputBaseField{T}.SetValueWithoutNotify"/> 赋值而非 value setter + Focus()，
        /// 避免触发内建通知链引发的意外全选；赋值后立即 + 延迟一帧两次强制钉住光标、清除选区，
        /// 覆盖内建可能的选区重置。
        /// </para>
        /// </summary>
        private void InsertNewlineAtCursor()
        {
            if (_inputField == null) return;

            var current = _inputField.value ?? string.Empty;
            var cursor = _inputField.cursorIndex;
            var select = _inputField.selectIndex;

            // 归一化选区边界（cursor/select 可能任一在前，且可能越界）
            var start = System.Math.Min(cursor, select);
            var end = System.Math.Max(cursor, select);
            if (start < 0) start = 0;
            if (start > current.Length) start = current.Length;
            if (end < 0) end = 0;
            if (end > current.Length) end = current.Length;

            var head = current.Substring(0, start);
            var tail = current.Substring(end);
            var newCursor = head.Length + 1;

            _inputField.SetValueWithoutNotify(head + "\n" + tail);

            SetInputCursor(newCursor);
            _inputField.schedule.Execute(() => SetInputCursor(newCursor));
        }

        /// <summary>把输入框光标钉在指定位置并清除选区（cursorIndex == selectIndex）。</summary>
        private void SetInputCursor(int index)
        {
            if (_inputField == null) return;
            try
            {
                _inputField.cursorIndex = index;
                _inputField.selectIndex = index;
            }
            catch
            {
                // 某些 Unity 版本上 cursorIndex 只读或延迟生效，忽略即可
            }
        }

        #endregion
    }
}
