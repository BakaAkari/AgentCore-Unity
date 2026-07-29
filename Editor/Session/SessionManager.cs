using System;
using System.Collections.Generic;
using AgentCore.Editor.Core;
using AgentCore.Editor.LLM;
using UnityEngine;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Session
{
    /// <summary>
    /// 会话生命周期管理器 — 协调 AgentLoop 和 SessionStorage。
    /// <para>
    /// 单例模式，负责：
    /// <list type="bullet">
    ///   <item>管理当前活动会话 ID</item>
    ///   <item>创建、加载、保存、删除会话</item>
    ///   <item>自动保存（节流：最多每 30 秒保存一次）</item>
    ///   <item>记录上一次活动会话 ID（用于窗口重新打开时恢复）</item>
    /// </list>
    /// </para>
    /// </summary>
    public class SessionManager
    {
        #region 常量

        /// <summary>日志前缀</summary>
        private const string LogPrefix = "[AgentCore] SessionManager: ";

        /// <summary>自动保存最小间隔（秒）</summary>
        private const double AutoSaveIntervalSeconds = 30.0;

        /// <summary>EditorPrefs 中存储上一次活动会话 ID 的 key</summary>
        private const string LastSessionIdKey = "AgentCore_LastSessionId";

        #endregion

        #region 单例

        /// <summary>
        /// 全局唯一的会话管理器实例。
        /// </summary>
        public static SessionManager Instance { get; } = new SessionManager();

        /// <summary>
        /// 私有构造函数，防止外部实例化。
        /// </summary>
        private SessionManager() { }

        #endregion

        #region 公开属性

        /// <summary>
        /// 当前活动会话 ID。
        /// </summary>
        public string CurrentSessionId { get; private set; }

        /// <summary>
        /// 当前内存中会话的标题。
        /// 用于 UI 精准更新标题文本，避免重建整个列表。
        /// </summary>
        public string CurrentSessionTitle => _currentSession?.Title;

        #endregion

        #region 私有字段

        /// <summary>当前会话数据（内存缓存）</summary>
        private SessionData _currentSession;

        /// <summary>上次自动保存时间</summary>
        private DateTime _lastAutoSaveTime = DateTime.MinValue;

        /// <summary>
        /// 标记当前会话内容是否发生了实质性变化（新增消息、标题变更等）。
        /// 仅在 _isDirty 为 true 时，保存操作才会更新 UpdatedAt 时间戳。
        /// 这避免了"切换会话时保存旧会话导致其 UpdatedAt 被刷新、排序跳变"的问题。
        /// </summary>
        private bool _isDirty;

        #endregion

        #region 公开方法

        /// <summary>
        /// 创建新会话。
        /// 生成新的 GUID 作为会话 ID，并记录到 EditorPrefs。
        /// </summary>
        public void CreateNewSession()
        {
            var sessionId = Guid.NewGuid().ToString();
            CurrentSessionId = sessionId;

            _currentSession = new SessionData
            {
                Id = sessionId,
                Title = SessionData.DefaultTitle,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                MessageCount = 0
            };

            _isDirty = false;

            // 立即持久化到磁盘，确保会话在列表中可见
            SessionStorage.Save(_currentSession);

            // 记录当前活动会话 ID
            SaveLastSessionId(sessionId);

            AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}New session created: {sessionId}");
        }

        /// <summary>
        /// 标记当前会话内容已发生实质性变化。
        /// 调用此方法后，下一次 SaveCurrentSession / ForceSave / AutoSave 会更新 UpdatedAt。
        /// <para>
        /// 应在以下场景调用：
        /// <list type="bullet">
        ///   <item>用户发送新消息</item>
        ///   <item>AI 回复完成</item>
        ///   <item>会话标题变更</item>
        /// </list>
        /// </para>
        /// </summary>
        public void MarkDirty()
        {
            _isDirty = true;
        }

        /// <summary>
        /// 保存当前会话。
        /// 从 AgentLoop 的运行时数据创建 SessionData 并写入磁盘。
        /// <para>
        /// 只有当 <see cref="_isDirty"/> 为 true 时才会更新 UpdatedAt 时间戳。
        /// 这避免了切换会话时保存旧会话导致其排序跳变的问题。
        /// </para>
        /// </summary>
        /// <param name="messages">LLM 消息历史</param>
        /// <param name="turns">UI 对话轮次</param>
        /// <param name="compressionMetrics">压缩统计数据（会话级别）</param>
        public void SaveCurrentSession(List<ChatMessage> messages, List<ConversationTurn> turns, Core.Compression.CompressionMetrics compressionMetrics = null)
        {
            if (string.IsNullOrEmpty(CurrentSessionId))
            {
                AgentCoreLog.Warning($"{LogPrefix}No active session to save.");
                return;
            }

            try
            {
                _currentSession = SessionData.FromAgentLoop(messages, turns, compressionMetrics, _currentSession, updateTimestamp: _isDirty);
                SessionStorage.Save(_currentSession);
                _lastAutoSaveTime = DateTime.UtcNow;

                // 保存完成后重置脏标志
                _isDirty = false;
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"{LogPrefix}Failed to save current session: {ex.Message}");
            }
        }

        /// <summary>
        /// 加载指定会话。
        /// </summary>
        /// <param name="sessionId">会话 ID</param>
        /// <returns>会话数据，加载失败时返回 null</returns>
        public SessionData LoadSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                AgentCoreLog.Warning($"{LogPrefix}Cannot load session with empty Id.");
                return null;
            }

            var session = SessionStorage.Load(sessionId);
            if (session != null)
            {
                CurrentSessionId = sessionId;
                _currentSession = session;
                _isDirty = false; // 刚加载的会话没有未保存的变更
                SaveLastSessionId(sessionId);
                AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Session loaded and set as active: {sessionId}");
            }

            return session;
        }

        /// <summary>
        /// 异步加载指定会话并设为当前活动会话（#1 CRITICAL 性能修复的传染层）。
        /// <para>
        /// 文件读取与反序列化经 <see cref="SessionStorage.LoadAsync"/> 移出主线程，
        /// 供 UI 热路径（<c>ChatWindow.SwitchToSession</c>）调用以消除切换会话时的卡顿。
        /// await 续体回到主线程后，设置活动会话状态与 EditorPrefs 均在主线程完成。
        /// </para>
        /// 同步 <see cref="LoadSession"/> 保留，供窗口恢复等同步上下文兜底。
        /// </summary>
        /// <param name="sessionId">会话 ID</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>会话数据，加载失败时返回 null</returns>
        public async Task<SessionData> LoadSessionAsync(string sessionId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                AgentCoreLog.Warning($"{LogPrefix}Cannot load session with empty Id.");
                return null;
            }

            var session = await SessionStorage.LoadAsync(sessionId, ct);
            if (session != null)
            {
                CurrentSessionId = sessionId;
                _currentSession = session;
                _isDirty = false; // 刚加载的会话没有未保存的变更
                SaveLastSessionId(sessionId);
                AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Session loaded and set as active (async): {sessionId}");
            }

            return session;
        }

        /// <summary>
        /// 获取会话列表。
        /// </summary>
        /// <returns>会话摘要列表，按最后更新时间降序排列</returns>
        public List<SessionSummary> GetSessionList()
        {
            return SessionStorage.ListSessions();
        }

        /// <summary>
        /// 异步获取会话列表（#8 HIGH 性能修复的传染层）。
        /// <para>
        /// 经 <see cref="SessionStorage.ListSessionsAsync"/> 把全量会话文件的读取与解析移出主线程，
        /// 侧边栏刷新时主线程不再阻塞。
        /// </para>
        /// </summary>
        /// <param name="ct">取消令牌</param>
        /// <returns>会话摘要列表，按最后更新时间降序排列</returns>
        public Task<List<SessionSummary>> GetSessionListAsync(CancellationToken ct = default)
        {
            return SessionStorage.ListSessionsAsync(ct);
        }

        /// <summary>
        /// 重命名会话。
        /// 修改会话标题并保存到磁盘。
        /// </summary>
        /// <param name="sessionId">会话 ID</param>
        /// <param name="newTitle">新标题</param>
        /// <param name="manuallySet">
        /// 是否为用户手动重命名。true 时置 <see cref="SessionData.TitleManuallySet"/>=true，
        /// 供后续自动命名逻辑跳过（尊重用户意图）。
        /// 自动命名 / 后台补名等非手动路径应保持默认 false，不改变该标记。
        /// </param>
        /// <returns>是否重命名成功</returns>
        public bool RenameSession(string sessionId, string newTitle, bool manuallySet = false)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                AgentCoreLog.Warning($"{LogPrefix}Cannot rename session with empty Id.");
                return false;
            }

            if (string.IsNullOrEmpty(newTitle))
            {
                AgentCoreLog.Warning($"{LogPrefix}Cannot rename session with empty title.");
                return false;
            }

            try
            {
                var session = SessionStorage.Load(sessionId);
                if (session == null)
                {
                    AgentCoreLog.Warning($"{LogPrefix}Session not found for rename: {sessionId}");
                    return false;
                }

                session.Title = newTitle.Trim();
                session.UpdatedAt = DateTime.UtcNow;
                if (manuallySet)
                {
                    session.TitleManuallySet = true;
                }
                SessionStorage.Save(session);

                // 如果是当前活动会话，同步更新内存缓存
                if (CurrentSessionId == sessionId && _currentSession != null)
                {
                    _currentSession.Title = session.Title;
                    if (manuallySet)
                    {
                        _currentSession.TitleManuallySet = true;
                    }
                }

                AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Session renamed: {sessionId} -> \"{newTitle}\"");
                return true;
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"{LogPrefix}Failed to rename session {sessionId}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 设置会话的 tag（单 tag）。传入 null 或空字符串表示清除 tag（回到未分类）。
        /// </summary>
        /// <param name="sessionId">会话 ID</param>
        /// <param name="tag">tag 文本；null / 空字符串 = 清除</param>
        /// <returns>是否成功</returns>
        public bool SetSessionTag(string sessionId, string tag)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                AgentCoreLog.Warning($"{LogPrefix}Cannot set tag on session with empty Id.");
                return false;
            }

            try
            {
                var session = SessionStorage.Load(sessionId);
                if (session == null)
                {
                    AgentCoreLog.Warning($"{LogPrefix}Session not found for set tag: {sessionId}");
                    return false;
                }

                // null / 空字符串归一化为 null（未分类）
                var normalized = string.IsNullOrWhiteSpace(tag) ? null : tag.Trim();
                session.Tag = normalized;
                SessionStorage.Save(session);

                if (CurrentSessionId == sessionId && _currentSession != null)
                {
                    _currentSession.Tag = normalized;
                }

                AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Session tag set: {sessionId} -> {(normalized ?? "<none>")}");
                return true;
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"{LogPrefix}Failed to set tag on session {sessionId}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 设置会话归档状态。
        /// </summary>
        /// <param name="sessionId">会话 ID</param>
        /// <param name="archived">true=归档，false=取消归档</param>
        /// <returns>是否成功</returns>
        public bool SetSessionArchived(string sessionId, bool archived)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                AgentCoreLog.Warning($"{LogPrefix}Cannot set archived on session with empty Id.");
                return false;
            }

            try
            {
                var session = SessionStorage.Load(sessionId);
                if (session == null)
                {
                    AgentCoreLog.Warning($"{LogPrefix}Session not found for set archived: {sessionId}");
                    return false;
                }

                session.Archived = archived;
                SessionStorage.Save(session);

                if (CurrentSessionId == sessionId && _currentSession != null)
                {
                    _currentSession.Archived = archived;
                }

                AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Session archived set: {sessionId} -> {archived}");
                return true;
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"{LogPrefix}Failed to set archived on session {sessionId}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 设置会话的"标题已手动设置"标记。
        /// true 时自动命名逻辑应跳过该会话，尊重用户意图。
        /// </summary>
        /// <param name="sessionId">会话 ID</param>
        /// <param name="manually">是否手动设置过标题</param>
        /// <returns>是否成功</returns>
        public bool MarkTitleManuallySet(string sessionId, bool manually)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                AgentCoreLog.Warning($"{LogPrefix}Cannot mark title-manually-set on session with empty Id.");
                return false;
            }

            try
            {
                var session = SessionStorage.Load(sessionId);
                if (session == null)
                {
                    AgentCoreLog.Warning($"{LogPrefix}Session not found for mark title-manually-set: {sessionId}");
                    return false;
                }

                session.TitleManuallySet = manually;
                SessionStorage.Save(session);

                if (CurrentSessionId == sessionId && _currentSession != null)
                {
                    _currentSession.TitleManuallySet = manually;
                }

                AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Session title-manually-set: {sessionId} -> {manually}");
                return true;
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"{LogPrefix}Failed to mark title-manually-set on session {sessionId}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 删除会话。
        /// 如果删除的是当前活动会话，则清除当前会话状态。
        /// </summary>
        /// <param name="sessionId">要删除的会话 ID</param>
        public void DeleteSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                AgentCoreLog.Warning($"{LogPrefix}Cannot delete session with empty Id.");
                return;
            }

            SessionStorage.Delete(sessionId);

            // 如果删除的是当前活动会话，清除状态
            if (CurrentSessionId == sessionId)
            {
                CurrentSessionId = null;
                _currentSession = null;
                _isDirty = false;
                AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Active session was deleted, cleared current session state.");
            }
        }

        /// <summary>
        /// 自动保存（节流：最多每 30 秒保存一次）。
        /// 在每次 AI 回复完成后调用。
        /// </summary>
        /// <param name="messages">LLM 消息历史</param>
        /// <param name="turns">UI 对话轮次</param>
        /// <param name="compressionMetrics">压缩统计数据（会话级别）</param>
        public void AutoSave(List<ChatMessage> messages, List<ConversationTurn> turns, Core.Compression.CompressionMetrics compressionMetrics = null)
        {
            if (string.IsNullOrEmpty(CurrentSessionId))
            {
                return;
            }

            // 节流检查
            var elapsed = (DateTime.UtcNow - _lastAutoSaveTime).TotalSeconds;
            if (elapsed < AutoSaveIntervalSeconds)
            {
                return;
            }

            SaveCurrentSession(messages, turns, compressionMetrics);
            AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Auto-saved session: {CurrentSessionId}");
        }

        /// <summary>
        /// 强制保存当前会话（忽略节流限制）。
        /// 用于窗口关闭、对话重置等关键时刻。
        /// </summary>
        /// <param name="messages">LLM 消息历史</param>
        /// <param name="turns">UI 对话轮次</param>
        /// <param name="compressionMetrics">压缩统计数据（会话级别）</param>
        public void ForceSave(List<ChatMessage> messages, List<ConversationTurn> turns, Core.Compression.CompressionMetrics compressionMetrics = null)
        {
            if (string.IsNullOrEmpty(CurrentSessionId))
            {
                return;
            }

            // 跳过没有用户消息的空会话（仅含 system prompt），避免产生幽灵会话文件
            bool hasUserMessages = messages != null && messages.Exists(m => m.Role == "user");
            if (!hasUserMessages)
            {
                AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix} ForceSave skipped: no user messages in session {CurrentSessionId}");
                return;
            }

            SaveCurrentSession(messages, turns, compressionMetrics);
        }

        /// <summary>
        /// 获取上一次活动会话的 ID。
        /// 用于窗口重新打开时恢复上一次的会话。
        /// </summary>
        /// <returns>上一次活动会话 ID，如果没有则返回 null</returns>
        public string GetLastSessionId()
        {
            var id = UnityEditor.EditorPrefs.GetString(LastSessionIdKey, "");
            return string.IsNullOrEmpty(id) ? null : id;
        }

        /// <summary>
        /// 尝试恢复上一次的会话。
        /// </summary>
        /// <returns>恢复的会话数据，如果没有可恢复的会话则返回 null</returns>
        public SessionData TryRestoreLastSession()
        {
            var lastId = GetLastSessionId();
            if (string.IsNullOrEmpty(lastId))
            {
                AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}No previous session to restore.");
                return null;
            }

            var session = LoadSession(lastId);
            if (session != null)
            {
                AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Restored last session: {lastId} ({session.Title})");
            }
            else
            {
                AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Failed to restore last session: {lastId}, file may have been deleted.");
            }

            return session;
        }

        /// <summary>
        /// 触发当前会话的自动记忆提取（fire-and-forget）。
        /// 在会话切换、对话重置、窗口关闭等场景调用。
        /// 如果当前没有活动会话或 mem0 未启用，则静默跳过。
        /// </summary>
        /// <param name="llmClient">LLM 客户端（用于提取摘要）</param>
        public void TriggerAutoMemory(ILLMClient llmClient)
        {
            if (_currentSession == null || llmClient == null)
            {
                return;
            }

            try
            {
                AutoMemoryStrategy.TriggerAsync(_currentSession, llmClient);
            }
            catch (Exception ex)
            {
                // 静默处理 — 自动记忆失败不应影响正常功能
                AgentCoreLog.Warning($"{LogPrefix}Failed to trigger auto-memory: {ex.Message}");
            }
        }

        /// <summary>
        /// 触发指定会话数据的自动记忆提取（fire-and-forget）。
        /// 用于在保存会话后、切换前触发。
        /// </summary>
        /// <param name="session">要提取记忆的会话数据</param>
        /// <param name="llmClient">LLM 客户端</param>
        public void TriggerAutoMemory(SessionData session, ILLMClient llmClient)
        {
            if (session == null || llmClient == null)
            {
                return;
            }

            try
            {
                AutoMemoryStrategy.TriggerAsync(session, llmClient);
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"{LogPrefix}Failed to trigger auto-memory: {ex.Message}");
            }
        }

        #endregion

        #region 私有方法

        /// <summary>
        /// 将当前活动会话 ID 保存到 EditorPrefs。
        /// </summary>
        /// <param name="sessionId">会话 ID</param>
        private static void SaveLastSessionId(string sessionId)
        {
            UnityEditor.EditorPrefs.SetString(LastSessionIdKey, sessionId ?? "");
        }

        #endregion
    }
}
