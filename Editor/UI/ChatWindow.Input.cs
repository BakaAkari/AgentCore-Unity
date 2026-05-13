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
                Debug.LogError("[AgentCore] AgentLoop is not initialized.");
                return;
            }

            if (_agentLoop.CurrentState != AgentState.Idle)
            {
                Debug.LogWarning("[AgentCore] Cannot send message while agent is busy.");
                return;
            }

            // 记录最后一条用户消息（用于错误重试）
            _lastUserMessage = text;

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
    }
}
