using System;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Core
{
    public partial class AgentLoop
    {
        /// <summary>
        /// 设置 Agent 状态并派发状态变更事件。
        /// </summary>
        /// <param name="newState">新的 Agent 状态</param>
        private void SetState(AgentState newState)
        {
            if (CurrentState == newState)
            {
                return;
            }

            var previousState = CurrentState;
            CurrentState = newState;
            AgentCore.Editor.Utils.AgentCoreLog.Debug($"[AgentCore] State: {previousState} -> {newState}");
            EmitEvent(AgentEvent.StateChanged(newState));
        }

        /// <summary>
        /// 派发 Agent 事件到主线程订阅方。
        /// 使用 <see cref="AsyncHelper.RunOnMainThread"/> 确保事件在 Unity 主线程上触发,
        /// 因为 LLM 流式回调可能在后台线程执行.
        /// </summary>
        /// <param name="evt">要派发的事件</param>
        private void EmitEvent(AgentEvent evt)
        {
            if (evt == null) return;

            using (AgentCoreProfilerMarkers.EmitMarshalled.Auto())
            {
                AsyncHelper.RunOnMainThread(() => OnAgentEvent?.Invoke(evt));
            }
        }
    }
}
