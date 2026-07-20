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

            if (_agentLoop == null)
            {
                AgentCoreLog.Error("[AgentCore] AgentLoop is not initialized.");
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

            // 添加用户消息气泡
            AddUserMessage(text);

            // 用户主动发送新消息 → 强制回到底部并恢复自动追底
            ScrollToBottom(force: true);

            // 立刻显示 pending 占位气泡（解决"点击发送 → 5-30 秒 UI 无反应"的感知问题）
            ShowPendingIndicator("思考中");

            // 异步发送消息
            AsyncHelper.RunAsync(
                () => _agentLoop.SendMessageAsync(text),
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
                    // IME 守卫：中文/日文/韩文输入法在候选框按 Enter 是"确认选词"，
                    // 不应触发发送。UnityEngine.Input.compositionString 在组字未提交时非空，
                    // 用它拦截 IME 确认阶段的 Enter。组字提交后 compositionString 会清空，
                    // 用户再按 Enter 才真正发送。
                    if (IsImeComposing())
                    {
                        // 处于输入法组字状态：不发送，也不拦截默认行为（让 TextField 完成选词/换行）
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

        #endregion
    }
}
