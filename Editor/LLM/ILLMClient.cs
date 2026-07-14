using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCore.Editor.LLM
{
    /// <summary>
    /// LLM 客户端接口。
    /// 支持非流式和流式两种调用模式。
    /// v1.6.5+: 所有调用统一经过 RequestEnrichment + GetEffectiveMaxTokens，
    /// 确保 reasoning 模型适配在每条 LLM 调用路径上生效。
    /// </summary>
    public interface ILLMClient
    {
        /// <summary>
        /// 非流式 Chat Completion 调用。
        /// 等待完整响应后一次性返回。
        /// </summary>
        /// <param name="messages">对话消息列表</param>
        /// <param name="tools">工具定义列表（可选）</param>
        /// <param name="ct">取消令牌</param>
        /// <param name="contentMaxTokens">content 部分的 token 预算覆盖（可选）。
        /// 传入时使用 GetEffectiveMaxTokens(contentMaxTokens) 计算实际 max_tokens，
        /// 适用于压缩等需要较小 content 输出的场景。</param>
        /// <returns>完整的 Chat Completion 响应</returns>
        Task<ChatCompletionResponse> ChatCompletionAsync(
            List<ChatMessage> messages,
            List<ToolDefinition> tools = null,
            CancellationToken ct = default,
            int? contentMaxTokens = null);

        /// <summary>
        /// 流式 Chat Completion 调用。
        /// 通过回调逐 chunk 推送结果。
        /// </summary>
        /// <param name="messages">对话消息列表</param>
        /// <param name="onChunk">每个 chunk 的回调</param>
        /// <param name="tools">工具定义列表（可选）</param>
        /// <param name="ct">取消令牌</param>
        /// <param name="contentMaxTokens">content 部分的 token 预算覆盖（可选）</param>
        /// <returns>完成后返回拼接好的完整 assistant 消息</returns>
        Task<ChatMessage> ChatCompletionStreamAsync(
            List<ChatMessage> messages,
            Action<StreamChunk> onChunk,
            List<ToolDefinition> tools = null,
            CancellationToken ct = default,
            int? contentMaxTokens = null);
    }
}
