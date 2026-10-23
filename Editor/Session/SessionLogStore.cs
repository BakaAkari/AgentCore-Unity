using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Core;
using AgentCore.Editor.Core.SelfChallenge;
using AgentCore.Editor.LLM;
using AgentCore.Editor.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.Session
{
    /// <summary><see cref="SessionLogStore"/> 日志级别（便于在 Unity 外测试时注入自己的日志实现）。</summary>
    public enum SessionLogLevel
    {
        Info,
        Warning,
        Error
    }

    /// <summary>
    /// 会话 JSONL 追加式存储核心（Codex-CLI rollout 风格）。不依赖 Unity API，目录由构造参数给出，可在临时目录中单测。
    /// <para>
    /// <b>磁盘格式</b>（每个会话一个 <c>&lt;id&gt;.jsonl</c>，UTF-8 无 BOM，每行一条紧凑 JSON 记录，<c>"t"</c> 为记录类型且总在最前）：
    /// <list type="bullet">
    ///   <item><c>{"t":"header","v":1,"meta":{…}}</c> —— 第 1 行，格式版本 + 会话元数据（id/标题/时间/tag/归档/压缩统计…）。</item>
    ///   <item><c>{"t":"snapshot","messages":[…],"turns":[…]}</c> —— 完整历史；整理（compaction）时紧跟 header 写出，其后再写一条 meta。</item>
    ///   <item><c>{"t":"msg","i":N,"m":{…}}</c> / <c>{"t":"msg_set","i":N,"m":{…}}</c> —— 追加 / 替换第 N 条 LLM 消息。</item>
    ///   <item><c>{"t":"turn","i":N,"d":{…}}</c> / <c>{"t":"turn_set","i":N,"d":{…}}</c> —— 追加 / 替换第 N 个 UI 轮次
    ///         （assistant 轮次在流式输出、工具结果回填、Self-Challenge 完成时会原地变化）。</item>
    ///   <item><c>{"t":"truncate","keep_messages":M,"keep_turns":T}</c> —— 只保留前 M 条消息 / 前 T 个轮次（回退 rewind 等）。</item>
    ///   <item><c>{"t":"meta","meta":{…}}</c> —— 元数据整体替换；每批非空写入的最后一行，会话列表只需读文件尾部即可拿到最新标题等字段。</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>增量判定</b>：每个会话缓存"已落盘状态"（各条消息 / 轮次的廉价指纹：字段引用 + 值比较，不做序列化）。
    /// 保存时按下标比对：新增的追加，变化的写 <c>*_set</c>，变短的先写 <c>truncate</c>；变化过多（如历史压缩改写）、
    /// 无缓存（首次保存 / 域重载后未加载过）、文件长度与缓存不符、或日志中作废字节超过一半时，整理为 header + snapshot + meta
    /// 并经 <see cref="AtomicFile"/> 原子替换（保留 <c>.bak</c>）。
    /// </para>
    /// <para>
    /// <b>线程模型</b>：快照在调用线程（主线程）捕获——列表浅拷贝，末尾 <see cref="MutableTailTurns"/> 个轮次的
    /// SelfChallenge 深拷贝（它在运行中被原地修改）；指纹比对、序列化与落盘都在唯一的写入队列中按 FIFO 执行。
    /// 后台写入由按需启动的单个工作任务消费队列；同步保存 / 读取 / 列表 / 删除在持锁后先把队列排空，保证读到最新内容。
    /// </para>
    /// <para>
    /// <b>迁移</b>：旧版 <c>&lt;id&gt;.json</c> 仍可直接加载；首次保存时写出 jsonl，成功后把旧文件改名为
    /// <c>&lt;id&gt;.json.migrated</c>（保留以便降级时手动改回，不会出现在会话列表里）。
    /// </para>
    /// </summary>
    public sealed class SessionLogStore
    {
        #region 常量

        /// <summary>当前写出的格式版本。</summary>
        public const int FormatVersion = 1;

        /// <summary>会话日志扩展名。</summary>
        public const string LogExtension = ".jsonl";

        /// <summary>旧版整份 JSON 扩展名。</summary>
        public const string LegacyExtension = ".json";

        /// <summary>原子替换时保留的上一版本后缀。</summary>
        public const string BackupSuffix = ".bak";

        /// <summary>旧版 JSON 迁移完成后的改名后缀。</summary>
        public const string MigratedSuffix = ".migrated";

        /// <summary>日志小于此大小时不因作废字节而整理。</summary>
        public const long DefaultCompactMinBytes = 64 * 1024;

        /// <summary>视为"仍可能原地变化"的末尾轮次数：其 SelfChallenge 在捕获时深拷贝并做内容比对。</summary>
        public const int MutableTailTurns = 4;

        /// <summary>原地替换记录数超过此值且超过总条目 1/4 时，直接整理而不是逐条写 *_set。</summary>
        private const int MaxSetRecordsBeforeCompact = 8;

        /// <summary>会话列表读取文件尾部的字节数（用于找最后一条 meta 记录）。</summary>
        private const int SummaryTailBytes = 64 * 1024;

        private const string TypeHeader = "header";
        private const string TypeSnapshot = "snapshot";
        private const string TypeMessage = "msg";
        private const string TypeMessageSet = "msg_set";
        private const string TypeTurn = "turn";
        private const string TypeTurnSet = "turn_set";
        private const string TypeTruncate = "truncate";
        private const string TypeMeta = "meta";

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>JSONL 行序列化设置：紧凑单行；读取时不把字符串自动解析为日期（避免内容恰好像日期时被改写）。</summary>
        internal static readonly JsonSerializerSettings LineSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.None,
            NullValueHandling = NullValueHandling.Ignore,
            DateFormatString = "yyyy-MM-ddTHH:mm:ss.fffZ",
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            DateParseHandling = DateParseHandling.None
        };

        /// <summary>旧版整份 JSON 的序列化设置（与 P1 及以前完全一致）。</summary>
        internal static readonly JsonSerializerSettings LegacySettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            DateFormatString = "yyyy-MM-ddTHH:mm:ss.fffZ",
            DateTimeZoneHandling = DateTimeZoneHandling.Utc
        };

        #endregion

        #region 字段

        private readonly string _directory;
        private readonly long _compactMinBytes;
        private readonly Action<SessionLogLevel, string> _log;

        /// <summary>写入 / 状态缓存 / 读取共用的锁。</summary>
        private readonly object _gate = new object();

        /// <summary>唯一的写入队列（FIFO）。</summary>
        private readonly ConcurrentQueue<WriteRequest> _queue = new ConcurrentQueue<WriteRequest>();

        /// <summary>后台工作任务是否在运行（0/1）。</summary>
        private int _workerActive;

        /// <summary>各会话已落盘状态（受 <see cref="_gate"/> 保护）。始终与磁盘文件一致；写失败时移除，下次保存整理。</summary>
        private readonly Dictionary<string, PersistedState> _states = new Dictionary<string, PersistedState>(StringComparer.OrdinalIgnoreCase);

        #endregion

        #region 构造 / 属性

        /// <param name="directory">会话目录（不存在时首次写入自动创建）。</param>
        /// <param name="compactMinBytes">日志达到此大小后才因作废字节过半而整理。</param>
        /// <param name="log">日志回调；null 时写入 <see cref="AgentCoreLog"/>。</param>
        public SessionLogStore(string directory, long compactMinBytes = DefaultCompactMinBytes, Action<SessionLogLevel, string> log = null)
        {
            if (string.IsNullOrEmpty(directory))
                throw new ArgumentException("Session directory must not be empty.", nameof(directory));
            _directory = directory;
            _compactMinBytes = Math.Max(0, compactMinBytes);
            _log = log ?? DefaultLog;
        }

        /// <summary>会话目录。</summary>
        public string DirectoryPath => _directory;

        /// <summary>队列中尚未落盘的保存请求数。</summary>
        public int PendingCount => _queue.Count;

        /// <summary>会话日志路径。</summary>
        public string GetLogPath(string sessionId) => Path.Combine(_directory, sessionId + LogExtension);

        /// <summary>旧版 JSON 路径。</summary>
        public string GetLegacyPath(string sessionId) => Path.Combine(_directory, sessionId + LegacyExtension);

        private static void DefaultLog(SessionLogLevel level, string message)
        {
            switch (level)
            {
                case SessionLogLevel.Error: AgentCoreLog.Error(message); break;
                case SessionLogLevel.Warning: AgentCoreLog.Warning(message); break;
                default: AgentCoreLog.Info(message); break;
            }
        }

        private void Info(string message) => _log(SessionLogLevel.Info, "[AgentCore] SessionLogStore: " + message);
        private void Warn(string message) => _log(SessionLogLevel.Warning, "[AgentCore] SessionLogStore: " + message);
        private void Error(string message) => _log(SessionLogLevel.Error, "[AgentCore] SessionLogStore: " + message);

        #endregion

        #region 写入

        /// <summary>
        /// 同步保存：在调用线程捕获快照并入队，随后在调用线程持锁排空队列（含本请求）后返回。
        /// </summary>
        public void Save(SessionData session)
        {
            var request = WriteRequest.Capture(session);
            _queue.Enqueue(request);
            lock (_gate)
            {
                ProcessQueueLocked();
            }
        }

        /// <summary>
        /// 后台保存：在调用线程（主线程）捕获快照并入队，比对 / 序列化 / 落盘由后台工作任务完成。
        /// </summary>
        public void SaveInBackground(SessionData session)
        {
            var request = WriteRequest.Capture(session);
            _queue.Enqueue(request);
            if (Interlocked.CompareExchange(ref _workerActive, 1, 0) == 0)
                Task.Run(WorkerLoop);
        }

        /// <summary>
        /// 排空写入队列（域重载 / 退出前调用）。<paramref name="timeoutMs"/> 内拿不到锁时返回 false，队列交给工作任务继续处理。
        /// </summary>
        public bool Flush(int timeoutMs = Timeout.Infinite)
        {
            if (!Monitor.TryEnter(_gate, timeoutMs))
            {
                Warn($"Timed out after {timeoutMs} ms waiting for pending session writes ({_queue.Count} queued).");
                return false;
            }

            try
            {
                ProcessQueueLocked();
                return true;
            }
            finally
            {
                Monitor.Exit(_gate);
            }
        }

        private void WorkerLoop()
        {
            while (true)
            {
                try
                {
                    lock (_gate)
                    {
                        ProcessQueueLocked();
                    }
                }
                catch (Exception ex)
                {
                    Error($"Session writer failed: {ex.Message}");
                }

                Volatile.Write(ref _workerActive, 0);
                // 释放标记后再检查一次，避免"入队方看到标记仍为 1 而未启动工作任务"时请求滞留
                if (_queue.IsEmpty || Interlocked.CompareExchange(ref _workerActive, 1, 0) != 0)
                    return;
            }
        }

        private void ProcessQueueLocked()
        {
            while (_queue.TryDequeue(out var request))
            {
                try
                {
                    ApplyLocked(request, forceCompact: false);
                }
                catch (Exception ex)
                {
                    // 状态可能与磁盘不一致：丢弃缓存，用整理（原子整份写）重试一次
                    _states.Remove(request.Id);
                    Warn($"Incremental save of {request.Id} failed ({ex.Message}); rewriting the whole session file.");
                    try
                    {
                        ApplyLocked(request, forceCompact: true);
                    }
                    catch (Exception retryEx)
                    {
                        _states.Remove(request.Id);
                        Error($"Failed to save session {request.Id}: {retryEx.Message}. The previous on-disk version is kept.");
                    }
                }
            }
        }

        private void ApplyLocked(WriteRequest request, bool forceCompact)
        {
            Directory.CreateDirectory(_directory);
            var path = GetLogPath(request.Id);
            _states.TryGetValue(request.Id, out var state);

            if (forceCompact || state == null || state.NeedsCompaction || !FileMatches(path, state))
            {
                CompactLocked(request, path);
                return;
            }

            var delta = BuildDelta(state, request);
            if (delta == null)
            {
                CompactLocked(request, path);
                return;
            }

            if (delta.Text.Length == 0)
                return; // 无任何变化

            var bytes = Utf8NoBom.GetBytes(delta.Text.ToString());
            AppendBytes(path, bytes);
            delta.CommitTo(state);
            state.FileBytes += bytes.Length;

            if (state.FileBytes >= _compactMinBytes && state.SupersededBytes * 2 > state.FileBytes)
            {
                CompactLocked(request, path);
                return;
            }

            Info($"Session appended: {request.Id} ({delta.RecordCount} records, {bytes.Length} bytes)");
        }

        private static bool FileMatches(string path, PersistedState state)
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length == state.FileBytes;
        }

        private static void AppendBytes(string path, byte[] bytes)
        {
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        /// <summary>
        /// 构建增量记录。返回 null 表示变化过多应直接整理；返回 Text 为空表示无变化。
        /// </summary>
        private Delta BuildDelta(PersistedState state, WriteRequest request)
        {
            var delta = new Delta();
            int oldM = state.Messages.Count, newM = request.Messages.Count;
            int oldT = state.Turns.Count, newT = request.Turns.Count;
            int keepM = Math.Min(oldM, newM), keepT = Math.Min(oldT, newT);

            var messageSets = new List<int>();
            for (int i = 0; i < keepM; i++)
            {
                if (!state.Messages[i].Matches(request.Messages[i]))
                    messageSets.Add(i);
            }

            var turnSets = new List<int>();
            for (int i = 0; i < keepT; i++)
            {
                if (!state.Turns[i].Matches(request.Turns[i].Dto, request.Turns[i].SelfChallengeJson))
                    turnSets.Add(i);
            }

            int setCount = messageSets.Count + turnSets.Count;
            if (setCount > MaxSetRecordsBeforeCompact && setCount * 4 > newM + newT)
                return null;

            if (newM < oldM || newT < oldT)
            {
                delta.Truncate = true;
                delta.KeepMessages = keepM;
                delta.KeepTurns = keepT;
                delta.TruncateBytes = delta.AddLine(new LogRecord { Type = TypeTruncate, KeepMessages = keepM, KeepTurns = keepT });
            }

            foreach (var i in messageSets)
                AddMessage(delta, TypeMessageSet, i, request.Messages[i]);
            for (int i = keepM; i < newM; i++)
                AddMessage(delta, TypeMessage, i, request.Messages[i]);

            foreach (var i in turnSets)
                AddTurn(delta, TypeTurnSet, i, request.Turns[i]);
            for (int i = keepT; i < newT; i++)
                AddTurn(delta, TypeTurn, i, request.Turns[i]);

            var metaJson = JsonConvert.SerializeObject(request.Meta, LineSettings);
            if (delta.RecordCount > 0 || !string.Equals(metaJson, state.MetaJson, StringComparison.Ordinal))
            {
                // meta 总是每批的最后一行：会话列表从文件尾部即可读到最新元数据
                delta.MetaJson = metaJson;
                delta.MetaBytes = delta.AddLine(new LogRecord { Type = TypeMeta, Meta = request.Meta });
            }

            return delta;
        }

        private static void AddMessage(Delta delta, string type, int index, SerializableChatMessage message)
        {
            long bytes = delta.AddLine(new LogRecord { Type = type, Index = index, Message = message });
            delta.MessageWrites.Add(new ItemWrite<MessageFingerprint>(index, MessageFingerprint.From(message), bytes));
        }

        private static void AddTurn(Delta delta, string type, int index, CapturedTurn turn)
        {
            long bytes = delta.AddLine(new LogRecord { Type = type, Index = index, Turn = turn.Dto });
            delta.TurnWrites.Add(new ItemWrite<TurnFingerprint>(index, TurnFingerprint.From(turn.Dto, turn.SelfChallengeJson), bytes));
        }

        /// <summary>整理：header + snapshot + meta 三行，原子替换（保留 .bak），并重建已落盘状态；随后迁移旧版 JSON。</summary>
        private void CompactLocked(WriteRequest request, string path)
        {
            var header = JsonConvert.SerializeObject(new LogRecord { Type = TypeHeader, Version = FormatVersion, Meta = request.Meta }, LineSettings);
            var snapshot = JsonConvert.SerializeObject(new LogRecord
            {
                Type = TypeSnapshot,
                Messages = request.Messages,
                Turns = request.Turns.Select(t => t.Dto).ToList()
            }, LineSettings);

            // 末尾再写一条 meta：会话列表只读文件尾部，snapshot 很长时也不必回头找 header
            var meta = JsonConvert.SerializeObject(new LogRecord { Type = TypeMeta, Meta = request.Meta }, LineSettings);

            int snapshotBytes = Utf8NoBom.GetByteCount(snapshot) + 1;
            int metaBytes = Utf8NoBom.GetByteCount(meta) + 1;
            var bytes = Utf8NoBom.GetBytes(header + "\n" + snapshot + "\n" + meta + "\n");
            AtomicFile.WriteAllBytes(path, bytes, backupPath: path + BackupSuffix);

            var state = new PersistedState
            {
                MetaJson = JsonConvert.SerializeObject(request.Meta, LineSettings),
                MetaBytes = metaBytes,
                FileBytes = bytes.Length
            };
            int itemCount = request.Messages.Count + request.Turns.Count;
            long perItem = itemCount == 0 ? 0 : snapshotBytes / itemCount;
            foreach (var m in request.Messages)
            {
                state.Messages.Add(MessageFingerprint.From(m));
                state.MessageBytes.Add(perItem);
            }
            foreach (var t in request.Turns)
            {
                state.Turns.Add(TurnFingerprint.From(t.Dto, t.SelfChallengeJson));
                state.TurnBytes.Add(perItem);
            }
            _states[request.Id] = state;

            Info($"Session compacted: {request.Id} ({request.Meta.Title}, {bytes.Length} bytes)");
            MigrateLegacyLocked(request.Id);
        }

        /// <summary>jsonl 已完整写出后，把旧版 &lt;id&gt;.json 改名为 &lt;id&gt;.json.migrated（保留以便降级）。</summary>
        private void MigrateLegacyLocked(string sessionId)
        {
            var legacy = GetLegacyPath(sessionId);
            if (!File.Exists(legacy)) return;
            try
            {
                var migrated = legacy + MigratedSuffix;
                if (File.Exists(migrated))
                    File.Delete(migrated);
                File.Move(legacy, migrated);
                Info($"Migrated legacy session file {Path.GetFileName(legacy)} -> {Path.GetFileName(migrated)}");
            }
            catch (Exception ex)
            {
                // 旧文件仍在也不影响正确性：读取总是优先 jsonl
                Warn($"Could not rename migrated legacy session file {legacy}: {ex.Message}");
            }
        }

        #endregion

        #region 读取

        /// <summary>
        /// 加载会话（先排空写入队列）。jsonl 优先，其次旧版 JSON。
        /// 尾行残缺（追加时崩溃）忽略并警告；中间行损坏时保留坏行之前的全部内容、把原文件复制为
        /// <c>*.corrupted-时间戳</c> 并立即整理。绝不静默丢弃会话。
        /// </summary>
        /// <returns>会话数据；不存在或无法恢复时返回 null（损坏文件保留在磁盘上）。</returns>
        public SessionData Load(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return null;

            lock (_gate)
            {
                ProcessQueueLocked();
                var path = GetLogPath(sessionId);
                if (File.Exists(path))
                    return LoadLogLocked(sessionId, path);

                var legacy = GetLegacyPath(sessionId);
                if (File.Exists(legacy))
                {
                    var session = LoadLegacyWithRecovery(sessionId, legacy);
                    if (session != null)
                    {
                        // 下一次保存整理为 jsonl 并迁移旧文件
                        _states[sessionId] = new PersistedState { NeedsCompaction = true };
                    }
                    return session;
                }

                return null;
            }
        }

        private SessionData LoadLogLocked(string sessionId, string path)
        {
            var result = Replay(path);
            if (result.Session != null)
            {
                if (result.CorruptLine > 0)
                {
                    var copy = CopyAside(path);
                    Warn($"Session {sessionId} has a corrupted record at line {result.CorruptLine} ({result.Error}). " +
                         $"Recovered {result.Session.Messages.Count} messages / {result.Session.Turns.Count} turns before it; " +
                         $"the original file was kept at {copy ?? path}.");
                    RewriteRecoveredLocked(result.Session, path);
                }
                else
                {
                    if (result.TornTail)
                        Warn($"Session {sessionId}: the last record was incomplete (interrupted write) and was ignored ({result.Error}).");

                    if (result.NeedsCompaction || !_states.ContainsKey(sessionId) || _states[sessionId].NeedsCompaction)
                        _states[sessionId] = result.State;
                }

                Info($"Session loaded: {sessionId} ({result.Session.Title}, {result.Session.MessageCount} messages)");
                return result.Session;
            }

            // 连 header 都读不出：依次尝试 .bak、迁移前的旧版 JSON
            Warn($"Session file {Path.GetFileName(path)} is unreadable ({result.Error}); trying backups.");
            SessionData recovered = null;
            string source = null;
            var backup = path + BackupSuffix;
            if (File.Exists(backup))
            {
                var fromBackup = Replay(backup);
                if (fromBackup.Session != null)
                {
                    recovered = fromBackup.Session;
                    source = backup;
                }
            }

            if (recovered == null)
            {
                foreach (var legacy in new[] { GetLegacyPath(sessionId), GetLegacyPath(sessionId) + MigratedSuffix })
                {
                    if (!File.Exists(legacy)) continue;
                    recovered = TryReadLegacy(legacy, out _);
                    if (recovered != null)
                    {
                        source = legacy;
                        break;
                    }
                }
            }

            var aside = CopyAside(path);
            if (recovered == null)
            {
                Error($"Session {sessionId} is corrupted ({result.Error}) and no usable backup was found. " +
                      $"The damaged file was kept at {aside ?? path}.");
                return null;
            }

            Warn($"Session {sessionId} was corrupted ({result.Error}); restored from {Path.GetFileName(source)}. " +
                 $"Changes after that version are lost. The damaged file was kept at {aside ?? path}.");
            RewriteRecoveredLocked(recovered, path);
            return recovered;
        }

        /// <summary>把恢复出的会话整理写回（原子替换）；失败时只记日志，下次保存再整理。</summary>
        private void RewriteRecoveredLocked(SessionData session, string path)
        {
            try
            {
                CompactLocked(WriteRequest.Capture(session), path);
            }
            catch (Exception ex)
            {
                _states[session.Id] = new PersistedState { NeedsCompaction = true };
                Warn($"Could not rewrite recovered session {session.Id}: {ex.Message}");
            }
        }

        /// <summary>把文件复制为 <c>*.corrupted-时间戳</c>；返回副本路径，失败返回 null。</summary>
        private string CopyAside(string path)
        {
            try
            {
                var target = $"{path}.corrupted-{DateTime.Now:yyyyMMdd-HHmmss}";
                for (int n = 1; File.Exists(target); n++)
                    target = $"{path}.corrupted-{DateTime.Now:yyyyMMdd-HHmmss}-{n}";
                File.Copy(path, target);
                return target;
            }
            catch (Exception ex)
            {
                Warn($"Could not copy damaged session file aside: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 只读加载（不修复、不更新缓存），供跨会话检索使用。
        /// </summary>
        public SessionData ReadForSearch(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return null;
            lock (_gate)
            {
                ProcessQueueLocked();
                var path = GetLogPath(sessionId);
                if (File.Exists(path))
                    return Replay(path).Session;

                var legacy = GetLegacyPath(sessionId);
                return File.Exists(legacy) ? TryReadLegacy(legacy, out _) : null;
            }
        }

        /// <summary>
        /// 重放 jsonl。坏行之前的内容全部保留；坏行是最后一个非空行时视为追加中断（残缺尾行）。
        /// </summary>
        internal ReplayResult Replay(string path)
        {
            var result = new ReplayResult();
            byte[] data;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    data = new byte[stream.Length];
                    int read = 0;
                    while (read < data.Length)
                    {
                        int n = stream.Read(data, read, data.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read < data.Length) Array.Resize(ref data, read);
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }

            // 切分行：(起点, 长度, 是否以 \n 结尾)
            var lines = new List<(int start, int length, bool terminated)>();
            int lineStart = 0;
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] == (byte)'\n')
                {
                    lines.Add((lineStart, i - lineStart, true));
                    lineStart = i + 1;
                }
            }
            if (lineStart < data.Length)
                lines.Add((lineStart, data.Length - lineStart, false));

            int lastNonEmpty = -1;
            for (int i = lines.Count - 1; i >= 0 && lastNonEmpty < 0; i--)
            {
                if (!IsBlank(data, lines[i].start, lines[i].length))
                    lastNonEmpty = i;
            }

            var replay = new ReplayState();
            for (int li = 0; li < lines.Count; li++)
            {
                var (start, length, terminated) = lines[li];
                if (IsBlank(data, start, length))
                {
                    if (!terminated && length > 0) result.NeedsCompaction = true;
                    continue;
                }

                string error;
                try
                {
                    var text = Utf8NoBom.GetString(data, start, length).TrimEnd('\r');
                    var record = JsonConvert.DeserializeObject<LogRecord>(text, LineSettings);
                    error = replay.Apply(record, length + (terminated ? 1 : 0));
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                if (error != null)
                {
                    result.Error = error;
                    if (li == lastNonEmpty)
                    {
                        result.TornTail = true;
                        result.NeedsCompaction = true;
                    }
                    else
                    {
                        result.CorruptLine = li + 1;
                    }
                    break;
                }

                if (!terminated)
                    result.NeedsCompaction = true; // 完整记录但缺换行：下一次追加前必须整理
            }

            if (!replay.HasHeader)
            {
                result.Error ??= "missing header record";
                result.CorruptLine = 0;
                return result;
            }

            result.Session = replay.ToSession();
            result.State = replay.ToState(data.LongLength, result.NeedsCompaction);
            return result;
        }

        private static bool IsBlank(byte[] data, int start, int length)
        {
            for (int i = start; i < start + length; i++)
            {
                var b = data[i];
                if (b != (byte)' ' && b != (byte)'\r' && b != (byte)'\t' && b != 0)
                    return false;
            }
            return true;
        }

        /// <summary>旧版 JSON 加载：损坏时把坏文件改名保留为 <c>*.corrupted-时间戳</c> 并从 <c>.bak</c> 恢复（与 P1 行为一致）。</summary>
        private SessionData LoadLegacyWithRecovery(string sessionId, string filePath)
        {
            var session = TryReadLegacy(filePath, out var reason);
            if (session != null)
            {
                Info($"Legacy session loaded: {sessionId} ({session.Title}, {session.MessageCount} messages)");
                return session;
            }

            var corruptedPath = $"{filePath}.corrupted-{DateTime.Now:yyyyMMdd-HHmmss}";
            try
            {
                File.Move(filePath, corruptedPath);
            }
            catch (Exception ex)
            {
                Warn($"Could not move corrupted session file aside: {ex.Message}");
                corruptedPath = filePath;
            }

            var backupPath = filePath + BackupSuffix;
            if (!File.Exists(backupPath))
            {
                Error($"Session {sessionId} is corrupted ({reason}) and has no backup. The damaged file was kept at {corruptedPath}.");
                return null;
            }

            try
            {
                var json = File.ReadAllText(backupPath);
                var restored = JsonConvert.DeserializeObject<SessionData>(json, LegacySettings);
                if (restored == null)
                    throw new InvalidDataException("backup is empty");

                AtomicFile.WriteAllText(filePath, json);
                Warn($"Session {sessionId} was corrupted ({reason}); restored the previous save from backup. " +
                     $"Changes after that save are lost. The damaged file was kept at {corruptedPath}.");
                return restored;
            }
            catch (Exception ex)
            {
                Error($"Session {sessionId} is corrupted ({reason}) and its backup could not be loaded ({ex.Message}). " +
                      $"The damaged file was kept at {corruptedPath}.");
                return null;
            }
        }

        private static SessionData TryReadLegacy(string path, out string reason)
        {
            reason = null;
            try
            {
                var session = JsonConvert.DeserializeObject<SessionData>(File.ReadAllText(path), LegacySettings);
                if (session == null) reason = "file is empty";
                return session;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return null;
            }
        }

        #endregion

        #region 列表 / 删除

        /// <summary>
        /// 列出全部会话摘要（未排序）。jsonl 只读文件尾部找最后一条 meta / header 记录；找不到时才完整重放。
        /// 同一会话同时存在 jsonl 与旧版 JSON（迁移中途中断）时以 jsonl 为准。
        /// 读不出元数据的 jsonl 仍以占位标题列出（加载时会尝试恢复），不会从列表中消失。
        /// </summary>
        public List<SessionSummary> ListSummaries(CancellationToken ct = default)
        {
            var summaries = new List<SessionSummary>();
            lock (_gate)
            {
                ProcessQueueLocked();
                if (!Directory.Exists(_directory))
                    return summaries;

                var logIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var legacyFiles = new List<string>();
                foreach (var file in Directory.GetFiles(_directory))
                {
                    var ext = Path.GetExtension(file);
                    if (string.Equals(ext, LogExtension, StringComparison.OrdinalIgnoreCase))
                    {
                        ct.ThrowIfCancellationRequested();
                        logIds.Add(Path.GetFileNameWithoutExtension(file));
                        var summary = ReadLogSummary(file);
                        if (summary != null) summaries.Add(summary);
                    }
                    else if (string.Equals(ext, LegacyExtension, StringComparison.OrdinalIgnoreCase))
                    {
                        legacyFiles.Add(file);
                    }
                }

                foreach (var file in legacyFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    if (logIds.Contains(Path.GetFileNameWithoutExtension(file))) continue;
                    try
                    {
                        var summary = ParseLegacySummary(File.ReadAllText(file));
                        if (summary != null) summaries.Add(summary);
                    }
                    catch (Exception ex)
                    {
                        Warn($"Failed to parse session file {Path.GetFileName(file)}: {ex.Message}");
                    }
                }
            }

            return summaries;
        }

        private SessionSummary ReadLogSummary(string path)
        {
            var id = Path.GetFileNameWithoutExtension(path);
            try
            {
                var meta = ReadTailMeta(path);
                if (meta == null)
                {
                    // 尾部没有 meta（极少见：超长尾行等）：完整重放
                    var replay = Replay(path);
                    if (replay.Session != null)
                        meta = SessionLogMeta.From(replay.Session);
                }

                if (meta != null)
                    return meta.ToSummary(id);
            }
            catch (Exception ex)
            {
                Warn($"Failed to read session summary {Path.GetFileName(path)}: {ex.Message}");
            }

            Warn($"Session file {Path.GetFileName(path)} has no readable metadata; listing it with a placeholder title.");
            DateTime updated;
            try { updated = File.GetLastWriteTimeUtc(path); } catch { updated = DateTime.MinValue; }
            return new SessionSummary
            {
                Id = id,
                Title = "损坏的会话 " + id,
                UpdatedAt = updated,
                CreatedAt = updated
            };
        }

        /// <summary>从文件尾部向前找最后一条完整的 meta / header 记录。</summary>
        private static SessionLogMeta ReadTailMeta(string path)
        {
            byte[] buffer;
            long length;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                length = stream.Length;
                int take = (int)Math.Min(length, SummaryTailBytes);
                buffer = new byte[take];
                stream.Seek(length - take, SeekOrigin.Begin);
                int read = 0;
                while (read < take)
                {
                    int n = stream.Read(buffer, read, take - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read < take) Array.Resize(ref buffer, read);
            }

            bool fromStart = buffer.LongLength == length;
            int end = buffer.Length;
            // 末尾没有换行的残段不完整，跳过
            while (end > 0 && buffer[end - 1] != (byte)'\n') end--;
            while (end > 0)
            {
                int lineEnd = end - 1; // 指向 '\n'
                int lineStart = lineEnd;
                while (lineStart > 0 && buffer[lineStart - 1] != (byte)'\n') lineStart--;
                if (lineStart == 0 && !fromStart)
                    break; // 截断处的半行

                var text = Utf8NoBom.GetString(buffer, lineStart, lineEnd - lineStart).TrimEnd('\r');
                if (text.StartsWith("{\"t\":\"" + TypeMeta + "\"", StringComparison.Ordinal) ||
                    text.StartsWith("{\"t\":\"" + TypeHeader + "\"", StringComparison.Ordinal))
                {
                    try
                    {
                        var record = JsonConvert.DeserializeObject<LogRecord>(text, LineSettings);
                        if (record?.Meta != null)
                            return record.Meta;
                    }
                    catch (JsonException)
                    {
                        // 损坏的 meta 行：继续往前找
                    }
                }

                end = lineStart;
            }

            return null;
        }

        /// <summary>从旧版整份 JSON 文本解析摘要（只取摘要字段）。</summary>
        internal static SessionSummary ParseLegacySummary(string json)
        {
            var obj = JObject.Parse(json);

            var id = obj.Value<string>("id");
            if (string.IsNullOrEmpty(id))
                return null;

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
        /// 删除会话的全部文件（jsonl / .bak / 旧版 JSON / 已迁移的旧版 JSON）。损坏副本 <c>*.corrupted-*</c> 保留。
        /// </summary>
        /// <returns>是否删除了至少一个文件。</returns>
        public bool Delete(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return false;
            lock (_gate)
            {
                ProcessQueueLocked();
                _states.Remove(sessionId);
                bool any = false;
                var log = GetLogPath(sessionId);
                var legacy = GetLegacyPath(sessionId);
                foreach (var file in new[] { log, log + BackupSuffix, legacy, legacy + BackupSuffix, legacy + MigratedSuffix })
                {
                    if (!File.Exists(file)) continue;
                    File.Delete(file);
                    any = true;
                }
                return any;
            }
        }

        #endregion

        #region 内部类型

        /// <summary>主线程捕获的不可变保存请求。</summary>
        private sealed class WriteRequest
        {
            public string Id;
            public SessionLogMeta Meta;
            public List<SerializableChatMessage> Messages;
            public List<CapturedTurn> Turns;

            public static WriteRequest Capture(SessionData session)
            {
                if (session == null) throw new ArgumentNullException(nameof(session));
                if (string.IsNullOrEmpty(session.Id)) throw new ArgumentException("Session Id must not be empty.", nameof(session));

                var request = new WriteRequest
                {
                    Id = session.Id,
                    Meta = SessionLogMeta.From(session),
                    Messages = session.Messages != null
                        ? session.Messages.Where(m => m != null).ToList()
                        : new List<SerializableChatMessage>()
                };

                var turns = session.Turns != null ? session.Turns.Where(t => t != null).ToList() : new List<SerializableConversationTurn>();
                request.Turns = new List<CapturedTurn>(turns.Count);
                int tailStart = Math.Max(0, turns.Count - MutableTailTurns);
                for (int i = 0; i < turns.Count; i++)
                {
                    var turn = turns[i];
                    if (i >= tailStart && turn.SelfChallenge != null)
                    {
                        // 运行中的轮次 SelfChallenge 会被原地修改：在主线程深拷贝，后台只读副本
                        var json = JsonConvert.SerializeObject(turn.SelfChallenge, LineSettings);
                        var clone = turn.CloneShallow();
                        clone.SelfChallenge = JsonConvert.DeserializeObject<SelfChallengeData>(json, LineSettings);
                        request.Turns.Add(new CapturedTurn(clone, json));
                    }
                    else
                    {
                        request.Turns.Add(new CapturedTurn(turn, null));
                    }
                }

                return request;
            }
        }

        /// <summary>捕获的轮次；<see cref="SelfChallengeJson"/> 仅末尾轮次在主线程预先序列化。</summary>
        private sealed class CapturedTurn
        {
            public readonly SerializableConversationTurn Dto;
            public readonly string SelfChallengeJson;

            public CapturedTurn(SerializableConversationTurn dto, string selfChallengeJson)
            {
                Dto = dto;
                SelfChallengeJson = selfChallengeJson;
            }
        }

        /// <summary>某会话已落盘状态：与磁盘文件内容一一对应。</summary>
        internal sealed class PersistedState
        {
            public readonly List<MessageFingerprint> Messages = new List<MessageFingerprint>();
            public readonly List<TurnFingerprint> Turns = new List<TurnFingerprint>();

            /// <summary>各条目最近一次写出所占字节（来自 snapshot 的条目取平均值）。</summary>
            public readonly List<long> MessageBytes = new List<long>();
            public readonly List<long> TurnBytes = new List<long>();

            public string MetaJson;
            public long MetaBytes;
            public long FileBytes;

            /// <summary>已被后续记录作废的字节数（估算）。</summary>
            public long SupersededBytes;

            /// <summary>下一次保存必须整理（旧版 JSON、尾行残缺、缺换行等）。</summary>
            public bool NeedsCompaction;
        }

        private readonly struct ItemWrite<T>
        {
            public readonly int Index;
            public readonly T Fingerprint;
            public readonly long Bytes;

            public ItemWrite(int index, T fingerprint, long bytes)
            {
                Index = index;
                Fingerprint = fingerprint;
                Bytes = bytes;
            }
        }

        /// <summary>一次增量保存要写出的记录及对已落盘状态的更新。</summary>
        private sealed class Delta
        {
            public readonly StringBuilder Text = new StringBuilder();
            public int RecordCount;
            public bool Truncate;
            public int KeepMessages, KeepTurns;
            public long TruncateBytes;
            public readonly List<ItemWrite<MessageFingerprint>> MessageWrites = new List<ItemWrite<MessageFingerprint>>();
            public readonly List<ItemWrite<TurnFingerprint>> TurnWrites = new List<ItemWrite<TurnFingerprint>>();
            public string MetaJson;
            public long MetaBytes;

            public long AddLine(LogRecord record)
            {
                var line = JsonConvert.SerializeObject(record, LineSettings);
                Text.Append(line).Append('\n');
                RecordCount++;
                return Utf8NoBom.GetByteCount(line) + 1;
            }

            public void CommitTo(PersistedState state)
            {
                if (Truncate)
                {
                    state.SupersededBytes += TruncateBytes;
                    state.SupersededBytes += Cut(state.Messages, state.MessageBytes, KeepMessages);
                    state.SupersededBytes += Cut(state.Turns, state.TurnBytes, KeepTurns);
                }

                foreach (var w in MessageWrites)
                    state.SupersededBytes += Put(state.Messages, state.MessageBytes, w);
                foreach (var w in TurnWrites)
                    state.SupersededBytes += Put(state.Turns, state.TurnBytes, w);

                if (MetaJson != null)
                {
                    state.SupersededBytes += state.MetaBytes;
                    state.MetaJson = MetaJson;
                    state.MetaBytes = MetaBytes;
                }
            }

            private static long Cut<T>(List<T> items, List<long> sizes, int keep)
            {
                long removed = 0;
                for (int i = keep; i < sizes.Count; i++) removed += sizes[i];
                if (keep < items.Count) items.RemoveRange(keep, items.Count - keep);
                if (keep < sizes.Count) sizes.RemoveRange(keep, sizes.Count - keep);
                return removed;
            }

            private static long Put<T>(List<T> items, List<long> sizes, ItemWrite<T> write)
            {
                if (write.Index < items.Count)
                {
                    long old = sizes[write.Index];
                    items[write.Index] = write.Fingerprint;
                    sizes[write.Index] = write.Bytes;
                    return old;
                }

                items.Add(write.Fingerprint);
                sizes.Add(write.Bytes);
                return 0;
            }
        }

        /// <summary>重放过程中的累积状态。</summary>
        private sealed class ReplayState
        {
            public bool HasHeader;
            public SessionLogMeta Meta;
            public string MetaJson;
            public long MetaBytes;
            public readonly List<SerializableChatMessage> Messages = new List<SerializableChatMessage>();
            public readonly List<SerializableConversationTurn> Turns = new List<SerializableConversationTurn>();
            public readonly List<long> MessageBytes = new List<long>();
            public readonly List<long> TurnBytes = new List<long>();
            public long Superseded;

            /// <summary>应用一条记录；返回错误描述，成功返回 null。</summary>
            public string Apply(LogRecord record, long bytes)
            {
                if (record == null || string.IsNullOrEmpty(record.Type))
                    return "record has no type";

                if (!HasHeader)
                {
                    if (record.Type != TypeHeader || record.Meta == null)
                        return "first record is not a header";
                    HasHeader = true;
                    SetMeta(record.Meta, bytes);
                    return null;
                }

                switch (record.Type)
                {
                    case TypeHeader:
                        return "duplicate header record";

                    case TypeSnapshot:
                        Superseded += Sum(MessageBytes) + Sum(TurnBytes);
                        Messages.Clear();
                        Turns.Clear();
                        MessageBytes.Clear();
                        TurnBytes.Clear();
                        var messages = record.Messages ?? new List<SerializableChatMessage>();
                        var turns = record.Turns ?? new List<SerializableConversationTurn>();
                        if (messages.Any(m => m == null) || turns.Any(t => t == null))
                            return "snapshot contains null items";
                        long perItem = messages.Count + turns.Count == 0 ? 0 : bytes / (messages.Count + turns.Count);
                        Messages.AddRange(messages);
                        Turns.AddRange(turns);
                        MessageBytes.AddRange(Enumerable.Repeat(perItem, messages.Count));
                        TurnBytes.AddRange(Enumerable.Repeat(perItem, turns.Count));
                        return null;

                    case TypeMessage:
                    case TypeMessageSet:
                        if (record.Message == null || record.Index == null) return "message record without payload";
                        return Put(Messages, MessageBytes, record.Message, record.Index.Value, record.Type == TypeMessageSet, bytes);

                    case TypeTurn:
                    case TypeTurnSet:
                        if (record.Turn == null || record.Index == null) return "turn record without payload";
                        return Put(Turns, TurnBytes, record.Turn, record.Index.Value, record.Type == TypeTurnSet, bytes);

                    case TypeTruncate:
                        int keepM = record.KeepMessages ?? Messages.Count;
                        int keepT = record.KeepTurns ?? Turns.Count;
                        if (keepM < 0 || keepM > Messages.Count || keepT < 0 || keepT > Turns.Count)
                            return $"truncate out of range ({keepM}/{Messages.Count}, {keepT}/{Turns.Count})";
                        Superseded += bytes;
                        for (int i = keepM; i < MessageBytes.Count; i++) Superseded += MessageBytes[i];
                        for (int i = keepT; i < TurnBytes.Count; i++) Superseded += TurnBytes[i];
                        Messages.RemoveRange(keepM, Messages.Count - keepM);
                        MessageBytes.RemoveRange(keepM, MessageBytes.Count - keepM);
                        Turns.RemoveRange(keepT, Turns.Count - keepT);
                        TurnBytes.RemoveRange(keepT, TurnBytes.Count - keepT);
                        return null;

                    case TypeMeta:
                        if (record.Meta == null) return "meta record without payload";
                        Superseded += MetaBytes;
                        SetMeta(record.Meta, bytes);
                        return null;

                    default:
                        // 更新版本写出的未知记录：无法理解其语义，按损坏处理（保留之前的内容）
                        return $"unknown record type '{record.Type}'";
                }
            }

            private void SetMeta(SessionLogMeta meta, long bytes)
            {
                Meta = meta;
                MetaJson = JsonConvert.SerializeObject(meta, LineSettings);
                MetaBytes = bytes;
            }

            private string Put<T>(List<T> items, List<long> sizes, T item, int index, bool replace, long bytes)
            {
                if (replace)
                {
                    if (index < 0 || index >= items.Count)
                        return $"replace index {index} out of range ({items.Count})";
                    Superseded += sizes[index];
                    items[index] = item;
                    sizes[index] = bytes;
                    return null;
                }

                if (index != items.Count)
                    return $"append index {index} does not match count {items.Count}";
                items.Add(item);
                sizes.Add(bytes);
                return null;
            }

            private static long Sum(List<long> values)
            {
                long total = 0;
                foreach (var v in values) total += v;
                return total;
            }

            public SessionData ToSession()
            {
                var session = new SessionData
                {
                    Messages = new List<SerializableChatMessage>(Messages),
                    Turns = new List<SerializableConversationTurn>(Turns)
                };
                Meta.ApplyTo(session);
                session.MessageCount = session.Messages.Count;
                return session;
            }

            public PersistedState ToState(long fileBytes, bool needsCompaction)
            {
                var state = new PersistedState
                {
                    MetaJson = MetaJson,
                    MetaBytes = MetaBytes,
                    FileBytes = fileBytes,
                    SupersededBytes = Superseded,
                    NeedsCompaction = needsCompaction
                };
                foreach (var m in Messages) state.Messages.Add(MessageFingerprint.From(m));
                state.MessageBytes.AddRange(MessageBytes);
                foreach (var t in Turns) state.Turns.Add(TurnFingerprint.From(t, null));
                state.TurnBytes.AddRange(TurnBytes);
                return state;
            }
        }

        /// <summary>重放结果。</summary>
        internal sealed class ReplayResult
        {
            /// <summary>重放出的会话；连 header 都没有时为 null。</summary>
            public SessionData Session;
            public PersistedState State;

            /// <summary>最后一个非空行无法解析（追加中断）。</summary>
            public bool TornTail;

            /// <summary>中间损坏行的行号（从 1 开始）；0 表示无。</summary>
            public int CorruptLine;

            /// <summary>下一次保存前需整理。</summary>
            public bool NeedsCompaction;

            public string Error;
        }

        #endregion
    }

    /// <summary>JSONL 单行记录（各类型共用，未用字段不写出）。<c>t</c> 必须是第一个属性。</summary>
    internal sealed class LogRecord
    {
        [JsonProperty("t")] public string Type { get; set; }
        [JsonProperty("v", NullValueHandling = NullValueHandling.Ignore)] public int? Version { get; set; }
        [JsonProperty("i", NullValueHandling = NullValueHandling.Ignore)] public int? Index { get; set; }
        [JsonProperty("keep_messages", NullValueHandling = NullValueHandling.Ignore)] public int? KeepMessages { get; set; }
        [JsonProperty("keep_turns", NullValueHandling = NullValueHandling.Ignore)] public int? KeepTurns { get; set; }
        [JsonProperty("meta", NullValueHandling = NullValueHandling.Ignore)] public SessionLogMeta Meta { get; set; }
        [JsonProperty("m", NullValueHandling = NullValueHandling.Ignore)] public SerializableChatMessage Message { get; set; }
        [JsonProperty("d", NullValueHandling = NullValueHandling.Ignore)] public SerializableConversationTurn Turn { get; set; }
        [JsonProperty("messages", NullValueHandling = NullValueHandling.Ignore)] public List<SerializableChatMessage> Messages { get; set; }
        [JsonProperty("turns", NullValueHandling = NullValueHandling.Ignore)] public List<SerializableConversationTurn> Turns { get; set; }
    }

    /// <summary>会话元数据（header / meta 记录载荷）。字段名与旧版整份 JSON 保持一致。</summary>
    internal sealed class SessionLogMeta
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("created_at")] public DateTime CreatedAt { get; set; }
        [JsonProperty("updated_at")] public DateTime UpdatedAt { get; set; }
        [JsonProperty("message_count")] public int MessageCount { get; set; }
        [JsonProperty("turn_count")] public int TurnCount { get; set; }
        [JsonProperty("compression_metrics", NullValueHandling = NullValueHandling.Ignore)] public SerializableCompressionMetrics CompressionMetrics { get; set; }
        [JsonProperty("tag", NullValueHandling = NullValueHandling.Ignore)] public string Tag { get; set; }
        [JsonProperty("archived", DefaultValueHandling = DefaultValueHandling.Ignore)] public bool Archived { get; set; }
        [JsonProperty("title_manually_set", DefaultValueHandling = DefaultValueHandling.Ignore)] public bool TitleManuallySet { get; set; }
        [JsonProperty("reasoning_effort_override", NullValueHandling = NullValueHandling.Ignore)] public string ReasoningEffortOverride { get; set; }

        public static SessionLogMeta From(SessionData session)
        {
            SerializableCompressionMetrics metrics = null;
            if (session.CompressionMetrics != null)
            {
                // 拷贝一份：调用方之后修改原对象不影响已入队的请求
                metrics = JsonConvert.DeserializeObject<SerializableCompressionMetrics>(
                    JsonConvert.SerializeObject(session.CompressionMetrics));
            }

            return new SessionLogMeta
            {
                Id = session.Id,
                Title = session.Title,
                CreatedAt = session.CreatedAt,
                UpdatedAt = session.UpdatedAt,
                MessageCount = session.MessageCount,
                TurnCount = session.Turns?.Count ?? 0,
                CompressionMetrics = metrics,
                Tag = session.Tag,
                Archived = session.Archived,
                TitleManuallySet = session.TitleManuallySet,
                ReasoningEffortOverride = session.ReasoningEffortOverride
            };
        }

        public void ApplyTo(SessionData session)
        {
            session.Id = Id;
            session.Title = Title;
            session.CreatedAt = CreatedAt;
            session.UpdatedAt = UpdatedAt;
            session.CompressionMetrics = CompressionMetrics;
            session.Tag = Tag;
            session.Archived = Archived;
            session.TitleManuallySet = TitleManuallySet;
            session.ReasoningEffortOverride = ReasoningEffortOverride;
        }

        public SessionSummary ToSummary(string fallbackId)
        {
            return new SessionSummary
            {
                Id = string.IsNullOrEmpty(Id) ? fallbackId : Id,
                Title = Title ?? "未命名会话",
                UpdatedAt = UpdatedAt,
                CreatedAt = CreatedAt,
                MessageCount = MessageCount,
                Tag = Tag,
                Archived = Archived,
                TitleManuallySet = TitleManuallySet
            };
        }
    }

    /// <summary>LLM 消息的廉价指纹：只拷贝字段引用（字符串不可变），比较时 <see cref="string.Equals(string, string)"/> 先比引用。</summary>
    internal sealed class MessageFingerprint
    {
        private string _role, _content, _toolCallId, _reasoningContent, _kind;
        private string[] _calls;
        private string[] _blocks;

        public static MessageFingerprint From(SerializableChatMessage m)
        {
            return new MessageFingerprint
            {
                _role = m.Role,
                _content = m.Content,
                _toolCallId = m.ToolCallId,
                _reasoningContent = m.ReasoningContent,
                _kind = m.Kind,
                _calls = FlattenCalls(m.ToolCalls),
                _blocks = FlattenBlocks(m.ReasoningBlocks)
            };
        }

        public bool Matches(SerializableChatMessage m)
        {
            return string.Equals(_role, m.Role) &&
                   string.Equals(_content, m.Content) &&
                   string.Equals(_toolCallId, m.ToolCallId) &&
                   string.Equals(_reasoningContent, m.ReasoningContent) &&
                   string.Equals(_kind, m.Kind) &&
                   SameStrings(_calls, FlattenCalls(m.ToolCalls)) &&
                   SameStrings(_blocks, FlattenBlocks(m.ReasoningBlocks));
        }

        private static string[] FlattenCalls(List<SerializableToolCall> calls)
        {
            if (calls == null || calls.Count == 0) return null;
            var result = new string[calls.Count * 4];
            for (int i = 0; i < calls.Count; i++)
            {
                var c = calls[i];
                result[i * 4] = c?.Id;
                result[i * 4 + 1] = c?.Type;
                result[i * 4 + 2] = c?.Function?.Name;
                result[i * 4 + 3] = c?.Function?.Arguments;
            }
            return result;
        }

        private static string[] FlattenBlocks(List<ReasoningBlock> blocks)
        {
            if (blocks == null || blocks.Count == 0) return null;
            var result = new string[blocks.Count * 4];
            for (int i = 0; i < blocks.Count; i++)
            {
                var b = blocks[i];
                result[i * 4] = b?.Type;
                result[i * 4 + 1] = b?.Thinking;
                result[i * 4 + 2] = b?.Signature;
                result[i * 4 + 3] = b?.Data;
            }
            return result;
        }

        internal static bool SameStrings(string[] a, string[] b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (!string.Equals(a[i], b[i])) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// UI 轮次的廉价指纹。SelfChallenge 是会被原地修改的可变对象：末尾轮次在主线程捕获时已序列化，按内容比较；
    /// 其余轮次先比引用，引用不同再在写入线程序列化比较（非末尾轮次不再变化，可安全读取）。
    /// </summary>
    internal sealed class TurnFingerprint
    {
        private string _id, _role, _content, _reasoning, _raw;
        private ThinkingTraceSource _reasoningSource;
        private VisiblePlanningTraceState _planningState;
        private double _reasoningDurationMs;
        private DateTime _timestamp;
        private int _messageEndIndex;
        private string[] _calls;
        private double[] _callTimes;
        private bool[] _callSuccess;
        private object _selfChallengeRef;
        private string _selfChallengeJson;

        public static TurnFingerprint From(SerializableConversationTurn t, string selfChallengeJson)
        {
            var fp = new TurnFingerprint
            {
                _id = t.Id,
                _role = t.Role,
                _content = t.Content,
                _reasoning = t.Reasoning,
                _raw = t.RawAssistantContent,
                _reasoningSource = t.ReasoningSource,
                _planningState = t.PlanningTraceState,
                _reasoningDurationMs = t.ReasoningDurationMs,
                _timestamp = t.Timestamp,
                _messageEndIndex = t.MessageEndIndex,
                _selfChallengeRef = t.SelfChallenge
            };

            if (t.SelfChallenge != null)
                fp._selfChallengeJson = selfChallengeJson ?? JsonConvert.SerializeObject(t.SelfChallenge, SessionLogStore.LineSettings);

            var calls = t.ToolCalls;
            if (calls != null && calls.Count > 0)
            {
                fp._calls = new string[calls.Count * 4];
                fp._callTimes = new double[calls.Count];
                fp._callSuccess = new bool[calls.Count];
                for (int i = 0; i < calls.Count; i++)
                {
                    var c = calls[i];
                    fp._calls[i * 4] = c?.Id;
                    fp._calls[i * 4 + 1] = c?.ToolName;
                    fp._calls[i * 4 + 2] = c?.Arguments;
                    fp._calls[i * 4 + 3] = c?.Result;
                    fp._callTimes[i] = c?.ExecutionTimeMs ?? 0;
                    fp._callSuccess[i] = c?.Success ?? false;
                }
            }

            return fp;
        }

        public bool Matches(SerializableConversationTurn t, string selfChallengeJson)
        {
            if (!(string.Equals(_id, t.Id) &&
                  string.Equals(_role, t.Role) &&
                  string.Equals(_content, t.Content) &&
                  string.Equals(_reasoning, t.Reasoning) &&
                  string.Equals(_raw, t.RawAssistantContent) &&
                  _reasoningSource == t.ReasoningSource &&
                  _planningState == t.PlanningTraceState &&
                  _reasoningDurationMs.Equals(t.ReasoningDurationMs) &&
                  _timestamp == t.Timestamp &&
                  _messageEndIndex == t.MessageEndIndex))
                return false;

            if (!CallsMatch(t.ToolCalls))
                return false;

            if (t.SelfChallenge == null || _selfChallengeRef == null)
                return t.SelfChallenge == null && _selfChallengeRef == null;

            if (selfChallengeJson == null && ReferenceEquals(t.SelfChallenge, _selfChallengeRef))
                return true;

            var json = selfChallengeJson ?? JsonConvert.SerializeObject(t.SelfChallenge, SessionLogStore.LineSettings);
            return string.Equals(json, _selfChallengeJson, StringComparison.Ordinal);
        }

        private bool CallsMatch(List<SerializableToolCallInfo> calls)
        {
            int count = calls?.Count ?? 0;
            int mine = _callTimes?.Length ?? 0;
            if (count != mine) return false;
            for (int i = 0; i < count; i++)
            {
                var c = calls[i];
                if (!string.Equals(_calls[i * 4], c?.Id) ||
                    !string.Equals(_calls[i * 4 + 1], c?.ToolName) ||
                    !string.Equals(_calls[i * 4 + 2], c?.Arguments) ||
                    !string.Equals(_calls[i * 4 + 3], c?.Result) ||
                    !_callTimes[i].Equals(c?.ExecutionTimeMs ?? 0) ||
                    _callSuccess[i] != (c?.Success ?? false))
                    return false;
            }
            return true;
        }
    }
}
