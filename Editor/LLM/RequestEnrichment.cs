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
        /// 将 ChatCompletionRequest 序列化为 JSON，并根据 ActiveModelConfig 注入额外字段。
        /// 注入顺序：stream_options → reasoning → extraRequestBody → 清除 null 值。
        /// <para>
        /// 注意：此方法只负责"按 ActiveModelConfig 意图注入"，不做任何供应商判断——
        /// 是否支持 reasoning 等字段完全由 <see cref="RequestPruningRegistry"/> 的
        /// error-driven learning 路径判定（首次请求踩雷 → 学习 → 之后自动 strip）。
        /// </para>
        /// </summary>
        /// <param name="request">强类型请求对象</param>
        /// <param name="reasoningLevelOverride">
        /// v1.14.10: 会话级思考强度快捷覆盖（chat 面板下拉选择，类似 Codex/Claude Code）。
        /// null/空字符串/"auto" = 不覆盖，走 <see cref="ActiveModelConfig.ReasoningEffort"/> 的
        /// 全局默认值（现状行为，本参数缺省时完全等价于修改前的行为）；其他值经
        /// <see cref="ReasoningParamMapper.ParseLevel"/> 解析后，按当前 endpoint/modelName 的
        /// 供应商特征组装成具体协议字段，覆盖全局默认。
        /// </param>
        /// <returns>增强后的 JSON 字符串，可直接作为 HTTP body 发送</returns>
        public static string BuildEnrichedJson(ChatCompletionRequest request, string reasoningLevelOverride = null)
        {
            // Step 1: 序列化为 JObject（保留 null 移除语义由 JsonHelper 控制）
            var baseJson = JsonHelper.Serialize(request);
            var body = JObject.Parse(baseJson);

            // Step 2: 注入 stream_options（仅流式请求）
            if (request.Stream)
            {
                InjectStreamOptions(body);
            }

            // Step 3: 注入 reasoning 参数。
            // v1.14.10: 会话覆盖优先于全局设置——ParseLevel 对 null/空/"auto" 统一返回 Auto，
            // Auto 时 ReasoningParamMapper.ApplyLevel 直接 no-op，退回到下面的全局默认路径，
            // 保证"没有设置覆盖"时行为与修改前完全一致。
            var overrideLevel = ReasoningParamMapper.ParseLevel(reasoningLevelOverride);
            if (overrideLevel != ReasoningParamMapper.ReasoningLevel.Auto)
            {
                ReasoningParamMapper.ApplyLevel(body, overrideLevel, ActiveModelConfig.Endpoint, ActiveModelConfig.ModelName);
            }
            else if (ActiveModelConfig.EnableReasoningOutput)
            {
                InjectReasoning(body, ActiveModelConfig.ReasoningEffort, ActiveModelConfig.ReasoningMaxTokens);
            }

            // Step 4: 深度合并用户自定义 extra body
            if (!string.IsNullOrWhiteSpace(ActiveModelConfig.ExtraRequestBody))
            {
                MergeExtraBody(body, ActiveModelConfig.ExtraRequestBody);
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

            // v1.14.10: 部分模型（实测 DeepSeek-V4-Flash 经 vLLM 0.25.1 部署）的服务端
            // reasoning parser 默认初始状态是 CONTENT，不会主动拆分 <think> 标签到独立的
            // reasoning/reasoning_content delta 字段 —— 除非请求体显式传入
            // chat_template_kwargs.thinking=true（vLLM 专属扩展字段，透传给 chat template
            // 和 reasoning parser 的 thinking 开关）。缺失时思考内容会整段混入正常 content，
            // UI 侧 ThinkingDrawer 收不到任何 reasoning token，用户看到的是"思考过程被当成
            // 正文消息发了出来"。见 vllm/parser/deepseek_v4.py:
            //   thinking = bool(chat_kwargs.get("thinking") or chat_kwargs.get("enable_thinking"))
            //              and chat_kwargs.get("reasoning_effort") != "none"
            //   initial_state = REASONING if thinking else CONTENT
            // 已用 curl 直连 vLLM 实测验证：加上此字段后 delta 从 content 变为 reasoning 字段。
            // 该字段是 vLLM 专属扩展，非 vLLM 供应商若报错会被 RequestPruningRegistry 的
            // error-driven learning 自动学习剔除（同一套现有安全网），不需要按供应商特判。
            if (body["chat_template_kwargs"] is JObject existingKwargs)
            {
                existingKwargs["thinking"] = true;
            }
            else
            {
                body["chat_template_kwargs"] = new JObject { ["thinking"] = true };
            }
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
                AgentCoreLog.Warning($"[AgentCore] RequestEnrichment: failed to parse extraRequestBody, skipping merge. Error: {ex.Message}");
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
