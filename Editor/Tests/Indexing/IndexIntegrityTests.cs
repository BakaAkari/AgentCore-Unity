#if AGENTCORE_INDEXING
using System;
using System.IO;
using System.Linq;
using AgentCore.Editor.Components.Indexing.Core;
using AgentCore.Editor.Components.Indexing.Models;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.Indexing
{
    /// <summary>
    /// 索引完整性：全量完成标记、单文件原子替换、清除工作区时的 Root 状态、按根路径的工作区身份。
    /// 每个用例使用独立的临时目录，不依赖 Unity 工程。
    /// </summary>
    [TestFixture]
    public class IndexIntegrityTests
    {
        /// <summary>足够大，测试中不会触发自动整理。</summary>
        private const long NoAutoCompact = long.MaxValue / 4;

        private string _dir;
        private System.Threading.SynchronizationContext _savedContext;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "agentcore-integrity-tests-" + Guid.NewGuid().ToString("N"));
            // 测试用 .Result/.Wait() 同步等待；清掉 UnitySynchronizationContext 避免续延投递回被阻塞的主线程
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

        private const string WorkspaceRoot = "C:/ws";

        private JsonlIndexStore Open() => new JsonlIndexStore(_dir, NoAutoCompact);

        private static IndexWorkspace Workspace(string fingerprint = null, string svnUrl = "svn://host/trunk") => new IndexWorkspace
        {
            Fingerprint = fingerprint ?? IndexWorkspaceResolver.ComputeWorkspaceFingerprint(WorkspaceRoot),
            WorkspaceRoot = WorkspaceRoot,
            DisplayName = "test",
            VcsType = "Svn",
            SvnUrl = svnUrl,
        };

        private static int AddRoot(JsonlIndexStore store, int wid) => store.UpsertRootAsync(wid, new IndexRoot
        {
            RootPath = WorkspaceRoot + "/A",
            ScopeType = IndexScopeType.Project,
            ScopeName = "A",
            IsEnabled = true,
        }).Result;

        private static SymbolInfo[] Symbols(string path, params string[] names) => names.Select(n => new SymbolInfo
        {
            Name = n,
            FullName = "Test." + n,
            Namespace = "Test",
            SymbolType = "class",
            ScopeType = IndexScopeType.Project,
            FilePath = path,
        }).ToArray();

        private static string[] SymbolNames(JsonlIndexStore store, int fileId) =>
            store.GetSymbolsByFileAsync(fileId).Result.Select(s => s.Name).OrderBy(n => n).ToArray();

        // ── 1 / 3：清除工作区 ────────────────────────────────────────────────────

        [Test]
        public void ClearWorkspace_RemovesFullIndexMarkerAndRootStates()
        {
            int wid, rootId;
            using (var store = Open())
            {
                wid = store.UpsertWorkspaceAsync(Workspace()).Result;
                rootId = AddRoot(store, wid);
                store.SetMetadataAsync(wid, CodebaseIndexer.MetaKeyLastFullIndex, "2026-01-01T00:00:00Z").Wait();
                new IndexRootStateStore(store, wid).SetStateAsync(rootId, IndexRootState.Indexing).Wait();

                store.ClearWorkspaceIndexAsync(wid).Wait();

                Assert.IsNull(store.GetMetadataAsync(wid, CodebaseIndexer.MetaKeyLastFullIndex).Result,
                    "清空后不能再被视为有完整全量索引");
                Assert.AreEqual(IndexRootState.NotIndexed, new IndexRootStateStore(store, wid).LoadAsync(rootId).Result.State,
                    "清空索引要一并清掉 Root 状态，不能卡在 Indexing");
            }

            using (var reopened = Open())
            {
                Assert.IsNull(reopened.GetMetadataAsync(wid, CodebaseIndexer.MetaKeyLastFullIndex).Result, "重放后同样不存在");
                Assert.AreEqual(IndexRootState.NotIndexed, new IndexRootStateStore(reopened, wid).LoadAsync(rootId).Result.State);
            }
        }

        // ── 2：单文件原子替换 ────────────────────────────────────────────────────

        [Test]
        public void ReplaceFileIndex_ReplacesSymbolsAndReusesFileId_AcrossReopen()
        {
            const string path = WorkspaceRoot + "/A/Alpha.cs";
            int wid, rootId, fileId;
            using (var store = Open())
            {
                wid = store.UpsertWorkspaceAsync(Workspace()).Result;
                rootId = AddRoot(store, wid);
                fileId = store.ReplaceFileIndexAsync(wid, rootId,
                    new IndexedFile { FilePath = path, LastModified = 1, SymbolCount = 2 },
                    Symbols(path, "Old1", "Old2")).Result;

                var sameId = store.ReplaceFileIndexAsync(wid, rootId,
                    new IndexedFile { FilePath = path, LastModified = 2, SymbolCount = 1 },
                    Symbols(path, "New1")).Result;

                Assert.AreEqual(fileId, sameId, "同一路径复用文件 Id");
                CollectionAssert.AreEqual(new[] { "New1" }, SymbolNames(store, fileId), "旧符号被整体替换");
            }

            using (var reopened = Open())
            {
                CollectionAssert.AreEqual(new[] { "New1" }, SymbolNames(reopened, fileId), "替换操作要能重放");
                Assert.AreEqual(2, reopened.GetFileByPathAsync(wid, path).Result.LastModified);
            }
        }

        [Test]
        public void ReplaceFallback_WritesSymbolsBeforeFinalFileRecord()
        {
            const string path = WorkspaceRoot + "/A/Beta.cs";
            using (var store = Open())
            {
                var wid = store.UpsertWorkspaceAsync(Workspace()).Result;
                var rootId = AddRoot(store, wid);
                var fileId = IndexFileReplace.FallbackAsync(store, wid, rootId,
                    new IndexedFile { FilePath = path, LastModified = 1 }, Symbols(path, "Old")).Result;

                var sameId = IndexFileReplace.FallbackAsync(store, wid, rootId,
                    new IndexedFile { FilePath = path, LastModified = 5 }, Symbols(path, "A", "B")).Result;

                Assert.AreEqual(fileId, sameId);
                CollectionAssert.AreEqual(new[] { "A", "B" }, SymbolNames(store, fileId));
                Assert.AreEqual(5, store.GetFileByPathAsync(wid, path).Result.LastModified, "符号写完后才写回真实修改时间");
            }
        }

        // ── 5：工作区身份 ────────────────────────────────────────────────────────

        [Test]
        public void WorkspaceFingerprint_DependsOnlyOnNormalizedRoot()
        {
            Assert.AreEqual(
                IndexWorkspaceResolver.ComputeWorkspaceFingerprint("C:/ws"),
                IndexWorkspaceResolver.ComputeWorkspaceFingerprint("c:\\WS\\"));
            Assert.AreNotEqual(
                IndexWorkspaceResolver.ComputeWorkspaceFingerprint("C:/ws"),
                IndexWorkspaceResolver.ComputeWorkspaceFingerprint("C:/other"));
        }

        [Test]
        public void UpsertWorkspace_SvnSwitch_ReusesWorkspace()
        {
            using (var store = Open())
            {
                var wid = store.UpsertWorkspaceAsync(Workspace(svnUrl: "svn://host/trunk")).Result;
                var switched = store.UpsertWorkspaceAsync(Workspace(svnUrl: "svn://host/branches/b1")).Result;

                Assert.AreEqual(wid, switched, "svn switch 不产生新工作区");
                Assert.AreEqual("svn://host/branches/b1", store.GetWorkspaceByFingerprintAsync(Workspace().Fingerprint).Result.SvnUrl,
                    "SVN URL 作为属性原地更新");
            }
        }

        [Test]
        public void UpsertWorkspace_LegacyFingerprint_IsMigratedInPlace()
        {
            const string path = WorkspaceRoot + "/A/Alpha.cs";
            int legacyId;
            using (var store = Open())
            {
                // 旧版指纹含 VCS 信息
                legacyId = store.UpsertWorkspaceAsync(Workspace(fingerprint: "legacy-trunk")).Result;
                var root = AddRoot(store, legacyId);
                store.ReplaceFileIndexAsync(legacyId, root, new IndexedFile { FilePath = path, LastModified = 1 }, Symbols(path, "Kept")).Wait();
            }

            using (var store = Open())
            {
                var newFingerprint = IndexWorkspaceResolver.ComputeWorkspaceFingerprint(WorkspaceRoot);
                Assert.IsNotNull(store.GetWorkspaceByFingerprintAsync(newFingerprint).Result, "新指纹也能按根路径找到旧记录");

                var wid = store.UpsertWorkspaceAsync(Workspace()).Result;
                Assert.AreEqual(legacyId, wid, "同一根路径沿用旧工作区，不新建");
                Assert.AreEqual(newFingerprint, store.GetWorkspaceByFingerprintAsync(newFingerprint).Result.Fingerprint, "指纹迁移为新格式");
                Assert.IsNotNull(store.GetFileByPathAsync(wid, path).Result, "迁移不丢已有索引数据");
            }
        }

        [Test]
        public void Upsert_Unchanged_DoesNotAppendOpLog()
        {
            using (var store = Open())
            {
                var wid = store.UpsertWorkspaceAsync(Workspace()).Result;
                AddRoot(store, wid);
                var ops = store.PendingOpCount;

                store.UpsertWorkspaceAsync(Workspace()).Wait();
                AddRoot(store, wid);

                Assert.AreEqual(ops, store.PendingOpCount, "无变化的工作区 / Root 不追加操作日志");
            }
        }
    }
}
#endif
