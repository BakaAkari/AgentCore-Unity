#if AGENTCORE_VCS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using AgentCore.Editor.Components.VCS.Tools;

namespace AgentCore.Tests.Editor.VCS
{
    /// <summary>
    /// <see cref="SvnAdapter"/> 集成测试：在临时目录中用 svnadmin 建立 file:// 仓库并检出工作副本，跑真实的 svn 命令。
    /// 没有 svn / svnadmin（TortoiseSVN 需勾选 command line client tools）时 Ignore。只使用 ASCII 路径，与系统代码页无关。
    /// </summary>
    [TestFixture]
    public class SvnAdapterIntegrationTests
    {
        private string _root;
        private string _repoUrl;
        private string _wc;
        private SvnAdapter _adapter;

        /// <summary>每个测试开头调用（Unity Test Framework 的 [SetUp] 不支持 async Task）。</summary>
        private async Task CreateWorkingCopyAsync()
        {
            var svn = SvnCommandLine.ResolveExecutable("svn");
            var svnDirectory = Path.IsPathRooted(svn) ? Path.GetDirectoryName(svn) : null;
            var svnadmin = svnDirectory != null ? Path.Combine(svnDirectory, "svnadmin.exe") : "svnadmin";

            var probe = await VcsCommandExecutor.ExecuteAsync("svn", new[] { "--version", "--quiet" }, Path.GetTempPath(), 30);
            if (!probe.Success)
                Assert.Ignore("svn command line client is not available.");

            _root = Path.Combine(Path.GetTempPath(), "agentcore-svn-it-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var repo = Path.Combine(_root, "repo");
            _wc = Path.Combine(_root, "wc");
            Directory.CreateDirectory(_root);

            var create = await VcsCommandExecutor.ExecuteAsync(svnadmin, new[] { "create", repo }, _root, 60);
            if (!create.Success)
                Assert.Ignore("svnadmin is not available: " + create.ErrorMessage);

            _repoUrl = "file:///" + repo.Replace('\\', '/');
            await Svn(_root, "checkout", "--non-interactive", _repoUrl, _wc);

            // 初始提交：Assets/a.txt + .meta
            Write("Assets.meta", "folder meta");
            Write("Assets/a.txt", "line1\nline2\n");
            Write("Assets/a.txt.meta", "meta a");
            await Svn(_wc, "add", "Assets", "Assets.meta");
            await Svn(_wc, "commit", "-m", "init", "--non-interactive");

            _adapter = new SvnAdapter(_wc);
        }

        [TearDown]
        public void TearDown()
        {
            if (string.IsNullOrEmpty(_root) || !Directory.Exists(_root))
                return;
            try
            {
                // svn 的 pristine / needs-lock 文件是只读的
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

        #region helpers

        private void Write(string relative, string content)
        {
            var path = Path.Combine(_wc, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (File.Exists(path))
                File.SetAttributes(path, FileAttributes.Normal);
            File.WriteAllText(path, content);
        }

        private static async Task<CommandResult> Svn(string cwd, params string[] args)
        {
            var result = await VcsCommandExecutor.ExecuteAsync("svn", args, cwd, 120);
            Assert.IsTrue(result.Success, $"svn {string.Join(" ", args)} failed: {result.ErrorMessage}");
            return result;
        }

        private async Task<bool> IsVersioned(string relative)
        {
            var info = await VcsCommandExecutor.ExecuteAsync("svn", new[] { "info", "--non-interactive", SvnCommandLine.PathArg(relative) }, _wc, 60);
            return info.Success;
        }

        private async Task<VcsFileStatus> StatusOf(string relative)
        {
            var status = await _adapter.GetStatusAsync();
            Assert.IsTrue(status.Success, status.ErrorMessage);
            var normalized = relative.Replace('\\', '/');
            return status.Files.FirstOrDefault(f => f.FilePath.Replace('\\', '/').EndsWith(normalized, StringComparison.OrdinalIgnoreCase));
        }

        #endregion

        [Test]
        public async Task CommitFiles_NewFolder_CommitsRequestedFileWithMetaAndParentsOnly()
        {
            await CreateWorkingCopyAsync();
            Write("Assets/New.meta", "folder meta");
            Write("Assets/New/b.txt", "b");
            Write("Assets/New/b.txt.meta", "meta b");
            Write("Assets/New/sibling.txt", "not requested");

            var result = await _adapter.CommitFilesAsync("add b", new List<string> { "Assets/New/b.txt" });

            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.IsTrue(await IsVersioned("Assets/New/b.txt"));
            Assert.IsTrue(await IsVersioned("Assets/New/b.txt.meta"));
            Assert.IsTrue(await IsVersioned("Assets/New.meta"));
            Assert.IsFalse(await IsVersioned("Assets/New/sibling.txt"), "unrequested sibling must not be committed");
            StringAssert.Contains("Committed revision 2", result.Message);
        }

        [Test]
        public async Task Revert_AddedDirectory_NonRecursiveFailsWithHint_RecursiveSucceeds()
        {
            await CreateWorkingCopyAsync();
            Write("Assets/Dir/c.txt", "c");
            await Svn(_wc, "add", "Assets/Dir");

            var shallow = await _adapter.RevertFilesAsync(new List<string> { "Assets/Dir" }, false);
            Assert.IsFalse(shallow.Success);
            StringAssert.Contains("E155038", shallow.ErrorMessage);
            StringAssert.Contains("recursive=true", shallow.ErrorMessage);

            var deep = await _adapter.RevertFilesAsync(new List<string> { "Assets/Dir" }, true);
            Assert.IsTrue(deep.Success, deep.ErrorMessage);
            Assert.IsFalse(await IsVersioned("Assets/Dir"));
            Assert.IsTrue(File.Exists(Path.Combine(_wc, "Assets", "Dir", "c.txt")), "revert keeps added files on disk");
        }

        [Test]
        public async Task Revert_UnversionedFile_IsSkippedNotFailed()
        {
            await CreateWorkingCopyAsync();
            Write("Assets/a.txt", "changed\n");
            Write("Assets/loose.txt", "unversioned");

            var result = await _adapter.RevertFilesAsync(new List<string> { "Assets/a.txt", "Assets/loose.txt" }, false);

            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.IsTrue(result.SkippedFiles.Keys.Any(k => k.Replace('\\', '/').EndsWith("Assets/loose.txt")));
            Assert.AreEqual("line1\nline2\n", File.ReadAllText(Path.Combine(_wc, "Assets", "a.txt")));
            Assert.IsTrue(File.Exists(Path.Combine(_wc, "Assets", "loose.txt")));
        }

        [Test]
        public async Task Unstage_KeepsLocalModifications()
        {
            await CreateWorkingCopyAsync();
            Write("Assets/a.txt", "modified\n");
            Write("Assets/n.txt", "new");

            var stage = await _adapter.StageFilesAsync(new List<string> { "Assets/n.txt" });
            Assert.IsTrue(stage.Success, stage.ErrorMessage);
            Assert.AreEqual(VcsFileState.Added, (await StatusOf("Assets/n.txt"))?.State);

            var unstage = await _adapter.UnstageFilesAsync(new List<string> { "Assets/n.txt", "Assets/a.txt" });

            Assert.IsTrue(unstage.Success, unstage.ErrorMessage);
            Assert.AreEqual(VcsFileState.Untracked, (await StatusOf("Assets/n.txt"))?.State);
            Assert.AreEqual(VcsFileState.Modified, (await StatusOf("Assets/a.txt"))?.State, "modified file must not be reverted by unstage");
            Assert.AreEqual("modified\n", File.ReadAllText(Path.Combine(_wc, "Assets", "a.txt")));
        }

        [Test]
        public async Task LockAndUnlock_ReportLockStateOwnerAndComment()
        {
            await CreateWorkingCopyAsync();
            var locked = await _adapter.LockFilesAsync(new List<string> { "Assets/a.txt" }, "editing scene");
            Assert.IsTrue(locked.Success, locked.ErrorMessage);

            var status = await _adapter.GetStatusAsync();
            var entry = status.LockedFiles.FirstOrDefault(f => f.FilePath.Replace('\\', '/').EndsWith("Assets/a.txt"));
            Assert.IsNotNull(entry, "locked unmodified file must be listed in LockedFiles");
            Assert.AreEqual("K", entry.LockState);
            Assert.IsFalse(string.IsNullOrEmpty(entry.LockOwner));
            Assert.AreEqual("editing scene", entry.LockComment);

            var unlocked = await _adapter.UnlockFilesAsync(new List<string> { "Assets/a.txt" });
            Assert.IsTrue(unlocked.Success, unlocked.ErrorMessage);
            Assert.IsEmpty((await _adapter.GetStatusAsync()).LockedFiles);
        }

        [Test]
        public async Task Lock_MissingFile_IsPartial()
        {
            await CreateWorkingCopyAsync();
            var result = await _adapter.LockFilesAsync(new List<string> { "Assets/a.txt", "Assets/nope.txt" }, null);

            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.IsTrue(result.IsPartial);
            Assert.IsTrue(result.FailedFiles.Keys.Any(k => k.Contains("nope.txt")));
        }

        [Test]
        public async Task Status_ModifiedNeedsLockFileWithoutLock_Warns()
        {
            await CreateWorkingCopyAsync();
            // Windows 版 svn 会对命令行里的 "*" 做通配展开，任意值都会被规范化为 "*"
            await Svn(_wc, "propset", "svn:needs-lock", "yes", "Assets/a.txt");
            await Svn(_wc, "commit", "-m", "needs-lock", "--non-interactive");
            Write("Assets/a.txt", "edited without lock\n");

            var status = await _adapter.GetStatusAsync();

            Assert.IsTrue(status.Success, status.ErrorMessage);
            Assert.IsTrue(status.Files.Single(f => f.FilePath.Replace('\\', '/').EndsWith("Assets/a.txt")).NeedsLock);
            Assert.IsTrue(status.Warnings.Any(w => w.Contains("svn:needs-lock")));
        }

        [Test]
        public async Task Sync_ConflictingUpdate_IsPartialWithConflictedFile()
        {
            await CreateWorkingCopyAsync();
            var other = Path.Combine(_root, "wc2");
            await Svn(_root, "checkout", "--non-interactive", _repoUrl, other);
            File.WriteAllText(Path.Combine(other, "Assets", "a.txt"), "theirs\nline2\n");
            await Svn(other, "commit", "-m", "theirs", "--non-interactive");

            Write("Assets/a.txt", "mine\nline2\n");
            var result = await _adapter.SyncAsync();

            Assert.IsTrue(result.Success, result.ErrorMessage);
            Assert.IsTrue(result.IsPartial, "conflicts must make the update partial");
            Assert.IsTrue(result.ConflictedFiles.Any(f => f.Replace('\\', '/').EndsWith("Assets/a.txt")));

            var resolve = await _adapter.ResolveFilesAsync(new List<string> { "Assets/a.txt" });
            Assert.IsTrue(resolve.Success, resolve.ErrorMessage);
            Assert.AreNotEqual(VcsFileState.Conflicted, (await StatusOf("Assets/a.txt"))?.State);
        }

        [Test]
        public async Task GetLogWithError_ReturnsCommitsAndNoError()
        {
            await CreateWorkingCopyAsync();
            var (commits, error) = await _adapter.GetLogWithErrorAsync(10);

            Assert.IsNull(error);
            Assert.AreEqual("init", commits.Single().Message);
        }
    }
}
#endif
