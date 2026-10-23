#if AGENTCORE_VCS
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
    }
}
#endif
