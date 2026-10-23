using System;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.LLM
{
    /// <summary>
    /// 归一化的单次 LLM 调用用量（v1.17.0）。
    /// <para>
    /// 各供应商对"缓存命中"的上报字段互不相同，此前插件只读 <c>prompt_tokens/completion_tokens</c>，
    /// 完全看不到缓存命中率——Claude 0% 命中、vLLM 前缀被破坏这类问题只能体现在账单上。
    /// 本类把所有已知字段归一为五个互斥分量：
    /// </para>
    /// <list type="bullet">
    /// <item><see cref="InputUncached"/> —— 全价输入（未命中、也未写入缓存的部分）</item>
    /// <item><see cref="CacheRead"/> —— 命中缓存的输入（OpenAI cached_tokens / DeepSeek prompt_cache_hit_tokens /
    ///     Anthropic cache_read_input_tokens）</item>
    /// <item><see cref="CacheWrite"/> —— 本次写入缓存的输入（Anthropic cache_creation_input_tokens /
    ///     OpenAI cache_write_tokens），按写入溢价计费</item>
    /// <item><see cref="Output"/> —— 输出 token（含 reasoning）</item>
    /// <item><see cref="Reasoning"/> —— 输出中的 reasoning 部分（仅供展示，已包含在 Output 内）</item>
    /// </list>
    /// <para>
    /// 关键语义差异：OpenAI 兼容协议的 <c>prompt_tokens</c> <b>包含</b>缓存部分；Anthropic 原生协议的
    /// <c>input_tokens</c> <b>不包含</b>缓存部分（总输入 = input + cache_read + cache_creation）。
    /// 两个工厂方法分别处理，调用方不需要关心。
    /// </para>
    /// </summary>
    [Serializable]
    public sealed class LlmUsage
    {
        /// <summary>全价输入 token（未命中缓存、未写入缓存）。</summary>
        public int InputUncached;

        /// <summary>命中缓存的输入 token。</summary>
        public int CacheRead;

        /// <summary>本次写入缓存的输入 token。</summary>
        public int CacheWrite;

        /// <summary>输出 token（包含 reasoning）。</summary>
        public int Output;

        /// <summary>输出中的 reasoning token（已包含在 <see cref="Output"/> 内，仅供展示）。</summary>
        public int Reasoning;

        /// <summary>产生该用量的模型名（便于按模型计价；可空）。</summary>
        public string Model;

        /// <summary>调用次数（单次调用为 1；累加后为总调用次数）。</summary>
        public int Calls = 1;
        public bool CacheMetricsKnown;
        public string CacheStatus => CacheMetricsKnown && TotalInput > 0 ? $"{HitRate * 100f:F1}%" : "unknown";

        /// <summary>总输入 token = 全价 + 命中 + 写入。</summary>
        public int TotalInput => InputUncached + CacheRead + CacheWrite;

        /// <summary>缓存命中率（命中输入 / 总输入），总输入为 0 时返回 0。</summary>
        public float HitRate => TotalInput > 0 ? (float)CacheRead / TotalInput : 0f;

        /// <summary>是否为空用量（服务端未上报任何字段）。</summary>
        public bool IsEmpty => TotalInput == 0 && Output == 0;

        /// <summary>
        /// 从 OpenAI 兼容协议的 <c>usage</c> 对象归一化。
        /// <para>
        /// 兼容字段（按优先级取第一个存在的）：
        /// 命中 = <c>prompt_tokens_details.cached_tokens</c>（OpenAI / vLLM --enable-prompt-tokens-details /
        /// OpenRouter）→ <c>prompt_cache_hit_tokens</c>（DeepSeek）→ <c>cache_read_input_tokens</c>（LiteLLM 透传 Anthropic）；
        /// 写入 = <c>prompt_tokens_details.cache_write_tokens</c>（OpenAI / OpenRouter）→ <c>cache_creation_input_tokens</c>（LiteLLM）。
        /// </para>
        /// </summary>
        /// <param name="usage">usage JSON 对象；null 返回 null。</param>
        /// <param name="model">模型名（可空）。</param>
        public static LlmUsage FromOpenAI(JObject usage, string model = null)
        {
            if (usage == null) return null;

            int prompt = ReadInt(usage, "prompt_tokens");
            int completion = ReadInt(usage, "completion_tokens");
            var promptDetails = usage["prompt_tokens_details"] as JObject;
            var completionDetails = usage["completion_tokens_details"] as JObject;

            int cacheRead = FirstPresent(
                promptDetails, "cached_tokens",
                usage, "prompt_cache_hit_tokens",
                usage, "cache_read_input_tokens");
            int cacheWrite = FirstPresent(
                promptDetails, "cache_write_tokens",
                usage, "cache_creation_input_tokens",
                null, null);

            // DeepSeek 只给 hit/miss 不给 prompt_tokens 的极端情况：用 hit + miss 还原总输入
            if (prompt == 0 && usage["prompt_cache_miss_tokens"] != null)
            {
                prompt = cacheRead + ReadInt(usage, "prompt_cache_miss_tokens");
            }

            return new LlmUsage
            {
                CacheMetricsKnown = IsNumber(promptDetails?["cached_tokens"]) || IsNumber(usage["prompt_cache_hit_tokens"]) || IsNumber(usage["cache_read_input_tokens"]),
                InputUncached = Math.Max(0, prompt - cacheRead - cacheWrite),
                CacheRead = cacheRead,
                CacheWrite = cacheWrite,
                Output = completion,
                Reasoning = ReadInt(completionDetails, "reasoning_tokens"),
                Model = model
            };
        }

        /// <summary>
        /// 从 Anthropic Messages API 的 <c>usage</c> 对象归一化。
        /// <c>input_tokens</c> 已是未缓存的剩余部分，不需要再减。
        /// </summary>
        /// <param name="usage">usage JSON 对象；null 返回 null。</param>
        /// <param name="model">模型名（可空）。</param>
        public static LlmUsage FromAnthropic(JObject usage, string model = null)
        {
            if (usage == null) return null;

            return new LlmUsage
            {
                CacheMetricsKnown = IsNumber(usage["cache_read_input_tokens"]),
                InputUncached = ReadInt(usage, "input_tokens"),
                CacheRead = ReadInt(usage, "cache_read_input_tokens"),
                CacheWrite = ReadInt(usage, "cache_creation_input_tokens"),
                Output = ReadInt(usage, "output_tokens"),
                Model = model
            };
        }

        /// <summary>
        /// 用 <paramref name="later"/> 中非零的字段覆盖本实例（Anthropic 流式：message_start 给输入，
        /// message_delta 给累计输出，部分网关在 message_delta 里也重复给输入）。
        /// </summary>
        public void MergeLatest(LlmUsage later)
        {
            if (later == null) return;
            CacheMetricsKnown |= later.CacheMetricsKnown;
            if (later.InputUncached > 0) InputUncached = later.InputUncached;
            if (later.CacheRead > 0) CacheRead = later.CacheRead;
            if (later.CacheWrite > 0) CacheWrite = later.CacheWrite;
            if (later.Output > 0) Output = later.Output;
            if (later.Reasoning > 0) Reasoning = later.Reasoning;
            if (!string.IsNullOrEmpty(later.Model)) Model = later.Model;
        }

        /// <summary>把另一份用量累加到本实例（会话/全局账本用）。</summary>
        public void Add(LlmUsage other)
        {
            if (other == null) return;
            CacheMetricsKnown = Calls == 0 ? other.CacheMetricsKnown : CacheMetricsKnown && other.CacheMetricsKnown;
            InputUncached += other.InputUncached;
            CacheRead += other.CacheRead;
            CacheWrite += other.CacheWrite;
            Output += other.Output;
            Reasoning += other.Reasoning;
            Calls += other.Calls;
        }

        /// <summary>复制一份（账本快照用）。</summary>
        public LlmUsage Clone() => (LlmUsage)MemberwiseClone();

        /// <summary>一行日志格式，如 <c>in=12034 (uncached=1200 read=10500 write=334) out=812 hit=87.3%</c>。</summary>
        public string ToLogString()
        {
            var reasoning = Reasoning > 0 ? $" (reasoning={Reasoning})" : string.Empty;
            return $"in={TotalInput} (uncached={InputUncached} read={CacheRead} write={CacheWrite}) " +
                   $"out={Output}{reasoning} hit={CacheStatus}";
        }

        private static bool IsNumber(JToken token) => token?.Type == JTokenType.Integer;

        private static int ReadInt(JObject obj, string key)
        {
            if (obj == null || key == null) return 0;
            var token = obj[key];
            if (token == null || token.Type == JTokenType.Null) return 0;
            try { return token.Value<int>(); }
            catch { return 0; }
        }

        private static int FirstPresent(JObject a, string ka, JObject b, string kb, JObject c, string kc)
        {
            if (a?[ka] != null && a[ka].Type != JTokenType.Null) return ReadInt(a, ka);
            if (b?[kb] != null && b[kb].Type != JTokenType.Null) return ReadInt(b, kb);
            if (c != null && kc != null && c[kc] != null && c[kc].Type != JTokenType.Null) return ReadInt(c, kc);
            return 0;
        }
    }
}
