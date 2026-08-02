using System;
using System.IO;
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
        /// 单次 <c>ReadLineAsync</c> 的空闲超时上限。
        /// <para>
        /// 根因 (已核实，非猜测)：<see cref="AgentCore.Editor.Utils.HttpClientFactory"/> 上配置的
        /// <c>HttpClient.Timeout</c> 在 <see cref="System.Net.Http.HttpCompletionOption.ResponseHeadersRead"/>
        /// 模式下只覆盖"发出请求到拿到响应头"这一段 —— <c>SendAsync</c> 一旦返回，该 timeout 就已经
        /// 失效，后续对响应 body 流的手动 <c>ReadLineAsync</c> 循环完全不受它保护。真实故障案例
        /// (v1.13.0 后续): 远端模型代理主动关闭连接 (TCP 进入 CLOSE_WAIT)，但本地读取循环既不返回
        /// null、也不抛异常，永久挂起，只能靠用户手动点取消才能恢复。
        /// </para>
        /// <para>
        /// 修复不针对"远端主动关闭"这一种故障模式打补丁 —— 无论卡住的原因是什么 (对方 FIN、
        /// 代理静默丢包、中间网络设备吞连接)，只要读取空闲超过阈值就视为死连接，主动关闭底层
        /// 流以唤醒/终结挂起的读取，抛出 <see cref="TimeoutException"/> 交给上层 (FallbackRouter)
        /// 的既有重试机制接管。复用 <see cref="AgentCore.Editor.Utils.HttpClientFactory.DefaultTimeoutSeconds"/>
        /// 而非新增配置项 —— 该常量本来就代表"这条 LLM 请求允许的最大等待时间"这一个概念，
        /// 只是原实现把它错误地只套用在了 headers 阶段；这里是把同一个语义正确地延伸到 body 读取阶段，
        /// 不是引入第二个含义重叠的超时来源。
        /// </para>
        /// </summary>
        private static readonly TimeSpan StreamIdleTimeout =
            TimeSpan.FromSeconds(AgentCore.Editor.Utils.HttpClientFactory.DefaultTimeoutSeconds);

        /// <summary>
        /// 解析 SSE 流，通过回调逐 chunk 推送解析结果。
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

            // ADR-19: 用 Stopwatch 实现"基于时间的让步预算"，避免固定 N chunk 阈值在
            //   不同吞吐场景下要么过频（吐字慢）要么过疏（Hold on 复现）。
            var yieldTimer = System.Diagnostics.Stopwatch.StartNew();

            while (!ct.IsCancellationRequested)
            {
                string line;
                try
                {
                    line = await ReadLineWithIdleTimeoutAsync(reader, responseStream, ct);
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    // 取消时读取可能抛异常，正常退出
                    return;
                }

                // ReadLineAsync 返回 null 即流真正结束 (原实现依赖 reader.EndOfStream
                // 作为 loop 条件, 但 EndOfStream 属性 getter 在 NetworkStream 上会同步
                // 阻塞主线程做 peek — Profiler 实测 28 次调用共 199ms/帧 (334ms 一帧
                // 里的 60%). 改为仅通过 ReadLineAsync 返回值判定 stream 结束, 语义等价.
                if (line == null) break;

                // 空行跳过（SSE 格式中的事件分隔符）
                if (string.IsNullOrEmpty(line)) continue;

                // 只处理 data: 开头的行
                if (!line.StartsWith("data: ")) continue;

                var data = line.Substring(6); // 去掉 "data: " 前缀

                // [DONE] 信号 — 流结束
                if (data == "[DONE]")
                {
                    onChunk?.Invoke(StreamChunk.Finished());
                    return;
                }

                // 解析 JSON chunk
                try
                {
                    ParseChunkJson(data, onChunk);
                }
                catch (Exception ex)
                {
                    AgentCoreLog.Warning($"[AgentCore] SSE chunk parse error: {ex.Message}\nData: {data}");
                    // 解析错误不中断流，继续处理下一行
                }

                // ADR-19: 主线程连续占用满 YieldBudgetMs 才让步，
                //   让出去后重置计时器，继续在主线程 parse。
                if (yieldTimer.ElapsedMilliseconds >= YieldBudgetMs)
                {
                    await Task.Yield();
                    yieldTimer.Restart();
                }
            }

            // 如果流结束但没收到 [DONE]，也发送完成信号
            if (!ct.IsCancellationRequested)
            {
                onChunk?.Invoke(StreamChunk.Finished("stream_end"));
            }
        }

        /// <summary>
        /// 带空闲超时的 <c>ReadLineAsync</c>。
        /// <para>
        /// 用 <c>Task.WhenAny</c> 竞速一个 <see cref="StreamIdleTimeout"/> 定时任务：读取先完成则
        /// 正常返回；超时先触发则主动 <c>Dispose</c> 底层流唤醒/终结挂起的读取，并抛出
        /// <see cref="TimeoutException"/>。<c>Dispose</c> 而非 <c>Close</c> —— 对 <c>NetworkStream</c>
        /// 效果一致，但对某些 handler 包装的 stream 更可靠地释放底层 socket，避免 CLOSE_WAIT 累积。
        /// </para>
        /// </summary>
        private static async Task<string> ReadLineWithIdleTimeoutAsync(
            StreamReader reader, Stream responseStream, CancellationToken ct)
        {
            var readTask = reader.ReadLineAsync();
            var timeoutTask = Task.Delay(StreamIdleTimeout, ct);

            var completed = await Task.WhenAny(readTask, timeoutTask);
            if (completed == readTask)
                return await readTask;

            // 超时先完成 —— 读取仍在挂起，主动关闭底层流以终结它，防止 socket 泄漏。
            try { responseStream.Dispose(); } catch { /* 已关闭或正在被读取任务处理，忽略 */ }

            // readTask 被放弃后，Dispose() 会让它内部以异常收尾 —— 挂一个静默 continuation
            // 观察掉该异常，避免变成 unobserved task exception (GC 终结阶段的噪音/警告)。
            _ = readTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

            throw new TimeoutException(
                $"[AgentCore] SSE stream read idle for {StreamIdleTimeout.TotalSeconds}s with no new data. " +
                "Connection likely dropped by remote (e.g. proxy closed it) without a clean stream-end signal.");
        }

        /// <summary>
        /// 解析单个 SSE data 行的 JSON 内容，通过回调推送解析结果。
        /// 支持单个 chunk 中包含多个 tool_call delta 的场景。
        /// </summary>
        private void ParseChunkJson(string json, Action<StreamChunk> onChunk)
        {
            JObject rawChunk = null;
            try
            {
                rawChunk = JObject.Parse(json);
            }
            catch
            {
                // Typed deserialization below will surface the parsing error through the caller.
            }

            var chunk = JsonHelper.Deserialize<ChatCompletionChunk>(json);
            if (chunk?.Choices == null || chunk.Choices.Count == 0) return;

            var choice = chunk.Choices[0];
            var delta = choice.Delta;

            if (delta == null) return;

            var reasoning = ReasoningFieldExtractor.ExtractFromChunk(rawChunk);
            if (!string.IsNullOrEmpty(reasoning))
            {
                onChunk?.Invoke(StreamChunk.Reasoning(reasoning));
            }

            // 文本内容 token
            if (!string.IsNullOrEmpty(delta.Content))
            {
                onChunk?.Invoke(StreamChunk.Token(delta.Content));
                return;
            }

            // 工具调用增量 — 遍历所有 tool_call delta，不再只取 [0]
            // P0-1 fix: 支持并行工具调用场景，单个 SSE chunk 可能包含多个不同 index 的 tool_call delta
            if (delta.ToolCalls != null && delta.ToolCalls.Count > 0)
            {
                foreach (var toolCallDelta in delta.ToolCalls)
                {
                    onChunk?.Invoke(StreamChunk.ToolDelta(toolCallDelta));
                }
                return;
            }

            // finish_reason
            if (!string.IsNullOrEmpty(choice.FinishReason))
            {
                onChunk?.Invoke(StreamChunk.Finished(choice.FinishReason));
                return;
            }

            // 角色标记等其他 delta，忽略
        }
    }
}
