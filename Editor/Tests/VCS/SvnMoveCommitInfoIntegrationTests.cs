using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using AgentCore.Editor.Components.VCS.Tools;
using AgentCore.Editor.Components.VCS.UI;

namespace AgentCore.Tests.Editor.VCS
{
    /// <summary>
    /// 真实 svn 的 move_files（.meta 跟随、未受控回退文件系统移动）与 get_commit_info 集成测试，
    /// 以及修订号规范化 / externals 根解析的单元测试。没有 svn / svnadmin 时集成部分 Ignore。
    /// </summary>
    [TestFixture]
    public class SvnMoveCommitInfoIntegrationTests
    {
        private string _root;
        private string _wc;
        private SvnAdapter _adapter;

        private async Task CreateRepositoryAsync()
        {
            var svn = SvnCommandLine.ResolveExecutable("svn");
            var svnDirectory = Path.IsPathRooted(svn) ? Path.GetDirectoryName(svn) : null;
            var svnadmin = svnDirectory != null ? Path.Combine(svnDirectory, "svnadmin.exe") : "svnadmin";

            var probe = await VcsCommandExecutor.ExecuteAsync("svn", new[] { "--version", "--quiet" }, Path.GetTempPath(), 30);
            if (!probe.Success)
                Assert.Ignore("svn command line client is not available.");

            _root = Path.Combine(Path.GetTempPath(), "agentcore-svn-move-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var repo = Path.Combine(_root, "repo");
            _wc = Path.Combine(_root, "wc");
            Directory.CreateDirectory(_root);

            var create = await VcsCommandExecutor.ExecuteAsync(svnadmin, new[] { "create", repo }, _root, 60);
            if (!create.Success)
                Assert.Ignore("svnadmin is not available: " + create.ErrorMessage);

            var repoUrl = "file:///" + repo.Replace('\\', '/');
            await Svn(_root, "checkout", "--non-interactive", repoUrl, _wc);

            Write("Assets.meta", "folder meta");
            Write("Assets/a.txt", "a");
            Write("Assets/a.txt.meta", "meta a");
            await Svn(_wc, "add", "Assets", "Assets.meta");
            await Svn(_wc, "commit", "-m", "init 初始提交", "--non-interactive", "--username", "alice");
            _adapter = new SvnAdapter(_wc);
        }

        [TearDown]
        public void TearDown()
        {
            if (string.IsNullOrEmpty(_root) || !Directory.Exists(_root))
                return;
            try
            {
                foreach (var file in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(_root, true);
                _root = null;
            }
            catch (Exception)
            {
                // 临时目录清理失败不影响结果
            }
        }

        private void Write(string relative, string content)
        {
            var path = Path.Combine(_wc, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
        }

        private static async Task Svn(string cwd, params string[] args)
        {
            var result = await VcsCommandExecutor.ExecuteAsync("svn", args, cwd, 120);
            Assert.IsTrue(result.Success, $"svn {string.Join(" ", args)} failed: {result.ErrorMessage}");
        }

        private static VcsFileStatus Find(VcsStatusResult status, string relative) =>
            status.Files.FirstOrDefault(f => f.FilePath.Replace('\\', '/').Equals(relative, StringComparison.OrdinalIgnoreCase));

        [Test]
        public async Task Move_VersionedAsset_MovesMetaWithHistory_CreatesParents()
        {
            await CreateRepositoryAsync();

            var result = await _adapter.MoveAsync("Assets/a.txt", "Assets/Sub/b.txt");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.IsFalse(result.IsPartial, string.Join("; ", result.FailedFiles.Select(kv => kv.Key + ": " + kv.Value)));
            Assert.IsTrue(File.Exists(Path.Combine(_wc, "Assets/Sub/b.txt")));
            Assert.IsTrue(File.Exists(Path.Combine(_wc, "Assets/Sub/b.txt.meta")));
            Assert.IsFalse(File.Exists(Path.Combine(_wc, "Assets/a.txt.meta")));

            var status = await _adapter.GetStatusAsync();
            Assert.IsTrue(status.Success, status.ErrorMessage);
            Assert.AreEqual(VcsFileState.Renamed, Find(status, "Assets/Sub/b.txt")?.State);
            Assert.AreEqual(VcsFileState.Renamed, Find(status, "Assets/Sub/b.txt.meta")?.State);
        }

        [Test]
        public async Task Move_PassingMetaPaths_MovesAssetToo()
        {
            await CreateRepositoryAsync();

            var result = await _adapter.MoveAsync("Assets/a.txt.meta", "Assets/c.txt.meta");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.IsTrue(File.Exists(Path.Combine(_wc, "Assets/c.txt")));
            Assert.IsTrue(File.Exists(Path.Combine(_wc, "Assets/c.txt.meta")));
        }

        [Test]
        public async Task Move_UnversionedFile_FallsBackToFileSystem()
        {
            await CreateRepositoryAsync();
            Write("Assets/new.txt", "n");
            Write("Assets/new.txt.meta", "meta n");

            var result = await _adapter.MoveAsync("Assets/new.txt", "Assets/Moved/new.txt");
            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.IsTrue(File.Exists(Path.Combine(_wc, "Assets/Moved/new.txt")));
            Assert.IsTrue(File.Exists(Path.Combine(_wc, "Assets/Moved/new.txt.meta")));
            Assert.IsFalse(File.Exists(Path.Combine(_wc, "Assets/new.txt")));
        }

        [Test]
        public async Task Move_ExistingDestination_Fails()
        {
            await CreateRepositoryAsync();
            Write("Assets/b.txt", "b");

            var result = await _adapter.MoveAsync("Assets/a.txt", "Assets/b.txt");
            Assert.IsFalse(result.Success);
            StringAssert.Contains("already exists", result.ErrorMessage);
            Assert.IsTrue(File.Exists(Path.Combine(_wc, "Assets/a.txt")));
        }

        [Test]
        public async Task CommitInfo_ReturnsAuthorMessageAndChangedPaths()
        {
            await CreateRepositoryAsync();

            var info = await _adapter.GetCommitInfoAsync("r1");
            Assert.IsTrue(info.Success, info.ErrorMessage);
            Assert.AreEqual("1", info.Revision);
            Assert.AreEqual("alice", info.Author);
            Assert.AreEqual("init 初始提交", info.Message);
            CollectionAssert.Contains(info.ChangedFiles, "A /Assets/a.txt");
            CollectionAssert.Contains(info.ChangedFiles, "A /Assets.meta");

            var missing = await _adapter.GetCommitInfoAsync("99");
            Assert.IsFalse(missing.Success);

            var invalid = await _adapter.GetCommitInfoAsync("abc");
            Assert.IsFalse(invalid.Success);
            StringAssert.Contains("Invalid SVN revision", invalid.ErrorMessage);
        }

        [TestCase("123", "123")]
        [TestCase("r123", "123")]
        [TestCase(" R7 ", "7")]
        [TestCase("head", "HEAD")]
        [TestCase("PREV", "PREV")]
        [TestCase("r", null)]
        [TestCase("abc", null)]
        [TestCase("", null)]
        public void NormalizeRevision(string input, string expected)
        {
            Assert.AreEqual(expected, SvnAdapter.NormalizeRevision(input));
        }

        [Test]
        public void ParseSvnLogXml_ReadsChangedPathsWithCopySource()
        {
            const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<log><logentry revision=""5""><author>bob</author><date>2026-11-13T00:00:00.000000Z</date>
<paths>
<path action=""A"" kind=""file"" copyfrom-path=""/trunk/Assets/a.txt"" copyfrom-rev=""4"">/trunk/Assets/b.txt</path>
<path action=""D"" kind=""file"">/trunk/Assets/a.txt</path>
</paths><msg>rename</msg></logentry></log>";
            var commit = SvnAdapter.ParseSvnLogXml(xml).Single();
            CollectionAssert.AreEqual(new[]
            {
                "A /trunk/Assets/b.txt (from /trunk/Assets/a.txt@4)",
                "D /trunk/Assets/a.txt"
            }, commit.ChangedFiles);
        }

        [Test]
        public void ParseExternalRoots_ReturnsOnlyExternalEntries()
        {
            const string xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<status><target path=""."">
<entry path=""Assets/Plugins/Shared""><wc-status item=""external"" props=""none""/></entry>
<entry path=""Assets/a.txt""><wc-status item=""modified"" props=""none"" revision=""1""/></entry>
<entry path=""Assets/Libs/Art""><wc-status item=""external"" props=""none""/></entry>
</target></status>";
            CollectionAssert.AreEqual(new[] { "Assets/Plugins/Shared", "Assets/Libs/Art" }, SvnOverlayStatusParser.ParseExternalRoots(xml));
            CollectionAssert.IsEmpty(SvnOverlayStatusParser.ParseExternalRoots("<status><target"));
        }
    }
}
