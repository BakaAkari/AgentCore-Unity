using NUnit.Framework;
using AgentCore.Editor.Components.VCS.Tools;

namespace AgentCore.Tests.Editor.VCS
{
    /// <summary>
    /// <see cref="GitOutputParser"/> 与 git 全局选项（core.quotepath=off）的单元测试。
    /// 样本取自真实 <c>git diff-tree --no-commit-id --name-only -r -z</c> 输出。
    /// </summary>
    [TestFixture]
    public class GitOutputParserTests
    {
        #region ParseNulSeparatedPaths

        [Test]
        public void ParseNulSeparatedPaths_NullOrEmpty_ReturnsEmpty()
        {
            Assert.IsEmpty(GitOutputParser.ParseNulSeparatedPaths(null));
            Assert.IsEmpty(GitOutputParser.ParseNulSeparatedPaths(string.Empty));
        }

        [Test]
        public void ParseNulSeparatedPaths_ChineseAndSpaces_KeptVerbatim()
        {
            var output = "资源 目录/文件 1.txt\0Assets/普通.cs\0Assets/a b/c d.prefab\0";

            var paths = GitOutputParser.ParseNulSeparatedPaths(output);

            CollectionAssert.AreEqual(
                new[] { "资源 目录/文件 1.txt", "Assets/普通.cs", "Assets/a b/c d.prefab" },
                paths);
        }

        [Test]
        public void ParseNulSeparatedPaths_LeadingTrailingSpacesAndNewlines_NotTrimmedOrSplit()
        {
            // -z 模式下路径首尾空格、内嵌换行 / 引号都是文件名的一部分
            var output = " lead.txt\0trail.txt \0line\nbreak.txt\0quo\"te.txt\0";

            var paths = GitOutputParser.ParseNulSeparatedPaths(output);

            CollectionAssert.AreEqual(
                new[] { " lead.txt", "trail.txt ", "line\nbreak.txt", "quo\"te.txt" },
                paths);
        }

        [Test]
        public void ParseNulSeparatedPaths_NoTrailingNul_LastRecordKept()
        {
            var paths = GitOutputParser.ParseNulSeparatedPaths("a.txt\0目录/b.txt");

            CollectionAssert.AreEqual(new[] { "a.txt", "目录/b.txt" }, paths);
        }

        #endregion

        #region Git 全局选项

        [Test]
        public void ApplyGlobalOptions_Git_PrependsQuotePathOff()
        {
            Assert.AreEqual("-c core.quotepath=off diff HEAD", VcsArgs.ApplyGlobalOptions("git", "diff HEAD"));
            Assert.AreEqual("-c core.quotepath=off", VcsArgs.ApplyGlobalOptions("git", string.Empty));
        }

        [Test]
        public void ApplyGlobalOptions_GitFullPathOrExe_IsRecognized()
        {
            Assert.IsTrue(VcsArgs.IsGitCommand("git.exe"));
            Assert.IsTrue(VcsArgs.IsGitCommand("GIT"));
            Assert.IsTrue(VcsArgs.IsGitCommand(@"C:\Program Files\Git\cmd\git.exe"));
            Assert.IsTrue(VcsArgs.IsGitCommand("/usr/bin/git"));
        }

        [Test]
        public void ApplyGlobalOptions_OtherCommands_Unchanged()
        {
            Assert.AreEqual("info --xml", VcsArgs.ApplyGlobalOptions("svn", "info --xml"));
            Assert.AreEqual("status", VcsArgs.ApplyGlobalOptions("p4", "status"));
            Assert.IsFalse(VcsArgs.IsGitCommand("gitk"));
            Assert.IsFalse(VcsArgs.IsGitCommand(null));
        }

        #endregion
    }
}
