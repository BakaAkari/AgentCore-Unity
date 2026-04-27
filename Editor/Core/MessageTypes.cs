using System;
using System.Collections.Generic;

namespace AgentCore.Editor.Core
{
    #region Agent 状态枚举

    /// <summary>
    /// Agent 运行状态枚举。
    /// 描述 Agent Loop 当前所处的生命周期阶段。
    /// </summary>
    public enum AgentState
    {
        /// <summary>空闲，可接受新的用户输入</summary>
        Idle,

        /// <summary>正在思考，等待 LLM 首个响应</summary>
        Thinking,

        /// <summary>正在流式输出 LLM 响应内容</summary>
        Streaming,

        /// <summary>正在执行工具调用</summary>
        ExecutingTool,

        /// <summary>发生错误</summary>
        Error
    }

    #endregion

    #region Agent 事件类型

    /// <summary>
    /// Agent 事件类型枚举。
    /// 用于区分不同类型的 Agent 事件，UI 层据此做出相应处理。
    /// </summary>
    public enum AgentEventType
    {
        // === Phase 1 已有 ===

        /// <summary>Agent 状态发生变更</summary>
        StateChanged,

        /// <summary>收到一个流式 token</summary>
        StreamToken,

        /// <summary>收到完整的助手消息（流式结束后）</summary>
        AssistantMessage,

        /// <summary>发生错误</summary>
        Error,

        /// <summary>对话已重置</summary>
        ConversationReset,

        // === Phase 2 新增 ===

        /// <summary>开始执行工具调用</summary>
        ToolCallStarted,

        /// <summary>工具调用完成</summary>
        ToolCallCompleted,

        /// <summary>工具调用失败</summary>
        ToolCallFailed,

        /// <summary>新一轮循环开始</summary>
        LoopRoundStarted,

        /// <summary>循环结束（最终回答）</summary>
        LoopCompleted
    }

    #endregion

    #region Agent 事件数据

    /// <summary>
    /// Agent 事件数据。
    /// 通过静态工厂方法创建，携带事件类型和相关数据。
    /// UI 层通过订阅 <see cref="AgentLoop.OnAgentEvent"/> 接收此事件。
    /// </summary>
    public class AgentEvent
    {
        /// <summary>事件类型</summary>
        public AgentEventType Type { get; }

        /// <summary>Agent 状态（<see cref="AgentEventType.StateChanged"/> 时有值）</summary>
        public AgentState State { get; }

        /// <summary>
        /// 内容文本。
        /// <see cref="AgentEventType.StreamToken"/> 时为单个 token，
        /// <see cref="AgentEventType.AssistantMessage"/> 时为完整消息，
        /// <see cref="AgentEventType.Error"/> 时为错误信息。
        /// </summary>
        public string Content { get; }

        /// <summary>消息唯一标识（关联到 <see cref="ConversationTurn.Id"/>）</summary>
        public string MessageId { get; }

        /// <summary>工具名称（工具调用事件时有值）</summary>
        public string ToolName { get; }

        /// <summary>工具调用唯一标识（LLM 返回的 tool_call id，用于区分同名工具的多次调用）</summary>
        public string ToolCallId { get; }

        /// <summary>工具参数（JSON string，<see cref="AgentEventType.ToolCallStarted"/> 时有值）</summary>
        public string ToolArguments { get; }

        /// <summary>工具执行结果（<see cref="AgentEventType.ToolCallCompleted"/> 或 <see cref="AgentEventType.ToolCallFailed"/> 时有值）</summary>
        public string ToolResult { get; }

        /// <summary>当前循环轮次（<see cref="AgentEventType.LoopRoundStarted"/> 时有值）</summary>
        public int CurrentRound { get; }

        /// <summary>最大轮次（<see cref="AgentEventType.LoopRoundStarted"/> 时有值）</summary>
        public int MaxRounds { get; }

        /// <summary>执行时间（毫秒，工具调用完成/失败事件时有值）</summary>
        public double ExecutionTimeMs { get; }

        /// <summary>
        /// 私有构造函数，强制使用工厂方法创建实例。
        /// </summary>
        private AgentEvent(
            AgentEventType type,
            AgentState state = AgentState.Idle,
            string content = null,
            string messageId = null,
            string toolName = null,
            string toolCallId = null,
            string toolArguments = null,
            string toolResult = null,
            int currentRound = 0,
            int maxRounds = 0,
            double executionTimeMs = 0)
        {
            Type = type;
            State = state;
            Content = content;
            MessageId = messageId;
            ToolName = toolName;
            ToolCallId = toolCallId;
            ToolArguments = toolArguments;
            ToolResult = toolResult;
            CurrentRound = currentRound;
            MaxRounds = maxRounds;
            ExecutionTimeMs = executionTimeMs;
        }

        #region Phase 1 工厂方法

        /// <summary>
        /// 创建状态变更事件。
        /// </summary>
        /// <param name="state">新的 Agent 状态</param>
        /// <returns>状态变更事件</returns>
        public static AgentEvent StateChanged(AgentState state)
        {
            return new AgentEvent(AgentEventType.StateChanged, state: state);
        }

        /// <summary>
        /// 创建流式 token 事件。
        /// </summary>
        /// <param name="token">单个 token 文本</param>
        /// <param name="messageId">所属消息的唯一标识</param>
        /// <returns>流式 token 事件</returns>
        public static AgentEvent StreamToken(string token, string messageId)
        {
            return new AgentEvent(AgentEventType.StreamToken, content: token, messageId: messageId);
        }

        /// <summary>
        /// 创建完整助手消息事件（流式结束后发送）。
        /// </summary>
        /// <param name="fullContent">完整的助手回复内容</param>
        /// <param name="messageId">消息唯一标识</param>
        /// <returns>助手消息事件</returns>
        public static AgentEvent AssistantMessage(string fullContent, string messageId)
        {
            return new AgentEvent(AgentEventType.AssistantMessage, content: fullContent, messageId: messageId);
        }

        /// <summary>
        /// 创建错误事件。
        /// </summary>
        /// <param name="error">错误信息</param>
        /// <returns>错误事件</returns>
        public static AgentEvent ErrorEvent(string error)
        {
            return new AgentEvent(AgentEventType.Error, content: error);
        }

        /// <summary>
        /// 创建对话重置事件。
        /// </summary>
        /// <returns>对话重置事件</returns>
        public static AgentEvent ConversationReset()
        {
            return new AgentEvent(AgentEventType.ConversationReset);
        }

        #endregion

        #region Phase 2 工厂方法

        /// <summary>
        /// 创建工具调用开始事件。
        /// </summary>
        /// <param name="toolName">工具名称</param>
        /// <param name="arguments">工具参数（JSON string）</param>
        /// <param name="messageId">关联的消息 ID</param>
        /// <returns>工具调用开始事件</returns>
        public static AgentEvent ToolCallStarted(string toolName, string arguments, string messageId = null, string toolCallId = null)
        {
            return new AgentEvent(
                AgentEventType.ToolCallStarted,
                toolName: toolName,
                toolCallId: toolCallId,
                toolArguments: arguments,
                messageId: messageId
            );
        }

        /// <summary>
        /// 创建工具调用完成事件。
        /// </summary>
        /// <param name="toolName">工具名称</param>
        /// <param name="result">工具执行结果</param>
        /// <param name="executionTimeMs">执行耗时（毫秒）</param>
        /// <param name="messageId">关联的消息 ID</param>
        /// <returns>工具调用完成事件</returns>
        public static AgentEvent ToolCallCompleted(string toolName, string result, double executionTimeMs, string messageId = null, string toolCallId = null)
        {
            return new AgentEvent(
                AgentEventType.ToolCallCompleted,
                toolName: toolName,
                toolCallId: toolCallId,
                toolResult: result,
                executionTimeMs: executionTimeMs,
                messageId: messageId
            );
        }

        /// <summary>
        /// 创建工具调用失败事件。
        /// </summary>
        /// <param name="toolName">工具名称</param>
        /// <param name="error">错误信息</param>
        /// <param name="executionTimeMs">执行耗时（毫秒）</param>
        /// <param name="messageId">关联的消息 ID</param>
        /// <returns>工具调用失败事件</returns>
        public static AgentEvent ToolCallFailed(string toolName, string error, double executionTimeMs, string messageId = null, string toolCallId = null)
        {
            return new AgentEvent(
                AgentEventType.ToolCallFailed,
                toolName: toolName,
                toolCallId: toolCallId,
                toolResult: error,
                executionTimeMs: executionTimeMs,
                messageId: messageId
            );
        }

        /// <summary>
        /// 创建循环轮次开始事件。
        /// </summary>
        /// <param name="currentRound">当前轮次（从 1 开始）</param>
        /// <param name="maxRounds">最大轮次</param>
        /// <returns>循环轮次开始事件</returns>
        public static AgentEvent LoopRoundStarted(int currentRound, int maxRounds)
        {
            return new AgentEvent(
                AgentEventType.LoopRoundStarted,
                currentRound: currentRound,
                maxRounds: maxRounds
            );
        }

        /// <summary>
        /// 创建循环结束事件。
        /// </summary>
        /// <param name="totalRounds">总共执行的轮次数</param>
        /// <returns>循环结束事件</returns>
        public static AgentEvent LoopCompleted(int totalRounds)
        {
            return new AgentEvent(
                AgentEventType.LoopCompleted,
                currentRound: totalRounds
            );
        }

        #endregion
    }

    #endregion

    #region 工具调用信息

    /// <summary>
    /// 工具调用信息 — 记录单次工具调用的完整生命周期数据。
    /// <para>
    /// 用于 <see cref="ConversationTurn.ToolCalls"/> 中，记录本轮对话中所有工具调用的详细信息。
    /// 初始创建时仅包含请求信息，执行完成后填充结果字段。
    /// </para>
    /// </summary>
    public class ToolCallInfo
    {
        /// <summary>工具调用 ID（对应 LLM 返回的 tool_call_id）</summary>
        public string Id { get; }

        /// <summary>工具名称</summary>
        public string ToolName { get; }

        /// <summary>工具参数（JSON string）</summary>
        public string Arguments { get; }

        /// <summary>执行结果（可变，执行后填充）</summary>
        public string Result { get; set; }

        /// <summary>执行是否成功（可变，执行后填充）</summary>
        public bool Success { get; set; }

        /// <summary>执行耗时（毫秒，可变，执行后填充）</summary>
        public double ExecutionTimeMs { get; set; }

        /// <summary>调用开始时间</summary>
        public DateTime StartTime { get; }

        /// <summary>调用结束时间（可变，执行后填充）</summary>
        public DateTime? EndTime { get; set; }

        /// <summary>
        /// 创建工具调用信息实例。
        /// </summary>
        /// <param name="id">工具调用 ID</param>
        /// <param name="toolName">工具名称</param>
        /// <param name="arguments">工具参数（JSON string）</param>
        public ToolCallInfo(string id, string toolName, string arguments)
        {
            Id = id ?? string.Empty;
            ToolName = toolName ?? string.Empty;
            Arguments = arguments ?? string.Empty;
            StartTime = DateTime.UtcNow;
            Success = false;
        }

        /// <inheritdoc />
        public override string ToString()
        {
            var status = EndTime.HasValue ? (Success ? "OK" : "FAIL") : "PENDING";
            return $"ToolCallInfo[{ToolName}] {status} ({ExecutionTimeMs:F1}ms)";
        }
    }

    #endregion

    #region 对话轮次记录

    /// <summary>
    /// 对话轮次记录。
    /// 用于 UI 显示和对话历史管理，与 LLM 消息历史（<see cref="LLM.ChatMessage"/>）分离。
    /// 支持流式输出时逐步追加内容。
    /// </summary>
    public class ConversationTurn
    {
        /// <summary>轮次唯一标识（GUID）</summary>
        // P3-1 fix: 使用 internal set 代替反射设置 backing field
        public string Id { get; internal set; }

        /// <summary>角色标识：&quot;user&quot; / &quot;assistant&quot; / &quot;system&quot;</summary>
        public string Role { get; }

        /// <summary>消息内容（可变，流式输出时逐步追加）</summary>
        public string Content { get; set; }

        /// <summary>消息创建时间戳</summary>
        // P3-1 fix: 使用 internal set 代替反射设置 backing field
        public DateTime Timestamp { get; internal set; }

        /// <summary>是否正在流式输出中</summary>
        public bool IsStreaming { get; set; }

        /// <summary>本轮的工具调用信息列表（Phase 2 新增，可为 null 表示无工具调用）</summary>
        public List<ToolCallInfo> ToolCalls { get; set; }

        /// <summary>
        /// 创建一个新的对话轮次。
        /// </summary>
        /// <param name="role">角色标识</param>
        /// <param name="content">初始内容（可为空）</param>
        public ConversationTurn(string role, string content = "")
        {
            Id = Guid.NewGuid().ToString();
            Role = role;
            Content = content ?? "";
            Timestamp = DateTime.UtcNow;
            IsStreaming = false;
        }
    }

    #endregion
}
