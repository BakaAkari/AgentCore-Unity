using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.LLM.Anthropic
{
    /// <summary>
    /// 构建 Anthropic Messages 请求所需的全部参数（v1.17.0）。纯数据，便于单测。
    /// </summary>
    public sealed class AnthropicRequestOptions
    {
        public string Model;
        public bool Stream;

        /// <summary>content 部分的 max_tokens（不含 thinking 预算）。</summary>
        public int ContentMaxTokens = 8192;

        /// <summary>解析后的思考等级（Auto 表示按 <see cref="DefaultReasoningEnabled"/> / <see cref="DefaultEffort"/>）。</summary>
        public ReasoningParamMapper.ReasoningLevel Level = ReasoningParamMapper.ReasoningLevel.Auto;

        /// <summary>Auto 时是否开启思考（对应 profile 的 enableReasoningOutput）。</summary>
        public bool DefaultReasoningEnabled = true;

        /// <summary>Auto 时的 effort（"" = 模型默认）。</summary>
        public string DefaultEffort = "";

        /// <summary>budget_tokens 旧形态模型在 Auto 时使用的预算（0 = 4096）。</summary>
        public int DefaultBudgetTokens;

        /// <summary>采样温度（仅在模型支持且未启用思考时发送）。</summary>
        public float? Temperature;

        /// <summary>缓存 TTL 为 1 小时。</summary>
        public bool CacheTtl1h;

        /// <summary>不回放 thinking 块（签名失效重试时使用）。</summary>
        public bool DropReasoning;

        /// <summary>模型档案（为 null 时按 <see cref="Model"/> 查注册表）。</summary>
        public ModelProfile Profile;

        /// <summary>
        /// 使用会话中途工具变更（延迟工具带 <c>defer_loading</c> 声明，ToolChange 渲染为 <c>tool_addition</c>）。
        /// false 时只发送 <see cref="LLM.ToolChanges.EffectiveTools"/>。
        /// </summary>
        public bool ToolChanges;
    }

    /// <summary>
    /// 规范消息（OpenAI 形状的 <see cref="ChatMessage"/> 列表）→ Anthropic Messages 请求 JSON 的纯函数映射（v1.17.0）。
    /// <para>
    /// 设计要点（全部为"缓存前缀稳定 + 协议合法"服务）：
    /// </para>
    /// <list type="number">
    /// <item>开头连续的 system 消息 → 顶层 <c>system</c> 文本块；第一个缓存断点打在最后一块上（连同 tools 一起缓存）。</item>
    /// <item>会话中途的 system 消息 / <see cref="MessageKind.OperatorNote"/> → 包成 <c>&lt;system-reminder&gt;</c> 文本，
    ///     并入相邻 user 消息。追加式，不改动已缓存前缀；且对所有模型/网关都合法。</item>
    /// <item>tool 消息 → user 消息内的 <c>tool_result</c> 块；同角色相邻消息合并；user 内 tool_result 一律排在最前（协议要求）。</item>
    /// <item>悬空 tool_use / tool_result 修复：缺结果补占位错误结果，无主结果降级为文本。</item>
    /// <item>首条与末条必须是 user（不支持 assistant prefill 的新模型会 400），不满足时补 "(continue)"。</item>
    /// <item>assistant 的 thinking 块按原顺序回放（仅回放带签名的块），保证工具循环中推理连续且前缀字节稳定。</item>
    /// <item>第二、三个断点：最后一条消息末块 + 倒数第二条 user 消息末块（保证 20 块回溯窗口内必有上一次的缓存写入点）。</item>
    /// <item>延迟工具（<see cref="ToolDefinition.DeferLoading"/>）：<see cref="AnthropicRequestOptions.ToolChanges"/> 时全集声明，
    ///     <see cref="MessageKind.ToolChange"/> 渲染为紧邻下一条 assistant 之前（或末尾）的 <c>tool_addition</c> 系统消息，
    ///     工具列表不变 → 缓存前缀不变；否则只发送已启用的工具。</item>
    /// </list>
    /// </summary>
    public static class AnthropicMessageMapper
    {
        /// <summary>空文本占位（Anthropic 拒绝空 text 块）。</summary>
        internal const string EmptyPlaceholder = "(empty)";

        /// <summary>首/末消息补位用的 user 文本。</summary>
        internal const string ContinuePlaceholder = "(continue)";

        /// <summary>
        /// 构建完整请求 JSON。
        /// </summary>
        public static JObject BuildRequest(
            AnthropicRequestOptions options, IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var profile = options.Profile ?? ModelRegistry.Lookup(options.Model);
            var body = new JObject { ["model"] = options.Model };

            // ---- system ----
            int leading = 0;
            var system = new JArray();
            if (messages != null)
            {
                while (leading < messages.Count && messages[leading]?.Role == "system" && messages[leading].Kind == MessageKind.Normal)
                {
                    var text = messages[leading].Content;
                    if (!string.IsNullOrEmpty(text)) system.Add(TextBlock(text));
                    leading++;
                }
            }

            // ---- tools ----
            HashSet<string> deferred = null;
            if (UsesToolChanges(options, tools))
                deferred = new HashSet<string>(
                    tools.Where(t => t?.DeferLoading == true && t.Function?.Name != null).Select(t => t.Function.Name),
                    StringComparer.Ordinal);
            else if (tools != null)
                tools = LLM.ToolChanges.EffectiveTools(tools.ToList(), messages);
            var toolArray = BuildTools(tools, deferred != null);
            if (toolArray.Count > 0) body["tools"] = toolArray;

            if (system.Count > 0)
            {
                body["system"] = system;
                MarkCache((JObject)system[system.Count - 1], options.CacheTtl1h);
            }
            else
            {
                var lastLoaded = toolArray.LastOrDefault(t => t["defer_loading"] == null);
                if (lastLoaded != null) MarkCache((JObject)lastLoaded, options.CacheTtl1h);
            }

            // ---- messages ----
            var wire = BuildMessages(messages, leading, options.DropReasoning, deferred);
            ApplyMessageBreakpoints(wire, options.CacheTtl1h);
            body["messages"] = wire;

            // ---- thinking / effort / max_tokens / temperature ----
            var thinking = ResolveThinking(options, profile, out var effort, out int budget);
            if (thinking != null) body["thinking"] = thinking;
            if (effort != null) body["output_config"] = new JObject { ["effort"] = effort };

            int maxTokens = Math.Max(1, options.ContentMaxTokens) + budget;
            if (profile.MaxOutput > 0 && maxTokens > profile.MaxOutput) maxTokens = profile.MaxOutput;
            if (budget > 0 && maxTokens <= budget) maxTokens = budget + 1024;
            body["max_tokens"] = maxTokens;

            bool thinkingActive = thinking != null && thinking.Value<string>("type") != "disabled";
            if (options.Temperature.HasValue && profile.SupportsTemperature && !thinkingActive)
            {
                body["temperature"] = Math.Max(0f, Math.Min(1f, options.Temperature.Value));
            }

            if (options.Stream) body["stream"] = true;
            return body;
        }

        /// <summary>
        /// 按模型思考形态把抽象等级翻译为 <c>thinking</c> 对象与 <c>output_config.effort</c>。
        /// </summary>
        /// <param name="effort">输出：effort 字符串（null = 不发送）</param>
        /// <param name="budget">输出：thinking 预算（仅 budget_tokens 形态 &gt; 0，需加到 max_tokens 上）</param>
        /// <returns>thinking 对象；null = 不发送</returns>
        internal static JObject ResolveThinking(AnthropicRequestOptions o, ModelProfile p, out string effort, out int budget)
        {
            effort = null;
            budget = 0;

            bool on;
            string wantedEffort;
            switch (o.Level)
            {
                case ReasoningParamMapper.ReasoningLevel.Off: on = false; wantedEffort = null; break;
                case ReasoningParamMapper.ReasoningLevel.Low: on = true; wantedEffort = "low"; break;
                case ReasoningParamMapper.ReasoningLevel.Medium: on = true; wantedEffort = "medium"; break;
                case ReasoningParamMapper.ReasoningLevel.High: on = true; wantedEffort = "high"; break;
                default:
                    on = o.DefaultReasoningEnabled;
                    wantedEffort = NormalizeEffort(o.DefaultEffort);
                    break;
            }

            switch (p.Thinking)
            {
                case ThinkingMode.None:
                    return null;

                case ThinkingMode.BudgetTokens:
                    if (!on) return null;
                    budget = wantedEffort switch
                    {
                        "low" => 2048,
                        "medium" => 8192,
                        "high" => 24576,
                        "xhigh" or "max" => 32768,
                        _ => o.DefaultBudgetTokens >= 1024 ? o.DefaultBudgetTokens : 4096
                    };
                    if (p.SupportsEffort && wantedEffort != null) effort = ClampEffort(wantedEffort, p);
                    return new JObject { ["type"] = "enabled", ["budget_tokens"] = budget };

                case ThinkingMode.AlwaysOn:
                    if (!on)
                    {
                        if (p.ThinkingOff == "between_tools")
                            return new JObject { ["type"] = "between_tools" };
                        effort = "low";
                        return null;
                    }
                    if (p.SupportsEffort && wantedEffort != null) effort = ClampEffort(wantedEffort, p);
                    return Adaptive();

                default: // AdaptiveExplicit / AdaptiveDefaultOn
                    if (!on)
                    {
                        return p.ThinkingOff == "disabled" ? new JObject { ["type"] = "disabled" } : null;
                    }
                    if (p.SupportsEffort && wantedEffort != null) effort = ClampEffort(wantedEffort, p);
                    return Adaptive();
            }
        }

        private static JObject Adaptive() => new() { ["type"] = "adaptive", ["display"] = "summarized" };

        private static string NormalizeEffort(string effort)
        {
            var e = effort?.Trim().ToLowerInvariant();
            return e is "low" or "medium" or "high" or "xhigh" or "max" ? e : null;
        }

        private static string ClampEffort(string effort, ModelProfile p)
            => (effort == "xhigh" || effort == "max") && !p.SupportsXHigh ? "high" : effort;

        /// <summary>OpenAI 工具定义 → Anthropic <c>{name, description, input_schema}</c>。</summary>
        /// <summary>本次请求是否使用会话中途工具变更（需开启且存在延迟工具）。</summary>
        internal static bool UsesToolChanges(AnthropicRequestOptions options, IReadOnlyList<ToolDefinition> tools)
            => options != null && options.ToolChanges && tools != null && tools.Any(t => t?.DeferLoading == true);

        internal static JArray BuildTools(IReadOnlyList<ToolDefinition> tools, bool emitDeferLoading = false)
        {
            var arr = new JArray();
            if (tools == null) return arr;
            foreach (var t in tools)
            {
                var fn = t?.Function;
                if (fn == null || string.IsNullOrEmpty(fn.Name)) continue;
                var schema = fn.Parameters != null ? (JObject)fn.Parameters.DeepClone() : new JObject();
                if (schema["type"] == null) schema["type"] = "object";
                var tool = new JObject { ["name"] = fn.Name, ["input_schema"] = schema };
                if (!string.IsNullOrEmpty(fn.Description)) tool["description"] = fn.Description;
                if (emitDeferLoading && t.DeferLoading) tool["defer_loading"] = true;
                arr.Add(tool);
            }
            return arr;
        }

        /// <summary>
        /// 构建 messages 数组（不含断点）。<paramref name="start"/> 之前的消息已作为顶层 system。
        /// </summary>
        /// <param name="deferredTools">延迟声明的工具名；非 null 时 ToolChange 渲染为 <c>tool_addition</c>，否则丢弃。</param>
        internal static JArray BuildMessages(
            IReadOnlyList<ChatMessage> messages, int start, bool dropReasoning, ISet<string> deferredTools = null)
        {
            var turns = new List<(string role, JArray content)>();
            var pendingAdditions = new List<string>();
            var added = new HashSet<string>(StringComparer.Ordinal);
            void QueueAddition(string name)
            {
                if (deferredTools != null && name != null && deferredTools.Contains(name) && added.Add(name))
                    pendingAdditions.Add(name);
            }

            if (messages != null)
            {
                for (int i = start; i < messages.Count; i++)
                {
                    var m = messages[i];
                    if (m == null) continue;
                    if (m.Kind == MessageKind.ToolChange)
                    {
                        foreach (var name in LLM.ToolChanges.GetAddedNames(m)) QueueAddition(name);
                        continue;
                    }
                    switch (m.Role)
                    {
                        case "assistant":
                            // 调用过的延迟工具必须在调用之前启用（ToolChange 被压缩/截断掉时的兜底）
                            if (m.ToolCalls != null)
                                foreach (var tc in m.ToolCalls) QueueAddition(tc?.Function?.Name);
                            FlushToolAdditions(turns, pendingAdditions);
                            Append(turns, "assistant", AssistantBlocks(m, dropReasoning));
                            break;
                        case "tool":
                            Append(turns, "user", new JArray(new JObject
                            {
                                ["type"] = "tool_result",
                                ["tool_use_id"] = m.ToolCallId ?? string.Empty,
                                ["content"] = string.IsNullOrEmpty(m.Content) ? EmptyPlaceholder : m.Content
                            }));
                            break;
                        case "system":
                            if (!string.IsNullOrEmpty(m.Content))
                                Append(turns, "user", new JArray(TextBlock(WrapReminder(m.Content))));
                            break;
                        default:
                            if (m.Kind == MessageKind.OperatorNote)
                                Append(turns, "user", new JArray(TextBlock(WrapReminder(m.Content ?? string.Empty))));
                            else
                                Append(turns, "user", new JArray(TextBlock(string.IsNullOrEmpty(m.Content) ? EmptyPlaceholder : m.Content)));
                            break;
                    }
                }
            }

            RepairToolPairs(turns);

            if (turns.Count == 0 || turns[0].role != "user")
                turns.Insert(0, ("user", new JArray(TextBlock(ContinuePlaceholder))));
            if (turns[turns.Count - 1].role != "user")
                turns.Add(("user", new JArray(TextBlock(ContinuePlaceholder))));
            FlushToolAdditions(turns, pendingAdditions);

            var wire = new JArray();
            foreach (var (role, content) in turns)
            {
                wire.Add(new JObject { ["role"] = role, ["content"] = role == "user" ? ToolResultsFirst(content) : content });
            }
            return wire;
        }

        /// <summary>
        /// 输出待启用工具的 <c>tool_addition</c> 系统消息。协议要求紧跟 user 消息、其后为 assistant 或位于末尾，
        /// 因此只在追加 assistant 之前或收尾时调用。
        /// </summary>
        private static void FlushToolAdditions(List<(string role, JArray content)> turns, List<string> pending)
        {
            if (pending.Count == 0) return;
            if (turns.Count == 0 || turns[turns.Count - 1].role != "user")
                turns.Add(("user", new JArray(TextBlock(ContinuePlaceholder))));
            var blocks = new JArray();
            foreach (var name in pending)
            {
                blocks.Add(new JObject
                {
                    ["type"] = "tool_addition",
                    ["tool"] = new JObject { ["type"] = "tool_reference", ["name"] = name }
                });
            }
            turns.Add(("system", blocks));
            pending.Clear();
        }

        /// <summary>中途系统消息统一包装格式。</summary>
        internal static string WrapReminder(string text) => "<system-reminder>\n" + text + "\n</system-reminder>";

        private static JArray AssistantBlocks(ChatMessage m, bool dropReasoning)
        {
            var blocks = new JArray();
            if (!dropReasoning && m.ReasoningBlocks != null)
            {
                foreach (var rb in m.ReasoningBlocks)
                {
                    if (rb == null || !rb.IsReplayable) continue;
                    if (rb.Type == "redacted_thinking")
                        blocks.Add(new JObject { ["type"] = "redacted_thinking", ["data"] = rb.Data });
                    else
                        blocks.Add(new JObject { ["type"] = "thinking", ["thinking"] = rb.Thinking ?? string.Empty, ["signature"] = rb.Signature });
                }
            }

            if (!string.IsNullOrEmpty(m.Content)) blocks.Add(TextBlock(m.Content));

            if (m.ToolCalls != null)
            {
                foreach (var tc in m.ToolCalls)
                {
                    if (tc?.Function == null || string.IsNullOrEmpty(tc.Function.Name)) continue;
                    blocks.Add(new JObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = string.IsNullOrEmpty(tc.Id) ? "toolu_" + Guid.NewGuid().ToString("N") : tc.Id,
                        ["name"] = tc.Function.Name,
                        ["input"] = ParseToolInput(tc.Function.Arguments)
                    });
                }
            }

            if (!blocks.Any(b => b.Value<string>("type") is "text" or "tool_use"))
                blocks.Add(TextBlock(EmptyPlaceholder));
            return blocks;
        }

        /// <summary>tool_call.arguments（JSON 字符串）→ tool_use.input 对象；非对象时包成 <c>_raw_arguments</c>。</summary>
        internal static JObject ParseToolInput(string arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments)) return new JObject();
            try
            {
                if (JToken.Parse(arguments) is JObject obj) return obj;
            }
            catch
            {
                var repaired = OpenAICompatibleClient.SanitizeToolArguments(arguments);
                try { if (JToken.Parse(repaired) is JObject fixedObj) return fixedObj; }
                catch { /* 落到下面的原文包装 */ }
            }
            return new JObject { ["_raw_arguments"] = arguments };
        }

        private static void Append(List<(string role, JArray content)> turns, string role, JArray blocks)
        {
            if (blocks == null || blocks.Count == 0) return;
            if (turns.Count > 0 && turns[turns.Count - 1].role == role)
            {
                foreach (var b in blocks) turns[turns.Count - 1].content.Add(b);
                return;
            }
            turns.Add((role, blocks));
        }

        /// <summary>user 内容块重排：tool_result 在前，其余保持相对顺序（协议要求）。</summary>
        private static JArray ToolResultsFirst(JArray content)
        {
            var results = content.Where(b => b.Value<string>("type") == "tool_result").ToList();
            if (results.Count == 0 || results.Count == content.Count) return content;
            var others = content.Where(b => b.Value<string>("type") != "tool_result").ToList();
            var arr = new JArray();
            foreach (var r in results) arr.Add(r);
            foreach (var o in others) arr.Add(o);
            return arr;
        }

        /// <summary>
        /// 修复 tool_use / tool_result 配对：每个 assistant 的 tool_use 必须在紧随其后的 user 消息中有结果；
        /// 缺失补错误占位结果；不属于前一条 assistant 的 tool_result 降级为文本。
        /// </summary>
        private static void RepairToolPairs(List<(string role, JArray content)> turns)
        {
            for (int i = 0; i < turns.Count; i++)
            {
                var (role, content) = turns[i];
                if (role == "user")
                {
                    var allowed = new HashSet<string>();
                    if (i > 0 && turns[i - 1].role == "assistant")
                    {
                        foreach (var b in turns[i - 1].content)
                            if (b.Value<string>("type") == "tool_use") allowed.Add(b.Value<string>("id"));
                    }
                    for (int k = 0; k < content.Count; k++)
                    {
                        var b = (JObject)content[k];
                        if (b.Value<string>("type") != "tool_result") continue;
                        var id = b.Value<string>("tool_use_id");
                        if (allowed.Contains(id)) continue;
                        content[k] = TextBlock($"[tool result {id}]\n{ContentAsText(b["content"])}");
                    }
                    continue;
                }

                var useIds = content.Where(b => b.Value<string>("type") == "tool_use").Select(b => b.Value<string>("id")).ToList();
                if (useIds.Count == 0) continue;

                if (i + 1 >= turns.Count || turns[i + 1].role != "user")
                    turns.Insert(i + 1, ("user", new JArray()));
                var next = turns[i + 1].content;
                var have = new HashSet<string>(next.Where(b => b.Value<string>("type") == "tool_result").Select(b => b.Value<string>("tool_use_id")));
                foreach (var id in useIds)
                {
                    if (have.Contains(id)) continue;
                    next.Add(new JObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = id,
                        ["content"] = "(no result recorded)",
                        ["is_error"] = true
                    });
                }
            }
        }

        private static string ContentAsText(JToken content)
        {
            if (content == null) return string.Empty;
            if (content.Type == JTokenType.String) return content.Value<string>();
            return string.Concat(content.Children().Select(c => c.Value<string>("text") ?? string.Empty));
        }

        /// <summary>
        /// 消息断点：最后一条非 system 消息末块 + 倒数第二条 user 消息末块（<c>tool_addition</c> 系统消息不能打断点）。
        /// </summary>
        internal static void ApplyMessageBreakpoints(JArray wire, bool ttl1h)
        {
            if (wire == null || wire.Count == 0) return;
            for (int i = wire.Count - 1; i >= 0; i--)
            {
                if ((wire[i] as JObject)?.Value<string>("role") == "system") continue;
                MarkLastCacheableBlock(wire[i] as JObject, ttl1h);
                break;
            }

            int userSeen = 0;
            for (int i = wire.Count - 1; i >= 0; i--)
            {
                if ((wire[i] as JObject)?.Value<string>("role") != "user") continue;
                if (++userSeen == 2)
                {
                    MarkLastCacheableBlock(wire[i] as JObject, ttl1h);
                    break;
                }
            }
        }

        private static void MarkLastCacheableBlock(JObject message, bool ttl1h)
        {
            if (message?["content"] is not JArray content) return;
            for (int i = content.Count - 1; i >= 0; i--)
            {
                if (content[i] is JObject b && b.Value<string>("type") is "text" or "tool_result" or "tool_use" or "image")
                {
                    MarkCache(b, ttl1h);
                    return;
                }
            }
        }

        private static void MarkCache(JObject block, bool ttl1h)
        {
            var cc = new JObject { ["type"] = "ephemeral" };
            if (ttl1h) cc["ttl"] = "1h";
            block["cache_control"] = cc;
        }

        private static JObject TextBlock(string text) => new() { ["type"] = "text", ["text"] = text };

        /// <summary>Anthropic stop_reason → OpenAI finish_reason。</summary>
        public static string MapStopReason(string stopReason) => stopReason switch
        {
            "end_turn" or "stop_sequence" => "stop",
            "tool_use" => "tool_calls",
            "max_tokens" or "model_context_window_exceeded" => "length",
            "refusal" => "content_filter",
            "pause_turn" => "stop",
            null => null,
            _ => stopReason
        };
    }
}
