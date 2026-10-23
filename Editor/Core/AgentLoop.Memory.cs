using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Cloud;
using AgentCore.Editor.LLM;
using UnityEngine;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Core
{
    public partial class AgentLoop
    {
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
                AgentCoreLog.Warning($"[AgentCore] Memory recall timed out after {MemoryRecallTimeoutSeconds}s.");
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

            // v1.17.0: 只注入历史中尚未出现过的记忆条目（记忆消息改为只追加不删除，避免重复堆积）
            var alreadyInjected = CollectInjectedMemoryLines();

            var sb = new StringBuilder();
            sb.AppendLine(MemoryMessagePrefix);

            int totalChars = sb.Length;
            int addedCount = 0;

            foreach (var memory in memories)
            {
                if (string.IsNullOrWhiteSpace(memory.Content))
                    continue;

                var line = $"- {memory.Content.Trim()}";
                if (alreadyInjected.Contains(line))
                    continue;

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
        /// 将格式化的记忆文本作为系统消息<b>追加</b>到 <see cref="_messages"/> 末尾（当前用户消息之后）。
        /// <para>
        /// v1.17.0: 旧实现插入到系统提示词之后、并在每轮先删除旧记忆消息——这会改写历史前缀的第 2 条消息，
        /// 使其后整段对话的 prompt 缓存每轮全部失效（Claude 显式缓存与 vLLM 自动前缀缓存皆然）。
        /// 现改为只追加：历史前缀保持字节稳定，新召回的记忆以增量形式出现在本轮。
        /// </para>
        /// </summary>
        /// <param name="memoryContext">格式化的记忆上下文文本</param>
        private void InjectMemoryContext(string memoryContext)
        {
            if (string.IsNullOrEmpty(memoryContext))
                return;

            _messages.Add(new ChatMessage { Role = "system", Content = memoryContext, Kind = MessageKind.OperatorNote });
        }

        /// <summary>收集历史中已注入过的记忆行（"- xxx" 形式）。</summary>
        private HashSet<string> CollectInjectedMemoryLines()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in _messages)
            {
                if (m.Role != "system" || m.Content == null || !m.Content.StartsWith(MemoryMessagePrefix)) continue;
                foreach (var raw in m.Content.Split('\n'))
                {
                    var line = raw.TrimEnd('\r');
                    if (line.StartsWith("- ")) set.Add(line);
                }
            }
            return set;
        }
    }
}
