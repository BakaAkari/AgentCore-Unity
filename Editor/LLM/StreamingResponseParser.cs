using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Utils;
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
                    var chunk = ParseChunkJson(data);
                    if (chunk != null)
                    {
                        onChunk?.Invoke(chunk);
                    }
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
        /// 解析单个 SSE data 行的 JSON 内容。
        /// </summary>
        private StreamChunk ParseChunkJson(string json)
        {
            var chunk = JsonHelper.Deserialize<ChatCompletionChunk>(json);
            if (chunk?.Choices == null || chunk.Choices.Count == 0) return null;

            var choice = chunk.Choices[0];
            var delta = choice.Delta;

            if (delta == null) return null;

            // 文本内容 token
            if (!string.IsNullOrEmpty(delta.Content))
            {
                return StreamChunk.Token(delta.Content);
            }

            // 工具调用增量（Phase 2 使用）
            if (delta.ToolCalls != null && delta.ToolCalls.Count > 0)
            {
                return StreamChunk.ToolDelta(delta.ToolCalls[0]);
            }

            // finish_reason
            if (!string.IsNullOrEmpty(choice.FinishReason))
            {
                return StreamChunk.Finished(choice.FinishReason);
            }

            // 角色标记等其他 delta，忽略
            return null;
        }
    }
}
