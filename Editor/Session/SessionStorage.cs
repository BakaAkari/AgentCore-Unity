using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Session
{
    /// <summary>
    /// 会话存储管理器 — 负责会话数据的 JSON 文件读写。
    /// <para>
    /// 存储路径：Library/AgentCore/sessions/（相对于 Unity 项目根目录）。
    /// 该目录位于 Library/ 下，不进版本控制。
    /// </para>
    /// <para>
    /// 线程模型：会话目录在 <c>[InitializeOnLoad]</c> 时于主线程缓存，之后任意线程都可安全取路径。
    /// <see cref="SaveInBackground"/> 把 JSON 序列化 + 原子落盘移到后台；所有写入（同步/后台）共用一把写锁并按
    /// 递增序号比较，旧序号的写入不会覆盖新序号。读取前先等待同一会话尚未落盘的后台写入，避免读到旧内容。
    /// 域重载 / 退出前会等待在途写入完成。
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    public static class SessionStorage
    {
        #region 常量

        /// <summary>日志前缀</summary>
        private const string LogPrefix = "[AgentCore] SessionStorage: ";

        /// <summary>JSON 序列化设置</summary>
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            DateFormatString = "yyyy-MM-ddTHH:mm:ss.fffZ",
            DateTimeZoneHandling = DateTimeZoneHandling.Utc
        };

        /// <summary>主线程缓存的会话目录（Application.dataPath 只能在主线程访问）。</summary>
        private static string _sessionDirectory;

        /// <summary>所有会话文件写入共用的锁（同步 Save 与后台写入串行化）。</summary>
        private static readonly object WriteLock = new object();

        /// <summary>全局递增的保存序号；每次 Save / SaveInBackground 在调用线程上领取。</summary>
        private static long _saveSequence;

        /// <summary>每个会话已落盘的最大序号（受 <see cref="WriteLock"/> 保护）。</summary>
        private static readonly Dictionary<string, long> WrittenSequence = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        /// <summary>每个会话最近一次后台写入的 Task。</summary>
        private static readonly ConcurrentDictionary<string, Task> PendingWrites = new ConcurrentDictionary<string, Task>(StringComparer.OrdinalIgnoreCase);

        /// <summary>域重载 / 退出前等待在途写入的最长时间。</summary>
        private const int FlushTimeoutMs = 5000;

        static SessionStorage()
        {
            // [InitializeOnLoad] 保证在主线程执行：此时缓存目录，之后后台线程也能取到
            _sessionDirectory = BuildSessionDirectory();
            AssemblyReloadEvents.beforeAssemblyReload -= FlushPendingWrites;
            AssemblyReloadEvents.beforeAssemblyReload += FlushPendingWrites;
            EditorApplication.quitting -= FlushPendingWrites;
            EditorApplication.quitting += FlushPendingWrites;
        }

        #endregion

        #region 公开方法

        /// <summary>
        /// 获取会话存储目录的绝对路径。任意线程可调用（路径已在主线程缓存）。
        /// </summary>
        /// <returns>会话存储目录路径</returns>
        public static string GetSessionDirectory()
        {
            return _sessionDirectory ??= BuildSessionDirectory();
        }

        private static string BuildSessionDirectory()
        {
            // Application.dataPath = "{ProjectRoot}/Assets"
            // 我们需要 "{ProjectRoot}/Library/AgentCore/sessions/"
            return Path.Combine(Application.dataPath, "..", "Library", "AgentCore", "sessions");
        }

        /// <summary>
        /// 将会话保存为 JSON 文件。
        /// 文件名格式：{session.Id}.json
        /// </summary>
        /// <param name="session">要保存的会话数据</param>
        public static void Save(SessionData session)
        {
            if (session == null)
            {
                AgentCoreLog.Error($"{LogPrefix}Cannot save null session.");
                return;
            }

            if (string.IsNullOrEmpty(session.Id))
            {
                AgentCoreLog.Error($"{LogPrefix}Cannot save session with empty Id.");
                return;
            }

            try
            {
                WriteCore(session, Interlocked.Increment(ref _saveSequence));
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"{LogPrefix}Failed to save session {session.Id}: {ex.Message}");
            }
        }

        /// <summary>
        /// 在后台线程序列化并落盘会话（用于每轮结束的自动保存等高频路径，避免大 JSON 序列化 + 落盘卡主线程）。
        /// <para>
        /// 调用方须传入不再被修改的快照（如 <see cref="SessionData.FromAgentLoop"/> 新建的对象）。
        /// 后台序列化若因共享子对象被并发修改而失败，会回到主线程用同一序号重试一次。
        /// 序号在调用时领取：之后的同步 <see cref="Save"/> 一定覆盖它，它也不会覆盖之后的写入。
        /// </para>
        /// </summary>
        public static void SaveInBackground(SessionData session)
        {
            if (session == null || string.IsNullOrEmpty(session.Id))
            {
                Save(session); // 走同步路径输出同样的错误日志
                return;
            }

            long seq = Interlocked.Increment(ref _saveSequence);
            var id = session.Id;
            // 确保目录缓存已在主线程完成
            GetSessionDirectory();

            var task = Task.Run(() =>
            {
                try
                {
                    WriteCore(session, seq);
                }
                catch (Exception ex)
                {
                    AgentCoreLog.Warning($"{LogPrefix}Background save of {id} failed ({ex.Message}), retrying on main thread.");
                    MainThread.Post(() =>
                    {
                        try
                        {
                            WriteCore(session, seq);
                        }
                        catch (Exception retryEx)
                        {
                            AgentCoreLog.Error($"{LogPrefix}Failed to save session {id}: {retryEx.Message}");
                        }
                    });
                }
            });
            PendingWrites[id] = task;
            // 只移除仍是自己的条目（之后的新写入已替换时保留新条目）
            task.ContinueWith(t => ((ICollection<KeyValuePair<string, Task>>)PendingWrites).Remove(new KeyValuePair<string, Task>(id, t)),
                TaskScheduler.Default);
        }

        /// <summary>
        /// 序列化并原子写入；序号不新于已落盘版本时直接跳过（保证"后保存者胜出"）。
        /// </summary>
        private static void WriteCore(SessionData session, long seq)
        {
            lock (WriteLock)
            {
                if (WrittenSequence.TryGetValue(session.Id, out var written) && written >= seq)
                    return;

                var directory = GetSessionDirectory();
                EnsureDirectoryExists(directory);

                var filePath = Path.Combine(directory, $"{session.Id}.json");
                var json = JsonConvert.SerializeObject(session, JsonSettings);
                AtomicFile.WriteAllText(filePath, json, backupPath: filePath + BackupSuffix);
                WrittenSequence[session.Id] = seq;
            }

            AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Session saved: {session.Id} ({session.Title})");
        }

        /// <summary>同步等待指定会话的在途后台写入（读文件前调用，避免读到旧内容）。</summary>
        private static void WaitPendingWrite(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            if (PendingWrites.TryGetValue(sessionId, out var task) && !task.IsCompleted)
            {
                try { task.Wait(FlushTimeoutMs); } catch (Exception) { /* 写入错误已在任务内记录 */ }
            }
        }

        /// <summary>异步等待指定会话的在途后台写入。</summary>
        private static async Task WaitPendingWriteAsync(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            if (PendingWrites.TryGetValue(sessionId, out var task) && !task.IsCompleted)
            {
                try { await task; } catch (Exception) { /* 写入错误已在任务内记录 */ }
            }
        }

        /// <summary>同步等待全部在途后台写入（列表 / 检索 / 域重载 / 退出前）。</summary>
        private static void WaitAllPendingWrites()
        {
            var tasks = PendingWrites.Values.Where(t => !t.IsCompleted).ToArray();
            if (tasks.Length == 0) return;
            try { Task.WaitAll(tasks, FlushTimeoutMs); } catch (Exception) { /* 写入错误已在任务内记录 */ }
        }

        /// <summary>域重载 / 编辑器退出前：等待在途后台写入落盘，避免最后一次自动保存丢失。</summary>
        private static void FlushPendingWrites()
        {
            WaitAllPendingWrites();
        }

        /// <summary>上一份完好会话的备份后缀（由 AtomicFile 在替换时保留）。不匹配 *.json，不会出现在会话列表里。</summary>
        private const string BackupSuffix = ".bak";

        /// <summary>
        /// 会话文件损坏时：把坏文件改名保留为 <c>*.corrupted-时间戳</c>，再从 <c>.bak</c> 恢复。
        /// 绝不静默丢弃——无备份时坏文件仍在磁盘上，日志给出位置。
        /// </summary>
        private static SessionData RecoverFromBackup(string sessionId, string filePath, string reason)
        {
            var corruptedPath = $"{filePath}.corrupted-{DateTime.Now:yyyyMMdd-HHmmss}";
            try
            {
                File.Move(filePath, corruptedPath);
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"{LogPrefix}Could not move corrupted session file aside: {ex.Message}");
                corruptedPath = filePath;
            }

            var backupPath = filePath + BackupSuffix;
            if (!File.Exists(backupPath))
            {
                AgentCoreLog.Error($"{LogPrefix}Session {sessionId} is corrupted ({reason}) and has no backup. The damaged file was kept at {corruptedPath}.");
                return null;
            }

            try
            {
                var json = File.ReadAllText(backupPath);
                var session = JsonConvert.DeserializeObject<SessionData>(json, JsonSettings);
                if (session == null)
                    throw new InvalidDataException("backup is empty");

                AtomicFile.WriteAllText(filePath, json);
                AgentCoreLog.Warning($"{LogPrefix}Session {sessionId} was corrupted ({reason}); restored the previous save from backup. " +
                                     $"Changes after that save are lost. The damaged file was kept at {corruptedPath}.");
                return session;
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"{LogPrefix}Session {sessionId} is corrupted ({reason}) and its backup could not be loaded ({ex.Message}). " +
                                   $"The damaged file was kept at {corruptedPath}.");
                return null;
            }
        }

        /// <summary>
        /// 加载指定会话。
        /// </summary>
        /// <param name="sessionId">会话 ID</param>
        /// <returns>会话数据，加载失败时返回 null</returns>
        public static SessionData Load(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                AgentCoreLog.Error($"{LogPrefix}Cannot load session with empty Id.");
                return null;
            }

            try
            {
                WaitPendingWrite(sessionId);
                var filePath = Path.Combine(GetSessionDirectory(), $"{sessionId}.json");

                if (!File.Exists(filePath))
                {
                    AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Session file not found: {filePath}");
                    return null;
                }

                SessionData session;
                try
                {
                    var json = File.ReadAllText(filePath);
                    session = JsonConvert.DeserializeObject<SessionData>(json, JsonSettings);
                }
                catch (JsonException ex)
                {
                    return RecoverFromBackup(sessionId, filePath, ex.Message);
                }

                if (session == null)
                    return RecoverFromBackup(sessionId, filePath, "file is empty");

                AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Session loaded: {sessionId} ({session.Title}, {session.MessageCount} messages)");
                return session;
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"{LogPrefix}Failed to load session {sessionId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 异步加载指定会话（#1 CRITICAL 性能修复）。
        /// <para>
        /// 文件读取与 JSON 反序列化都放到后台线程执行 —— 切换会话时会话文件可达数 MB，
        /// 同步 <see cref="Load"/> 的 <c>File.ReadAllText</c> + <c>JsonConvert.Deserialize</c>
        /// 会阻塞 Unity 主线程 50-200ms 造成明显卡顿。此方法把这两步都移出主线程，
        /// await 续体回到主线程后调用方即可安全操作 UI。
        /// </para>
        /// 同步 <see cref="Load"/> 保留作为兼容 fallback，供构造/重命名/打 tag 等非热路径同步上下文调用。
        /// </summary>
        /// <param name="sessionId">会话 ID</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>会话数据，加载失败时返回 null</returns>
        public static async Task<SessionData> LoadAsync(string sessionId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                AgentCoreLog.Error($"{LogPrefix}Cannot load session with empty Id.");
                return null;
            }

            try
            {
                await WaitPendingWriteAsync(sessionId);
                var filePath = Path.Combine(GetSessionDirectory(), $"{sessionId}.json");

                if (!File.Exists(filePath))
                {
                    AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Session file not found: {filePath}");
                    return null;
                }

                // 读取（异步 I/O）+ 反序列化（Task.Run 后台线程）都不占用主线程
                var json = await File.ReadAllTextAsync(filePath, ct);
                SessionData session;
                try
                {
                    session = await Task.Run(
                        () => JsonConvert.DeserializeObject<SessionData>(json, JsonSettings), ct);
                }
                catch (JsonException ex)
                {
                    return RecoverFromBackup(sessionId, filePath, ex.Message);
                }

                if (session == null)
                    return RecoverFromBackup(sessionId, filePath, "file is empty");

                AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Session loaded (async): {sessionId} ({session.Title}, {session.MessageCount} messages)");
                return session;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"{LogPrefix}Failed to load session {sessionId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 列出所有已保存的会话摘要，按 UpdatedAt 降序排列。
        /// </summary>
        /// <returns>会话摘要列表</returns>
        public static List<SessionSummary> ListSessions()
        {
            var summaries = new List<SessionSummary>();

            try
            {
                WaitAllPendingWrites();
                var directory = GetSessionDirectory();

                if (!Directory.Exists(directory))
                {
                    return summaries;
                }

                var files = Directory.GetFiles(directory, "*.json");

                // P2-2 fix: 使用 JObject 轻量级解析，只读取摘要字段，避免反序列化完整消息列表
                foreach (var file in files)
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var summary = ParseSummary(json);
                        if (summary != null)
                        {
                            summaries.Add(summary);
                        }
                    }
                    catch (Exception ex)
                    {
                        AgentCoreLog.Warning($"{LogPrefix}Failed to parse session file {Path.GetFileName(file)}: {ex.Message}");
                    }
                }

                // 按 UpdatedAt 降序排列（最近更新的在前）
                summaries.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"{LogPrefix}Failed to list sessions: {ex.Message}");
            }

            return summaries;
        }

        /// <summary>
        /// 异步列出所有已保存的会话摘要，按 UpdatedAt 降序排列（#8 HIGH 性能修复）。
        /// <para>
        /// 侧边栏刷新原本在主线程逐个 <c>File.ReadAllText</c> + <c>JObject.Parse</c> 全量会话文件。
        /// 摘要所需的 <c>tag</c> / <c>archived</c> / <c>message_count</c> 字段在 JSON 中位于
        /// <c>messages</c> 数组之后（尾部），无法只流式读头部，因此改为：文件读取用异步 I/O，
        /// 解析集中放到一个后台 <see cref="Task.Run"/>，主线程全程不阻塞。
        /// </para>
        /// </summary>
        /// <param name="ct">取消令牌</param>
        /// <returns>会话摘要列表</returns>
        public static async Task<List<SessionSummary>> ListSessionsAsync(CancellationToken ct = default)
        {
            try
            {
                foreach (var pending in PendingWrites.Values.ToArray())
                {
                    try { await pending; } catch (Exception) { /* 写入错误已在任务内记录 */ }
                }

                var directory = GetSessionDirectory();

                if (!Directory.Exists(directory))
                {
                    return new List<SessionSummary>();
                }

                var files = Directory.GetFiles(directory, "*.json");

                // 1. 异步读取全部文件内容（每次 await 期间主线程空闲）
                var contents = new List<(string file, string json)>(files.Length);
                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var json = await File.ReadAllTextAsync(file, ct);
                        contents.Add((file, json));
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        AgentCoreLog.Warning($"{LogPrefix}Failed to read session file {Path.GetFileName(file)}: {ex.Message}");
                    }
                }

                // 2. 后台线程集中解析 + 排序（JObject.Parse 会解析整份 JSON，属 CPU 密集，移出主线程）
                return await Task.Run(() =>
                {
                    var summaries = new List<SessionSummary>(contents.Count);
                    foreach (var (file, json) in contents)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            var summary = ParseSummary(json);
                            if (summary != null)
                            {
                                summaries.Add(summary);
                            }
                        }
                        catch (Exception ex)
                        {
                            AgentCoreLog.Warning($"{LogPrefix}Failed to parse session file {Path.GetFileName(file)}: {ex.Message}");
                        }
                    }

                    summaries.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
                    return summaries;
                }, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"{LogPrefix}Failed to list sessions: {ex.Message}");
                return new List<SessionSummary>();
            }
        }

        /// <summary>
        /// 从会话 JSON 文本解析出轻量级摘要（只读取摘要字段，不反序列化完整消息列表）。
        /// 供同步 <see cref="ListSessions"/> 与异步 <see cref="ListSessionsAsync"/> 共用。
        /// </summary>
        /// <param name="json">会话文件 JSON 文本</param>
        /// <returns>会话摘要；当 id 为空时返回 null</returns>
        private static SessionSummary ParseSummary(string json)
        {
            var obj = JObject.Parse(json);

            var id = obj.Value<string>("id");
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            return new SessionSummary
            {
                Id = id,
                Title = obj.Value<string>("title") ?? "未命名会话",
                UpdatedAt = obj.Value<DateTime?>("updated_at") ?? DateTime.MinValue,
                CreatedAt = obj.Value<DateTime?>("created_at") ?? DateTime.MinValue,
                MessageCount = obj.Value<int?>("message_count") ?? 0,
                Tag = obj.Value<string>("tag"),
                Archived = obj.Value<bool?>("archived") ?? false,
                TitleManuallySet = obj.Value<bool?>("title_manually_set") ?? false
            };
        }

        /// <summary>
        /// 删除指定会话文件。
        /// </summary>
        /// <param name="sessionId">要删除的会话 ID</param>
        public static void Delete(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                AgentCoreLog.Error($"{LogPrefix}Cannot delete session with empty Id.");
                return;
            }

            try
            {
                WaitPendingWrite(sessionId);
                var filePath = Path.Combine(GetSessionDirectory(), $"{sessionId}.json");

                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    if (File.Exists(filePath + BackupSuffix))
                        File.Delete(filePath + BackupSuffix);
                    AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Session deleted: {sessionId}");
                }
                else
                {
                    AgentCoreLog.Warning($"{LogPrefix}Session file not found for deletion: {sessionId}");
                }
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"{LogPrefix}Failed to delete session {sessionId}: {ex.Message}");
            }
        }

        /// <summary>
        /// 跨会话全文检索（v1.14.9+ Session tag 互通）。
        /// <para>
        /// 设计要点（对应 plans/session-search-tags.md 讨论结论）：
        /// 1. <b>强制按 tag scope</b>——不支持无 tag 的全局搜索，从设计上堵住"越搜越大"失控；
        /// 2. <b>返回片段（snippet），不是整份消息</b>——避免匹配到的 session 内容直接顶爆调用方 token 预算；
        /// 3. <b>扫描文件数硬顶</b> <paramref name="maxScanFiles"/>——tag 下 session 很多时只扫最近
        ///    更新的那部分，不是无限扫描；命中数硬顶 <paramref name="limit"/>；
        /// 4. 排除 <paramref name="excludeSessionId"/>（通常是调用方自己的当前会话），
        ///    检索"其他"会话没有意义搜自己正在进行的对话。
        /// </para>
        /// <para>
        /// 本方法只做只读文本匹配，不反序列化完整 <see cref="SessionData"/> 消息对象树——
        /// 直接在原始 JSON 文本上做大小写不敏感的子串匹配，兼顾简单和到几百个 session 文件的性能。
        /// </para>
        /// </summary>
        /// <param name="tag">会话 tag（必填，大小写不敏感精确匹配）。</param>
        /// <param name="query">检索关键词（必填，大小写不敏感子串匹配）。</param>
        /// <param name="limit">最多返回的命中结果数（含所有 session 汇总）。</param>
        /// <param name="maxScanFiles">最多扫描的 session 文件数（按 UpdatedAt 降序，即最近更新的优先）。</param>
        /// <param name="excludeSessionId">排除的会话 ID（通常是调用方自己），可为 null。</param>
        /// <returns>检索结果（含是否被截断的提示信息）。</returns>
        public static SessionSearchResult SearchSessions(
            string tag,
            string query,
            int limit,
            int maxScanFiles,
            string excludeSessionId = null)
        {
            var result = new SessionSearchResult();

            if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(query))
            {
                return result;
            }

            try
            {
                WaitAllPendingWrites();
                var directory = GetSessionDirectory();
                if (!Directory.Exists(directory))
                {
                    return result;
                }

                var files = Directory.GetFiles(directory, "*.json");

                // 1. 先只解析摘要字段筛出 tag 匹配的 session，按 UpdatedAt 降序排列，
                //    再按 maxScanFiles 截断——保证"扫描哪些文件"这一步本身就是有界的。
                var candidates = new List<(string file, SessionSummary summary)>();
                foreach (var file in files)
                {
                    try
                    {
                        var sessionId = Path.GetFileNameWithoutExtension(file);
                        if (!string.IsNullOrEmpty(excludeSessionId) &&
                            string.Equals(sessionId, excludeSessionId, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var json = File.ReadAllText(file);
                        var summary = ParseSummary(json);
                        if (summary == null) continue;
                        if (string.IsNullOrEmpty(summary.Tag) ||
                            !string.Equals(summary.Tag, tag, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        candidates.Add((file, summary));
                    }
                    catch (Exception ex)
                    {
                        AgentCoreLog.Warning($"{LogPrefix}Failed to parse session file for search {Path.GetFileName(file)}: {ex.Message}");
                    }
                }

                candidates.Sort((a, b) => b.summary.UpdatedAt.CompareTo(a.summary.UpdatedAt));
                result.MatchedTagSessionCount = candidates.Count;

                bool scanTruncated = candidates.Count > maxScanFiles;
                if (scanTruncated)
                {
                    candidates = candidates.Take(maxScanFiles).ToList();
                }
                result.ScanTruncated = scanTruncated;
                result.ScannedSessionCount = candidates.Count;

                // 2. 逐个 session 全文匹配 Messages[].Content，产出片段级结果。
                foreach (var (file, summary) in candidates)
                {
                    if (result.Hits.Count >= limit) break;

                    SessionData session;
                    try
                    {
                        var json = File.ReadAllText(file);
                        session = JsonConvert.DeserializeObject<SessionData>(json, JsonSettings);
                    }
                    catch (Exception ex)
                    {
                        AgentCoreLog.Warning($"{LogPrefix}Failed to load session content for search {summary.Id}: {ex.Message}");
                        continue;
                    }

                    if (session?.Messages == null) continue;

                    var hitsInSession = new List<SessionSearchSnippet>();
                    foreach (var msg in session.Messages)
                    {
                        if (string.IsNullOrEmpty(msg.Content)) continue;

                        int searchFrom = 0;
                        while (true)
                        {
                            int idx = msg.Content.IndexOf(query, searchFrom, StringComparison.OrdinalIgnoreCase);
                            if (idx < 0) break;

                            hitsInSession.Add(new SessionSearchSnippet
                            {
                                Role = msg.Role,
                                Snippet = BuildSnippet(msg.Content, idx, query.Length)
                            });

                            searchFrom = idx + query.Length;
                        }
                    }

                    if (hitsInSession.Count == 0) continue;

                    result.Hits.Add(new SessionSearchHit
                    {
                        SessionId = summary.Id,
                        Title = summary.Title,
                        UpdatedAt = summary.UpdatedAt,
                        MatchCount = hitsInSession.Count,
                        // 单个 session 内的片段本身也不能无界返回——最多附 3 条代表性片段，
                        // 其余只体现在 MatchCount 里，需要更多细节时用 sessionId 精确 LoadSession。
                        Snippets = hitsInSession.Take(3).ToList()
                    });
                }

                // 命中次数降序，同分按最近更新降序 —— 让最相关 + 最新的结果排前面。
                result.Hits.Sort((a, b) =>
                {
                    int byCount = b.MatchCount.CompareTo(a.MatchCount);
                    return byCount != 0 ? byCount : b.UpdatedAt.CompareTo(a.UpdatedAt);
                });

                if (result.Hits.Count > limit)
                {
                    result.Hits = result.Hits.Take(limit).ToList();
                }
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"{LogPrefix}SearchSessions failed: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// 截取命中点前后各 <paramref name="contextChars"/> 字符构成片段，两端截断处加 "…" 提示。
        /// </summary>
        private static string BuildSnippet(string content, int matchIndex, int matchLength, int contextChars = 200)
        {
            int start = Math.Max(0, matchIndex - contextChars);
            int end = Math.Min(content.Length, matchIndex + matchLength + contextChars);

            var snippet = content.Substring(start, end - start);
            if (start > 0) snippet = "…" + snippet;
            if (end < content.Length) snippet += "…";
            return snippet;
        }

        #endregion

        #region 私有方法

        /// <summary>
        /// 确保目录存在，如果不存在则创建。
        /// </summary>
        /// <param name="directory">目录路径</param>
        private static void EnsureDirectoryExists(string directory)
        {
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Created session directory: {directory}");
            }
        }

        #endregion
    }

    /// <summary>
    /// 会话摘要 — 用于会话列表显示，不包含完整消息数据。
    /// </summary>
    [Serializable]
    public class SessionSummary
    {
        /// <summary>会话唯一标识</summary>
        [JsonProperty("id")]
        public string Id { get; set; }

        /// <summary>会话标题</summary>
        [JsonProperty("title")]
        public string Title { get; set; }

        /// <summary>最后更新时间</summary>
        [JsonProperty("updated_at")]
        public DateTime UpdatedAt { get; set; }

        /// <summary>创建时间（UTC）。旧版本 session 无该字段时为 DateTime.MinValue。</summary>
        [JsonProperty("created_at")]
        public DateTime CreatedAt { get; set; }

        /// <summary>消息总数</summary>
        [JsonProperty("message_count")]
        public int MessageCount { get; set; }

        /// <summary>用户手动打的 tag（单 tag，null=未分类）</summary>
        [JsonProperty("tag", NullValueHandling = NullValueHandling.Ignore)]
        public string Tag { get; set; }

        /// <summary>是否已归档</summary>
        [JsonProperty("archived", NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool Archived { get; set; }

        /// <summary>用户是否手动设置过标题</summary>
        [JsonProperty("title_manually_set", NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool TitleManuallySet { get; set; }
    }

    /// <summary>
    /// <see cref="SessionStorage.SearchSessions"/> 的完整返回结果。
    /// </summary>
    [Serializable]
    public class SessionSearchResult
    {
        /// <summary>命中的会话列表（片段级，已按相关度排序并截断到 limit）。</summary>
        public List<SessionSearchHit> Hits { get; set; } = new List<SessionSearchHit>();

        /// <summary>该 tag 下匹配到的会话总数（截断前）。</summary>
        public int MatchedTagSessionCount { get; set; }

        /// <summary>实际扫描的会话数（应用 maxScanFiles 截断后）。</summary>
        public int ScannedSessionCount { get; set; }

        /// <summary>是否因 maxScanFiles 限制而未扫描到该 tag 下的全部会话——调用方应据此判断是否需要缩小范围或换关键词重新搜索。</summary>
        public bool ScanTruncated { get; set; }
    }

    /// <summary>单个会话内的检索命中汇总。</summary>
    [Serializable]
    public class SessionSearchHit
    {
        public string SessionId { get; set; }
        public string Title { get; set; }
        public DateTime UpdatedAt { get; set; }

        /// <summary>该会话内命中的总次数（可能大于 Snippets.Count，因为片段数被截断到 3 条）。</summary>
        public int MatchCount { get; set; }

        /// <summary>代表性片段（最多 3 条），需要更多细节时应显式加载该 SessionId 的完整内容。</summary>
        public List<SessionSearchSnippet> Snippets { get; set; } = new List<SessionSearchSnippet>();
    }

    /// <summary>单条命中片段。</summary>
    [Serializable]
    public class SessionSearchSnippet
    {
        /// <summary>该片段所属消息的角色（user/assistant/tool）。</summary>
        public string Role { get; set; }

        /// <summary>命中点前后各 ~200 字符的上下文片段。</summary>
        public string Snippet { get; set; }
    }
}
