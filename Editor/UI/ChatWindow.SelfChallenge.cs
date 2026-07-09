using AgentCore.Editor.Core;
using UnityEngine;

namespace AgentCore.Editor.UI
{
    /// <summary>
    /// ChatWindow 的 Self-Challenge 事件处理层(Phase 9)。
    /// 订阅 IntentChallengeCompleted / AnswerChallengeCompleted / AnswerChallengeRegenerating / AnswerChallengeRegenerated,
    /// 定位对应 assistant turn 的 SelfChallengeCard 并更新其数据。
    /// </summary>
    public partial class ChatWindow
    {
        /// <summary>
        /// 处理 Self-Challenge 相关事件。
        /// </summary>
        private void HandleSelfChallengeEvent(AgentEvent evt)
        {
            if (evt == null || evt.SelfChallenge == null || string.IsNullOrEmpty(evt.TurnId))
            {
                return;
            }

            if (!_assistantTurnViews.TryGetValue(evt.TurnId, out var turnView))
            {
                // 该 turn 尚未在 UI 中创建, 或已被销毁
                return;
            }

            var card = turnView.EnsureSelfChallengeCard(evt.TurnId);
            card.SetData(evt.SelfChallenge);

            // 简要 debug 日志便于验证
            Debug.Log($"[AgentCore.UI][SelfChallenge] {evt.Type} — turn={evt.TurnId}  " +
                      $"NodeA={evt.SelfChallenge.NodeATriggered}, NodeB={evt.SelfChallenge.NodeBTriggered}, " +
                      $"Verdict={evt.SelfChallenge.NodeBVerdict?.ToString() ?? "n/a"}, " +
                      $"clarify={evt.SelfChallenge.TriggeredClarification}");
        }
    }
}
