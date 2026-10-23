using System.Collections.Generic;
using AgentCore.Editor.LLM;

namespace AgentCore.Editor.Core
{
    /// <summary>
    /// 请求前的消息历史修复（纯函数，不修改入参）。
    /// <para>
    /// OpenAI / Anthropic 兼容接口都要求：带 tool_calls 的 assistant 消息后面紧跟每个 tool_call_id 各一条
    /// tool 结果，且不能出现没有对应调用的 tool 结果，否则直接 400。取消、域重载、截断（TrimToFit）、
    /// 被取代的运行丢弃结果，都会留下不完整的配对。这里统一修复：
    /// </para>
    /// <list type="bullet">
    ///   <item>缺结果的 tool_call：若后面别处有同 id 的结果则挪回紧邻位置，否则补一条"已中断"合成结果</item>
    ///   <item>结果按 tool_calls 顺序排列，同一 id 只保留第一条</item>
    ///   <item>没有对应调用的孤立 tool 结果直接丢弃</item>
    /// </list>
    /// 参考 Codex 的 <c>ensure_call_outputs_present</c> / <c>remove_orphan_outputs</c> 与 Claude Code 的
    /// 中断结果补齐。每次发请求前对副本调用；取消收尾时对正式历史调用一次，避免存盘的会话本身就是坏的。
    /// </summary>
    public static class HistorySanitizer
    {
        /// <summary>合成结果的内容。明确告诉模型工具可能已部分执行，避免它盲目重试。</summary>
        public const string InterruptedToolResult =
            "[Tool result unavailable] The call was interrupted before its result was recorded " +
            "(user cancellation, domain reload or session restore). The tool may have partially executed; " +
            "verify the current state before retrying.";

        public struct Report
        {
            public int SyntheticResults;
            public int MovedResults;
            public int DroppedOrphans;
            public int DroppedDuplicates;

            public bool Changed => SyntheticResults + MovedResults + DroppedOrphans + DroppedDuplicates > 0;

            public override string ToString()
            {
                return $"synthetic={SyntheticResults}, moved={MovedResults}, orphans={DroppedOrphans}, duplicates={DroppedDuplicates}";
            }
        }

        public static List<ChatMessage> Normalize(IReadOnlyList<ChatMessage> messages)
        {
            return Normalize(messages, out _);
        }

        public static List<ChatMessage> Normalize(IReadOnlyList<ChatMessage> messages, out Report report)
        {
            report = default;
            var result = new List<ChatMessage>(messages?.Count ?? 0);
            if (messages == null)
                return result;

            // 已被某个 assistant 认领（原位或挪动）的 tool 消息下标
            var consumed = new HashSet<int>();

            for (int i = 0; i < messages.Count; i++)
            {
                var msg = messages[i];
                if (msg == null)
                    continue;

                if (IsTool(msg))
                {
                    if (!consumed.Contains(i))
                        report.DroppedOrphans++;
                    continue;
                }

                result.Add(msg);

                if (!IsAssistantWithToolCalls(msg))
                    continue;

                // 紧随其后的连续 tool 结果（按 id 取第一条）
                var adjacent = new Dictionary<string, int>();
                int j = i + 1;
                for (; j < messages.Count && messages[j] != null && IsTool(messages[j]); j++)
                {
                    var id = messages[j].ToolCallId;
                    if (string.IsNullOrEmpty(id))
                        continue;
                    if (adjacent.ContainsKey(id))
                        continue;
                    adjacent[id] = j;
                }

                var emitted = new HashSet<string>();
                foreach (var call in msg.ToolCalls)
                {
                    var id = call?.Id;
                    if (string.IsNullOrEmpty(id) || !emitted.Add(id))
                        continue;

                    if (adjacent.TryGetValue(id, out var at))
                    {
                        consumed.Add(at);
                        result.Add(messages[at]);
                        continue;
                    }

                    int displaced = FindDisplacedResult(messages, j, id, consumed);
                    if (displaced >= 0)
                    {
                        consumed.Add(displaced);
                        result.Add(messages[displaced]);
                        report.MovedResults++;
                        continue;
                    }

                    result.Add(ChatMessage.Tool(id, InterruptedToolResult));
                    report.SyntheticResults++;
                }

                // 紧邻块里未被认领的条目：同 id 的重复结果，或不属于本次调用的结果
                for (int k = i + 1; k < j; k++)
                {
                    if (consumed.Contains(k))
                        continue;
                    var id = messages[k].ToolCallId;
                    if (!string.IsNullOrEmpty(id) && emitted.Contains(id))
                    {
                        consumed.Add(k);
                        report.DroppedDuplicates++;
                    }
                    // 其余留给外层循环按孤立结果丢弃
                }

                i = j - 1;
            }

            return result;
        }

        /// <summary>
        /// 在下一个带 tool_calls 的 assistant 之前查找被挤到别处的同 id 结果（例如中间插进了 system 消息）。
        /// 越过下一个 assistant 就不再找：那之后的同 id 结果属于别的调用批次，挪过来会打乱因果。
        /// </summary>
        private static int FindDisplacedResult(IReadOnlyList<ChatMessage> messages, int start, string id, HashSet<int> consumed)
        {
            for (int k = start; k < messages.Count; k++)
            {
                var m = messages[k];
                if (m == null)
                    continue;
                if (IsAssistantWithToolCalls(m))
                    return -1;
                if (IsTool(m) && m.ToolCallId == id && !consumed.Contains(k))
                    return k;
            }
            return -1;
        }

        private static bool IsTool(ChatMessage m)
        {
            return m.Role == "tool";
        }

        private static bool IsAssistantWithToolCalls(ChatMessage m)
        {
            return m.Role == "assistant" && m.ToolCalls != null && m.ToolCalls.Count > 0;
        }
    }
}
