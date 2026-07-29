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
}
