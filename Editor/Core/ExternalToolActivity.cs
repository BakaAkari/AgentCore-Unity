using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.Core
{
    /// <summary>外部工具活动（MCP）统计时间窗口。</summary>
    public enum ActivityWindow
    {
        Today = 1,
        Days7 = 7,
        Days30 = 30,
        Days90 = 90,
    }

    /// <summary>指定时间窗口内的外部工具调用聚合统计。</summary>
    public sealed class ActivityWindowStats
    {
        public ActivityWindow Window { get; set; }
        public int TotalCalls { get; set; }
        public int SucceededCalls { get; set; }
        public int FailedCalls { get; set; }
        public double SuccessRate { get; set; }
        public long TotalDurationMs { get; set; }
        public double AverageDurationMs { get; set; }
        public string TopToolName { get; set; } = string.Empty;
        public int TopToolCalls { get; set; }
    }

    /// <summary>
    /// 外部调用来源（MCP 客户端等）的工具调用记录：Chat 窗口的活动条与“外部活动”窗口据此显示外部 agent 正在做什么、做过什么。
    /// 线程安全；<see cref="Changed"/> 可能在任意线程触发，UI 订阅方需自行切回主线程。
    /// 内存中保留最近 <see cref="Capacity"/> 条，并会在首次访问时自动从 <c>Library/AgentCore/Mcp/audit.1.jsonl</c>
    /// 与 <c>audit.jsonl</c> 水合历史记录（跨 Domain Reload 与 Editor 重启保留）。
    /// </summary>
    public static class ExternalToolActivity
    {
        public const int Capacity = 200;
        private const int RetentionDays = 90;
        private const double DeduplicationToleranceSeconds = 2.0;

        public sealed class Record
        {
            public string Id;
            public string Source;
            public string Tool;
            public string Action;
            public string ArgumentsSummary;
            public DateTime StartedAt;
            public DateTime? FinishedAt;

            /// <summary>running / ok / partial / error / cancelled</summary>
            public string Status = "running";
            public string ResultPreview;

            public bool IsRunning => FinishedAt == null;

            public double ElapsedSeconds => ((FinishedAt ?? GetLocalNow()) - StartedAt).TotalSeconds;
        }

        private sealed class ActivityEntry
        {
            public string RecordId;
            public string Source;
            public string Tool;
            public string Action;
            public string ArgumentsSummary;
            public string Status;
            public string ResultPreview;
            public DateTime StartedAt;
            public DateTime FinishedAt;
            public double ElapsedMs;
            public bool FromAuditLog;

            public Record ToRecord() => new Record
            {
                Id = RecordId,
                Source = Source,
                Tool = Tool,
                Action = Action,
                ArgumentsSummary = ArgumentsSummary,
                StartedAt = StartedAt,
                FinishedAt = FinishedAt,
                Status = Status,
                ResultPreview = ResultPreview,
            };
        }

        private static readonly string DefaultMcpDirectory =
            Path.Combine(Path.GetFullPath(Directory.GetCurrentDirectory()), "Library", "AgentCore", "Mcp");

        private static readonly object Gate = new object();
        private static readonly LinkedList<Record> Records = new LinkedList<Record>();
        private static readonly List<ActivityEntry> AuditHistory = new List<ActivityEntry>();
        private static readonly List<ActivityEntry> LiveCompleted = new List<ActivityEntry>();
        private static readonly HashSet<string> LiveCompletedIds = new HashSet<string>(StringComparer.Ordinal);

        private static bool _hydrated;
        private static Func<string[]> _auditFilesProvider;
        private static Func<DateTime> _localNowProvider;

        /// <summary>有调用开始、结束、清空或重载（任意线程）。</summary>
        public static event Action Changed;

        public static Record Begin(string source, string tool, string action, string argumentsSummary)
        {
            var now = GetLocalNow();
            var record = new Record
            {
                Id = Guid.NewGuid().ToString("N"),
                Source = string.IsNullOrEmpty(source) ? "external client" : source,
                Tool = tool,
                Action = action,
                ArgumentsSummary = argumentsSummary,
                StartedAt = now,
            };
            lock (Gate)
            {
                EnsureHydratedLocked();
                Records.AddFirst(record);
                while (Records.Count > Capacity)
                    Records.RemoveLast();
            }
            Raise();
            return record;
        }

        public static void End(Record record, string status, string resultPreview)
        {
            if (record == null)
                return;

            var now = GetLocalNow();
            lock (Gate)
            {
                record.Status = string.IsNullOrEmpty(status) ? "ok" : status;
                record.ResultPreview = resultPreview;
                record.FinishedAt = now;

                var id = record.Id ?? Guid.NewGuid().ToString("N");
                if (LiveCompletedIds.Add(id))
                {
                    var elapsedMs = Math.Max(0.0, (now - record.StartedAt).TotalMilliseconds);
                    LiveCompleted.Add(new ActivityEntry
                    {
                        RecordId = id,
                        Source = string.IsNullOrEmpty(record.Source) ? "external client" : record.Source,
                        Tool = record.Tool,
                        Action = record.Action,
                        ArgumentsSummary = record.ArgumentsSummary,
                        Status = record.Status,
                        ResultPreview = record.ResultPreview,
                        StartedAt = record.StartedAt,
                        FinishedAt = now,
                        ElapsedMs = elapsedMs,
                        FromAuditLog = false,
                    });
                    PruneLiveCompletedLocked(now.Date);
                }
            }
            Raise();
        }

        /// <summary>最近的记录（新的在前）。</summary>
        public static IReadOnlyList<Record> Snapshot()
        {
            lock (Gate)
            {
                EnsureHydratedLocked();
                return Records.ToList();
            }
        }

        public static IReadOnlyList<Record> Running()
        {
            lock (Gate)
            {
                EnsureHydratedLocked();
                return Records.Where(r => r.IsRunning).ToList();
            }
        }

        /// <summary>
        /// 清空内存中的活动列表与缓存统计，但<strong>不删除</strong>磁盘上的 <c>audit.jsonl</c>。
        /// 在当前 AppDomain 内保持清空状态（不会再次自动水合），除非显式调用 <see cref="ReloadFromAuditLog"/>。
        /// </summary>
        public static void Clear()
        {
            lock (Gate)
            {
                _hydrated = true;
                Records.Clear();
                AuditHistory.Clear();
                LiveCompleted.Clear();
                LiveCompletedIds.Clear();
            }
            Raise();
        }

        /// <summary>
        /// 强制从审计日志文件（<c>audit.1.jsonl</c> + <c>audit.jsonl</c>）重新加载最近记录与 90 天统计历史。
        /// </summary>
        public static void ReloadFromAuditLog()
        {
            lock (Gate)
            {
                LoadFromAuditFilesLocked(preserveRunningRecords: true);
                _hydrated = true;
            }
            Raise();
        }

        /// <summary>
        /// 按日历窗口汇总外部工具调用统计（<c>Today</c> = 今日，<c>Days7</c> = 近 7 日，
        /// <c>Days30</c> = 近 30 日，<c>Days90</c> = 近 90 日），合并审计日志与内存中新完成的记录并去重。
        /// </summary>
        public static ActivityWindowStats GetWindowStats(ActivityWindow window)
        {
            var stats = new ActivityWindowStats
            {
                Window = window,
                TopToolName = string.Empty,
            };

            List<ActivityEntry> combined;
            DateTime today;
            lock (Gate)
            {
                EnsureHydratedLocked();
                today = GetLocalNow().Date;
                combined = CombineAndDeduplicateLocked();
            }

            var windowDays = Math.Max(1, (int)window);
            var startDate = today.AddDays(-(windowDays - 1));

            double totalDurationMs = 0.0;
            var toolCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var toolDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < combined.Count; i++)
            {
                var entry = combined[i];
                var entryDate = entry.FinishedAt.Date;
                if (entryDate < startDate || entryDate > today)
                    continue;

                stats.TotalCalls++;
                if (IsSuccessStatus(entry.Status))
                    stats.SucceededCalls++;
                else
                    stats.FailedCalls++;

                totalDurationMs += Math.Max(0.0, entry.ElapsedMs);

                if (!string.IsNullOrWhiteSpace(entry.Tool))
                {
                    var key = entry.Tool.Trim();
                    if (!toolDisplayNames.ContainsKey(key))
                        toolDisplayNames[key] = key;
                    toolCounts[key] = toolCounts.TryGetValue(key, out var count) ? count + 1 : 1;
                }
            }

            if (stats.TotalCalls > 0)
            {
                stats.SuccessRate = (double)stats.SucceededCalls / stats.TotalCalls;
                stats.TotalDurationMs = (long)Math.Round(totalDurationMs);
                stats.AverageDurationMs = totalDurationMs / stats.TotalCalls;
            }

            string topKey = null;
            var topCount = 0;
            foreach (var kvp in toolCounts)
            {
                if (kvp.Value > topCount ||
                    (kvp.Value == topCount && topKey != null &&
                     string.Compare(kvp.Key, topKey, StringComparison.OrdinalIgnoreCase) < 0))
                {
                    topKey = kvp.Key;
                    topCount = kvp.Value;
                }
            }

            if (topKey != null)
            {
                stats.TopToolName = toolDisplayNames[topKey];
                stats.TopToolCalls = topCount;
            }

            return stats;
        }

        /// <summary>测试隔离钩子：注入临时审计文件列表与可控时钟，重置水合状态，绝不触碰真实 <c>Library/AgentCore/Mcp/audit.jsonl</c>。</summary>
        internal static void ConfigureForTesting(Func<string[]> auditFilesProvider, Func<DateTime> localNowProvider)
        {
            lock (Gate)
            {
                _auditFilesProvider = auditFilesProvider;
                _localNowProvider = localNowProvider;
                Records.Clear();
                AuditHistory.Clear();
                LiveCompleted.Clear();
                LiveCompletedIds.Clear();
                _hydrated = false;
            }
        }

        /// <summary>测试清理钩子：还原默认审计文件路径与系统时钟，清空测试态内存缓存。</summary>
        internal static void ResetTestingOverrides()
        {
            lock (Gate)
            {
                _auditFilesProvider = null;
                _localNowProvider = null;
                Records.Clear();
                AuditHistory.Clear();
                LiveCompleted.Clear();
                LiveCompletedIds.Clear();
                _hydrated = false;
            }
        }

        private static DateTime GetLocalNow()
        {
            var provider = _localNowProvider;
            return provider != null ? provider() : DateTime.Now;
        }

        private static string[] GetAuditFiles()
        {
            var provider = _auditFilesProvider;
            if (provider != null)
                return provider() ?? Array.Empty<string>();

            return new[]
            {
                Path.Combine(DefaultMcpDirectory, "audit.1.jsonl"),
                Path.Combine(DefaultMcpDirectory, "audit.jsonl"),
            };
        }

        private static void EnsureHydratedLocked()
        {
            if (_hydrated)
                return;

            LoadFromAuditFilesLocked(preserveRunningRecords: true);
            _hydrated = true;
        }

        private static void LoadFromAuditFilesLocked(bool preserveRunningRecords)
        {
            var running = preserveRunningRecords
                ? Records.Where(r => r != null && r.IsRunning).ToList()
                : new List<Record>();

            var parsedAll = ReadAllAuditEntries();
            var cutoffDate = GetLocalNow().Date.AddDays(-(RetentionDays - 1));

            AuditHistory.Clear();
            for (var i = 0; i < parsedAll.Count; i++)
            {
                if (parsedAll[i].FinishedAt.Date >= cutoffDate)
                    AuditHistory.Add(parsedAll[i]);
            }

            // 合并未出现在审计文件中的 live 完成记录，去重后按最新在前重建内存 Snapshot（最多 Capacity 条）
            var unmatchedLive = GetUnmatchedLiveEntriesLocked(parsedAll);

            var completedForSnapshot = new List<ActivityEntry>(parsedAll.Count + unmatchedLive.Count);
            completedForSnapshot.AddRange(parsedAll);
            completedForSnapshot.AddRange(unmatchedLive);

            // parsedAll 原序为从旧到新；为了在时间相同时保持文件靠后的条目排在更前面，带原始序号倒序排序
            var orderedCompleted = completedForSnapshot
                .Select((entry, index) => new { entry, index })
                .OrderByDescending(x => x.entry.FinishedAt)
                .ThenByDescending(x => x.index)
                .Take(Math.Max(0, Capacity - running.Count))
                .Select(x => x.entry.ToRecord())
                .ToList();

            Records.Clear();
            for (var i = 0; i < running.Count && Records.Count < Capacity; i++)
                Records.AddLast(running[i]);
            for (var i = 0; i < orderedCompleted.Count && Records.Count < Capacity; i++)
                Records.AddLast(orderedCompleted[i]);
        }

        private static List<ActivityEntry> ReadAllAuditEntries()
        {
            var result = new List<ActivityEntry>();
            string[] files;
            try
            {
                files = GetAuditFiles();
            }
            catch
            {
                return result;
            }

            if (files == null || files.Length == 0)
                return result;

            var seq = 0;
            for (var f = 0; f < files.Length; f++)
            {
                var path = files[f];
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    continue;

                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        if (TryParseAuditLine(line, seq, out var entry))
                        {
                            result.Add(entry);
                            seq++;
                        }
                    }
                }
                catch
                {
                    // 读取单个审计文件失败时跳过，不影响其余文件或调用流程
                }
            }

            return result;
        }

        private static bool TryParseAuditLine(string line, int sequence, out ActivityEntry entry)
        {
            entry = null;
            JObject obj;
            try
            {
                using var sr = new StringReader(line);
                using var jr = new JsonTextReader(sr) { DateParseHandling = DateParseHandling.None };
                obj = JObject.Load(jr);
            }
            catch
            {
                return false;
            }

            var timeToken = obj["time"];
            if (timeToken == null || timeToken.Type == JTokenType.Null)
                return false;

            if (!TryParseTimestamp(timeToken, out var finishedAt))
                return false;

            var tool = ReadString(obj, "tool");
            if (string.IsNullOrWhiteSpace(tool))
                return false;

            var source = ReadString(obj, "client");
            if (string.IsNullOrEmpty(source))
                source = ReadString(obj, "source");
            if (string.IsNullOrEmpty(source))
                source = "external client";

            var action = ReadString(obj, "action");
            var arguments = ReadString(obj, "arguments");
            var status = ReadString(obj, "status");
            if (string.IsNullOrEmpty(status))
                status = "ok";

            var elapsedMs = ReadDouble(obj, "elapsedMs");
            if (elapsedMs < 0 || double.IsNaN(elapsedMs) || double.IsInfinity(elapsedMs))
                elapsedMs = 0;

            var error = ReadString(obj, "error");
            if (error == null)
                error = ReadString(obj, "resultPreview");

            var startedAt = elapsedMs > 0 ? finishedAt.AddMilliseconds(-elapsedMs) : finishedAt;

            entry = new ActivityEntry
            {
                RecordId = $"audit-{finishedAt.Ticks}-{sequence}",
                Source = source,
                Tool = tool,
                Action = action,
                ArgumentsSummary = arguments,
                Status = status,
                ResultPreview = error,
                StartedAt = startedAt,
                FinishedAt = finishedAt,
                ElapsedMs = elapsedMs,
                FromAuditLog = true,
            };
            return true;
        }

        private static bool TryParseTimestamp(JToken token, out DateTime localTime)
        {
            localTime = default;
            if (token == null || token.Type == JTokenType.Null)
                return false;

            if (token.Type == JTokenType.Date)
            {
                var rawDate = token.Value<DateTime>();
                localTime = rawDate.Kind == DateTimeKind.Utc ? rawDate.ToLocalTime() : rawDate;
                return true;
            }

            var text = token.Type == JTokenType.String ? token.Value<string>() : token.ToString();
            if (string.IsNullOrWhiteSpace(text))
                return false;

            if (DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces,
                out var dto))
            {
                localTime = dto.LocalDateTime;
                return true;
            }

            if (DateTime.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces,
                out var dt))
            {
                localTime = dt.Kind == DateTimeKind.Utc ? dt.ToLocalTime() : dt;
                return true;
            }

            return false;
        }

        private static string ReadString(JObject obj, string key)
        {
            var token = obj[key];
            if (token == null || token.Type == JTokenType.Null)
                return null;
            return token.Type == JTokenType.String ? token.Value<string>() : token.ToString();
        }

        private static double ReadDouble(JObject obj, string key)
        {
            var token = obj[key];
            if (token == null || token.Type == JTokenType.Null)
                return 0.0;
            try
            {
                return token.Value<double>();
            }
            catch
            {
                return double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : 0.0;
            }
        }

        /// <summary>
        /// 将审计日志条目与内存新完成的 live 条目合并，并对同一调用（同一 source/tool/action/status 且完成时间在容差内）
        /// 执行一对一去重，避免既走 <see cref="Begin"/>/<see cref="End"/> 又写 <c>McpAuditLog</c> 的调用被双计，
        /// 同时保留短时间内多次合法相同调用的真实次数。
        /// </summary>
        private static List<ActivityEntry> CombineAndDeduplicateLocked()
        {
            var unmatchedLive = GetUnmatchedLiveEntriesLocked(AuditHistory);
            var combined = new List<ActivityEntry>(AuditHistory.Count + unmatchedLive.Count);
            combined.AddRange(AuditHistory);
            combined.AddRange(unmatchedLive);
            return combined;
        }

        private static List<ActivityEntry> GetUnmatchedLiveEntriesLocked(List<ActivityEntry> auditEntries)
        {
            if (LiveCompleted.Count == 0)
                return new List<ActivityEntry>();

            if (auditEntries == null || auditEntries.Count == 0)
                return new List<ActivityEntry>(LiveCompleted);

            var consumedAudit = new bool[auditEntries.Count];
            var unmatched = new List<ActivityEntry>();

            for (var i = 0; i < LiveCompleted.Count; i++)
            {
                var live = LiveCompleted[i];
                var bestIndex = -1;
                var bestDeltaSeconds = double.MaxValue;

                for (var j = 0; j < auditEntries.Count; j++)
                {
                    if (consumedAudit[j])
                        continue;

                    var audit = auditEntries[j];
                    if (!IsSameCallSignature(live, audit))
                        continue;

                    var delta = Math.Abs((live.FinishedAt - audit.FinishedAt).TotalSeconds);
                    if (delta <= DeduplicationToleranceSeconds && delta < bestDeltaSeconds)
                    {
                        bestDeltaSeconds = delta;
                        bestIndex = j;
                    }
                }

                if (bestIndex >= 0)
                    consumedAudit[bestIndex] = true;
                else
                    unmatched.Add(live);
            }

            return unmatched;
        }

        private static bool IsSameCallSignature(ActivityEntry a, ActivityEntry b)
        {
            return string.Equals(a.Source ?? string.Empty, b.Source ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.Tool ?? string.Empty, b.Tool ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.Action ?? string.Empty, b.Action ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.Status ?? string.Empty, b.Status ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSuccessStatus(string status)
        {
            return string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "partial", StringComparison.OrdinalIgnoreCase);
        }

        private static void PruneLiveCompletedLocked(DateTime localToday)
        {
            var cutoff = localToday.Date.AddDays(-(RetentionDays - 1));
            for (var i = LiveCompleted.Count - 1; i >= 0; i--)
            {
                if (LiveCompleted[i].FinishedAt.Date < cutoff)
                {
                    LiveCompletedIds.Remove(LiveCompleted[i].RecordId);
                    LiveCompleted.RemoveAt(i);
                }
            }
        }

        private static void Raise()
        {
            try
            {
                Changed?.Invoke();
            }
            catch (Exception)
            {
                // 订阅方的异常不影响调用
            }
        }
    }
}
