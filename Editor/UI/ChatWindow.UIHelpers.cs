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
        /// <param name="text">状态文本</param>
        /// <param name="isError">是否为错误状态（红色）</param>
        private void UpdateStatusLabel(string text, bool isError = false)
        {
            if (_agentStatusLine == null) return;
            // 非活跃状态：就绪、等待澄清、错误
            var isIdle = text == "就绪" || text.StartsWith("等待");
            _agentStatusLine.SetStatus(text, isError, isActive: !isError && !isIdle);
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
