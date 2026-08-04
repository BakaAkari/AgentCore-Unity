using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.LLM;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Tools.Safety;
using AgentCore.Editor.Utils;
using AgentCore.Editor.Workspace.Safety;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace AgentCore.Editor.Tools
{
    #region ToolCallResult — 工具调用结果

    /// <summary>
    /// 工具调用结果 — 封装原始 <see cref="LLM.ToolCall"/> 信息与执行结果。
    /// <para>
    /// 包含：
    /// <list type="bullet">
    ///   <item>原始的 LLM tool_call 请求（<see cref="ToolCall"/>）</item>
    ///   <item>工具执行结果（<see cref="ToolResult"/>）</item>
    ///   <item>工具名称和执行耗时</item>
    ///   <item>治理层策略决策（<see cref="ToolPolicyDecision"/>，可选）</item>
    /// </list>
    /// </para>
    /// <para>
    /// 通过 <see cref="ToChatMessage"/> 可直接转换为 <c>role="tool"</c> 的 <see cref="ChatMessage"/>，
    /// 用于发回 LLM 继续对话。
    /// </para>
    /// </summary>
    public class ToolCallResult
    {
        /// <summary>原始的 LLM tool_call 请求</summary>
        public ToolCall ToolCall { get; }

        /// <summary>工具执行结果</summary>
        public ToolResult Result { get; }

        /// <summary>工具名称（从 ToolCall.Function.Name 提取）</summary>
        public string ToolName { get; }

        /// <summary>执行耗时（毫秒）</summary>
        public double ExecutionTimeMs { get; }

        /// <summary>
        /// 治理层策略决策。
        /// <para>
        /// <c>null</c> 表示工具在策略评估前就已失败（如未知工具、参数解析失败）。
        /// 非 null 时可用于审计事件发射。
        /// </para>
        /// </summary>
        public ToolPolicyDecision? Decision { get; }

        /// <summary>
        /// 创建工具调用结果实例。
        /// </summary>
        /// <param name="toolCall">原始的 LLM tool_call 请求</param>
        /// <param name="result">工具执行结果</param>
        /// <param name="toolName">工具名称</param>
        /// <param name="executionTimeMs">执行耗时（毫秒）</param>
        /// <param name="decision">治理层策略决策（可选）</param>
        public ToolCallResult(ToolCall toolCall, ToolResult result, string toolName, double executionTimeMs, ToolPolicyDecision? decision = null)
        {
            ToolCall = toolCall ?? throw new ArgumentNullException(nameof(toolCall));
            Result = result ?? throw new ArgumentNullException(nameof(result));
            ToolName = toolName ?? string.Empty;
            ExecutionTimeMs = executionTimeMs;
            Decision = decision;
        }

        /// <summary>
        /// 将此结果转换为 <c>role="tool"</c> 的 <see cref="ChatMessage"/>，用于发回 LLM。
        /// <para>
        /// 使用 <see cref="ChatMessage.Tool(string, string)"/> 工厂方法构造，
        /// tool_call_id 取自 <see cref="ToolCall.Id"/>，
        /// content 取自 <see cref="ToolResult.GetContentForLLM"/>。
        /// </para>
        /// </summary>
        /// <returns>role="tool" 的 ChatMessage</returns>
        public ChatMessage ToChatMessage()
        {
            return ChatMessage.Tool(
                ToolCall.Id,
                Result.GetContentForLLM()
            );
        }

        /// <inheritdoc />
        public override string ToString()
        {
            var status = Result.Success ? "OK" : "FAIL";
            var policyTag = Decision.HasValue ? $" [{Decision.Value.Outcome}]" : "";
            return $"ToolCallResult[{ToolName}] {status}{policyTag} ({ExecutionTimeMs:F1}ms)";
        }
    }

    #endregion

    #region ToolCallDispatcher — 工具调用分发器

    /// <summary>
    /// 工具调用分发器 — 将 LLM 返回的 <see cref="ToolCall"/> 分发到对应的 <see cref="IAgentTool"/> 执行。
    /// <para>
    /// 核心职责：
    /// <list type="number">
    ///   <item>从 <see cref="ToolCall.Function"/>.<see cref="FunctionCall.Name"/> 解析工具名称</item>
    ///   <item>从 <see cref="ToolRegistry"/> 查找对应的 <see cref="IAgentTool"/></item>
    ///   <item>解析 <see cref="FunctionCall.Arguments"/>（JSON string → JObject）</item>
    ///   <item>通过 <see cref="ToolRiskPolicy"/> 评估策略决策（Allow / RequireConfirmation / Block）</item>
    ///   <item>如果工具需要主线程执行，通过 <see cref="EditorApplication.delayCall"/> 调度</item>
    ///   <item>调用 <see cref="IAgentTool.ExecuteAsync"/> 并包装为 <see cref="ToolCallResult"/></item>
    /// </list>
    /// </para>
    /// <para>
    /// 设计要点：
    /// <list type="bullet">
    ///   <item>工具调用<b>串行执行</b>（Unity Editor 不支持并行操作场景对象）</item>
    ///   <item>参数解析容错：无效 JSON 返回错误结果而非抛出异常</item>
    ///   <item>未知工具返回错误结果而非抛出异常</item>
    ///   <item>治理层策略：Block → 直接拒绝；RequireConfirmation → 请求用户确认；Allow → 直接执行</item>
    /// </list>
    /// </para>
    /// </summary>
    public class ToolCallDispatcher
    {
        #region 常量

        /// <summary>日志前缀</summary>
        private const string LogPrefix = "[AgentCore] ToolCallDispatcher: ";

        /// <summary>策略阻断时返回给 LLM 的提示后缀</summary>
        private const string BlockedSuffix = " Do not retry without changing approach.";

        #endregion

        #region 私有字段

        /// <summary>工具注册表引用</summary>
        private readonly ToolRegistry _registry;

        /// <summary>
        /// 用户确认提供器。
        /// <para>
        /// <c>null</c> 为 fail-safe 模式：任何 RequireConfirmation 决策都将自动拒绝。
        /// 正常使用时应通过构造函数注入 ChatWindow 内嵌确认提供者或其他实现。
        /// </para>
        /// </summary>
        private readonly IToolConfirmationProvider _confirmationProvider;

        #endregion

        #region 构造函数

        /// <summary>
        /// 创建工具调用分发器实例。
        /// </summary>
        /// <param name="registry">工具注册表，用于查找工具实例</param>
        /// <param name="confirmationProvider">
        /// 用户确认提供器。传 null 为 fail-safe 模式（RequireConfirmation 时自动拒绝）。
        /// </param>
        /// <exception cref="ArgumentNullException">registry 为 null 时抛出</exception>
        public ToolCallDispatcher(ToolRegistry registry, IToolConfirmationProvider confirmationProvider = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _confirmationProvider = confirmationProvider;
        }

        #endregion

        #region 单个分发

        /// <summary>
        /// 分发执行单个 <see cref="ToolCall"/>。
        /// <para>
        /// 执行流程：
        /// <list type="number">
        ///   <item>从 <c>toolCall.Function.Name</c> 获取工具名称</item>
        ///   <item>从 <see cref="ToolRegistry"/> 查找对应的 <see cref="IAgentTool"/></item>
        ///   <item>如果找不到，返回错误结果</item>
        ///   <item>解析 <c>toolCall.Function.Arguments</c>（JSON string → JObject）</item>
        ///   <item>Schema 参数预校验</item>
        ///   <item>治理层策略评估（<see cref="ToolRiskPolicy.Evaluate"/>）</item>
        ///   <item>如果工具需要主线程执行，使用 <see cref="TaskCompletionSource{T}"/> + <see cref="EditorApplication.delayCall"/> 调度</item>
        ///   <item>调用 <c>tool.ExecuteAsync(parameters, ct)</c></item>
        ///   <item>包装为 <see cref="ToolCallResult"/> 返回</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <param name="toolCall">LLM 返回的工具调用请求</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>工具调用结果</returns>
        /// <exception cref="ArgumentNullException">toolCall 为 null 时抛出</exception>
        public async Task<ToolCallResult> DispatchAsync(ToolCall toolCall, CancellationToken ct = default)
        {
            if (toolCall == null)
                throw new ArgumentNullException(nameof(toolCall));

            var stopwatch = Stopwatch.StartNew();
            var toolName = toolCall.Function?.Name ?? "(unknown)";

            try
            {
                ct.ThrowIfCancellationRequested();

                // 1. 查找工具
                var tool = _registry.GetTool(toolName);
                if (tool == null)
                {
                    stopwatch.Stop();
                    var errorMsg = $"Unknown tool '{toolName}'. Available tools: {GetAvailableToolNames()}";
                    AgentCoreLog.Warning($"{LogPrefix}{errorMsg}");
                    return new ToolCallResult(
                        toolCall,
                        ToolResult.Fail(errorMsg, stopwatch.Elapsed.TotalMilliseconds),
                        toolName,
                        stopwatch.Elapsed.TotalMilliseconds
                    );
                }

                // 2. 解析参数
                var parameters = ParseArguments(toolCall.Function?.Arguments);

                if (parameters == null)
                {
                    stopwatch.Stop();
                    var errorMsg = $"Invalid JSON arguments for tool '{toolName}': {TruncateForLog(toolCall.Function?.Arguments)}";
                    AgentCoreLog.Warning($"{LogPrefix}{errorMsg}");
                    return new ToolCallResult(
                        toolCall,
                        ToolResult.Fail(errorMsg, stopwatch.Elapsed.TotalMilliseconds),
                        toolName,
                        stopwatch.Elapsed.TotalMilliseconds
                    );
                }

                // 3. Schema 参数预校验
                var schema = tool.Metadata.ParametersSchema;
                if (!ToolParameterValidator.Validate(parameters, schema, out var validationError))
                {
                    stopwatch.Stop();
                    var errorMsg = $"Invalid arguments for tool '{toolName}': {validationError}";
                    AgentCoreLog.Warning($"{LogPrefix}{errorMsg}");
                    return new ToolCallResult(
                        toolCall,
                        ToolResult.Fail(errorMsg, stopwatch.Elapsed.TotalMilliseconds),
                        toolName,
                        stopwatch.Elapsed.TotalMilliseconds
                    );
                }

                // 3.5 Play Mode preflight（D3）—— write 类工具在 Play Mode 中一律 Block
                //   v1.11+ (Bug X): 提前提取 action 以支持 ReadOnlyActions 白名单跳过
                //   (对齐 ToolRiskPolicy 只读白名单粒度修复)。
                //   v1.14.6 (batch_execute 确认弹窗降噪): batch_execute 没有单一 "action" 字段，
                //   改为动态检查 operations 数组中每个子操作是否命中各自工具自己声明的
                //   ReadOnlyActions 白名单；全部只读时解析为哨兵 action 值，命中
                //   BatchExecuteTool 自身声明的 ReadOnlyActions，从而在纯读排查场景
                //   (get_info/search/read_file 等组合) 跳过高风险确认。不硬编码任何
                //   具体子工具/action 名单，判定依据是子工具自身已声明的白名单。
                var preflightAction = toolName == BatchExecuteReadOnlyToolName
                    ? ResolveBatchExecuteEffectiveAction(parameters)
                    : ExtractActionFromParameters(parameters);
                if (Safety.PlayModePreflight.IsBlockedInPlayMode(tool.Metadata, preflightAction, out var playModeReason))
                {
                    stopwatch.Stop();
                    AgentCoreLog.Warning($"{LogPrefix}Tool '{toolName}' blocked by Play Mode preflight.");
                    return new ToolCallResult(
                        toolCall,
                        ToolResult.Fail(playModeReason, stopwatch.Elapsed.TotalMilliseconds),
                        toolName,
                        stopwatch.Elapsed.TotalMilliseconds
                    );
                }

                // 4. 治理层策略评估 (G.1)
                var action = preflightAction;
                var paramSummary = BuildParameterSummary(parameters);
                var pathRisk = ToolPathRiskResolver.Resolve(parameters, tool.Metadata, out var pathTargets);
                var decision = ToolRiskPolicy.Evaluate(
                    tool.Metadata,
                    pathRisk,
                    toolName,
                    action,
                    paramSummary,
                    pathTargets);

                switch (decision.Outcome)
                {
                    case ToolPolicyOutcome.Block:
                    {
                        stopwatch.Stop();
                        var reasons = decision.Reasons != null ? string.Join("; ", decision.Reasons) : "Blocked by policy";
                        var blockMsg = $"Tool '{toolName}' blocked: {reasons}.{BlockedSuffix}";
                        AgentCoreLog.Warning($"{LogPrefix}{blockMsg}");
                        return new ToolCallResult(
                            toolCall,
                            ToolResult.Fail(blockMsg, stopwatch.Elapsed.TotalMilliseconds),
                            toolName,
                            stopwatch.Elapsed.TotalMilliseconds,
                            decision
                        );
                    }

                    case ToolPolicyOutcome.RequireConfirmation:
                    {
                        var confirmed = await RequestUserConfirmationAsync(decision, ct);
                        if (!confirmed)
                        {
                            stopwatch.Stop();
                            var rejectMsg = $"Tool '{toolName}' rejected by user.{BlockedSuffix}";
                            AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}{rejectMsg}");
                            return new ToolCallResult(
                                toolCall,
                                ToolResult.Fail(rejectMsg, stopwatch.Elapsed.TotalMilliseconds),
                                toolName,
                                stopwatch.Elapsed.TotalMilliseconds,
                                decision
                            );
                        }
                        // 用户确认通过，继续执行
                        break;
                    }

                    case ToolPolicyOutcome.Allow:
                    default:
                        // 直接放行
                        break;
                }

                // 5. 执行工具
                AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Executing tool '{toolName}' (mainThread={tool.Metadata.RequiresMainThread})");

                ToolResult result;
                if (tool.Metadata.RequiresMainThread)
                {
                    result = await ExecuteOnMainThreadAsync(tool, parameters, ct);
                }
                else
                {
                    result = await tool.ExecuteAsync(parameters, ct);
                }

                stopwatch.Stop();

                AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Tool '{toolName}' completed: {(result.Success ? "OK" : "FAIL")} ({stopwatch.Elapsed.TotalMilliseconds:F1}ms)");

                return new ToolCallResult(
                    toolCall,
                    result,
                    toolName,
                    stopwatch.Elapsed.TotalMilliseconds,
                    decision
                );
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Tool '{toolName}' was cancelled");
                return new ToolCallResult(
                    toolCall,
                    ToolResult.Fail($"Tool '{toolName}' was cancelled", stopwatch.Elapsed.TotalMilliseconds),
                    toolName,
                    stopwatch.Elapsed.TotalMilliseconds
                );
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                AgentCoreLog.Error($"{LogPrefix}Tool '{toolName}' threw exception: {ex.Message}\n{ex.StackTrace}");
                return new ToolCallResult(
                    toolCall,
                    ToolResult.Fail($"Exception executing '{toolName}': {ex.Message}", stopwatch.Elapsed.TotalMilliseconds),
                    toolName,
                    stopwatch.Elapsed.TotalMilliseconds
                );
            }
        }

        #endregion

        #region 批量分发

        /// <summary>
        /// 批量分发执行多个 <see cref="ToolCall"/>。
        /// <para>
        /// 按顺序逐个调用 <see cref="DispatchAsync"/>，收集所有结果。
        /// 单个工具失败不影响其他工具的执行。
        /// </para>
        /// </summary>
        /// <param name="toolCalls">工具调用请求列表</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>工具调用结果列表（顺序与输入一致）</returns>
        public async Task<List<ToolCallResult>> DispatchAllAsync(
            IReadOnlyList<ToolCall> toolCalls,
            CancellationToken ct = default)
        {
            if (toolCalls == null || toolCalls.Count == 0)
                return new List<ToolCallResult>();

            var results = new List<ToolCallResult>(toolCalls.Count);

            // 串行执行（Unity Editor 不支持并行操作场景对象）
            foreach (var toolCall in toolCalls)
            {
                ct.ThrowIfCancellationRequested();
                var result = await DispatchAsync(toolCall, ct);
                results.Add(result);
            }

            var successCount = CountSuccessful(results);
            var failCount = CountFailed(results);
            AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Batch dispatch completed: {toolCalls.Count} calls, {successCount} success, {failCount} failed");

            return results;
        }

        #endregion

        #region 消息转换

        /// <summary>
        /// 将单个 <see cref="ToolCallResult"/> 转换为 <c>role="tool"</c> 的 <see cref="ChatMessage"/>。
        /// </summary>
        /// <param name="result">工具调用结果</param>
        /// <returns>role="tool" 的 ChatMessage</returns>
        public static ChatMessage ToToolMessage(ToolCallResult result)
        {
            if (result == null)
                throw new ArgumentNullException(nameof(result));

            return result.ToChatMessage();
        }

        /// <summary>
        /// 将多个 <see cref="ToolCallResult"/> 转换为 <c>role="tool"</c> 的 <see cref="ChatMessage"/> 列表。
        /// </summary>
        /// <param name="results">工具调用结果列表</param>
        /// <returns>role="tool" 的 ChatMessage 列表</returns>
        public static List<ChatMessage> ToToolMessages(List<ToolCallResult> results)
        {
            if (results == null || results.Count == 0)
                return new List<ChatMessage>();

            var messages = new List<ChatMessage>(results.Count);
            foreach (var result in results)
            {
                messages.Add(result.ToChatMessage());
            }
            return messages;
        }

        #endregion

        #region 内部辅助方法

        /// <summary>
        /// 安全解析工具调用参数（JSON string → JObject）。
        /// <para>
        /// LLM 返回的 <c>function.arguments</c> 可能是无效 JSON，
        /// 此方法使用 <see cref="JsonHelper.ParseObject"/> 进行容错解析。
        /// 空字符串或 null 视为空参数对象（合法）。
        /// </para>
        /// </summary>
        /// <param name="arguments">JSON 格式的参数字符串</param>
        /// <returns>解析后的 JObject，解析失败时返回 null</returns>
        private static JObject ParseArguments(string arguments)
        {
            // 空参数视为合法的空对象
            if (string.IsNullOrWhiteSpace(arguments))
                return new JObject();

            // 使用 JsonHelper 的安全解析
            var parsed = JsonHelper.ParseObject(arguments);
            return parsed; // null 表示解析失败
        }

        /// <summary>
        /// 在 Unity 主线程上执行工具。
        /// <para>
        /// 使用 <see cref="TaskCompletionSource{T}"/> + <see cref="EditorApplication.update"/>
        /// 模式，将异步工具执行调度到主线程。相比 delayCall，update 回调在每一帧都会检查，
        /// 并带有超时保护，避免在长 import / Domain Reload / 模态对话框期间无限挂起。
        /// </para>
        /// </summary>
        /// <param name="tool">要执行的工具</param>
        /// <param name="parameters">工具参数</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>工具执行结果</returns>
        private static async Task<ToolResult> ExecuteOnMainThreadAsync(
            IAgentTool tool,
            JObject parameters,
            CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<ToolResult>();
            var executed = false;

            // DIAG (2026-08-04, 8分钟卡死排查用，一次性诊断日志，非永久遥测):
            // 记录"发起主线程调度请求"到"EditorApplication.update 回调真正被调用"之间的排队耗时。
            // 若下次复现长时间卡死，diagQueueSw 的值能直接说明卡死是发生在"等待被调度到主线程"
            // 这一段（例如 Domain Reload 期间 update 回调整体停摆），还是发生在拿到主线程之后
            // 的工具执行本身（ExecuteToolOnMainThreadAsync 内部）。
            var diagQueueSw = Stopwatch.StartNew();
            AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}[DIAG] MainThread dispatch REQUESTED tool='{tool.Metadata.Name}' at {DateTime.Now:HH:mm:ss.fff}");

            // 注册取消回调
            using var registration = ct.Register(() =>
            {
                tcs.TrySetCanceled(ct);
            });

            // 超时保护：主线程若 30 秒内仍未执行，则返回失败而不是无限挂起
            const int mainThreadTimeoutSeconds = 30;
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(mainThreadTimeoutSeconds));
            using var timeoutRegistration = timeoutCts.Token.Register(() =>
            {
                if (!executed)
                {
                    AgentCore.Editor.Utils.AgentCoreLog.Warning($"{LogPrefix}[DIAG] MainThread dispatch TIMEOUT tool='{tool.Metadata.Name}' after {diagQueueSw.Elapsed.TotalMilliseconds:F0}ms queued (30s timeout fired) at {DateTime.Now:HH:mm:ss.fff}");
                    tcs.TrySetException(new TimeoutException(
                        $"Tool '{tool.Metadata.Name}' timed out after {mainThreadTimeoutSeconds}s waiting for the main thread. " +
                        "This often happens during long asset imports, Domain Reload, or modal dialogs. Please retry after the operation completes."));
                }
            });

            // 调度到主线程执行
            EditorApplication.CallbackFunction callback = null;
            callback = () =>
            {
                EditorApplication.update -= callback;

                if (tcs.Task.IsCompleted)
                    return;

                executed = true;
                // DIAG (2026-08-04, 8分钟卡死排查用): update 回调真正被调用的时刻，
                // 与上面 REQUESTED 日志的时间差 = 主线程排队等待耗时。
                AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}[DIAG] MainThread dispatch CALLBACK_FIRED tool='{tool.Metadata.Name}' queued={diagQueueSw.Elapsed.TotalMilliseconds:F0}ms at {DateTime.Now:HH:mm:ss.fff}");
                _ = ExecuteToolOnMainThreadAsync(tool, parameters, ct, tcs);
            };
            EditorApplication.update += callback;

            return await tcs.Task;
        }

        /// <summary>
        /// 实际在主线程上执行工具的异步包装。
        /// </summary>
        private static async Task ExecuteToolOnMainThreadAsync(
            IAgentTool tool,
            JObject parameters,
            CancellationToken ct,
            TaskCompletionSource<ToolResult> tcs)
        {
            // v1.12.0-alpha.4: 此处曾用 ProfilerMarker.Begin/End 包裹 await tool.ExecuteAsync,
            // 意图观测工具执行耗时. 但 ProfilerMarker 按 frame 校验 Begin/End 配对, 跨 await 到下一帧
            // 会污染 Console:
            //   [Error] Missing Profiler.EndSample: AgentCore.ToolExec
            //   [Error] Non-matching Profiler.EndSample: AgentCore.ToolExec
            // 因此移除. 工具执行耗时可从 Unity Profiler 内建的 UnitySynchronizationContext.ExecuteTasks
            // 采样观测, 不需要自定义 marker.
            try
            {
                var result = await tool.ExecuteAsync(parameters, ct);
                tcs.TrySetResult(result);
            }
            catch (OperationCanceledException oce)
            {
                tcs.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }

        /// <summary>
        /// 请求用户确认工具执行。
        /// <para>
        /// 如果 <see cref="_confirmationProvider"/> 为 null（fail-safe 模式），直接返回 false（拒绝）。
        /// </para>
        /// </summary>
        /// <param name="decision">策略决策（必须包含 ConfirmationRequest）</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>true = 用户确认执行；false = 用户拒绝或超时</returns>
        private async Task<bool> RequestUserConfirmationAsync(ToolPolicyDecision decision, CancellationToken ct)
        {
            if (_confirmationProvider == null)
            {
                // Fail-safe: 无提供器时自动拒绝
                AgentCoreLog.Warning($"{LogPrefix}No confirmation provider configured. Fail-safe: rejecting RequireConfirmation decision.");
                return false;
            }

            if (decision.ConfirmationRequest == null)
            {
                // 防御性检查：RequireConfirmation 必须携带 request
                AgentCoreLog.Error($"{LogPrefix}RequireConfirmation decision without ConfirmationRequest. Rejecting.");
                return false;
            }

            // DIAG (2026-08-04, 8分钟卡死排查用，一次性诊断日志，非永久遥测):
            // 若下次复现"Hold on / busy" 长时间卡死，对比这两行时间戳与 Editor.log 里
            // Reloading assemblies / Compiling shader 等条目的时间戳，可判定卡死时长是否
            // 主要落在"等待用户点击确认框"这一段，还是落在确认之外的其它阶段。
            var diagSw = Stopwatch.StartNew();
            AgentCoreLog.Info($"{LogPrefix}[DIAG] Confirmation wait START tool='{decision.ConfirmationRequest.ToolName}' at {DateTime.Now:HH:mm:ss.fff}");
            var confirmedResult = await _confirmationProvider.RequestConfirmationAsync(decision.ConfirmationRequest, ct);
            diagSw.Stop();
            AgentCoreLog.Info($"{LogPrefix}[DIAG] Confirmation wait END tool='{decision.ConfirmationRequest.ToolName}' result={confirmedResult} waited={diagSw.Elapsed.TotalMilliseconds:F0}ms at {DateTime.Now:HH:mm:ss.fff}");
            return confirmedResult;
        }

        /// <summary>
        /// <c>batch_execute</c> 工具名常量，用于 Play Mode preflight / 风险策略评估中
        /// 触发只读白名单动态判定（见 <see cref="ResolveBatchExecuteEffectiveAction"/>）。
        /// </summary>
        private const string BatchExecuteReadOnlyToolName = "batch_execute";

        /// <summary>
        /// 动态判定 <c>batch_execute</c> 的 <c>operations</c> 数组是否全部由"策略上可直接放行"
        /// 的子操作组成（即每个子操作单独评估都会得到 <see cref="ToolPolicyOutcome.Allow"/>）。
        /// <para>
        /// v1.14.6 (确认弹窗降噪)：batch_execute 因固定声明 <see cref="ToolRiskLevel.High"/>，
        /// 此前无条件对每次调用触发用户确认弹窗，即便子操作全部是 get_info/read_file/search/
        /// read_console 等纯读排查操作。
        /// </para>
        /// <para>
        /// 判定方式：对每个子操作复用现有的 <see cref="Safety.PlayModePreflight.IsBlockedInPlayMode"/>
        /// 与 <see cref="ToolRiskPolicy.Evaluate"/> 完整评估链（与 <see cref="DispatchAsync"/>
        /// 对单个工具调用的判定完全一致），而不是自建一套简化规则——这样能自动覆盖：
        /// 工具级 <see cref="ToolRiskLevel.ReadOnly"/>（如 read_console 整工具只读，无需
        /// 逐 action 声明白名单）、逐 action 的 <see cref="ToolMetadata.ReadOnlyActions"/> 白名单、
        /// <see cref="ToolRiskPolicy"/> 的破坏性 token 兜底、以及 Play Mode 下的写操作拦截。
        /// 任一子操作的独立评估结果不是 Allow（RequireConfirmation / Block / preflight 拒绝），
        /// 或子操作缺失可解析的 tool/action、目标工具未注册，均视为"不能确定全安全"，
        /// 返回空字符串走原有（High 风险确认）路径 —— fail-closed，不放行不确定的批量操作。
        /// 不硬编码任何具体工具名/action 名单，完全依赖各子工具自身已声明的风险元数据。
        /// </para>
        /// </summary>
        /// <param name="parameters">batch_execute 的已解析参数（含 operations 数组）</param>
        /// <returns>全部子操作均可直接放行时返回哨兵 action 值；否则返回空字符串</returns>
        private string ResolveBatchExecuteEffectiveAction(JObject parameters)
        {
            var operations = parameters?["operations"] as JArray;
            if (operations == null || operations.Count == 0)
                return string.Empty;

            foreach (var op in operations)
            {
                var subToolName = op?["tool"]?.Type == JTokenType.String
                    ? op["tool"].Value<string>()
                    : null;
                if (string.IsNullOrEmpty(subToolName))
                    return string.Empty;

                // batch_execute 嵌套 batch_execute 不在本次判定范围内，保守起见直接拒绝自动放行。
                if (string.Equals(subToolName, BatchExecuteReadOnlyToolName, StringComparison.OrdinalIgnoreCase))
                    return string.Empty;

                var subTool = _registry.GetTool(subToolName);
                if (subTool?.Metadata == null)
                    return string.Empty;

                var subArgs = op["args"] as JObject ?? new JObject();
                var subAction = ExtractActionFromParameters(subArgs);

                // 与 DispatchAsync 对单个工具调用完全一致的判定链：Play Mode 拦截 + 风险策略评估。
                if (Safety.PlayModePreflight.IsBlockedInPlayMode(subTool.Metadata, subAction, out _))
                    return string.Empty;

                var subPathRisk = ToolPathRiskResolver.Resolve(subArgs, subTool.Metadata, out var subTargets);
                var subDecision = ToolRiskPolicy.Evaluate(
                    subTool.Metadata,
                    subPathRisk,
                    subToolName,
                    subAction,
                    BuildParameterSummary(subArgs),
                    subTargets);

                if (!subDecision.IsAllowed)
                    return string.Empty;
            }

            return Native.Meta.BatchExecuteTool.AllSubOperationsReadOnlySentinel;
        }

        /// <summary>
        /// 从工具参数中提取 action 字段（用于策略评估）。
        /// 大多数 AgentCore 工具使用 "action" 作为分发键。
        /// </summary>
        /// <param name="parameters">已解析的工具参数</param>
        /// <returns>action 值，不存在时返回空字符串</returns>
        private static string ExtractActionFromParameters(JObject parameters)
        {
            if (parameters == null) return string.Empty;
            var actionToken = parameters["action"];
            if (actionToken == null) return string.Empty;
            return actionToken.Type == JTokenType.String ? actionToken.Value<string>() ?? string.Empty : string.Empty;
        }

        /// <summary>
        /// 从工具参数构建摘要字典（用于策略评估和审计日志）。
        /// <para>
        /// 只取顶层 string/number/boolean 值，跳过嵌套对象和数组以控制摘要大小。
        /// 值超过 100 字符时会截断。
        /// </para>
        /// </summary>
        /// <param name="parameters">已解析的工具参数</param>
        /// <returns>参数名 → 值摘要的只读字典</returns>
        private static IReadOnlyDictionary<string, string> BuildParameterSummary(JObject parameters)
        {
            if (parameters == null || !parameters.HasValues)
                return new Dictionary<string, string>(0);

            var summary = new Dictionary<string, string>(parameters.Count);
            foreach (var prop in parameters.Properties())
            {
                switch (prop.Value.Type)
                {
                    case JTokenType.String:
                    case JTokenType.Integer:
                    case JTokenType.Float:
                    case JTokenType.Boolean:
                        var val = prop.Value.ToString();
                        summary[prop.Name] = val.Length > 100 ? val.Substring(0, 100) + "..." : val;
                        break;
                    case JTokenType.Array:
                        var arr = (JArray)prop.Value;
                        summary[prop.Name] = $"[array, {arr.Count} items]";
                        break;
                    case JTokenType.Object:
                        summary[prop.Name] = "[object]";
                        break;
                    default:
                        // Null, Undefined 等 — 跳过
                        break;
                }
            }
            return summary;
        }

        /// <summary>
        /// 获取所有可用工具名称（用于错误提示）。
        /// </summary>
        /// <returns>逗号分隔的工具名称字符串</returns>
        private string GetAvailableToolNames()
        {
            var names = _registry.GetAllToolNames();
            if (names == null || names.Count == 0)
                return "(none)";

            // 限制显示数量，避免错误消息过长
            const int maxDisplay = 20;
            if (names.Count <= maxDisplay)
                return string.Join(", ", names);

            var displayed = new List<string>(maxDisplay);
            for (int i = 0; i < maxDisplay; i++)
            {
                displayed.Add(names[i]);
            }
            return string.Join(", ", displayed) + $" ... (+{names.Count - maxDisplay} more)";
        }

        /// <summary>
        /// 截断字符串用于日志显示。
        /// </summary>
        /// <param name="value">原始字符串</param>
        /// <param name="maxLength">最大长度，默认 200</param>
        /// <returns>截断后的字符串</returns>
        private static string TruncateForLog(string value, int maxLength = 200)
        {
            if (string.IsNullOrEmpty(value)) return "(empty)";
            return value.Length <= maxLength ? value : value.Substring(0, maxLength) + "...";
        }

        /// <summary>
        /// 统计成功的结果数量。
        /// </summary>
        private static int CountSuccessful(List<ToolCallResult> results)
        {
            int count = 0;
            foreach (var r in results)
            {
                if (r.Result.Success) count++;
            }
            return count;
        }

        /// <summary>
        /// 统计失败的结果数量。
        /// </summary>
        private static int CountFailed(List<ToolCallResult> results)
        {
            int count = 0;
            foreach (var r in results)
            {
                if (!r.Result.Success) count++;
            }
            return count;
        }

        #endregion
    }

    #endregion
}
