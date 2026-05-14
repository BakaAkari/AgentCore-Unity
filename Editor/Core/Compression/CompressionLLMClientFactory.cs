using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Config;
using AgentCore.Editor.LLM;
using AgentCore.Editor.Utils;
using UnityEngine;

namespace AgentCore.Editor.Core.Compression
{
    /// <summary>
    /// 压缩专用 LLM 客户端工厂 — 创建用于上下文压缩任务的 ILLMClient 实例。
    /// <para>
    /// 支持两种模式：
    /// <list type="bullet">
    ///   <item>分离模式：使用独立的 endpoint/model/apiKey（推荐，使用快速廉价模型如 Haiku）</item>
    ///   <item>共享模式：复用主 LLM 客户端（配置简单，但成本较高）</item>
    /// </list>
    /// </para>
    /// <para>
    /// 设计原则：
    /// <list type="bullet">
    ///   <item>压缩请求使用非流式调用（不需要流式输出）</item>
    ///   <item>低温度（0.1）确保压缩结果稳定一致</item>
    ///   <item>较小的 max_tokens（512）限制压缩输出长度</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class CompressionLLMClientFactory
    {
        /// <summary>压缩请求的默认温度（低温度确保稳定输出）</summary>
        private const float CompressionTemperature = 0.1f;

        /// <summary>压缩请求的默认最大输出 token 数</summary>
        private const int CompressionMaxTokens = 512;

        /// <summary>
        /// 创建压缩专用的 LLM 客户端。
        /// <para>
        /// 根据 <see cref="AgentCoreSettings"/> 中的压缩配置决定使用分离模式还是共享模式：
        /// <list type="bullet">
        ///   <item>如果 <c>useSeparateCompressionLLM = true</c> 且配置了压缩 LLM endpoint，使用分离模式</item>
        ///   <item>否则返回 null，调用方应使用主 LLM 客户端</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <returns>压缩专用 ILLMClient 实例，或 null 表示应使用主 LLM</returns>
        public static ILLMClient CreateCompressionClient()
        {
            var settings = AgentCoreSettings.instance;

            if (!settings.useSeparateCompressionLLM)
            {
                return null; // 使用主 LLM
            }

            // 检查压缩 LLM 配置是否有效
            if (string.IsNullOrWhiteSpace(settings.compressionLLMEndpoint))
            {
                Debug.LogWarning("[AgentCore] Compression LLM endpoint not configured, falling back to main LLM.");
                return null;
            }

            if (string.IsNullOrWhiteSpace(settings.compressionLLMModel))
            {
                Debug.LogWarning("[AgentCore] Compression LLM model not configured, falling back to main LLM.");
                return null;
            }

            // 获取 API Key：优先使用压缩专用 key，否则使用主 LLM key
            string apiKey = SecureKeyStorage.GetCompressionLLMApiKey();
            if (string.IsNullOrEmpty(apiKey))
            {
                apiKey = SecureKeyStorage.GetLLMApiKey();
            }

            return new CompressionLLMClient(
                settings.compressionLLMEndpoint,
                settings.compressionLLMModel,
                apiKey,
                CompressionTemperature,
                CompressionMaxTokens
            );
        }
    }

    /// <summary>
    /// 压缩专用 LLM 客户端 — 轻量级 OpenAI 兼容客户端，专为压缩任务优化。
    /// <para>
    /// 与 <see cref="OpenAICompatibleClient"/> 的区别：
    /// <list type="bullet">
    ///   <item>使用固定的 endpoint/model/apiKey（不从 Settings 动态读取）</item>
    ///   <item>仅支持非流式调用（压缩不需要流式输出）</item>
    ///   <item>低温度 + 小 max_tokens（压缩输出简短稳定）</item>
    ///   <item>不发送 tools 参数（压缩不需要工具调用）</item>
    /// </list>
    /// </para>
    /// </summary>
    internal class CompressionLLMClient : ILLMClient
    {
        private readonly string _endpoint;
        private readonly string _model;
        private readonly string _apiKey;
        private readonly float _temperature;
        private readonly int _maxTokens;

        /// <summary>
        /// 创建压缩专用 LLM 客户端。
        /// </summary>
        /// <param name="endpoint">API 端点 URL</param>
        /// <param name="model">模型名称</param>
        /// <param name="apiKey">API Key</param>
        /// <param name="temperature">生成温度</param>
        /// <param name="maxTokens">最大输出 token 数</param>
        public CompressionLLMClient(string endpoint, string model, string apiKey, float temperature, int maxTokens)
        {
            _endpoint = endpoint?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(endpoint));
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _apiKey = apiKey ?? "";
            _temperature = temperature;
            _maxTokens = maxTokens;
        }

        /// <summary>
        /// 非流式 Chat Completion 调用（压缩任务的主要入口）。
        /// </summary>
        public async Task<ChatCompletionResponse> ChatCompletionAsync(
            List<ChatMessage> messages,
            List<ToolDefinition> tools = null,
            CancellationToken ct = default)
        {
            var request = new ChatCompletionRequest
            {
                Model = _model,
                Messages = messages,
                Tools = null, // 压缩不需要工具
                Stream = false,
                Temperature = _temperature,
                MaxTokens = _maxTokens
            };

            var json = JsonHelper.Serialize(request);
            var url = $"{_endpoint}/chat/completions";

            var client = HttpClientFactory.GetClient();
            using var httpRequest = HttpClientFactory.CreateRequest(HttpMethod.Post, url, _apiKey);
            httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.SendAsync(httpRequest, ct);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Compression LLM API error: HTTP {(int)response.StatusCode} {response.ReasonPhrase}\n{responseBody}");
            }

            var result = JsonHelper.Deserialize<ChatCompletionResponse>(responseBody);
            if (result == null)
            {
                throw new InvalidOperationException($"Failed to parse compression LLM response:\n{responseBody}");
            }

            return result;
        }

        /// <summary>
        /// 流式调用（压缩不使用流式，但接口要求实现）。
        /// 内部调用非流式接口并模拟流式回调。
        /// </summary>
        public async Task<ChatMessage> ChatCompletionStreamAsync(
            List<ChatMessage> messages,
            Action<StreamChunk> onChunk,
            List<ToolDefinition> tools = null,
            CancellationToken ct = default)
        {
            // 压缩任务不需要流式，直接调用非流式接口
            var response = await ChatCompletionAsync(messages, null, ct);

            if (response?.Choices != null && response.Choices.Count > 0)
            {
                var message = response.Choices[0].Message;

                // 模拟流式完成回调
                if (onChunk != null && !string.IsNullOrEmpty(message?.Content))
                {
                    onChunk(new StreamChunk { Type = StreamChunkType.ContentToken, Content = message.Content });
                    onChunk(new StreamChunk { Type = StreamChunkType.Done, FinishReason = "stop" });
                }

                return message;
            }

            return null;
        }
    }
}
