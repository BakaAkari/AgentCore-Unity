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

        private static readonly object PersistLock = new object();
        private static bool _loaded;

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
            EditorPrefs.DeleteKey(PersistKey);
            AgentCoreLog.Info("[AgentCore] RequestPruningRegistry: cleared all learned pruning rules (memory + persisted).");
        }
    }
}
