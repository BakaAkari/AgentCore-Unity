using AgentCore.Editor.Config;
using AgentCore.Editor.Utils;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AgentCore.Editor.LLM
{
    /// <summary>
    /// 请求增强层：在序列化后的 JSON 请求体上注入额外参数。
    /// 设计目标：不污染强类型 ChatCompletionRequest 模型，
    /// 在 JSON 层面干净地合并 reasoning、stream_options 和用户自定义 extra body。
    /// </summary>
    public static class RequestEnrichment
    {
        /// <summary>
        /// 将 ChatCompletionRequest 序列化为 JSON，并根据 Settings 配置注入额外字段。
        /// 注入顺序：stream_options → reasoning → extraRequestBody → 清除 null 值。
        /// </summary>
        /// <param name="request">强类型请求对象</param>
        /// <param name="settings">当前设置实例</param>
        /// <returns>增强后的 JSON 字符串，可直接作为 HTTP body 发送</returns>
        public static string BuildEnrichedJson(ChatCompletionRequest request, AgentCoreSettings settings)
        {
            // Step 1: 序列化为 JObject（保留 null 移除语义由 JsonHelper 控制）
            var baseJson = JsonHelper.Serialize(request);
            var body = JObject.Parse(baseJson);

            // Step 2: 注入 stream_options（仅流式请求）
            if (request.Stream)
            {
                InjectStreamOptions(body);
            }

            // Step 3: 注入 reasoning 参数（当启用时）
            if (settings.enableReasoningOutput)
            {
                InjectReasoning(body, settings.reasoningEffort, settings.reasoningMaxTokens);
            }

            // Step 4: 深度合并用户自定义 extra body
            if (!string.IsNullOrWhiteSpace(settings.extraRequestBody))
            {
                MergeExtraBody(body, settings.extraRequestBody);
            }

            // Step 5: 清除所有值为 null 的属性（防止某些 API 对 null 敏感）
            RemoveNullProperties(body);

            return body.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// 注入 stream_options，确保流式响应包含 usage 信息。
        /// 如果 body 中已有 stream_options，则合并而非覆盖。
        /// </summary>
        private static void InjectStreamOptions(JObject body)
        {
            if (body["stream_options"] is JObject existing)
            {
                // 保留已有字段，仅确保 include_usage 为 true
                existing["include_usage"] = true;
            }
            else
            {
                body["stream_options"] = new JObject
                {
                    ["include_usage"] = true
                };
            }
        }

        /// <summary>
        /// 注入 reasoning 参数，触发支持推理的模型返回 reasoning_content。
        /// 兼容 OpenRouter 和其他 OpenAI 兼容代理的 reasoning 协议。
        /// </summary>
        /// <param name="body">请求 JSON body</param>
        /// <param name="effort">推理努力级别（low/medium/high），空字符串表示不指定</param>
        /// <param name="maxTokens">推理最大 token 数，0 表示不限制</param>
        private static void InjectReasoning(JObject body, string effort, int maxTokens)
        {
            var reasoning = new JObject();

            if (!string.IsNullOrWhiteSpace(effort))
            {
                reasoning["effort"] = effort.Trim().ToLowerInvariant();
            }

            if (maxTokens > 0)
            {
                reasoning["max_tokens"] = maxTokens;
            }

            // 即使 reasoning 为空对象 {}，也要注入 — 这是触发 OpenRouter 返回 reasoning_content 的最低要求
            body["reasoning"] = reasoning;
        }

        /// <summary>
        /// 将用户自定义的 JSON 字符串深度合并到请求 body 中。
        /// 使用 JObject.Merge 的 MergeArrayHandling.Replace 策略：
        /// - 对象属性递归合并
        /// - 数组整体替换（用户意图优先）
        /// - 标量值覆盖（用户意图优先）
        /// </summary>
        private static void MergeExtraBody(JObject body, string extraJson)
        {
            try
            {
                var extra = JObject.Parse(extraJson);
                body.Merge(extra, new JsonMergeSettings
                {
                    MergeArrayHandling = MergeArrayHandling.Replace,
                    MergeNullValueHandling = MergeNullValueHandling.Merge
                });
            }
            catch (Newtonsoft.Json.JsonReaderException ex)
            {
                // extraRequestBody 格式错误时静默跳过，不阻断请求
                Debug.LogWarning($"[AgentCore] RequestEnrichment: failed to parse extraRequestBody, skipping merge. Error: {ex.Message}");
            }
        }

        /// <summary>
        /// 递归移除 JObject 中所有值为 JTokenType.Null 的属性。
        /// 某些 LLM API（如 Ollama）对 null 字段敏感，返回 400 错误。
        /// </summary>
        private static void RemoveNullProperties(JObject obj)
        {
            var propertiesToRemove = new System.Collections.Generic.List<string>();

            foreach (var property in obj.Properties())
            {
                if (property.Value.Type == JTokenType.Null)
                {
                    propertiesToRemove.Add(property.Name);
                }
                else if (property.Value is JObject childObj)
                {
                    RemoveNullProperties(childObj);
                }
            }

            foreach (var name in propertiesToRemove)
            {
                obj.Remove(name);
            }
        }
    }
}
