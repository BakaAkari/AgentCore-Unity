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
            int? contentMaxTokens = null,
            string reasoningLevelOverride = null)
        {
            var settings = AgentCoreSettings.instance;
            var apiKey = ActiveModelConfig.ApiKey;
            var endpoint = ActiveModelConfig.Endpoint;
            var model = ActiveModelConfig.ModelName;

            // 修复消息历史中所有 tool_calls 的 arguments，防止无效 JSON 导致服务端解析失败
            SanitizeMessageToolCalls(messages);

            var request = new ChatCompletionRequest
            {
                Model = model,
                Messages = messages,
                Tools = tools?.Count > 0 ? tools : null,
                Stream = false,
                Temperature = ActiveModelConfig.Temperature,
                MaxTokens = contentMaxTokens.HasValue
                    ? settings.GetEffectiveMaxTokens(contentMaxTokens.Value)
                    : settings.GetEffectiveMaxTokens()
            };

            var enrichedJson = RequestEnrichment.BuildEnrichedJson(request, reasoningLevelOverride);
            var url = endpoint.TrimEnd('/') + "/chat/completions";
            AgentCore.Editor.Utils.AgentCoreLog.Debug($"[AgentCore] LLM request: {url} model={model} messages={messages.Count}");

            // 首次发送：应用当前已知的 pruning 规则
            var body = RequestPruningRegistry.ApplyPruning(endpoint, model, enrichedJson);
            var (response, responseBody) = await SendPostAsync(url, apiKey, body, ct);

            // 400 → 尝试从错误消息学习禁字段，学到则重试一次（每次调用最多重试 1 次，不递归）
            if (!response.IsSuccessStatusCode && (int)response.StatusCode == 400)
            {
                var learned = RequestPruningRegistry.LearnFromErrorResponse(endpoint, model, responseBody);
                response.Dispose();
                if (learned.Count > 0)
                {
                    AgentCoreLog.Info($"[AgentCore] LLM request auto-retry after learning {learned.Count} banned field(s): [{string.Join(", ", learned)}]");
                    var retryBody = RequestPruningRegistry.ApplyPruning(endpoint, model, enrichedJson);
                    (response, responseBody) = await SendPostAsync(url, apiKey, retryBody, ct);
                }
            }

            using (response)
            {
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
        }

        /// <summary>
        /// 发送 POST 请求并返回响应对象 + 已读响应体字符串。
        /// 调用方负责 <c>Dispose</c> 返回的 <see cref="HttpResponseMessage"/>。
        /// </summary>
        private static async Task<(HttpResponseMessage, string)> SendPostAsync(
            string url, string apiKey, string body, CancellationToken ct)
        {
            var client = HttpClientFactory.GetClient();
            using var httpRequest = HttpClientFactory.CreateRequest(HttpMethod.Post, url, apiKey);
            httpRequest.Content = new StringContent(body, Encoding.UTF8, "application/json");
            var response = await client.SendAsync(httpRequest, ct);
            var responseBody = await response.Content.ReadAsStringAsync();
            return (response, responseBody);
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
            int? contentMaxTokens = null,
            string reasoningLevelOverride = null)
        {
            var settings = AgentCoreSettings.instance;
            var apiKey = ActiveModelConfig.ApiKey;
            var endpoint = ActiveModelConfig.Endpoint;
            var model = ActiveModelConfig.ModelName;

            // 修复消息历史中所有 tool_calls 的 arguments，防止无效 JSON 导致服务端解析失败
            SanitizeMessageToolCalls(messages);

            var request = new ChatCompletionRequest
            {
                Model = model,
                Messages = messages,
                Tools = tools?.Count > 0 ? tools : null,
                Stream = true,
                Temperature = ActiveModelConfig.Temperature,
                MaxTokens = contentMaxTokens.HasValue
                    ? settings.GetEffectiveMaxTokens(contentMaxTokens.Value)
                    : settings.GetEffectiveMaxTokens()
            };

            var enrichedJson = RequestEnrichment.BuildEnrichedJson(request, reasoningLevelOverride);
            var url = endpoint.TrimEnd('/') + "/chat/completions";
            AgentCore.Editor.Utils.AgentCoreLog.Debug($"[AgentCore] LLM stream request: {url} model={model} messages={messages.Count}");

            // 首次发送
            var body = RequestPruningRegistry.ApplyPruning(endpoint, model, enrichedJson);
            var response = await SendPostForStreamAsync(url, apiKey, body, ct);

            // Header 阶段 400 → 学习 → 重试一次（此时流未开始，重试安全）
            if (!response.IsSuccessStatusCode && (int)response.StatusCode == 400)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                var learned = RequestPruningRegistry.LearnFromErrorResponse(endpoint, model, errorBody);
                response.Dispose();

                if (learned.Count > 0)
                {
                    AgentCoreLog.Info($"[AgentCore] LLM stream request auto-retry after learning {learned.Count} banned field(s): [{string.Join(", ", learned)}]");
                    var retryBody = RequestPruningRegistry.ApplyPruning(endpoint, model, enrichedJson);
                    response = await SendPostForStreamAsync(url, apiKey, retryBody, ct);
                }
                else
                {
                    // 未匹配到任何学习模式的 400 直接抛出（真错误 - 消息格式错、apiKey 错等）
                    throw new HttpRequestException(
                        $"LLM API error: HTTP 400 {response.ReasonPhrase}\n{errorBody}");
                }
            }

            using (response)
            {
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
                    // v1.14.14 fix: Build() 在 function.name 缺失时返回 null（数据不完整），
                    // 这里跳过而非加入列表，避免把残缺的 assistant tool_call 写进历史 / 发回 API
                    // 触发 vLLM `function.name Field required` 400。
                    var built = builder.Build();
                    if (built != null)
                    {
                        toolCalls.Add(built);
                    }
                }

                // 返回拼接好的完整 assistant 消息
                var content = contentBuilder.ToString();
                return ChatMessage.Assistant(
                    string.IsNullOrEmpty(content) ? null : content,
                    toolCalls.Count > 0 ? toolCalls : null
                );
            }
        }

        /// <summary>
        /// 发送 POST 请求并以 <see cref="HttpCompletionOption.ResponseHeadersRead"/> 模式返回，
        /// 用于流式响应。调用方负责 <c>Dispose</c>。
        /// </summary>
        private static async Task<HttpResponseMessage> SendPostForStreamAsync(
            string url, string apiKey, string body, CancellationToken ct)
        {
            var client = HttpClientFactory.GetClient();
            using var httpRequest = HttpClientFactory.CreateRequest(HttpMethod.Post, url, apiKey);
            httpRequest.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
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
        /// 遍历消息列表，修复所有 assistant 消息中 tool_calls 的 arguments 字段，
        /// 并统一清洗 tool_call id 格式（v1.14.0+）。
        /// <para>
        /// id 清洗动机：部分模型（如 GLM）生成的 tool_call id 允许包含冒号/点等字符，
        /// 自家网关不校验故从未暴露问题；但 fallback 到 AWS Bedrock 时，Bedrock 对
        /// <c>tool_use.id</c> 有严格正则校验（<c>^[a-zA-Z0-9_-]+$</c>），不合规 id 直接 400，
        /// 且历史消息里 assistant.tool_calls[].id 与对应 tool 消息的 tool_call_id 必须
        /// 清洗后依然一致（否则模型侧无法配对 tool_use/tool_result）。
        /// <see cref="SanitizeToolCallId"/> 是纯函数（同输入必同输出），故 assistant 侧和
        /// tool 侧分别独立清洗即可保持一致，无需维护映射表。幂等：已合规的 id 清洗后不变。
        /// </para>
        /// </summary>
        /// <param name="messages">待发送的消息列表（会就地修改）</param>
        private static void SanitizeMessageToolCalls(List<ChatMessage> messages)
        {
            if (messages == null) return;

            // v1.14.14 fix: 收集缺失 function.name 的 assistant tool_call 的 id，
            // 用于第二遍同步剔除其配对的 tool 消息，避免悬空的 tool_call_id 让模型侧无法配对。
            var droppedToolCallIds = new HashSet<string>();

            foreach (var message in messages)
            {
                if (message.ToolCalls != null && message.ToolCalls.Count > 0)
                {
                    foreach (var toolCall in message.ToolCalls)
                    {
                        // v1.14.13 fix: 门禁兜底 —— 即使源头(ToolCallBuilder.Build)已补 id，
                        // 老数据/特殊路径仍可能让 assistant.tool_calls[].id 为空/空白。空 id 发回
                        // API 会被严格 pydantic 校验以 `id: missing` 400 拒绝，这里在发送前兜底：
                        // 空/空白 id 也动态补一个唯一且合规的 id（不静默跳过、不用固定占位符，
                        // 避免多个空 id 工具调用共享同一 id 造成配对错乱），避免把坏消息交给服务端 400。
                        // 正常路径下该分支不触发（源头已保证非空）；触发仅见于历史遗留数据。
                        if (string.IsNullOrWhiteSpace(toolCall.Id))
                        {
                            toolCall.Id = Guid.NewGuid().ToString();
                        }
                        else
                        {
                            toolCall.Id = SanitizeToolCallId(toolCall.Id);
                        }

                        if (toolCall.Function != null && !string.IsNullOrEmpty(toolCall.Function.Arguments))
                        {
                            toolCall.Function.Arguments = SanitizeToolArguments(toolCall.Function.Arguments);
                        }

                        // v1.14.14 fix: 门禁兜底（同 id 的平行字段）—— function.name 缺失时，
                        // 该 assistant tool_call 是不完整的（发回 vLLM 会因缺 name 返回
                        // `function.name Field required` 400，且 ToolCallDispatcher 无法按名派发）。
                        // 正常路径下 Build() 已拦截新生成的缺 name tool_call；此分支只兜底历史遗留
                        // 数据/其它写路径写入的坏消息——把它们从列表剔除，并记下 id 用于清扫配对 tool 消息。
                        if (string.IsNullOrWhiteSpace(toolCall.Function?.Name))
                        {
                            AgentCore.Editor.Utils.AgentCoreLog.Warning(
                                "[AgentCore] Sanitize: dropping assistant tool_call with empty function.name (id=" +
                                (string.IsNullOrEmpty(toolCall.Id) ? "(generated)" : toolCall.Id) +
                                ") to avoid vLLM 'function.name Field required' 400.");
                            droppedToolCallIds.Add(toolCall.Id);
                        }
                    }
                }

                if (message.Role == "tool" && !string.IsNullOrEmpty(message.ToolCallId))
                {
                    message.ToolCallId = SanitizeToolCallId(message.ToolCallId);
                }
            }

            if (droppedToolCallIds.Count == 0) return;

            // 第二遍：过滤掉缺 name 的 assistant tool_call，并同步剔除配对的 tool 消息
            // （避免悬空 tool_call_id，否则模型侧无法配对 tool_use/tool_result）。
            for (int i = messages.Count - 1; i >= 0; i--)
            {
                var message = messages[i];

                if (message.Role == "assistant" && message.ToolCalls != null && message.ToolCalls.Count > 0)
                {
                    var filtered = message.ToolCalls.FindAll(
                        tc => !droppedToolCallIds.Contains(tc.Id) && !string.IsNullOrWhiteSpace(tc.Function?.Name));
                    message.ToolCalls = filtered.Count > 0 ? filtered : null;

                    // v1.14.14 防御：若该 assistant 消息的所有 tool_call 都被剔除且正文也为空，
                    // 会退化成 {role:assistant} 空消息，可能又被服务端以 content 缺失 400。
                    // 补一个可见占位正文，避免引入新的坏消息。
                    if (filtered.Count == 0 && string.IsNullOrEmpty(message.Content))
                    {
                        message.Content = "(tool_call dropped: function.name missing from stream)";
                    }
                }
                else if (message.Role == "tool" && !string.IsNullOrEmpty(message.ToolCallId)
                         && droppedToolCallIds.Contains(message.ToolCallId))
                {
                    // 该 tool 消息对应的 assistant tool_call 已被剔除，作为整体移除，
                    // 保持 tool_messages 与 assistant tool_calls 配对完整，避免模型侧解析错乱。
                    // 因消息定序由 AgentLoop 保证（assistant 在前、其 tool 结果紧随其后），
                    // 这里只按 id 配对移除，不额外假设位置。
                    messages.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// 将 tool_call id 清洗为跨供应商兼容的格式：仅保留 <c>[a-zA-Z0-9_-]</c>，
        /// 其余字符（冒号、点、斜杠等）替换为下划线。空结果 / 空输入回退为固定占位符
        /// （极端场景，正常不会触发——id 通常非空且含字母数字）。
        /// </summary>
        private static string SanitizeToolCallId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "call_unknown";

            var sb = new StringBuilder(id.Length);
            foreach (var c in id)
            {
                sb.Append(IsValidToolCallIdChar(c) ? c : '_');
            }

            var result = sb.ToString();
            return result.Length > 0 ? result : "call_unknown";
        }

        private static bool IsValidToolCallIdChar(char c)
            => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-';

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

            // 策略 3：最后手段 — 将整个 arguments 作为纯文本包装。
            // v1.14.13 note(Bug A 已实测证伪): 曾怀疑此处二次转义，经 sourceai vLLM(8000)
            // 实测带 id 的 _raw_arguments 形态被正常解析(200 OK)，该层转义为合法 JSON 所需，非 bug。请勿改。
            AgentCoreLog.Warning($"[AgentCore] Unable to fully repair tool arguments, wrapping as raw text. Original (first 200 chars): {arguments.Substring(0, Math.Min(200, arguments.Length))}");
            var safeContent = Newtonsoft.Json.JsonConvert.SerializeObject(arguments);
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

            /// <summary>
            /// 累积流式工具调用并生成最终 <see cref="ToolCall"/>。
            /// 可能返回 null —— 表示该 tool_call 数据不完整、不可用，调用方应跳过而不是发回 API。
            /// </summary>
            public ToolCall Build()
            {
                // v1.14.13 fix: 流式 tool_call id 可能因模型/网关返回的第一片 chunk 未带 id、
                // 或 id 格式异常被解析器吞掉而缺失，导致最终 ToolCall.Id 为空。空 id 发回
                // API 会被严格 pydantic 校验以 `id: missing` 直接 400 拒绝（实测：大 arguments
                // 多 chunk 时触发）。这里在源头兜底：id 为空/空白时动态生成一个唯一且合规的 id
                // （与系统 ConversationTurn.Id 同源的 GUID 格式，连字符在 Bedrock 正则
                // ^[a-zA-Z0-9_-]+$ 中合规），保证 assistant.tool_calls[].id 非空，且与后续
                // tool 消息的 tool_call_id 源出一致（该 ToolCall 是两边共用的唯一 id 源）。
                var id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString() : Id;

                // v1.14.14 fix: 与 id 同源但漏修的字段 —— function.name。流式首片 chunk 中的
                // function.name 若未被拼装到（大 arguments 多 chunk 分片、服务端只发 arguments
                // 增量等），FunctionName 为空。若原样构造 Function.Name=null，JsonHelper 以
                // NullValueHandling.Ignore 序列化会把 name 键整体省略，发出
                // "function":{"arguments":...} —— vLLM pydantic 判别式 union 对它报
                // `function.name -> Field required` 直接 400（实测 diag_20260814_153523）。
                // 与 id 不同，name 语义上必须是真实工具名、不能编造 GUID（编造会污染历史、
                // 且 ToolCallDispatcher 会报 Unknown tool），因此这里不伪造名字而是返回 null，
                // 由调用方跳过该残缺 tool_call（不写入历史、不发回 API），并记告警提示重试/缩体积。
                if (string.IsNullOrWhiteSpace(FunctionName))
                {
                    AgentCore.Editor.Utils.AgentCoreLog.Warning(
                        "[AgentCore] Streamed tool_call has no function.name; dropping this tool call " +
                        "(avoids sending a malformed assistant tool_call that vLLM would reject with " +
                        "'function.name Field required'). Consider narrowing the single tool_call, or retry.");
                    return null;
                }

                return new ToolCall
                {
                    Id = id,
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
}
