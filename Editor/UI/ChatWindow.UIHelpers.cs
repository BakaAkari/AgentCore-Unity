using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI
{
    /// <summary>
    /// ChatWindow 分部类 — UI 辅助方法。
    /// 包含状态行更新、发送按钮启用/禁用和取消按钮显示/隐藏。
    /// </summary>
    public partial class ChatWindow
    {
        #region UI 辅助方法

        /// <summary>
        /// 更新状态行文本和样式。
        /// </summary>
        /// <param name="text">状态文本 (通常已本地化)</param>
        /// <param name="isError">是否为错误状态（红色）</param>
        /// <remarks>
        /// v1.9.0+: 之前依赖硬编码中文文本("就绪" / "等待...")判断活跃与否, 多语言后失效.
        /// 改为默认视作活跃状态; 调用方需在明确处于 Idle/WaitingClarification 时使用
        /// <see cref="UpdateStatusLabel(string, bool, bool)"/> 重载并传 isActive=false.
        /// </remarks>
        private void UpdateStatusLabel(string text, bool isError = false)
        {
            UpdateStatusLabel(text, isError, isActive: !isError);
        }

        /// <summary>
        /// 更新状态行文本和样式 (显式指定是否活跃).
        /// </summary>
        /// <param name="text">状态文本 (通常已本地化)</param>
        /// <param name="isError">是否为错误状态（红色）</param>
        /// <param name="isActive">是否为活跃状态 (显示动画等). Idle / Waiting-类应传 false.</param>
        private void UpdateStatusLabel(string text, bool isError, bool isActive)
        {
            if (_agentStatusLine == null) return;
            _agentStatusLine.SetStatus(text, isError, isActive: isActive && !isError);
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
