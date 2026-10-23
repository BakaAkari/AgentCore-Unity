using System;
using System.Collections.Generic;
using System.Linq;

namespace AgentCore.Editor.LLM
{
    /// <summary>
    /// 会话中途启用工具（v1.17.0）。
    /// <para>
    /// 工具定义渲染在 prompt 最前端：工具列表一变，Anthropic 的整个缓存（tools → system → messages）全部失效，
    /// 下一次请求按 1.25× 重写全部前缀。为此 Agent 每轮声明的是<b>稳定的工具全集</b>，按需工具标记
    /// <see cref="ToolDefinition.DeferLoading"/>；某个按需工具变为可用时，在历史末尾追加一条
    /// <see cref="MessageKind.ToolChange"/> 消息（追加式，持久化，随会话恢复）。
    /// </para>
    /// <list type="bullet">
    /// <item>支持的 Anthropic 模型：全集带 <c>defer_loading</c> 发送，ToolChange 渲染为 <c>tool_addition</c> 系统消息，缓存前缀保留。</item>
    /// <item>其他情况（OpenAI 兼容协议 / 不支持的模型）：只发送 <see cref="EffectiveTools"/>，ToolChange 消息不上线。</item>
    /// </list>
    /// </summary>
    public static class ToolChanges
    {
        /// <summary>Anthropic beta 头。</summary>
        public const string AnthropicBeta = "mid-conversation-tool-changes-2026-07-01";

        private const string ContentPrefix = "Tools now available: ";

        /// <summary>创建一条启用 <paramref name="toolNames"/> 的 ToolChange 消息。</summary>
        public static ChatMessage CreateAddition(IEnumerable<string> toolNames)
            => new()
            {
                Role = "system",
                Kind = MessageKind.ToolChange,
                Content = ContentPrefix + string.Join(", ", toolNames)
            };

        /// <summary>ToolChange 消息启用的工具名；其他消息返回空。</summary>
        public static IReadOnlyList<string> GetAddedNames(ChatMessage message)
        {
            if (message?.Kind != MessageKind.ToolChange || string.IsNullOrEmpty(message.Content))
                return Array.Empty<string>();
            var body = message.Content.StartsWith(ContentPrefix, StringComparison.Ordinal)
                ? message.Content.Substring(ContentPrefix.Length)
                : message.Content;
            return body.Split(',').Select(n => n.Trim()).Where(n => n.Length > 0).ToList();
        }

        /// <summary>历史中已启用（ToolChange）或已调用过（tool_call）的工具名。</summary>
        public static HashSet<string> CollectAvailable(IReadOnlyList<ChatMessage> messages)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (messages == null) return names;
            foreach (var m in messages)
            {
                if (m == null) continue;
                foreach (var n in GetAddedNames(m)) names.Add(n);
                if (m.ToolCalls == null) continue;
                foreach (var tc in m.ToolCalls)
                {
                    var n = tc?.Function?.Name;
                    if (!string.IsNullOrEmpty(n)) names.Add(n);
                }
            }
            return names;
        }

        /// <summary>
        /// 不使用延迟声明时实际应发送的工具：非延迟工具 ∪ 历史中已启用/已调用的延迟工具（保持声明顺序）。
        /// 没有延迟工具时原样返回。
        /// </summary>
        public static List<ToolDefinition> EffectiveTools(List<ToolDefinition> tools, IReadOnlyList<ChatMessage> messages)
        {
            if (tools == null || !tools.Any(t => t != null && t.DeferLoading)) return tools;
            var available = CollectAvailable(messages);
            var result = tools.Where(t => t != null && (!t.DeferLoading || available.Contains(t.Function?.Name ?? string.Empty))).ToList();
            return result.Count > 0 ? result : null;
        }

        /// <summary>去掉 ToolChange 消息（不含时原样返回）。</summary>
        public static List<ChatMessage> StripMessages(List<ChatMessage> messages)
        {
            if (messages == null || !messages.Any(m => m?.Kind == MessageKind.ToolChange)) return messages;
            return messages.Where(m => m?.Kind != MessageKind.ToolChange).ToList();
        }

        /// <summary>
        /// 本轮可见但历史中尚未启用的延迟工具名（按声明顺序）。供 Agent 在每次 LLM 调用前追加 ToolChange。
        /// </summary>
        public static List<string> PendingAdditions(
            IReadOnlyList<ToolDefinition> declared, ICollection<string> visibleNames, IReadOnlyList<ChatMessage> history)
        {
            var pending = new List<string>();
            if (declared == null || visibleNames == null || visibleNames.Count == 0) return pending;
            HashSet<string> added = null;
            foreach (var t in declared)
            {
                var name = t?.Function?.Name;
                if (t == null || !t.DeferLoading || string.IsNullOrEmpty(name) || !visibleNames.Contains(name)) continue;
                added ??= CollectAdded(history);
                if (!added.Contains(name)) pending.Add(name);
            }
            return pending;
        }

        /// <summary>历史中 ToolChange 已启用的工具名。</summary>
        public static HashSet<string> CollectAdded(IReadOnlyList<ChatMessage> messages)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (messages == null) return names;
            foreach (var m in messages)
                foreach (var n in GetAddedNames(m)) names.Add(n);
            return names;
        }
    }
}
