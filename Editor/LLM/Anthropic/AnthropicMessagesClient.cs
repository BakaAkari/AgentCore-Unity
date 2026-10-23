using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Config;
using AgentCore.Editor.Utils;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.LLM.Anthropic
{
    /// <summary>
    /// Anthropic 原生 Messages API 客户端（v1.17.0）。
    /// <para>
    /// 与 <see cref="OpenAICompatibleClient"/> 实现同一 <see cref="ILLMClient"/> 接口，上层（AgentLoop、压缩、
    /// 标题生成）无感知。相比经 OpenAI 兼容网关访问 Claude，原生协议带来：显式 prompt caching 断点
    /// （命中部分按 0.1× 计费）、thinking 块签名回放、精确的缓存命中用量上报。
    /// </para>
    /// <para>
    /// 连接参数全部来自 <see cref="ActiveModelConfig"/>（同样受 <see cref="ActiveModelConfig.UseProfile"/> 作用域影响）。
    /// 400 恢复策略：错误提及 thinking/signature → 去掉 thinking 回放并关闭思考后重试一次；
    /// 否则交给 <see cref="RequestPruningRegistry"/> 学习禁用字段后重试一次。
    /// </para>
    /// </summary>
    public class AnthropicMessagesClient : ILLMClient
    {
        /// <summary>Anthropic API 版本头。</summary>
        public const string ApiVersion = "2023-06-01";

        public async Task<ChatCompletionResponse> ChatCompletionAsync(
            List<ChatMessage> messages,
            List<ToolDefinition> tools = null,
            CancellationToken ct = default,
            int? contentMaxTokens = null,
            string reasoningLevelOverride = null)
        {
            var model = ActiveModelConfig.ModelName;
            using var response = await SendWithRecoveryAsync(messages, tools, stream: false, contentMaxTokens, reasoningLevelOverride, ct);
            var body = await response.Content.ReadAsStringAsync();
            JObject json;
            try { json = JObject.Parse(body); }
            catch (Exception ex) { throw new InvalidOperationException($"Failed to parse Anthropic response: {ex.Message}\n{body}"); }

            var acc = AnthropicStreamAccumulator.FromMessage(json, model);
            var message = acc.BuildMessage();
            UsageLedger.Record(acc.Usage, "anthropic", ActiveModelConfig.CacheTtl1h);

            return new ChatCompletionResponse
            {
                Id = json.Value<string>("id"),
                Object = "chat.completion",
                Model = acc.Model,
                Choices = new List<Choice>
                {
                    new() { Index = 0, Message = message, FinishReason = AnthropicMessageMapper.MapStopReason(acc.StopReason) }
                },
                Usage = ToLegacyUsage(acc.Usage),
                NormalizedUsage = acc.Usage
            };
        }

        public async Task<ChatMessage> ChatCompletionStreamAsync(
            List<ChatMessage> messages,
            Action<StreamChunk> onChunk,
            List<ToolDefinition> tools = null,
            CancellationToken ct = default,
            int? contentMaxTokens = null,
            string reasoningLevelOverride = null)
        {
            var model = ActiveModelConfig.ModelName;
            using var response = await SendWithRecoveryAsync(messages, tools, stream: true, contentMaxTokens, reasoningLevelOverride, ct);
            using var stream = await response.Content.ReadAsStreamAsync();

            var acc = new AnthropicStreamAccumulator(model);
            await AnthropicSseReader.ReadAsync(stream, evt => acc.Process(evt, onChunk), ct);

            ct.ThrowIfCancellationRequested();
            if (!acc.Completed)
                throw new EndOfStreamException("Anthropic stream ended without message_stop.");

            UsageLedger.Record(acc.Usage, "anthropic-stream", ActiveModelConfig.CacheTtl1h);
            return acc.BuildMessage();
        }

        /// <summary>
        /// 发送请求；对 400 做一次性恢复重试。返回成功响应（调用方 Dispose），失败抛 <see cref="HttpRequestException"/>。
        /// </summary>
        private static async Task<HttpResponseMessage> SendWithRecoveryAsync(
            List<ChatMessage> messages, List<ToolDefinition> tools, bool stream,
            int? contentMaxTokens, string reasoningLevelOverride, CancellationToken ct)
        {
            var endpoint = ActiveModelConfig.Endpoint;
            var model = ActiveModelConfig.ModelName;
            var url = BuildMessagesUrl(endpoint);
            OpenAICompatibleClient.SanitizeMessageToolCalls(messages);

            var options = BuildOptions(stream, contentMaxTokens, reasoningLevelOverride);
            var toolChangesKey = endpoint + "|" + model;
            if (options.ToolChanges && ToolChangesRejected.ContainsKey(toolChangesKey)) options.ToolChanges = false;
            var headers = BuildRequestHeaders(options, tools);
            var json = BuildJson(options, messages, tools);
            AgentCoreLog.Debug($"[AgentCore] Anthropic request: {url} model={model} messages={messages?.Count ?? 0} stream={stream}");

            var response = await OpenAIChatTransporter.SendJsonAsync(url, headers, RequestPruningRegistry.ApplyPruning(endpoint, model, json), stream, ct);
            if (response.IsSuccessStatusCode) return response;

            var errorBody = await response.Content.ReadAsStringAsync();
            int status = (int)response.StatusCode;
            var reason = response.ReasonPhrase;
            response.Dispose();

            if (status == 400)
            {
                string retryJson = null;
                if (AnthropicMessageMapper.UsesToolChanges(options, tools) && IsToolChangeError(errorBody))
                {
                    // 网关 / 平台不支持会话中途工具变更：本会话内记住，退回"只发送已启用工具"
                    AgentCoreLog.Info("[AgentCore] Anthropic 400 on mid-conversation tool changes; retrying with enabled tools only.");
                    ToolChangesRejected[toolChangesKey] = true;
                    options.ToolChanges = false;
                    headers = BuildRequestHeaders(options, tools);
                    retryJson = BuildJson(options, messages, tools);
                }
                else if (IsThinkingRelatedError(errorBody))
                {
                    AgentCoreLog.Info("[AgentCore] Anthropic 400 on thinking replay; retrying once without thinking blocks.");
                    options.DropReasoning = true;
                    options.Level = ReasoningParamMapper.ReasoningLevel.Off;
                    retryJson = BuildJson(options, messages, tools);
                }
                else
                {
                    var learned = RequestPruningRegistry.LearnFromErrorResponse(endpoint, model, errorBody);
                    if (learned.Count > 0)
                    {
                        AgentCoreLog.Info($"[AgentCore] Anthropic request auto-retry after learning banned field(s): [{string.Join(", ", learned)}]");
                        retryJson = json;
                    }
                }

                if (retryJson != null)
                {
                    response = await OpenAIChatTransporter.SendJsonAsync(url, headers, RequestPruningRegistry.ApplyPruning(endpoint, model, retryJson), stream, ct);
                    if (response.IsSuccessStatusCode) return response;
                    errorBody = await response.Content.ReadAsStringAsync();
                    status = (int)response.StatusCode;
                    reason = response.ReasonPhrase;
                    response.Dispose();
                }
            }

            throw new HttpRequestException($"LLM API error: HTTP {status} {reason}\n{errorBody}");
        }

        private static AnthropicRequestOptions BuildOptions(bool stream, int? contentMaxTokens, string reasoningLevelOverride)
        {
            var model = ActiveModelConfig.ModelName;
            var profile = ModelRegistry.Lookup(model);
            return new AnthropicRequestOptions
            {
                Model = model,
                Stream = stream,
                ContentMaxTokens = contentMaxTokens ?? ActiveModelConfig.MaxTokens,
                Level = ReasoningParamMapper.ParseLevel(reasoningLevelOverride),
                DefaultReasoningEnabled = ActiveModelConfig.EnableReasoningOutput,
                DefaultEffort = ActiveModelConfig.ReasoningEffort,
                DefaultBudgetTokens = ActiveModelConfig.ReasoningMaxTokens,
                Temperature = ActiveModelConfig.Temperature,
                CacheTtl1h = ActiveModelConfig.CacheTtl1h,
                Profile = profile,
                ToolChanges = profile.SupportsToolChanges
            };
        }

        private static string BuildJson(AnthropicRequestOptions options, List<ChatMessage> messages, List<ToolDefinition> tools)
        {
            var body = AnthropicMessageMapper.BuildRequest(options, messages, tools);
            var extra = ActiveModelConfig.ExtraRequestBody;
            if (!string.IsNullOrWhiteSpace(extra)) RequestEnrichment.MergeExtraBody(body, extra);
            RequestEnrichment.RemoveNullProperties(body);
            return body.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// base URL → messages 端点：<c>https://api.anthropic.com</c> 与 <c>https://api.anthropic.com/v1</c> 都映射到
        /// <c>https://api.anthropic.com/v1/messages</c>；已是完整 <c>/messages</c> 地址则原样使用。
        /// </summary>
        public static string BuildMessagesUrl(string endpoint)
        {
            var e = (endpoint ?? string.Empty).Trim().TrimEnd('/');
            if (e.EndsWith("/messages", StringComparison.OrdinalIgnoreCase)) return e;
            if (e.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) return e + "/messages";
            return e + "/v1/messages";
        }

        /// <summary>拒绝会话中途工具变更的 endpoint|model（进程内记忆，域重载后重新探测）。</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> ToolChangesRejected = new();

        private static Dictionary<string, string> BuildRequestHeaders(AnthropicRequestOptions options, List<ToolDefinition> tools)
        {
            var headers = BuildHeaders(ActiveModelConfig.ApiKey, ActiveModelConfig.AuthScheme);
            if (AnthropicMessageMapper.UsesToolChanges(options, tools)) headers["anthropic-beta"] = ToolChanges.AnthropicBeta;
            return headers;
        }

        /// <summary>400 是否由会话中途工具变更（defer_loading / tool_addition / 中途 system 消息）引起。</summary>
        internal static bool IsToolChangeError(string errorBody)
        {
            if (string.IsNullOrEmpty(errorBody)) return false;
            var s = errorBody.ToLowerInvariant();
            return s.Contains("defer_loading") || s.Contains("tool_addition") || s.Contains("tool_reference")
                   || s.Contains("anthropic-beta") || s.Contains("role 'system'") || s.Contains("mid-conversation");
        }

        internal static Dictionary<string, string> BuildHeaders(string apiKey, string authScheme)
        {
            var headers = OpenAIChatTransporter.BuildAuthHeaders(apiKey, authScheme != AuthSchemes.Bearer);
            headers["anthropic-version"] = ApiVersion;
            return headers;
        }

        /// <summary>400 是否由 thinking 块回放引起（签名失效 / 历史被编辑 / 思考配置与历史不一致）。</summary>
        internal static bool IsThinkingRelatedError(string errorBody)
        {
            if (string.IsNullOrEmpty(errorBody)) return false;
            var s = errorBody.ToLowerInvariant();
            return s.Contains("thinking") || s.Contains("signature");
        }

        private static Usage ToLegacyUsage(LlmUsage u)
        {
            if (u == null) return null;
            return new Usage { PromptTokens = u.TotalInput, CompletionTokens = u.Output, TotalTokens = u.TotalInput + u.Output };
        }
    }

    /// <summary>
    /// Anthropic SSE 读取器：解析 <c>data:</c> 行为 JSON 事件（事件类型取自 data 内 <c>type</c> 字段，
    /// 不依赖 <c>event:</c> 行）。复用 <see cref="StreamingResponseParser"/> 的空闲超时看门狗与主线程让步预算。
    /// </summary>
    internal static class AnthropicSseReader
    {
        private const long YieldBudgetMs = 200;

        public static async Task ReadAsync(Stream stream, Action<JObject> onEvent, CancellationToken ct)
        {
            using var reader = new StreamReader(stream);
            var yieldTimer = System.Diagnostics.Stopwatch.StartNew();

            while (!ct.IsCancellationRequested)
            {
                string line;
                try
                {
                    line = await StreamingResponseParser.ReadLineWithIdleTimeoutAsync(reader, stream, ct);
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }

                if (line == null)
                    throw new EndOfStreamException("Anthropic stream ended without message_stop.");
                if (!line.StartsWith("data:")) continue;

                var data = line.Substring(5).TrimStart();
                if (data.Length == 0 || data == "[DONE]") continue;

                JObject evt;
                try { evt = JObject.Parse(data); }
                catch (Exception ex)
                {
                    AgentCoreLog.Warning($"[AgentCore] Anthropic SSE parse error: {ex.Message}\nData: {data}");
                    continue;
                }

                onEvent(evt);
                if (evt.Value<string>("type") == "message_stop") return;

                if (yieldTimer.ElapsedMilliseconds >= YieldBudgetMs)
                {
                    await Task.Yield();
                    yieldTimer.Restart();
                }
            }
            ct.ThrowIfCancellationRequested();
        }
    }
}
