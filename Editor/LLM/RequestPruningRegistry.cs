using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using AgentCore.Editor.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace AgentCore.Editor.LLM
{
    /// <summary>
    /// 请求字段自动裁剪注册表（error-driven request pruning, v1.13.0-alpha.2+；
    /// v1.14.0 起持久化到 EditorPrefs，Domain Reload / Editor 重启后学到的规则不丢失）。
    /// <para>
    /// 目标：当供应商（Bedrock / OpenAI 原生 / Anthropic 原生等）对某些字段返回 400 时
    /// （如 <c>reasoning: Extra inputs are not permitted</c> / <c>`temperature` is deprecated</c>），
    /// 从错误消息里学到"此 endpoint + model 组合不接受该字段"，加入注册表；
    /// 下次同组合请求自动 strip 掉这些字段，用户无感。
    /// </para>
    /// <para>
    /// 生存期：内存缓存（<see cref="ConcurrentDictionary{TKey, TValue}"/>）+ EditorPrefs 持久化。
    /// 每次学到新字段立即写盘，Editor 重启后首次访问时懒加载读回。不做"服务端解禁"检测——
    /// 若供应商后续放宽限制，字段会被裁剪但服务端通常忽略缺失的可选字段，不影响功能；
    /// 用户可通过 <see cref="ClearAll"/> 手动清空重学。
    /// </para>
    /// <para>
    /// 键 = <c>endpoint + "|" + model</c>；值 = 该组合下已知禁用的 JSON 顶层字段集合。
    /// 供应商差异按 endpoint 隔离（同一 Bedrock 网关的不同模型也可能规则不同，故加 model）。
    /// </para>
    /// </summary>
    public static class RequestPruningRegistry
    {
        /// <summary>EditorPrefs 持久化键。整个注册表序列化为一个 JSON 字符串存于此键。</summary>
        private const string PersistKey = "AgentCore_RequestPruningRegistry_v1";

        /// <summary>reasoning 档位学习结果的持久化键（与字段裁剪分开，避免旧键格式冲突）。</summary>
        private const string ReasoningPersistKey = "AgentCore_ReasoningPruning_v1";

        /// <summary>400 响应里出现这些词才视为"reasoning 档位错误"，避免把签名/温度错误误学成档位禁用。</summary>
        private static readonly Regex EffortContext = new(
            @"reasoning_effort|reasoning\.effort|output_config|budget_tokens|\beffort\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>"Supported values are: 'low', 'medium', 'high'" / "must be one of ['low','high']"。</summary>
        private static readonly Regex SupportedValuesList = new(
            @"(?:supported\s+values\s+are|allowed\s+values\s+are|must\s+be\s+one\s+of)\s*[:=]?\s*(?<list>\[[^\]]*\]|(?:[`'""][^`'""]+[`'""]\s*,?\s*)+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex QuotedToken = new(
            @"[`'""]([A-Za-z][A-Za-z0-9_\-]*)[`'""]", RegexOptions.Compiled);

        private static readonly Regex BareToken = new(
            @"[A-Za-z][A-Za-z0-9_\-]*", RegexOptions.Compiled);

        /// <summary>"Invalid value: 'xhigh'" / "Unsupported value 'max'"。</summary>
        private static readonly Regex InvalidValuePattern = new(
            @"(?:invalid|unsupported|unknown|unrecognized)\s+(?:value\s+)?[:=]?\s*[`'""]?([A-Za-z][A-Za-z0-9_\-]*)[`'""]?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>"'minimal' is not supported" / "effort 'max' is not supported"。</summary>
        private static readonly Regex NotSupportedValuePattern = new(
            @"[`'""]?([A-Za-z][A-Za-z0-9_\-]*)['`""]?\s+is\s+not\s+(?:supported|permitted|valid|recognized|allowed)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// 错误消息 → 禁用字段的正则库。
        /// <para>
        /// 每条规则匹配一类供应商的错误文案。字段名必须与 <see cref="ChatCompletionRequest"/> 的
        /// <c>[JsonProperty]</c> 名或 <see cref="RequestEnrichment"/> 注入的字段名保持一致（小写）。
        /// </para>
        /// <para>
        /// 维护策略：见到新错误文案 → 新加一条正则。不追求"完备覆盖"，只求"覆盖今天见到的"。
        /// 未匹配的 400 会直接抛给用户，用户贴 log → 我们加正则。
        /// </para>
        /// </summary>
        private static readonly (Regex Pattern, string Field)[] ErrorSignatures = new[]
        {
            // Bedrock / LiteLLM: "reasoning: Extra inputs are not permitted"
            (new Regex(@"reasoning[^a-z0-9_]{1,20}Extra inputs are not permitted", RegexOptions.IgnoreCase | RegexOptions.Compiled), "reasoning"),

            // Bedrock / LiteLLM: "`temperature` is deprecated for this model"
            (new Regex(@"[`'""]?temperature[`'""]?\s+is\s+deprecated", RegexOptions.IgnoreCase | RegexOptions.Compiled), "temperature"),
            // OpenAI 原生: "'temperature' is not supported with this model" / "Unsupported parameter: 'temperature'"
            (new Regex(@"[`'""]?temperature[`'""]?\s+is\s+not\s+supported", RegexOptions.IgnoreCase | RegexOptions.Compiled), "temperature"),
            (new Regex(@"Unsupported\s+parameter:\s*[`'""]?temperature[`'""]?", RegexOptions.IgnoreCase | RegexOptions.Compiled), "temperature"),

            // Bedrock / LiteLLM: "`top_p` is deprecated" / "`top_p` is not supported"
            (new Regex(@"[`'""]?top_p[`'""]?\s+is\s+(deprecated|not\s+supported)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "top_p"),

            // OpenAI 原生 reasoning models: "'max_tokens' is not supported ... use 'max_completion_tokens'"
            (new Regex(@"[`'""]?max_tokens[`'""]?\s+is\s+not\s+supported", RegexOptions.IgnoreCase | RegexOptions.Compiled), "max_tokens"),

            // Generic: "stream_options" / "stop" 等常见拒绝
            (new Regex(@"[`'""]?stream_options[`'""]?\s+is\s+not\s+(supported|permitted)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "stream_options"),
        };

        /// <summary>
        /// 已学到的禁字段清单。key = "endpoint|model"，value = 该组合下禁用的顶层 JSON 字段集合。
        /// 懒加载：首次访问时从 EditorPrefs 读回；此后内存缓存为准，每次变更同步写盘。
        /// </summary>
        private static readonly ConcurrentDictionary<string, HashSet<string>> BannedFields
            = new ConcurrentDictionary<string, HashSet<string>>();

        /// <summary>已学到的 reasoning 档位约束。key 同 <see cref="MakeKey"/>。</summary>
        private static readonly ConcurrentDictionary<string, ReasoningRule> ReasoningRules
            = new ConcurrentDictionary<string, ReasoningRule>();

        private static readonly object PersistLock = new object();
        private static bool _loaded;

        /// <summary>单次 400 学到的档位结果（供客户端决定是否重建请求重试一次）。</summary>
        public sealed class ReasoningLearningResult
        {
            /// <summary>本次是否真的学到了新约束。</summary>
            public bool Learned { get; }

            /// <summary>建议下次尝试的档位（<see cref="ReasoningParamMapper.ToEffortString"/> 规范名；null = 无建议）。</summary>
            public string NextSuggestedEffort { get; }

            internal ReasoningLearningResult(bool learned, string nextSuggestedEffort)
            {
                Learned = learned;
                NextSuggestedEffort = nextSuggestedEffort;
            }

            /// <summary>未学到任何约束。</summary>
            public static ReasoningLearningResult None => new(false, null);
        }

        /// <summary>
        /// 某 endpoint|model 组合已学到的 reasoning 档位约束快照。
        /// </summary>
        public readonly struct ReasoningConstraint
        {
            /// <summary>学到的最高可用档（null = 未学到上限）。</summary>
            public readonly ReasoningParamMapper.ReasoningLevel? MaxAllowedTier;

            /// <summary>学到的被服务端拒绝的档位（快照，可能为空）。</summary>
            public readonly IReadOnlyCollection<ReasoningParamMapper.ReasoningLevel> BannedTiers;

            internal ReasoningConstraint(
                ReasoningParamMapper.ReasoningLevel? maxAllowedTier,
                IReadOnlyCollection<ReasoningParamMapper.ReasoningLevel> bannedTiers)
            {
                MaxAllowedTier = maxAllowedTier;
                BannedTiers = bannedTiers ?? Array.Empty<ReasoningParamMapper.ReasoningLevel>();
            }

            /// <summary>是否没有任何已学约束。</summary>
            public bool IsEmpty => !MaxAllowedTier.HasValue && BannedTiers.Count == 0;
        }

        private sealed class ReasoningRule
        {
            public string MaxAllowedTier;
            public readonly HashSet<string> BannedTiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class PersistedReasoningRule
        {
            [JsonProperty("max")] public string MaxAllowedTier;
            [JsonProperty("banned")] public List<string> BannedTiers;
        }

        /// <summary>确保 EditorPrefs 里的持久化数据已加载进内存缓存（幂等，仅首次真正读取）。</summary>
        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (PersistLock)
            {
                if (_loaded) return;
                try
                {
                    var raw = EditorPrefs.GetString(PersistKey, string.Empty);
                    if (!string.IsNullOrEmpty(raw))
                    {
                        var dict = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(raw);
                        if (dict != null)
                        {
                            foreach (var kv in dict)
                            {
                                var set = new HashSet<string>(kv.Value ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                                BannedFields[kv.Key] = set;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    AgentCoreLog.Warning($"[AgentCore] RequestPruningRegistry: failed to load persisted rules, starting empty: {ex.Message}");
                }

                try
                {
                    var rawReasoning = EditorPrefs.GetString(ReasoningPersistKey, string.Empty);
                    if (!string.IsNullOrEmpty(rawReasoning))
                    {
                        var dict = JsonConvert.DeserializeObject<Dictionary<string, PersistedReasoningRule>>(rawReasoning);
                        if (dict != null)
                        {
                            foreach (var kv in dict)
                            {
                                var rule = new ReasoningRule { MaxAllowedTier = kv.Value?.MaxAllowedTier };
                                if (kv.Value?.BannedTiers != null)
                                    foreach (var tier in kv.Value.BannedTiers)
                                        if (!string.IsNullOrWhiteSpace(tier)) rule.BannedTiers.Add(tier);
                                ReasoningRules[kv.Key] = rule;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    AgentCoreLog.Warning($"[AgentCore] RequestPruningRegistry: failed to load persisted reasoning rules, starting empty: {ex.Message}");
                }
                finally
                {
                    _loaded = true;
                }
            }
        }

        /// <summary>将当前内存缓存整体序列化写回 EditorPrefs。IO 失败不阻塞请求，仅记警告。</summary>
        private static void Persist()
        {
            try
            {
                var dict = new Dictionary<string, List<string>>();
                foreach (var kv in BannedFields)
                {
                    lock (kv.Value) dict[kv.Key] = new List<string>(kv.Value);
                }
                var json = JsonConvert.SerializeObject(dict);
                EditorPrefs.SetString(PersistKey, json);
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] RequestPruningRegistry: failed to persist learned rules: {ex.Message}");
            }
        }

        /// <summary>将 reasoning 约束整体序列化写回 EditorPrefs（独立键）。</summary>
        private static void PersistReasoning()
        {
            try
            {
                var dict = new Dictionary<string, PersistedReasoningRule>();
                foreach (var kv in ReasoningRules)
                {
                    lock (kv.Value)
                    {
                        dict[kv.Key] = new PersistedReasoningRule
                        {
                            MaxAllowedTier = kv.Value.MaxAllowedTier,
                            BannedTiers = new List<string>(kv.Value.BannedTiers)
                        };
                    }
                }
                EditorPrefs.SetString(ReasoningPersistKey, JsonConvert.SerializeObject(dict));
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] RequestPruningRegistry: failed to persist reasoning rules: {ex.Message}");
            }
        }

        /// <summary>
        /// 查询指定组合已学到的 reasoning 档位约束（无记录时返回空约束）。
        /// </summary>
        public static ReasoningConstraint GetReasoningConstraint(string endpoint, string model)
        {
            EnsureLoaded();
            var key = MakeKey(endpoint, model);
            if (!ReasoningRules.TryGetValue(key, out var rule))
                return new ReasoningConstraint(null, Array.Empty<ReasoningParamMapper.ReasoningLevel>());

            lock (rule)
            {
                ReasoningParamMapper.ReasoningLevel? max = null;
                if (!string.IsNullOrWhiteSpace(rule.MaxAllowedTier))
                {
                    var parsed = ReasoningParamMapper.ParseLevel(rule.MaxAllowedTier);
                    if (parsed != ReasoningParamMapper.ReasoningLevel.Auto) max = parsed;
                }

                var banned = new List<ReasoningParamMapper.ReasoningLevel>(rule.BannedTiers.Count);
                foreach (var tier in rule.BannedTiers)
                {
                    var parsed = ReasoningParamMapper.ParseLevel(tier);
                    if (parsed != ReasoningParamMapper.ReasoningLevel.Auto) banned.Add(parsed);
                }
                return new ReasoningConstraint(max, banned);
            }
        }

        /// <summary>
        /// 从 400 响应学习"此 endpoint|model 不接受哪些 reasoning 档位"。
        /// <para>
        /// 识别两类文案：① 列出允许值（<c>Supported values are: 'low', 'medium', 'high'</c> /
        /// <c>must be one of [...]</c>）→ 直接取上限并禁用低于下限的档；② 只说明某档不被支持
        /// （<c>'minimal' is not supported</c>）→ 禁用该档并逐级下调上限（Max→XHigh→High→Medium）。
        /// </para>
        /// </summary>
        /// <param name="endpoint">当前 endpoint。</param>
        /// <param name="model">当前模型名。</param>
        /// <param name="attemptedEffort">本次请求实际发送的 effort 字符串（可为 null）。</param>
        /// <param name="responseBody">400 响应体。</param>
        /// <returns>学习结果；<see cref="ReasoningLearningResult.Learned"/> 为 true 时调用方应重建请求重试一次。</returns>
        public static ReasoningLearningResult LearnReasoningFromErrorResponse(
            string endpoint, string model, string attemptedEffort, string responseBody)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(responseBody))
                return ReasoningLearningResult.None;

            var allowed = ExtractAllowedEfforts(responseBody);
            var attemptedFromBody = NormalizeEffort(ExtractAttemptedEffort(responseBody));
            var attempted = NormalizeEffort(attemptedEffort) ?? attemptedFromBody;

            if (allowed == null && attempted == null)
                return ReasoningLearningResult.None;

            // 只有明确与 effort/预算相关的错误才学习，避免把签名/温度/上下文超限误学成档位禁用。
            // 从响应体里直接解析出的值本身已是 effort 档位名（minimal/low/.../max），可免此检查。
            if (allowed == null && !attemptedFromBody.HasValue && !EffortContext.IsMatch(responseBody))
                return ReasoningLearningResult.None;

            var key = MakeKey(endpoint, model);
            var rule = ReasoningRules.GetOrAdd(key, _ => new ReasoningRule());
            bool changed = false;
            string next = null;

            lock (rule)
            {
                if (allowed != null && allowed.Count > 0)
                {
                    var min = allowed[0];
                    var max = allowed[0];
                    foreach (var tier in allowed)
                    {
                        if ((int)tier < (int)min) min = tier;
                        if ((int)tier > (int)max) max = tier;
                    }

                    if (rule.MaxAllowedTier == null || IsLowerThan(max, rule.MaxAllowedTier))
                    {
                        rule.MaxAllowedTier = ReasoningParamMapper.ToEffortString(max);
                        changed = true;
                    }

                    // 服务端允许的最低档高于我们请求的档时，把更低的档登记为禁用，让吸附向上。
                    for (var tier = ReasoningParamMapper.ReasoningLevel.Minimal;
                         (int)tier < (int)min;
                         tier = (ReasoningParamMapper.ReasoningLevel)((int)tier + 1))
                    {
                        if (rule.BannedTiers.Add(ReasoningParamMapper.ToEffortString(tier))) changed = true;
                    }

                    if (attempted.HasValue && !Contains(allowed, attempted.Value)
                        && rule.BannedTiers.Add(ReasoningParamMapper.ToEffortString(attempted.Value)))
                        changed = true;

                    next = ReasoningParamMapper.ToEffortString(max);
                }
                else
                {
                    var rejected = attempted.Value;
                    if (rule.BannedTiers.Add(ReasoningParamMapper.ToEffortString(rejected))) changed = true;

                    if ((int)rejected >= (int)ReasoningParamMapper.ReasoningLevel.Medium)
                    {
                        var steppedDown = (ReasoningParamMapper.ReasoningLevel)((int)rejected - 1);
                        if (rule.MaxAllowedTier == null || IsLowerThan(steppedDown, rule.MaxAllowedTier))
                        {
                            rule.MaxAllowedTier = ReasoningParamMapper.ToEffortString(steppedDown);
                            changed = true;
                        }
                        next = ReasoningParamMapper.ToEffortString(steppedDown);
                    }
                    else if (rejected == ReasoningParamMapper.ReasoningLevel.Minimal)
                    {
                        next = ReasoningParamMapper.ToEffortString(ReasoningParamMapper.ReasoningLevel.Low);
                    }
                    else if (rejected == ReasoningParamMapper.ReasoningLevel.Low)
                    {
                        next = ReasoningParamMapper.ToEffortString(ReasoningParamMapper.ReasoningLevel.Minimal);
                    }
                }
            }

            if (changed)
            {
                PersistReasoning();
                AgentCoreLog.Info($"[AgentCore] RequestPruningRegistry: learned reasoning constraint for '{key}' (attempted={attemptedEffort ?? "?"}, next={next ?? "?"}).");
            }

            return changed ? new ReasoningLearningResult(true, next) : ReasoningLearningResult.None;
        }

        /// <summary>
        /// 手动登记 reasoning 约束（测试 / 用户显式覆盖）。写入即持久化。
        /// </summary>
        public static void RegisterReasoningConstraint(
            string endpoint, string model, string maxAllowedTier, IEnumerable<string> bannedTiers = null)
        {
            EnsureLoaded();
            var key = MakeKey(endpoint, model);
            var rule = ReasoningRules.GetOrAdd(key, _ => new ReasoningRule());
            lock (rule)
            {
                if (!string.IsNullOrWhiteSpace(maxAllowedTier))
                    rule.MaxAllowedTier = ReasoningParamMapper.ToEffortString(
                        ReasoningParamMapper.ParseLevel(maxAllowedTier));
                if (bannedTiers != null)
                {
                    foreach (var tier in bannedTiers)
                    {
                        if (string.IsNullOrWhiteSpace(tier)) continue;
                        var parsed = ReasoningParamMapper.ParseLevel(tier);
                        if (parsed != ReasoningParamMapper.ReasoningLevel.Auto)
                            rule.BannedTiers.Add(ReasoningParamMapper.ToEffortString(parsed));
                    }
                }
            }
            PersistReasoning();
        }

        private static List<ReasoningParamMapper.ReasoningLevel> ExtractAllowedEfforts(string responseBody)
        {
            var match = SupportedValuesList.Match(responseBody);
            if (!match.Success) return null;

            var listText = match.Groups["list"].Value;
            var tiers = new List<ReasoningParamMapper.ReasoningLevel>();
            foreach (Match quoted in QuotedToken.Matches(listText))
                AddTier(tiers, quoted.Groups[1].Value);

            if (tiers.Count == 0)
            {
                foreach (Match bare in BareToken.Matches(listText))
                    AddTier(tiers, bare.Value);
            }

            return tiers.Count > 0 ? tiers : null;
        }

        private static void AddTier(List<ReasoningParamMapper.ReasoningLevel> tiers, string token)
        {
            var level = NormalizeEffort(token);
            if (!level.HasValue || level.Value == ReasoningParamMapper.ReasoningLevel.Off) return;
            if (!Contains(tiers, level.Value)) tiers.Add(level.Value);
        }

        private static string ExtractAttemptedEffort(string responseBody)
        {
            var invalid = InvalidValuePattern.Match(responseBody);
            if (invalid.Success) return invalid.Groups[1].Value;

            var notSupported = NotSupportedValuePattern.Match(responseBody);
            if (notSupported.Success) return notSupported.Groups[1].Value;

            return null;
        }

        private static ReasoningParamMapper.ReasoningLevel? NormalizeEffort(string effort)
        {
            if (string.IsNullOrWhiteSpace(effort)) return null;
            var parsed = ReasoningParamMapper.ParseLevel(effort);
            return parsed == ReasoningParamMapper.ReasoningLevel.Auto ? (ReasoningParamMapper.ReasoningLevel?)null : parsed;
        }

        private static bool IsLowerThan(ReasoningParamMapper.ReasoningLevel candidate, string current)
        {
            var parsed = ReasoningParamMapper.ParseLevel(current);
            if (parsed == ReasoningParamMapper.ReasoningLevel.Auto) return true;
            return (int)candidate < (int)parsed;
        }

        private static bool Contains(List<ReasoningParamMapper.ReasoningLevel> list, ReasoningParamMapper.ReasoningLevel tier)
        {
            foreach (var item in list)
                if (item == tier) return true;
            return false;
        }

        /// <summary>
        /// 生成 registry key。相同 endpoint 下不同 model 可能规则不同（例：LiteLLM 上 GPT-4 允许 temperature，
        /// Claude Sonnet 5 却不允许），故 endpoint + model 组合。
        /// </summary>
        public static string MakeKey(string endpoint, string model)
        {
            var e = (endpoint ?? string.Empty).Trim();
            var m = (model ?? string.Empty).Trim();
            return e + "|" + m;
        }

        /// <summary>
        /// 查询指定组合当前已知的禁字段集合（返回快照，调用方可安全枚举）。
        /// 无记录时返回空集合。
        /// </summary>
        public static IReadOnlyCollection<string> GetBannedFields(string endpoint, string model)
        {
            EnsureLoaded();
            var key = MakeKey(endpoint, model);
            if (BannedFields.TryGetValue(key, out var set))
            {
                lock (set) return new List<string>(set);
            }
            return Array.Empty<string>();
        }

        /// <summary>
        /// 从 HTTP 400 的响应体解析已知错误签名，返回本次识别到的禁字段（可能为空）。
        /// 匹配到任何字段时会**登记入表并持久化**（下次自动 strip，重启 Editor 也保留），
        /// 并返回该次识别集合供调用方决定是否重试。
        /// </summary>
        /// <returns>本次响应体里新识别到的禁字段集合；空集合表示无匹配（不该重试）。</returns>
        public static IReadOnlyCollection<string> LearnFromErrorResponse(string endpoint, string model, string responseBody)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(responseBody))
                return Array.Empty<string>();

            var newlyLearned = new List<string>(4);
            foreach (var (pattern, field) in ErrorSignatures)
            {
                if (pattern.IsMatch(responseBody))
                {
                    newlyLearned.Add(field);
                }
            }

            if (newlyLearned.Count == 0)
                return Array.Empty<string>();

            var key = MakeKey(endpoint, model);
            var set = BannedFields.GetOrAdd(key, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            lock (set)
            {
                foreach (var f in newlyLearned)
                    set.Add(f);
            }

            Persist();
            AgentCoreLog.Info($"[AgentCore] RequestPruningRegistry: learned {newlyLearned.Count} banned field(s) for '{key}': [{string.Join(", ", newlyLearned)}]. Persisted — future requests (incl. after Editor restart) will auto-strip.");
            return newlyLearned;
        }

        /// <summary>
        /// 手动登记禁字段（供测试/特殊场景使用）。幂等 - 已存在则忽略。写入即持久化。
        /// </summary>
        public static void RegisterBannedField(string endpoint, string model, string field)
        {
            EnsureLoaded();
            if (string.IsNullOrWhiteSpace(field)) return;
            var key = MakeKey(endpoint, model);
            var set = BannedFields.GetOrAdd(key, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            bool added;
            lock (set) { added = set.Add(field.Trim()); }
            if (added) Persist();
        }

        /// <summary>
        /// 从已序列化的请求 body（JSON 字符串）里移除注册表中标记为禁用的顶层字段，返回裁剪后的 body。
        /// 若无需裁剪则原样返回（避免不必要的 parse/reserialize）。
        /// </summary>
        public static string ApplyPruning(string endpoint, string model, string body)
        {
            if (string.IsNullOrEmpty(body)) return body;

            var banned = GetBannedFields(endpoint, model);
            if (banned.Count == 0) return body;

            try
            {
                var obj = JObject.Parse(body);
                bool changed = false;
                foreach (var field in banned)
                {
                    if (obj.Remove(field))
                        changed = true;
                }
                if (!changed) return body;

                AgentCoreLog.Debug($"[AgentCore] RequestPruningRegistry: stripped [{string.Join(", ", banned)}] from request to '{MakeKey(endpoint, model)}'");
                return obj.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (Exception ex)
            {
                // Parse 失败不阻塞请求，原样发出去让服务端报错以便下次学习。
                AgentCoreLog.Warning($"[AgentCore] RequestPruningRegistry: failed to prune body, sending as-is: {ex.Message}");
                return body;
            }
        }

        /// <summary>
        /// 清空所有已学清单（内存 + EditorPrefs 持久化，测试 / 用户强制重学时用）。
        /// </summary>
        public static void ClearAll()
        {
            BannedFields.Clear();
            ReasoningRules.Clear();
            EditorPrefs.DeleteKey(PersistKey);
            EditorPrefs.DeleteKey(ReasoningPersistKey);
            AgentCoreLog.Info("[AgentCore] RequestPruningRegistry: cleared all learned pruning + reasoning rules (memory + persisted).");
        }
    }
}
