using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Config;
using AgentCore.Editor.Utils;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.LLM
{
    /// <summary>
    /// 视觉模型客户端（v1.15.0）。独立于主模型的 provider-profile 体系实例化。
    /// <para>
    /// 连接参数（endpoint / model / apiKey）全部来自 <see cref="VisionModelConfig"/>
    /// （独立单一配置，不可热切换），经 <see cref="OpenAIChatTransporter"/>（与主模型共用同一
    /// 底层 HTTP 发送端）发送 OpenAI 兼容的多模态 chat completion 请求，返回视觉模型对图片的
    /// 文字描述 —— 供主 LLM（如文本型的 DeepSeek-V4）拿文字做视觉矫正。
    /// </para>
    /// <para>
    /// 复用主模型的安全网：对 400 响应走 <see cref="RequestPruningRegistry"/> 自动学习禁字段并
    /// 重试一次（视觉模型同为 OpenAI 兼容，踩同样 400 问题可自助恢复）。
    /// </para>
    /// </summary>
    public static class VisionLLMClient
    {
        /// <summary>
        /// 分析一张图片，返回视觉模型的文字描述。
        /// </summary>
        /// <param name="imageBase64DataUrl">图片的 base64 data URL（如 <c>data:image/png;base64,xxx</c>）</param>
        /// <param name="prompt">对视觉模型的分析指令（如 "describe this scene, note UI layout, colors, any visible errors"）</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>视觉模型的文字描述（不含 tool_calls；若含 tool_calls 则返回消息文本为空时的提示）</returns>
        /// <exception cref="InvalidOperationException">视觉未配置 / 请求失败 / 空响应</exception>
        public static async Task<string> AnalyzeImageAsync(
            string imageBase64DataUrl, string prompt, CancellationToken ct = default)
        {
            if (!VisionModelConfig.IsEnabled)
                throw new InvalidOperationException("Vision model is not enabled. Enable it in Project Settings > AgentCore > Model & Agent (Vision Model).");
            if (!VisionModelConfig.IsConfigured)
                throw new InvalidOperationException("Vision model is not fully configured (endpoint/model missing). Configure it in Project Settings > AgentCore > Model & Agent (Vision Model).");
            if (string.IsNullOrWhiteSpace(imageBase64DataUrl))
                throw new ArgumentException("imageBase64DataUrl is empty.", nameof(imageBase64DataUrl));

            var endpoint = VisionModelConfig.Endpoint;
            var model = VisionModelConfig.ModelName;
            var apiKey = VisionModelConfig.ApiKey;

            // OpenAI 多模态消息: content 为数组, 含 image_url + text 两部分。
            var request = new JObject
            {
                ["model"] = model,
                ["messages"] = new JArray
                {
                    new JObject
                    {
                        ["role"] = "user",
                        ["content"] = new JArray
                        {
                            new JObject
                            {
                                ["type"] = "image_url",
                                ["image_url"] = new JObject { ["url"] = imageBase64DataUrl }
                            },
                            new JObject { ["type"] = "text", ["text"] = prompt ?? "Describe this image in detail." }
                        }
                    }
                },
                // 视觉分析是描述性任务: 显式关 reasoning, 避免某些 OpenAI 兼容后端对
                // reasoning 字段的困惑; 保守只发 model/messages/max_tokens, 超出的字段交给 Pruning 自学剔除。
                ["max_tokens"] = 1024
            };

            var url = OpenAIChatTransporter.BuildChatCompletionsUrl(endpoint);
            var baseJson = request.ToString(Newtonsoft.Json.Formatting.None);

            // 首次发送: 应用当前已知 pruning 规则(与主模型同一套安全网)
            var body = RequestPruningRegistry.ApplyPruning(endpoint, model, baseJson);
            var (response, responseBody) = await OpenAIChatTransporter.PostAsync(url, apiKey, body, ct);

            // 400 → 尝试从错误消息学习禁字段, 学到则重试一次(不递归)
            if (!response.IsSuccessStatusCode && (int)response.StatusCode == 400)
            {
                var learned = RequestPruningRegistry.LearnFromErrorResponse(endpoint, model, responseBody);
                response.Dispose();
                if (learned.Count > 0)
                {
                    AgentCoreLog.Info($"[AgentCore] Vision request auto-retry after learning {learned.Count} banned field(s): [{string.Join(", ", learned)}]");
                    var retryBody = RequestPruningRegistry.ApplyPruning(endpoint, model, baseJson);
                    (response, responseBody) = await OpenAIChatTransporter.PostAsync(url, apiKey, retryBody, ct);
                }
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"Vision API error: HTTP {(int)response.StatusCode} {response.ReasonPhrase}\n{responseBody}");
                }

                var result = JsonHelper.Deserialize<ChatCompletionResponse>(responseBody);
                if (result == null)
                {
                    throw new InvalidOperationException($"Failed to parse vision response:\n{responseBody}");
                }

                var msg = result.GetMessage();
                var text = msg?.Content;
                if (string.IsNullOrWhiteSpace(text))
                {
                    // 视觉模型正常应只返回文本; 若出 reasoning-only 或空, 如实提示而非返回空串。
                    throw new InvalidOperationException("Vision model returned empty text content.");
                }
                return text;
            }
        }
    }
}
