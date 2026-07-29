using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Config;
using AgentCore.Editor.Utils;
using Newtonsoft.Json.Linq;
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
            CancellationToken ct = default,
            int? contentMaxTokens = null)
        {
            var settings = AgentCoreSettings.instance;
            var apiKey = SecureKeyStorage.GetLLMApiKey();

            // 修复消息历史中所有 tool_calls 的 arguments，防止无效 JSON 导致服务端解析失败
            SanitizeMessageToolCalls(messages);

            var request = new ChatCompletionRequest
            {
                Model = settings.llmModel,
                Messages = messages,
                Tools = tools?.Count > 0 ? tools : null,
                Stream = false,
                Temperature = settings.temperature,
                MaxTokens = contentMaxTokens.HasValue
                    ? settings.GetEffectiveMaxTokens(contentMaxTokens.Value)
                    : settings.GetEffectiveMaxTokens()
            };

            var json = RequestEnrichment.BuildEnrichedJson(request, settings);
            var url = settings.GetChatCompletionsUrl();
AgentCore.Editor.Utils.AgentCoreLog.Debug($"[AgentCore] LLM request: {url} model={settings.llmModel} messages={messages.Count}");


            var client = HttpClientFactory.GetClient();
            using var httpRequest = HttpClientFactory.CreateRequest(HttpMethod.Post, url, apiKey);
            httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(httpRequest, ct);
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
                AgentCore.Editor.Utils.AgentCoreLog.Debug($"[AgentCore] LLM usage: prompt={result.Usage.PromptTokens} completion={result.Usage.CompletionTokens} total={result.Usage.TotalTokens}");
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
            CancellationToken ct = default,
            int? contentMaxTokens = null)
        {
            var settings = AgentCoreSettings.instance;
            var apiKey = SecureKeyStorage.GetLLMApiKey();

            // 修复消息历史中所有 tool_calls 的 arguments，防止无效 JSON 导致服务端解析失败
            SanitizeMessageToolCalls(messages);

            var request = new ChatCompletionRequest
            {
                Model = settings.llmModel,
                Messages = messages,
                Tools = tools?.Count > 0 ? tools : null,
                Stream = true,
                Temperature = settings.temperature,
                MaxTokens = contentMaxTokens.HasValue
                    ? settings.GetEffectiveMaxTokens(contentMaxTokens.Value)
                    : settings.GetEffectiveMaxTokens()
            };

            var json = RequestEnrichment.BuildEnrichedJson(request, settings);
            var url = settings.GetChatCompletionsUrl();
AgentCore.Editor.Utils.AgentCoreLog.Debug($"[AgentCore] LLM stream request: {url} model={settings.llmModel} messages={messages.Count}");


            var client = HttpClientFactory.GetClient();
            using var httpRequest = HttpClientFactory.CreateRequest(HttpMethod.Post, url, apiKey);
            httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

            // 使用 ResponseHeadersRead 以便尽早开始读取流
            using var response = await client.SendAsync(
                httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException(
                    $"LLM API error: HTTP {(int)response.StatusCode} {response.ReasonPhrase}\n{errorBody}");
            }

            // 读取 SSE 流
            using var stream = await response.Content.ReadAsStreamAsync();

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

                    case StreamChunkType.ReasoningToken:
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
                        AgentCoreLog.Warning($"[AgentCore] Stream error: {chunk.Error}");
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
        /// 遍历消息列表，修复所有 assistant 消息中 tool_calls 的 arguments 字段。
        /// 确保发送给 API 的历史消息中不包含无效 JSON，防止 vLLM 等服务端解析失败。
        /// </summary>
        /// <param name="messages">待发送的消息列表（会就地修改）</param>
        private static void SanitizeMessageToolCalls(List<ChatMessage> messages)
        {
            if (messages == null) return;

            foreach (var message in messages)
            {
                if (message.ToolCalls == null || message.ToolCalls.Count == 0)
                    continue;

                foreach (var toolCall in message.ToolCalls)
                {
                    if (toolCall.Function != null && !string.IsNullOrEmpty(toolCall.Function.Arguments))
                    {
                        toolCall.Function.Arguments = SanitizeToolArguments(toolCall.Function.Arguments);
                    }
                }
            }
        }

        /// <summary>
        /// 修复 LLM 生成的无效 JSON arguments。
        /// 某些模型（如 Qwen3）会在 tool_calls.arguments 中生成包含未转义反斜杠的内容，
        /// 导致下游服务器（如 vLLM）在 json.loads() 时失败。
        ///
        /// 常见场景：
        /// 1. Windows 路径中的反斜杠（\U, \P 等）
        /// 2. 嵌入的源代码中包含正则表达式（\d, \s, \w 等）
        /// 3. 嵌入的源代码中包含字符串字面量的转义
        /// </summary>
        /// <param name="arguments">原始 arguments 字符串</param>
        /// <returns>修复后的合法 JSON 字符串</returns>
        private static string SanitizeToolArguments(string arguments)
        {
            if (string.IsNullOrEmpty(arguments))
                return "{}";

            // 快速路径：如果已经是合法 JSON，直接返回
            try
            {
                JToken.Parse(arguments);
                return arguments;
            }
            catch (Exception ex)
            {
                // 需要修复 — 记录原始错误信息用于调试
                AgentCore.Editor.Utils.AgentCoreLog.Debug($"[AgentCore] Tool arguments JSON invalid, attempting repair: {ex.Message}");
            }

            // 策略 1：逐字符扫描修复无效转义序列
            var repaired = RepairInvalidEscapes(arguments);
            try
            {
                JToken.Parse(repaired);
                AgentCore.Editor.Utils.AgentCoreLog.Debug("[AgentCore] Tool arguments repaired successfully (escape fix).");
                return repaired;
            }
            catch
            {
                // 策略 1 失败，继续尝试
            }

            // 策略 2：尝试用 Newtonsoft.Json 的宽松模式读取
            // 如果上面的修复不够，尝试提取可解析的部分并重建
            try
            {
                // 尝试找到 JSON 对象的边界并逐字段修复
                var rebuilt = RebuildJsonArguments(arguments);
                if (rebuilt != null)
                {
                    JToken.Parse(rebuilt);
                    AgentCore.Editor.Utils.AgentCoreLog.Debug("[AgentCore] Tool arguments repaired successfully (rebuild).");
                    return rebuilt;
                }
            }
            catch
            {
                // 策略 2 也失败
            }

            // 策略 3：最后手段 — 将整个 arguments 作为纯文本包装
            AgentCoreLog.Warning($"[AgentCore] Unable to fully repair tool arguments, wrapping as raw text. Original (first 200 chars): {arguments.Substring(0, Math.Min(200, arguments.Length))}");
            // 用 Newtonsoft.Json 安全序列化为字符串值，确保所有特殊字符被正确转义
            var safeContent = Newtonsoft.Json.JsonConvert.SerializeObject(arguments);
            // safeContent 现在是一个带引号的合法 JSON 字符串，如 "\"...escaped content...\""
            // 我们需要把它包装成一个对象
            return $"{{\"_raw_arguments\": {safeContent}}}";
        }

        /// <summary>
        /// 逐字符扫描 JSON 字符串，修复无效的转义序列。
        /// 在 JSON 字符串值内部，将不合法的 \X 转换为 \\X（双转义）。
        /// </summary>
        private static string RepairInvalidEscapes(string json)
        {
            var sb = new StringBuilder(json.Length + 64);
            bool inString = false;
            
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];

                if (!inString)
                {
                    sb.Append(c);
                    if (c == '"')
                        inString = true;
                    continue;
                }

                // 在字符串内部
                if (c == '"')
                {
                    // 未转义的引号 — 字符串结束
                    sb.Append(c);
                    inString = false;
                    continue;
                }

                if (c == '\\')
                {
                    // 检查下一个字符是否是合法的 JSON 转义
                    if (i + 1 < json.Length)
                    {
                        char next = json[i + 1];
                        if (next == '"' || next == '\\' || next == '/' ||
                            next == 'b' || next == 'f' || next == 'n' ||
                            next == 'r' || next == 't')
                        {
                            // 合法转义序列，保持原样
                            sb.Append(c);
                            sb.Append(next);
                            i++; // 跳过下一个字符
                        }
                        else if (next == 'u')
                        {
                            // \uXXXX — 检查后面是否有 4 个十六进制字符
                            if (i + 5 < json.Length &&
                                IsHexChar(json[i + 2]) && IsHexChar(json[i + 3]) &&
                                IsHexChar(json[i + 4]) && IsHexChar(json[i + 5]))
                            {
                                // 合法的 \uXXXX
                                sb.Append(json, i, 6);
                                i += 5;
                            }
                            else
                            {
                                // 无效的 \u（后面不是 4 个 hex），双转义
                                sb.Append('\\');
                                sb.Append('\\');
                                sb.Append(next);
                                i++;
                            }
                        }
                        else
                        {
                            // 非法转义序列（如 \d, \s, \w, \U, \P 等）
                            // 将 \ 双转义为 \\，保留后续字符
                            sb.Append('\\');
                            sb.Append('\\');
                            sb.Append(next);
                            i++;
                        }
                    }
                    else
                    {
                        // 字符串末尾的孤立反斜杠
                        sb.Append('\\');
                        sb.Append('\\');
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// 尝试重建 JSON arguments：提取键值对并用 Newtonsoft.Json 安全重新序列化。
        /// </summary>
        private static string RebuildJsonArguments(string arguments)
        {
            // 尝试用正则提取顶层键值对
            // 匹配模式: "key": "value" 或 "key": value
            var result = new JObject();
            
            // 找到第一个 { 和最后一个 }
            int start = arguments.IndexOf('{');
            int end = arguments.LastIndexOf('}');
            if (start < 0 || end <= start) return null;

            string inner = arguments.Substring(start + 1, end - start - 1);
            
            // 尝试逐个提取 "key": 后面的值
            var keyPattern = new Regex(@"""(\w+)""\s*:\s*");
            var matches = keyPattern.Matches(inner);
            
            for (int i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                string key = match.Groups[1].Value;
                int valueStart = match.Index + match.Length;
                
                // 确定值的结束位置（下一个键的开始，或字符串末尾）
                int valueEnd = (i + 1 < matches.Count) ? matches[i + 1].Index : inner.Length;
                string rawValue = inner.Substring(valueStart, valueEnd - valueStart).TrimEnd(',', ' ', '\n', '\r');
                
                // 尝试解析值
                try
                {
                    var token = JToken.Parse(rawValue);
                    result[key] = token;
                }
                catch
                {
                    // 值无法解析，用安全序列化包装为字符串
                    if (rawValue.StartsWith("\"") && rawValue.EndsWith("\""))
                    {
                        // 去掉外层引号，作为原始字符串内容
                        string rawContent = rawValue.Substring(1, rawValue.Length - 2);
                        result[key] = rawContent;
                    }
                    else
                    {
                        result[key] = rawValue;
                    }
                }
            }

            if (result.Count == 0) return null;
            return result.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// 判断字符是否是十六进制字符。
        /// </summary>
        private static bool IsHexChar(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
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
                    Arguments = SanitizeToolArguments(ArgumentsBuilder.ToString())
                }
            };
        }
    }
}
