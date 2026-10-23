using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using AgentCore.Editor.LLM.Anthropic;

namespace AgentCore.Editor.LLM
{
    /// <summary>
    /// 带状态码与 <c>Retry-After</c> 的 HTTP 错误。继承 <see cref="HttpRequestException"/>，
    /// 原有按 HttpRequestException / 消息字符串判断的调用方行为不变；消息格式保持
    /// <c>LLM API error: HTTP {code} {reason}\n{body}</c>。
    /// </summary>
    public class LlmHttpException : HttpRequestException
    {
        public int StatusCode { get; }

        /// <summary>服务端要求的最短等待时间（来自 <c>retry-after-ms</c> / <c>Retry-After</c>），没有则为 null。</summary>
        public TimeSpan? RetryAfter { get; }

        public LlmHttpException(int statusCode, string reasonPhrase, string body, TimeSpan? retryAfter)
            : base($"LLM API error: HTTP {statusCode} {reasonPhrase}\n{body}")
        {
            StatusCode = statusCode;
            RetryAfter = retryAfter;
        }

        /// <summary>从失败响应构造（须在 Dispose 响应之前调用）。</summary>
        public static LlmHttpException FromResponse(HttpResponseMessage response, string body)
        {
            return new LlmHttpException((int)response.StatusCode, response.ReasonPhrase, body,
                LlmRetryPolicy.GetRetryAfter(response, DateTimeOffset.UtcNow));
        }
    }

    /// <summary>
    /// LLM 请求重试策略的纯逻辑部分：<c>Retry-After</c> 解析、指数退避 + 抖动、失败阶段分类。
    /// 不持有状态，供 <c>FallbackRouter</c> 调用并单测。
    /// </summary>
    public static class LlmRetryPolicy
    {
        /// <summary>
        /// 自动等待 <c>Retry-After</c> 的上限。服务端要求等待更久时不自动重试（直接走 fallback 链 / 报错），
        /// 避免在用户无感知的情况下卡住几分钟。
        /// </summary>
        public static readonly TimeSpan MaxHonouredRetryAfter = TimeSpan.FromSeconds(120);

        /// <summary>
        /// 解析 <c>Retry-After</c> 值：delta-seconds（允许小数）或 HTTP 日期（RFC 1123 等）。
        /// 已过去的日期视为 0；无法解析返回 null。
        /// </summary>
        public static TimeSpan? ParseRetryAfter(string value, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var s = value.Trim();

            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return null;
                return TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.MaxValue.TotalSeconds / 2));
            }

            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var date))
            {
                var delta = date - now;
                return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
            }

            return null;
        }

        /// <summary>解析 <c>retry-after-ms</c>（OpenAI 等网关的毫秒扩展头）。</summary>
        public static TimeSpan? ParseRetryAfterMs(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)) return null;
            if (double.IsNaN(ms) || double.IsInfinity(ms) || ms < 0) return null;
            return TimeSpan.FromMilliseconds(Math.Min(ms, TimeSpan.MaxValue.TotalMilliseconds / 2));
        }

        /// <summary>从响应头读取服务端要求的等待时间：<c>retry-after-ms</c> 优先，其次 <c>Retry-After</c>。</summary>
        public static TimeSpan? GetRetryAfter(HttpResponseMessage response, DateTimeOffset now)
        {
            if (response == null) return null;
            try
            {
                if (response.Headers.TryGetValues("retry-after-ms", out var msValues))
                {
                    var ms = ParseRetryAfterMs(msValues.FirstOrDefault());
                    if (ms.HasValue) return ms;
                }
                if (response.Headers.TryGetValues("Retry-After", out var values))
                    return ParseRetryAfter(values.FirstOrDefault(), now);
            }
            catch
            {
                // 头部格式异常不影响错误本身的上报
            }
            return null;
        }

        /// <summary>异常链中携带的 <c>Retry-After</c>（目前只有 <see cref="LlmHttpException"/> 携带）。</summary>
        public static TimeSpan? GetRetryAfter(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is LlmHttpException http) return http.RetryAfter;
            }
            return null;
        }

        /// <summary>
        /// 指数退避 + 等比抖动（equal jitter）：第 <paramref name="retryNumber"/> 次重试（从 1 开始）的上限为
        /// <c>min(maxDelay, baseDelay * 2^(n-1))</c>，实际等待在 [上限/2, 上限] 内按 <paramref name="jitter01"/> 取值。
        /// 保留一半确定性下限，避免抖动到接近 0 时对已过载的服务端立即重打。
        /// </summary>
        public static TimeSpan ComputeBackoff(int retryNumber, TimeSpan baseDelay, TimeSpan maxDelay, double jitter01)
        {
            if (retryNumber < 1) retryNumber = 1;
            if (baseDelay < TimeSpan.Zero) baseDelay = TimeSpan.Zero;
            if (maxDelay < baseDelay) maxDelay = baseDelay;
            jitter01 = double.IsNaN(jitter01) ? 0 : Math.Max(0, Math.Min(1, jitter01));

            double capMs = baseDelay.TotalMilliseconds * Math.Pow(2, Math.Min(retryNumber - 1, 30));
            capMs = Math.Min(capMs, maxDelay.TotalMilliseconds);
            return TimeSpan.FromMilliseconds(capMs / 2 + capMs / 2 * jitter01);
        }

        /// <summary>
        /// 本次重试前的等待：服务端给了 <c>Retry-After</c> 就按它等（不短于它，也不额外加长），否则用指数退避。
        /// </summary>
        public static TimeSpan ComputeDelay(int retryNumber, TimeSpan? retryAfter, TimeSpan baseDelay, TimeSpan maxDelay, double jitter01)
        {
            if (retryAfter.HasValue)
                return retryAfter.Value < TimeSpan.Zero ? TimeSpan.Zero : retryAfter.Value;
            return ComputeBackoff(retryNumber, baseDelay, maxDelay, jitter01);
        }

        /// <summary>
        /// 失败发生在响应流开始之后（流中途断开 / 空闲超时 / 流内 error 事件）返回 true；
        /// 连接、响应头阶段的失败（HTTP 错误码、连接被拒、等待响应头超时等）返回 false。
        /// 两类失败分别计入独立的重试预算。
        /// </summary>
        public static bool IsMidStreamFailure(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                switch (e)
                {
                    case LlmStreamIdleTimeoutException _:
                    case LlmStreamErrorException _:
                    case AnthropicApiException _:
                    case IOException _: // 含 EndOfStreamException
                        return true;
                    case HttpRequestException _:
                        return false;
                }
            }
            return false;
        }

        /// <summary>HTTP 状态码是否值得重试：408 / 425 / 429 / 5xx（含 Anthropic 529 overloaded）。</summary>
        public static bool IsRetryableStatus(int status)
        {
            return status == 408 || status == 425 || status == 429 || status >= 500;
        }

        /// <summary>给状态提示用的简短失败原因。</summary>
        public static string ShortReason(Exception ex)
        {
            switch (ex)
            {
                case LlmHttpException http: return $"HTTP {http.StatusCode}";
                case LlmStreamIdleTimeoutException _: return "stream idle timeout";
                case EndOfStreamException _: return "stream interrupted";
                case TimeoutException _: return "timeout";
            }
            var msg = ex?.Message ?? "unknown error";
            int nl = msg.IndexOf('\n');
            if (nl >= 0) msg = msg.Substring(0, nl);
            return msg.Length > 80 ? msg.Substring(0, 80) + "…" : msg;
        }
    }
}
