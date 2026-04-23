using System;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Core
{
    /// <summary>
    /// Domain Reload 中断阶段枚举。
    /// 描述 Agent 在 Domain Reload 发生时正处于哪个执行阶段。
    /// </summary>
    public enum InterruptPhase
    {
        /// <summary>无中断（正常状态）</summary>
        None,

        /// <summary>正在流式接收 LLM 响应</summary>
        Streaming,

        /// <summary>正在执行工具调用</summary>
        ExecutingTool,

        /// <summary>正在等待编译完成</summary>
        WaitingCompilation
    }

    /// <summary>
    /// Domain Reload 状态持久化容器。
    /// <para>
    /// 使用 <see cref="ScriptableSingleton{T}"/> 跨 Domain Reload 存活，
    /// 记录 Agent 在 Domain Reload 前的中断状态，以便 Reload 后检测并恢复。
    /// </para>
    /// <para>
    /// 生命周期：
    /// <list type="bullet">
    ///   <item>在 <c>AssemblyReloadEvents.beforeAssemblyReload</c> 中由 AgentLoop 调用 <see cref="MarkInterrupted"/> 写入中断标记</item>
    ///   <item>在 <c>ChatWindow.TryRestoreSession()</c> 中读取中断标记并决定恢复策略</item>
    ///   <item>恢复完成后调用 <see cref="ClearInterruption"/> 清除标记</item>
    /// </list>
    /// </para>
    /// </summary>
    [FilePath("AgentCore/DomainReloadState.asset", FilePathAttribute.Location.PreferencesFolder)]
    public class DomainReloadState : ScriptableSingleton<DomainReloadState>
    {
        #region 序列化字段

        /// <summary>是否被 Domain Reload 中断</summary>
        [SerializeField] private bool _wasInterrupted;

        /// <summary>被中断的会话 ID</summary>
        [SerializeField] private string _interruptedSessionId;

        /// <summary>中断发生在哪个阶段</summary>
        [SerializeField] private InterruptPhase _interruptPhase = InterruptPhase.None;

        /// <summary>最后执行的工具名（如 manage_script）</summary>
        [SerializeField] private string _lastToolName;

        /// <summary>中断时间戳（ISO 8601 格式）</summary>
        [SerializeField] private string _interruptTimestamp;

        /// <summary>是否有未完成的 tool calls</summary>
        [SerializeField] private bool _hadPendingToolCalls;

        // === Phase 2 新增字段 ===

        /// <summary>最后一条用户消息（用于重发场景）</summary>
        [SerializeField] private string _pendingUserMessage;

        /// <summary>最后一条 assistant 部分内容（用于 streaming 中断恢复）</summary>
        [SerializeField] private string _lastAssistantContent;

        /// <summary>编译是否成功（afterAssemblyReload 时设置）</summary>
        [SerializeField] private bool _compilationSucceeded;

        /// <summary>编译错误信息（afterAssemblyReload 时设置）</summary>
        [SerializeField] private string _compilationErrors;

        /// <summary>被中断的 tool_call ID</summary>
        [SerializeField] private string _interruptedToolCallId;

        #endregion

        #region 公开属性

        /// <summary>是否被 Domain Reload 中断</summary>
        public bool WasInterrupted => _wasInterrupted;

        /// <summary>被中断的会话 ID</summary>
        public string InterruptedSessionId => _interruptedSessionId;

        /// <summary>中断发生在哪个阶段</summary>
        public InterruptPhase InterruptPhase => _interruptPhase;

        /// <summary>最后执行的工具名</summary>
        public string LastToolName => _lastToolName;

        /// <summary>中断时间戳（ISO 8601 格式）</summary>
        public string InterruptTimestamp => _interruptTimestamp;

        /// <summary>是否有未完成的 tool calls</summary>
        public bool HadPendingToolCalls => _hadPendingToolCalls;

        // === Phase 2 新增属性 ===

        /// <summary>最后一条用户消息（用于重发场景）</summary>
        public string PendingUserMessage => _pendingUserMessage;

        /// <summary>最后一条 assistant 部分内容（用于 streaming 中断恢复）</summary>
        public string LastAssistantContent => _lastAssistantContent;

        /// <summary>编译是否成功</summary>
        public bool CompilationSucceeded => _compilationSucceeded;

        /// <summary>编译错误信息</summary>
        public string CompilationErrors => _compilationErrors;

        /// <summary>被中断的 tool_call ID</summary>
        public string InterruptedToolCallId => _interruptedToolCallId;

        #endregion

        #region 公开方法

        /// <summary>
        /// 标记 Agent 被 Domain Reload 中断。
        /// 在 <c>AssemblyReloadEvents.beforeAssemblyReload</c> 回调中调用。
        /// </summary>
        /// <param name="sessionId">当前会话 ID</param>
        /// <param name="phase">中断时的执行阶段</param>
        /// <param name="lastToolName">最后执行的工具名（可为 null）</param>
        /// <param name="hadPendingToolCalls">是否有未完成的 tool calls</param>
        /// <param name="pendingUserMessage">最后一条用户消息（可为 null）</param>
        /// <param name="lastAssistantContent">最后一条 assistant 部分内容（可为 null）</param>
        /// <param name="interruptedToolCallId">被中断的 tool_call ID（可为 null）</param>
        public void MarkInterrupted(
            string sessionId,
            InterruptPhase phase,
            string lastToolName = null,
            bool hadPendingToolCalls = false,
            string pendingUserMessage = null,
            string lastAssistantContent = null,
            string interruptedToolCallId = null)
        {
            _wasInterrupted = true;
            _interruptedSessionId = sessionId;
            _interruptPhase = phase;
            _lastToolName = lastToolName ?? string.Empty;
            _interruptTimestamp = DateTime.UtcNow.ToString("o");
            _hadPendingToolCalls = hadPendingToolCalls;
            _pendingUserMessage = pendingUserMessage ?? string.Empty;
            _lastAssistantContent = lastAssistantContent ?? string.Empty;
            _interruptedToolCallId = interruptedToolCallId ?? string.Empty;

            // 编译结果在 afterAssemblyReload 时设置，此处先重置
            _compilationSucceeded = false;
            _compilationErrors = string.Empty;

            // ScriptableSingleton 需要显式标记脏以确保序列化
            Save(true);

            Debug.Log($"[AgentCore] DomainReloadState: Marked interrupted — session={sessionId}, phase={phase}, " +
                      $"tool={lastToolName}, pendingToolCalls={hadPendingToolCalls}, " +
                      $"hasUserMsg={!string.IsNullOrEmpty(pendingUserMessage)}, " +
                      $"hasAssistantContent={!string.IsNullOrEmpty(lastAssistantContent)}, " +
                      $"toolCallId={interruptedToolCallId}");
        }

        /// <summary>
        /// 设置编译结果。
        /// 在 Domain Reload 完成后（afterAssemblyReload）调用，记录编译是否成功。
        /// </summary>
        /// <param name="succeeded">编译是否成功</param>
        /// <param name="errors">编译错误信息（可为 null）</param>
        public void SetCompilationResult(bool succeeded, string errors = null)
        {
            _compilationSucceeded = succeeded;
            _compilationErrors = errors ?? string.Empty;
            Save(true);

            Debug.Log($"[AgentCore] DomainReloadState: Compilation result set — succeeded={succeeded}, " +
                      $"errors={(!string.IsNullOrEmpty(errors) ? errors.Substring(0, Math.Min(errors.Length, 200)) : "(none)")}");
        }

        /// <summary>
        /// 清除中断标记。
        /// 在会话恢复完成后调用。
        /// </summary>
        public void ClearInterruption()
        {
            var wasInterrupted = _wasInterrupted;
            var sessionId = _interruptedSessionId;

            _wasInterrupted = false;
            _interruptedSessionId = string.Empty;
            _interruptPhase = InterruptPhase.None;
            _lastToolName = string.Empty;
            _interruptTimestamp = string.Empty;
            _hadPendingToolCalls = false;
            _pendingUserMessage = string.Empty;
            _lastAssistantContent = string.Empty;
            _compilationSucceeded = false;
            _compilationErrors = string.Empty;
            _interruptedToolCallId = string.Empty;

            Save(true);

            if (wasInterrupted)
            {
                Debug.Log($"[AgentCore] DomainReloadState: Interruption cleared for session {sessionId}.");
            }
        }

        #endregion
    }
}
