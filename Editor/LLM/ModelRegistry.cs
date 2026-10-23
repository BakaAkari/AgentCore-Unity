using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentCore.Editor.Utils;
using Newtonsoft.Json;

namespace AgentCore.Editor.LLM
{
    /// <summary>模型家族（决定协议细节与缓存策略的大类）。</summary>
    public enum ModelFamily
    {
        Unknown,
        Anthropic,
        OpenAI,
        DeepSeek,
        Gemini,
        Qwen,
        Glm
    }

    /// <summary>
    /// Anthropic 模型的思考（thinking）参数形态。不同代际的模型接受的形态互不兼容，发错直接 400。
    /// </summary>
    public enum ThinkingMode
    {
        /// <summary>不支持思考参数，请求中不得出现 <c>thinking</c>。</summary>
        None,
        /// <summary>旧形态 <c>{type:"enabled", budget_tokens:N}</c>（Haiku 4.5、Sonnet 4.5 及更早）。</summary>
        BudgetTokens,
        /// <summary><c>{type:"adaptive"}</c>，省略 = 不思考，可显式 disabled（Opus 4.6/4.7/4.8、Sonnet 4.6、Sonnet 5）。</summary>
        AdaptiveExplicit,
        /// <summary><c>{type:"adaptive"}</c>，省略 = 默认思考，可显式 disabled（Opus 5、Haiku 5.5）。</summary>
        AdaptiveDefaultOn,
        /// <summary>思考始终开启，disabled/budget 均 400，只能靠 effort 调节（Opus 5.5、Sonnet 5.5、Fable 5.x）。</summary>
        AlwaysOn
    }

    /// <summary>
    /// 单个模型（按名称前缀匹配）的能力与价格档案（v1.17.0）。
    /// <para>
    /// 字段全部可在 <c>ProjectSettings/AgentCoreModelRegistry.json</c> 中覆盖/新增——新模型上线不必改代码。
    /// 价格单位为美元 / 百万 token；0 表示未知（成本面板显示 "—"，不猜）。
    /// </para>
    /// </summary>
    [Serializable]
    public sealed class ModelProfile
    {
        /// <summary>模型名前缀（小写，已剥离 <c>anthropic/</c> 之类的网关前缀）。最长前缀优先。</summary>
        [JsonProperty("prefix")] public string Prefix;

        [JsonProperty("family")] public ModelFamily Family;

        /// <summary>上下文窗口（token）。0 = 未知，回落 <c>ContextWindowManager</c> 旧表。</summary>
        [JsonProperty("context_window")] public int ContextWindow;

        /// <summary>单次最大输出（token）。0 = 未知（不做截断）。</summary>
        [JsonProperty("max_output")] public int MaxOutput;

        [JsonProperty("input_price")] public double InputPrice;
        [JsonProperty("output_price")] public double OutputPrice;

        /// <summary>缓存读取价格。0 = 按 <see cref="InputPrice"/> × 0.1 估算。</summary>
        [JsonProperty("cache_read_price")] public double CacheReadPrice;

        /// <summary>缓存写入价格（5 分钟 TTL）。0 = Anthropic 按 1.25×，其他按 1×。</summary>
        [JsonProperty("cache_write_price")] public double CacheWritePrice;

        [JsonProperty("thinking")] public ThinkingMode Thinking = ThinkingMode.None;

        /// <summary>是否接受非默认 temperature（新一代 Claude 发了直接 400）。</summary>
        [JsonProperty("supports_temperature")] public bool SupportsTemperature = true;

        /// <summary>是否接受 <c>output_config.effort</c>。</summary>
        [JsonProperty("supports_effort")] public bool SupportsEffort;

        /// <summary>是否接受 <c>xhigh</c> effort 档（Opus 4.7 起）。</summary>
        [JsonProperty("supports_xhigh")] public bool SupportsXHigh;

        /// <summary>
        /// "关闭思考"时使用的形态：<c>disabled</c>（显式 disabled）、<c>omit</c>（省略 thinking 参数）、
        /// <c>between_tools</c>（Sonnet 5.5 专用）、<c>effort_low</c>（无法关闭，只能降到 low）。
        /// </summary>
        [JsonProperty("thinking_off")] public string ThinkingOff = "disabled";

        /// <summary>DeepSeek 思考模式 + 工具调用时需要回传 <c>reasoning_content</c>。</summary>
        [JsonProperty("echo_reasoning_content")] public bool EchoReasoningContent;

        /// <summary>
        /// 支持会话中途增减工具（<c>defer_loading</c> + <c>tool_addition</c>，beta
        /// <c>mid-conversation-tool-changes-2026-07-01</c>）。不支持时工具变化会使整个缓存失效。
        /// </summary>
        [JsonProperty("supports_tool_changes")] public bool SupportsToolChanges;

        /// <summary>估算缓存读价格（未配置时按 0.1× 输入价）。</summary>
        public double EffectiveCacheReadPrice => CacheReadPrice > 0 ? CacheReadPrice : InputPrice * 0.1;

        /// <summary>估算缓存写价格。</summary>
        /// <param name="ttl1h">是否为 1 小时 TTL（Anthropic 写入价 2×）。</param>
        public double EffectiveCacheWritePrice(bool ttl1h)
        {
            if (CacheWritePrice > 0 && !ttl1h) return CacheWritePrice;
            if (Family == ModelFamily.Anthropic) return InputPrice * (ttl1h ? 2.0 : 1.25);
            return InputPrice;
        }

        /// <summary>价格是否已知。</summary>
        public bool HasPricing => InputPrice > 0 || OutputPrice > 0;

        /// <summary>
        /// 估算一次调用的美元成本；价格未知返回 null。
        /// </summary>
        public double? EstimateCostUsd(LlmUsage usage, bool ttl1h = false)
        {
            if (usage == null || !HasPricing) return null;
            const double perToken = 1.0 / 1_000_000.0;
            return (usage.InputUncached * InputPrice
                    + usage.CacheRead * EffectiveCacheReadPrice
                    + usage.CacheWrite * EffectiveCacheWritePrice(ttl1h)
                    + usage.Output * OutputPrice) * perToken;
        }
    }

    /// <summary>
    /// 模型能力/价格注册表（v1.17.0）。
    /// <para>
    /// 取代散落在 <c>ReasoningParamMapper</c> / <c>ContextWindowManager</c> / <c>ModelCapabilityDetector</c>
    /// 中的三张前缀表成为"模型知识"的单一真源（旧表保留为未命中时的兜底，不破坏现有行为）。
    /// 内置条目覆盖 Anthropic 全系（协议细节必须精确，发错 400）；其他家族只记录协议相关的
    /// 家族标识，价格留空，由用户在覆盖文件中按自己的合同价填写。
    /// </para>
    /// <para>
    /// 覆盖文件：<c>ProjectSettings/AgentCoreModelRegistry.json</c>（数组，字段同 <see cref="ModelProfile"/>），
    /// 用户条目优先于内置条目。文件修改后调用 <see cref="Reload"/> 或重启 Editor 生效。
    /// </para>
    /// </summary>
    public static class ModelRegistry
    {
        /// <summary>覆盖文件路径（相对项目根目录）。</summary>
        public const string OverrideFilePath = "ProjectSettings/AgentCoreModelRegistry.json";

        private static List<ModelProfile> _overrides;
        private static readonly object Lock = new();

        private static readonly ModelProfile Unknown = new() { Prefix = "", Family = ModelFamily.Unknown };

        /// <summary>
        /// 内置条目。价格来源：Anthropic 官方价目（2026-10）；其它家族价格未内置。
        /// </summary>
        private static readonly List<ModelProfile> BuiltIns = new()
        {
            // ── Anthropic: 思考始终开启的一代（disabled / budget_tokens / temperature 均 400）──
            Claude("claude-fable-5-1", 10, 50, 0.25, ThinkingMode.AlwaysOn, "effort_low", temp: false, xhigh: true, toolChanges: true),
            Claude("claude-fable-5", 10, 50, 0, ThinkingMode.AlwaysOn, "effort_low", temp: false, xhigh: true, toolChanges: true),
            Claude("claude-mythos-5-1", 10, 50, 0.25, ThinkingMode.AlwaysOn, "effort_low", temp: false, xhigh: true, toolChanges: true),
            Claude("claude-mythos-5", 10, 50, 0, ThinkingMode.AlwaysOn, "effort_low", temp: false, xhigh: true, toolChanges: true),
            Claude("claude-opus-5-5", 4, 20, 0.20, ThinkingMode.AlwaysOn, "effort_low", temp: false, xhigh: true, toolChanges: true),
            Claude("claude-sonnet-5-5", 2, 10, 0.20, ThinkingMode.AlwaysOn, "between_tools", temp: false, xhigh: true, toolChanges: true),
            // ── Anthropic: adaptive，默认开启但可关闭 ──
            Claude("claude-opus-5", 5, 25, 0, ThinkingMode.AdaptiveDefaultOn, "disabled", temp: false, xhigh: true, toolChanges: true),
            Claude("claude-haiku-5-5", 0.10, 0.50, 0, ThinkingMode.AdaptiveDefaultOn, "disabled", temp: false, xhigh: true),
            // ── Anthropic: adaptive，省略即不思考 ──
            Claude("claude-opus-4-8", 5, 25, 0, ThinkingMode.AdaptiveExplicit, "omit", temp: false, xhigh: true, toolChanges: true),
            Claude("claude-opus-4-7", 5, 25, 0, ThinkingMode.AdaptiveExplicit, "omit", temp: false, xhigh: true),
            Claude("claude-sonnet-5", 2, 10, 0, ThinkingMode.AdaptiveExplicit, "omit", temp: false, xhigh: true),
            Claude("claude-opus-4-6", 5, 25, 0, ThinkingMode.AdaptiveExplicit, "omit", temp: true, xhigh: false),
            Claude("claude-sonnet-4-6", 3, 15, 0, ThinkingMode.AdaptiveExplicit, "omit", temp: true, xhigh: false),
            // ── Anthropic: budget_tokens 旧形态 ──
            Claude("claude-opus-4-5", 5, 25, 0, ThinkingMode.BudgetTokens, "omit", temp: true, xhigh: false, effort: true, ctx: 200_000, maxOut: 64_000),
            Claude("claude-sonnet-4-5", 3, 15, 0, ThinkingMode.BudgetTokens, "omit", temp: true, xhigh: false, effort: false, ctx: 200_000, maxOut: 64_000),
            Claude("claude-haiku-4-5", 1, 5, 0, ThinkingMode.BudgetTokens, "omit", temp: true, xhigh: false, effort: false, ctx: 200_000, maxOut: 64_000),
            Claude("claude-sonnet-4", 0, 0, 0, ThinkingMode.BudgetTokens, "omit", temp: true, xhigh: false, effort: false, ctx: 200_000, maxOut: 64_000),
            Claude("claude-opus-4", 0, 0, 0, ThinkingMode.BudgetTokens, "omit", temp: true, xhigh: false, effort: false, ctx: 200_000, maxOut: 32_000),
            Claude("claude-3-7", 0, 0, 0, ThinkingMode.BudgetTokens, "omit", temp: true, xhigh: false, effort: false, ctx: 200_000, maxOut: 64_000),
            Claude("claude-", 0, 0, 0, ThinkingMode.None, "omit", temp: true, xhigh: false, effort: false, ctx: 200_000, maxOut: 8_192),

            // ── 其他家族：只记录家族（协议差异），价格/窗口留空 ──
            new() { Prefix = "deepseek", Family = ModelFamily.DeepSeek, EchoReasoningContent = true },
            new() { Prefix = "gpt-", Family = ModelFamily.OpenAI },
            new() { Prefix = "o1", Family = ModelFamily.OpenAI },
            new() { Prefix = "o3", Family = ModelFamily.OpenAI },
            new() { Prefix = "o4", Family = ModelFamily.OpenAI },
            new() { Prefix = "gemini", Family = ModelFamily.Gemini },
            new() { Prefix = "qwen", Family = ModelFamily.Qwen },
            new() { Prefix = "glm", Family = ModelFamily.Glm },
        };

        private static ModelProfile Claude(
            string prefix, double input, double output, double cacheRead,
            ThinkingMode thinking, string thinkingOff, bool temp, bool xhigh,
            bool effort = true, int ctx = 1_000_000, int maxOut = 128_000, bool toolChanges = false)
            => new()
            {
                Prefix = prefix,
                Family = ModelFamily.Anthropic,
                ContextWindow = ctx,
                MaxOutput = maxOut,
                InputPrice = input,
                OutputPrice = output,
                CacheReadPrice = cacheRead,
                Thinking = thinking,
                ThinkingOff = thinkingOff,
                SupportsTemperature = temp,
                SupportsEffort = effort,
                SupportsXHigh = xhigh,
                SupportsToolChanges = toolChanges
            };

        /// <summary>
        /// 规范化模型名：小写、剥离网关前缀（<c>anthropic/claude-x</c> → <c>claude-x</c>，
        /// Bedrock <c>us.anthropic.claude-x</c> → <c>claude-x</c>）。
        /// </summary>
        public static string Normalize(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName)) return string.Empty;
            var name = modelName.Trim().ToLowerInvariant();
            int slash = name.LastIndexOf('/');
            if (slash >= 0 && slash < name.Length - 1) name = name.Substring(slash + 1);
            int anthropicDot = name.IndexOf("anthropic.", StringComparison.Ordinal);
            if (anthropicDot >= 0) name = name.Substring(anthropicDot + "anthropic.".Length);
            return name;
        }

        /// <summary>
        /// 查找模型档案（最长前缀匹配，用户覆盖优先）；未命中返回 <see cref="ModelFamily.Unknown"/> 空档案（非 null）。
        /// </summary>
        public static ModelProfile Lookup(string modelName)
        {
            var name = Normalize(modelName);
            if (name.Length == 0) return Unknown;

            var fromOverride = BestMatch(GetOverrides(), name);
            if (fromOverride != null) return fromOverride;
            return BestMatch(BuiltIns, name) ?? Unknown;
        }

        /// <summary>按模型名判断家族。</summary>
        public static ModelFamily GetFamily(string modelName) => Lookup(modelName).Family;

        /// <summary>估算一次调用成本（未知价格返回 null）。</summary>
        public static double? EstimateCostUsd(LlmUsage usage, bool ttl1h = false)
            => usage == null ? null : Lookup(usage.Model).EstimateCostUsd(usage, ttl1h);

        /// <summary>丢弃已加载的覆盖文件缓存，下次 <see cref="Lookup"/> 时重新读取。</summary>
        public static void Reload()
        {
            lock (Lock) _overrides = null;
        }

        /// <summary>测试用：直接注入覆盖条目（null 恢复为从文件读取）。</summary>
        internal static void SetOverridesForTesting(List<ModelProfile> overrides)
        {
            lock (Lock) _overrides = overrides;
        }

        private static ModelProfile BestMatch(List<ModelProfile> entries, string name)
        {
            if (entries == null) return null;
            ModelProfile best = null;
            foreach (var e in entries)
            {
                if (e == null || string.IsNullOrEmpty(e.Prefix)) continue;
                if (!name.StartsWith(e.Prefix.ToLowerInvariant(), StringComparison.Ordinal)) continue;
                if (best == null || e.Prefix.Length > best.Prefix.Length) best = e;
            }
            return best;
        }

        private static List<ModelProfile> GetOverrides()
        {
            lock (Lock)
            {
                if (_overrides != null) return _overrides;
                _overrides = new List<ModelProfile>();
                try
                {
                    if (File.Exists(OverrideFilePath))
                    {
                        var json = File.ReadAllText(OverrideFilePath);
                        var parsed = JsonConvert.DeserializeObject<List<ModelProfile>>(json);
                        if (parsed != null) _overrides = parsed.Where(p => p != null && !string.IsNullOrEmpty(p.Prefix)).ToList();
                    }
                }
                catch (Exception ex)
                {
                    AgentCoreLog.Warning($"[AgentCore] Failed to load {OverrideFilePath}: {ex.Message}");
                }
                return _overrides;
            }
        }
    }
}
