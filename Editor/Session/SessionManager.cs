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
        /// Fork 会话：从指定 turn（含）截断源会话历史，创建一个全新会话作为延续起点。
        /// <para>
        /// 设计取舍（详见架构讨论，2026-08-05）：不做"总结后新建会话"——那需要在源会话里
        /// 再调一次 LLM，如果源会话历史本身已经损坏（tool_calls 缺 function.name 等）导致
        /// LLM 持续 400，总结路径同样会失败，用户完全无法脱身。Fork 走纯粹的"复制截断"，
        /// 不依赖 LLM 调用，任何时候都能成功。
        /// </para>
        /// <para>
        /// 边界必须按 Turn.MessageEndIndex 快照截断，不能按数组下标启发式推断——
        /// ask_user 等场景会在同一个 assistant turn 生命周期内向 messages 追加多条消息
        /// （见 ResumeFromUserInput），turn 数量与 message 数量不是线性对应关系。
        /// </para>
        /// </summary>
        /// <param name="sourceSessionId">源会话 ID。</param>
        /// <param name="forkAtTurnId">Fork 点：复制到这条 turn（含）为止的历史。</param>
        /// <returns>
        /// 新建会话的 <see cref="SessionData"/>；若源会话不存在、目标 turn 不存在，
        /// 或目标 turn 的 <c>MessageEndIndex</c> 为 -1（未记录快照，多为旧版本会话数据
        /// 或异常中断的轮次，边界不可靠）则返回 null，不冒险产生新的坏历史。
        /// </returns>
        public SessionData ForkSession(string sourceSessionId, string forkAtTurnId)
        {
            if (string.IsNullOrEmpty(sourceSessionId) || string.IsNullOrEmpty(forkAtTurnId))
            {
                AgentCoreLog.Warning($"{LogPrefix}ForkSession called with empty sourceSessionId or forkAtTurnId.");
                return null;
            }

            // 源会话若恰好是当前活动会话，用内存中的最新数据（可能比磁盘上的更新），
            // 否则从磁盘加载。避免"刚发完消息还没触发自动保存就 fork"丢最后一轮的问题。
            SessionData source;
            if (sourceSessionId == CurrentSessionId && _currentSession != null)
            {
                source = _currentSession;
            }
            else
            {
                source = SessionStorage.Load(sourceSessionId);
            }

            if (source == null)
            {
                AgentCoreLog.Warning($"{LogPrefix}ForkSession: source session not found: {sourceSessionId}");
                return null;
            }

            var turns = source.Turns;
            if (turns == null || turns.Count == 0)
            {
                AgentCoreLog.Warning($"{LogPrefix}ForkSession: source session has no turns: {sourceSessionId}");
                return null;
            }

            int turnIndex = turns.FindIndex(t => t.Id == forkAtTurnId);
            if (turnIndex < 0)
            {
                AgentCoreLog.Warning($"{LogPrefix}ForkSession: fork turn not found: {forkAtTurnId}");
                return null;
            }

            var forkTurn = turns[turnIndex];
            int messageCutoff;

            if (forkTurn.MessageEndIndex >= 0)
            {
                messageCutoff = forkTurn.MessageEndIndex;
            }
            else if (turnIndex == turns.Count - 1)
            {
                // 安全特例：没有 MessageEndIndex 快照（旧版本会话数据），但 fork 点恰好是
                // 该会话的最后一条消息 —— 这种情况下"复制到 fork 点为止"等价于"复制全部历史"，
                // 不需要知道任何中间 turn 具体占了几条 message，天然不存在边界猜测风险。
                // 中间某条历史消息就没有这个保证（会被记忆注入的 system 消息、历史压缩等打乱
                // turn↔message 的对应关系），因此仍然拒绝。
                messageCutoff = source.Messages?.Count ?? 0;
                AgentCoreLog.Info($"{LogPrefix}ForkSession: turn {forkAtTurnId} has no MessageEndIndex snapshot, " +
                    "but is the last turn in the session — forking the entire history (safe boundary).");
            }
            else
            {
                AgentCoreLog.Warning($"{LogPrefix}ForkSession: turn {forkAtTurnId} has no MessageEndIndex snapshot " +
                    "(legacy session data or turn was never marked complete) and is not the last turn — " +
                    "refusing to fork on an unreliable mid-history boundary.");
                return null;
            }

            if (source.Messages == null || messageCutoff > source.Messages.Count)
            {
                AgentCoreLog.Warning($"{LogPrefix}ForkSession: MessageEndIndex {messageCutoff} out of range " +
                    $"for source session {sourceSessionId} (messages count = {source.Messages?.Count ?? 0}).");
                return null;
            }

            // 复制消息历史：截断到 messageCutoff（不含），并顺手过滤掉这次事故教训对应的坏数据——
            // tool_calls 里 function.name 为空/null 的 assistant 消息不允许带入新会话。
            // 关键点：assistant 消息被丢弃后，紧跟着的 tool 结果消息（tool_call_id 指向被丢弃
            // 的那个 tool_call）如果原样保留，会变成"孤儿 tool 消息"——引用一个新会话历史里
            // 根本不存在的 tool_call_id，这是 OpenAI 兼容 API 同样会拒绝的另一种坏结构，等于
            // 把一种 400 换成另一种 400。用"保留合法 tool_call_id 集合"的方式联动过滤：
            // 只有当 tool 消息的 tool_call_id 确实来自一条被保留的 assistant 消息时才留下。
            var keptToolCallIds = new HashSet<string>();
            var newMessages = new List<SerializableChatMessage>();
            for (int i = 0; i < messageCutoff; i++)
            {
                var msg = source.Messages[i];

                if (msg.Role == "assistant" && msg.ToolCalls != null && msg.ToolCalls.Count > 0)
                {
                    bool hasInvalidToolCall = msg.ToolCalls.Exists(tc => string.IsNullOrEmpty(tc.Function?.Name));
                    if (hasInvalidToolCall)
                    {
                        AgentCoreLog.Warning($"{LogPrefix}ForkSession: dropped assistant message with invalid " +
                            "tool_call(s) (missing function.name) while copying history — this is the exact " +
                            "corruption pattern that causes repeated LLM 400s.");
                        continue;
                    }

                    foreach (var tc in msg.ToolCalls)
                    {
                        if (!string.IsNullOrEmpty(tc.Id)) keptToolCallIds.Add(tc.Id);
                    }
                }
                else if (msg.Role == "tool")
                {
                    // 联动过滤：tool_call_id 对应的 assistant 消息已被丢弃（或从未在保留集合里），
                    // 这条 tool 结果消息就是孤儿，必须一起丢弃，否则产生新的坏结构。
                    if (string.IsNullOrEmpty(msg.ToolCallId) || !keptToolCallIds.Contains(msg.ToolCallId))
                    {
                        AgentCoreLog.Warning($"{LogPrefix}ForkSession: dropped orphan tool-result message " +
                            $"(tool_call_id={msg.ToolCallId ?? "<null>"}) whose originating assistant tool_call " +
                            "was dropped — keeping it would create a different 400-causing structure.");
                        continue;
                    }
                }

                newMessages.Add(msg);
            }

            // 复制 UI turns：同样截断到 forkAtTurnId（含）。
            var newTurns = new List<SerializableConversationTurn>(turns.GetRange(0, turnIndex + 1));

            var newSession = new SessionData
            {
                Id = Guid.NewGuid().ToString(),
                Title = string.IsNullOrEmpty(source.Title) || source.Title == SessionData.DefaultTitle
                    ? SessionData.DefaultTitle
                    : source.Title + " (fork)",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Messages = newMessages,
                Turns = newTurns,
                MessageCount = newMessages.Count,
                // 压缩统计是源会话全生命周期的累计值，对新会话无意义，不带过去，重置为初始态。
                CompressionMetrics = null,
                Tag = null,
                Archived = false,
                TitleManuallySet = false
            };

            SessionStorage.Save(newSession);
            AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}ForkSession: created {newSession.Id} from " +
                $"{sourceSessionId} at turn {forkAtTurnId} ({newMessages.Count} messages, {newTurns.Count} turns).");

            return newSession;
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
