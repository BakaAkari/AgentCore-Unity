using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Utils;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AgentCore.Editor.LLM
{
    /// <summary>
    /// SSE (Server-Sent Events) 流式响应解析器。
    /// 解析 OpenAI 兼容 API 的流式响应格式。
    /// 
    /// SSE 格式示例：
    /// data: {"choices":[{"delta":{"content":"Hello"},"index":0}]}
    /// data: {"choices":[{"delta":{"content":" world"},"index":0}]}
    /// data: [DONE]
    /// </summary>
    public class StreamingResponseParser
    {
        /// <summary>
        /// 解析 SSE 流，通过回调逐 chunk 推送解析结果。
        /// </summary>
        /// <param name="responseStream">HTTP 响应流</param>
        /// <param name="onChunk">每个解析出的 chunk 的回调</param>
        /// <param name="ct">取消令牌</param>
        /// <summary>
        /// 主线程连续占用 <see cref="YieldBudgetMs"/> 毫秒后强制让出一次。
        /// 依据：Unity 内置 "Hold on / UnitySynchronization.ExecuteTasks" 保护阈值约 500ms。
        /// 设置为 200ms 提供 2.5x 安全余量；同时避免像"每 N chunk 让步"那样在高吞吐时过频 yield，
        /// 导致 EditorApplication tick 恢复延迟叠加，出现"吐字变慢"的可见回退。
        /// </summary>
        private const long YieldBudgetMs = 200;

        /// <summary>
        /// 流读取空闲上限（null = <see cref="StreamIdleWatchdog.DefaultIdleTimeout"/>）。
        /// 只限制"连续多久没有新数据"，不限制整条流的总时长。
        /// </summary>
        public TimeSpan? IdleTimeout { get; set; }

        /// <summary>
        /// 解析 SSE 流，通过回调逐 chunk 推送解析结果。
        /// <para>
        /// 按 WHATWG SSE 规则成帧：<c>data:</c> 后的空格可有可无；以 <c>:</c> 开头的注释行（keep-alive）忽略；
        /// 同一事件的多行 data 以 <c>\n</c> 拼接，遇空行派发。行尾 CR / LF / CRLF 与开头 BOM 由
        /// <see cref="StreamReader"/> 处理。
        /// </para>
        /// <para>
        /// 结束判定：收到 <c>[DONE]</c> 或任一 <c>finish_reason</c> 才算完整；连接在两者之前断开抛
        /// <see cref="EndOfStreamException"/>（可重试），不再把半截回复当作正常完成。
        /// 流中途的 <c>event: error</c> / <c>{"error":...}</c> 抛 <see cref="LlmStreamErrorException"/>。
        /// 取消时抛 <see cref="OperationCanceledException"/>。
        /// </para>
        /// </summary>
        /// <param name="responseStream">HTTP 响应流</param>
        /// <param name="onChunk">每个解析出的 chunk 的回调</param>
        /// <param name="ct">取消令牌</param>
        public async Task ParseStreamAsync(
            Stream responseStream,
            Action<StreamChunk> onChunk,
            CancellationToken ct = default)
        {
            using var reader = new StreamReader(responseStream);
            // 空闲看门狗：每收到一行重置计时，与流同生命周期（v1.13 的根因与修复思路见 StreamIdleWatchdog 注释：
            // ResponseHeadersRead 模式下 HttpClient.Timeout 不保护 body 读取，远端静默断开时读取会永久挂起）
            using var watchdog = new StreamIdleWatchdog(responseStream, ct, IdleTimeout);

            // ADR-19: 用 Stopwatch 实现"基于时间的让步预算"，避免固定 N chunk 阈值在
            //   不同吞吐场景下要么过频（吐字慢）要么过疏（Hold on 复现）。
            var yieldTimer = System.Diagnostics.Stopwatch.StartNew();

            var data = new StringBuilder();
            string eventType = null;
            bool sawFinishReason = false;

            while (true)
            {
                string line;
                try
                {
                    line = await watchdog.ReadLineAsync(reader);
                }
                catch (Exception ex) when (ct.IsCancellationRequested && !(ex is OperationCanceledException))
                {
                    throw new OperationCanceledException(ct);
                }

                // ReadLineAsync 返回 null 即流真正结束 (原实现依赖 reader.EndOfStream
                // 作为 loop 条件, 但 EndOfStream 属性 getter 在 NetworkStream 上会同步
                // 阻塞主线程做 peek — Profiler 实测 28 次调用共 199ms/帧 (334ms 一帧
                // 里的 60%). 改为仅通过 ReadLineAsync 返回值判定 stream 结束, 语义等价.
                bool eof = line == null;

                if (!eof && line.Length > 0)
                {
                    AppendField(line, data, ref eventType);
                    continue;
                }

                // 空行（或 EOF 时残留的未派发事件）：派发
                if (data.Length > 0)
                {
                    var payload = data.ToString(0, data.Length - 1); // 去掉末尾的 \n
                    var type = eventType;
                    data.Clear();
                    eventType = null;

                    if (payload == "[DONE]")
                    {
                        // finish_reason 已经派发过 Done 时不重复，避免覆盖 "length" 等真实原因
                        if (!sawFinishReason)
                            onChunk?.Invoke(StreamChunk.Finished());
                        return;
                    }

                    if (DispatchEvent(type, payload, onChunk))
                        sawFinishReason = true;

                    // ADR-19: 主线程连续占用满 YieldBudgetMs 才让步，
                    //   让出去后重置计时器，继续在主线程 parse。
                    if (yieldTimer.ElapsedMilliseconds >= YieldBudgetMs)
                    {
                        await Task.Yield();
                        yieldTimer.Restart();
                    }
                }
                else
                {
                    eventType = null;
                }

                if (eof) break;
            }

            ct.ThrowIfCancellationRequested();

            // 部分网关不发 [DONE]，但只要收到过 finish_reason 就是完整回复。两者都没有 = 连接中途断开。
            if (!sawFinishReason)
            {
                throw new EndOfStreamException(
                    "[AgentCore] SSE stream ended without [DONE] or finish_reason; the response is incomplete.");
            }
        }

        /// <summary>按 SSE 字段规则累积一行：<c>field: value</c>（冒号后最多去掉一个空格）。</summary>
        private static void AppendField(string line, StringBuilder data, ref string eventType)
        {
            if (line[0] == ':')
                return; // 注释 / keep-alive

            int colon = line.IndexOf(':');
            string field = colon < 0 ? line : line.Substring(0, colon);
            string value = colon < 0 ? string.Empty : line.Substring(colon + 1);
            if (value.Length > 0 && value[0] == ' ')
                value = value.Substring(1);

            switch (field)
            {
                case "data":
                    data.Append(value).Append('\n');
                    break;
                case "event":
                    eventType = value;
                    break;
                // id / retry 等字段对单次请求无意义，忽略
            }
        }

        /// <summary>派发一个完整事件，返回本事件是否携带了 finish_reason。</summary>
        private bool DispatchEvent(string eventType, string payload, Action<StreamChunk> onChunk)
        {
            JObject raw = null;
            try
            {
                raw = JObject.Parse(payload);
            }
            catch (Exception ex)
            {
                if (eventType == "error")
                    throw new LlmStreamErrorException($"LLM stream error event: {payload}", retryable: true);
                // 单个坏 chunk 不中断流：缺失的内容会体现在结束判定或工具参数校验上
                AgentCoreLog.Warning($"[AgentCore] SSE chunk parse error: {ex.Message}\nData: {payload}");
                return false;
            }

            if (eventType == "error" || raw["error"] is JObject)
                throw CreateStreamError(raw["error"] as JObject ?? raw, payload);

            return ParseChunkJson(payload, raw, onChunk);
        }

        /// <summary>
        /// 流中途错误：过载 / 限流 / 服务端错误可重试，其余（请求错误、鉴权）不可重试。
        /// </summary>
        private static LlmStreamErrorException CreateStreamError(JObject error, string payload)
        {
            var type = error?.Value<string>("type") ?? string.Empty;
            var code = error?["code"]?.ToString() ?? string.Empty;
            var message = error?.Value<string>("message") ?? payload;

            bool retryable =
                type.Contains("overloaded") || type.Contains("server_error") || type.Contains("api_error") ||
                type.Contains("rate_limit") || type.Contains("timeout") ||
                code == "429" || code.StartsWith("5") || code.Contains("rate_limit") || code.Contains("overloaded");

            return new LlmStreamErrorException(
                $"LLM stream error ({(string.IsNullOrEmpty(type) ? code : type)}): {message}", retryable);
        }

        /// <summary>
        /// 解析单个 SSE 事件的 JSON 内容，通过回调推送解析结果，返回是否携带 finish_reason。
        /// 同一 chunk 中的 reasoning / content / tool_calls / finish_reason 各自独立处理，互不吞掉。
        /// </summary>
        private bool ParseChunkJson(string json, JObject rawChunk, Action<StreamChunk> onChunk)
        {
            var chunk = JsonHelper.Deserialize<ChatCompletionChunk>(json);

            // v1.17.0: usage 通常在最后一个 choices 为空的 chunk 上（stream_options.include_usage），
            // 旧实现在 choices 为空时直接 return，导致流式调用永远拿不到 usage/缓存命中信息。
            var usageObj = chunk?.Usage ?? rawChunk?["usage"] as JObject;
            if (usageObj != null && usageObj.HasValues)
            {
                var usage = LlmUsage.FromOpenAI(usageObj, chunk?.Model);
                if (usage != null && !usage.IsEmpty) onChunk?.Invoke(StreamChunk.UsageReport(usage));
            }

            if (chunk?.Choices == null || chunk.Choices.Count == 0) return false;

            var choice = chunk.Choices[0];
            var delta = choice.Delta;

            if (delta != null)
            {
                var reasoning = ReasoningFieldExtractor.ExtractFromChunk(rawChunk);
                if (!string.IsNullOrEmpty(reasoning))
                    onChunk?.Invoke(StreamChunk.Reasoning(reasoning));

                // 文本内容 token
                if (!string.IsNullOrEmpty(delta.Content))
                    onChunk?.Invoke(StreamChunk.Token(delta.Content));

                // 工具调用增量 — 遍历所有 tool_call delta，不再只取 [0]
                // P0-1 fix: 支持并行工具调用场景，单个 SSE chunk 可能包含多个不同 index 的 tool_call delta
                if (delta.ToolCalls != null)
                {
                    foreach (var toolCallDelta in delta.ToolCalls)
                        onChunk?.Invoke(StreamChunk.ToolDelta(toolCallDelta));
                }
            }

            // finish_reason 可能与最后一段 content / tool_calls 同 chunk，也可能 delta 为空
            if (string.IsNullOrEmpty(choice.FinishReason)) return false;
            onChunk?.Invoke(StreamChunk.Finished(choice.FinishReason));
            return true;
        }
    }
}
