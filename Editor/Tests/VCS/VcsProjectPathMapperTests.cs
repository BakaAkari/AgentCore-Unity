#if AGENTCORE_VCS
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using AgentCore.Editor.Components.VCS.Tools;

namespace AgentCore.Tests.Editor.VCS
{
    /// <summary>
    /// version_control 工具的路径约定（<see cref="VcsProjectPathMapper"/>）：返回路径在工程内时为工程相对路径，
    /// 输入同时接受工程相对 / VCS 根相对 / 绝对路径。Unity 工程位于工作副本子目录（中文 + 空格）。
    /// </summary>
    [TestFixture]
    public class VcsProjectPathMapperTests
    {
        private static readonly string WcRoot = Path.Combine(Path.GetTempPath(), "agentcore-map", "wc");
        private static readonly string UnityRoot = Path.Combine(WcRoot, "Game", "项目 A");

        private static VcsProjectPathMapper Nested() => new VcsProjectPathMapper(WcRoot, UnityRoot);

        [Test]
        public void ToOutput_WcRelativeInsideProject_BecomesProjectRelative()
        {
            var m = Nested();
            Assert.IsFalse(m.ProjectIsVcsRoot);
            Assert.AreEqual("Assets/中文 资源/a.prefab", m.ToOutput("Game/项目 A/Assets/中文 资源/a.prefab"));
            Assert.AreEqual("Assets/a.prefab", m.ToOutput(@"Game\项目 A\Assets\a.prefab"));
            Assert.AreEqual("ProjectSettings/ProjectSettings.asset", m.ToOutput("Game/项目 A/ProjectSettings/ProjectSettings.asset"));
            Assert.AreEqual(".", m.ToOutput("Game/项目 A"));
        }

        [Test]
        public void ToOutput_AbsoluteInsideProject_BecomesProjectRelative()
        {
            var m = Nested();
            Assert.AreEqual("Assets/b.mat", m.ToOutput(Path.Combine(UnityRoot, "Assets", "b.mat")));
        }

        [Test]
        public void ToOutput_OutsideProject_KeptAsIs()
        {
            var m = Nested();
            Assert.AreEqual("Docs/readme.txt", m.ToOutput("Docs/readme.txt"));
            Assert.AreEqual("Game/other/x.txt", m.ToOutput("Game/other/x.txt"));
            var outside = Path.Combine(WcRoot, "Docs", "r.txt");
            Assert.AreEqual(outside, m.ToOutput(outside));
            Assert.AreEqual("//depot/main/a.txt", m.ToOutput("//depot/main/a.txt"));
            Assert.IsNull(m.ToOutput((string)null));
            Assert.AreEqual("", m.ToOutput(""));
        }

        [Test]
        public void ToOutput_ProjectIsVcsRoot_PathsUnchanged()
        {
            var m = new VcsProjectPathMapper(UnityRoot, UnityRoot);
            Assert.IsTrue(m.ProjectIsVcsRoot);
            Assert.AreEqual("Assets/a.prefab", m.ToOutput("Assets/a.prefab"));
        }

        [Test]
        public void ToOutput_UnknownRoots_PathsUnchanged()
        {
            var m = new VcsProjectPathMapper(null, null);
            Assert.AreEqual("Game/项目 A/Assets/a.prefab", m.ToOutput("Game/项目 A/Assets/a.prefab"));
        }

        [Test]
        public void ToOutputKeys_MapsKeysAndMergesCollisions()
        {
            var m = Nested();
            var mapped = m.ToOutputKeys(new Dictionary<string, string>
            {
                { "Game/项目 A/Assets/a.txt", "locked" },
                { Path.Combine(UnityRoot, "Assets", "a.txt"), "out of date" },
                { "Docs/r.txt", "unversioned" }
            });
            Assert.AreEqual(2, mapped.Count);
            Assert.AreEqual("locked; out of date", mapped["Assets/a.txt"]);
            Assert.AreEqual("unversioned", mapped["Docs/r.txt"]);
        }

        [Test]
        public void ToOutput_List_PreservesOrder()
        {
            var m = Nested();
            CollectionAssert.AreEqual(new[] { "Assets/a.txt", "Docs/b.txt" }, m.ToOutput(new[] { "Game/项目 A/Assets/a.txt", "Docs/b.txt" }));
            Assert.IsEmpty(m.ToOutput((IEnumerable<string>)null));
        }

        [Test]
        public void ResolveInput_ProjectRelative_BecomesAbsoluteWhenOnlyItExists()
        {
            var m = Nested();
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.Combine(UnityRoot, "Assets", "a.prefab"),
                Path.Combine(UnityRoot, "Assets"),
                Path.Combine(WcRoot, "Docs", "r.txt")
            };
            Func<string, bool> exists = p => existing.Contains(Path.GetFullPath(p).TrimEnd('\\', '/'));

            Assert.AreEqual(Path.Combine(UnityRoot, "Assets", "a.prefab"), m.ResolveInput("Assets/a.prefab", exists));
            // 已删除的文件：只有工程侧父目录存在
            Assert.AreEqual(Path.Combine(UnityRoot, "Assets", "deleted.prefab"), m.ResolveInput("Assets/deleted.prefab", exists));
            // VCS 根相对路径存在：原样
            Assert.AreEqual("Docs/r.txt", m.ResolveInput("Docs/r.txt", exists));
            // 绝对路径 / depot 路径：原样
            var abs = Path.Combine(WcRoot, "x.txt");
            Assert.AreEqual(abs, m.ResolveInput(abs, exists));
            Assert.AreEqual("//depot/a", m.ResolveInput("//depot/a", exists));
            // 都不存在：原样，交给 VCS 报错
            Assert.AreEqual("Nope/x.txt", m.ResolveInput("Nope/x.txt", exists));
        }

        [Test]
        public void ResolveInput_ProjectIsVcsRoot_Unchanged()
        {
            var m = new VcsProjectPathMapper(UnityRoot, UnityRoot);
            Assert.AreEqual("Assets/a.prefab", m.ResolveInput("Assets/a.prefab", _ => true));
        }

        [Test]
        public void ResolveInput_EscapingVcsRoot_Unchanged()
        {
            var m = Nested();
            Assert.AreEqual("../../../../outside.txt", m.ResolveInput("../../../../outside.txt", _ => false));
        }
    }
}
#endif
