using System;
using System.Collections.Generic;
using System.Linq;
using AgentCore.Editor.L10n;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.LLM
{
    /// <summary>LLM 用量统计时间窗口。</summary>
    public enum UsageWindow
    {
        Session = 0,
        Today = 1,
        Days7 = 7,
        Days30 = 30,
        Days90 = 90,
    }

    /// <summary>指定窗口内单个模型的用量汇总。</summary>
    public sealed class UsageModelBreakdown
    {
        public string Model { get; set; }
        public long CallCount { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long CacheReadTokens { get; set; }
        public long CacheCreationTokens { get; set; }
        public long ReasoningTokens { get; set; }
        public long TotalTokens { get; set; }
        public double CostUsd { get; set; }
        public bool CostPartial { get; set; }
        public bool CacheMetricsKnown { get; set; }

        public double CacheHitRate => InputTokens > 0 ? (double)CacheReadTokens / InputTokens : 0.0;
    }

    /// <summary>指定时间窗口内的 LLM 用量汇总（含按模型拆解）。</summary>
    public sealed class UsagePeriodSummary
    {
        public UsageWindow Window { get; set; }
        public int WindowDays { get; set; }
        public int ActiveDays { get; set; }
        public long CallCount { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long CacheReadTokens { get; set; }
        public long CacheCreationTokens { get; set; }
        public long ReasoningTokens { get; set; }
        public long TotalTokens { get; set; }
        public double CostUsd { get; set; }
        public bool CostPartial { get; set; }
        public bool CacheMetricsKnown { get; set; }
        public IReadOnlyList<UsageModelBreakdown> Models { get; set; } = Array.Empty<UsageModelBreakdown>();

        public double CacheHitRate => InputTokens > 0 ? (double)CacheReadTokens / InputTokens : 0.0;
    }

    /// <summary>
    /// 持久化 LLM 用量账本（支持跨 Domain Reload 与 Editor 重启恢复当前会话及 90 天滚动历史）。
    /// <para>
    /// 所有客户端（OpenAI 兼容 / Anthropic 原生 / Vision）在拿到 usage 后调用 <see cref="Record"/>，
    /// 账本累计当前会话与当日历史桶并原子持久化到 <c>{PreferencesFolder}/AgentCore/UsageHistory.json</c>，
    /// 同时通过 <see cref="OnUsageRecorded"/> 与 <see cref="OnChanged"/> 通知 UI。静态、线程安全。
    /// </para>
    /// </summary>
    public static class UsageLedger
    {
        private static readonly object Lock = new object();

        private static bool _hydrated;
        private static bool _isTesting;
        private static UsageHistoryData _history = UsageHistoryStore.CreateEmpty();
        private static LlmUsage _session = NewEmpty();
        private static LlmUsage _last;
        private static double _sessionCostUsd;
        private static bool _sessionCostPartial;

        /// <summary>记录一次调用后触发（参数为该次调用用量；清零时传 null）。可能在后台线程触发，UI 需自行切回主线程。</summary>
        public static event Action<LlmUsage> OnUsageRecorded;

        /// <summary>账本状态变更后触发（记录、重置会话、清空历史或重载）。可能在后台线程触发，UI 需自行切回主线程。</summary>
        public static event Action OnChanged;

        /// <summary>当前生效的持久化文件路径（<c>{PreferencesFolder}/AgentCore/UsageHistory.json</c>）。</summary>
        public static string HistoryFilePath => UsageHistoryStore.GetFilePath();

        /// <summary>当前绑定的会话 ID（未绑定时为 null）。</summary>
        public static string CurrentSessionId
        {
            get
            {
                lock (Lock)
                {
                    EnsureHydratedLocked();
                    return _history.CurrentSession?.SessionId;
                }
            }
        }

        /// <summary>会话累计用量快照。</summary>
        public static LlmUsage Session
        {
            get
            {
                lock (Lock)
                {
                    EnsureHydratedLocked();
                    return _session.Clone();
                }
            }
        }

        /// <summary>最近一次调用的用量（无则 null）。</summary>
        public static LlmUsage Last
        {
            get
            {
                lock (Lock)
                {
                    EnsureHydratedLocked();
                    return _last?.Clone();
                }
            }
        }

        /// <summary>会话累计估算成本（美元，仅计入价格已知的调用）。</summary>
        public static double SessionCostUsd
        {
            get
            {
                lock (Lock)
                {
                    EnsureHydratedLocked();
                    return _sessionCostUsd;
                }
            }
        }

        /// <summary>会话中是否有价格未知的调用（成本为下限估计）。</summary>
        public static bool SessionCostPartial
        {
            get
            {
                lock (Lock)
                {
                    EnsureHydratedLocked();
                    return _sessionCostPartial;
                }
            }
        }

        /// <summary>
        /// 记录一次调用用量并持久化到历史存储，写一行 Info 日志（含命中率）。
        /// </summary>
        /// <param name="usage">归一化用量</param>
        /// <param name="source">调用来源标签（日志用，如 "stream" / "anthropic"）</param>
        /// <param name="ttl1h">Anthropic 1h TTL（影响缓存写入计价）</param>
        public static void Record(LlmUsage usage, string source = null, bool ttl1h = false)
        {
            if (!_isTesting)
            {
                try
                {
                    GatewayDiscovery.ObserveUsage(AgentCore.Editor.Config.ActiveModelConfig.ActiveProfile, usage);
                }
                catch (Exception ex)
                {
                    AgentCoreLog.Warning($"[AgentCore] GatewayDiscovery.ObserveUsage failed: {ex.Message}");
                }
            }

            usage ??= new LlmUsage();

            var cost = usage.IsEmpty ? (double?)null : ModelRegistry.EstimateCostUsd(usage, ttl1h);
            var localNow = UsageHistoryStore.GetLocalNow();
            var dateKey = UsageHistoryStore.FormatLocalDate(localNow);
            var modelKey = string.IsNullOrWhiteSpace(usage.Model) ? "unknown" : usage.Model.Trim();

            lock (Lock)
            {
                EnsureHydratedLocked();

                var cur = _history.CurrentSession;
                cur.Totals.Add(usage, cost);
                UsageHistoryStore.AccumulateModelBucket(cur.Models, modelKey, usage, cost);
                if (!string.IsNullOrWhiteSpace(usage.Model))
                    cur.Model = usage.Model.Trim();
                cur.LastCall = LlmUsageSnapshot.FromLlmUsage(usage);
                cur.HasLastCall = true;
                cur.UpdatedAtUtc = DateTime.UtcNow.ToString("o");

                UsageHistoryStore.AccumulateDailyBucket(_history, dateKey, modelKey, usage, cost);
                UsageHistoryStore.Prune(_history, localNow.Date);
                SyncSessionFieldsFromHistoryLocked();

                UsageHistoryStore.SaveAfterRecord(_history, dateKey, modelKey, usage, cost, localNow);
            }

            var costText = cost.HasValue ? $" cost=${cost.Value:F4}" : string.Empty;
            AgentCoreLog.Info($"[AgentCore] LLM usage{(source != null ? $" [{source}]" : "")} model={usage.Model}: {usage.ToLogString()}{costText}");

            RaiseUsageRecorded(usage);
            RaiseChanged();
        }

        /// <summary>
        /// 仅清零当前会话累计并持久化；<strong>不清除</strong> 1d / 7d / 30d / 90d 历史每日桶。
        /// </summary>
        public static void ResetSession()
        {
            lock (Lock)
            {
                EnsureHydratedLocked();
                ResetSessionCountersLocked(preserveSessionId: true);
                UsageHistoryStore.Save(_history);
            }

            RaiseUsageRecorded(null);
            RaiseChanged();
        }

        /// <summary>
        /// 绑定当前对话会话 ID：
        /// 若与已持久化会话 ID 相同（如 Domain Reload / Editor 重启恢复同一会话），保留当前会话计数不重置；
        /// 若与已持久化会话 ID 不同，则更新为新的会话 ID，并在 <paramref name="resetIfChanged"/> 为 <c>true</c> 时重置会话计数（不影响历史桶）。
        /// </summary>
        public static void BindSession(string sessionId, bool resetIfChanged = false)
        {
            var normalized = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();
            if (string.IsNullOrEmpty(normalized))
                return;

            bool didReset = false;
            bool didChange = false;

            lock (Lock)
            {
                EnsureHydratedLocked();
                var currentId = _history.CurrentSession.SessionId;
                if (string.Equals(currentId, normalized, StringComparison.Ordinal))
                    return;

                _history.CurrentSession.SessionId = normalized;
                _history.CurrentSession.UpdatedAtUtc = DateTime.UtcNow.ToString("o");
                didChange = true;

                if (resetIfChanged)
                {
                    ResetSessionCountersLocked(preserveSessionId: true);
                    didReset = true;
                }

                UsageHistoryStore.Save(_history);
            }

            if (didReset)
                RaiseUsageRecorded(null);
            if (didChange)
                RaiseChanged();
        }

        /// <summary>
        /// 显式清空全部 90 天历史桶与当前会话统计并持久化（供设置页“清空历史”按钮调用）。
        /// </summary>
        public static void ClearHistory()
        {
            lock (Lock)
            {
                _history = UsageHistoryStore.CreateEmpty();
                _hydrated = true;
                SyncSessionFieldsFromHistoryLocked();
                UsageHistoryStore.Save(_history);
            }

            RaiseUsageRecorded(null);
            RaiseChanged();
        }

        /// <summary>从磁盘重新加载历史与当前会话快照（用于手动刷新或测试模拟 Domain Reload / Editor 重启）。</summary>
        public static void ReloadFromDisk()
        {
            lock (Lock)
            {
                _hydrated = false;
                EnsureHydratedLocked();
            }

            RaiseChanged();
        }

        /// <summary>按时间窗口汇总用量与按模型拆解（模型列表按 TotalTokens 降序、CostUsd 降序排列）。</summary>
        public static UsagePeriodSummary GetWindowSummary(UsageWindow window)
        {
            lock (Lock)
            {
                EnsureHydratedLocked();

                if (window == UsageWindow.Session)
                {
                    var session = _history.CurrentSession;
                    var totals = session?.Totals ?? new UsageAggregate();
                    return BuildPeriodSummary(
                        UsageWindow.Session,
                        windowDays: 0,
                        activeDays: totals.CallCount > 0 ? 1 : 0,
                        totals: totals,
                        models: BuildModelBreakdowns(session?.Models));
                }

                var windowDays = Math.Max(1, (int)window);
                var today = UsageHistoryStore.GetLocalNow().Date;
                var startDate = today.AddDays(-(windowDays - 1));

                var aggregate = new UsageAggregate();
                var activeDays = 0;
                var perModel = new Dictionary<string, UsageModelBucket>(StringComparer.OrdinalIgnoreCase);

                if (_history.Days != null)
                {
                    for (var i = 0; i < _history.Days.Count; i++)
                    {
                        var day = _history.Days[i];
                        if (day == null || !UsageHistoryStore.TryParseLocalDate(day.Date, out var dayDate))
                            continue;
                        if (dayDate.Date < startDate || dayDate.Date > today)
                            continue;

                        if (day.Totals != null && day.Totals.CallCount > 0)
                            activeDays++;

                        aggregate.Add(day.Totals);

                        if (day.Models == null) continue;
                        for (var j = 0; j < day.Models.Count; j++)
                        {
                            var modelBucket = day.Models[j];
                            if (modelBucket == null || modelBucket.Totals == null) continue;
                            var key = string.IsNullOrWhiteSpace(modelBucket.Model) ? "unknown" : modelBucket.Model.Trim();
                            if (!perModel.TryGetValue(key, out var merged))
                            {
                                merged = new UsageModelBucket
                                {
                                    Model = key,
                                    Totals = new UsageAggregate(),
                                };
                                perModel[key] = merged;
                            }
                            merged.Totals.Add(modelBucket.Totals);
                        }
                    }
                }

                return BuildPeriodSummary(
                    window,
                    windowDays,
                    activeDays,
                    aggregate,
                    BuildModelBreakdowns(perModel.Values));
            }
        }

        /// <summary>一行摘要，如 <c>12 次 · 输入 1.2M（缓存命中 83.1%）· 输出 45.6K · ≈$1.23</c>。</summary>
        public static string FormatSessionSummary() => FormatWindowSummary(UsageWindow.Session);

        /// <summary>按时间窗口格式化一行用量摘要。</summary>
        public static string FormatWindowSummary(UsageWindow window)
        {
            var summary = GetWindowSummary(window);
            if (summary == null || summary.CallCount == 0)
                return Loc.Tr("contextUsage.llmUsage.none", "暂无调用");

            var cacheText = summary.CacheMetricsKnown && summary.InputTokens > 0
                ? $"{summary.CacheHitRate * 100.0:F1}%"
                : Loc.Tr("contextUsage.cache.unknown", "未知");

            var costText = summary.CostUsd > 0
                ? $" · ≈${summary.CostUsd:F2}{(summary.CostPartial ? "+" : "")}"
                : (summary.CostPartial ? " · " + Loc.Tr("contextUsage.cost.unknown", "费用未知") : string.Empty);

            return Loc.Tr(
                "contextUsage.session.summary",
                "{0} 次 · 输入 {1}（缓存命中 {2}）· 输出 {3}{4}",
                summary.CallCount,
                FormatTokens(summary.InputTokens),
                cacheText,
                FormatTokens(summary.OutputTokens),
                costText);
        }

        /// <summary>紧凑 token 数格式：1234 → 1.2K，1234567 → 1.2M，1234567890 → 1.2B。</summary>
        public static string FormatTokens(long n)
        {
            if (n >= 1_000_000_000L) return $"{n / 1_000_000_000.0:F1}B";
            if (n >= 1_000_000L) return $"{n / 1_000_000.0:F1}M";
            if (n >= 1_000L) return $"{n / 1_000.0:F1}K";
            return n.ToString();
        }

        /// <summary>测试隔离钩子：注入临时存储路径与可控时钟，并从注入路径重新水合，绝不读写用户真实目录。</summary>
        internal static void ConfigureForTesting(Func<string> pathProvider, Func<DateTime> localNowProvider)
        {
            lock (Lock)
            {
                _isTesting = true;
                UsageHistoryStore.FilePathProvider = pathProvider;
                UsageHistoryStore.LocalNowProvider = localNowProvider;
                UsageHistoryStore.ResetTrackingForTesting();
                _hydrated = false;
                EnsureHydratedLocked();
            }
        }

        /// <summary>测试清理钩子：还原默认路径与时钟提供器，下次访问时按需懒加载。</summary>
        internal static void ResetTestingOverrides()
        {
            lock (Lock)
            {
                _isTesting = false;
                UsageHistoryStore.FilePathProvider = null;
                UsageHistoryStore.LocalNowProvider = null;
                UsageHistoryStore.ResetTrackingForTesting();
                _history = UsageHistoryStore.CreateEmpty();
                _session = NewEmpty();
                _last = null;
                _sessionCostUsd = 0;
                _sessionCostPartial = false;
                _hydrated = false;
            }
        }

        private static void EnsureHydratedLocked()
        {
            if (_hydrated)
                return;

            _history = UsageHistoryStore.Load();
            SyncSessionFieldsFromHistoryLocked();
            _hydrated = true;
        }

        private static void ResetSessionCountersLocked(bool preserveSessionId)
        {
            var sessionId = preserveSessionId ? _history.CurrentSession?.SessionId : null;
            _history.CurrentSession = new UsageSessionSnapshot
            {
                SessionId = sessionId,
                Model = null,
                Totals = new UsageAggregate(),
                Models = new List<UsageModelBucket>(),
                LastCall = null,
                HasLastCall = false,
                UpdatedAtUtc = DateTime.UtcNow.ToString("o"),
            };
            SyncSessionFieldsFromHistoryLocked();
        }

        private static void SyncSessionFieldsFromHistoryLocked()
        {
            var cur = _history.CurrentSession;
            var totals = cur?.Totals;
            if (totals == null || totals.CallCount == 0)
            {
                _session = NewEmpty();
                _last = null;
                _sessionCostUsd = 0;
                _sessionCostPartial = false;
                return;
            }

            _session = new LlmUsage
            {
                InputUncached = ClampToInt(totals.UncachedInputTokens),
                CacheRead = ClampToInt(totals.CacheReadTokens),
                CacheWrite = ClampToInt(totals.CacheCreationTokens),
                Output = ClampToInt(totals.OutputTokens),
                Reasoning = ClampToInt(totals.ReasoningTokens),
                Model = cur.Model,
                Calls = ClampToInt(totals.CallCount),
                CacheMetricsKnown = totals.CacheMetricsKnown,
            };
            _last = cur.HasLastCall && cur.LastCall != null ? cur.LastCall.ToLlmUsage() : null;
            _sessionCostUsd = totals.CostUsd;
            _sessionCostPartial = totals.CostPartial;
        }

        private static UsagePeriodSummary BuildPeriodSummary(
            UsageWindow window,
            int windowDays,
            int activeDays,
            UsageAggregate totals,
            IReadOnlyList<UsageModelBreakdown> models)
        {
            totals ??= new UsageAggregate();
            return new UsagePeriodSummary
            {
                Window = window,
                WindowDays = windowDays,
                ActiveDays = activeDays,
                CallCount = totals.CallCount,
                InputTokens = totals.InputTokens,
                OutputTokens = totals.OutputTokens,
                CacheReadTokens = totals.CacheReadTokens,
                CacheCreationTokens = totals.CacheCreationTokens,
                ReasoningTokens = totals.ReasoningTokens,
                TotalTokens = totals.TotalTokens,
                CostUsd = totals.CostUsd,
                CostPartial = totals.CostPartial,
                CacheMetricsKnown = totals.CacheMetricsKnown,
                Models = models ?? Array.Empty<UsageModelBreakdown>(),
            };
        }

        private static IReadOnlyList<UsageModelBreakdown> BuildModelBreakdowns(IEnumerable<UsageModelBucket> buckets)
        {
            if (buckets == null)
                return Array.Empty<UsageModelBreakdown>();

            var list = new List<UsageModelBreakdown>();
            foreach (var bucket in buckets)
            {
                if (bucket == null || bucket.Totals == null)
                    continue;
                if (bucket.Totals.CallCount == 0 && bucket.Totals.TotalTokens == 0 && bucket.Totals.CostUsd == 0)
                    continue;

                list.Add(new UsageModelBreakdown
                {
                    Model = string.IsNullOrWhiteSpace(bucket.Model) ? "unknown" : bucket.Model,
                    CallCount = bucket.Totals.CallCount,
                    InputTokens = bucket.Totals.InputTokens,
                    OutputTokens = bucket.Totals.OutputTokens,
                    CacheReadTokens = bucket.Totals.CacheReadTokens,
                    CacheCreationTokens = bucket.Totals.CacheCreationTokens,
                    ReasoningTokens = bucket.Totals.ReasoningTokens,
                    TotalTokens = bucket.Totals.TotalTokens,
                    CostUsd = bucket.Totals.CostUsd,
                    CostPartial = bucket.Totals.CostPartial,
                    CacheMetricsKnown = bucket.Totals.CacheMetricsKnown,
                });
            }

            return list
                .OrderByDescending(m => m.TotalTokens)
                .ThenByDescending(m => m.CostUsd)
                .ThenBy(m => m.Model, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static int ClampToInt(long value)
        {
            if (value <= 0) return 0;
            return value >= int.MaxValue ? int.MaxValue : (int)value;
        }

        private static void RaiseUsageRecorded(LlmUsage usage)
        {
            try { OnUsageRecorded?.Invoke(usage); }
            catch (Exception ex) { AgentCoreLog.Warning($"[AgentCore] UsageLedger listener failed: {ex.Message}"); }
        }

        private static void RaiseChanged()
        {
            try { OnChanged?.Invoke(); }
            catch (Exception ex) { AgentCoreLog.Warning($"[AgentCore] UsageLedger OnChanged listener failed: {ex.Message}"); }
        }

        private static LlmUsage NewEmpty() => new LlmUsage { Calls = 0 };
    }
}
