using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using AgentCore.Editor.Components.VCS.Tools;

namespace AgentCore.Tests.Editor.VCS
{
    /// <summary>
    /// <see cref="VcsExternalToolLauncher"/> 的 TortoiseProc 参数构造测试。
    /// </summary>
    [TestFixture]
    public class VcsExternalToolArgsTests
    {
        [Test]
        public void SinglePath_IsQuoted()
        {
            Assert.IsTrue(VcsExternalToolLauncher.TryBuildTortoiseArgs("log", @"C:\My Project\中文\a b.cs", out var args, out _));
            Assert.AreEqual("/command:log /path:\"C:\\My Project\\中文\\a b.cs\"", args);
        }

        [Test]
        public void PathWithDoubleQuote_IsRejectedWithReason()
        {
            Assert.IsFalse(VcsExternalToolLauncher.TryBuildTortoiseArgs("log", "bad\"path", out var args, out var error));
            Assert.IsNull(args);
            StringAssert.Contains("double quote", error);
        }

        [Test]
        public void MultiplePaths_UsePathFile()
        {
            var paths = new[] { @"C:\p\a.cs", @"C:\p\中文 b.cs" };

            Assert.IsTrue(VcsExternalToolLauncher.TryBuildTortoiseArgs("commit", paths, out var args, out _));

            var match = Regex.Match(args, "^/command:commit /pathfile:\"([^\"]+)\" /deletepathfile$");
            Assert.IsTrue(match.Success, args);
            var file = match.Groups[1].Value;
            try
            {
                var bytes = File.ReadAllBytes(file);
                // UTF-16LE 无 BOM，换行分隔
                Assert.AreNotEqual(0xFF, bytes[0]);
                Assert.AreEqual(string.Join("\n", paths), Encoding.Unicode.GetString(bytes));
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Test]
        public void LogMessage_WithQuotesAndNewlines_GoesToFile()
        {
            const string message = "fix: \"quoted\" message\nsecond line";

            Assert.IsTrue(VcsExternalToolLauncher.TryBuildTortoiseLogMessageArg(message, out var arg, out _));

            var match = Regex.Match(arg, "^/logmsgfile:\"([^\"]+)\"$");
            Assert.IsTrue(match.Success, arg);
            try
            {
                Assert.AreEqual(message, File.ReadAllText(match.Groups[1].Value, Encoding.UTF8));
            }
            finally
            {
                File.Delete(match.Groups[1].Value);
            }
        }

        [Test]
        public void EmptyLogMessage_ProducesNoArgument()
        {
            Assert.IsTrue(VcsExternalToolLauncher.TryBuildTortoiseLogMessageArg(string.Empty, out var arg, out _));
            Assert.AreEqual(string.Empty, arg);
        }
    }
}
