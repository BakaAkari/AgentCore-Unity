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
        public async Task ParseStreamAsync(
            Stream responseStream,
            Action<StreamChunk> onChunk,
            CancellationToken ct = default)
        {
            using var reader = new StreamReader(responseStream);

            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                string line;
                try
                {
                    line = await reader.ReadLineAsync();
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    // 取消时读取可能抛异常，正常退出
                    return;
                }

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
                    Debug.LogWarning($"[AgentCore] SSE chunk parse error: {ex.Message}\nData: {data}");
                    // 解析错误不中断流，继续处理下一行
                }
            }

            // 如果流结束但没收到 [DONE]，也发送完成信号
            if (!ct.IsCancellationRequested)
            {
                onChunk?.Invoke(StreamChunk.Finished("stream_end"));
            }
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
