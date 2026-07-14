using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Cloud;
using AgentCore.Editor.LLM;
using UnityEngine;

namespace AgentCore.Editor.Core
{
    public partial class AgentLoop
    {
        /// <summary>
        /// 移除之前注入的记忆消息，避免记忆消息在对话历史中累积。
        /// 通过消息内容前缀 <see cref="MemoryMessagePrefix"/> 来识别记忆消息。
        /// </summary>
        private void RemoveOldMemoryMessages()
        {
            for (int i = _messages.Count - 1; i >= 0; i--)
            {
                if (_messages[i].Role == "system" &&
                    _messages[i].Content != null &&
                    _messages[i].Content.StartsWith(MemoryMessagePrefix))
                {
                    _messages.RemoveAt(i);
                    AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] Removed old memory injection message.");
                }
            }
        }

        /// <summary>
        /// 搜索与用户消息相关的记忆。
        /// 使用 <see cref="Mem0Client.SearchMemoryAsync"/> 进行语义搜索。
        /// <para>
        /// 关键约束：
        /// <list type="bullet">
        ///   <item>搜索查询截断到 <see cref="MemoryRecallMaxQueryLength"/> 字符</item>
        ///   <item>限制返回 <see cref="MemoryRecallMaxResults"/> 条结果</item>
        ///   <item>设置 <see cref="MemoryRecallTimeoutSeconds"/> 秒超时，避免影响响应速度</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <param name="userMessage">用户消息文本</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>匹配的记忆列表，失败时返回空列表</returns>
        private async Task<List<Mem0Memory>> SearchRelevantMemories(string userMessage, CancellationToken ct)
        {
            // 截断查询到合理长度
            var query = userMessage.Length > MemoryRecallMaxQueryLength
                ? userMessage.Substring(0, MemoryRecallMaxQueryLength)
                : userMessage;

            // 创建带超时的取消令牌
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(MemoryRecallTimeoutSeconds));

            try
            {
                var client = Mem0Client.FromSettings();
                var memories = await client.SearchMemoryAsync(
                    query: query,
                    limit: MemoryRecallMaxResults,
                    ct: timeoutCts.Token
                );
                return memories;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 仅超时导致的取消，不是用户主动取消
                Debug.LogWarning($"[AgentCore] Memory recall timed out after {MemoryRecallTimeoutSeconds}s.");
                return new List<Mem0Memory>();
            }
        }

        /// <summary>
        /// 将搜索到的记忆格式化为系统消息文本。
        /// 限制总字符数不超过 <see cref="MemoryContextMaxChars"/>（约 1000 token）。
        /// </summary>
        /// <param name="memories">记忆列表</param>
        /// <returns>格式化的记忆上下文文本，无有效内容时返回 null</returns>
        private string FormatMemoriesAsContext(List<Mem0Memory> memories)
        {
            if (memories == null || memories.Count == 0)
                return null;

            var sb = new StringBuilder();
            sb.AppendLine(MemoryMessagePrefix);

            int totalChars = sb.Length;
            int addedCount = 0;

            foreach (var memory in memories)
            {
                if (string.IsNullOrWhiteSpace(memory.Content))
                    continue;

                var line = $"- {memory.Content.Trim()}";

                // 检查是否超过最大字符限制
                if (totalChars + line.Length + 2 > MemoryContextMaxChars) // +2 for newline
                    break;

                sb.AppendLine(line);
                totalChars += line.Length + 2;
                addedCount++;
            }

            if (addedCount == 0)
                return null;

            sb.AppendLine("[请参考以上记忆辅助回答，但以当前对话上下文为准]");
            return sb.ToString();
        }

        /// <summary>
        /// 将格式化的记忆文本作为系统消息注入到 <see cref="_messages"/> 列表中。
        /// 位置：在系统提示词（index 0）之后、第一条用户消息之前。
        /// </summary>
        /// <param name="memoryContext">格式化的记忆上下文文本</param>
        private void InjectMemoryContext(string memoryContext)
        {
            if (string.IsNullOrEmpty(memoryContext))
                return;

            // 找到第一条非 system 消息的位置（即系统提示词之后）
            int insertIndex = 1; // 默认在 system prompt 之后
            for (int i = 0; i < _messages.Count; i++)
            {
                if (_messages[i].Role != "system")
                {
                    insertIndex = i;
                    break;
                }
                insertIndex = i + 1;
            }

            _messages.Insert(insertIndex, ChatMessage.System(memoryContext));
        }
    }
}
