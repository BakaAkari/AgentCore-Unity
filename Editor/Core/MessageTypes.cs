using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Linq;
using AgentCore.Editor.Tools.Safety;

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

        /// <summary>正在压缩上下文（Phase 5）</summary>
        Compressing,

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
        LoopCompleted,

        // === Phase 4.5 新增 ===

        /// <summary>文件变更列表更新（工具执行后触发）</summary>
        FileChangesUpdated,

        // === G.1 治理层新增 ===

        /// <summary>
        /// 工具执行前需要用户确认（<see cref="ToolPolicyOutcome.RequireConfirmation"/>）。
        /// UI 层应展示 <see cref="AgentEvent.ConfirmationRequest"/> 的内容，但实际确认交互由
        /// <see cref="IToolConfirmationProvider"/> 在 dispatcher 内完成；本事件用于审计/追踪。
        /// </summary>
        ToolConfirmationRequested,

        /// <summary>
        /// 工具被治理层硬阻断（<see cref="ToolPolicyOutcome.Block"/>），不会执行。
        /// 例如对 Unity Hub Root 或 Package Root 的写操作。
        /// UI 层应显式提示用户为何被阻断（<see cref="AgentEvent.Policy"/>.Reasons）。
        /// </summary>
        ToolBlocked
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
        /// 结构化错误详情（<see cref="AgentEventType.Error"/> 时有值）。
        /// 包含异常类型、HTTP 状态码、堆栈摘要等信息，用于 UI 展示详细错误。
        /// </summary>
        public ErrorDetail Detail { get; }

        /// <summary>
        /// 文件变更摘要列表（<see cref="AgentEventType.FileChangesUpdated"/> 时有值）。
        /// 包含当前会话中所有被修改文件的合并摘要。
        /// </summary>
        public List<FileChangeSummary> FileChanges { get; }

        /// <summary>
        /// 治理层评估结果（<see cref="AgentEventType.ToolConfirmationRequested"/> 与
        /// <see cref="AgentEventType.ToolBlocked"/> 时有值）。
        /// 包含 Outcome / Risk / Reasons，用于 UI 展示判定依据与审计。
        /// </summary>
        public ToolPolicyDecision? Policy { get; }

        /// <summary>
        /// 工具确认请求详情（<see cref="AgentEventType.ToolConfirmationRequested"/> 时有值）。
        /// 实际的确认 UI 交互由 <see cref="IToolConfirmationProvider"/> 完成，
        /// 本字段仅作为事件总线上的快照，供日志/审计/侧栏面板使用。
        /// </summary>
        public ToolConfirmationRequest ConfirmationRequest { get; }

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
            double executionTimeMs = 0,
            ErrorDetail detail = null,
            List<FileChangeSummary> fileChanges = null,
            ToolPolicyDecision? policy = null,
            ToolConfirmationRequest confirmationRequest = null)
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
            Detail = detail;
            FileChanges = fileChanges;
            Policy = policy;
            ConfirmationRequest = confirmationRequest;
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
        /// 创建错误事件（简单文本）。
        /// </summary>
        /// <param name="error">错误信息</param>
        /// <returns>错误事件</returns>
        public static AgentEvent ErrorEvent(string error)
        {
            return new AgentEvent(AgentEventType.Error, content: error,
                detail: ErrorDetail.FromMessage(error));
        }

        /// <summary>
        /// 创建错误事件（从异常，携带完整结构化信息）。
        /// </summary>
        /// <param name="ex">异常对象</param>
        /// <param name="context">错误发生的上下文描述（如 "LLM 请求"、"Domain Reload 恢复"）</param>
        /// <returns>携带 ErrorDetail 的错误事件</returns>
        public static AgentEvent ErrorEvent(Exception ex, string context = null)
        {
            var detail = ErrorDetail.FromException(ex, context);
            return new AgentEvent(AgentEventType.Error, content: detail.Message, detail: detail);
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

        #region Phase 4.5 工厂方法

        /// <summary>
        /// 创建文件变更更新事件。
        /// </summary>
        /// <param name="changes">文件变更摘要列表</param>
        /// <returns>文件变更更新事件</returns>
        public static AgentEvent FileChangesUpdated(List<FileChangeSummary> changes)
        {
            return new AgentEvent(
                AgentEventType.FileChangesUpdated,
                fileChanges: changes
            );
        }

        #endregion

        #region G.1 治理层工厂方法

        /// <summary>
        /// 创建工具确认请求事件（治理层评估为 <see cref="ToolPolicyOutcome.RequireConfirmation"/>）。
        /// 该事件仅作为审计/UI 快照，实际确认交互由 dispatcher 内的 <see cref="IToolConfirmationProvider"/> 完成。
        /// </summary>
        /// <param name="toolName">工具名称</param>
        /// <param name="toolCallId">LLM tool_call id</param>
        /// <param name="decision">治理层评估结果</param>
        /// <param name="messageId">关联消息 ID</param>
        /// <returns>工具确认请求事件</returns>
        public static AgentEvent ToolConfirmationRequested(
            string toolName,
            string toolCallId,
            ToolPolicyDecision decision,
            string messageId = null)
        {
            return new AgentEvent(
                AgentEventType.ToolConfirmationRequested,
                toolName: toolName,
                toolCallId: toolCallId,
                messageId: messageId,
                policy: decision,
                confirmationRequest: decision.ConfirmationRequest
            );
        }

        /// <summary>
        /// 创建工具阻断事件（治理层评估为 <see cref="ToolPolicyOutcome.Block"/>）。
        /// 该工具不会执行，对应的 ToolResult 会被构造为 Fail 并返回 LLM。
        /// </summary>
        /// <param name="toolName">工具名称</param>
        /// <param name="toolCallId">LLM tool_call id</param>
        /// <param name="decision">治理层评估结果（包含阻断原因）</param>
        /// <param name="messageId">关联消息 ID</param>
        /// <returns>工具阻断事件</returns>
        public static AgentEvent ToolBlocked(
            string toolName,
            string toolCallId,
            ToolPolicyDecision decision,
            string messageId = null)
        {
            string reasonText = (decision.Reasons != null && decision.Reasons.Count > 0)
                ? string.Join("; ", decision.Reasons)
                : "blocked by governance policy";

            return new AgentEvent(
                AgentEventType.ToolBlocked,
                toolName: toolName,
                toolCallId: toolCallId,
                toolResult: reasonText,
                messageId: messageId,
                policy: decision
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

    #region 错误详情

    /// <summary>
    /// 结构化错误详情。
    /// <para>
    /// 用于在 UI 中展示详细的错误信息，帮助用户快速定位问题原因。
    /// 包含错误分类、异常类型、HTTP 状态码、堆栈摘要等。
    /// </para>
    /// </summary>
    public class ErrorDetail
    {
        /// <summary>错误分类标签（如 "网络错误"、"认证失败"、"流式解析错误"）</summary>
        public string Category { get; set; }

        /// <summary>用户可读的错误消息</summary>
        public string Message { get; set; }

        /// <summary>异常类型全名（如 "System.Net.Http.HttpRequestException"）</summary>
        public string ExceptionType { get; set; }

        /// <summary>HTTP 状态码（如果是 HTTP 错误，否则为 0）</summary>
        public int HttpStatusCode { get; set; }

        /// <summary>堆栈摘要（前 5 行）</summary>
        public string StackSummary { get; set; }

        /// <summary>错误发生的上下文（如 "LLM 请求"、"工具执行"）</summary>
        public string Context { get; set; }

        /// <summary>内部异常消息（如果有）</summary>
        public string InnerMessage { get; set; }

        /// <summary>错误发生时间</summary>
        public DateTime Timestamp { get; set; } = DateTime.Now;

        /// <summary>
        /// 从异常创建 ErrorDetail，自动提取结构化信息。
        /// </summary>
        /// <param name="ex">异常对象</param>
        /// <param name="context">错误上下文描述</param>
        /// <returns>结构化错误详情</returns>
        public static ErrorDetail FromException(Exception ex, string context = null)
        {
            if (ex == null) return FromMessage("未知错误");

            var detail = new ErrorDetail
            {
                Message = ex.Message,
                ExceptionType = ex.GetType().FullName,
                Context = context,
                InnerMessage = ex.InnerException?.Message,
                StackSummary = TruncateStack(ex.StackTrace, 5)
            };

            // 分类和 HTTP 状态码提取
            ClassifyException(ex, detail);

            // 如果是包装异常（如 FallbackRouter 的重试失败），解析内部异常
            if (ex.InnerException != null)
            {
                ClassifyException(ex.InnerException, detail);
            }

            return detail;
        }

        /// <summary>
        /// 从简单文本消息创建 ErrorDetail。
        /// </summary>
        /// <param name="message">错误消息</param>
        /// <returns>结构化错误详情</returns>
        public static ErrorDetail FromMessage(string message)
        {
            var detail = new ErrorDetail
            {
                Message = message ?? "未知错误",
                Category = ClassifyByMessage(message)
            };
            return detail;
        }

        /// <summary>
        /// 格式化为用户可读的多行文本。
        /// </summary>
        public string FormatForDisplay()
        {
            var sb = new StringBuilder();

            // 错误分类标签
            if (!string.IsNullOrEmpty(Category))
            {
                sb.AppendLine($"[{Category}]");
            }

            // 主要错误消息
            sb.AppendLine(Message);

            // HTTP 状态码
            if (HttpStatusCode > 0)
            {
                sb.AppendLine($"HTTP {HttpStatusCode} — {GetHttpStatusDescription(HttpStatusCode)}");
            }

            // 异常类型
            if (!string.IsNullOrEmpty(ExceptionType))
            {
                sb.AppendLine($"异常类型: {GetShortTypeName(ExceptionType)}");
            }

            // 内部异常
            if (!string.IsNullOrEmpty(InnerMessage))
            {
                sb.AppendLine($"内部错误: {InnerMessage}");
            }

            // 上下文
            if (!string.IsNullOrEmpty(Context))
            {
                sb.AppendLine($"发生位置: {Context}");
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// 获取堆栈摘要（用于可展开的详情区域）。
        /// </summary>
        public string GetStackForDisplay()
        {
            if (string.IsNullOrEmpty(StackSummary)) return null;
            return StackSummary;
        }

        /// <summary>
        /// 根据异常类型分类并提取 HTTP 状态码。
        /// </summary>
        private static void ClassifyException(Exception ex, ErrorDetail detail)
        {
            switch (ex)
            {
                case HttpRequestException httpEx:
                    detail.HttpStatusCode = ExtractHttpStatusCode(httpEx.Message);
                    detail.Category = detail.HttpStatusCode switch
                    {
                        401 or 403 => "认证失败",
                        404 => "端点不存在",
                        429 => "请求限流",
                        >= 500 and < 600 => "服务端错误",
                        _ => "网络错误"
                    };
                    break;

                case TaskCanceledException:
                case OperationCanceledException:
                    detail.Category = "请求超时/取消";
                    break;

                case System.IO.IOException:
                    detail.Category = "IO 错误";
                    break;

                case InvalidOperationException:
                    detail.Category = "操作无效";
                    break;

                case ArgumentException:
                    detail.Category = "参数错误";
                    break;

                default:
                    if (string.IsNullOrEmpty(detail.Category))
                    {
                        detail.Category = ClassifyByMessage(ex.Message);
                    }
                    break;
            }
        }

        /// <summary>
        /// 通过消息文本推断错误分类。
        /// </summary>
        private static string ClassifyByMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return "未知错误";

            var lower = message.ToLowerInvariant();

            if (lower.Contains("timeout") || lower.Contains("timed out"))
                return "请求超时";
            if (lower.Contains("connection") && (lower.Contains("refused") || lower.Contains("reset")))
                return "连接失败";
            if (lower.Contains("401") || lower.Contains("unauthorized"))
                return "认证失败";
            if (lower.Contains("403") || lower.Contains("forbidden"))
                return "权限不足";
            if (lower.Contains("404") || lower.Contains("not found"))
                return "端点不存在";
            if (lower.Contains("429") || lower.Contains("rate limit"))
                return "请求限流";
            if (lower.Contains("500") || lower.Contains("internal server"))
                return "服务端错误";
            if (lower.Contains("502") || lower.Contains("bad gateway"))
                return "网关错误";
            if (lower.Contains("503") || lower.Contains("service unavailable"))
                return "服务不可用";
            if (lower.Contains("domain reload"))
                return "Domain Reload";
            if (lower.Contains("[retry]"))
                return "自动重试";
            if (lower.Contains("stream") || lower.Contains("parse") || lower.Contains("json"))
                return "解析错误";

            return "运行时错误";
        }

        /// <summary>
        /// 从 HTTP 错误消息中提取状态码。
        /// </summary>
        private static int ExtractHttpStatusCode(string message)
        {
            if (string.IsNullOrEmpty(message)) return 0;

            // 匹配常见的 HTTP 状态码模式
            var patterns = new[] { "401", "403", "404", "429", "400", "500", "502", "503", "504" };
            foreach (var code in patterns)
            {
                if (message.Contains(code) && int.TryParse(code, out int statusCode))
                    return statusCode;
            }
            return 0;
        }

        /// <summary>
        /// 获取 HTTP 状态码的中文描述。
        /// </summary>
        private static string GetHttpStatusDescription(int code)
        {
            return code switch
            {
                400 => "请求格式错误",
                401 => "未授权（API Key 无效或缺失）",
                403 => "禁止访问（权限不足）",
                404 => "端点不存在（检查 API URL）",
                429 => "请求过于频繁（触发限流）",
                500 => "服务器内部错误",
                502 => "网关错误（上游服务不可用）",
                503 => "服务暂时不可用",
                504 => "网关超时",
                _ => $"HTTP 错误 {code}"
            };
        }

        /// <summary>
        /// 获取类型的短名称（去掉命名空间前缀）。
        /// </summary>
        private static string GetShortTypeName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return "";
            var lastDot = fullName.LastIndexOf('.');
            return lastDot >= 0 ? fullName.Substring(lastDot + 1) : fullName;
        }

        /// <summary>
        /// 截断堆栈信息到指定行数。
        /// </summary>
        private static string TruncateStack(string stackTrace, int maxLines)
        {
            if (string.IsNullOrEmpty(stackTrace)) return null;

            var lines = stackTrace.Split('\n');
            var count = Math.Min(lines.Length, maxLines);
            var sb = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                var line = lines[i].Trim();
                if (!string.IsNullOrEmpty(line))
                {
                    sb.AppendLine(line);
                }
            }
            if (lines.Length > maxLines)
            {
                sb.AppendLine($"... 还有 {lines.Length - maxLines} 行");
            }
            return sb.ToString().TrimEnd();
        }
    }

    #endregion
}
