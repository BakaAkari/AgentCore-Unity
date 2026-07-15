using System;
using AgentCore.Editor.Config;
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

        /// <summary>最后一条 assistant reasoning 内容（用于 ThinkingDrawer 恢复）</summary>
        [SerializeField] private string _lastAssistantReasoning;

        /// <summary>最后一条 assistant reasoning 来源</summary>
        [SerializeField] private ThinkingTraceSource _lastAssistantReasoningSource = ThinkingTraceSource.None;

        /// <summary>最后一条 assistant reasoning 耗时（毫秒）</summary>
        [SerializeField] private double _lastAssistantReasoningDurationMs;

        /// <summary>最后一条 assistant 原始 content，仅用于恢复 UI/session</summary>
        [SerializeField] private string _lastAssistantRawContent;

        /// <summary>最后一条 assistant 可见规划 trace 状态</summary>
        [SerializeField] private VisiblePlanningTraceState _lastAssistantPlanningTraceState = VisiblePlanningTraceState.None;

        /// <summary>编译是否成功（afterAssemblyReload 时设置）</summary>
        [SerializeField] private bool _compilationSucceeded;

        /// <summary>编译错误信息（afterAssemblyReload 时设置）</summary>
        [SerializeField] private string _compilationErrors;

        /// <summary>被中断的 tool_call ID</summary>
        [SerializeField] private string _interruptedToolCallId;

        // === Phase 4.5 新增字段 ===

        /// <summary>文件变更记录的 JSON 序列化数据（跨 Domain Reload 保留）</summary>
        [SerializeField] private string _fileChangeRecordsJson;

        // === Phase 5 新增字段（压缩统计数据持久化）===

        /// <summary>工具结果压缩成功次数</summary>
        [SerializeField] private int _toolResultCompressionSuccessCount;

        /// <summary>对话压缩成功次数</summary>
        [SerializeField] private int _conversationCompressionSuccessCount;

        /// <summary>总节省 token 数</summary>
        [SerializeField] private int _totalTokensSaved;

        /// <summary>工具结果压缩前的总 token 数</summary>
        [SerializeField] private int _toolResultOriginalTokens;

        /// <summary>对话压缩前的总 token 数</summary>
        [SerializeField] private int _conversationOriginalTokens;

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

        /// <summary>最后一条 assistant reasoning 内容（用于 ThinkingDrawer 恢复）</summary>
        public string LastAssistantReasoning => _lastAssistantReasoning;

        /// <summary>最后一条 assistant reasoning 来源</summary>
        public ThinkingTraceSource LastAssistantReasoningSource => _lastAssistantReasoningSource;

        /// <summary>最后一条 assistant reasoning 耗时（毫秒）</summary>
        public double LastAssistantReasoningDurationMs => _lastAssistantReasoningDurationMs;

        /// <summary>最后一条 assistant 原始 content，仅用于恢复 UI/session</summary>
        public string LastAssistantRawContent => _lastAssistantRawContent;

        /// <summary>最后一条 assistant 可见规划 trace 状态</summary>
        public VisiblePlanningTraceState LastAssistantPlanningTraceState => _lastAssistantPlanningTraceState;

        /// <summary>编译是否成功</summary>
        public bool CompilationSucceeded => _compilationSucceeded;

        /// <summary>编译错误信息</summary>
        public string CompilationErrors => _compilationErrors;

        /// <summary>被中断的 tool_call ID</summary>
        public string InterruptedToolCallId => _interruptedToolCallId;

        // === Phase 4.5 新增属性 ===

        /// <summary>文件变更记录的 JSON 序列化数据</summary>
        public string FileChangeRecordsJson => _fileChangeRecordsJson;

        // === Phase 5 新增属性（压缩统计数据）===

        /// <summary>工具结果压缩成功次数</summary>
        public int ToolResultCompressionSuccessCount => _toolResultCompressionSuccessCount;

        /// <summary>对话压缩成功次数</summary>
        public int ConversationCompressionSuccessCount => _conversationCompressionSuccessCount;

        /// <summary>总节省 token 数</summary>
        public int TotalTokensSaved => _totalTokensSaved;

        /// <summary>工具结果压缩前的总 token 数</summary>
        public int ToolResultOriginalTokens => _toolResultOriginalTokens;

        /// <summary>对话压缩前的总 token 数</summary>
        public int ConversationOriginalTokens => _conversationOriginalTokens;

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
        /// <param name="lastAssistantReasoning">最后一条 assistant reasoning 内容（可为 null）</param>
        /// <param name="lastAssistantReasoningSource">最后一条 assistant reasoning 来源</param>
        /// <param name="lastAssistantReasoningDurationMs">最后一条 assistant reasoning 耗时（毫秒）</param>
        /// <param name="lastAssistantRawContent">最后一条 assistant 原始 content（可为 null）</param>
        /// <param name="lastAssistantPlanningTraceState">最后一条 assistant 可见规划 trace 状态</param>
        public void MarkInterrupted(
            string sessionId,
            InterruptPhase phase,
            string lastToolName = null,
            bool hadPendingToolCalls = false,
            string pendingUserMessage = null,
            string lastAssistantContent = null,
            string interruptedToolCallId = null,
            string lastAssistantReasoning = null,
            ThinkingTraceSource lastAssistantReasoningSource = ThinkingTraceSource.None,
            double lastAssistantReasoningDurationMs = 0,
            string lastAssistantRawContent = null,
            VisiblePlanningTraceState lastAssistantPlanningTraceState = VisiblePlanningTraceState.None)
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
            _lastAssistantReasoning = lastAssistantReasoning ?? string.Empty;
            _lastAssistantReasoningSource = lastAssistantReasoningSource;
            _lastAssistantReasoningDurationMs = Math.Max(0, lastAssistantReasoningDurationMs);
            _lastAssistantRawContent = lastAssistantRawContent ?? string.Empty;
            _lastAssistantPlanningTraceState = lastAssistantPlanningTraceState;

            // 编译结果在 afterAssemblyReload 时设置，此处先重置
            _compilationSucceeded = false;
            _compilationErrors = string.Empty;

            // ScriptableSingleton 需要显式标记脏以确保序列化
            SafeSave(true);

            AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] DomainReloadState: Marked interrupted — session={sessionId}, phase={phase}, " +
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
            SafeSave(true);

            AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] DomainReloadState: Compilation result set — succeeded={succeeded}, " +
                      $"errors={(!string.IsNullOrEmpty(errors) ? errors.Substring(0, Math.Min(errors.Length, 200)) : "(none)")}");
        }

        /// <summary>
        /// 保存文件变更记录的 JSON 数据。
        /// 在 <c>AssemblyReloadEvents.beforeAssemblyReload</c> 回调中由 AgentLoop 调用，
        /// 将 <see cref="FileChangeTracker"/> 的记录序列化后保存，以便 Domain Reload 后恢复。
        /// </summary>
        /// <param name="json">文件变更记录的 JSON 字符串（可为 null 或空）</param>
        public void SaveFileChangeRecords(string json)
        {
            _fileChangeRecordsJson = json ?? string.Empty;
            SafeSave(true);
        }

        /// <summary>
        /// 清除文件变更记录数据。
        /// 在会话切换或重置时调用。
        /// </summary>
        public void ClearFileChangeRecords()
        {
            _fileChangeRecordsJson = string.Empty;
            SafeSave(true);
        }

        /// <summary>
        /// 保存压缩统计数据。
        /// 在 <c>AssemblyReloadEvents.beforeAssemblyReload</c> 回调中由 AgentLoop 调用，
        /// 将 <see cref="Compression.CompressionMetrics"/> 的统计数据保存，以便 Domain Reload 后恢复。
        /// </summary>
        /// <param name="toolResultSuccessCount">工具结果压缩成功次数</param>
        /// <param name="conversationSuccessCount">对话压缩成功次数</param>
        /// <param name="totalTokensSaved">总节省 token 数</param>
        /// <param name="toolResultOriginalTokens">工具结果压缩前的总 token 数</param>
        /// <param name="conversationOriginalTokens">对话压缩前的总 token 数</param>
        public void SaveCompressionMetrics(
            int toolResultSuccessCount,
            int conversationSuccessCount,
            int totalTokensSaved,
            int toolResultOriginalTokens,
            int conversationOriginalTokens)
        {
            _toolResultCompressionSuccessCount = toolResultSuccessCount;
            _conversationCompressionSuccessCount = conversationSuccessCount;
            _totalTokensSaved = totalTokensSaved;
            _toolResultOriginalTokens = toolResultOriginalTokens;
            _conversationOriginalTokens = conversationOriginalTokens;
            SafeSave(true);

            AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] DomainReloadState: Compression metrics saved — " +
                      $"toolResults={toolResultSuccessCount}, conversations={conversationSuccessCount}, " +
                      $"tokensSaved={totalTokensSaved}");
        }

        /// <summary>
        /// 清除压缩统计数据。
        /// 在会话切换或重置时调用。
        /// </summary>
        public void ClearCompressionMetrics()
        {
            _toolResultCompressionSuccessCount = 0;
            _conversationCompressionSuccessCount = 0;
            _totalTokensSaved = 0;
            _toolResultOriginalTokens = 0;
            _conversationOriginalTokens = 0;
            SafeSave(true);
        }

        /// <summary>
        /// 清除中断标记。
        /// 在会话恢复完成后调用。
        /// 注意：不清除文件变更记录（<see cref="_fileChangeRecordsJson"/>）和压缩统计数据，
        /// 因为这些数据需要在整个会话期间保留。
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
            _lastAssistantReasoning = string.Empty;
            _lastAssistantReasoningSource = ThinkingTraceSource.None;
            _lastAssistantReasoningDurationMs = 0;
            _lastAssistantRawContent = string.Empty;
            _lastAssistantPlanningTraceState = VisiblePlanningTraceState.None;
            _compilationSucceeded = false;
            _compilationErrors = string.Empty;
            _interruptedToolCallId = string.Empty;
            // 注意：不清除 _fileChangeRecordsJson 和压缩统计数据，这些数据独立于中断状态

            SafeSave(true);

            if (wasInterrupted)
            {
                AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] DomainReloadState: Interruption cleared for session {sessionId}.");
            }
        }

        /// <summary>
        /// Safe wrapper around <see cref="ScriptableSingleton{T}.Save(bool)"/> that ensures the
        /// shared AgentCore preferences directory exists before writing. See
        /// <see cref="PreferencesFolderPathHelper"/> for details.
        /// </summary>
        internal void SafeSave(bool saveAsText)
        {
            if (!PreferencesFolderPathHelper.EnsureAgentCoreDirectory())
            {
                AgentCoreLog.Warning("[AgentCore] Skipping DomainReloadState.Save — preferences directory not available.");
                return;
            }
            try
            {
                Save(saveAsText);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentCore] DomainReloadState.Save failed: {ex.Message}");
            }
        }

        #endregion
    }
}
