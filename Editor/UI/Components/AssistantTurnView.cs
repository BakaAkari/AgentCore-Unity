using System.Collections.Generic;
using AgentCore.Editor.Core;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Components
{
    /// <summary>
    /// 单个 assistant turn 的固定布局容器。
    /// <para>
    /// 支持多轮 LLM 调用：每轮拥有独立的 ThinkingDrawer 和 ToolCallGroup，
    /// 通过 <see cref="BeginNewRound"/> 创建新轮次区域。
    /// 布局：[Round1(Thinking+Tools)] [Separator] [Round2(Thinking+Tools)] ... [SelfChallenge] [Bubble]
    /// </para>
    /// </summary>
    public class AssistantTurnView : VisualElement
    {
        #region 轮次区域

        /// <summary>
        /// 单轮区域：独立的 ThinkingDrawer + ToolSlot。
        /// 每轮 LLM 调用创建一个新区域，使后续轮次的 reasoning 拥有独立窗口。
        /// </summary>
        private class RoundSection
        {
            public ThinkingDrawer ThinkingDrawer { get; }
            public VisualElement ToolSlot { get; }
            public ToolCallGroup ToolGroup { get; set; }

            public RoundSection()
            {
                ThinkingDrawer = new ThinkingDrawer();
                ToolSlot = new VisualElement();
                ToolSlot.style.flexDirection = FlexDirection.Column;
                ToolSlot.style.width = Length.Percent(100);
            }
        }

        #endregion

        #region 字段

        private readonly List<RoundSection> _rounds = new List<RoundSection>();
        private readonly VisualElement _roundsContainer;
        private readonly VisualElement _selfChallengeSlot;
        private readonly VisualElement _bubbleSlot;

        #endregion

        #region 属性

        /// <summary>
        /// 当前（最新）轮次的 ThinkingDrawer。
        /// reasoning token 追加到此 drawer。
        /// </summary>
        public ThinkingDrawer ThinkingDrawer => _rounds.Count > 0
            ? _rounds[_rounds.Count - 1].ThinkingDrawer
            : null;

        /// <summary>
        /// 当前 SelfChallengeCard (Phase 9); 默认 null, 通过 <see cref="EnsureSelfChallengeCard"/> 创建。
        /// </summary>
        public SelfChallengeCard SelfChallengeCard { get; private set; }

        /// <summary>
        /// 当前消息气泡。
        /// </summary>
        public MessageBubble Bubble { get; private set; }

        /// <summary>
        /// 当前（最新）轮次的工具调用分组。
        /// </summary>
        public ToolCallGroup ToolGroup => _rounds.Count > 0
            ? _rounds[_rounds.Count - 1].ToolGroup
            : null;

        /// <summary>当前轮次区域。</summary>
        private RoundSection CurrentRound => _rounds.Count > 0
            ? _rounds[_rounds.Count - 1]
            : null;

        #endregion

        #region 构造函数

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
            // v1.7.13 fix: turn 容器及各 slot 强制撑满对话区可用宽度，
            // 否则整个 turn 的宽度会被首段渲染的内容（如一句短文本气泡）锁成 min-content，
            // 导致后续加入的工具卡片/长内容被压窄。撑满后各段独立铺开，
            // MessageBubble 仍靠自身 max-width:82% + align-self:flex-start 保持内容自然伸缩。
            style.width = Length.Percent(100);

            _roundsContainer = new VisualElement { name = $"rounds-container-{messageId}" };
            _roundsContainer.style.flexDirection = FlexDirection.Column;
            _roundsContainer.style.width = Length.Percent(100);
            Add(_roundsContainer);

            // Phase 9: Self-Challenge Card 挂载点(位于所有轮次之下、Bubble 之上)
            _selfChallengeSlot = new VisualElement { name = $"self-challenge-slot-{messageId}" };
            _selfChallengeSlot.style.flexDirection = FlexDirection.Column;
            _selfChallengeSlot.style.width = Length.Percent(100);
            Add(_selfChallengeSlot);

            _bubbleSlot = new VisualElement { name = $"bubble-slot-{messageId}" };
            _bubbleSlot.style.flexDirection = FlexDirection.Column;
            _bubbleSlot.style.width = Length.Percent(100);
            Add(_bubbleSlot);

            // 自动创建第一轮区域
            BeginNewRound();
        }

        #endregion

        #region 轮次管理

        /// <summary>
        /// 创建新轮次区域（包含独立的 ThinkingDrawer 和 ToolSlot）。
        /// 在 LoopRoundStarted 事件（第 2 轮起）调用，使后续 reasoning 拥有独立窗口。
        /// </summary>
        public void BeginNewRound()
        {
            // BUG1(第二起) 结构性预防：新一轮开始前，强制结束上一轮 drawer 的计时（若仍在 running）。
            // 见 ThinkingDrawer.ForceCompleteIfRunning 上的详细说明——新一轮的开始本身就是
            // "上一轮必然已经结束"的强证据，不依赖 Core 是否正确 emit 了 ReasoningCompleted。
            CurrentRound?.ThinkingDrawer.ForceCompleteIfRunning();

            var section = new RoundSection();

            var sectionContainer = new VisualElement();
            sectionContainer.style.flexDirection = FlexDirection.Column;
            sectionContainer.style.width = Length.Percent(100);
            sectionContainer.Add(section.ThinkingDrawer);
            sectionContainer.Add(section.ToolSlot);

            _rounds.Add(section);
            _roundsContainer.Add(sectionContainer);
        }

        /// <summary>
        /// 在轮次容器中添加分隔线（位于新轮次区域之前）。
        /// </summary>
        /// <param name="separator">分隔线 VisualElement。</param>
        public void AddRoundSeparator(VisualElement separator)
        {
            if (separator == null) return;
            _roundsContainer.Add(separator);
        }

        /// <summary>
        /// v1.14.10: 把"过程轮次"（非最后一轮）的可见 content 渲染为次要样式的旁白文字，
        /// 插入对应轮次区域（ThinkingDrawer 下方、ToolSlot 上方）。
        /// <para>
        /// 背景：部分模型（实测 DeepSeek-V4-Flash）习惯在每轮工具调用前输出一句过渡说明
        /// （"让我检查……"），这些说明此前和最终报告被无差别拼接进同一个 MessageBubble，
        /// 用户视觉上无法区分"过程旁白"与"正式回复"（见 ConversationTurn.RoundContents 类注释）。
        /// </para>
        /// <para>
        /// 只在最终定格画面（<c>FinalizeAssistantMessage</c>）调用一次，不参与流式过程的
        /// 实时渲染——流式阶段仍是整段堆在一起显示（与此前行为一致），生成完成后才重新
        /// 按轮次整理展示。样式：小字号、次要灰色，视觉上明显弱于正式回复的 MessageBubble，
        /// 但不做折叠/展开交互（不是 ThinkingDrawer，语义不同——这是"agent 说过的话"，
        /// 不是"内部思考过程"，用户可能仍想直接看到，只是不应该和最终结论混同）。
        /// </para>
        /// </summary>
        /// <param name="roundIndex">轮次下标（0-based，对应 <see cref="_rounds"/> 里的 RoundSection）。</param>
        /// <param name="text">该轮次的过程性旁白文字（已去除首尾空白，非空才会调用本方法）。</param>
        public void ShowRoundNarration(int roundIndex, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (roundIndex < 0 || roundIndex >= _rounds.Count) return;

            var section = _rounds[roundIndex];

            var narrationLabel = new Label(text.Trim());
            narrationLabel.AddToClassList("round-narration-text");
            narrationLabel.style.whiteSpace = WhiteSpace.Normal;
            narrationLabel.style.fontSize = 12;
            narrationLabel.style.color = new StyleColor(new UnityEngine.Color(0.58f, 0.58f, 0.58f));
            narrationLabel.style.marginLeft = 8;
            narrationLabel.style.marginRight = 8;
            narrationLabel.style.marginTop = 2;
            narrationLabel.style.marginBottom = 4;
            narrationLabel.selection.isSelectable = true;

            // 插在 ThinkingDrawer 之后、ToolSlot 之前——与该轮次的思考/工具调用顺序对齐
            // （用户阅读顺序：先看这轮在想什么 → 这轮说了什么 → 这轮调了什么工具）。
            var sectionContainer = section.ToolSlot.parent;
            if (sectionContainer == null)
            {
                // 兜底：找不到父容器时直接加到 ToolSlot 前面（理论上不会发生，BeginNewRound
                // 里 ThinkingDrawer/ToolSlot 总是被 Add 进同一个 sectionContainer）。
                return;
            }
            int toolSlotIndex = sectionContainer.IndexOf(section.ToolSlot);
            sectionContainer.Insert(toolSlotIndex >= 0 ? toolSlotIndex : 0, narrationLabel);
        }

        #endregion

        #region Self-Challenge

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

        #endregion

        #region 消息气泡

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
        /// 移除消息气泡（保留 ThinkingDrawer / ToolGroup / SelfChallengeCard）。
        /// 用于 reasoning-only 空正文回复：GLM 把 max_tokens 全用在 reasoning、没输出正文时，
        /// 不保留空的正文气泡壳，但用户仍可展开 ThinkingDrawer 查看思考过程。
        /// </summary>
        public void RemoveBubble()
        {
            if (Bubble == null) return;
            Bubble.RemoveFromHierarchy();
            Bubble = null;
        }

        #endregion

        #region 工具调用分组

        /// <summary>
        /// 确保当前轮次的工具调用分组存在。
        /// </summary>
        /// <returns>当前轮次的工具调用分组。</returns>
        public ToolCallGroup EnsureToolGroup()
        {
            var section = CurrentRound;
            if (section == null)
            {
                BeginNewRound();
                section = CurrentRound;
            }
            if (section.ToolGroup == null)
            {
                section.ToolGroup = new ToolCallGroup();
                section.ToolSlot.Add(section.ToolGroup);
            }
            return section.ToolGroup;
        }

        /// <summary>
        /// 恢复已有工具调用分组到当前轮次。
        /// </summary>
        /// <param name="group">工具调用分组。</param>
        public void SetToolGroup(ToolCallGroup group)
        {
            if (group == null) return;
            var section = CurrentRound;
            if (section == null)
            {
                BeginNewRound();
                section = CurrentRound;
            }
            section.ToolSlot.Clear();
            section.ToolGroup = group;
            section.ToolSlot.Add(group);
        }

        #endregion

        #region 恢复

        /// <summary>
        /// 恢复 ThinkingDrawer 内容（会话恢复时调用）。
        /// 将所有 reasoning 放入第一轮的 ThinkingDrawer。
        /// </summary>
        /// <param name="turn">assistant turn。</param>
        public void RestoreThinking(ConversationTurn turn)
        {
            if (turn == null || string.IsNullOrEmpty(turn.Reasoning)) return;
            if (_rounds.Count > 0)
            {
                _rounds[0].ThinkingDrawer.SetReasoning(turn.Reasoning, turn.ReasoningSource, turn.ReasoningDurationMs);
            }
        }

        #endregion
    }
}
