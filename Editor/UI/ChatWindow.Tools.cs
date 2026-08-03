using AgentCore.Editor.Core;
using AgentCore.Editor.UI.Components;
using UnityEngine;
using UnityEngine.UIElements;
using AgentCore.Editor.Utils;

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
                    AgentCore.Editor.Utils.AgentCoreLog.Debug($"[AgentCore.UI] EnsureToolCallGroup: 新建 ToolCallGroup, 添加到 AssistantTurnView");
                }
                else
                {
                    _currentToolCallGroup = new ToolCallGroup();
                    _messageListManager?.AddItem(_currentToolCallGroup);
                    AgentCore.Editor.Utils.AgentCoreLog.Debug($"[AgentCore.UI] EnsureToolCallGroup: 无 assistant turn，降级添加到 MessageListManager");
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
            AgentCore.Editor.Utils.AgentCoreLog.Debug($"[AgentCore.UI] HandleToolCallStarted: tool={evt.ToolName}, toolCallId={evt.ToolCallId ?? "(null)"}, messageId={evt.MessageId ?? "(null)"}");

            var group = EnsureToolCallGroup();

            // v1.14.5: 参数已接收完毕，真正开始执行——清除"接收参数中"进度态，
            // 避免 ToolCallCard 出现后摘要栏同时挂着两套状态文案。
            group.ClearReceivingArguments();

            var card = new ToolCallCard(evt.ToolName, evt.ToolArguments);
            card.SetStatus(ToolCallStatus.Running, AgentCore.Editor.L10n.Loc.Tr("chat.tool.status.running", "执行中..."));
            group.AddToolCard(card);

            // 用 ToolCallId 作为 key（支持同名工具多次调用）
            var key = GetToolCallKey(evt);
            _activeToolCards[key] = card;
            AgentCore.Editor.Utils.AgentCoreLog.Debug($"[AgentCore.UI] HandleToolCallStarted: card 已添加, key={key}, _activeToolCards.Count={_activeToolCards.Count}");

            // 更新状态行：显示当前执行的工具名
            UpdateStatusLabel(AgentCore.Editor.L10n.Loc.Tr("chat.status.executingToolNamed", "执行工具: {0}", evt.ToolName));

            ScrollToBottom(force: true); // 新工具调用卡片添加，强制滚动到底部
        }

        /// <summary>
        /// 处理工具调用参数流式接收进度事件（v1.14.5，节流后触发，非每 delta 一次）。
        /// <para>
        /// 此事件在 <see cref="ToolCallCard"/> 尚未创建之前到达——工具名可能仍为 null
        /// （function.name 的 delta 还没到），此时只报告字符数；工具名到达后一起显示。
        /// 若已经有对应的 ToolCallCard（罕见的竞态：多工具并行，某个 index 的卡片已建立
        /// 但另一个 index 仍在接收参数），不影响已存在卡片，只驱动分组级的进度提示。
        /// </para>
        /// </summary>
        /// <param name="evt">工具调用进度事件</param>
        private void HandleToolCallProgress(AgentEvent evt)
        {
            var group = EnsureToolCallGroup();
            group.ReportReceivingArguments(evt.ToolName, evt.ProgressCharCount);
        }

        /// <summary>
        /// 处理工具调用完成事件：更新对应的 ToolCallCard 为完成状态。
        /// </summary>
        /// <param name="evt">工具调用完成事件</param>
        private void HandleToolCallCompleted(AgentEvent evt)
        {
            var key = FindToolCardKey(evt);
            AgentCore.Editor.Utils.AgentCoreLog.Debug($"[AgentCore.UI] HandleToolCallCompleted: tool={evt.ToolName}, key={key ?? "(no match)"}, found={key != null}");

            if (key != null && _activeToolCards.TryGetValue(key, out var card))
            {
                var completedText = evt.ExecutionTimeMs > 0
                    ? AgentCore.Editor.L10n.Loc.Tr("chat.tool.status.completedWithTime", "完成 ({0})", $"{evt.ExecutionTimeMs:F0}ms")
                    : AgentCore.Editor.L10n.Loc.Tr("chat.tool.status.completed", "完成");
                card.SetStatus(ToolCallStatus.Completed, completedText);

                if (!string.IsNullOrEmpty(evt.ToolResult))
                {
                    // v1.4.8：完整保留原始结果，不再做 200 字符截断。
                    // ToolCallCard 内部已改用 ScrollView + 只读 TextField，
                    // 长内容可滚动查看且可 Ctrl+C 复制，用户诊断时能拿到完整信息。
                    card.SetDetails(evt.ToolResult);
                }

                _activeToolCards.Remove(key);

                // 通知分组容器更新统计和折叠状态
                _currentToolCallGroup?.NotifyToolStatusChanged();
            }
            else
            {
                AgentCoreLog.Warning($"[AgentCore.UI] HandleToolCallCompleted: 未找到 key={key} 的卡片, 当前 keys=[{string.Join(", ", _activeToolCards.Keys)}]");
            }
        }

        /// <summary>
        /// 处理工具调用失败事件：更新对应的 ToolCallCard 为失败状态。
        /// </summary>
        /// <param name="evt">工具调用失败事件</param>
        private void HandleToolCallFailed(AgentEvent evt)
        {
            var key = FindToolCardKey(evt);
            AgentCore.Editor.Utils.AgentCoreLog.Debug($"[AgentCore.UI] HandleToolCallFailed: tool={evt.ToolName}, key={key ?? "(no match)"}, found={key != null}");

            if (key != null && _activeToolCards.TryGetValue(key, out var card))
            {
                card.SetStatus(ToolCallStatus.Failed, AgentCore.Editor.L10n.Loc.Tr("chat.tool.status.failed", "失败"));

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
                AgentCoreLog.Warning($"[AgentCore.UI] HandleToolCallFailed: 未找到 key={key} 的卡片, 当前 keys=[{string.Join(", ", _activeToolCards.Keys)}]");
            }
        }

        /// <summary>
        /// 处理循环轮次开始事件：创建新轮次区域（独立 ThinkingDrawer）、添加轮次分隔线、更新分组信息。
        /// <para>
        /// 第 1 轮：AssistantTurnView 构造时已自动创建首个 RoundSection，此处仅更新分组信息。
        /// 第 2+ 轮：调用 BeginNewRound 创建新区域（含独立 ThinkingDrawer），使后续 reasoning
        /// 显示在新窗口而非追加到旧窗口。分隔线添加到轮次容器中，位于新区域之前。
        /// </para>
        /// </summary>
        /// <param name="evt">循环轮次开始事件</param>
        private void HandleLoopRoundStarted(AgentEvent evt)
        {
            // 第 1 轮：AssistantTurnView 构造时已创建首个 RoundSection，无需新建
            if (evt.CurrentRound > 1)
            {
                // 第 2+ 轮：在 AssistantTurnView 中创建新轮次区域（独立 ThinkingDrawer）
                if (!string.IsNullOrEmpty(_currentAssistantTurnId))
                {
                    var turnView = EnsureAssistantTurnView(_currentAssistantTurnId);

                    // 先添加轮次分隔线到轮次容器
                    var separator = CreateRoundSeparator(evt);
                    turnView?.AddRoundSeparator(separator);

                    // 创建新轮次区域（独立 ThinkingDrawer + ToolSlot）
                    turnView?.BeginNewRound();

                    // 重置当前工具调用分组引用，使后续工具卡片添加到新轮次的 ToolGroup
                    _currentToolCallGroup = null;
                }
            }

            // 确保当前轮次有 ToolCallGroup 并更新轮次信息
            var group = EnsureToolCallGroup();
            group.UpdateRoundInfo(evt.CurrentRound, evt.MaxRounds, evt.TokensUsed);

            ScrollToBottom(force: true);
        }

        /// <summary>
        /// 创建轮次分隔线 VisualElement。
        /// </summary>
        private static VisualElement CreateRoundSeparator(AgentEvent evt)
        {
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
            var tokenSuffix = evt.TokensUsed > 0 ? $" | {FormatTokenCount(evt.TokensUsed)}" : "";
            var roundLabel = new Label($" 第 {evt.CurrentRound}/{evt.MaxRounds} 轮{tokenSuffix} ");
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

            return separator;
        }

        /// <summary>
        /// 格式化 token 数量为人类可读形式。
        /// </summary>
        private static string FormatTokenCount(int tokens)
        {
            if (tokens >= 1_000_000)
                return $"{tokens / 1_000_000.0:F1}M tokens";
            if (tokens >= 1_000)
                return $"{tokens / 1_000.0:F1}K tokens";
            return $"{tokens} tokens";
        }

        #endregion
    }
}
