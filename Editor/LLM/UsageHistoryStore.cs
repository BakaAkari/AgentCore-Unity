using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using AgentCore.Editor.Config;
using AgentCore.Editor.Utils;
using Newtonsoft.Json;

namespace AgentCore.Editor.LLM
{
    /// <summary>
    /// 持久化用量聚合值：全部 token 与调用次数使用 <see cref="long"/>，避免 90 天累计溢出 <see cref="int"/>。
    /// </summary>
    [Serializable]
    public sealed class UsageAggregate
    {
        [JsonProperty("callCount")]
        public long CallCount;

        /// <summary>总输入 token（含未缓存 + 缓存读取 + 缓存写入）。</summary>
        [JsonProperty("inputTokens")]
        public long InputTokens;

        /// <summary>总输出 token（已包含 reasoning）。</summary>
        [JsonProperty("outputTokens")]
        public long OutputTokens;

        [JsonProperty("cacheReadTokens")]
        public long CacheReadTokens;

        [JsonProperty("cacheCreationTokens")]
        public long CacheCreationTokens;

        [JsonProperty("reasoningTokens")]
        public long ReasoningTokens;

        /// <summary>总 token（<see cref="InputTokens"/> + <see cref="OutputTokens"/>）。</summary>
        [JsonProperty("totalTokens")]
        public long TotalTokens;

        [JsonProperty("costUsd")]
        public double CostUsd;

        [JsonProperty("costPartial")]
        public bool CostPartial;

        [JsonProperty("cacheMetricsKnown")]
        public bool CacheMetricsKnown;

        [JsonIgnore]
        public long UncachedInputTokens => Math.Max(0L, InputTokens - CacheReadTokens - CacheCreationTokens);

        [JsonIgnore]
        public double CacheHitRate => InputTokens > 0 ? (double)CacheReadTokens / InputTokens : 0.0;

        public void Add(LlmUsage usage, double? costUsd)
        {
            if (usage == null) return;

            long uncached = Math.Max(0, usage.InputUncached);
            long cacheRead = Math.Max(0, usage.CacheRead);
            long cacheCreation = Math.Max(0, usage.CacheWrite);
            long input = uncached + cacheRead + cacheCreation;
            long output = Math.Max(0, usage.Output);
            long reasoning = Math.Max(0, usage.Reasoning);
            long calls = Math.Max(0, usage.Calls);

            CacheMetricsKnown = CallCount == 0
                ? usage.CacheMetricsKnown
                : (CacheMetricsKnown && usage.CacheMetricsKnown);

            CallCount += calls;
            InputTokens += input;
            OutputTokens += output;
            CacheReadTokens += cacheRead;
            CacheCreationTokens += cacheCreation;
            ReasoningTokens += reasoning;
            TotalTokens += input + output;

            if (costUsd.HasValue)
                CostUsd += costUsd.Value;
            else
                CostPartial = true;
        }

        public void Add(UsageAggregate other)
        {
            if (other == null) return;
            if (other.CallCount == 0 && other.TotalTokens == 0 && other.CostUsd == 0 && !other.CostPartial)
                return;

            CacheMetricsKnown = CallCount == 0
                ? other.CacheMetricsKnown
                : (CacheMetricsKnown && other.CacheMetricsKnown);

            CallCount += other.CallCount;
            InputTokens += other.InputTokens;
            OutputTokens += other.OutputTokens;
            CacheReadTokens += other.CacheReadTokens;
            CacheCreationTokens += other.CacheCreationTokens;
            ReasoningTokens += other.ReasoningTokens;
            TotalTokens += other.TotalTokens;
            CostUsd += other.CostUsd;
            CostPartial |= other.CostPartial;
        }

        public UsageAggregate Clone() => (UsageAggregate)MemberwiseClone();
    }

    /// <summary>按模型聚合的用量桶。</summary>
    [Serializable]
    public sealed class UsageModelBucket
    {
        [JsonProperty("model")]
        public string Model;

        [JsonProperty("totals")]
        public UsageAggregate Totals = new UsageAggregate();

        public UsageModelBucket Clone() => new UsageModelBucket
        {
            Model = Model,
            Totals = Totals != null ? Totals.Clone() : new UsageAggregate(),
        };
    }

    /// <summary>按本地自然日（<c>yyyy-MM-dd</c>）聚合的每日用量桶。</summary>
    [Serializable]
    public sealed class UsageDailyBucket
    {
        [JsonProperty("date")]
        public string Date;

        [JsonProperty("totals")]
        public UsageAggregate Totals = new UsageAggregate();

        [JsonProperty("models")]
        public List<UsageModelBucket> Models = new List<UsageModelBucket>();

        public UsageDailyBucket Clone()
        {
            var copy = new UsageDailyBucket
            {
                Date = Date,
                Totals = Totals != null ? Totals.Clone() : new UsageAggregate(),
                Models = new List<UsageModelBucket>(),
            };
            if (Models != null)
            {
                for (var i = 0; i < Models.Count; i++)
                {
                    if (Models[i] != null)
                        copy.Models.Add(Models[i].Clone());
                }
            }
            return copy;
        }
    }

    /// <summary>单次 <see cref="LlmUsage"/> 的持久化快照（用于恢复最近一次调用详情）。</summary>
    [Serializable]
    public sealed class LlmUsageSnapshot
    {
        [JsonProperty("inputUncached")]
        public int InputUncached;

        [JsonProperty("cacheRead")]
        public int CacheRead;

        [JsonProperty("cacheWrite")]
        public int CacheWrite;

        [JsonProperty("output")]
        public int Output;

        [JsonProperty("reasoning")]
        public int Reasoning;

        [JsonProperty("model")]
        public string Model;

        [JsonProperty("calls")]
        public int Calls = 1;

        [JsonProperty("cacheMetricsKnown")]
        public bool CacheMetricsKnown;

        public static LlmUsageSnapshot FromLlmUsage(LlmUsage usage)
        {
            if (usage == null) return null;
            return new LlmUsageSnapshot
            {
                InputUncached = usage.InputUncached,
                CacheRead = usage.CacheRead,
                CacheWrite = usage.CacheWrite,
                Output = usage.Output,
                Reasoning = usage.Reasoning,
                Model = usage.Model,
                Calls = usage.Calls,
                CacheMetricsKnown = usage.CacheMetricsKnown,
            };
        }

        public LlmUsage ToLlmUsage() => new LlmUsage
        {
            InputUncached = InputUncached,
            CacheRead = CacheRead,
            CacheWrite = CacheWrite,
            Output = Output,
            Reasoning = Reasoning,
            Model = Model,
            Calls = Calls,
            CacheMetricsKnown = CacheMetricsKnown,
        };

        public LlmUsageSnapshot Clone() => (LlmUsageSnapshot)MemberwiseClone();
    }

    /// <summary>当前会话的持久化快照（跨 Domain Reload 与 Editor 重启保留）。</summary>
    [Serializable]
    public sealed class UsageSessionSnapshot
    {
        [JsonProperty("sessionId")]
        public string SessionId;

        [JsonProperty("model")]
        public string Model;

        [JsonProperty("totals")]
        public UsageAggregate Totals = new UsageAggregate();

        [JsonProperty("models")]
        public List<UsageModelBucket> Models = new List<UsageModelBucket>();

        [JsonProperty("lastCall")]
        public LlmUsageSnapshot LastCall;

        [JsonProperty("hasLastCall")]
        public bool HasLastCall;

        [JsonProperty("updatedAtUtc")]
        public string UpdatedAtUtc;

        public UsageSessionSnapshot Clone()
        {
            var copy = new UsageSessionSnapshot
            {
                SessionId = SessionId,
                Model = Model,
                Totals = Totals != null ? Totals.Clone() : new UsageAggregate(),
                Models = new List<UsageModelBucket>(),
                LastCall = LastCall?.Clone(),
                HasLastCall = HasLastCall,
                UpdatedAtUtc = UpdatedAtUtc,
            };
            if (Models != null)
            {
                for (var i = 0; i < Models.Count; i++)
                {
                    if (Models[i] != null)
                        copy.Models.Add(Models[i].Clone());
                }
            }
            return copy;
        }
    }

    /// <summary>
    /// <c>{PreferencesFolder}/AgentCore/UsageHistory.json</c> 根结构（schema v1）。
    /// </summary>
    [Serializable]
    public sealed class UsageHistoryData
    {
        [JsonProperty("version")]
        public int Version = UsageHistoryStore.CurrentVersion;

        [JsonProperty("updatedAtUtc")]
        public string UpdatedAtUtc;

        [JsonProperty("currentSession")]
        public UsageSessionSnapshot CurrentSession = new UsageSessionSnapshot();

        [JsonProperty("days")]
        public List<UsageDailyBucket> Days = new List<UsageDailyBucket>();

        public UsageHistoryData Clone()
        {
            var copy = new UsageHistoryData
            {
                Version = Version,
                UpdatedAtUtc = UpdatedAtUtc,
                CurrentSession = CurrentSession != null ? CurrentSession.Clone() : new UsageSessionSnapshot(),
                Days = new List<UsageDailyBucket>(),
            };
            if (Days != null)
            {
                for (var i = 0; i < Days.Count; i++)
                {
                    if (Days[i] != null)
                        copy.Days.Add(Days[i].Clone());
                }
            }
            return copy;
        }
    }

    /// <summary>
    /// 跨工程、按用户持久化的 LLM 用量历史存储：读写 <c>{PreferencesFolder}/AgentCore/UsageHistory.json</c>，
    /// 保留最近 90 个自然日（含今日），原子写入，并发安全，损坏文件自动容错恢复。
    /// </summary>
    public static class UsageHistoryStore
    {
        public const int CurrentVersion = 1;
        public const int RetentionDays = 90;

        private const int MaxIoAttempts = 10;
        private const int RetryDelayMs = 20;

        private static readonly object IoGate = new object();
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            DateParseHandling = DateParseHandling.None,
            Formatting = Formatting.Indented,
        };

        private static string _lastTrackedPath;
        private static DateTime _lastSavedWriteUtc;
        private static long _lastSavedLength = -1;

        /// <summary>测试可注入的存储路径提供器；null 表示使用默认 PreferencesFolder 路径。</summary>
        internal static Func<string> FilePathProvider;

        /// <summary>测试可注入的本地时间提供器；null 表示使用 <see cref="DateTime.Now"/>。</summary>
        internal static Func<DateTime> LocalNowProvider;

        public static string GetFilePath()
        {
            var provider = FilePathProvider;
            try
            {
                return provider != null ? provider() : PreferencesFolderPathHelper.GetUsageHistoryFilePath();
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[UsageHistoryStore] Failed to resolve file path: {ex.Message}");
                return null;
            }
        }

        public static DateTime GetLocalNow()
        {
            var provider = LocalNowProvider;
            return provider != null ? provider() : DateTime.Now;
        }

        public static string FormatLocalDate(DateTime localDate) =>
            localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        public static bool TryParseLocalDate(string dateText, out DateTime date) =>
            DateTime.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

        public static UsageHistoryData CreateEmpty() => new UsageHistoryData
        {
            Version = CurrentVersion,
            UpdatedAtUtc = DateTime.UtcNow.ToString("o"),
            CurrentSession = new UsageSessionSnapshot
            {
                Totals = new UsageAggregate(),
                Models = new List<UsageModelBucket>(),
                UpdatedAtUtc = DateTime.UtcNow.ToString("o"),
            },
            Days = new List<UsageDailyBucket>(),
        };

        /// <summary>从磁盘读取用量历史；文件缺失或损坏时安全返回空结构而不抛异常。</summary>
        public static UsageHistoryData Load()
        {
            lock (IoGate)
            {
                var path = GetFilePath();
                var data = LoadFromPathLocked(path, backupCorrupt: true);
                Prune(data, GetLocalNow().Date);
                TrackFileStampLocked(path);
                return data;
            }
        }

        /// <summary>将完整数据原子写入磁盘；失败时记录警告并返回 <c>false</c>，绝不抛出未捕获异常。</summary>
        public static bool Save(UsageHistoryData data)
        {
            if (data == null) return false;

            lock (IoGate)
            {
                var path = GetFilePath();
                if (string.IsNullOrEmpty(path))
                    return false;

                Normalize(data);
                Prune(data, GetLocalNow().Date);
                data.Version = CurrentVersion;
                data.UpdatedAtUtc = DateTime.UtcNow.ToString("o");

                var saved = WriteAtomicLocked(path, data);
                if (saved)
                    TrackFileStampLocked(path);
                return saved;
            }
        }

        /// <summary>
        /// 写入一次新增用量：若磁盘文件在本次进程上次写入之后被另一个 Unity Editor 实例修改，
        /// 则先读取最新磁盘历史再合并本次增量，避免多实例相互覆盖每日统计。
        /// </summary>
        internal static bool SaveAfterRecord(
            UsageHistoryData inMemoryState,
            string dateKey,
            string modelKey,
            LlmUsage usage,
            double? costUsd,
            DateTime localNow)
        {
            if (inMemoryState == null) return false;

            lock (IoGate)
            {
                var path = GetFilePath();
                if (string.IsNullOrEmpty(path))
                    return false;

                if (WasModifiedExternallyLocked(path))
                {
                    var diskData = LoadFromPathLocked(path, backupCorrupt: true);
                    AccumulateDailyBucket(diskData, dateKey, modelKey, usage, costUsd);
                    Prune(diskData, localNow.Date);
                    inMemoryState.Days = diskData.Days;
                }

                Normalize(inMemoryState);
                Prune(inMemoryState, localNow.Date);
                inMemoryState.Version = CurrentVersion;
                inMemoryState.UpdatedAtUtc = DateTime.UtcNow.ToString("o");

                var saved = WriteAtomicLocked(path, inMemoryState);
                if (saved)
                    TrackFileStampLocked(path);
                return saved;
            }
        }

        /// <summary>
        /// 裁剪早于最近 <see cref="RetentionDays"/> 个自然日（含 <paramref name="localToday"/>，
        /// 即保留 <c>[localToday - 89 .. localToday]</c>）的每日桶，并按日期升序排序。
        /// </summary>
        public static void Prune(UsageHistoryData data, DateTime localToday)
        {
            if (data == null) return;
            Normalize(data);

            var cutoff = localToday.Date.AddDays(-(RetentionDays - 1));
            for (var i = data.Days.Count - 1; i >= 0; i--)
            {
                var bucket = data.Days[i];
                if (bucket == null || !TryParseLocalDate(bucket.Date, out var bucketDate) || bucketDate.Date < cutoff)
                {
                    data.Days.RemoveAt(i);
                }
            }

            data.Days.Sort((a, b) => string.CompareOrdinal(a.Date, b.Date));
        }

        /// <summary>把一次调用增量累加到指定日期和模型的桶里。</summary>
        internal static void AccumulateDailyBucket(
            UsageHistoryData data,
            string dateKey,
            string modelKey,
            LlmUsage usage,
            double? costUsd)
        {
            if (data == null || string.IsNullOrEmpty(dateKey) || usage == null)
                return;

            Normalize(data);

            UsageDailyBucket dayBucket = null;
            for (var i = 0; i < data.Days.Count; i++)
            {
                if (string.Equals(data.Days[i].Date, dateKey, StringComparison.Ordinal))
                {
                    dayBucket = data.Days[i];
                    break;
                }
            }

            if (dayBucket == null)
            {
                dayBucket = new UsageDailyBucket
                {
                    Date = dateKey,
                    Totals = new UsageAggregate(),
                    Models = new List<UsageModelBucket>(),
                };
                data.Days.Add(dayBucket);
            }

            dayBucket.Totals.Add(usage, costUsd);
            AccumulateModelBucket(dayBucket.Models, modelKey, usage, costUsd);
        }

        internal static void AccumulateModelBucket(
            List<UsageModelBucket> models,
            string modelKey,
            LlmUsage usage,
            double? costUsd)
        {
            if (models == null || usage == null)
                return;

            var normalizedKey = string.IsNullOrWhiteSpace(modelKey) ? "unknown" : modelKey.Trim();
            UsageModelBucket modelBucket = null;
            for (var i = 0; i < models.Count; i++)
            {
                if (models[i] != null && string.Equals(models[i].Model, normalizedKey, StringComparison.OrdinalIgnoreCase))
                {
                    modelBucket = models[i];
                    break;
                }
            }

            if (modelBucket == null)
            {
                modelBucket = new UsageModelBucket
                {
                    Model = normalizedKey,
                    Totals = new UsageAggregate(),
                };
                models.Add(modelBucket);
            }

            modelBucket.Totals ??= new UsageAggregate();
            modelBucket.Totals.Add(usage, costUsd);
        }

        internal static void ResetTrackingForTesting()
        {
            lock (IoGate)
            {
                _lastTrackedPath = null;
                _lastSavedWriteUtc = default;
                _lastSavedLength = -1;
            }
        }

        private static UsageHistoryData LoadFromPathLocked(string path, bool backupCorrupt)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return CreateEmpty();

            string json;
            try
            {
                json = ReadAllTextWithRetryLocked(path);
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[UsageHistoryStore] Failed to read '{path}': {ex.Message}");
                return CreateEmpty();
            }

            if (string.IsNullOrWhiteSpace(json))
                return CreateEmpty();

            try
            {
                var parsed = JsonConvert.DeserializeObject<UsageHistoryData>(json, JsonSettings);
                if (parsed == null)
                {
                    AgentCoreLog.Warning($"[UsageHistoryStore] History file '{path}' deserialized to null; starting fresh.");
                    if (backupCorrupt) TryBackupCorruptFileLocked(path);
                    return CreateEmpty();
                }

                Normalize(parsed);
                return parsed;
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[UsageHistoryStore] Corrupted history file '{path}', recovering fresh state: {ex.Message}");
                if (backupCorrupt) TryBackupCorruptFileLocked(path);
                return CreateEmpty();
            }
        }

        private static string ReadAllTextWithRetryLocked(string path)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    return reader.ReadToEnd();
                }
                catch (IOException) when (attempt < MaxIoAttempts - 1)
                {
                    Thread.Sleep(RetryDelayMs);
                }
                catch (UnauthorizedAccessException) when (attempt < MaxIoAttempts - 1)
                {
                    Thread.Sleep(RetryDelayMs);
                }
            }
        }

        private static bool WriteAtomicLocked(string path, UsageHistoryData data)
        {
            string tempPath = null;
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonConvert.SerializeObject(data, JsonSettings);
                var fileName = Path.GetFileName(path);
                tempPath = string.IsNullOrEmpty(dir)
                    ? fileName + "." + Guid.NewGuid().ToString("N") + ".tmp"
                    : Path.Combine(dir, fileName + "." + Guid.NewGuid().ToString("N") + ".tmp");

                File.WriteAllText(tempPath, json, Utf8NoBom);

                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            try
                            {
                                File.Replace(tempPath, path, null);
                            }
                            catch (PlatformNotSupportedException)
                            {
                                File.Copy(tempPath, path, true);
                                File.Delete(tempPath);
                            }
                            catch (IOException) when (!File.Exists(path))
                            {
                                File.Move(tempPath, path);
                            }
                        }
                        else
                        {
                            try
                            {
                                File.Move(tempPath, path);
                            }
                            catch (IOException) when (File.Exists(path))
                            {
                                File.Replace(tempPath, path, null);
                            }
                        }
                        return true;
                    }
                    catch (IOException) when (attempt < MaxIoAttempts - 1)
                    {
                        Thread.Sleep(RetryDelayMs);
                    }
                    catch (UnauthorizedAccessException) when (attempt < MaxIoAttempts - 1)
                    {
                        Thread.Sleep(RetryDelayMs);
                    }
                }
            }
            catch (Exception ex)
            {
                // Cross-volume or locked fallback: try Copy overwrite once if temp exists
                if (!string.IsNullOrEmpty(tempPath) && File.Exists(tempPath))
                {
                    try
                    {
                        File.Copy(tempPath, path, true);
                        File.Delete(tempPath);
                        return true;
                    }
                    catch
                    {
                        // fall through to warning
                    }
                }
                AgentCoreLog.Warning($"[UsageHistoryStore] Failed to save '{path}': {ex.Message}");
                return false;
            }
            finally
            {
                if (!string.IsNullOrEmpty(tempPath) && File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { /* ignore cleanup failure */ }
                }
            }
        }

        private static void TryBackupCorruptFileLocked(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                var backup = path + ".corrupt";
                File.Copy(path, backup, true);
            }
            catch
            {
                // Best-effort backup only.
            }
        }

        private static bool WasModifiedExternallyLocked(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return false;

            try
            {
                var info = new FileInfo(path);
                if (!string.Equals(_lastTrackedPath, path, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (_lastSavedLength < 0)
                    return true;
                return info.LastWriteTimeUtc != _lastSavedWriteUtc || info.Length != _lastSavedLength;
            }
            catch
            {
                return false;
            }
        }

        private static void TrackFileStampLocked(string path)
        {
            _lastTrackedPath = path;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                _lastSavedWriteUtc = default;
                _lastSavedLength = -1;
                return;
            }

            try
            {
                var info = new FileInfo(path);
                _lastSavedWriteUtc = info.LastWriteTimeUtc;
                _lastSavedLength = info.Length;
            }
            catch
            {
                _lastSavedWriteUtc = default;
                _lastSavedLength = -1;
            }
        }

        private static void Normalize(UsageHistoryData data)
        {
            if (data.Version <= 0)
                data.Version = CurrentVersion;

            data.CurrentSession ??= new UsageSessionSnapshot();
            data.CurrentSession.Totals ??= new UsageAggregate();
            data.CurrentSession.Models ??= new List<UsageModelBucket>();
            for (var i = data.CurrentSession.Models.Count - 1; i >= 0; i--)
            {
                if (data.CurrentSession.Models[i] == null)
                    data.CurrentSession.Models.RemoveAt(i);
                else
                    data.CurrentSession.Models[i].Totals ??= new UsageAggregate();
            }

            data.Days ??= new List<UsageDailyBucket>();
            for (var i = data.Days.Count - 1; i >= 0; i--)
            {
                var day = data.Days[i];
                if (day == null)
                {
                    data.Days.RemoveAt(i);
                    continue;
                }
                day.Totals ??= new UsageAggregate();
                day.Models ??= new List<UsageModelBucket>();
                for (var j = day.Models.Count - 1; j >= 0; j--)
                {
                    if (day.Models[j] == null)
                        day.Models.RemoveAt(j);
                    else
                        day.Models[j].Totals ??= new UsageAggregate();
                }
            }
        }
    }
}
