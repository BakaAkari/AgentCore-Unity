#if AGENTCORE_INDEXING
using System;
using System.IO;
using System.Linq;
using AgentCore.Editor.Components.Indexing.Core;
using AgentCore.Editor.Components.Indexing.Models;
using AgentCore.Editor.Components.Indexing.Query;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.Indexing
{
    /// <summary>
    /// 索引查询端的关键行为：全名精确匹配、按 ID 查找、匹配质量排序、文本搜索的 Root 过滤、
    /// 索引完整性判断，以及提取器的全名（外层类型链）与声明片段换行。
    /// </summary>
    [TestFixture]
    public class IndexQueryTests
    {
        private const long NoAutoCompact = long.MaxValue / 4;

        private string _dir;
        private System.Threading.SynchronizationContext _savedContext;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "agentcore-query-tests-" + Guid.NewGuid().ToString("N"));
            // 测试用 .Result/.Wait() 同步等待；清掉 UnitySynchronizationContext，避免续延投递回被阻塞的主线程死锁
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

        private JsonlIndexStore Open() => new JsonlIndexStore(_dir, NoAutoCompact);

        private static int AddWorkspace(JsonlIndexStore store)
        {
            return store.UpsertWorkspaceAsync(new IndexWorkspace { Fingerprint = "fp-query", WorkspaceRoot = "C:/ws", DisplayName = "q" }).Result;
        }

        private static int AddRoot(JsonlIndexStore store, int wid, string name, bool enabled = true)
        {
            return store.UpsertRootAsync(wid, new IndexRoot
            {
                RootPath = "C:/ws/" + name,
                DisplayName = name,
                ScopeType = IndexScopeType.Project,
                ScopeName = name,
                IsEnabled = enabled,
            }).Result;
        }

        /// <summary>在一个文件中插入若干符号：(Name, FullName, ScopeType)。</summary>
        private static void AddSymbols(JsonlIndexStore store, int wid, int rootId, string path, params (string name, string fullName, IndexScopeType scope)[] symbols)
        {
            var fileId = store.UpsertFileAsync(wid, rootId, new IndexedFile { FilePath = path, SymbolCount = symbols.Length }).Result;
            store.BulkInsertSymbolsAsync(symbols.Select(s => new SymbolInfo
            {
                FileId = fileId,
                WorkspaceId = wid,
                RootId = rootId,
                Name = s.name,
                FullName = s.fullName,
                Namespace = "Game",
                SymbolType = "class",
                ScopeType = s.scope,
                FilePath = path,
            })).Wait();
        }

        // ── 查询 ───────────────────────────────────────────────────────────────

        [Test]
        public void NonFuzzySearch_MatchesFullNameIgnoringCase_AndLookupById()
        {
            using (var store = Open())
            {
                var wid = AddWorkspace(store);
                var root = AddRoot(store, wid, "A");
                AddSymbols(store, wid, root, "C:/ws/A/Outer.cs",
                    ("Outer", "Game.Outer", IndexScopeType.Project),
                    ("Run", "Game.Outer.Run", IndexScopeType.Project));

                var hits = store.SearchSymbolsAsync(wid, new SearchQuery { Query = "game.outer.RUN", Fuzzy = false }).Result;
                Assert.AreEqual(1, hits.Count, "非模糊查询应能按全名（不区分大小写）命中");
                Assert.AreEqual("Game.Outer.Run", hits[0].FullName);

                var byId = store.GetSymbolByIdAsync(wid, hits[0].Id).Result;
                Assert.IsNotNull(byId);
                Assert.AreEqual("Game.Outer.Run", byId.FullName);
                Assert.IsNull(store.GetSymbolByIdAsync(wid, 99999).Result);
            }
        }

        [Test]
        public void FuzzySearch_RanksByMatchQualityBeforeScope()
        {
            using (var store = Open())
            {
                var wid = AddWorkspace(store);
                var root = AddRoot(store, wid, "A");
                // 精确命中放在优先级较低的 Shared scope，旧排序（先 scope 后名称）会把它排到后面
                AddSymbols(store, wid, root, "C:/ws/A/All.cs",
                    ("Spawn", "Game.Player.Spawn", IndexScopeType.Project),
                    ("MyPlayerView", "Game.MyPlayerView", IndexScopeType.Project),
                    ("PlayerController", "Game.PlayerController", IndexScopeType.Project),
                    ("Player", "Game.Player", IndexScopeType.Shared));

                var hits = store.SearchSymbolsAsync(wid, new SearchQuery { Query = "player", Fuzzy = true }).Result;
                CollectionAssert.AreEqual(
                    new[] { "Player", "PlayerController", "MyPlayerView", "Spawn" },
                    hits.Select(h => h.Name).ToArray(),
                    "排序应为 Name 精确 > Name 前缀 > Name 包含 > 仅 FullName 包含");
            }
        }

        [Test]
        public void TextSearch_SkipsDisabledRoots()
        {
            using (var store = Open())
            {
                var wid = AddWorkspace(store);
                var enabled = AddRoot(store, wid, "A");
                var disabled = AddRoot(store, wid, "B", enabled: false);
                AddSymbols(store, wid, enabled, "C:/ws/A/Inv.cs", ("Inventory", "Game.Inventory", IndexScopeType.Project));
                AddSymbols(store, wid, disabled, "C:/ws/B/Inv.cs", ("InventoryOld", "Game.InventoryOld", IndexScopeType.Project));

                var page = store.SearchSymbolsByTextPageAsync(wid, "inventory", 10).Result;
                CollectionAssert.AreEqual(new[] { "Inventory" }, page.Matches.Select(m => m.Name).ToArray());
            }
        }

        // ── 索引完整性 ─────────────────────────────────────────────────────────

        [Test]
        public void Completeness_EmptyOrUnfinishedIndex_IsReportedIncomplete()
        {
            using (var store = Open())
            {
                var wid = AddWorkspace(store);
                var root = AddRoot(store, wid, "A");

                // 空库：root 即使标为 Ready 也不能算完整
                new IndexRootStateStore(store, wid).MarkReadyAsync(root, 0, 0).Wait();
                var empty = IndexCompleteness.ReadAsync(store, wid, backgroundRunning: false).Result;
                Assert.IsTrue(empty.Incomplete);
                StringAssert.Contains("索引存储为空", empty.Describe());
                StringAssert.Contains("last_full_index_at", empty.Describe());

                // 有数据 + 全量时间 + root Ready：完整，结果非空时不再给警告
                AddSymbols(store, wid, root, "C:/ws/A/X.cs", ("X", "Game.X", IndexScopeType.Project));
                store.SetMetadataAsync(wid, CodebaseIndexer.MetaKeyLastFullIndex, "2026-01-01T00:00:00Z").Wait();
                var full = IndexCompleteness.ReadAsync(store, wid, backgroundRunning: false).Result;
                Assert.IsFalse(full.Incomplete, full.Describe());
                Assert.IsNull(full.WarningFor(1));
                StringAssert.Contains("索引完整", full.WarningFor(0), "0 结果时也要写明索引状态");

                // 新增一个从未索引的已启用 root：不完整
                AddRoot(store, wid, "B");
                var partial = IndexCompleteness.ReadAsync(store, wid, backgroundRunning: false).Result;
                Assert.IsTrue(partial.Incomplete);
                StringAssert.Contains("NotIndexed", partial.Describe());
            }
        }

        // ── 提取器 ─────────────────────────────────────────────────────────────

        [Test]
        public void Extractor_FullNameIncludesContainingTypes_AndSnippetHasNoDoubleCarriageReturn()
        {
            Directory.CreateDirectory(_dir);
            var path = Path.Combine(_dir, "Nested.cs");
            File.WriteAllText(path, string.Join("\r\n",
                "namespace Game",
                "{",
                "    public class Outer",
                "    {",
                "        public class Inner",
                "        {",
                "            public void Run() { }",
                "        }",
                "    }",
                "}",
                ""));

            var result = RoslynSymbolExtractor.ExtractFromFile(path, 1, null, null);
            Assert.IsTrue(result.IsSuccess, result.ErrorMessage);

            var names = result.Symbols.Select(s => s.FullName).ToList();
            CollectionAssert.Contains(names, "Game.Outer");
            CollectionAssert.Contains(names, "Game.Outer.Inner");
            CollectionAssert.Contains(names, "Game.Outer.Inner.Run");
            Assert.IsFalse(result.Symbols.Any(s => s.DeclarationSnippet != null && s.DeclarationSnippet.Contains("\r")),
                "声明片段不应残留 '\\r'（CRLF 文件曾产生 \"\\r\\r\\n\"）");
        }
    }
}
#endif
