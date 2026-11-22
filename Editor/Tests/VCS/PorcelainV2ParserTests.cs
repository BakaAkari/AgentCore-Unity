using System.Linq;
using NUnit.Framework;
using AgentCore.Editor.Components.VCS.Tools;

namespace AgentCore.Tests.Editor.VCS
{
    /// <summary>
    /// <see cref="PorcelainV2Parser"/> 单元测试。样本取自真实 <c>git status --porcelain=v2 -z --branch</c> 输出。
    /// </summary>
    [TestFixture]
    public class PorcelainV2ParserTests
    {
        private const string H = "78981922613b2afb6025042ff6bd878ac1994e85";

        private static string Ordinary(string xy, string path) =>
            $"1 {xy} N... 100644 100644 100644 {H} {H} {path}\0";

        #region 分支头

        [Test]
        public void Parse_BranchHeaders_AreRead()
        {
            var output = "# branch.oid 441257687215eda639e9823666bb4a83708c0859\0"
                         + "# branch.head main\0"
                         + "# branch.upstream origin/main\0"
                         + "# branch.ab +2 -5\0";

            var status = PorcelainV2Parser.Parse(output);

            Assert.AreEqual("441257687215eda639e9823666bb4a83708c0859", status.BranchOid);
            Assert.AreEqual("main", status.BranchHead);
            Assert.AreEqual("origin/main", status.Upstream);
            Assert.IsTrue(status.HasAheadBehind);
            Assert.AreEqual(2, status.Ahead);
            Assert.AreEqual(5, status.Behind);
            Assert.IsEmpty(status.Files);
            Assert.IsFalse(status.HasIncompleteRecord);
        }

        [Test]
        public void Parse_InitialAndDetached_NoUpstream()
        {
            var status = PorcelainV2Parser.Parse("# branch.oid (initial)\0# branch.head (detached)\0");

            Assert.AreEqual("(initial)", status.BranchOid);
            Assert.AreEqual("(detached)", status.BranchHead);
            Assert.IsNull(status.Upstream);
            Assert.IsFalse(status.HasAheadBehind);
        }

        [Test]
        public void Parse_UnknownHeadersAndRecordTypes_AreIgnored()
        {
            var output = "# stash 3\0# branch.future value\0x something new\0" + Ordinary(".M", "a.txt");

            var status = PorcelainV2Parser.Parse(output);

            Assert.AreEqual(1, status.Files.Count);
            Assert.AreEqual("a.txt", status.Files[0].FilePath);
        }

        #endregion

        #region 普通记录

        [TestCase(".M", VcsFileState.Modified)]
        [TestCase("M.", VcsFileState.Modified)]
        [TestCase("MM", VcsFileState.Modified)]
        [TestCase(".T", VcsFileState.Modified)]
        [TestCase("A.", VcsFileState.Added)]
        [TestCase("AM", VcsFileState.Added)]
        [TestCase("D.", VcsFileState.Deleted)]
        [TestCase(".D", VcsFileState.Deleted)]
        [TestCase("MD", VcsFileState.Deleted)]
        public void Parse_OrdinaryRecord_MapsXY(string xy, VcsFileState expected)
        {
            var status = PorcelainV2Parser.Parse(Ordinary(xy, "Assets/a.cs"));

            Assert.AreEqual(1, status.Files.Count);
            Assert.AreEqual(expected, status.Files[0].State);
            Assert.AreEqual(xy, status.Files[0].StatusCode);
            Assert.AreEqual("Assets/a.cs", status.Files[0].FilePath);
        }

        [Test]
        public void Parse_PathWithSpacesAndNonAscii_IsPreserved()
        {
            var output = Ordinary(".M", "Assets/My Folder/a b.txt") + Ordinary("A.", "Assets/中文 资源/贴图.png");

            var status = PorcelainV2Parser.Parse(output);

            Assert.AreEqual(2, status.Files.Count);
            Assert.AreEqual("Assets/My Folder/a b.txt", status.Files[0].FilePath);
            Assert.AreEqual("Assets/中文 资源/贴图.png", status.Files[1].FilePath);
        }

        [Test]
        public void Parse_PathWithNewline_IsPreserved()
        {
            var status = PorcelainV2Parser.Parse(Ordinary(".M", "weird\nname.txt"));

            Assert.AreEqual("weird\nname.txt", status.Files[0].FilePath);
        }

        #endregion

        #region 重命名 / 复制

        [Test]
        public void Parse_RenameRecord_ConsumesOrigPath()
        {
            var output = $"2 R. N... 100644 100644 100644 {H} {H} R100 new 名.txt\0old.txt\0"
                         + Ordinary(".M", "after.txt");

            var status = PorcelainV2Parser.Parse(output);

            Assert.AreEqual(2, status.Files.Count);
            var renamed = status.Files[0];
            Assert.AreEqual(VcsFileState.Renamed, renamed.State);
            Assert.AreEqual("new 名.txt", renamed.FilePath);
            Assert.AreEqual("old.txt", renamed.OriginalPath);
            StringAssert.Contains("old.txt", renamed.StateDescription);
            Assert.AreEqual("after.txt", status.Files[1].FilePath);
        }

        [Test]
        public void Parse_CopyRecord_MapsToCopied()
        {
            var output = $"2 C. N... 100644 100644 100644 {H} {H} C75 copy.txt\0src.txt\0";

            var status = PorcelainV2Parser.Parse(output);

            Assert.AreEqual(VcsFileState.Copied, status.Files[0].State);
            Assert.AreEqual("src.txt", status.Files[0].OriginalPath);
        }

        [Test]
        public void Parse_RenameMissingOrigPath_FlagsIncomplete()
        {
            var output = $"2 R. N... 100644 100644 100644 {H} {H} R100 new.txt\0";

            var status = PorcelainV2Parser.Parse(output);

            Assert.IsTrue(status.HasIncompleteRecord);
            Assert.IsEmpty(status.Files);
        }

        #endregion

        #region 冲突

        [TestCase("UU")]
        [TestCase("AA")]
        [TestCase("DD")]
        [TestCase("AU")]
        [TestCase("UD")]
        public void Parse_UnmergedRecord_IsConflicted(string xy)
        {
            var output = $"u {xy} N... 100644 100644 100644 100644 {H} {H} {H} Assets/conflict file.txt\0";

            var status = PorcelainV2Parser.Parse(output);

            Assert.AreEqual(1, status.Files.Count);
            Assert.AreEqual(VcsFileState.Conflicted, status.Files[0].State);
            Assert.AreEqual("Assets/conflict file.txt", status.Files[0].FilePath);
        }

        [TestCase("UU", true)]
        [TestCase("AA", true)]
        [TestCase("DD", true)]
        [TestCase(".U", true)]
        [TestCase("MM", false)]
        [TestCase("AD", false)]
        public void IsConflictXY_Rules(string xy, bool expected)
        {
            Assert.AreEqual(expected, PorcelainV2Parser.IsConflictXY(xy));
        }

        [Test]
        public void Parse_OrdinaryRecordWithConflictXY_IsConflicted()
        {
            var status = PorcelainV2Parser.Parse(Ordinary("AA", "x.txt"));

            Assert.AreEqual(VcsFileState.Conflicted, status.Files[0].State);
        }

        #endregion

        #region 未跟踪 / 忽略

        [Test]
        public void Parse_UntrackedAndIgnored()
        {
            var status = PorcelainV2Parser.Parse("? 未跟踪 file.txt\0! Library/cache.bin\0");

            Assert.AreEqual(2, status.Files.Count);
            Assert.AreEqual(VcsFileState.Untracked, status.Files[0].State);
            Assert.AreEqual("未跟踪 file.txt", status.Files[0].FilePath);
            Assert.AreEqual(VcsFileState.Ignored, status.Files[1].State);
            Assert.AreEqual("Library/cache.bin", status.Files[1].FilePath);
        }

        #endregion

        #region 边界

        [Test]
        public void Parse_EmptyOrNull_ReturnsEmpty()
        {
            Assert.IsEmpty(PorcelainV2Parser.Parse(null).Files);
            Assert.IsEmpty(PorcelainV2Parser.Parse(string.Empty).Files);
        }

        [Test]
        public void Parse_TrailingNewlineAfterLastRecord_IsNotIncomplete()
        {
            var status = PorcelainV2Parser.Parse(Ordinary(".M", "a.txt") + "\r\n");

            Assert.AreEqual(1, status.Files.Count);
            Assert.IsFalse(status.HasIncompleteRecord);
        }

        [Test]
        public void Parse_TruncatedLastRecord_IsFlaggedAndSkipped()
        {
            var output = Ordinary(".M", "a.txt") + "1 .M N... 100644 100644 100644 " + H.Substring(0, 10);

            var status = PorcelainV2Parser.Parse(output);

            Assert.AreEqual(1, status.Files.Count);
            Assert.IsTrue(status.HasIncompleteRecord);
        }

        [Test]
        public void Parse_FullSample_FromRealRepository()
        {
            // 真实仓库输出：普通修改 + 重命名 + 冲突 + 未跟踪
            var output = "# branch.oid 441257687215eda639e9823666bb4a83708c0859\0# branch.head main\0"
                         + Ordinary(".M", "a b.txt")
                         + $"2 R. N... 100644 100644 100644 {H} {H} R100 new 名.txt\0old.txt\0"
                         + $"u UU N... 100644 100644 100644 100644 {H} {H} {H} conflict.txt\0"
                         + "? 未跟踪 file.txt\0";

            var status = PorcelainV2Parser.Parse(output);

            Assert.AreEqual("main", status.BranchHead);
            CollectionAssert.AreEqual(
                new[] { "a b.txt", "new 名.txt", "conflict.txt", "未跟踪 file.txt" },
                status.Files.Select(f => f.FilePath).ToArray());
            CollectionAssert.AreEqual(
                new[] { VcsFileState.Modified, VcsFileState.Renamed, VcsFileState.Conflicted, VcsFileState.Untracked },
                status.Files.Select(f => f.State).ToArray());
        }

        #endregion
    }
}
