using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Config;
using AgentCore.Editor.LLM;
using AgentCore.Editor.Skills;
using UnityEngine;

namespace AgentCore.Editor.Core.Compression
{
    /// <summary>
    /// 对话历史压缩器 — 当上下文接近窗口限制时，将旧对话段落摘要化。
    /// <para>
    /// 工作流程：
    /// <list type="number">
    ///   <item>计算当前对话的 token 使用率</item>
    ///   <item>如果使用率超过触发阈值（默认 70%），启动压缩</item>
    ///   <item>选择最旧的 N 条消息（保留 system prompt 和最近 K 轮不压缩）</item>
    ///   <item>调用压缩 LLM 生成摘要</item>
    ///   <item>用摘要消息替换原始消息段</item>
    ///   <item>如果压缩失败，不修改消息列表（由 ContextWindowManager.TrimToFit 兜底）</item>
    /// </list>
    /// </para>
    /// <para>
    /// 关键设计：
    /// <list type="bullet">
    ///   <item>只压缩已完成的对话轮次（不压缩正在进行的 tool_call 对）</item>
    ///   <item>保留最近 N 轮完整对话不压缩（确保 LLM 有足够近期上下文）</item>
    ///   <item>压缩后的摘要作为 system 消息插入（紧跟在原始 system prompt 之后）</item>
    ///   <item>支持增量压缩：已有摘要时，将新的旧消息合并到现有摘要中</item>
    /// </list>
    /// </para>
    /// </summary>
    public class ConversationCompressor
    {
        private readonly ILLMClient _compressionClient;
        private readonly ILLMClient _mainClient;
        private readonly CompressionMetrics _metrics;

        /// <summary>最少保留的最近消息数（不压缩）— 确保 LLM 有足够近期上下文</summary>
        private const int MinRecentMessagesToKeep = 10;

        /// <summary>压缩摘要消息的标识前缀（用于识别已有摘要）</summary>
        private const string SummaryMessageMarker = "[conversation summary]";

        /// <summary>
        /// 创建对话历史压缩器。
        /// </summary>
        /// <param name="compressionClient">压缩专用 LLM 客户端（可为 null，表示使用主 LLM）</param>
        /// <param name="mainClient">主 LLM 客户端（作为 fallback）</param>
        /// <param name="metrics">压缩统计指标</param>
        public ConversationCompressor(ILLMClient compressionClient, ILLMClient mainClient, CompressionMetrics metrics)
        {
            _compressionClient = compressionClient;
            _mainClient = mainClient ?? throw new ArgumentNullException(nameof(mainClient));
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        }

        /// <summary>
        /// 检查并执行对话压缩（如果需要）。
        /// <para>
        /// 此方法直接修改传入的消息列表（in-place）。
        /// 如果不需要压缩或压缩失败，消息列表保持不变。
        /// </para>
        /// </summary>
        /// <param name="messages">LLM 消息历史（会被直接修改）</param>
        /// <param name="maxContextTokens">模型的最大上下文 token 数</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>是否执行了压缩</returns>
        public async Task<bool> CompressIfNeededAsync(List<ChatMessage> messages, int maxContextTokens, CancellationToken ct)
        {
            if (messages == null || messages.Count == 0)
            {
                return false;
            }

            var settings = AgentCoreSettings.instance;

            // 检查是否启用压缩
            if (!settings.compressionEnabled)
            {
                return false;
            }

            // 计算当前 token 使用率
            int currentTokens = TokenCounter.EstimateConversationTokens(messages);
            float usageRatio = (float)currentTokens / maxContextTokens;

            // 未达到触发阈值
            if (usageRatio < settings.conversationCompressionTrigger)
            {
                return false;
            }

            AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] Context usage {usageRatio:P0} exceeds trigger threshold " +
                      $"{settings.conversationCompressionTrigger:P0} ({currentTokens}/{maxContextTokens} tokens). " +
                      $"Starting conversation compression...");

            // 确定可压缩的消息范围
            var compressRange = DetermineCompressRange(messages);
            if (compressRange.count <= 0)
            {
                AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] No messages eligible for compression (all are recent or protected).");
                return false;
            }

            // 尝试 LLM 压缩
            try
            {
                string summary = await CompressMessagesWithLLMAsync(
                    messages, compressRange.startIndex, compressRange.count, ct);

                if (!string.IsNullOrEmpty(summary))
                {
                    // 计算压缩前后的 token 数
                    int originalTokens = 0;
                    for (int i = compressRange.startIndex; i < compressRange.startIndex + compressRange.count; i++)
                    {
                        originalTokens += TokenCounter.EstimateMessageTokens(messages[i]);
                    }
                    int summaryTokens = TokenCounter.EstimateTokens(summary);

                    // 替换消息：移除旧消息，插入摘要
                    ApplyCompression(messages, compressRange.startIndex, compressRange.count, summary);

                    _metrics.RecordConversationCompression(originalTokens, summaryTokens, compressRange.count);

                    int newTotalTokens = TokenCounter.EstimateConversationTokens(messages);
                    AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] Conversation compressed: removed {compressRange.count} messages, " +
                              $"{originalTokens} → {summaryTokens} tokens (summary). " +
                              $"Total context: {currentTokens} → {newTotalTokens} tokens.");

                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                throw; // 不吞没取消异常
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentCore] Conversation compression failed: {ex.Message}. " +
                                 "ContextWindowManager.TrimToFit will handle overflow.");
                _metrics.RecordConversationCompressionFailure();
            }

            return false;
        }

        /// <summary>
        /// 确定可压缩的消息范围。
        /// <para>
        /// 规则：
        /// <list type="bullet">
        ///   <item>跳过第一条 system 消息（永远保留）</item>
        ///   <item>跳过已有的摘要消息（避免重复压缩）</item>
        ///   <item>保留最近 MinRecentMessagesToKeep 条消息不压缩</item>
        ///   <item>确保不在 tool_call 对中间截断</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <param name="messages">消息列表</param>
        /// <returns>可压缩范围的起始索引和数量</returns>
        private (int startIndex, int count) DetermineCompressRange(List<ChatMessage> messages)
        {
            // 找到压缩起始位置（跳过 system prompt 和已有摘要）
            int startIndex = 0;

            // 跳过第一条 system 消息
            if (messages.Count > 0 && messages[0].Role == "system")
            {
                startIndex = 1;
            }

            // v1.4.0 fix: 跳过所有紧跟主 system prompt 之后的
            //   (a) 已有摘要消息（避免重复压缩）
            //   (b) Workspace snapshot 首轮注入的 system 消息（首轮上下文，跨轮次保留）
            //   (c) Deferred context（# Available Tools 开头）
            //   (d) ADR-18 Skill 内容（Skills.SkillContentBuilder.Marker 开头）
            // 这四类都是"运行时静态上下文"，被压缩会破坏 LLM 对 workspace/tools/skills 状态的感知。
            while (startIndex < messages.Count &&
                   messages[startIndex].Role == "system" &&
                   messages[startIndex].Content != null &&
                   (messages[startIndex].Content.StartsWith(SummaryMessageMarker)
                    || messages[startIndex].Content.StartsWith(WorkspaceSnapshotBuilder.SnapshotMarker)
                    // Deferred context（Active Tools List + PROJECT + Workspace PROJECT.md）
                    // 由 BootstrapContext.CompileDeferredContext 生成，首轮注入后应跨轮次保留。
                    // 未来若需要精确标记可加专用前缀；此处以启发式识别 "# Available Tools" 开头。
                    || messages[startIndex].Content.StartsWith("# Available Tools")
                    // ADR-18 Skill 内容：由 LoadSkillTool 加载后每轮同步到 _messages。
                    // 必须跨轮次保留直到 unload，否则会破坏 LLM 对已加载 skill 的感知。
                    || messages[startIndex].Content.StartsWith(SkillContentBuilder.Marker)))
            {
                startIndex++;
            }

            // 计算可压缩的结束位置（保留最近 N 条消息）
            int endIndex = messages.Count - MinRecentMessagesToKeep;

            // 确保结束位置不在 tool_call 对中间
            endIndex = AdjustEndIndexForToolCalls(messages, endIndex);

            // 计算可压缩的消息数量
            int count = endIndex - startIndex;

            if (count <= 2) // 至少要有 3 条消息才值得压缩
            {
                return (startIndex, 0);
            }

            return (startIndex, count);
        }

        /// <summary>
        /// 调整结束索引，确保不在 tool_call 对中间截断。
        /// 如果结束位置落在 tool response 消息上，向前移动到 assistant 消息之前。
        /// </summary>
        private static int AdjustEndIndexForToolCalls(List<ChatMessage> messages, int endIndex)
        {
            if (endIndex <= 0 || endIndex >= messages.Count)
            {
                return Math.Max(0, Math.Min(endIndex, messages.Count));
            }

            // 如果结束位置是 tool 消息，向前移动到对应的 assistant 消息之前
            while (endIndex > 0 && messages[endIndex - 1].Role == "tool")
            {
                endIndex--;
            }

            // 如果结束位置是包含 tool_calls 的 assistant 消息，也要排除它
            if (endIndex > 0 && endIndex <= messages.Count)
            {
                var msg = messages[endIndex - 1];
                if (msg.Role == "assistant" && msg.ToolCalls != null && msg.ToolCalls.Count > 0)
                {
                    endIndex--;
                }
            }

            return Math.Max(0, endIndex);
        }

        /// <summary>
        /// 使用 LLM 压缩消息段落。
        /// </summary>
        /// <param name="messages">完整消息列表</param>
        /// <param name="startIndex">压缩起始索引</param>
        /// <param name="count">要压缩的消息数量</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>压缩后的摘要文本，失败时返回 null</returns>
        private async Task<string> CompressMessagesWithLLMAsync(
            List<ChatMessage> messages, int startIndex, int count, CancellationToken ct)
        {
            // 构建要压缩的对话文本
            var conversationText = new StringBuilder();
            for (int i = startIndex; i < startIndex + count && i < messages.Count; i++)
            {
                var msg = messages[i];
                string roleLabel = msg.Role switch
                {
                    "user" => "User",
                    "assistant" => "Assistant",
                    "tool" => $"Tool[{msg.ToolCallId ?? "?"}]",
                    "system" => "System",
                    _ => msg.Role
                };

                conversationText.AppendLine($"[{roleLabel}]: {msg.Content ?? "(tool_calls)"}");

                // 如果 assistant 消息有 tool_calls，记录工具调用信息
                if (msg.ToolCalls != null)
                {
                    foreach (var tc in msg.ToolCalls)
                    {
                        conversationText.AppendLine($"  → Called: {tc.Function?.Name}({tc.Function?.Arguments ?? ""})");
                    }
                }
            }

            // 计算目标 token 数（原始的 20-30%）
            int originalTokens = TokenCounter.EstimateTokens(conversationText.ToString());
            int targetTokens = Math.Max(100, originalTokens / 4); // 压缩到 25%

            // 选择客户端
            var client = _compressionClient ?? _mainClient;

            // 构建压缩请求
            var compressionMessages = new List<ChatMessage>
            {
                ChatMessage.System(CompressionPrompts.ConversationCompressionSystem),
                ChatMessage.User(string.Format(
                    CompressionPrompts.ConversationCompressionUser,
                    targetTokens,
                    conversationText.ToString()))
            };

            // 调用 LLM
            var response = await client.ChatCompletionAsync(compressionMessages, null, ct);

            if (response?.Choices != null && response.Choices.Count > 0)
            {
                var summary = response.Choices[0].Message?.Content;
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    return summary.Trim();
                }
            }

            return null;
        }

        /// <summary>
        /// 应用压缩结果：移除旧消息，插入摘要消息。
        /// </summary>
        /// <param name="messages">消息列表（会被直接修改）</param>
        /// <param name="startIndex">要移除的起始索引</param>
        /// <param name="count">要移除的消息数量</param>
        /// <param name="summary">压缩摘要文本</param>
        private static void ApplyCompression(List<ChatMessage> messages, int startIndex, int count, string summary)
        {
            // 移除旧消息
            messages.RemoveRange(startIndex, count);

            // 在同一位置插入摘要消息（作为 system 消息）
            var summaryMessage = ChatMessage.System(
                $"{SummaryMessageMarker}\n{summary}");

            messages.Insert(startIndex, summaryMessage);
        }
    }
}
