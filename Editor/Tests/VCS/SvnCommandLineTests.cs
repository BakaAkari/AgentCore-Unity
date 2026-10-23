#if AGENTCORE_VCS
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using AgentCore.Editor.Components.VCS.Tools;

namespace AgentCore.Tests.Editor.VCS
{
    /// <summary>
    /// <see cref="SvnCommandLine"/> / <see cref="UnityMetaPaths"/> 单元测试：参数构造、输出解码、错误提示、svn.exe 定位。
    /// 编码相关的预期来自 Windows 中文系统（ACP/OEM = 936）上对 svn 1.14 的实测。
    /// </summary>
    [TestFixture]
    public class SvnCommandLineTests
    {
        #region 参数

        [TestCase("Assets/a.png", "Assets/a.png")]
        [TestCase("-rf.txt", "./-rf.txt")]
        [TestCase("--force", "./--force")]
        [TestCase("Assets/icon@2x.png", "Assets/icon@2x.png@")]
        [TestCase("-a@b", "./-a@b@")]
        [TestCase(@"C:\wc\a@5", @"C:\wc\a@5@")]
        [TestCase("Assets/角色/英雄.prefab", "Assets/角色/英雄.prefab")]
        public void PathArg_EscapesOptionsAndPegRevisions(string input, string expected)
        {
            Assert.AreEqual(expected, SvnCommandLine.PathArg(input));
        }

        [TestCase("Assets/icon@2x.png", "Assets/icon@2x.png")]
        [TestCase("-x.txt", "./-x.txt")]
        public void LocalDiffPathArg_NeverAppendsPeg(string input, string expected)
        {
            // 本地 svn diff 不剥离 peg：追加 '@' 会报 E155010（实测 svn 1.14）
            Assert.AreEqual(expected, SvnCommandLine.LocalDiffPathArg(input));
        }

        [TestCase("svn", true)]
        [TestCase("SVN.exe", true)]
        [TestCase(@"C:\Program Files\TortoiseSVN\bin\svn.exe", true)]
        [TestCase("svnadmin", false)]
        [TestCase("git", false)]
        [TestCase(null, false)]
        public void IsSvnCommand_RecognizesSvnExecutable(string command, bool expected)
        {
            Assert.AreEqual(expected, SvnCommandLine.IsSvnCommand(command));
        }

        [Test]
        public void MessageFileArgs_UseUtf8Encoding()
        {
            CollectionAssert.AreEqual(new[] { "-F", "m.txt", "--encoding", "UTF-8" }, SvnCommandLine.MessageFileArgs("m.txt"));
        }

        [Test]
        public void WriteMessageFile_IsUtf8WithoutBom()
        {
            var file = SvnCommandLine.WriteMessageFile("提交 🚀 한국어", Path.GetTempPath());
            try
            {
                var bytes = File.ReadAllBytes(file);
                Assert.AreNotEqual(0xEF, bytes[0], "BOM must not be written");
                Assert.AreEqual("提交 🚀 한국어", Encoding.UTF8.GetString(bytes));
            }
            finally
            {
                SvnCommandLine.TryDeleteFile(file);
            }
        }

        [Test]
        public void WriteTargetsFile_UsesArgumentEncodingAndLf()
        {
            var file = SvnCommandLine.WriteTargetsFile(new[] { "a.txt", "./-b@" }, Path.GetTempPath());
            try
            {
                var text = SvnCommandLine.ArgumentEncoding.GetString(File.ReadAllBytes(file));
                StringAssert.DoesNotContain("\r", text);
                StringAssert.StartsWith("a.txt\n./-b@", text);
            }
            finally
            {
                SvnCommandLine.TryDeleteFile(file);
            }
        }

        [Test]
        public void CanEncode_RejectsCharactersOutsideCodePage()
        {
            Encoding gbk;
            try { gbk = Encoding.GetEncoding(936); }
            catch (Exception) { Assert.Ignore("Code page 936 is not available in this runtime."); return; }

            Assert.IsTrue(SvnCommandLine.CanEncode("Assets/角色.prefab", gbk));
            Assert.IsFalse(SvnCommandLine.CanEncode("Assets/한국어.prefab", gbk));
            Assert.IsTrue(SvnCommandLine.CanEncode("anything 한국어", new UTF8Encoding(false)));
        }

        #endregion

        #region 输出解码

        [Test]
        public void DecodeOutput_MixedUtf8AndCodePageLines_DecodesEachLine()
        {
            Encoding gbk;
            try { gbk = Encoding.GetEncoding(936); }
            catch (Exception) { Assert.Ignore("Code page 936 is not available in this runtime."); return; }

            // svn diff：头部路径按控制台代码页，文件内容是原始 UTF-8 字节
            var bytes = new List<byte>();
            bytes.AddRange(gbk.GetBytes("Index: 角色/说明.txt\r\n"));
            bytes.AddRange(Encoding.UTF8.GetBytes("+中文内容 ✓\n"));
            var text = SvnCommandLine.DecodeOutput(bytes.ToArray(), bytes.Count, gbk);

            Assert.AreEqual("Index: 角色/说明.txt\r\n+中文内容 ✓\n", text);
        }

        [Test]
        public void DecodeOutput_StripsBomAndHandlesEmpty()
        {
            var bytes = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a' };
            Assert.AreEqual("a", SvnCommandLine.DecodeOutput(bytes, bytes.Length, Encoding.ASCII));
            Assert.AreEqual(string.Empty, SvnCommandLine.DecodeOutput(new byte[0], 0));
            Assert.AreEqual(string.Empty, SvnCommandLine.DecodeOutput(null, 0));
        }

        [Test]
        public void IsValidUtf8_RejectsInvalidSequences()
        {
            var valid = Encoding.UTF8.GetBytes("abc 中文 🚀");
            Assert.IsTrue(SvnCommandLine.IsValidUtf8(valid, 0, valid.Length));
            // 截断的多字节序列
            Assert.IsFalse(SvnCommandLine.IsValidUtf8(valid, 0, 6));
            // 过长编码 / 孤立续字节 / 代理区
            Assert.IsFalse(SvnCommandLine.IsValidUtf8(new byte[] { 0xC0, 0xAF }, 0, 2));
            Assert.IsFalse(SvnCommandLine.IsValidUtf8(new byte[] { 0x80 }, 0, 1));
            Assert.IsFalse(SvnCommandLine.IsValidUtf8(new byte[] { 0xED, 0xA0, 0x80 }, 0, 3));
            // GBK "角色"
            Assert.IsFalse(SvnCommandLine.IsValidUtf8(new byte[] { 0xBD, 0xC7, 0xC9, 0xAB }, 0, 4));
        }

        #endregion

        #region 错误提示

        [TestCase("svn: E155004: Run 'svn cleanup' to remove locks", "cleanup")]
        [TestCase("svn: E155007: '/x' is not a working copy", "not inside an SVN working copy")]
        [TestCase("svn: E215004: No more credentials or we tried too many times.\nAuthentication failed", "authentication")]
        [TestCase("svn: E230001: Server SSL certificate verification failed", "does not bypass")]
        [TestCase("svn: E155011: File 'a' is out of date", "update")]
        [TestCase("svn: E155038: Try 'svn revert --depth infinity' instead?", "recursive=true")]
        [TestCase("svn: warning: W160035: Path '/a' is already locked by user 'bob'", "locked by another user")]
        [TestCase("svn: E200009: '/x/y' is not known to exist in the repository and is not part of the commit", "parent directory")]
        public void AppendErrorHint_AddsActionableHint(string error, string expectedFragment)
        {
            var result = SvnCommandLine.AppendErrorHint(error);
            StringAssert.StartsWith(error.TrimEnd(), result);
            StringAssert.Contains("Hint:", result);
            StringAssert.Contains(expectedFragment, result);
        }

        [Test]
        public void AppendErrorHint_SharesTableWithSvnErrorHints_AndIsIdempotent()
        {
            var error = "svn: E155004: Run 'svn cleanup' to remove locks";
            var appended = SvnCommandLine.AppendErrorHint(error);

            // 同一张表：命令层追加的提示与 SvnErrorHints 一致；面板 / 远端检查再 Describe 不会重复追加
            StringAssert.EndsWith(AgentCore.Editor.Workspace.Resolution.SvnErrorHints.GetHint(error), appended);
            Assert.AreEqual(appended, SvnCommandLine.AppendErrorHint(appended));
            Assert.AreEqual(appended, AgentCore.Editor.Workspace.Resolution.SvnErrorHints.Describe(appended, "D:/w"));
        }

        [Test]
        public void AppendErrorHint_UnknownError_Unchanged()
        {
            Assert.AreEqual("svn: E999999: something", SvnCommandLine.AppendErrorHint("svn: E999999: something"));
            Assert.IsNull(SvnCommandLine.AppendErrorHint(null));
        }

        #endregion

        #region svn.exe 定位

        [Test]
        public void FindSvnExecutable_OnPath_ReturnsBareCommand()
        {
            var result = SvnCommandLine.FindSvnExecutable(@"C:\Tools;C:\svn\bin", @"C:\TSVN", new[] { @"C:\Program Files" },
                p => p == Path.Combine(@"C:\svn\bin", "svn.exe"));
            Assert.AreEqual("svn", result);
        }

        [Test]
        public void FindSvnExecutable_NotOnPath_FallsBackToTortoiseSvn()
        {
            var registry = Path.Combine(@"D:\TortoiseSVN", "bin", "svn.exe");
            var programFiles = Path.Combine(@"C:\Program Files", "TortoiseSVN", "bin", "svn.exe");

            Assert.AreEqual(registry, SvnCommandLine.FindSvnExecutable(@"C:\Windows", @"D:\TortoiseSVN", new[] { @"C:\Program Files" },
                p => p == registry || p == programFiles));
            Assert.AreEqual(programFiles, SvnCommandLine.FindSvnExecutable(@"C:\Windows", null, new[] { null, @"C:\Program Files" },
                p => p == programFiles));
            Assert.IsNull(SvnCommandLine.FindSvnExecutable(null, null, null, p => false));
        }

        [Test]
        public void ResolveExecutable_NonSvnCommand_Unchanged()
        {
            Assert.AreEqual("git", SvnCommandLine.ResolveExecutable("git"));
            Assert.AreEqual(@"C:\x\svn.exe", SvnCommandLine.ResolveExecutable(@"C:\x\svn.exe"));
        }

        #endregion

        #region Unity .meta

        [TestCase("Assets/a.png", "Assets/a.png.meta")]
        [TestCase("Assets/a.png.meta", "Assets/a.png")]
        [TestCase("Assets/Folder/", "Assets/Folder.meta")]
        [TestCase(@"Assets\Folder.META", @"Assets\Folder")]
        [TestCase("", null)]
        public void GetCompanion_PairsAssetAndMeta(string path, string expected)
        {
            Assert.AreEqual(expected, UnityMetaPaths.GetCompanion(path));
        }

        #endregion
    }
}
#endif
