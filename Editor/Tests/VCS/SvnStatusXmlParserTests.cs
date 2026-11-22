using System.Linq;
using NUnit.Framework;
using AgentCore.Editor.Components.VCS.Tools;

namespace AgentCore.Tests.Editor.VCS
{
    /// <summary>
    /// <see cref="SvnStatusXmlParser"/> 单元测试。样本结构取自 <c>svn status --xml</c> / <c>svn status -u --xml</c> 输出。
    /// </summary>
    [TestFixture]
    public class SvnStatusXmlParserTests
    {
        private static string Entry(string path, string wcAttributes, string repos = null) =>
            $"<entry path=\"{path}\"><wc-status {wcAttributes}></wc-status>{repos}</entry>";

        private static string Wrap(string entries, string against = null) =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<status>\n<target path=\".\">\n"
            + entries + against + "\n</target>\n</status>\n";

        #region 本地状态

        [TestCase("modified", "none", VcsFileState.Modified)]
        [TestCase("added", "none", VcsFileState.Added)]
        [TestCase("deleted", "none", VcsFileState.Deleted)]
        [TestCase("unversioned", "none", VcsFileState.Untracked)]
        [TestCase("missing", "none", VcsFileState.Missing)]
        [TestCase("replaced", "none", VcsFileState.Modified)]
        [TestCase("obstructed", "none", VcsFileState.Modified)]
        [TestCase("incomplete", "none", VcsFileState.Modified)]
        [TestCase("conflicted", "none", VcsFileState.Conflicted)]
        public void Parse_ItemStates_AreMapped(string item, string props, VcsFileState expected)
        {
            var result = SvnStatusXmlParser.Parse(Wrap(Entry("Assets/a.cs", $"item=\"{item}\" props=\"{props}\"")));

            Assert.AreEqual(1, result.Files.Count);
            Assert.AreEqual(expected, result.Files[0].State);
            Assert.AreEqual(item, result.Files[0].StatusCode);
            Assert.IsFalse(result.IsIncomplete);
        }

        [Test]
        public void Parse_NormalAndIgnored_AreSkipped()
        {
            var xml = Wrap(Entry("a.txt", "item=\"normal\" props=\"none\"")
                           + Entry("Library", "item=\"ignored\" props=\"none\"")
                           + Entry("ext", "item=\"external\" props=\"none\""));

            Assert.IsEmpty(SvnStatusXmlParser.Parse(xml).Files);
        }

        [Test]
        public void Parse_PropertiesOnlyModified_IsModified()
        {
            var result = SvnStatusXmlParser.Parse(Wrap(Entry("Assets", "item=\"normal\" props=\"modified\"")));

            Assert.AreEqual(VcsFileState.Modified, result.Files.Single().State);
            StringAssert.Contains("Properties", result.Files[0].StateDescription);
        }

        #endregion

        #region 冲突

        [Test]
        public void Parse_PropertyConflict_IsConflicted()
        {
            var result = SvnStatusXmlParser.Parse(Wrap(Entry("a.prefab", "item=\"normal\" props=\"conflicted\"")));

            Assert.AreEqual(VcsFileState.Conflicted, result.Files.Single().State);
        }

        [Test]
        public void Parse_TreeConflict_IsConflicted()
        {
            var result = SvnStatusXmlParser.Parse(Wrap(Entry("Assets/moved.cs", "item=\"missing\" props=\"none\" tree-conflicted=\"true\"")));

            Assert.AreEqual(VcsFileState.Conflicted, result.Files.Single().State);
            StringAssert.Contains("tree", result.Files[0].StateDescription);
        }

        #endregion

        #region 路径 / 结构

        [Test]
        public void Parse_NonAsciiAndSpaces_AndChangelists()
        {
            var xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><status><target path=\".\">"
                      + Entry("Assets/中文 目录/贴图 1.png", "item=\"modified\" props=\"none\"")
                      + "</target><changelist name=\"cl\">"
                      + Entry("Assets/a&amp;b.txt", "item=\"added\" props=\"none\"")
                      + "</changelist></status>";

            var result = SvnStatusXmlParser.Parse(xml);

            CollectionAssert.AreEqual(
                new[] { "Assets/中文 目录/贴图 1.png", "Assets/a&b.txt" },
                result.Files.Select(f => f.FilePath).ToArray());
        }

        [Test]
        public void Parse_WcStatusWithCommitChild_IsHandled()
        {
            var xml = Wrap("<entry path=\"a.txt\"><wc-status item=\"modified\" props=\"none\" revision=\"5\">"
                           + "<commit revision=\"3\"><author>alice</author><date>2024-01-01T00:00:00Z</date></commit>"
                           + "</wc-status></entry>");

            Assert.AreEqual(VcsFileState.Modified, SvnStatusXmlParser.Parse(xml).Files.Single().State);
        }

        [Test]
        public void Parse_Truncated_KeepsCompleteEntriesAndFlags()
        {
            var xml = Wrap(Entry("a.txt", "item=\"modified\" props=\"none\"") + Entry("b.txt", "item=\"added\" props=\"none\""));
            var truncated = xml.Substring(0, xml.IndexOf("b.txt") + 10);

            var result = SvnStatusXmlParser.Parse(truncated);

            Assert.IsTrue(result.IsIncomplete);
            Assert.AreEqual("a.txt", result.Files.Single().FilePath);
        }

        [Test]
        public void Parse_EmptyOrNull_ReturnsEmpty()
        {
            Assert.IsEmpty(SvnStatusXmlParser.Parse(null).Files);
            Assert.IsEmpty(SvnStatusXmlParser.Parse("").Files);
            Assert.IsFalse(SvnStatusXmlParser.Parse("").IsIncomplete);
        }

        #endregion

        #region 远端状态（-u）

        [Test]
        public void Parse_ReposStatus_CollectsRemoteChanges()
        {
            var xml = Wrap(
                Entry("local-only.txt", "item=\"modified\" props=\"none\"", "<repos-status item=\"none\" props=\"none\"/>")
                + Entry("remote.txt", "item=\"normal\" props=\"none\"", "<repos-status item=\"modified\" props=\"none\"/>")
                + Entry("new remote 文件.txt", "item=\"none\" props=\"none\"", "<repos-status item=\"added\" props=\"none\"/>")
                + Entry("propchange", "item=\"normal\" props=\"none\"", "<repos-status item=\"none\" props=\"modified\"/>"),
                "<against revision=\"42\"/>");

            var result = SvnStatusXmlParser.Parse(xml);

            CollectionAssert.AreEqual(new[] { "remote.txt", "new remote 文件.txt", "propchange" }, result.RemoteChangedFiles);
            Assert.AreEqual("42", result.AgainstRevision);
            Assert.AreEqual("local-only.txt", result.Files.Single().FilePath);
        }

        #endregion

        #region 锁 / move / copy / switch

        private static string Lock(string token, string owner, string comment = null) =>
            $"<lock><token>{token}</token><owner>{owner}</owner>{(comment == null ? "" : $"<comment>{comment}</comment>")}<created>2026-01-01T00:00:00Z</created></lock>";

        private static string LockedEntry(string path, string item, string wcLock, string reposStatus) =>
            $"<entry path=\"{path}\"><wc-status item=\"{item}\" props=\"none\" revision=\"3\">{wcLock}</wc-status>{reposStatus}</entry>";

        [TestCase("t1", "t1", true, "K")]
        [TestCase("t1", "t2", true, "T")]
        [TestCase("t1", null, true, "B")]
        [TestCase("t1", null, false, "K")]
        [TestCase(null, "t2", true, "O")]
        [TestCase(null, null, true, null)]
        public void MapLockState_FollowsSvnColumnSix(string wcToken, string reposToken, bool hasRepos, string expected)
        {
            Assert.AreEqual(expected, SvnStatusXmlParser.MapLockState(wcToken, reposToken, hasRepos));
        }

        [Test]
        public void Parse_LocalLockOnUnmodifiedFile_IsReportedInLockedFiles()
        {
            var xml = Wrap(LockedEntry("Assets/Main.unity", "normal", Lock("opaquelocktoken:1", "alice", "编辑场景"), null));

            var result = SvnStatusXmlParser.Parse(xml);

            Assert.IsEmpty(result.Files);
            var locked = result.LockedFiles.Single();
            Assert.AreEqual("K", locked.LockState);
            Assert.AreEqual("alice", locked.LockOwner);
            Assert.AreEqual("编辑场景", locked.LockComment);
            Assert.AreEqual(VcsFileState.Unmodified, locked.State);
        }

        [Test]
        public void Parse_RemoteLocks_DistinguishOtherStolenBroken()
        {
            var xml = Wrap(
                LockedEntry("other.prefab", "normal", "", "<repos-status item=\"none\" props=\"none\">" + Lock("t9", "bob") + "</repos-status>")
                + LockedEntry("stolen.prefab", "modified", Lock("t1", "me"), "<repos-status item=\"none\" props=\"none\">" + Lock("t2", "carol") + "</repos-status>")
                + LockedEntry("broken.prefab", "normal", Lock("t3", "me"), "<repos-status item=\"none\" props=\"none\"/>")
                + LockedEntry("mine.prefab", "modified", Lock("t4", "me"), "<repos-status item=\"none\" props=\"none\">" + Lock("t4", "me") + "</repos-status>"),
                "<against revision=\"5\"/>");

            var result = SvnStatusXmlParser.Parse(xml);
            var byPath = result.LockedFiles.ToDictionary(f => f.FilePath);

            Assert.AreEqual("O", byPath["other.prefab"].LockState);
            Assert.AreEqual("bob", byPath["other.prefab"].LockOwner);
            Assert.AreEqual("T", byPath["stolen.prefab"].LockState);
            Assert.AreEqual("carol", byPath["stolen.prefab"].LockOwner);
            Assert.AreEqual("B", byPath["broken.prefab"].LockState);
            Assert.AreEqual("K", byPath["mine.prefab"].LockState);
            // 内容修改的锁定文件同时出现在 Files 中
            CollectionAssert.AreEquivalent(new[] { "stolen.prefab", "mine.prefab" }, result.Files.Select(f => f.FilePath));
            Assert.IsEmpty(result.RemoteChangedFiles);
        }

        [Test]
        public void Parse_MoveAndCopy_AreMapped()
        {
            var xml = Wrap(
                Entry("new.cs", "item=\"added\" props=\"none\" copied=\"true\" moved-from=\"old.cs\"")
                + Entry("old.cs", "item=\"deleted\" props=\"none\" moved-to=\"new.cs\"")
                + Entry("copy.cs", "item=\"added\" props=\"none\" copied=\"true\""));

            var result = SvnStatusXmlParser.Parse(xml);
            var byPath = result.Files.ToDictionary(f => f.FilePath);

            Assert.AreEqual(VcsFileState.Renamed, byPath["new.cs"].State);
            Assert.AreEqual("old.cs", byPath["new.cs"].OriginalPath);
            Assert.AreEqual(VcsFileState.Deleted, byPath["old.cs"].State);
            Assert.AreEqual("new.cs", byPath["old.cs"].MovedToPath);
            Assert.AreEqual(VcsFileState.Copied, byPath["copy.cs"].State);
        }

        [Test]
        public void Parse_SwitchedAndWcLocked_AreReported()
        {
            var xml = Wrap(
                Entry("Assets/Plugins", "item=\"normal\" props=\"none\" switched=\"true\"")
                + Entry("Assets", "item=\"normal\" props=\"none\" wc-locked=\"true\""));

            var result = SvnStatusXmlParser.Parse(xml);

            Assert.IsEmpty(result.LockedFiles);
            Assert.IsTrue(result.Warnings.Any(w => w.Contains("cleanup")));
            Assert.IsTrue(result.Warnings.Any(w => w.Contains("Assets/Plugins") && w.Contains("switched")));
        }

        #endregion
    }
}
