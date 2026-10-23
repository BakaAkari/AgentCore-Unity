using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using AgentCore.Editor.Bootstrap;
using AgentCore.Editor.Workspace;
using AgentCore.Editor.Workspace.Resolution;

namespace AgentCore.Tests.Editor.Core
{
    /// <summary>
    /// SVN 工作副本检测：svn info --xml 解析（中文 / 空格路径、external）、最近 .svn 根、
    /// svn info 失败时保持 Fingerprint 稳定、错误提示分类、上下文摘要中的 SVN 说明。
    /// </summary>
    [TestFixture]
    public class SvnWorkspaceDetectionTests
    {
        private string _tempRoot;

        [SetUp]
        public void SetUp()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "agentcore-svn-detect-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_tempRoot, true); } catch { /* 忽略 */ }
        }

        // ── svn info --xml ──

        private const string InfoXmlChinese =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<info>\n<entry\n   kind=\"dir\"\n   path=\"D:\\work\\项目 A\\client\"\n   revision=\"1234\">\n" +
            "<url>https://svn.example.com/repo/branches/rel%201.0/client</url>\n<relative-url>^/branches/rel%201.0/client</relative-url>\n" +
            "<repository>\n<root>https://svn.example.com/repo</root>\n<uuid>0000</uuid>\n</repository>\n" +
            "<wc-info>\n<wcroot-abspath>D:/work/项目 A</wcroot-abspath>\n<schedule>normal</schedule>\n<depth>infinity</depth>\n</wc-info>\n" +
            "<commit\n   revision=\"1200\">\n<author>a</author>\n</commit>\n</entry>\n</info>\n";

        [Test]
        public void ParseSvnInfoXml_ChinesePathAndSpaces_ParsesAllFields()
        {
            var info = new WorkspaceVcsInfo { Type = WorkspaceVcsType.Svn };

            Assert.IsTrue(SvnWorkspaceInfoResolver.ParseSvnInfoXml(InfoXmlChinese, info));

            Assert.AreEqual("D:/work/项目 A", info.RootPath);
            Assert.AreEqual("https://svn.example.com/repo/branches/rel%201.0/client", info.Url);
            Assert.AreEqual("https://svn.example.com/repo", info.RepositoryRoot);
            Assert.AreEqual("1234", info.Revision);
            StringAssert.StartsWith("branches/", info.BranchId);
        }

        [Test]
        public void ParseSvnInfoXml_ExternalHasOwnWcRoot()
        {
            var xml = InfoXmlChinese.Replace("<wcroot-abspath>D:/work/项目 A</wcroot-abspath>",
                "<wcroot-abspath>D:/work/项目 A/client/Assets/Shared</wcroot-abspath>");
            var info = new WorkspaceVcsInfo { Type = WorkspaceVcsType.Svn };

            Assert.IsTrue(SvnWorkspaceInfoResolver.ParseSvnInfoXml(xml, info));
            Assert.AreEqual("D:/work/项目 A/client/Assets/Shared", info.RootPath);
        }

        [Test]
        public void ParseSvnInfoXml_GarbageOrEmpty_ReturnsFalse()
        {
            Assert.IsFalse(SvnWorkspaceInfoResolver.ParseSvnInfoXml("svn: E155007: not a working copy", new WorkspaceVcsInfo()));
            Assert.IsFalse(SvnWorkspaceInfoResolver.ParseSvnInfoXml(string.Empty, new WorkspaceVcsInfo()));
            Assert.IsFalse(SvnWorkspaceInfoResolver.ParseSvnInfoXml("<info></info>", new WorkspaceVcsInfo()));
        }

        // ── .svn 根查找 ──

        [Test]
        public void FindWorkingCopyRoot_NestedUnityProject_ReturnsAncestorWcRoot()
        {
            Directory.CreateDirectory(Path.Combine(_tempRoot, ".svn"));
            var unityRoot = Path.Combine(_tempRoot, "Game", "项目 A");
            Directory.CreateDirectory(Path.Combine(unityRoot, "Assets"));

            var root = SvnWorkspaceInfoResolver.FindWorkingCopyRoot(unityRoot);

            Assert.AreEqual(UnityRootResolver.NormalizePath(_tempRoot), root);
        }

        [Test]
        public void FindWorkingCopyRoot_InsideExternal_ReturnsNearestRoot()
        {
            // 外层工作副本根 / external（自带 .svn）紧挨在根目录下：旧实现取"最上层连续 .svn"，external 被误判为外层根
            Directory.CreateDirectory(Path.Combine(_tempRoot, ".svn"));
            var external = Path.Combine(_tempRoot, "Shared");
            Directory.CreateDirectory(Path.Combine(external, ".svn"));
            var inside = Path.Combine(external, "Assets");
            Directory.CreateDirectory(inside);

            Assert.AreEqual(UnityRootResolver.NormalizePath(external), SvnWorkspaceInfoResolver.FindWorkingCopyRoot(inside));
            Assert.AreEqual(UnityRootResolver.NormalizePath(_tempRoot), SvnWorkspaceInfoResolver.FindWorkingCopyRoot(_tempRoot));
        }

        [Test]
        public void FindWorkingCopyRoot_NoSvn_ReturnsNull()
        {
            Assert.IsNull(SvnWorkspaceInfoResolver.FindWorkingCopyRoot(_tempRoot));
        }

        // ── svn info 失败时 Fingerprint 稳定 ──

        [Test]
        public void MergeWithKnownSvnInfo_ProbeFailedSameRoot_KeepsUrlAndCarriesError()
        {
            var known = new WorkspaceVcsInfo
            {
                Type = WorkspaceVcsType.Svn, RootPath = "D:/work/项目 A", Url = "https://svn/repo/trunk",
                RepositoryRoot = "https://svn/repo", BranchId = "trunk", Revision = "10", IsWorkingCopy = true, IsCommandAvailable = true
            };
            var probe = new WorkspaceVcsInfo
            {
                Type = WorkspaceVcsType.Svn, RootPath = "d:\\work\\项目 A\\", IsWorkingCopy = true, IsCommandAvailable = true,
                ErrorMessage = "svn: E155036: upgrade"
            };

            var merged = WorkspaceContextService.MergeWithKnownSvnInfo(probe, known);

            Assert.AreEqual(known.Url, merged.Url);
            Assert.AreEqual("trunk", merged.BranchId);
            Assert.AreEqual("10", merged.Revision);
            Assert.AreEqual(probe.ErrorMessage, merged.ErrorMessage);
            Assert.IsTrue(merged.IsWorkingCopy);
        }

        [Test]
        public void MergeWithKnownSvnInfo_DifferentRootOrProbeHasUrl_ReturnsProbe()
        {
            var known = new WorkspaceVcsInfo { Type = WorkspaceVcsType.Svn, RootPath = "D:/a", Url = "https://svn/repo/trunk" };
            var otherRoot = new WorkspaceVcsInfo { Type = WorkspaceVcsType.Svn, RootPath = "D:/b" };
            var withUrl = new WorkspaceVcsInfo { Type = WorkspaceVcsType.Svn, RootPath = "D:/a", Url = "https://svn/repo/branches/x" };

            Assert.AreSame(otherRoot, WorkspaceContextService.MergeWithKnownSvnInfo(otherRoot, known));
            Assert.AreSame(withUrl, WorkspaceContextService.MergeWithKnownSvnInfo(withUrl, known));
            Assert.AreSame(withUrl, WorkspaceContextService.MergeWithKnownSvnInfo(withUrl, null));
        }

        // ── 错误提示 ──

        [TestCase("svn: E155036: The working copy at 'D:\\w' is too old (format 29) to work with client version '1.14.3'. You need to upgrade the working copy first.", SvnErrorHints.Kind.NeedsUpgrade)]
        [TestCase("svn: E155004: Run 'svn cleanup' to remove locks (type 'svn help cleanup' for details)", SvnErrorHints.Kind.NeedsCleanup)]
        [TestCase("svn: E155037: Previous operation has not finished; run 'cleanup' if it was interrupted", SvnErrorHints.Kind.NeedsCleanup)]
        [TestCase("svn: E170013: Unable to connect to a repository at URL 'https://x'\nsvn: E731001: No such host is known.", SvnErrorHints.Kind.Offline)]
        [TestCase("svn: E170001: Authentication required for 'VisualSVN Server'", SvnErrorHints.Kind.AuthRequired)]
        [TestCase("svn: E215004: No more credentials or we tried too many times.", SvnErrorHints.Kind.AuthRequired)]
        [TestCase("svn: E155007: 'D:\\x' is not a working copy", SvnErrorHints.Kind.NotWorkingCopy)]
        [TestCase("The system cannot find the file specified", SvnErrorHints.Kind.CommandMissing)]
        [TestCase("something unrelated", SvnErrorHints.Kind.None)]
        [TestCase(null, SvnErrorHints.Kind.None)]
        public void SvnErrorHints_Classify(string text, SvnErrorHints.Kind expected)
        {
            Assert.AreEqual(expected, SvnErrorHints.Classify(text));
        }

        [Test]
        public void SvnErrorHints_Describe_KeepsOriginalAndAddsUpgradeCommand()
        {
            var text = "svn: E155036: The working copy at 'D:\\w' is too old";
            var described = SvnErrorHints.Describe(text, "D:/w");

            StringAssert.StartsWith(text, described);
            StringAssert.Contains("svn upgrade", described);
            StringAssert.Contains("D:/w", described);
            Assert.AreEqual("E155036", SvnErrorHints.FirstErrorCode(described));
            Assert.AreEqual("plain", SvnErrorHints.Describe("plain"));
        }

        [TestCase("svn: E230001: Server SSL certificate verification failed", SvnErrorHints.Kind.CertificateUntrusted)]
        [TestCase("svn: warning: W160035: Path '/a' is already locked by user 'bob' in filesystem", SvnErrorHints.Kind.LockedByOther)]
        [TestCase("svn: E155011: File 'a' is out of date", SvnErrorHints.Kind.OutOfDate)]
        [TestCase("svn: E155032: Pristine text not found", SvnErrorHints.Kind.NeedsCleanup)]
        [TestCase("svn: E170013: Unable to connect\nsvn: E215004: Authentication failed", SvnErrorHints.Kind.AuthRequired)]
        public void SvnErrorHints_MergedTable_CoversCommandLineCodes(string text, SvnErrorHints.Kind expected)
        {
            Assert.AreEqual(expected, SvnErrorHints.Classify(text));
        }

        [Test]
        public void SvnErrorHints_Describe_IsIdempotent()
        {
            var error = "svn: E155004: Run 'svn cleanup' to remove locks";
            var once = SvnErrorHints.Describe(error, "D:/w");
            Assert.AreEqual(once, SvnErrorHints.Describe(once, "D:/w"));
            StringAssert.Contains("\n" + SvnErrorHints.HintPrefix, once);
            Assert.IsTrue(SvnErrorHints.HasHint(once));
            Assert.IsFalse(SvnErrorHints.HasHint(error));
        }

        // ── 上下文摘要 ──

        [Test]
        public void AppendSvnNotes_NestedUnityProject_ExplainsPathPrefix()
        {
            var sb = new StringBuilder();
            var vcs = new WorkspaceVcsInfo { Type = WorkspaceVcsType.Svn, RootPath = "D:/work", IsCommandAvailable = true };

            ProjectContextCollector.AppendSvnNotes(sb, vcs, "D:/work/Game/项目 A");

            StringAssert.Contains("Game/项目 A/", sb.ToString());
            // 与 version_control 工具的路径约定一致：工具路径相对于 Unity 工程根
            StringAssert.Contains("相对于 Unity 工程根", sb.ToString());
        }

        [Test]
        public void AppendSvnNotes_ErrorMessage_IsReported()
        {
            var sb = new StringBuilder();
            var vcs = new WorkspaceVcsInfo
            {
                Type = WorkspaceVcsType.Svn, RootPath = "D:/work",
                ErrorMessage = SvnErrorHints.Describe("svn: E155036: too old", "D:/work")
            };

            ProjectContextCollector.AppendSvnNotes(sb, vcs, "D:/work");

            var text = sb.ToString();
            StringAssert.Contains("E155036", text);
            StringAssert.Contains("svn upgrade", text);
            // 错误信息与提示压成一行；工程即工作副本根时不输出路径前缀说明
            Assert.AreEqual(1, text.TrimEnd().Split('\n').Length, text);
        }

        // ── 真实 svn（未安装时忽略） ──

        [Test]
        public async Task Resolve_RealSvnWorkingCopy_ChinesePath()
        {
            var svn = FindSvnExecutable();
            if (svn == null)
                Assert.Ignore("svn command-line client not found.");

            var repo = Path.Combine(_tempRoot, "repo");
            var wc = Path.Combine(_tempRoot, "工作 副本");
            var exit = await RunAsync(Path.Combine(Path.GetDirectoryName(svn), "svnadmin.exe"), "create \"" + repo + "\"");
            if (exit != 0)
                Assert.Ignore("svnadmin not available.");
            var url = "file:///" + repo.Replace('\\', '/');
            Assert.AreEqual(0, await RunAsync(svn, "checkout --non-interactive \"" + url + "\" \"" + wc + "\""));

            var unityRoot = Path.Combine(wc, "Game", "项目 A");
            Directory.CreateDirectory(Path.Combine(unityRoot, "Assets"));

            var info = await Task.Run(() => SvnWorkspaceInfoResolver.Resolve(unityRoot));

            Assert.IsTrue(info.IsCommandAvailable);
            Assert.IsTrue(info.IsWorkingCopy, info.ErrorMessage);
            Assert.AreEqual(UnityRootResolver.NormalizePath(wc), info.RootPath, info.ErrorMessage);
        }

        private static string FindSvnExecutable()
        {
            var candidates = new[]
            {
                Path.Combine(Environment.GetEnvironmentVariable("ProgramW6432") ?? string.Empty, "TortoiseSVN", "bin", "svn.exe"),
                Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? string.Empty, "TortoiseSVN", "bin", "svn.exe")
            };
            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';'))
            {
                try
                {
                    var path = Path.Combine(dir.Trim().Trim('"'), "svn.exe");
                    if (dir.Trim().Length > 0 && File.Exists(path))
                        return path;
                }
                catch (ArgumentException) { /* 非法条目 */ }
            }
            return null;
        }

        private static Task<int> RunAsync(string fileName, string arguments)
        {
            return Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo(fileName, arguments)
                    {
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true
                    };
                    using (var p = Process.Start(psi))
                    {
                        if (p == null)
                            return -1;
                        var stdout = p.StandardOutput.ReadToEndAsync();
                        var stderr = p.StandardError.ReadToEndAsync();
                        if (!p.WaitForExit(60000))
                        {
                            try { p.Kill(); } catch { /* 忽略 */ }
                            return -1;
                        }
                        Task.WaitAll(stdout, stderr); // 线程池线程上等待已结束进程的管道，不涉及主线程
                        return p.ExitCode;
                    }
                }
                catch (Exception)
                {
                    return -1;
                }
            });
        }
    }
}
