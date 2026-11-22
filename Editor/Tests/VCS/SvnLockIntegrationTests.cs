using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using AgentCore.Editor.Components.VCS.Tools;
using AgentCore.Editor.Components.VCS.UI;
using AgentCore.Editor.Utils;
using AgentCore.Editor.Workspace.Resolution;

namespace AgentCore.Tests.Editor.VCS
{
    /// <summary>
    /// 真实 svn 的锁 / 远端检查 / 工作副本解析集成测试：临时目录中 svnadmin 建 file:// 仓库，检出两个工作副本模拟两位用户。
    /// 覆盖 svn status -u 的 O / T、RemoteRevision、PathBase → 角标 → SessionState 快照往返，
    /// 以及 SvnWorkspaceInfoResolver / SvnExecutable.Run 经统一定位器运行 svn。没有 svn / svnadmin 时 Ignore。
    /// </summary>
    [TestFixture]
    public class SvnLockIntegrationTests
    {
        private string _root;
        private string _repoUrl;
        private string _wc;
        private string _wc2;
        private SvnAdapter _adapter;

        /// <summary>每个测试开头调用（Unity Test Framework 的 [SetUp] 不支持 async Task）。</summary>
        private async Task CreateRepositoryAsync()
        {
            var svn = SvnCommandLine.ResolveExecutable("svn");
            var svnDirectory = Path.IsPathRooted(svn) ? Path.GetDirectoryName(svn) : null;
            var svnadmin = svnDirectory != null ? Path.Combine(svnDirectory, "svnadmin.exe") : "svnadmin";

            var probe = await VcsCommandExecutor.ExecuteAsync("svn", new[] { "--version", "--quiet" }, Path.GetTempPath(), 30);
            if (!probe.Success)
                Assert.Ignore("svn command line client is not available.");

            _root = Path.Combine(Path.GetTempPath(), "agentcore-svn-lock-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var repo = Path.Combine(_root, "repo");
            _wc = Path.Combine(_root, "wc");
            _wc2 = Path.Combine(_root, "wc2");
            Directory.CreateDirectory(_root);

            var create = await VcsCommandExecutor.ExecuteAsync(svnadmin, new[] { "create", repo }, _root, 60);
            if (!create.Success)
                Assert.Ignore("svnadmin is not available: " + create.ErrorMessage);

            _repoUrl = "file:///" + repo.Replace('\\', '/');
            await Svn(_root, "checkout", "--non-interactive", _repoUrl, _wc);

            Write(_wc, "Assets.meta", "folder meta");
            Write(_wc, "Assets/a.txt", "a");
            Write(_wc, "Assets/a.txt.meta", "meta a");
            await Svn(_wc, "add", "Assets", "Assets.meta");
            await Svn(_wc, "commit", "-m", "init", "--non-interactive", "--username", "alice");

            await Svn(_root, "checkout", "--non-interactive", _repoUrl, _wc2);
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

        private static void Write(string wc, string relative, string content)
        {
            var path = Path.Combine(wc, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (File.Exists(path))
                File.SetAttributes(path, FileAttributes.Normal);
            File.WriteAllText(path, content);
        }

        private static async Task Svn(string cwd, params string[] args)
        {
            var result = await VcsCommandExecutor.ExecuteAsync("svn", args, cwd, 120);
            Assert.IsTrue(result.Success, $"svn {string.Join(" ", args)} failed: {result.ErrorMessage}");
        }

        private static VcsFileStatus FindLock(VcsSyncStatus status, string relative)
        {
            return status.LockedFiles.FirstOrDefault(f => f.FilePath.Replace('\\', '/').Equals(relative, StringComparison.OrdinalIgnoreCase));
        }

        [Test]
        public async Task SyncStatus_LockedByOtherWorkingCopy_ReportsOwnerComment_AndSurvivesSnapshot()
        {
            await CreateRepositoryAsync();
            await Svn(_wc2, "lock", "--non-interactive", "--username", "bob", "-m", "art pass", "Assets/a.txt");

            var sync = await _adapter.GetSyncStatusAsync();
            Assert.IsTrue(sync.Success, sync.ErrorMessage);
            Assert.AreEqual("1", sync.RemoteRevision);
            Assert.AreEqual(_wc, sync.PathBase);

            var locked = FindLock(sync, "Assets/a.txt");
            Assert.IsNotNull(locked, "file locked only by another working copy must be listed");
            Assert.AreEqual("O", locked.LockState);
            Assert.AreEqual("bob", locked.LockOwner);
            Assert.AreEqual("art pass", locked.LockComment);

            // 角标：红色"他人锁定"，带持有者与注释
            var local = await _adapter.GetStatusAsync();
            Assert.IsTrue(local.Success, local.ErrorMessage);
            var snapshot = new SvnOverlaySnapshot();
            SvnOverlayStatusParser.ApplyLocalStatus(local, _wc, _wc, snapshot);
            SvnOverlayStatusParser.ApplyRemoteLocks(sync.LockedFiles, sync.PathBase, _wc, snapshot);
            Assert.IsTrue((snapshot.Get("Assets/a.txt") & SvnOverlayFlags.LockedByOther) != 0);
            Assert.AreEqual("bob", snapshot.LockOwners["Assets/a.txt"]);
            Assert.AreEqual("art pass", snapshot.LockComments["Assets/a.txt"]);

            // SessionState 快照往返（域重载后角标依赖它）
            var restored = VcsRemoteStatusMonitor.FromSnapshotJson(VcsRemoteStatusMonitor.ToSnapshotJson(sync));
            Assert.IsNotNull(restored);
            Assert.AreEqual("1", restored.RemoteRevision);
            Assert.AreEqual(_wc, restored.PathBase);
            var restoredLock = FindLock(restored, "Assets/a.txt");
            Assert.IsNotNull(restoredLock);
            Assert.AreEqual("O", restoredLock.LockState);
            Assert.AreEqual("bob", restoredLock.LockOwner);
        }

        [Test]
        public async Task SyncStatus_StolenLock_ReportsT_AndOverlayShowsLockLost()
        {
            await CreateRepositoryAsync();
            await Svn(_wc, "lock", "--non-interactive", "--username", "alice", "-m", "mine", "Assets/a.txt");
            await Svn(_wc2, "lock", "--force", "--non-interactive", "--username", "bob", "-m", "art pass", "Assets/a.txt");

            var local = await _adapter.GetStatusAsync();
            Assert.IsTrue(local.Success, local.ErrorMessage);
            var sync = await _adapter.GetSyncStatusAsync();
            Assert.IsTrue(sync.Success, sync.ErrorMessage);

            var locked = FindLock(sync, "Assets/a.txt");
            Assert.IsNotNull(locked);
            Assert.AreEqual("T", locked.LockState);
            Assert.AreEqual("bob", locked.LockOwner);

            var snapshot = new SvnOverlaySnapshot();
            SvnOverlayStatusParser.ApplyLocalStatus(local, _wc, _wc, snapshot);
            Assert.IsTrue((snapshot.Get("Assets/a.txt") & SvnOverlayFlags.LockedByMe) != 0, "without -u the local token still looks valid (K)");
            SvnOverlayStatusParser.ApplyRemoteLocks(sync.LockedFiles, sync.PathBase, _wc, snapshot);
            var flags = snapshot.Get("Assets/a.txt");
            Assert.IsTrue((flags & SvnOverlayFlags.LockLost) != 0);
            Assert.IsTrue((flags & SvnOverlayFlags.LockedByMe) == 0);
            Assert.AreEqual("bob", snapshot.LockOwners["Assets/a.txt"]);
        }

        [Test]
        public async Task SyncStatus_NewCommitElsewhere_RemoteRevisionAdvances()
        {
            await CreateRepositoryAsync();
            Write(_wc2, "Assets/a.txt", "changed elsewhere");
            await Svn(_wc2, "commit", "-m", "remote change", "--non-interactive", "--username", "bob");

            var sync = await _adapter.GetSyncStatusAsync();
            Assert.IsTrue(sync.Success, sync.ErrorMessage);
            Assert.AreEqual("2", sync.RemoteRevision, "the panel refreshes its log when this changes");
            Assert.IsTrue(sync.HasRemoteChanges);
        }

        [Test]
        public async Task WorkspaceInfoResolver_NestedProjectFolder_ReturnsWorkingCopyRoot()
        {
            await CreateRepositoryAsync();
            var nested = Path.Combine(_wc, "Game", "项目 A");
            if (!SvnCommandLine.CanPassToSvn(nested))
                Assert.Ignore("system code page cannot represent the test path.");
            Directory.CreateDirectory(nested);
            await Svn(_wc, "add", "Game");
            await Svn(_wc, "commit", "-m", "nested", "--non-interactive", "--username", "alice");
            await Svn(_wc, "update", "--non-interactive");

            // 同步运行 svn：放到线程池，不阻塞主线程
            var info = await Task.Run(() => SvnWorkspaceInfoResolver.Resolve(nested));

            Assert.IsNull(info.ErrorMessage, info.ErrorMessage);
            Assert.IsTrue(info.IsCommandAvailable);
            Assert.IsTrue(info.IsWorkingCopy);
            // wcroot-abspath 是规范长路径，临时目录可能是 8.3 短路径：按目录内容比较而不是字符串
            Assert.IsNotNull(info.RootPath);
            StringAssert.EndsWith("/wc", info.RootPath.Replace('\\', '/').TrimEnd('/'));
            Assert.IsTrue(File.Exists(Path.Combine(info.RootPath, "Assets.meta")), "wcroot-abspath decoded as UTF-8: " + info.RootPath);
            Assert.AreEqual("2", info.Revision);
            StringAssert.EndsWith("/Game/%E9%A1%B9%E7%9B%AE%20A", info.Url);
        }

        [Test]
        public async Task SvnExecutable_Run_UsesLocatorAndDecodesOutput()
        {
            var probe = await VcsCommandExecutor.ExecuteAsync("svn", new[] { "--version", "--quiet" }, Path.GetTempPath(), 30);
            if (!probe.Success)
                Assert.Ignore("svn command line client is not available.");

            var run = await Task.Run(() => SvnExecutable.Run(Path.GetTempPath(), 30000, "--version", "--quiet"));

            Assert.IsTrue(run.Started, run.Error);
            Assert.IsFalse(run.TimedOut);
            Assert.AreEqual(0, run.ExitCode, run.Error);
            StringAssert.IsMatch(@"^\d+\.\d+\.\d+", run.Output.Trim());
            Assert.AreEqual(probe.Output.Trim(), run.Output.Trim(), "both paths resolve the same svn.exe");
        }
    }
}
