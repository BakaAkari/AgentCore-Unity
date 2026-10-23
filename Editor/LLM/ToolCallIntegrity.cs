using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.LLM
{
    /// <summary>
    /// 模型返回的 tool_call 完整性校验：截断（finish_reason=length）或参数不是合法 JSON 对象的调用
    /// 标记 <see cref="ToolCall.InvalidReason"/>，由 <c>ToolCallDispatcher</c> 直接返回错误结果而不执行。
    /// <para>
    /// 参考 Codex / Claude Code：截断的参数绝不"修补后执行"——半截 JSON 被正则拼回去可能变成
    /// 语义完全不同的调用（例如 write_file 的 content 只剩一半）。只把错误回传给模型让它重发更小的调用。
    /// </para>
    /// </summary>
    public static class ToolCallIntegrity
    {
        public const string TruncatedReason =
            "Tool call was not executed: the model output hit the max token limit (finish_reason=length) " +
            "while generating this call, so its arguments are truncated. Re-issue the call with smaller arguments " +
            "(e.g. split large file content into several edits).";

        public const string InvalidJsonReason =
            "Tool call was not executed: its arguments are not a valid JSON object. Re-issue the call with valid JSON arguments.";

        /// <summary>
        /// 校验并标记一批 tool_call。<paramref name="finishReason"/> 为 OpenAI 语义（Anthropic 先经 MapStopReason 映射）。
        /// </summary>
        public static void MarkInvalid(IList<ToolCall> calls, string finishReason)
        {
            if (calls == null) return;
            bool truncated = string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase);
            foreach (var call in calls)
            {
                if (call == null || call.InvalidReason != null) continue;
                if (truncated)
                    call.InvalidReason = TruncatedReason;
                else if (!IsValidArgumentsObject(call.Function?.Arguments))
                    call.InvalidReason = InvalidJsonReason;
            }
        }

        /// <summary>
        /// 参数必须解析为 JSON 对象；空串视为 <c>{}</c>。仅含 <c>_raw_arguments</c> 的对象是历史修复时
        /// 对非法参数的包装（见 <c>OpenAICompatibleClient.SanitizeToolArguments</c>），同样视为非法。
        /// </summary>
        public static bool IsValidArgumentsObject(string arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments)) return true;
            try
            {
                return JToken.Parse(arguments) is JObject obj && !IsRawArgumentsWrapper(obj);
            }
            catch
            {
                return false;
            }
        }

        public static bool IsRawArgumentsWrapper(JObject obj)
        {
            return obj != null && obj.Count == 1 && obj.ContainsKey("_raw_arguments");
        }
    }

    /// <summary>
    /// 流中途收到服务端错误（<c>event: error</c> 或 <c>{"error":{...}}</c> 数据）。
    /// <see cref="Retryable"/> 表示过载 / 限流 / 服务端错误这类可重试的情况。
    /// </summary>
    public class LlmStreamErrorException : Exception
    {
        public bool Retryable { get; }

        public LlmStreamErrorException(string message, bool retryable) : base(message)
        {
            Retryable = retryable;
        }
    }

    /// <summary>模型输出被内容过滤（finish_reason=content_filter）。不可重试。</summary>
    public class LlmContentFilterException : Exception
    {
        public LlmContentFilterException(string message) : base(message)
        {
        }
    }
}
