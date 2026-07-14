using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Config;
using AgentCore.Editor.LLM;
using UnityEngine;

namespace AgentCore.Editor.Core.Compression
{
    /// <summary>
    /// 工具结果压缩器 — 将超过阈值的工具输出压缩为简洁摘要。
    /// <para>
    /// 工作流程：
    /// <list type="number">
    ///   <item>检查工具结果的 token 数是否超过阈值</item>
    ///   <item>如果超过，调用压缩 LLM 生成摘要</item>
    ///   <item>如果压缩 LLM 失败，降级为简单截断</item>
    ///   <item>返回压缩后的内容（带 [compressed] 前缀标记）</item>
    /// </list>
    /// </para>
    /// <para>
    /// 设计原则：
    /// <list type="bullet">
    ///   <item>非侵入式：压缩失败不影响主流程</item>
    ///   <item>可观测：通过 <see cref="CompressionMetrics"/> 追踪压缩效果</item>
    ///   <item>可配置：阈值和目标 token 数通过 Settings 配置</item>
    /// </list>
    /// </para>
    /// </summary>
    public class ToolResultCompressor
    {
        private readonly ILLMClient _compressionClient;
        private readonly ILLMClient _mainClient;
        private readonly CompressionMetrics _metrics;

        /// <summary>
        /// 创建工具结果压缩器。
        /// </summary>
        /// <param name="compressionClient">压缩专用 LLM 客户端（可为 null，表示使用主 LLM）</param>
        /// <param name="mainClient">主 LLM 客户端（作为 fallback）</param>
        /// <param name="metrics">压缩统计指标</param>
        public ToolResultCompressor(ILLMClient compressionClient, ILLMClient mainClient, CompressionMetrics metrics)
        {
            _compressionClient = compressionClient;
            _mainClient = mainClient ?? throw new ArgumentNullException(nameof(mainClient));
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        }

        /// <summary>
        /// 压缩工具结果内容（如果超过阈值）。
        /// <para>
        /// 压缩策略：
        /// <list type="number">
        ///   <item>估算 token 数，未超阈值则直接返回原始内容</item>
        ///   <item>超过阈值时，调用压缩 LLM 生成摘要</item>
        ///   <item>LLM 调用失败时，降级为简单截断（保留前 N 字符 + 尾部提示）</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <param name="toolName">工具名称（用于压缩 prompt）</param>
        /// <param name="content">原始工具结果内容</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>压缩后的内容（可能与原始内容相同，如果未超阈值）</returns>
        public async Task<string> CompressIfNeededAsync(string toolName, string content, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(content))
            {
                return content;
            }

            var settings = AgentCoreSettings.instance;

            // 检查是否启用压缩
            if (!settings.compressionEnabled)
            {
                return content;
            }

            // 估算 token 数
            int tokenCount = TokenCounter.EstimateTokens(content);

            // 未超阈值，直接返回
            if (tokenCount <= settings.toolResultCompressionThreshold)
            {
                _metrics.RecordToolResultCompressionSkipped();
                return content;
            }

            AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] Tool result from '{toolName}' exceeds threshold " +
                      $"({tokenCount} > {settings.toolResultCompressionThreshold} tokens), compressing...");

            // 尝试 LLM 压缩
            try
            {
                string compressed = await CompressWithLLMAsync(toolName, content, settings.toolResultTargetTokens, ct);

                if (!string.IsNullOrEmpty(compressed))
                {
                    int compressedTokens = TokenCounter.EstimateTokens(compressed);
                    _metrics.RecordToolResultCompression(tokenCount, compressedTokens);

                    AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] Tool result compressed: {tokenCount} → {compressedTokens} tokens " +
                              $"(saved {tokenCount - compressedTokens}, ratio: {(float)compressedTokens / tokenCount:P0})");

                    return CompressionPrompts.CompressedToolResultPrefix + compressed;
                }
            }
            catch (OperationCanceledException)
            {
                throw; // 不吞没取消异常
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentCore] LLM compression failed for '{toolName}': {ex.Message}. Falling back to truncation.");
                _metrics.RecordToolResultCompressionFailure();
            }

            // 降级：简单截断
            return FallbackTruncate(content, settings.toolResultTargetTokens);
        }

        /// <summary>
        /// 使用 LLM 压缩工具结果。
        /// </summary>
        /// <param name="toolName">工具名称</param>
        /// <param name="content">原始内容</param>
        /// <param name="targetTokens">目标 token 数</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>压缩后的文本，失败时返回 null</returns>
        private async Task<string> CompressWithLLMAsync(string toolName, string content, int targetTokens, CancellationToken ct)
        {
            // 选择客户端：优先使用压缩专用客户端
            var client = _compressionClient ?? _mainClient;

            // 构建压缩请求消息
            var messages = new List<ChatMessage>
            {
                ChatMessage.System(CompressionPrompts.ToolResultCompressionSystem),
                ChatMessage.User(string.Format(
                    CompressionPrompts.ToolResultCompressionUser,
                    toolName,
                    targetTokens,
                    content))
            };

            // 调用 LLM（非流式）
            var response = await client.ChatCompletionAsync(messages, null, ct);

            if (response?.Choices != null && response.Choices.Count > 0)
            {
                var compressed = response.Choices[0].Message?.Content;
                if (!string.IsNullOrWhiteSpace(compressed))
                {
                    return compressed.Trim();
                }
            }

            return null;
        }

        /// <summary>
        /// 降级截断策略 — 当 LLM 压缩失败时使用。
        /// <para>
        /// 保留内容的前 N 个字符（约等于目标 token 数 × 4），
        /// 并在末尾添加截断提示。
        /// </para>
        /// </summary>
        /// <param name="content">原始内容</param>
        /// <param name="targetTokens">目标 token 数</param>
        /// <returns>截断后的内容</returns>
        private static string FallbackTruncate(string content, int targetTokens)
        {
            // 粗略估算：1 token ≈ 4 字符（英文），保守取 3
            int maxChars = targetTokens * 3;

            if (content.Length <= maxChars)
            {
                return content;
            }

            // 保留前 80% 和后 20% 的配额
            int headChars = (int)(maxChars * 0.8);
            int tailChars = (int)(maxChars * 0.15);

            string head = content.Substring(0, headChars);
            string tail = content.Substring(content.Length - tailChars);

            int omittedChars = content.Length - headChars - tailChars;
            int omittedTokensEstimate = TokenCounter.EstimateTokens(
                content.Substring(headChars, content.Length - headChars - tailChars));

            return $"{CompressionPrompts.CompressedToolResultPrefix}" +
                   $"{head}\n\n... [{omittedChars} chars / ~{omittedTokensEstimate} tokens omitted] ...\n\n{tail}";
        }
    }
}
