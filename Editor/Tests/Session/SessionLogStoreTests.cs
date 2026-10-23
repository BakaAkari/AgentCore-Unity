using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AgentCore.Editor.Core.SelfChallenge;
using AgentCore.Editor.Session;
using Newtonsoft.Json;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.Session
{
    /// <summary>
    /// SessionLogStore（会话 JSONL 追加式存储）的单元测试：往返、只追加增长、截断 / 整理、残缺尾行、
    /// 中间损坏、旧版 JSON 迁移、列表元数据与后台写入。每个用例使用独立的临时目录，不依赖 Unity 工程。
    /// </summary>
    [TestFixture]
    public class SessionLogStoreTests
    {
        /// <summary>足够大，测试中不会因作废字节触发自动整理。</summary>
        private const long NoAutoCompact = long.MaxValue / 4;

        private string _dir;
        private List<string> _warnings;
        private List<string> _errors;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "agentcore-session-tests-" + Guid.NewGuid().ToString("N"));
            _warnings = new List<string>();
            _errors = new List<string>();
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (Directory.Exists(_dir))
                    Directory.Delete(_dir, recursive: true);
            }
            catch
            {
                // 临时目录清理失败不影响结果
            }
        }

        // ── 辅助 ───────────────────────────────────────────────────────────────

        /// <summary>新建 store 实例（模拟新进程 / 域重载后：没有任何已落盘状态缓存）。</summary>
        private SessionLogStore Open(long compactMinBytes = NoAutoCompact)
        {
            return new SessionLogStore(_dir, compactMinBytes, (level, message) =>
            {
                if (level == SessionLogLevel.Warning) _warnings.Add(message);
                else if (level == SessionLogLevel.Error) _errors.Add(message);
            });
        }

        private static SessionData NewSession(string id = "s1", int exchanges = 2)
        {
            var session = new SessionData
            {
                Id = id,
                Title = "测试会话",
                CreatedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                UpdatedAt = new DateTime(2026, 1, 2, 3, 4, 6, DateTimeKind.Utc),
                Tag = "unit",
            };
            session.Messages.Add(new SerializableChatMessage { Role = "system", Content = "sys" });
            for (int i = 0; i < exchanges; i++)
                AddExchange(session, i);
            return session;
        }

        /// <summary>追加一问一答（含一次工具调用），同时追加 user / assistant 两个轮次。</summary>
        private static void AddExchange(SessionData session, int n)
        {
            session.Messages.Add(new SerializableChatMessage { Role = "user", Content = "question " + n });
            session.Messages.Add(new SerializableChatMessage
            {
                Role = "assistant",
                ToolCalls = new List<SerializableToolCall>
                {
                    new SerializableToolCall { Id = "call" + n, Function = new SerializableFunctionCall { Name = "read_file", Arguments = "{\"path\":\"a" + n + "\"}" } }
                }
            });
            session.Messages.Add(new SerializableChatMessage { Role = "tool", ToolCallId = "call" + n, Content = "file body " + n + "\nline2" });
            session.Messages.Add(new SerializableChatMessage { Role = "assistant", Content = "answer " + n });

            session.Turns.Add(new SerializableConversationTurn
            {
                Id = "u" + n,
                Role = "user",
                Content = "question " + n,
                Timestamp = new DateTime(2026, 1, 2, 3, 5, n, DateTimeKind.Utc),
                MessageEndIndex = session.Messages.Count - 3
            });
            session.Turns.Add(new SerializableConversationTurn
            {
                Id = "a" + n,
                Role = "assistant",
                Content = "answer " + n,
                Timestamp = new DateTime(2026, 1, 2, 3, 6, n, DateTimeKind.Utc),
                MessageEndIndex = session.Messages.Count,
                ToolCalls = new List<SerializableToolCallInfo>
                {
                    new SerializableToolCallInfo { Id = "call" + n, ToolName = "read_file", Arguments = "{}", Result = "ok", Success = true, ExecutionTimeMs = 12.5 }
                }
            });
            session.MessageCount = session.Messages.Count;
        }

        private string LogPath(string id = "s1") => Path.Combine(_dir, id + SessionLogStore.LogExtension);

        private string[] Lines(string id = "s1") =>
            File.ReadAllText(LogPath(id)).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

        private static string RecordType(string line) => JsonConvert.DeserializeObject<Dictionary<string, object>>(line)["t"] as string;

        private static void AssertSameSession(SessionData expected, SessionData actual)
        {
            Assert.IsNotNull(actual);
            Assert.AreEqual(expected.Id, actual.Id);
            Assert.AreEqual(expected.Title, actual.Title);
            Assert.AreEqual(expected.Tag, actual.Tag);
            Assert.AreEqual(expected.Archived, actual.Archived);
            Assert.AreEqual(expected.CreatedAt, actual.CreatedAt);
            Assert.AreEqual(expected.UpdatedAt, actual.UpdatedAt);
            Assert.AreEqual(expected.Messages.Count, actual.Messages.Count, "message count");
            Assert.AreEqual(expected.Turns.Count, actual.Turns.Count, "turn count");
            Assert.AreEqual(Json(expected.Messages), Json(actual.Messages));
            Assert.AreEqual(Json(expected.Turns), Json(actual.Turns));
        }

        private static string Json(object value) => JsonConvert.SerializeObject(value, Formatting.None);

        // ── 往返 ───────────────────────────────────────────────────────────────

        [Test]
        public void SaveThenLoad_InNewStore_RoundTripsAllFields()
        {
            var session = NewSession();
            session.Archived = true;
            session.TitleManuallySet = true;
            session.ReasoningEffortOverride = "high";
            session.CompressionMetrics = new SerializableCompressionMetrics { ConversationCompressions = 2, ToolResultOriginalTokens = 900 };
            session.Messages[1].Kind = "Skill";
            session.Turns[1].SelfChallenge = new SelfChallengeData { NodeATriggered = true, NodeAOutput = "interpretation" };

            Open().Save(session);
            var loaded = Open().Load("s1");

            AssertSameSession(session, loaded);
            Assert.IsTrue(loaded.TitleManuallySet);
            Assert.AreEqual("high", loaded.ReasoningEffortOverride);
            Assert.AreEqual(2, loaded.CompressionMetrics.ConversationCompressions);
            Assert.AreEqual(900, loaded.CompressionMetrics.ToolResultOriginalTokens);
            Assert.AreEqual("interpretation", loaded.Turns[1].SelfChallenge.NodeAOutput);
            Assert.AreEqual(session.Messages.Count, loaded.MessageCount);
            Assert.IsEmpty(_warnings);
            Assert.IsEmpty(_errors);
        }

        [Test]
        public void FirstSave_WritesHeaderThenSnapshot()
        {
            Open().Save(NewSession());

            var lines = Lines();
            Assert.AreEqual(3, lines.Length);
            Assert.AreEqual("header", RecordType(lines[0]));
            Assert.AreEqual("snapshot", RecordType(lines[1]));
            Assert.AreEqual("meta", RecordType(lines[2]));
            StringAssert.StartsWith("{\"t\":\"header\",\"v\":1,", lines[0]);
        }

        [Test]
        public void ContentThatLooksLikeDate_IsNotRewritten()
        {
            var session = NewSession(exchanges: 1);
            session.Messages.Add(new SerializableChatMessage { Role = "user", Content = "2026-01-02T03:04:05Z" });

            Open().Save(session);
            var loaded = Open().Load("s1");

            Assert.AreEqual("2026-01-02T03:04:05Z", loaded.Messages.Last().Content);
        }

        // ── 只追加增长 ─────────────────────────────────────────────────────────

        [Test]
        public void SecondSave_AppendsOnlyNewItems_AndKeepsExistingBytes()
        {
            var store = Open();
            var session = NewSession();
            store.Save(session);
            var before = File.ReadAllBytes(LogPath());

            AddExchange(session, 2);
            store.Save(session);
            var after = File.ReadAllBytes(LogPath());

            Assert.Greater(after.Length, before.Length);
            CollectionAssert.AreEqual(before, after.Take(before.Length).ToArray(), "existing bytes must not be rewritten");

            var appended = Lines().Skip(3).Select(RecordType).ToList();
            CollectionAssert.AreEqual(new[] { "msg", "msg", "msg", "msg", "turn", "turn", "meta" }, appended);
            AssertSameSession(session, Open().Load("s1"));
        }

        [Test]
        public void SaveWithoutChanges_WritesNothing()
        {
            var store = Open();
            var session = NewSession();
            store.Save(session);
            var length = new FileInfo(LogPath()).Length;

            store.Save(session);
            store.Save(session);

            Assert.AreEqual(length, new FileInfo(LogPath()).Length);
        }

        [Test]
        public void StreamingAssistantTurn_WritesTurnSetRecords()
        {
            var store = Open();
            var session = NewSession();
            store.Save(session);

            session.Turns[3].Content = "answer 1 — streamed more text";
            session.Turns[3].ToolCalls[0].Result = "new result";
            store.Save(session);

            var appended = Lines().Skip(3).ToList();
            Assert.AreEqual(2, appended.Count);
            Assert.AreEqual("turn_set", RecordType(appended[0]));
            StringAssert.Contains("\"i\":3", appended[0]);
            Assert.AreEqual("meta", RecordType(appended[1]));
            AssertSameSession(session, Open().Load("s1"));
        }

        [Test]
        public void SelfChallengeMutatedInPlace_OnTailTurn_IsDetected()
        {
            var store = Open();
            var session = NewSession();
            var data = new SelfChallengeData { NodeATriggered = true };
            session.Turns[3].SelfChallenge = data;
            store.Save(session);

            data.DraftRegenerated = true; // 运行中原地修改
            store.Save(session);

            Assert.IsTrue(Lines().Skip(3).Any(l => RecordType(l) == "turn_set"));
            Assert.IsTrue(Open().Load("s1").Turns[3].SelfChallenge.DraftRegenerated);
        }

        [Test]
        public void MetadataOnlyChange_AppendsSingleMetaRecord_AndListSeesIt()
        {
            var store = Open();
            var session = NewSession();
            store.Save(session);

            session.Title = "重命名后";
            session.Tag = "other";
            store.Save(session);

            var appended = Lines().Skip(3).Select(RecordType).ToList();
            CollectionAssert.AreEqual(new[] { "meta" }, appended);

            var summary = Open().ListSummaries().Single();
            Assert.AreEqual("重命名后", summary.Title);
            Assert.AreEqual("other", summary.Tag);
            Assert.AreEqual(session.Messages.Count, summary.MessageCount);
        }

        // ── 截断 / 改写 ───────────────────────────────────────────────────────

        [Test]
        public void Rewind_WritesTruncateRecord()
        {
            var store = Open();
            var session = NewSession(exchanges: 3);
            store.Save(session);

            // 回退到第 2 个用户轮次之前（与 AgentLoop.TruncateBeforeTurn 同样的 RemoveRange 形态）
            session.Messages.RemoveRange(5, session.Messages.Count - 5);
            session.Turns.RemoveRange(2, session.Turns.Count - 2);
            session.MessageCount = session.Messages.Count;
            store.Save(session);

            var appended = Lines().Skip(3).Select(RecordType).ToList();
            CollectionAssert.AreEqual(new[] { "truncate", "meta" }, appended);
            AssertSameSession(session, Open().Load("s1"));

            // 回退后继续对话
            AddExchange(session, 9);
            store.Save(session);
            AssertSameSession(session, Open().Load("s1"));
        }

        [Test]
        public void HistoryRewrite_CompactsToSnapshot()
        {
            var store = Open();
            var session = NewSession(exchanges: 6);
            store.Save(session);
            AddExchange(session, 7);
            store.Save(session);
            Assert.Greater(Lines().Length, 3);

            // 模拟历史压缩：整段历史被摘要替换
            var summary = new SerializableChatMessage { Role = "system", Content = "summary of earlier conversation" };
            session.Messages = new List<SerializableChatMessage> { session.Messages[0], summary };
            session.Messages.AddRange(Enumerable.Range(0, 12).Select(i => new SerializableChatMessage { Role = "user", Content = "rewritten " + i }));
            session.Turns = session.Turns.Select(t => new SerializableConversationTurn
            {
                Id = t.Id, Role = t.Role, Content = t.Content + " (compressed)", Timestamp = t.Timestamp, MessageEndIndex = -1
            }).ToList();
            store.Save(session);

            var lines = Lines();
            Assert.AreEqual(3, lines.Length, "massive rewrite should compact to header + snapshot + meta");
            Assert.IsTrue(File.Exists(LogPath() + SessionLogStore.BackupSuffix), "compaction keeps the previous file as .bak");
            AssertSameSession(session, Open().Load("s1"));
        }

        [Test]
        public void LogGrowingBeyondTwiceLiveSize_IsCompactedAutomatically()
        {
            var store = Open(compactMinBytes: 0);
            var session = NewSession();
            store.Save(session);

            for (int i = 0; i < 20; i++)
            {
                session.Turns[3].Content = "streaming " + new string('x', 200 + i);
                store.Save(session);
            }

            Assert.LessOrEqual(Lines().Length, 7, "superseded turn_set records must be compacted away");
            AssertSameSession(session, Open().Load("s1"));
        }

        // ── 损坏恢复 ───────────────────────────────────────────────────────────

        [Test]
        public void TornLastLine_IsIgnoredWithWarning_AndNextSaveRepairs()
        {
            var store = Open();
            var session = NewSession();
            store.Save(session);
            var expected = JsonConvert.DeserializeObject<SessionData>(JsonConvert.SerializeObject(session));

            // 模拟追加时崩溃：只写出半行
            File.AppendAllText(LogPath(), "{\"t\":\"msg\",\"i\":9,\"m\":{\"role\":\"us", new UTF8Encoding(false));

            var reopened = Open();
            var loaded = reopened.Load("s1");
            AssertSameSession(expected, loaded);
            Assert.AreEqual(1, _warnings.Count(w => w.Contains("incomplete")), string.Join("\n", _warnings));
            Assert.IsEmpty(_errors);

            AddExchange(loaded, 5);
            reopened.Save(loaded);
            Assert.IsTrue(Lines().All(l => l.StartsWith("{\"t\":", StringComparison.Ordinal)));
            AssertSameSession(loaded, Open().Load("s1"));
        }

        [Test]
        public void TornLastLine_AppendedAfterValidRecords_KeepsThoseRecords()
        {
            var store = Open();
            var session = NewSession();
            store.Save(session);
            AddExchange(session, 2);
            store.Save(session);

            // 截掉最后一条 meta 记录的后半段
            var bytes = File.ReadAllBytes(LogPath());
            File.WriteAllBytes(LogPath(), bytes.Take(bytes.Length - 10).ToArray());

            var loaded = Open().Load("s1");
            Assert.AreEqual(session.Messages.Count, loaded.Messages.Count, "records before the torn meta line are kept");
            Assert.AreEqual(session.Turns.Count, loaded.Turns.Count);
        }

        [Test]
        public void CorruptMiddleLine_KeepsPrefix_CopiesFileAside_AndWarns()
        {
            var store = Open();
            var session = NewSession();
            store.Save(session);
            AddExchange(session, 2);
            store.Save(session);

            var lines = File.ReadAllText(LogPath()).Split('\n').ToList();
            lines[5] = "{\"t\":\"msg\",garbage";
            File.WriteAllText(LogPath(), string.Join("\n", lines), new UTF8Encoding(false));

            var loaded = Open().Load("s1");

            Assert.IsNotNull(loaded);
            Assert.AreEqual(session.Messages.Count - 4 + 2, loaded.Messages.Count, "messages up to the bad line are kept");
            Assert.AreEqual(1, Directory.GetFiles(_dir, "s1.jsonl.corrupted-*").Length);
            Assert.IsTrue(_warnings.Any(w => w.Contains("line 6")), string.Join("\n", _warnings));

            // 已整理：再次加载不再告警
            _warnings.Clear();
            Assert.AreEqual(loaded.Messages.Count, Open().Load("s1").Messages.Count);
            Assert.IsEmpty(_warnings);
        }

        [Test]
        public void UnreadableHeader_RestoresFromBackup_NeverDropsSession()
        {
            var store = Open();
            var session = NewSession();
            store.Save(session);
            session.Title = "v2";
            session.Messages = session.Messages.Take(2).ToList();
            session.Turns = new List<SerializableConversationTurn>();
            Open().Save(session); // 新实例没有已落盘状态 → 整理，旧文件保留为 .bak
            Assert.IsTrue(File.Exists(LogPath() + SessionLogStore.BackupSuffix));

            File.WriteAllText(LogPath(), "not json at all\n{\"t\":\"meta\"}\n");

            var loaded = Open().Load("s1");
            Assert.IsNotNull(loaded, "session must be recovered from .bak");
            Assert.AreEqual("测试会话", loaded.Title);
            Assert.AreEqual(1, Directory.GetFiles(_dir, "s1.jsonl.corrupted-*").Length);
        }

        // ── 旧版 JSON 迁移 ─────────────────────────────────────────────────────

        [Test]
        public void LegacyJson_LoadsThenMigratesOnFirstSave()
        {
            Directory.CreateDirectory(_dir);
            var session = NewSession("legacy");
            var legacyPath = Path.Combine(_dir, "legacy.json");
            File.WriteAllText(legacyPath, JsonConvert.SerializeObject(session, SessionLogStore.LegacySettings));

            var store = Open();
            var summary = store.ListSummaries().Single();
            Assert.AreEqual("legacy", summary.Id);
            Assert.AreEqual("测试会话", summary.Title);

            var loaded = store.Load("legacy");
            AssertSameSession(session, loaded);
            Assert.IsFalse(File.Exists(LogPath("legacy")), "loading alone does not migrate");

            loaded.Title = "迁移后";
            store.Save(loaded);

            Assert.IsTrue(File.Exists(LogPath("legacy")));
            Assert.IsFalse(File.Exists(legacyPath));
            Assert.IsTrue(File.Exists(legacyPath + SessionLogStore.MigratedSuffix));
            Assert.AreEqual("迁移后", Open().ListSummaries().Single().Title, "migrated copy must not appear twice in the list");
            AssertSameSession(loaded, Open().Load("legacy"));
        }

        [Test]
        public void JsonlWinsOverLeftoverLegacyJson()
        {
            Directory.CreateDirectory(_dir);
            var old = NewSession("dup");
            old.Title = "old";
            File.WriteAllText(Path.Combine(_dir, "dup.json"), JsonConvert.SerializeObject(old, SessionLogStore.LegacySettings));

            var current = NewSession("dup", exchanges: 3);
            current.Title = "new";
            var store = Open();
            store.Save(current);
            File.WriteAllText(Path.Combine(_dir, "dup.json"), JsonConvert.SerializeObject(old, SessionLogStore.LegacySettings)); // 模拟改名失败遗留

            var list = Open().ListSummaries();
            Assert.AreEqual(1, list.Count);
            Assert.AreEqual("new", list[0].Title);
            Assert.AreEqual("new", Open().Load("dup").Title);
        }

        // ── 后台写入 / 删除 / Fork 形态 ────────────────────────────────────────

        [Test]
        public void BackgroundSaves_AreAppliedInOrder_AndFlushDrains()
        {
            var store = Open();
            var session = NewSession();
            store.SaveInBackground(session);
            for (int i = 2; i < 6; i++)
            {
                var next = JsonConvert.DeserializeObject<SessionData>(JsonConvert.SerializeObject(session));
                AddExchange(next, i);
                store.SaveInBackground(next);
                session = next;
            }

            Assert.IsTrue(store.Flush(10000));
            Assert.AreEqual(0, store.PendingCount);
            AssertSameSession(session, Open().Load("s1"));
        }

        [Test]
        public void LoadAfterBackgroundSave_SeesLatestContent()
        {
            var store = Open();
            var session = NewSession();
            store.Save(session);
            AddExchange(session, 3);
            store.SaveInBackground(session);

            AssertSameSession(session, store.Load("s1"));
        }

        [Test]
        public void ForkedSessionSharingItems_IsIndependentFromSource()
        {
            var store = Open();
            var source = NewSession("src", exchanges: 3);
            store.Save(source);
            var sourceBytes = File.ReadAllBytes(LogPath("src"));

            var fork = new SessionData
            {
                Id = "fork",
                Title = source.Title + " (fork)",
                CreatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), // 存储精度为毫秒
                UpdatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                Messages = source.Messages.Take(5).ToList(),
                Turns = source.Turns.Take(2).ToList()
            };
            fork.MessageCount = fork.Messages.Count;
            store.Save(fork);

            CollectionAssert.AreEqual(sourceBytes, File.ReadAllBytes(LogPath("src")), "fork must not touch the source session");
            AssertSameSession(fork, Open().Load("fork"));
            AssertSameSession(source, Open().Load("src"));
        }

        [Test]
        public void Delete_RemovesAllSessionFiles()
        {
            Directory.CreateDirectory(_dir);
            var session = NewSession("gone");
            File.WriteAllText(Path.Combine(_dir, "gone.json"), JsonConvert.SerializeObject(session, SessionLogStore.LegacySettings));
            var store = Open();
            var loaded = store.Load("gone");
            store.Save(loaded); // 迁移 → gone.jsonl + gone.json.migrated
            loaded.Messages.RemoveAt(loaded.Messages.Count - 1);
            loaded.Messages = loaded.Messages.Select(m => new SerializableChatMessage { Role = m.Role, Content = (m.Content ?? "") + "!" }).ToList();
            store.Save(loaded); // 改写 → .bak

            Assert.IsTrue(store.Delete("gone"));
            Assert.IsEmpty(Directory.GetFiles(_dir, "gone*"));
            Assert.IsNull(Open().Load("gone"));
            Assert.IsEmpty(Open().ListSummaries());
        }
    }
}
