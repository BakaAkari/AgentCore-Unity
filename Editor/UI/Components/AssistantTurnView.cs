using AgentCore.Editor.Core;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Components
{
    /// <summary>
    /// 单个 assistant turn 的固定布局容器：ThinkingDrawer -> ToolCallGroup -> MessageBubble。
    /// </summary>
    public class AssistantTurnView : VisualElement
    {
        private readonly VisualElement _thinkingSlot;
        private readonly VisualElement _selfChallengeSlot;
        private readonly VisualElement _toolSlot;
        private readonly VisualElement _bubbleSlot;

        /// <summary>
        /// 当前 ThinkingDrawer。
        /// </summary>
        public ThinkingDrawer ThinkingDrawer { get; }

        /// <summary>
        /// 当前 SelfChallengeCard (Phase 9); 默认 null, 通过 <see cref="EnsureSelfChallengeCard"/> 创建。
        /// </summary>
        public SelfChallengeCard SelfChallengeCard { get; private set; }

        /// <summary>
        /// 当前消息气泡。
        /// </summary>
        public MessageBubble Bubble { get; private set; }

        /// <summary>
        /// 当前工具调用分组。
        /// </summary>
        public ToolCallGroup ToolGroup { get; private set; }

        /// <summary>
        /// 创建 assistant turn 容器。
        /// </summary>
        /// <param name="messageId">assistant turn ID。</param>
        public AssistantTurnView(string messageId)
        {
            AddToClassList("assistant-turn-view");
            style.flexDirection = FlexDirection.Column;
            style.marginTop = 2;
            style.marginBottom = 2;

            _thinkingSlot = new VisualElement { name = $"thinking-slot-{messageId}" };
            _thinkingSlot.style.flexDirection = FlexDirection.Column;
            Add(_thinkingSlot);

            // Phase 9: Self-Challenge Card 挂载点(位于 ThinkingDrawer 之下、ToolCallGroup 之上)
            _selfChallengeSlot = new VisualElement { name = $"self-challenge-slot-{messageId}" };
            _selfChallengeSlot.style.flexDirection = FlexDirection.Column;
            Add(_selfChallengeSlot);

            _toolSlot = new VisualElement { name = $"tool-slot-{messageId}" };
            _toolSlot.style.flexDirection = FlexDirection.Column;
            Add(_toolSlot);

            _bubbleSlot = new VisualElement { name = $"bubble-slot-{messageId}" };
            _bubbleSlot.style.flexDirection = FlexDirection.Column;
            Add(_bubbleSlot);

            ThinkingDrawer = new ThinkingDrawer();
            _thinkingSlot.Add(ThinkingDrawer);
        }

        /// <summary>
        /// 确保 SelfChallengeCard 存在; 若已存在直接返回。
        /// </summary>
        /// <param name="messageId">assistant turn ID。</param>
        /// <returns>SelfChallengeCard 实例。</returns>
        public SelfChallengeCard EnsureSelfChallengeCard(string messageId)
        {
            if (SelfChallengeCard != null) return SelfChallengeCard;

            SelfChallengeCard = new SelfChallengeCard(messageId);
            _selfChallengeSlot.Add(SelfChallengeCard);
            return SelfChallengeCard;
        }

        /// <summary>
        /// 确保消息气泡存在。
        /// </summary>
        /// <param name="messageId">消息 ID。</param>
        /// <param name="content">初始内容。</param>
        /// <param name="isStreaming">是否为流式气泡。</param>
        /// <returns>消息气泡。</returns>
        public MessageBubble EnsureBubble(string messageId, string content = "", bool isStreaming = true)
        {
            if (Bubble != null) return Bubble;

            Bubble = new MessageBubble(messageId, "assistant", content ?? string.Empty, isStreaming);
            _bubbleSlot.Add(Bubble);
            return Bubble;
        }

        /// <summary>
        /// 确保工具调用分组存在。
        /// </summary>
        /// <returns>工具调用分组。</returns>
        public ToolCallGroup EnsureToolGroup()
        {
            if (ToolGroup != null) return ToolGroup;

            ToolGroup = new ToolCallGroup();
            _toolSlot.Add(ToolGroup);
            return ToolGroup;
        }

        /// <summary>
        /// 恢复已有工具调用分组。
        /// </summary>
        /// <param name="group">工具调用分组。</param>
        public void SetToolGroup(ToolCallGroup group)
        {
            if (group == null) return;

            ToolGroup = group;
            _toolSlot.Clear();
            _toolSlot.Add(group);
        }

        /// <summary>
        /// 恢复 ThinkingDrawer 内容。
        /// </summary>
        /// <param name="turn">assistant turn。</param>
        public void RestoreThinking(ConversationTurn turn)
        {
            if (turn == null || string.IsNullOrEmpty(turn.Reasoning)) return;
            ThinkingDrawer.SetReasoning(turn.Reasoning, turn.ReasoningSource, turn.ReasoningDurationMs);
        }
    }
}
