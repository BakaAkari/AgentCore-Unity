using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Config;
using AgentCore.Editor.LLM;
using AgentCore.Editor.Utils;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AgentCore.Editor.Core.Compression
{
    /// <summary>
    /// 压缩专用 LLM 客户端工厂 — v1.6.5+ 统一管道架构。
    /// <para>
    /// v1.6.5 之前：创建独立的 CompressionLLMClient，绕过 RequestEnrichment，
    /// 硬编码 maxTokens=512，不处理 reasoning 适配 → GLM-5.2 压缩失败。
    ///
    /// v1.6.5+：直接返回 OpenAICompatibleClient 实例，复用统一管道：
    /// - RequestEnrichment.BuildEnrichedJson（reasoning 注入）
    /// - GetEffectiveMaxTokens()（reasoning + content 预算分离）
    /// - ApplyAdaptiveDefaults()（自适应参数）
    /// </para>
    /// <para>
    /// 压缩任务的特殊性通过 ChatCompletionRequest 参数控制：
    /// - 低温度（0.1）由调用方在 request 中设置
    /// - 小 max_tokens 由调用方传 CompressionMaxTokens
    /// - 不传 tools（压缩不需要工具）
    /// </para>
    /// </summary>
    public static class CompressionLLMClientFactory
    {
        /// <summary>压缩请求的默认最大输出 token 数（content 部分）</summary>
        public const int CompressionMaxTokens = 512;

        /// <summary>压缩请求的默认温度（低温度确保稳定输出）</summary>
        public const float CompressionTemperature = 0.1f;

        /// <summary>
        /// 创建压缩专用的 LLM 客户端。
        /// v1.6.5+: 始终返回 OpenAICompatibleClient，复用统一管道。
        /// </summary>
        /// <returns>OpenAICompatibleClient 实例（null = 主客户端不可用）</returns>
        public static ILLMClient CreateCompressionClient()
        {
            // v1.6.5+: 压缩器直接使用主 OpenAICompatibleClient
            // 所有 reasoning 适配、adaptive defaults、effective max tokens
            // 由 OpenAICompatibleClient + AgentCoreSettings 统一处理
            return new OpenAICompatibleClient();
        }
    }
}
