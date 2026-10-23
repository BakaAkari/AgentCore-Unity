using System;
using AgentCore.Editor.L10n;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.LLM
{
    /// <summary>
    /// 会话级 LLM 用量账本（v1.17.0）。
    /// <para>
    /// 所有客户端（OpenAI 兼容 / Anthropic 原生）在拿到 usage 后调用 <see cref="Record"/>，
    /// 账本累计本次 Editor 会话（或自上次 <see cref="ResetSession"/> 起）的分量合计与估算成本，
    /// 并通过 <see cref="OnUsageRecorded"/> 通知 UI。静态、线程安全；不持久化。
    /// </para>
    /// </summary>
    public static class UsageLedger
    {
        private static readonly object Lock = new();
        private static LlmUsage _session = NewEmpty();
        private static LlmUsage _last;
        private static double _sessionCostUsd;
        private static bool _sessionCostPartial;

        /// <summary>记录一次调用后触发（参数为该次调用用量）。可能在后台线程触发，UI 需自行切回主线程。</summary>
        public static event Action<LlmUsage> OnUsageRecorded;

        /// <summary>会话累计用量快照。</summary>
        public static LlmUsage Session
        {
            get { lock (Lock) return _session.Clone(); }
        }

        /// <summary>最近一次调用的用量（无则 null）。</summary>
        public static LlmUsage Last
        {
            get { lock (Lock) return _last?.Clone(); }
        }

        /// <summary>会话累计估算成本（美元，仅计入价格已知的调用）。</summary>
        public static double SessionCostUsd
        {
            get { lock (Lock) return _sessionCostUsd; }
        }

        /// <summary>会话中是否有价格未知的调用（成本为下限估计）。</summary>
        public static bool SessionCostPartial
        {
            get { lock (Lock) return _sessionCostPartial; }
        }

        /// <summary>
        /// 记录一次调用用量并写一行 Info 日志（含命中率）。null / 空用量忽略。
        /// </summary>
        /// <param name="usage">归一化用量</param>
        /// <param name="source">调用来源标签（日志用，如 "stream" / "anthropic"）</param>
        /// <param name="ttl1h">Anthropic 1h TTL（影响缓存写入计价）</param>
        public static void Record(LlmUsage usage, string source = null, bool ttl1h = false)
        {
            GatewayDiscovery.ObserveUsage(AgentCore.Editor.Config.ActiveModelConfig.ActiveProfile, usage);
            usage ??= new LlmUsage();

            var cost = usage.IsEmpty ? (double?)null : ModelRegistry.EstimateCostUsd(usage, ttl1h);
            lock (Lock)
            {
                _session.Add(usage);
                _last = usage.Clone();
                if (cost.HasValue) _sessionCostUsd += cost.Value;
                else _sessionCostPartial = true;
            }

            var costText = cost.HasValue ? $" cost=${cost.Value:F4}" : string.Empty;
            AgentCoreLog.Info($"[AgentCore] LLM usage{(source != null ? $" [{source}]" : "")} model={usage.Model}: {usage.ToLogString()}{costText}");

            try { OnUsageRecorded?.Invoke(usage); }
            catch (Exception ex) { AgentCoreLog.Warning($"[AgentCore] UsageLedger listener failed: {ex.Message}"); }
        }

        /// <summary>清零会话累计。</summary>
        public static void ResetSession()
        {
            lock (Lock)
            {
                _session = NewEmpty();
                _last = null;
                _sessionCostUsd = 0;
                _sessionCostPartial = false;
            }
            try { OnUsageRecorded?.Invoke(null); }
            catch { /* UI 监听异常不影响清零 */ }
        }

        /// <summary>一行摘要，如 <c>12 calls · in 1.2M (hit 83.1%) · out 45.6K · ≈$1.23</c>。</summary>
        public static string FormatSessionSummary()
        {
            LlmUsage s;
            double cost;
            bool partial;
            lock (Lock)
            {
                s = _session.Clone();
                cost = _sessionCostUsd;
                partial = _sessionCostPartial;
            }
            if (s.Calls == 0) return Loc.Tr("contextUsage.llmUsage.none", "暂无调用");
            var costText = cost > 0 ? $" · ≈${cost:F2}{(partial ? "+" : "")}" : (partial ? " · " + Loc.Tr("contextUsage.cost.unknown", "费用未知") : string.Empty);
            return Loc.Tr("contextUsage.session.summary", "{0} 次 · 输入 {1}（缓存命中 {2}）· 输出 {3}{4}", s.Calls, FormatTokens(s.TotalInput), s.CacheStatus == "unknown" ? Loc.Tr("contextUsage.cache.unknown", "未知") : s.CacheStatus, FormatTokens(s.Output), costText);
        }

        /// <summary>紧凑 token 数格式：1234 → 1.2K，1234567 → 1.2M。</summary>
        public static string FormatTokens(long n)
        {
            if (n >= 1_000_000) return $"{n / 1_000_000.0:F1}M";
            if (n >= 1_000) return $"{n / 1_000.0:F1}K";
            return n.ToString();
        }

        private static LlmUsage NewEmpty() => new() { Calls = 0 };
    }
}
