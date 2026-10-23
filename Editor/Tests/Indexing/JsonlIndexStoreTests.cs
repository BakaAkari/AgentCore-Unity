#if AGENTCORE_INDEXING
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AgentCore.Editor.Components.Indexing.Core;
using AgentCore.Editor.Components.Indexing.Models;
using AgentCore.Editor.Components.Indexing.Query;
using AgentCore.Tests.Editor.Utils;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.Indexing
{
    /// <summary>
    /// JsonlIndexStore 操作日志重放、整理、旧版迁移、损坏阈值与按 Root 清除的单元测试。
    /// 每个用例使用独立的临时目录，不依赖 Unity 工程。
    /// </summary>
    [TestFixture]
    public class JsonlIndexStoreTests
    {
        /// <summary>足够大，测试中不会触发自动整理。</summary>
        private const long NoAutoCompact = long.MaxValue / 4;

        private string _dir;
        private System.Threading.SynchronizationContext _savedContext;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "agentcore-jsonl-tests-" + Guid.NewGuid().ToString("N"));
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

        // ── 辅助 ───────────────────────────────────────────────────────────────

        private JsonlIndexStore Open(long compactMinBytes = NoAutoCompact) => new JsonlIndexStore(_dir, compactMinBytes);

        private sealed class Seeded
        {
            public int WorkspaceId;
            public int RootA;
            public int RootB;
            public int FileA;
            public int FileB;
        }

        /// <summary>
        /// 一个 workspace、两个 Root、每个 Root 一个文件、每个文件若干符号。
        /// </summary>
        private static Seeded Seed(JsonlIndexStore store, int symbolsPerFile = 2)
        {
            var seeded = new Seeded();
            seeded.WorkspaceId = store.UpsertWorkspaceAsync(new IndexWorkspace
            {
                Fingerprint = "fp-test",
                WorkspaceRoot = "C:/ws",
                DisplayName = "test",
            }).Result;

            seeded.RootA = store.UpsertRootAsync(seeded.WorkspaceId, new IndexRoot
            {
                RootPath = "C:/ws/A",
                ScopeType = IndexScopeType.Project,
                ScopeName = "A",
                IsEnabled = true,
            }).Result;
            seeded.RootB = store.UpsertRootAsync(seeded.WorkspaceId, new IndexRoot
            {
                RootPath = "C:/ws/B",
                ScopeType = IndexScopeType.Project,
                ScopeName = "B",
                IsEnabled = true,
            }).Result;

            seeded.FileA = AddFile(store, seeded.WorkspaceId, seeded.RootA, "C:/ws/A/Alpha.cs", "Alpha", symbolsPerFile);
            seeded.FileB = AddFile(store, seeded.WorkspaceId, seeded.RootB, "C:/ws/B/Beta.cs", "Beta", symbolsPerFile);

            store.SetMetadataAsync(seeded.WorkspaceId, CodebaseIndexer.MetaKeyLastFullIndex, "2026-01-01T00:00:00Z").Wait();
            return seeded;
        }

        private static int AddFile(JsonlIndexStore store, int wid, int rootId, string path, string prefix, int symbolCount)
        {
            var fileId = store.UpsertFileAsync(wid, rootId, new IndexedFile { FilePath = path, SymbolCount = symbolCount }).Result;
            store.BulkInsertSymbolsAsync(Enumerable.Range(0, symbolCount).Select(i => new SymbolInfo
            {
                FileId = fileId,
                WorkspaceId = wid,
                RootId = rootId,
                Name = prefix + i,
                FullName = "Test." + prefix + i,
                Namespace = "Test",
                SymbolType = "class",
                ScopeType = IndexScopeType.Project,
                FilePath = path,
            })).Wait();
            return fileId;
        }

        private string OpLogPath(int generation) => Path.Combine(_dir, "gen-" + generation, "oplog.jsonl");

        private static int CountSymbols(JsonlIndexStore store, int fileId) => store.GetSymbolsByFileAsync(fileId).Result.Count;

        // ── 重放 / 整理 ─────────────────────────────────────────────────────────

        [Test]
        public void OpLog_IsReplayedOnReopen()
        {
            Seeded seeded;
            using (var store = Open())
            {
                seeded = Seed(store);
                Assert.Greater(store.PendingOpCount, 0, "写入应追加到操作日志而不是整表重写");
                store.DeleteFileAsync(seeded.FileB).Wait();
            }

            using (var reopened = Open())
            {
                Assert.IsNotNull(reopened.GetWorkspaceByFingerprintAsync("fp-test").Result);
                Assert.AreEqual(2, CountSymbols(reopened, seeded.FileA));
                Assert.IsNull(reopened.GetFileByPathAsync(seeded.WorkspaceId, "C:/ws/B/Beta.cs").Result, "删除操作也要重放");
                Assert.AreEqual(0, CountSymbols(reopened, seeded.FileB));
                Assert.AreEqual(0, reopened.CorruptLineCount);
            }
        }

        [Test]
        public void Compact_StartsNewGeneration_AndPreservesData()
        {
            Seeded seeded;
            int generationBefore;
            using (var store = Open())
            {
                seeded = Seed(store);
                generationBefore = store.Generation;

                store.Compact();

                Assert.AreEqual(generationBefore + 1, store.Generation);
                Assert.AreEqual(0, store.PendingOpCount);
                Assert.IsFalse(Directory.Exists(Path.Combine(_dir, "gen-" + generationBefore)), "旧代目录应在 manifest 切换后删除");
            }

            using (var reopened = Open())
            {
                Assert.AreEqual(generationBefore + 1, reopened.Generation);
                Assert.AreEqual(2, CountSymbols(reopened, seeded.FileA));
                Assert.AreEqual(2, CountSymbols(reopened, seeded.FileB));
                Assert.AreEqual("2026-01-01T00:00:00Z",
                    reopened.GetMetadataAsync(seeded.WorkspaceId, CodebaseIndexer.MetaKeyLastFullIndex).Result);
            }
        }

        [Test]
        public void AutoCompaction_KeepsOpLogBounded()
        {
            Seeded seeded;
            using (var store = Open(compactMinBytes: 1))
            {
                seeded = Seed(store);
                for (var i = 0; i < 20; i++)
                    store.SetMetadataAsync(seeded.WorkspaceId, "k" + i, "v" + i).Wait();

                // 整理阈值为 max(compactMinBytes, 基线大小)：日志长到与基线相当时整理，写放大保持线性
                Assert.Greater(store.Generation, 2, "超过阈值应自动整理");
                Assert.Less(store.PendingOpCount, 40);
            }

            using (var reopened = Open())
            {
                Assert.AreEqual("v19", reopened.GetMetadataAsync(seeded.WorkspaceId, "k19").Result);
                Assert.AreEqual(2, CountSymbols(reopened, seeded.FileB));
            }
        }

        // ── 旧版迁移 ───────────────────────────────────────────────────────────

        [Test]
        public void LegacyLayout_IsMigratedToGenerations()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, "workspaces.jsonl"),
                "{\"Id\":1,\"Fingerprint\":\"fp-legacy\",\"WorkspaceRoot\":\"C:/ws\"}\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(_dir, "roots.jsonl"),
                "{\"Id\":1,\"WorkspaceId\":1,\"RootPath\":\"C:/ws/A\",\"IsEnabled\":true}\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(_dir, "files.jsonl"),
                "{\"Id\":1,\"WorkspaceId\":1,\"RootId\":1,\"FilePath\":\"C:/ws/A/Old.cs\"}\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(_dir, "symbols.jsonl"),
                "{\"Id\":1,\"FileId\":1,\"WorkspaceId\":1,\"RootId\":1,\"Name\":\"Old\",\"FullName\":\"Old\",\"SymbolType\":\"class\"}\n",
                new UTF8Encoding(false));

            using (var store = Open())
            {
                Assert.IsNotNull(store.GetWorkspaceByFingerprintAsync("fp-legacy").Result);
                Assert.AreEqual(1, CountSymbols(store, 1));
                Assert.GreaterOrEqual(store.Generation, 1);
            }

            Assert.IsTrue(File.Exists(Path.Combine(_dir, "manifest.json")));
            Assert.IsFalse(File.Exists(Path.Combine(_dir, "symbols.jsonl")), "迁移后旧版根目录文件应删除");

            using (var reopened = Open())
            {
                Assert.AreEqual(1, CountSymbols(reopened, 1));
            }
        }

        // ── 损坏处理 ───────────────────────────────────────────────────────────

        [Test]
        public void SingleCorruptLine_IsTolerated()
        {
            Seeded seeded;
            int generation;
            using (var store = Open())
            {
                seeded = Seed(store);
                generation = store.Generation;
            }

            File.AppendAllText(OpLogPath(generation), "{not json\n");

            using (var reopened = Open())
            {
                Assert.AreEqual(2, CountSymbols(reopened, seeded.FileA));
                Assert.AreEqual(1, reopened.CorruptLineCount);
                Assert.IsFalse(reopened.RequiresRebuild);
            }
        }

        [Test]
        public void CorruptLinesOverThreshold_RequireRebuild_AndDropLastFullIndex()
        {
            Seeded seeded;
            int generation;
            using (var store = Open())
            {
                seeded = Seed(store);
                generation = store.Generation;
            }

            File.AppendAllText(OpLogPath(generation), "garbage-1\ngarbage-2\n{\"op\":\"nope\",\"d\":{}}\n");

            using (var reopened = Open())
            {
                Assert.IsTrue(reopened.RequiresRebuild, "损坏超过阈值必须标记需要重建，而不是静默返回残缺数据");
                Assert.IsNotNull(reopened.HealthMessage);
                Assert.IsNull(
                    reopened.GetMetadataAsync(seeded.WorkspaceId, CodebaseIndexer.MetaKeyLastFullIndex).Result,
                    "清除 last_full_index_at 使增量入口降级为全量重建");

                reopened.AcknowledgeRebuildAsync().Wait();
                Assert.IsFalse(reopened.RequiresRebuild);
                Assert.AreEqual(0, reopened.CorruptLineCount);
            }

            using (var afterRebuild = Open())
            {
                Assert.IsFalse(afterRebuild.RequiresRebuild, "整理后损坏行不应再被读到");
            }
        }

        [Test]
        public void TornTail_DoesNotSwallowNextOperation()
        {
            Seeded seeded;
            int generation;
            using (var store = Open())
            {
                seeded = Seed(store);
                generation = store.Generation;
            }

            // 模拟写了一半的最后一行（无换行）
            File.AppendAllText(OpLogPath(generation), "{\"op\":\"meta\",\"d\":{\"wid\"");

            using (var store = Open())
            {
                store.SetMetadataAsync(seeded.WorkspaceId, "after_torn", "ok").Wait();
            }

            using (var reopened = Open())
            {
                Assert.AreEqual("ok", reopened.GetMetadataAsync(seeded.WorkspaceId, "after_torn").Result);
                Assert.AreEqual(1, reopened.CorruptLineCount, "只有残行本身计为损坏");
            }
        }

        // ── 按 Root 清除 ────────────────────────────────────────────────────────

        [Test]
        public void ClearRootIndex_OnlyRemovesThatRoot()
        {
            Seeded seeded;
            using (var store = Open())
            {
                seeded = Seed(store);

                var removed = store.ClearRootIndexAsync(seeded.WorkspaceId, seeded.RootA).Result;

                Assert.AreEqual(1, removed);
                Assert.AreEqual(0, store.GetFilesForRootAsync(seeded.RootA).Result.Count);
                Assert.AreEqual(0, CountSymbols(store, seeded.FileA));
                Assert.AreEqual(1, store.GetFilesForRootAsync(seeded.RootB).Result.Count, "其他 Root 的文件不能被清除");
                Assert.AreEqual(2, CountSymbols(store, seeded.FileB));
                Assert.AreEqual(2, store.GetRootsAsync(seeded.WorkspaceId).Result.Count, "Root 记录本身保留");
            }

            using (var reopened = Open())
            {
                Assert.AreEqual(0, reopened.GetFilesForRootAsync(seeded.RootA).Result.Count);
                Assert.AreEqual(1, reopened.GetFilesForRootAsync(seeded.RootB).Result.Count);
            }
        }

        // ── 搜索结果 ───────────────────────────────────────────────────────────

        [Test]
        public void SearchPage_ReportsTotalAndTruncation()
        {
            using (var store = Open())
            {
                var seeded = Seed(store, symbolsPerFile: 5);

                var page = store.SearchSymbolsPageAsync(seeded.WorkspaceId, new SearchQuery { Query = "Alpha", Limit = 2 }).Result;

                Assert.AreEqual(2, page.Matches.Count);
                Assert.AreEqual(5, page.TotalMatches);
                Assert.IsTrue(page.Truncated);
                Assert.AreEqual(SearchTruncation.MaxResults, page.TruncatedReason);

                var full = store.SearchSymbolsPageAsync(seeded.WorkspaceId, new SearchQuery { Query = "Beta", Limit = 50 }).Result;
                Assert.AreEqual(5, full.TotalMatches);
                Assert.IsFalse(full.Truncated);
                Assert.IsNull(full.TruncatedReason);
            }
        }

        [Test]
        public void RegexSearch_Timeout_IsThrown_NotEmptyResult()
        {
            using (var store = Open())
            {
                var seeded = Seed(store, symbolsPerFile: 0);
                AddFile(store, seeded.WorkspaceId, seeded.RootA, "C:/ws/A/Evil.cs", SafeRegexTests.CatastrophicInput, 1);

                var query = new SearchQuery { Query = SafeRegexTests.CatastrophicPattern, Regex = true, Limit = 10 };

                Assert.Throws<RegexMatchTimeoutException>(
                    () => store.SearchSymbolsPageAsync(seeded.WorkspaceId, query).GetAwaiter().GetResult());
            }
        }

        [Test]
        public void RegexSearch_InvalidPattern_Throws_InsteadOfFallingBackToFuzzy()
        {
            using (var store = Open())
            {
                var seeded = Seed(store);
                var query = new SearchQuery { Query = "(unclosed", Regex = true };

                Assert.Throws<ArgumentException>(
                    () => store.SearchSymbolsPageAsync(seeded.WorkspaceId, query).GetAwaiter().GetResult());
            }
        }

        [Test]
        public void DependencyQueries_AreExplicitlyUnsupported()
        {
            using (var store = Open())
            {
                var seeded = Seed(store);

                var ex = Assert.Throws<NotSupportedException>(
                    () => store.FindUsagesAsync(seeded.WorkspaceId, "Alpha0").GetAwaiter().GetResult());
                StringAssert.StartsWith("unsupported", ex.Message);
                Assert.Throws<NotSupportedException>(
                    () => store.GetDependenciesAsync(seeded.WorkspaceId, seeded.FileA).GetAwaiter().GetResult());
            }
        }
    }
}
#endif
