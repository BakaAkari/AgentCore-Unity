using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Config;
using AgentCore.Editor.Utils;
using UnityEngine;

namespace AgentCore.Editor.LLM
{
    /// <summary>
    /// OpenAI 兼容 API 客户端实现。
    /// 支持任何 OpenAI 兼容的 LLM 后端（LiteLLM, vLLM, Ollama 等）。
    /// </summary>
    public class OpenAICompatibleClient : ILLMClient
    {
        private readonly StreamingResponseParser _streamParser = new();

        /// <summary>
        /// 非流式 Chat Completion 调用。
        /// </summary>
        public async Task<ChatCompletionResponse> ChatCompletionAsync(
            List<ChatMessage> messages,
            List<ToolDefinition> tools = null,
            CancellationToken ct = default)
        {
            var settings = AgentCoreSettings.instance;
            var apiKey = SecureKeyStorage.GetLLMApiKey();

            var request = new ChatCompletionRequest
            {
                Model = settings.llmModel,
                Messages = messages,
                Tools = tools?.Count > 0 ? tools : null,
                Stream = false,
                Temperature = settings.temperature,
                MaxTokens = settings.maxTokens
            };

            var json = JsonHelper.Serialize(request);
            var url = settings.GetChatCompletionsUrl();

            Debug.Log($"[AgentCore] LLM request: {url} model={settings.llmModel} messages={messages.Count}");

            var client = HttpClientFactory.GetClient();
            using var httpRequest = HttpClientFactory.CreateRequest(HttpMethod.Post, url, apiKey);
            httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.SendAsync(httpRequest, ct);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"LLM API error: HTTP {(int)response.StatusCode} {response.ReasonPhrase}\n{responseBody}");
            }

            var result = JsonHelper.Deserialize<ChatCompletionResponse>(responseBody);
            if (result == null)
            {
                throw new InvalidOperationException($"Failed to parse LLM response:\n{responseBody}");
            }

            if (result.Usage != null)
            {
                Debug.Log($"[AgentCore] LLM usage: prompt={result.Usage.PromptTokens} completion={result.Usage.CompletionTokens} total={result.Usage.TotalTokens}");
            }

            return result;
        }

        /// <summary>
        /// 流式 Chat Completion 调用。
        /// 通过回调逐 chunk 推送，最终返回拼接好的完整 assistant 消息。
        /// </summary>
        public async Task<ChatMessage> ChatCompletionStreamAsync(
            List<ChatMessage> messages,
            Action<StreamChunk> onChunk,
            List<ToolDefinition> tools = null,
            CancellationToken ct = default)
        {
            var settings = AgentCoreSettings.instance;
            var apiKey = SecureKeyStorage.GetLLMApiKey();

            var request = new ChatCompletionRequest
            {
                Model = settings.llmModel,
                Messages = messages,
                Tools = tools?.Count > 0 ? tools : null,
                Stream = true,
                Temperature = settings.temperature,
                MaxTokens = settings.maxTokens
            };

            var json = JsonHelper.Serialize(request);
            var url = settings.GetChatCompletionsUrl();

            Debug.Log($"[AgentCore] LLM stream request: {url} model={settings.llmModel} messages={messages.Count}");

            var client = HttpClientFactory.GetClient();
            using var httpRequest = HttpClientFactory.CreateRequest(HttpMethod.Post, url, apiKey);
            httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

            // 使用 ResponseHeadersRead 以便尽早开始读取流
            var response = await client.SendAsync(
                httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException(
                    $"LLM API error: HTTP {(int)response.StatusCode} {response.ReasonPhrase}\n{errorBody}");
            }

            // 读取 SSE 流
            var stream = await response.Content.ReadAsStreamAsync();

            // 用于拼接完整的 assistant 消息
            var contentBuilder = new StringBuilder();
            var toolCalls = new List<ToolCall>();
            var toolCallBuilders = new Dictionary<int, ToolCallBuilder>();
            string finishReason = null;

            await _streamParser.ParseStreamAsync(stream, chunk =>
            {
                switch (chunk.Type)
                {
                    case StreamChunkType.ContentToken:
                        contentBuilder.Append(chunk.Content);
                        onChunk?.Invoke(chunk);
                        break;

                    case StreamChunkType.ToolCallDelta:
                        // 拼接工具调用的增量 JSON（Phase 2 完整使用）
                        AccumulateToolCallDelta(chunk.ToolCallDelta, toolCallBuilders);
                        onChunk?.Invoke(chunk);
                        break;

                    case StreamChunkType.Done:
                        finishReason = chunk.FinishReason;
                        onChunk?.Invoke(chunk);
                        break;

                    case StreamChunkType.Error:
                        Debug.LogWarning($"[AgentCore] Stream error: {chunk.Error}");
                        onChunk?.Invoke(chunk);
                        break;
                }
            }, ct);

            // 构建完整的 tool_calls 列表
            foreach (var builder in toolCallBuilders.Values)
            {
                toolCalls.Add(builder.Build());
            }

            // 返回拼接好的完整 assistant 消息
            var content = contentBuilder.ToString();
            return ChatMessage.Assistant(
                string.IsNullOrEmpty(content) ? null : content,
                toolCalls.Count > 0 ? toolCalls : null
            );
        }

        /// <summary>
        /// 累积工具调用的增量数据。
        /// 流式模式下，tool_calls 的 arguments 可能跨多个 chunk 分片传输。
        /// </summary>
        private void AccumulateToolCallDelta(
            ToolCall delta,
            Dictionary<int, ToolCallBuilder> builders)
        {
            if (delta == null) return;

            int index = delta.Index ?? 0;

            if (!builders.TryGetValue(index, out var builder))
            {
                builder = new ToolCallBuilder();
                builders[index] = builder;
            }

            // 累积 id
            if (!string.IsNullOrEmpty(delta.Id))
                builder.Id = delta.Id;

            // 累积 function name
            if (delta.Function != null)
            {
                if (!string.IsNullOrEmpty(delta.Function.Name))
                    builder.FunctionName = delta.Function.Name;

                // 累积 arguments（可能分片）
                if (delta.Function.Arguments != null)
                    builder.ArgumentsBuilder.Append(delta.Function.Arguments);
            }
        }

        /// <summary>
        /// 工具调用构建器，用于累积流式模式下的增量数据。
        /// </summary>
        private class ToolCallBuilder
        {
            public string Id { get; set; }
            public string FunctionName { get; set; }
            public StringBuilder ArgumentsBuilder { get; } = new();

            public ToolCall Build() => new()
            {
                Id = Id,
                Type = "function",
                Function = new FunctionCall
                {
                    Name = FunctionName,
                    Arguments = ArgumentsBuilder.ToString()
                }
            };
        }
    }
}
