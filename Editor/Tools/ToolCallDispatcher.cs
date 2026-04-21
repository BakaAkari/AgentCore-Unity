using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.LLM;
using AgentCore.Editor.Utils;
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
        /// 创建工具调用结果实例。
        /// </summary>
        /// <param name="toolCall">原始的 LLM tool_call 请求</param>
        /// <param name="result">工具执行结果</param>
        /// <param name="toolName">工具名称</param>
        /// <param name="executionTimeMs">执行耗时（毫秒）</param>
        public ToolCallResult(ToolCall toolCall, ToolResult result, string toolName, double executionTimeMs)
        {
            ToolCall = toolCall ?? throw new ArgumentNullException(nameof(toolCall));
            Result = result ?? throw new ArgumentNullException(nameof(result));
            ToolName = toolName ?? string.Empty;
            ExecutionTimeMs = executionTimeMs;
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
            return $"ToolCallResult[{ToolName}] {status} ({ExecutionTimeMs:F1}ms)";
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
    /// </list>
    /// </para>
    /// </summary>
    public class ToolCallDispatcher
    {
        #region 常量

        /// <summary>日志前缀</summary>
        private const string LogPrefix = "[AgentCore] ToolCallDispatcher: ";

        #endregion

        #region 私有字段

        /// <summary>工具注册表引用</summary>
        private readonly ToolRegistry _registry;

        #endregion

        #region 构造函数

        /// <summary>
        /// 创建工具调用分发器实例。
        /// </summary>
        /// <param name="registry">工具注册表，用于查找工具实例</param>
        /// <exception cref="ArgumentNullException">registry 为 null 时抛出</exception>
        public ToolCallDispatcher(ToolRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
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
                    Debug.LogWarning($"{LogPrefix}{errorMsg}");
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
                    Debug.LogWarning($"{LogPrefix}{errorMsg}");
                    return new ToolCallResult(
                        toolCall,
                        ToolResult.Fail(errorMsg, stopwatch.Elapsed.TotalMilliseconds),
                        toolName,
                        stopwatch.Elapsed.TotalMilliseconds
                    );
                }

                // 3. 执行工具
                Debug.Log($"{LogPrefix}Executing tool '{toolName}' (mainThread={tool.Metadata.RequiresMainThread})");

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

                Debug.Log($"{LogPrefix}Tool '{toolName}' completed: {(result.Success ? "OK" : "FAIL")} ({stopwatch.Elapsed.TotalMilliseconds:F1}ms)");

                return new ToolCallResult(
                    toolCall,
                    result,
                    toolName,
                    stopwatch.Elapsed.TotalMilliseconds
                );
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                Debug.Log($"{LogPrefix}Tool '{toolName}' was cancelled");
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
                Debug.LogError($"{LogPrefix}Tool '{toolName}' threw exception: {ex.Message}\n{ex.StackTrace}");
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
        /// 批量分发执行多个 <see cref="ToolCall"/>（顺序执行，不并行）。
        /// <para>
        /// Unity Editor 不支持并行操作场景对象，因此所有工具调用严格按顺序执行。
        /// 如果 <see cref="CancellationToken"/> 被取消，将停止执行剩余的工具调用。
        /// </para>
        /// </summary>
        /// <param name="toolCalls">LLM 返回的工具调用请求列表</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>所有工具调用结果的列表</returns>
        /// <exception cref="ArgumentNullException">toolCalls 为 null 时抛出</exception>
        public async Task<List<ToolCallResult>> DispatchAllAsync(
            List<ToolCall> toolCalls,
            CancellationToken ct = default)
        {
            if (toolCalls == null)
                throw new ArgumentNullException(nameof(toolCalls));

            var results = new List<ToolCallResult>(toolCalls.Count);

            Debug.Log($"{LogPrefix}Dispatching {toolCalls.Count} tool call(s)...");

            foreach (var tc in toolCalls)
            {
                if (ct.IsCancellationRequested)
                {
                    Debug.LogWarning($"{LogPrefix}Cancellation requested, skipping remaining {toolCalls.Count - results.Count} tool call(s)");
                    break;
                }

                var result = await DispatchAsync(tc, ct);
                results.Add(result);
            }

            Debug.Log($"{LogPrefix}Dispatched {results.Count}/{toolCalls.Count} tool call(s) — " +
                       $"success: {CountSuccessful(results)}, failed: {CountFailed(results)}");

            return results;
        }

        #endregion

        #region 结果转换

        /// <summary>
        /// 将单个 <see cref="ToolCallResult"/> 转换为 <c>role="tool"</c> 的 <see cref="ChatMessage"/>，
        /// 用于发回 LLM 继续对话。
        /// </summary>
        /// <param name="result">工具调用结果</param>
        /// <returns>role="tool" 的 ChatMessage</returns>
        /// <exception cref="ArgumentNullException">result 为 null 时抛出</exception>
        public static ChatMessage ToToolMessage(ToolCallResult result)
        {
            if (result == null)
                throw new ArgumentNullException(nameof(result));

            return result.ToChatMessage();
        }

        /// <summary>
        /// 将多个 <see cref="ToolCallResult"/> 转换为 <see cref="ChatMessage"/> 列表，
        /// 用于批量发回 LLM。
        /// </summary>
        /// <param name="results">工具调用结果列表</param>
        /// <returns>role="tool" 的 ChatMessage 列表</returns>
        /// <exception cref="ArgumentNullException">results 为 null 时抛出</exception>
        public static List<ChatMessage> ToToolMessages(List<ToolCallResult> results)
        {
            if (results == null)
                throw new ArgumentNullException(nameof(results));

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
        /// 使用 <see cref="TaskCompletionSource{T}"/> + <see cref="EditorApplication.delayCall"/>
        /// 模式，将异步工具执行调度到主线程。
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

            // 注册取消回调
            using var registration = ct.Register(() =>
            {
                tcs.TrySetCanceled(ct);
            });

            // 调度到主线程执行
            EditorApplication.delayCall += async () =>
            {
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
            };

            return await tcs.Task;
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
