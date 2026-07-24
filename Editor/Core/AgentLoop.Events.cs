using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using AgentCore.Editor.Utils;
using UnityEngine;

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
        /// 派发 Agent 事件。
        /// <para>
        /// v1.8.8: 加 SessionMode gate. Silent 模式下事件全部写入 _silentBuffer 不 marshal 到主线程,
        /// 由 FlushSilentBuffer 在 TurnDone/Error/手动切出 Silent 时统一 flush.
        /// Batched 模式沿用现有路径 (AsyncHelper.RunOnMainThread → 每帧 drain).
        /// </para>
        /// <para>
        /// 使用 <see cref="AsyncHelper.RunOnMainThread"/> 确保事件在 Unity 主线程上触发,
        /// 因为 LLM 流式回调可能在后台线程执行.
        /// </para>
        /// </summary>
        /// <param name="evt">要派发的事件</param>
        private void EmitEvent(AgentEvent evt)
        {
            if (evt == null) return;

            // v1.8.8: 懒订阅 SessionModeState.Changed. 幂等, 只挂一次.
            EnsureSessionModeSubscription();

            // v1.8.8 白名单修正: Silent 模式下用户交互控件相关的事件不 buffer, 直接 marshal.
            // 原设计"UI 完全冻结"过于激进 — 输入栏 send/cancel 按钮 / 状态栏 / 错误弹窗 / 工具确认
            // 都是用户主动交互点, 冻结会让用户以为 agent 死了 (v1.8.8 首次装机实测发现).
            // 只 buffer 会 append/rebuild 消息容器的事件 (StreamToken/ToolCallStarted 等).
            if (SessionModeState.IsSilent && !IsUserInteractionEvent(evt))
            {
                _silentBuffer.Enqueue(evt);
                return;
            }

            AsyncHelper.RunOnMainThread(() => OnAgentEvent?.Invoke(evt));
        }

        /// <summary>
        /// v1.8.8: 判断事件是否属于"用户交互控件"必需 (Silent 模式下也直接 marshal, 不 buffer).
        /// 白名单原则: 事件对应的 UI 更新是**用户主动交互反馈**, 而非**消息容器重建**.
        /// - StateChanged: send/cancel 按钮状态 + 状态栏文字
        /// - Error (ErrorEvent): 错误必须立即弹, 不能等 turn 结束
        /// - ConversationReset: 用户主动清空的即时反馈
        /// - ToolConfirmationRequested: 阻塞交互, 必须弹确认框
        /// - ToolBlocked: 用户必须看到"操作被治理层阻断"的提示
        /// 其他所有事件 (StreamToken/ReasoningToken/ToolCallStarted/Completed/Failed/AssistantMessage/
        /// LoopRoundStarted/LoopCompleted/FileChangesUpdated/ReasoningCompleted/AnswerChallenge*/etc)
        /// 都会 append/rebuild 消息容器, Silent 时 buffer.
        /// </summary>
        private static bool IsUserInteractionEvent(AgentEvent evt)
        {
            switch (evt.Type)
            {
                case AgentEventType.StateChanged:
                case AgentEventType.Error:
                case AgentEventType.ConversationReset:
                case AgentEventType.ToolConfirmationRequested:
                case AgentEventType.ToolBlocked:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// v1.8.8: Silent 模式下缓存的事件. Enqueue 可能来自后台线程 (LLM stream callback),
        /// Dequeue 在主线程. 用 ConcurrentQueue 保证跨线程安全.
        /// </summary>
        private readonly ConcurrentQueue<AgentEvent> _silentBuffer = new();

        /// <summary>
        /// v1.8.8: 是否已订阅 SessionModeState.Changed. 懒订阅, 避免 AgentLoop 无实例时空转.
        /// AgentLoop 由 ChatWindow.InitializeAgentLoop 创建, 一个 window 一个 loop, 单例语义.
        /// </summary>
        private bool _sessionModeSubscribed;

        /// <summary>
        /// v1.8.8: 首次触达时挂订阅. 由 EmitEvent 触发 (每次 Emit 都调 EnsureSessionModeSubscription),
        /// 因为 EmitEvent 是唯一必然被调用的路径. 幂等.
        /// </summary>
        private void EnsureSessionModeSubscription()
        {
            if (_sessionModeSubscribed) return;
            _sessionModeSubscribed = true;
            SessionModeState.Changed += OnSessionModeChanged;
        }

        /// <summary>
        /// v1.8.8: SessionMode 变化回调.
        /// - Silent → Batched: 立即 flush buffer 让累积事件可见 (用户 Q4: 手动切出走事件通道)
        /// - Batched → Silent: 无 flush 需要 (buffer 本来为空)
        /// </summary>
        private void OnSessionModeChanged(SessionMode newMode)
        {
            if (newMode == SessionMode.Batched)
            {
                FlushSilentBuffer();
            }
        }

        /// <summary>
        /// v1.8.8: 把 Silent buffer 里累积的事件全部走事件通道 flush.
        /// 调用时机 (由用户拍板 Q4):
        /// - Turn 结束 (TurnDone/Error): 无论当前 mode, 都调用一次以清空可能残留的 buffer
        /// - 用户手动切出 Silent (SessionModeState.Set(Batched)): 立即 flush
        ///
        /// 走事件通道 (逐个 AsyncHelper.RunOnMainThread) 而非 UI 直接构造 message —— 保证事件
        /// 语义链路一致 (StateChanged / ToolCallStarted 等都会被订阅方看到), 只是一波集中发出.
        /// UI 端 AsyncHelper.DrainMainThreadQueue 每帧最多 256 个, 大 buffer 会分几帧 flush 但
        /// 每帧成本可控.
        /// </summary>
        internal void FlushSilentBuffer()
        {
            int count = 0;
            var drained = new List<AgentEvent>();
            while (_silentBuffer.TryDequeue(out var evt))
            {
                drained.Add(evt);
                count++;
            }
            if (count == 0) return;

            AgentCore.Editor.Utils.AgentCoreLog.Debug($"[AgentCore] FlushSilentBuffer: replaying {count} buffered event(s)");

            foreach (var evt in drained)
            {
                var captured = evt;
                AsyncHelper.RunOnMainThread(() => OnAgentEvent?.Invoke(captured));
            }
        }
    }
}
