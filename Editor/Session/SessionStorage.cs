using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
    /// </summary>
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

        #endregion

        #region 公开方法

        /// <summary>
        /// 获取会话存储目录的绝对路径。
        /// </summary>
        /// <returns>会话存储目录路径</returns>
        public static string GetSessionDirectory()
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
                var directory = GetSessionDirectory();
                EnsureDirectoryExists(directory);

                var filePath = Path.Combine(directory, $"{session.Id}.json");
                var json = JsonConvert.SerializeObject(session, JsonSettings);
                File.WriteAllText(filePath, json);

                AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Session saved: {session.Id} ({session.Title})");
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"{LogPrefix}Failed to save session {session.Id}: {ex.Message}");
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
                var filePath = Path.Combine(GetSessionDirectory(), $"{sessionId}.json");

                if (!File.Exists(filePath))
                {
                    AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Session file not found: {filePath}");
                    return null;
                }

                var json = File.ReadAllText(filePath);
                var session = JsonConvert.DeserializeObject<SessionData>(json, JsonSettings);

                if (session == null)
                {
                    AgentCoreLog.Error($"{LogPrefix}Failed to deserialize session: {sessionId}");
                    return null;
                }

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
                var filePath = Path.Combine(GetSessionDirectory(), $"{sessionId}.json");

                if (!File.Exists(filePath))
                {
                    AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Session file not found: {filePath}");
                    return null;
                }

                // 读取（异步 I/O）+ 反序列化（Task.Run 后台线程）都不占用主线程
                var json = await File.ReadAllTextAsync(filePath, ct);
                var session = await Task.Run(
                    () => JsonConvert.DeserializeObject<SessionData>(json, JsonSettings), ct);

                if (session == null)
                {
                    AgentCoreLog.Error($"{LogPrefix}Failed to deserialize session: {sessionId}");
                    return null;
                }

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
                var filePath = Path.Combine(GetSessionDirectory(), $"{sessionId}.json");

                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
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
