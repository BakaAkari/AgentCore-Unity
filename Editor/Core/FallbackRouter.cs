using System;
using System.IO;
using System.Net.Http;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Config;
using AgentCore.Editor.LLM;
using UnityEngine;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Core
{
    /// <summary>
    /// 降级路由器 - 当主端点失败时自动重试
    /// 当前实现为简单的重试逻辑，未来可扩展为多端点路由
    /// </summary>
    public class FallbackRouter
    {
        private readonly AgentCoreSettings _settings;

        /// <summary>
        /// 连接阶段失败（HTTP 429 / 5xx / 529、连接被拒或重置、等待响应头超时等）的最大重试次数。
        /// </summary>
        public int MaxConnectRetries { get; set; } = 4;

        /// <summary>
        /// 流中途失败（断流、空闲超时、流内 error 事件）的最大重试次数。
        /// 只有本轮尚未向 UI 输出任何内容时才会重试，见 <see cref="ExecuteStreamWithRetryAsync"/>。
        /// </summary>
        public int MaxMidStreamRetries { get; set; } = 2;

        /// <summary>
        /// 指数退避的基准间隔（毫秒）：第 n 次重试等待 [base·2^(n-1)/2, base·2^(n-1)]，上限 <see cref="MaxRetryDelayMs"/>。
        /// 服务端返回 Retry-After 时以服务端为准。
        /// </summary>
        public int RetryDelayMs { get; set; } = 1000;

        /// <summary>
        /// 指数退避单次等待上限（毫秒）。
        /// </summary>
        public int MaxRetryDelayMs { get; set; } = 30000;

        private readonly System.Random _jitter = new System.Random();

        /// <summary>测试用：替换 fallback 链来源（null = 读取当前 profile 的配置）。</summary>
        internal Func<List<ProviderProfile>> FallbackChainOverride { get; set; }

        /// <summary>
        /// 上次错误信息
        /// </summary>
        public string LastError { get; private set; }

        public FallbackRouter(AgentCoreSettings settings)
        {
            _settings = settings;
        }

        /// <summary>
        /// 带重试的流式请求执行。
        /// 适配 ILLMClient.ChatCompletionStreamAsync 的签名。
        /// </summary>
        /// <param name="client">LLM 客户端</param>
        /// <param name="messages">对话消息列表</param>
        /// <param name="onChunk">流式 chunk 回调</param>
        /// <param name="tools">工具定义列表（可选）</param>
        /// <param name="ct">取消令牌</param>
        /// <param name="onStatusUpdate">状态更新回调（可选）</param>
        /// <param name="reasoningLevelOverride">v1.14.10: 会话级思考强度快捷覆盖，透传给
        /// <see cref="ILLMClient.ChatCompletionStreamAsync"/> 同名参数。</param>
        /// <returns>完整的 assistant ChatMessage</returns>
        public async Task<ChatMessage> ExecuteStreamWithRetryAsync(
            ILLMClient client,
            List<ChatMessage> messages,
            Action<StreamChunk> onChunk,
            List<ToolDefinition> tools = null,
            CancellationToken ct = default,
            Action<string> onStatusUpdate = null,
            string reasoningLevelOverride = null)
        {
            // 已有输出推给 UI（文本 / reasoning / 工具调用增量）后不再重试或切换 fallback：
            // 重放会在同一轮里重复输出，且半截工具调用可能已被 UI 展示。直接报错，由用户决定是否重发。
            bool emitted = false;
            Action<StreamChunk> trackedOnChunk = chunk =>
            {
                if (chunk.Type == StreamChunkType.ContentToken ||
                    chunk.Type == StreamChunkType.ReasoningToken ||
                    chunk.Type == StreamChunkType.ToolCallDelta)
                {
                    emitted = true;
                }
                onChunk?.Invoke(chunk);
            };

            // v1.6.5+: 流式路径不做空内容重试
            // 原因：reasoning chunks 已通过 onChunk 发送到 UI，重试会导致重复输出
            // 空内容检测由 CallLLMStreamAsync 返回后处理
            var (ok, result, lastException, actualAttempts) = await RunWithRetriesAsync(
                () => client.ChatCompletionStreamAsync(messages, trackedOnChunk, tools, ct, reasoningLevelOverride: reasoningLevelOverride),
                () => emitted, onStatusUpdate, ct);
            if (ok) return result;

            // v1.17.0: 主 profile 失败后依次尝试 fallback 链
            var (fbOk, fbResult, fbError) = await TryFallbackChainAsync(
                () => client.ChatCompletionStreamAsync(messages, trackedOnChunk, tools, ct, reasoningLevelOverride: reasoningLevelOverride),
                onStatusUpdate, ct, () => emitted);
            if (fbOk) return fbResult;
            lastException = fbError ?? lastException;
            if (emitted)
                throw PartialOutputFailure(lastException);

            // 所有重试都失败了（或遇到不可重试错误）
            throw new Exception(
                $"LLM request failed after {actualAttempts} attempt{(actualAttempts > 1 ? "s" : "")}. Last error: {lastException?.Message}",
                lastException);
        }

        /// <summary>
        /// 带重试的非流式请求执行。
        /// 适配 ILLMClient.ChatCompletionAsync 的签名。
        /// </summary>
        /// <param name="client">LLM 客户端</param>
        /// <param name="messages">对话消息列表</param>
        /// <param name="tools">工具定义列表（可选）</param>
        /// <param name="ct">取消令牌</param>
        /// <param name="onStatusUpdate">状态更新回调（可选）</param>
        /// <returns>完整的 ChatCompletionResponse</returns>
        public async Task<ChatCompletionResponse> ExecuteWithRetryAsync(
            ILLMClient client,
            List<ChatMessage> messages,
            List<ToolDefinition> tools = null,
            CancellationToken ct = default,
            Action<string> onStatusUpdate = null)
        {
            var (ok, result, lastException, actualAttempts) = await RunWithRetriesAsync(async () =>
            {
                var response = await client.ChatCompletionAsync(messages, tools, ct);

                // v1.6.5+: 空内容检测
                var msg = response?.GetMessage();
                if (msg == null || string.IsNullOrEmpty(msg.Content))
                {
                    throw new InvalidOperationException(
                        "LLM returned empty content (reasoning may have consumed the entire max_tokens budget).");
                }

                return response;
            }, null, onStatusUpdate, ct);
            if (ok) return result;

            // v1.17.0: 主 profile 失败后依次尝试 fallback 链
            var (fbOk, fbResult, fbError) = await TryFallbackChainAsync(async () =>
            {
                var response = await client.ChatCompletionAsync(messages, tools, ct);
                var msg = response?.GetMessage();
                if (msg == null || string.IsNullOrEmpty(msg.Content))
                    throw new InvalidOperationException("LLM returned empty content.");
                return response;
            }, onStatusUpdate, ct, null);
            if (fbOk) return fbResult;
            lastException = fbError ?? lastException;

            throw new Exception(
                $"LLM request failed after {actualAttempts} attempt{(actualAttempts > 1 ? "s" : "")}. Last error: {lastException?.Message}",
                lastException);
        }

        /// <summary>
        /// 主 profile 的重试循环：指数退避 + 抖动，服务端给出 Retry-After 时按其等待。
        /// <para>
        /// 连接阶段失败与流中途失败分别计入 <see cref="MaxConnectRetries"/> / <see cref="MaxMidStreamRetries"/>；
        /// 已有输出时（<paramref name="hasEmittedOutput"/>）一律不重试，抛 <see cref="PartialOutputFailure"/>。
        /// 退避等待使用调用方令牌：用户取消会立即中断等待并按取消上抛，不会被当成失败继续重试。
        /// </para>
        /// </summary>
        /// <returns>ok=true 时 result 有效；否则 error 为最后一次失败（交给 fallback 链 / 最终报错）。</returns>
        private async Task<(bool ok, T result, Exception error, int attempts)> RunWithRetriesAsync<T>(
            Func<Task<T>> call, Func<bool> hasEmittedOutput, Action<string> onStatusUpdate, CancellationToken ct)
        {
            int attempts = 0;
            int connectRetries = 0;
            int midStreamRetries = 0;

            while (true)
            {
                attempts++;
                TimeSpan delay = TimeSpan.Zero;
                try
                {
                    var result = await call();
                    LastError = null;
                    return (true, result, null, attempts);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // 用户取消，不重试
                    throw;
                }
                catch (Exception rawEx)
                {
                    var ex = NormalizeTimeout(rawEx);
                    LastError = ex.Message;
                    AgentCoreLog.Warning($"[AgentCore] LLM request failed (attempt {attempts}): {ex.Message}");

                    if (hasEmittedOutput != null && hasEmittedOutput())
                        throw PartialOutputFailure(ex);

                    // 判断是否值得重试
                    if (!IsRetryableError(ex))
                    {
                        AgentCoreLog.Error($"[AgentCore] Non-retryable error, giving up: {ex.Message}");
                        return (false, default, ex, attempts);
                    }

                    // 连接阶段 / 流中途分别计预算
                    bool midStream = LlmRetryPolicy.IsMidStreamFailure(ex);
                    int used = midStream ? midStreamRetries : connectRetries;
                    int budget = midStream ? MaxMidStreamRetries : MaxConnectRetries;
                    if (used >= budget)
                    {
                        AgentCoreLog.Warning($"[AgentCore] {(midStream ? "Mid-stream" : "Connect")} retry budget exhausted ({budget}).");
                        return (false, default, ex, attempts);
                    }

                    // 服务端要求等待过久：不在用户无感知的情况下长时间挂起，交给 fallback 链 / 报错
                    var retryAfter = LlmRetryPolicy.GetRetryAfter(ex);
                    if (retryAfter.HasValue && retryAfter.Value > LlmRetryPolicy.MaxHonouredRetryAfter)
                    {
                        var waitNote = $"Server asked to wait {retryAfter.Value.TotalSeconds:0}s ({LlmRetryPolicy.ShortReason(ex)}); not retrying automatically.";
                        AgentCoreLog.Warning($"[AgentCore] {waitNote}");
                        onStatusUpdate?.Invoke(waitNote);
                        return (false, default, ex, attempts);
                    }

                    if (midStream) midStreamRetries++; else connectRetries++;
                    int retryNumber = used + 1;
                    double jitter;
                    lock (_jitter) jitter = _jitter.NextDouble();
                    delay = LlmRetryPolicy.ComputeDelay(retryNumber, retryAfter,
                        TimeSpan.FromMilliseconds(Math.Max(0, RetryDelayMs)),
                        TimeSpan.FromMilliseconds(Math.Max(0, MaxRetryDelayMs)), jitter);

                    var status = $"Retry attempt {retryNumber}/{budget} in {delay.TotalSeconds:0.#}s " +
                                 $"({LlmRetryPolicy.ShortReason(ex)}{(retryAfter.HasValue ? ", Retry-After" : "")})...";
                    onStatusUpdate?.Invoke(status);
                    AgentCoreLog.Info($"[AgentCore] {status}");
                }

                // 退避等待受用户取消令牌约束：取消时 Task.Delay 抛 OperationCanceledException，按用户取消上抛
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, ct);
                ct.ThrowIfCancellationRequested();
            }
        }

        /// <summary>
        /// 依次在 fallback profile 作用域内执行一次调用（v1.17.0）。
        /// fallback 链来自当前 profile 的 <see cref="ProviderProfile.fallbackProfileIds"/>；每个备用 profile 只尝试一次
        /// （主 profile 已经历完整重试），通过 <see cref="ActiveModelConfig.UseProfile"/> 临时切换连接参数，
        /// 不修改全局 active profile。取消异常直接上抛。
        /// </summary>
        private async Task<(bool ok, T result, Exception error)> TryFallbackChainAsync<T>(
            Func<Task<T>> call, Action<string> onStatusUpdate, CancellationToken ct, Func<bool> hasEmittedOutput)
        {
            // 主 profile 已经输出过内容（例如流中途断开）时不切换模型，避免同一轮重复输出
            if (hasEmittedOutput != null && hasEmittedOutput())
                return (false, default, null);

            List<ProviderProfile> chain;
            try { chain = FallbackChainOverride?.Invoke() ?? AgentCoreProviderProfiles.instance.GetFallbackChain(ActiveModelConfig.ActiveProfile); }
            catch { return (false, default, null); }

            Exception last = null;
            foreach (var profile in chain)
            {
                var name = string.IsNullOrEmpty(profile.displayName) ? profile.modelName : profile.displayName;
                onStatusUpdate?.Invoke($"Falling back to {name}...");
                AgentCoreLog.Info($"[AgentCore] Falling back to profile '{name}' ({profile.modelName})");
                try
                {
                    using (ActiveModelConfig.UseProfile(profile))
                    {
                        var result = await call();
                        LastError = null;
                        return (true, result, null);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception rawEx)
                {
                    var ex = NormalizeTimeout(rawEx);
                    last = ex;
                    LastError = ex.Message;
                    AgentCoreLog.Warning($"[AgentCore] Fallback profile '{name}' failed: {ex.Message}");
                    if (hasEmittedOutput != null && hasEmittedOutput())
                        break;
                }
            }
            return (false, default, last);
        }

        /// <summary>
        /// 调用方令牌未取消时出现的 <see cref="OperationCanceledException"/>（含 <see cref="TaskCanceledException"/>）
        /// 来自 HttpClient.Timeout 等内部超时，不是用户取消：转成 <see cref="TimeoutException"/> 走重试，
        /// 而不是让整个运行按"用户取消"收尾。
        /// </summary>
        private static Exception NormalizeTimeout(Exception ex)
        {
            return ex is OperationCanceledException
                ? new TimeoutException($"LLM request timed out: {ex.Message}", ex)
                : ex;
        }

        private static Exception PartialOutputFailure(Exception ex)
        {
            AgentCoreLog.Warning("[AgentCore] LLM stream failed after partial output; not retrying to avoid duplicated output.");
            return new Exception(
                $"LLM stream was interrupted after partial output (not retried to avoid duplicated output). Last error: {ex?.Message}",
                ex);
        }

        /// <summary>
        /// 判断错误是否可重试。
        /// 优先通过异常类型判断，再回退到消息字符串匹配。
        /// v1.6.5+: 覆盖空内容响应（HTTP 200 但 content 为空）和 JSON 解析失败。
        /// </summary>
        private bool IsRetryableError(Exception ex)
        {
            // 1. 异常类型判断（优先，更可靠）
            switch (ex)
            {
                // TaskCanceledException 由 HttpClient 超时引发 -> 可重试（用户取消在调用方已提前拦截）
                case TaskCanceledException _:
                    return true;

                // 流在 [DONE] / finish_reason / message_stop 之前断开 -> 不完整回复，可重试
                case EndOfStreamException _:
                    return true;

                // 流中途的服务端错误事件：按错误类型判断
                case LlmStreamErrorException streamEx:
                    return streamEx.Retryable;

                // 内容过滤：重试结果相同
                case LlmContentFilterException _:
                    return false;

                // v1.13+ SSE 流空闲超时看门狗 (StreamIdleWatchdog -> LlmStreamIdleTimeoutException)
                // 抛出的 TimeoutException -> 判定为死连接，可重试 (显式类型判断，不依赖消息字符串
                // 匹配 "timeout" —— 该异常消息描述的是空闲时长，未必含这个词，类型判断更可靠)。
                case TimeoutException _:
                    return true;

                // v1.17.0: Anthropic 流中途 error 事件——过载/限流/服务端错误可重试，请求错误不可重试
                case AgentCore.Editor.LLM.Anthropic.AnthropicApiException apiEx:
                    return apiEx.ErrorType is "overloaded_error" or "api_error" or "rate_limit_error" or "timeout_error";

                // 带状态码的 HTTP 错误：408 / 425 / 429 / 5xx（含 529）可重试，其余 4xx 不可重试
                case LlmHttpException statusEx:
                    return LlmRetryPolicy.IsRetryableStatus(statusEx.StatusCode);

                // HttpRequestException 需要进一步检查内容（无状态码：连接被拒 / 重置 / DNS 等）
                case HttpRequestException httpEx:
                    return IsRetryableHttpError(httpEx);

                // v1.6.5+: JSON 解析失败（InvalidOperationException）-> 可重试
                // 服务端可能返回不完整 JSON（网络抖动、SGLang 内部错误）
                case InvalidOperationException _:
                    return true;
            }

            // 2. 回退到消息字符串匹配（处理非标准异常包装）
            var message = ex.Message.ToLowerInvariant();

            // 网络超时、连接错误 -> 可重试
            if (message.Contains("timeout") || message.Contains("timed out"))
                return true;
            if (message.Contains("connection") && (message.Contains("refused") || message.Contains("reset")))
                return true;

            // 服务端错误 -> 可重试
            if (message.Contains("502") || message.Contains("503") || message.Contains("504"))
                return true;
            if (message.Contains("rate limit") || message.Contains("429"))
                return true;

            // v1.6.5+: 空内容响应 -> 可重试
            // GLM-5.2 reasoning 吃光 maxTokens 时返回空 content + finish_reason=length
            if (message.Contains("empty") || message.Contains("no content") || message.Contains("未返回任何内容"))
                return true;

            // 认证错误、参数错误 -> 不可重试
            if (message.Contains("401") || message.Contains("403") || message.Contains("unauthorized"))
                return false;
            if (message.Contains("400") || message.Contains("bad request"))
                return false;
            if (message.Contains("404") || message.Contains("not found"))
                return false;

            // 默认可重试
            return true;
        }

        /// <summary>
        /// 判断 HttpRequestException 是否可重试。
        /// </summary>
        private static bool IsRetryableHttpError(HttpRequestException ex)
        {
            var message = ex.Message.ToLowerInvariant();

            // 连接被拒绝 / 重置 -> 可重试（服务暂时不可用）
            if (message.Contains("connection") && (message.Contains("refused") || message.Contains("reset")))
                return true;

            // 服务端错误 (5xx) 和限流 (429) -> 可重试；529 = Anthropic overloaded
            if (message.Contains("500") || message.Contains("502") || message.Contains("503") || message.Contains("504") || message.Contains("429") || message.Contains("529"))
                return true;

            // 客户端错误 (4xx 非 429) -> 不可重试
            if (message.Contains("401") || message.Contains("403") || message.Contains("400") || message.Contains("404"))
                return false;

            // 其他 HTTP 错误默认可重试
            return true;
        }
    }
}
