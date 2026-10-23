using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using AgentCore.Editor.Config;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.LLM
{
    /// <summary>
    /// OpenAI 兼容协议的方言差异层（v1.17.0）。
    /// <para>
    /// "OpenAI 兼容"只保证基本形状一致，缓存与推理相关的扩展各家不同。本类在 enrichment 之后、
    /// pruning 之前对请求 JSON 做纯函数式改写，规则全部按 <see cref="OpenAIDialects"/> 与
    /// <see cref="ModelRegistry"/> 判定：
    /// </para>
    /// <list type="bullet">
    /// <item><b>openai</b>：注入稳定的 <c>prompt_cache_key</c>（同一会话前缀 → 同一 key → 路由到同一缓存分片）。</item>
    /// <item><b>openrouter / litellm + Claude 模型</b>：在系统前缀末尾与最后一条 user/tool 消息上打
    ///     <c>cache_control</c> 内容块（这两类网关透传给 Anthropic；不打则 Claude 0% 命中）。</item>
    /// <item><b>deepseek</b>（或注册表标记需回传的模型）：带 tool_calls 的 assistant 消息回传
    ///     <c>reasoning_content</c>（思考模式工具调用的协议要求，缺失会 400 或丢失推理连续性）。</item>
    /// <item><b>generic / vllm</b>：不改写（vLLM 自动前缀缓存无需提示）。</item>
    /// </list>
    /// </summary>
    public static class OpenAIDialectAdapter
    {
        /// <summary>
        /// 按方言改写请求体（就地修改 <paramref name="body"/>）。
        /// </summary>
        /// <param name="body">已 enrichment 的请求 JSON</param>
        /// <param name="messages">与 <c>body.messages</c> 一一对应的原始消息（读取 JsonIgnore 字段用）</param>
        /// <param name="dialect">已解析的方言（<see cref="ActiveModelConfig.Dialect"/>）</param>
        /// <param name="model">模型名</param>
        public static void Apply(JObject body, IReadOnlyList<ChatMessage> messages, string dialect, string model)
        {
            if (body == null) return;
            var wireMessages = body["messages"] as JArray;
            var profile = ModelRegistry.Lookup(model);

            if (ShouldEchoReasoning(dialect, profile))
            {
                EchoReasoningContent(wireMessages, messages);
            }

            switch (dialect)
            {
                case OpenAIDialects.OpenAI:
                    if (body["prompt_cache_key"] == null)
                    {
                        var key = ComputePromptCacheKey(model, messages);
                        if (key != null) body["prompt_cache_key"] = key;
                    }
                    break;

                case OpenAIDialects.OpenRouter:
                case OpenAIDialects.LiteLLM:
                    if (profile.Family == ModelFamily.Anthropic)
                    {
                        ApplyCacheControlParts(wireMessages);
                    }
                    break;
            }
        }

        /// <summary>是否需要回传 reasoning_content。</summary>
        internal static bool ShouldEchoReasoning(string dialect, ModelProfile profile)
        {
            if (dialect == OpenAIDialects.DeepSeek) return true;
            if (dialect == OpenAIDialects.OpenAI || dialect == OpenAIDialects.OpenRouter) return false;
            return profile != null && profile.EchoReasoningContent;
        }

        /// <summary>
        /// 对带 tool_calls 的 assistant 消息回传 <c>reasoning_content</c>（仅 tool 调用轮需要，
        /// 普通回答轮的 reasoning 不回传以免浪费输入 token）。
        /// </summary>
        internal static void EchoReasoningContent(JArray wireMessages, IReadOnlyList<ChatMessage> messages)
        {
            if (wireMessages == null || messages == null) return;
            int n = Math.Min(wireMessages.Count, messages.Count);
            for (int i = 0; i < n; i++)
            {
                var m = messages[i];
                if (m == null || m.Role != "assistant") continue;
                if (m.ToolCalls == null || m.ToolCalls.Count == 0) continue;
                if (string.IsNullOrEmpty(m.ReasoningContent)) continue;
                if (wireMessages[i] is JObject wm && wm["reasoning_content"] == null)
                {
                    wm["reasoning_content"] = m.ReasoningContent;
                }
            }
        }

        /// <summary>
        /// 稳定的 prompt_cache_key：模型名 + 前两条消息（系统提示 + 首条用户消息）的 SHA-256 前 16 hex。
        /// 同一会话中这两条不变 → key 不变；不同会话即使系统提示相同也因首条用户消息不同而分流。
        /// </summary>
        internal static string ComputePromptCacheKey(string model, IReadOnlyList<ChatMessage> messages)
        {
            if (messages == null || messages.Count == 0) return null;
            var sb = new StringBuilder(model ?? string.Empty);
            for (int i = 0; i < Math.Min(2, messages.Count); i++)
            {
                sb.Append('\u001f').Append(messages[i]?.Role).Append('\u001e').Append(messages[i]?.Content);
            }
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
            var hex = new StringBuilder("agentcore-");
            for (int i = 0; i < 8; i++) hex.Append(hash[i].ToString("x2"));
            return hex.ToString();
        }

        /// <summary>
        /// 在系统前缀最后一条与最后一条 user/tool 消息上把字符串 content 改为带
        /// <c>cache_control:{type:"ephemeral"}</c> 的文本块数组（最多 2 个断点，Anthropic 上限 4）。
        /// </summary>
        internal static void ApplyCacheControlParts(JArray wireMessages)
        {
            if (wireMessages == null || wireMessages.Count == 0) return;

            int lastLeadingSystem = -1;
            for (int i = 0; i < wireMessages.Count; i++)
            {
                if ((wireMessages[i] as JObject)?.Value<string>("role") == "system") lastLeadingSystem = i;
                else break;
            }

            int lastTail = -1;
            for (int i = wireMessages.Count - 1; i > lastLeadingSystem; i--)
            {
                var role = (wireMessages[i] as JObject)?.Value<string>("role");
                if (role == "user" || role == "tool") { lastTail = i; break; }
            }

            if (lastLeadingSystem >= 0) MarkCacheControl(wireMessages[lastLeadingSystem] as JObject);
            if (lastTail >= 0) MarkCacheControl(wireMessages[lastTail] as JObject);
        }

        private static void MarkCacheControl(JObject message)
        {
            if (message == null) return;
            var content = message["content"];
            var marker = new JObject { ["type"] = "ephemeral" };

            if (content is JArray parts)
            {
                for (int i = parts.Count - 1; i >= 0; i--)
                {
                    if (parts[i] is JObject part && part.Value<string>("type") == "text")
                    {
                        part["cache_control"] = marker;
                        return;
                    }
                }
                return;
            }

            if (content == null || content.Type != JTokenType.String) return;
            var text = content.Value<string>();
            if (string.IsNullOrEmpty(text)) return;
            message["content"] = new JArray
            {
                new JObject { ["type"] = "text", ["text"] = text, ["cache_control"] = marker }
            };
        }
    }
}
