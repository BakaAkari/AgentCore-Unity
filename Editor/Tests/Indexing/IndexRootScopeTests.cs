#if AGENTCORE_INDEXING
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AgentCore.Editor.Components.Indexing.Core;
using AgentCore.Editor.Components.Indexing.Models;
using AgentCore.Editor.Components.Indexing.Roots;
using Newtonsoft.Json;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.Indexing
{
    /// <summary>
    /// 索引范围：Root 划分、OnDemand 不参与自动索引、扫描剪枝、定向增量的包含/排除判断。
    /// </summary>
    [TestFixture]
    public class IndexRootScopeTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "agentcore_idx_scope_" + Guid.NewGuid().ToString("N")).Replace('\\', '/');
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string Touch(string relativePath)
        {
            var path = Path.Combine(_dir, relativePath).Replace('\\', '/');
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "class C {}");
            return path;
        }

        private static IndexRoot Root(string path, IndexRootRole role = IndexRootRole.EditableProjectCode)
        {
            return new IndexRoot { RootPath = path, Role = role, ExcludePatterns = new List<string> { "Generated/" } };
        }

        private static List<string> Relative(IEnumerable<string> files, string baseDir)
        {
            return files.Select(f => f.Substring(baseDir.Length + 1)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        }

        [Test]
        public void UnityRootProvider_IndexesWholeAssets_EvenWithScriptsFolder()
        {
            Touch("Assets/Scripts/A.cs");
            Touch("Assets/Art/B.cs");
            Touch("Assets/Plugins/P.cs");

            var roots = new UnityRootProvider().DiscoverRoots(new IndexWorkspace { WorkspaceRoot = _dir, UnityRoot = _dir });

            var assets = roots.Single(r => r.RootPath == _dir + "/Assets");
            Assert.IsTrue(IndexingSchedulePolicy.ParticipatesInBackgroundLoop(assets));
            Assert.IsFalse(roots.Any(r => r.RootPath.EndsWith("/Assets/Scripts")));
            var plugins = roots.Single(r => r.RootPath == _dir + "/Assets/Plugins");
            Assert.IsFalse(IndexingSchedulePolicy.ParticipatesInBackgroundLoop(plugins), "Assets/Plugins 只经 index_scope 索引");

            var scan = IndexRootScanner.Scan(assets, roots, long.MaxValue, CancellationToken.None);
            CollectionAssert.AreEqual(new[] { "Art/B.cs", "Scripts/A.cs" }, Relative(scan.Files, _dir + "/Assets"));
        }

        [TestCase(IndexRootRole.CommercialPlugin)]
        [TestCase(IndexRootRole.EngineCode)]
        [TestCase(IndexRootRole.GeneratedCode)]
        [TestCase(IndexRootRole.ReadOnlyReference)]
        public void OnDemandRoles_DoNotParticipateInBackgroundLoop(IndexRootRole role)
        {
            Assert.IsFalse(IndexingSchedulePolicy.ParticipatesInBackgroundLoop(new IndexRoot { Role = role }));
        }

        [Test]
        public void Scan_PrunesVcsUnityGeneratedAndNestedRoots()
        {
            Touch("Tools/T.cs");
            Touch(".svn/pristine/X.cs");
            Touch("Game/Assets/G.cs");
            Touch("Game/ProjectSettings/ProjectVersion.txt");
            Touch("Game/Library/PackageCache/L.cs");
            Touch("Game/Temp/T.cs");
            Touch("Game/Assets/Library/UserLib.cs"); // Assets 里同名目录是用户代码，保留
            Touch("Shared/S.cs");

            var ancestor = Root(_dir);
            var nested = Root(_dir + "/Shared");
            var scan = IndexRootScanner.Scan(ancestor, new[] { ancestor, nested }, long.MaxValue, CancellationToken.None);

            Assert.IsTrue(scan.Complete);
            CollectionAssert.AreEqual(
                new[] { "Game/Assets/G.cs", "Game/Assets/Library/UserLib.cs", "Tools/T.cs" },
                Relative(scan.Files, _dir));
        }

        [Test]
        public void ShouldIndex_UsesSameRulesAsScan()
        {
            Touch("Game/Assets/A.cs");
            Touch("Game/ProjectSettings/ProjectVersion.txt");
            var root = Root(_dir);
            var roots = new[] { root };

            Assert.IsTrue(IndexRootScanner.ShouldIndex(_dir + "/Game/Assets/A.cs", root, roots));
            Assert.IsFalse(IndexRootScanner.ShouldIndex(_dir + "/Game/Assets/Game.asmdef", root, roots));
            Assert.IsFalse(IndexRootScanner.ShouldIndex(_dir + "/Game/Assets/config.json", root, roots));
            Assert.IsFalse(IndexRootScanner.ShouldIndex(_dir + "/Game/Assets/Generated/G.cs", root, roots));
            Assert.IsFalse(IndexRootScanner.ShouldIndex(_dir + "/Game/Library/L.cs", root, roots));
            Assert.IsFalse(IndexRootScanner.ShouldIndex(_dir + "/.git/X.cs", root, roots));
        }

        [Test]
        public void WorkspaceChildRootProvider_MatchesKeywordsAsWholeWordPrefix()
        {
            foreach (var name in new[] { "Builds", "AgentCore", "UICommon", "build_tools", "Gameplay2", "Docs" })
                Directory.CreateDirectory(Path.Combine(_dir, name));

            var roots = new WorkspaceChildRootProvider().DiscoverRoots(new IndexWorkspace { WorkspaceRoot = _dir });
            var byName = roots.ToDictionary(r => r.DisplayName, r => r.ScopeType);

            Assert.IsFalse(byName.ContainsKey("Builds"));
            Assert.IsFalse(byName.ContainsKey("AgentCore"));
            Assert.IsFalse(byName.ContainsKey("Docs"));
            Assert.AreEqual(IndexScopeType.UI, byName["UICommon"]);
            Assert.AreEqual(IndexScopeType.Tools, byName["build_tools"]);
            Assert.AreEqual(IndexScopeType.Mode, byName["Gameplay2"]);
        }

        [Test]
        public void IndexRoot_JsonRoundTrip_DoesNotDuplicatePatterns()
        {
            var root = new IndexRoot();
            var json = JsonConvert.SerializeObject(root);

            var copy = JsonConvert.DeserializeObject<IndexRoot>(JsonConvert.SerializeObject(JsonConvert.DeserializeObject<IndexRoot>(json)));

            CollectionAssert.AreEqual(root.IncludePatterns, copy.IncludePatterns);
            CollectionAssert.AreEqual(root.ExcludePatterns, copy.ExcludePatterns);
        }
    }
}
#endif
