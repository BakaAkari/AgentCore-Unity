using AgentCore.Editor.Core;
using AgentCore.Editor.UI.Components;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI
{
    /// <summary>
    /// ChatWindow 分部类 — 工具调用 UI 处理。
    /// 包含工具调用分组管理、工具卡片状态更新和轮次分隔线。
    /// </summary>
    public partial class ChatWindow
    {
        #region 工具调用 UI 处理

        /// <summary>
        /// 确保当前存在一个工具调用分组容器。
        /// 如果不存在，创建一个新的并添加到消息容器。
        /// </summary>
        /// <returns>当前的工具调用分组容器</returns>
        private ToolCallGroup EnsureToolCallGroup()
        {
            if (_currentToolCallGroup == null)
            {
                if (!string.IsNullOrEmpty(_currentAssistantTurnId))
                {
                    var turnView = EnsureAssistantTurnView(_currentAssistantTurnId);
                    _currentToolCallGroup = turnView?.EnsureToolGroup();
                    Debug.Log($"[AgentCore.UI] EnsureToolCallGroup: 新建 ToolCallGroup, 添加到 AssistantTurnView");
                }
                else
                {
                    _currentToolCallGroup = new ToolCallGroup();
                    _messageListManager?.AddItem(_currentToolCallGroup);
                    Debug.Log($"[AgentCore.UI] EnsureToolCallGroup: 无 assistant turn，降级添加到 MessageListManager");
                }
            }
            return _currentToolCallGroup;
        }

        /// <summary>
        /// 获取工具调用的唯一 key。优先使用 ToolCallId，缺失时用计数器生成。
        /// 仅在 HandleToolCallStarted 中调用（会递增计数器）。
        /// </summary>
        private string GetToolCallKey(AgentEvent evt)
        {
            if (!string.IsNullOrEmpty(evt.ToolCallId))
                return evt.ToolCallId;
            // fallback: 用工具名+计数器生成唯一 key
            return $"{evt.ToolName}_{_toolCallCounter++}";
        }

        /// <summary>
        /// 在 _activeToolCards 中查找与事件匹配的 key。
        /// 优先精确匹配 ToolCallId，找不到则按 ToolName 前缀匹配（兼容 toolName_{N} 格式）。
        /// 用于 HandleToolCallCompleted / HandleToolCallFailed。
        /// </summary>
        private string FindToolCardKey(AgentEvent evt)
        {
            // 1. 优先用 ToolCallId 精确匹配
            if (!string.IsNullOrEmpty(evt.ToolCallId) && _activeToolCards.ContainsKey(evt.ToolCallId))
                return evt.ToolCallId;

            // 2. fallback: 按 ToolName 前缀匹配（key 可能是 "toolName_0", "toolName_1" 等）
            //    取最后一个匹配项（最近添加的）
            string matched = null;
            if (!string.IsNullOrEmpty(evt.ToolName))
            {
                var prefix = evt.ToolName + "_";
                foreach (var key in _activeToolCards.Keys)
                {
                    if (key == evt.ToolName || key.StartsWith(prefix))
                        matched = key;
                }
            }

            return matched;
        }

        /// <summary>
        /// 处理工具调用开始事件：创建 ToolCallCard 并添加到分组容器。
        /// </summary>
        /// <param name="evt">工具调用开始事件</param>
        private void HandleToolCallStarted(AgentEvent evt)
        {
            Debug.Log($"[AgentCore.UI] HandleToolCallStarted: tool={evt.ToolName}, toolCallId={evt.ToolCallId ?? "(null)"}, messageId={evt.MessageId ?? "(null)"}");

            var group = EnsureToolCallGroup();

            var card = new ToolCallCard(evt.ToolName, evt.ToolArguments);
            card.SetStatus(ToolCallStatus.Running, "执行中...");
            group.AddToolCard(card);

            // 用 ToolCallId 作为 key（支持同名工具多次调用）
            var key = GetToolCallKey(evt);
            _activeToolCards[key] = card;
            Debug.Log($"[AgentCore.UI] HandleToolCallStarted: card 已添加, key={key}, _activeToolCards.Count={_activeToolCards.Count}");
            ScrollToBottom(force: true); // 新工具调用卡片添加，强制滚动到底部
        }

        /// <summary>
        /// 处理工具调用完成事件：更新对应的 ToolCallCard 为完成状态。
        /// </summary>
        /// <param name="evt">工具调用完成事件</param>
        private void HandleToolCallCompleted(AgentEvent evt)
        {
            var key = FindToolCardKey(evt);
            Debug.Log($"[AgentCore.UI] HandleToolCallCompleted: tool={evt.ToolName}, key={key ?? "(no match)"}, found={key != null}");

            if (key != null && _activeToolCards.TryGetValue(key, out var card))
            {
                var timeText = evt.ExecutionTimeMs > 0
                    ? $" ({evt.ExecutionTimeMs:F0}ms)"
                    : "";
                card.SetStatus(ToolCallStatus.Completed, $"完成{timeText}");

                if (!string.IsNullOrEmpty(evt.ToolResult))
                {
                    // 截断过长的结果
                    var result = evt.ToolResult.Length > 200
                        ? evt.ToolResult.Substring(0, 200) + "..."
                        : evt.ToolResult;
                    card.SetDetails(result);
                }

                _activeToolCards.Remove(key);

                // 通知分组容器更新统计和折叠状态
                _currentToolCallGroup?.NotifyToolStatusChanged();
            }
            else
            {
                Debug.LogWarning($"[AgentCore.UI] HandleToolCallCompleted: 未找到 key={key} 的卡片, 当前 keys=[{string.Join(", ", _activeToolCards.Keys)}]");
            }
        }

        /// <summary>
        /// 处理工具调用失败事件：更新对应的 ToolCallCard 为失败状态。
        /// </summary>
        /// <param name="evt">工具调用失败事件</param>
        private void HandleToolCallFailed(AgentEvent evt)
        {
            var key = FindToolCardKey(evt);
            Debug.Log($"[AgentCore.UI] HandleToolCallFailed: tool={evt.ToolName}, key={key ?? "(no match)"}, found={key != null}");

            if (key != null && _activeToolCards.TryGetValue(key, out var card))
            {
                card.SetStatus(ToolCallStatus.Failed, "失败");

                if (!string.IsNullOrEmpty(evt.ToolResult))
                {
                    card.SetDetails(evt.ToolResult);
                }

                _activeToolCards.Remove(key);

                // 通知分组容器更新统计和折叠状态
                _currentToolCallGroup?.NotifyToolStatusChanged();
            }
            else
            {
                Debug.LogWarning($"[AgentCore.UI] HandleToolCallFailed: 未找到 key={key} 的卡片, 当前 keys=[{string.Join(", ", _activeToolCards.Keys)}]");
            }
        }

        /// <summary>
        /// 处理循环轮次开始事件：更新分组容器的轮次信息，并在容器内添加轮次分隔线。
        /// </summary>
        /// <param name="evt">循环轮次开始事件</param>
        private void HandleLoopRoundStarted(AgentEvent evt)
        {
            var group = EnsureToolCallGroup();

            // 更新分组容器的轮次信息
            group.UpdateRoundInfo(evt.CurrentRound, evt.MaxRounds);

            // 第 1 轮不显示分隔线（避免冗余）
            if (evt.CurrentRound <= 1) return;

            var separator = new VisualElement();
            separator.style.flexDirection = FlexDirection.Row;
            separator.style.alignItems = Align.Center;
            separator.style.marginTop = 6;
            separator.style.marginBottom = 6;
            separator.style.marginLeft = 4;
            separator.style.marginRight = 4;

            // 左侧线条
            var leftLine = new VisualElement();
            leftLine.style.flexGrow = 1;
            leftLine.style.height = 1;
            leftLine.style.backgroundColor = new Color(0.3f, 0.3f, 0.3f);
            separator.Add(leftLine);

            // 轮次文本
            var roundLabel = new Label($" 第 {evt.CurrentRound}/{evt.MaxRounds} 轮 ");
            roundLabel.style.fontSize = 10;
            roundLabel.style.color = new Color(0.533f, 0.533f, 0.533f);
            roundLabel.style.flexShrink = 0;
            separator.Add(roundLabel);

            // 右侧线条
            var rightLine = new VisualElement();
            rightLine.style.flexGrow = 1;
            rightLine.style.height = 1;
            rightLine.style.backgroundColor = new Color(0.3f, 0.3f, 0.3f);
            separator.Add(rightLine);

            // 分隔线添加到分组容器内部
            group.AddSeparator(separator);
            ScrollToBottom(force: true); // 新轮次分隔线添加，强制滚动到底部
        }

        #endregion
    }
}
