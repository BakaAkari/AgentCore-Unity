#if AGENTCORE_VCS
using System.IO;
using System.Linq;
using NUnit.Framework;
using AgentCore.Editor.Components.VCS.Tools;

namespace AgentCore.Tests.Editor.VCS
{
    /// <summary>
    /// <see cref="SvnAdapter"/> 的文本 / XML 解析与 svn:ignore 规则计算（<see cref="VersionControlTool"/>）单元测试。
    /// 样本取自 svn 1.14 的实际输出。
    /// </summary>
    [TestFixture]
    public class SvnAdapterParserTests
    {
        #region update 冲突

        [Test]
        public void ParseSvnUpdateConflicts_DetectsTextPropertyAndTreeConflicts()
        {
            const string output =
                "Updating '.':\r\n" +
                "C    Assets\\a.txt\r\n" +
                " C   Assets\\props.txt\r\n" +
                "   C Assets\\tree.prefab.meta\r\n" +
                "U    Assets\\ok.txt\r\n" +
                "G    Assets\\merged.cs\r\n" +
                "Conflict discovered in 'Assets\\x.txt'.\r\n" +
                "Updated to revision 5.\r\n" +
                "Summary of conflicts:\r\n" +
                "  Text conflicts: 1\r\n" +
                "  Tree conflicts: 1\r\n";

            var conflicts = SvnAdapter.ParseSvnUpdateConflicts(output);

            CollectionAssert.AreEqual(new[] { "Assets\\a.txt", "Assets\\props.txt", "Assets\\tree.prefab.meta" }, conflicts);
        }

        [Test]
        public void ParseSvnUpdateConflicts_EmptyOutput_ReturnsEmpty()
        {
            Assert.IsEmpty(SvnAdapter.ParseSvnUpdateConflicts(null));
            Assert.IsEmpty(SvnAdapter.ParseSvnUpdateConflicts("At revision 7.\r\n"));
        }

        #endregion

        #region blame

        [Test]
        public void ParseSvnBlame_HandlesLocalModificationsEmptyLinesAndCrLf()
        {
            const string output =
                "     8   akari.pu a = 1;\r\n" +
                "     9        bob \r\n" +
                "     -          - 本地修改\r\n";

            var lines = SvnAdapter.ParseSvnBlame(output);

            Assert.AreEqual(3, lines.Count);
            Assert.AreEqual("8", lines[0].Revision);
            Assert.AreEqual("akari.pu", lines[0].Author);
            Assert.AreEqual("a = 1;", lines[0].Content);
            Assert.AreEqual(2, lines[1].LineNumber);
            Assert.AreEqual(string.Empty, lines[1].Content);
            Assert.AreEqual("working copy", lines[2].Revision);
            Assert.AreEqual("本地修改", lines[2].Content);
            Assert.IsFalse(lines.Any(l => l.Content.EndsWith("\r")));
        }

        #endregion

        #region XML

        [Test]
        public void ParsePropgetTargets_ReturnsOnlyTargetsWithProperty()
        {
            const string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<properties>\n" +
                "<target path=\"C:/wc/Assets/Main.unity\"><property name=\"svn:needs-lock\">*</property></target>\n" +
                "<target path=\"C:/wc/Assets/a.cs\"></target>\n" +
                "</properties>\n";

            CollectionAssert.AreEqual(new[] { "C:/wc/Assets/Main.unity" }, SvnAdapter.ParsePropgetTargets(xml));
            Assert.IsEmpty(SvnAdapter.ParsePropgetTargets("<properties>"));
        }

        [Test]
        public void ParseSvnListNames_ReadsEntryNames()
        {
            const string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<lists><list path=\"file:///r/tags\">" +
                "<entry kind=\"dir\"><name>v1.0</name><commit revision=\"3\"/></entry>" +
                "<entry kind=\"dir\"><name>发布-2</name><commit revision=\"4\"/></entry>" +
                "</list></lists>";

            CollectionAssert.AreEqual(new[] { "v1.0", "发布-2" }, SvnAdapter.ParseSvnListNames(xml));
        }

        [Test]
        public void ParseSvnLogXml_NormalizesLineEndings()
        {
            const string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<log><logentry revision=\"7\"><author>a</author>" +
                "<date>2026-01-01T00:00:00Z</date><msg>第一行\r\n第二行</msg></logentry></log>";

            var commit = SvnAdapter.ParseSvnLogXml(xml).Single();
            Assert.AreEqual("7", commit.Revision);
            Assert.AreEqual("第一行\n第二行", commit.Message);
        }

        #endregion

        #region 项目范围

        [Test]
        public void ComputeProjectScopeArg_ProjectBelowWorkingCopyRoot()
        {
            var root = Path.Combine(Path.GetTempPath(), "wc");
            Assert.AreEqual(Path.Combine("Client", "Unity"), SvnAdapter.ComputeProjectScopeArg(root, Path.Combine(root, "Client", "Unity")));
            Assert.IsNull(SvnAdapter.ComputeProjectScopeArg(root, root));
            Assert.IsNull(SvnAdapter.ComputeProjectScopeArg(root, Path.Combine(Path.GetTempPath(), "other")));
            Assert.IsNull(SvnAdapter.ComputeProjectScopeArg(root, root + "2"));
        }

        #endregion

        #region svn:ignore

        [Test]
        public void BuildSvnIgnorePlan_File_IgnoresNameAndMetaInParent()
        {
            var plan = VersionControlTool.BuildSvnIgnorePlan(@"C:\wc\Assets\Temp\debug.log", @"C:\wc", null, "file", _ => false);

            Assert.AreEqual(@"C:\wc\Assets\Temp", plan.Target);
            CollectionAssert.AreEqual(new[] { "debug.log", "debug.log.meta" }, plan.Patterns);
        }

        [Test]
        public void BuildSvnIgnorePlan_FolderPath_IgnoresFolderInItsParentWithoutTrailingSlash()
        {
            var plan = VersionControlTool.BuildSvnIgnorePlan(@"C:\wc\Assets\Generated\", @"C:\wc", null, "folder", p => p == @"C:\wc\Assets\Generated");

            Assert.AreEqual(@"C:\wc\Assets", plan.Target);
            CollectionAssert.AreEqual(new[] { "Generated", "Generated.meta" }, plan.Patterns);
        }

        [Test]
        public void BuildSvnIgnorePlan_FolderModeWithFilePath_UsesContainingFolder()
        {
            var plan = VersionControlTool.BuildSvnIgnorePlan(@"C:\wc\Assets\Generated\a.cs", @"C:\wc", null, "folder", _ => false);

            Assert.AreEqual(@"C:\wc\Assets", plan.Target);
            CollectionAssert.AreEqual(new[] { "Generated", "Generated.meta" }, plan.Patterns);
        }

        [Test]
        public void BuildSvnIgnorePlan_Extension_AndExplicitPattern()
        {
            var ext = VersionControlTool.BuildSvnIgnorePlan(@"C:\wc\Assets\a.tmp", @"C:\wc", null, "extension", _ => false);
            CollectionAssert.AreEqual(new[] { "*.tmp", "*.tmp.meta" }, ext.Patterns);

            var root = VersionControlTool.BuildSvnIgnorePlan(null, @"C:\wc", "Logs/", "folder", _ => false);
            Assert.AreEqual(@"C:\wc", root.Target);
            CollectionAssert.AreEqual(new[] { "Logs", "Logs.meta" }, root.Patterns);
        }

        [Test]
        public void ParseSvnIgnoreProperty_SplitsAndDeduplicates()
        {
            const string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<properties><target path=\"C:/wc\">" +
                "<property name=\"svn:ignore\">Library\r\nTemp\n\nLibrary\n忽略\n</property></target></properties>";

            CollectionAssert.AreEqual(new[] { "Library", "Temp", "忽略" }, VersionControlTool.ParseSvnIgnoreProperty(xml));
            Assert.IsEmpty(VersionControlTool.ParseSvnIgnoreProperty("<properties>\n</properties>"));
        }

        #endregion
    }
}
#endif
