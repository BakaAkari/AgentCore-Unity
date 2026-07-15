using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

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
                Debug.LogError($"{LogPrefix}Cannot save null session.");
                return;
            }

            if (string.IsNullOrEmpty(session.Id))
            {
                Debug.LogError($"{LogPrefix}Cannot save session with empty Id.");
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
                Debug.LogError($"{LogPrefix}Failed to save session {session.Id}: {ex.Message}");
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
                Debug.LogError($"{LogPrefix}Cannot load session with empty Id.");
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
                    Debug.LogError($"{LogPrefix}Failed to deserialize session: {sessionId}");
                    return null;
                }

                AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Session loaded: {sessionId} ({session.Title}, {session.MessageCount} messages)");
                return session;
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogPrefix}Failed to load session {sessionId}: {ex.Message}");
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
                        var obj = JObject.Parse(json);

                        var id = obj.Value<string>("id");
                        if (!string.IsNullOrEmpty(id))
                        {
                            summaries.Add(new SessionSummary
                            {
                                Id = id,
                                Title = obj.Value<string>("title") ?? "未命名会话",
                                UpdatedAt = obj.Value<DateTime?>("updated_at") ?? DateTime.MinValue,
                                MessageCount = obj.Value<int?>("message_count") ?? 0
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"{LogPrefix}Failed to parse session file {Path.GetFileName(file)}: {ex.Message}");
                    }
                }

                // 按 UpdatedAt 降序排列（最近更新的在前）
                summaries.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogPrefix}Failed to list sessions: {ex.Message}");
            }

            return summaries;
        }

        /// <summary>
        /// 删除指定会话文件。
        /// </summary>
        /// <param name="sessionId">要删除的会话 ID</param>
        public static void Delete(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                Debug.LogError($"{LogPrefix}Cannot delete session with empty Id.");
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
                    Debug.LogWarning($"{LogPrefix}Session file not found for deletion: {sessionId}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogPrefix}Failed to delete session {sessionId}: {ex.Message}");
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
    }
}
