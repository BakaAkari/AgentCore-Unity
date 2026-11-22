using System;
using System.IO;
using AgentCore.Editor.Components.Indexing.Core;
using AgentCore.Editor.Components.Indexing.Models;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.Indexing
{
    /// <summary>
    /// 索引运行元数据（last_full_index_at / last_index_at / last_scope_index_at）的语义测试：
    /// Scope 重建刷新 last_index_at 与 Scope 时间，但不冒充全量重建。
    /// </summary>
    [TestFixture]
    public class IndexRunMetadataTests
    {
        private const long NoAutoCompact = long.MaxValue / 4;

        private static readonly DateTime FullAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime ScopeAt = new DateTime(2026, 1, 2, 12, 30, 0, DateTimeKind.Utc);

        private string _dir;
        private System.Threading.SynchronizationContext _savedContext;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "agentcore-runmeta-tests-" + Guid.NewGuid().ToString("N"));
            // 测试用 .Result/.Wait() 同步等待；清掉 UnitySynchronizationContext，让 store 内部 await 的续延走线程池，
            // 否则续延投递回被阻塞的主线程会死锁
            _savedContext = System.Threading.SynchronizationContext.Current;
            System.Threading.SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void TearDown()
        {
            System.Threading.SynchronizationContext.SetSynchronizationContext(_savedContext);
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

        private static int SeedWorkspace(JsonlIndexStore store)
        {
            return store.UpsertWorkspaceAsync(new IndexWorkspace
            {
                Fingerprint = "fp-runmeta",
                WorkspaceRoot = "C:/ws",
                DisplayName = "test",
            }).Result;
        }

        private static IndexRoot Root(int id, string name) => new IndexRoot
        {
            Id = id,
            RootPath = "C:/ws/" + name,
            ScopeType = IndexScopeType.Project,
            ScopeName = name,
            IsEnabled = true,
        };

        [Test]
        public void ScopeRun_UpdatesLastIndexAndScope_ButNotFull()
        {
            using (var store = new JsonlIndexStore(_dir, NoAutoCompact))
            {
                var wid = SeedWorkspace(store);
                IndexRunMetadata.RecordAsync(store, wid, IndexRunKind.Full, null, FullAt).Wait();
                IndexRunMetadata.RecordAsync(store, wid, IndexRunKind.Scope, new[] { Root(7, "Shared") }, ScopeAt).Wait();

                var t = IndexRunMetadata.ReadAsync(store, wid).Result;

                Assert.AreEqual(FullAt, t.LastFullIndexAt, "Scope 重建不得改写 last_full_index_at");
                Assert.AreEqual(ScopeAt, t.LastScopeIndexAt);
                Assert.AreEqual(ScopeAt, t.LastIndexAt);
                Assert.AreEqual("scope", t.LastIndexKind);
                StringAssert.Contains("#7", t.LastScopeIndexRoots);
                StringAssert.Contains("Shared", t.LastScopeIndexRoots);
                Assert.IsNull(t.LastIncrementalAt, "Scope 重建不再写 last_incremental_at");
                Assert.IsNotNull(IndexRunMetadata.Describe(t), "Scope 晚于全量时应给出解读提示");
            }
        }

        [Test]
        public void FullRun_UpdatesFullAndLastIndex()
        {
            using (var store = new JsonlIndexStore(_dir, NoAutoCompact))
            {
                var wid = SeedWorkspace(store);
                IndexRunMetadata.RecordAsync(store, wid, IndexRunKind.Scope, new[] { Root(1, "A") }, FullAt).Wait();
                IndexRunMetadata.RecordAsync(store, wid, IndexRunKind.Full, null, ScopeAt).Wait();

                var t = IndexRunMetadata.ReadAsync(store, wid).Result;

                Assert.AreEqual(ScopeAt, t.LastFullIndexAt);
                Assert.AreEqual(ScopeAt, t.LastIndexAt);
                Assert.AreEqual("full", t.LastIndexKind);
                Assert.IsNull(IndexRunMetadata.Describe(t), "全量晚于 Scope 时无需提示");
            }
        }

        [Test]
        public void IncrementalRun_UpdatesIncrementalAndLastIndex()
        {
            using (var store = new JsonlIndexStore(_dir, NoAutoCompact))
            {
                var wid = SeedWorkspace(store);
                IndexRunMetadata.RecordAsync(store, wid, IndexRunKind.Incremental, null, ScopeAt).Wait();

                var t = IndexRunMetadata.ReadAsync(store, wid).Result;

                Assert.IsNull(t.LastFullIndexAt);
                Assert.AreEqual(ScopeAt, t.LastIncrementalAt);
                Assert.AreEqual(ScopeAt, t.LastIndexAt);
                Assert.AreEqual("incremental", t.LastIndexKind);
            }
        }

        [Test]
        public void GetStats_ParsesIsoTimestampsWrittenByIndexer()
        {
            using (var store = new JsonlIndexStore(_dir, NoAutoCompact))
            {
                var wid = SeedWorkspace(store);
                IndexRunMetadata.RecordAsync(store, wid, IndexRunKind.Full, null, FullAt).Wait();
                IndexRunMetadata.RecordAsync(store, wid, IndexRunKind.Scope, new[] { Root(2, "B") }, ScopeAt).Wait();

                var stats = store.GetStatsAsync(wid).Result;

                Assert.AreEqual(FullAt, stats.LastFullIndexAt, "ISO 8601 时间戳此前被按 Unix 秒解析而丢失");
                Assert.AreEqual(ScopeAt, stats.LastScopeIndexAt);
                Assert.AreEqual(ScopeAt, stats.LastIndexAt);
                Assert.AreEqual("scope", stats.LastIndexKind);
            }
        }

        [Test]
        public void ParseTime_AcceptsIsoAndUnixSeconds()
        {
            Assert.AreEqual(FullAt, IndexRunMetadata.ParseTime(FullAt.ToString("O")));
            Assert.AreEqual(FullAt, IndexRunMetadata.ParseTime(new DateTimeOffset(FullAt).ToUnixTimeSeconds().ToString()));
            Assert.IsNull(IndexRunMetadata.ParseTime(null));
            Assert.IsNull(IndexRunMetadata.ParseTime("not a time"));
        }
    }
}
