using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Session
{
    /// <summary>
    /// 会话存储管理器 — 会话持久化的静态门面，实际读写由 <see cref="SessionLogStore"/> 完成。
    /// <para>
    /// 存储路径：Library/AgentCore/sessions/（相对于 Unity 项目根目录）。
    /// 该目录位于 Library/ 下，不进版本控制。每个会话一个 <c>&lt;id&gt;.jsonl</c> 追加式日志
    /// （格式见 <see cref="SessionLogStore"/>）；旧版 <c>&lt;id&gt;.json</c> 仍可读取，首次保存时迁移。
    /// </para>
    /// <para>
    /// 线程模型：会话目录在 <c>[InitializeOnLoad]</c> 时于主线程缓存，之后任意线程都可安全取路径。
    /// 所有保存（同步 <see cref="Save"/> / 后台 <see cref="SaveInBackground"/>）在调用线程捕获快照后进入同一个 FIFO 写入队列，
    /// 只把相对已落盘状态的增量追加到日志；读取 / 列表 / 检索 / 删除前先排空队列，避免读到旧内容。
    /// 域重载 / 退出前排空队列（最多等待 5 秒）。
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    public static class SessionStorage
    {
        #region 常量

        /// <summary>日志前缀</summary>
        private const string LogPrefix = "[AgentCore] SessionStorage: ";

        /// <summary>主线程缓存的会话目录（Application.dataPath 只能在主线程访问）。</summary>
        private static string _sessionDirectory;

        /// <summary>进程内唯一的会话存储（含唯一的后台写入队列）。</summary>
        private static SessionLogStore _store;

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

        /// <summary>进程内唯一的 <see cref="SessionLogStore"/>（首次访问时创建）。</summary>
        internal static SessionLogStore Store
        {
            get
            {
                var store = Volatile.Read(ref _store);
                if (store != null) return store;
                Interlocked.CompareExchange(ref _store, new SessionLogStore(GetSessionDirectory()), null);
                return _store;
            }
        }

        /// <summary>
        /// 同步保存会话：只把相对上次落盘的增量追加到 <c>{session.Id}.jsonl</c>（必要时整理为完整快照）。
        /// 返回时已落盘。
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
                Store.Save(session);
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"{LogPrefix}Failed to save session {session.Id}: {ex.Message}");
            }
        }

        /// <summary>
        /// 后台保存（用于每轮结束的自动保存等高频路径）。
        /// <para>
        /// 快照在调用线程（主线程）捕获：列表浅拷贝，仍可能原地变化的末尾轮次的 SelfChallenge 深拷贝；
        /// 增量比对、序列化与落盘在唯一的写入队列中进行，与同步 <see cref="Save"/> 严格按调用顺序生效。
        /// </para>
        /// </summary>
        public static void SaveInBackground(SessionData session)
        {
            if (session == null || string.IsNullOrEmpty(session.Id))
            {
                Save(session); // 走同步路径输出同样的错误日志
                return;
            }

            try
            {
                Store.SaveInBackground(session);
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"{LogPrefix}Failed to queue save of session {session.Id}: {ex.Message}");
            }
        }

        /// <summary>域重载 / 编辑器退出前：排空写入队列，避免最后一次自动保存丢失。</summary>
        private static void FlushPendingWrites()
        {
            try
            {
                Volatile.Read(ref _store)?.Flush(FlushTimeoutMs);
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"{LogPrefix}Flushing pending session writes failed: {ex.Message}");
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
                var session = Store.Load(sessionId);
                if (session == null)
                    AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Session file not found or unreadable: {sessionId}");
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
        /// 排空写入队列、读文件、重放日志都放到后台线程执行 —— 切换会话时会话文件可达数 MB，
        /// 同步 <see cref="Load"/> 会阻塞 Unity 主线程造成明显卡顿。await 续体回到主线程后调用方即可安全操作 UI。
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
                var store = Store;
                var session = await Task.Run(() => store.Load(sessionId), ct);
                if (session == null)
                    AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Session file not found or unreadable: {sessionId}");
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
        /// jsonl 会话只读文件尾部的最后一条元数据记录，不重放整份历史。
        /// </summary>
        /// <returns>会话摘要列表</returns>
        public static List<SessionSummary> ListSessions()
        {
            try
            {
                var summaries = Store.ListSummaries();
                summaries.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
                return summaries;
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"{LogPrefix}Failed to list sessions: {ex.Message}");
                return new List<SessionSummary>();
            }
        }

        /// <summary>
        /// 异步列出所有已保存的会话摘要，按 UpdatedAt 降序排列（#8 HIGH 性能修复）。
        /// 文件读取与解析全部在后台线程进行，主线程不阻塞。
        /// </summary>
        /// <param name="ct">取消令牌</param>
        /// <returns>会话摘要列表</returns>
        public static async Task<List<SessionSummary>> ListSessionsAsync(CancellationToken ct = default)
        {
            try
            {
                var store = Store;
                return await Task.Run(() =>
                {
                    var summaries = store.ListSummaries(ct);
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
        /// 删除指定会话的全部文件（jsonl、备份、旧版 JSON 及其迁移副本）。
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
                if (Store.Delete(sessionId))
                    AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Session deleted: {sessionId}");
                else
                    AgentCoreLog.Warning($"{LogPrefix}Session file not found for deletion: {sessionId}");
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
                var store = Store;

                // 1. 先只读摘要（jsonl 只读文件尾部的元数据）筛出 tag 匹配的 session，按 UpdatedAt 降序排列，
                //    再按 maxScanFiles 截断——保证"扫描哪些文件"这一步本身就是有界的。
                var candidates = new List<SessionSummary>();
                foreach (var summary in store.ListSummaries())
                {
                    if (!string.IsNullOrEmpty(excludeSessionId) &&
                        string.Equals(summary.Id, excludeSessionId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (string.IsNullOrEmpty(summary.Tag) ||
                        !string.Equals(summary.Tag, tag, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    candidates.Add(summary);
                }

                candidates.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
                result.MatchedTagSessionCount = candidates.Count;

                bool scanTruncated = candidates.Count > maxScanFiles;
                if (scanTruncated)
                {
                    candidates = candidates.Take(maxScanFiles).ToList();
                }
                result.ScanTruncated = scanTruncated;
                result.ScannedSessionCount = candidates.Count;

                // 2. 逐个 session 全文匹配 Messages[].Content，产出片段级结果。
                foreach (var summary in candidates)
                {
                    if (result.Hits.Count >= limit) break;

                    SessionData session;
                    try
                    {
                        session = store.ReadForSearch(summary.Id);
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
