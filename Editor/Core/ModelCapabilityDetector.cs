using System;

namespace AgentCore.Editor.Core
{
    /// <summary>
    /// 模型能力探测器 — 基于 ContextWindowManager.ModelPrefixMap 同构前缀表,
    /// 探测当前 LLM 是否具备 native reasoning(extended thinking)能力。
    /// 用于 Self-Challenge 模型分级逃逸判定(ADR: self-challenge-model-tier-escape)。
    /// </summary>
    /// <remarks>
    /// 设计权衡:
    /// - 复用 ContextWindowManager 的前缀匹配模式,保持架构一致性
    /// - 能力探测表与 token 上限表分离 — 两个正交维度,避免耦合
    /// - 未知模型默认 false(保守,不逃逸,走自挑战)— 安全优先
    /// - 表需随新模型发布手动维护(同 ModelPrefixMap 的维护模式)
    /// </remarks>
    public static class ModelCapabilityDetector
    {
        /// <summary>
        /// 具备 native reasoning(extended thinking)的模型前缀表。
        /// 命中即视为高级模型,Self-Challenge 应逃逸以避免与 native thinking 重复消耗。
        /// </summary>
        /// <remarks>
        /// 命名惯例依据 OpenRouter / 各厂商官方模型 ID:
        /// - Claude: anthropic/claude-opus-*, anthropic/claude-3-opus-*, anthropic/claude-sonnet-4-*
        /// - OpenAI: openai/o1-*, openai/o3-*, openai/o4-*, openai/gpt-5*
        /// - DeepSeek: deepseek/deepseek-r*
        /// - Gemini: google/gemini-2.5-*
        /// - Z.ai: z-ai/glm-5*(GLM-5 系列为 large-scale reasoning model, OpenRouter supported_parameters 含 reasoning/reasoning_effort/include_reasoning)
        /// 注意: o1-mini 不具备完整 reasoning,但 prefix "o1-" 会命中 — 已知偏差,
        /// 用户可通过 selfChallengeEscapeEnabled=false 手动覆盖。
        /// </remarks>
        private static readonly string[] NativeReasoningPrefixes =
        {
            "claude-opus",       // Claude Opus 全系(含 4.x)
            "claude-3-opus",     // Claude 3 Opus
            "claude-sonnet-4",   // Claude Sonnet 4+(具备 extended thinking)
            "o1-",               // OpenAI o1 系列
            "o3-",               // OpenAI o3 系列
            "o4-",               // OpenAI o4 系列
            "gpt-5",             // GPT-5
            "deepseek-r",        // DeepSeek R 系列(推理模型)
            "gemini-2.5",        // Gemini 2.5 Pro(具备 thinking)
            "glm-5",             // GLM-5 系列(Z.ai reasoning model, 含 5.2 量化变体如 W4AFP8)
        };

        /// <summary>
        /// 判定模型是否具备 native reasoning 能力。
        /// 匹配规则:模型名以表中任一前缀开头(OrdinalIgnoreCase)即视为具备。
        /// </summary>
        /// <param name="modelName">模型标识(来自 ActiveModelConfig.ModelName)</param>
        /// <returns>true=具备 native reasoning,Self-Challenge 应逃逸;false=未知或不具备,走自挑战</returns>
        public static bool HasNativeReasoning(string modelName)
        {
            if (string.IsNullOrEmpty(modelName)) return false;
            foreach (var prefix in NativeReasoningPrefixes)
            {
                if (modelName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
