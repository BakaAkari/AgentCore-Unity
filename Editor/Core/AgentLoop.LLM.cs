using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Config;
using AgentCore.Editor.LLM;
using AgentCore.Editor.Tools;
using UnityEngine;

namespace AgentCore.Editor.Core
{
    public partial class AgentLoop
    {
        /// <summary>
        /// 调用 LLM 流式接口并返回完整的 assistant 消息。
        /// <para>
        /// 从 Phase 1 的内联逻辑提取为独立方法，支持工具定义参数。
        /// 处理流式回调中的 ContentToken、ToolCallDelta、Done 和 Error 事件。
        /// </para>
        /// </summary>
        /// <param name="assistantTurn">当前助手对话轮次（用于流式内容追加）</param>
        /// <param name="tools">工具定义列表（可为 null 或空列表表示不使用工具）</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>完整的 assistant ChatMessage（可能包含 tool_calls）</returns>
        private async Task<ChatMessage> CallLLMStreamAsync(
            ConversationTurn assistantTurn,
            List<ToolDefinition> tools,
            CancellationToken ct)
        {
            // Phase 3: 上下文窗口截断
            // 创建 _messages 的浅拷贝，对拷贝进行截断，不修改原始列表（保留完整历史用于 UI 显示）
            var settings = AgentCoreSettings.instance;
            int maxTokens = settings.maxContextTokens > 0
                ? settings.maxContextTokens
                : ContextWindowManager.GetModelMaxTokens(settings.llmModel);
            int reserveTokens = settings.reserveResponseTokens;

            var messagesSnapshot = ContextWindowManager.TrimToFit(
                _messages, maxTokens, reserveTokens);

            // 切换到 Streaming 状态
            SetState(AgentState.Streaming);

            // 传递有效的工具列表（空列表时传 null，避免 API 报错）
            var effectiveTools = (tools != null && tools.Count > 0) ? tools : null;

            // Phase 2 Step 11: 使用 FallbackRouter 包装 LLM 调用，支持自动重试
            var assistantMessage = await _fallbackRouter.ExecuteStreamWithRetryAsync(
                _llmClient,
                messagesSnapshot,
                chunk => OnStreamChunkReceived(chunk, assistantTurn, ct),
                tools: effectiveTools,
                ct: ct,
                onStatusUpdate: status => EmitEvent(AgentEvent.ErrorEvent($"[Retry] {status}"))
            );

            return assistantMessage;
        }

        /// <summary>
        /// 处理 LLM 流式回调中的单个 chunk。
        /// 此方法可能在后台线程被调用，通过 <see cref="EmitEvent"/> 确保事件在主线程派发。
        /// </summary>
        /// <param name="chunk">流式 chunk 数据</param>
        /// <param name="assistantTurn">当前助手对话轮次</param>
        /// <param name="ct">取消令牌</param>
        private void OnStreamChunkReceived(StreamChunk chunk, ConversationTurn assistantTurn, CancellationToken ct)
        {
            // 检查取消
            if (ct.IsCancellationRequested)
            {
                return;
            }

            switch (chunk.Type)
            {
                case StreamChunkType.ContentToken:
                    // 追加 token 到助手轮次内容
                    if (!string.IsNullOrEmpty(chunk.Content))
                    {
                        assistantTurn.Content += chunk.Content;
                        EmitEvent(AgentEvent.StreamToken(chunk.Content, assistantTurn.Id));
                    }
                    break;

                case StreamChunkType.Done:
                    // 流式完成，由 SendMessageAsync 的后续逻辑处理
                    Debug.Log($"[AgentCore] Stream completed. Finish reason: {chunk.FinishReason}");
                    break;

                case StreamChunkType.Error:
                    // 流式过程中的解析错误
                    Debug.LogError($"[AgentCore] Stream error: {chunk.Error}");
                    EmitEvent(AgentEvent.ErrorEvent(chunk.Error));
                    break;

                case StreamChunkType.ToolCallDelta:
                    // Phase 2：ToolCallDelta 由 OpenAICompatibleClient 内部累积，
                    // 最终通过 Done 事件返回完整的 tool_calls 列表。
                    // 此处仅记录日志用于调试。
                    Debug.Log($"[AgentCore] Received ToolCallDelta: {chunk.ToolCallDelta?.Function?.Name ?? "(accumulating)"}");
                    break;
            }
        }
    }
}
