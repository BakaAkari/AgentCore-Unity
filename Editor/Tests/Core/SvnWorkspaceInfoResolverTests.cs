using System;
using System.IO;
using NUnit.Framework;
using AgentCore.Editor.Workspace;
using AgentCore.Editor.Workspace.Resolution;

namespace AgentCore.Tests.Editor.Core
{
    /// <summary>
    /// SvnWorkspaceInfoResolver.ResolveFast（主线程快速路径，不启动 svn 子进程）
    /// 与 WorkspaceRootResolver.Resolve(unityRoot, svnInfo) 的单元测试。
    /// </summary>
    [TestFixture]
    public class SvnWorkspaceInfoResolverTests
    {
        private string _tempRoot;

        [SetUp]
        public void SetUp()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "agentcore-svnfast-" + Guid.NewGuid().ToString("N"), "工作 副本");
            Directory.CreateDirectory(_tempRoot);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(Path.GetDirectoryName(_tempRoot), true); } catch { /* 忽略 */ }
        }

        [Test]
        public void ResolveFast_DotSvnAboveUnityRoot_FindsWorkingCopyRoot()
        {
            Directory.CreateDirectory(Path.Combine(_tempRoot, ".svn"));
            var unityRoot = Path.Combine(_tempRoot, "client", "unity");
            Directory.CreateDirectory(unityRoot);

            var info = SvnWorkspaceInfoResolver.ResolveFast(unityRoot);

            Assert.IsTrue(info.IsWorkingCopy);
            Assert.AreEqual(WorkspaceVcsType.Svn, info.Type);
            Assert.AreEqual(UnityRootResolver.NormalizePath(_tempRoot), info.RootPath);
            Assert.IsNull(info.Url, "快速路径不运行 svn info，不应有 URL");
        }

        [Test]
        public void ResolveFast_NoDotSvn_NotWorkingCopy()
        {
            var info = SvnWorkspaceInfoResolver.ResolveFast(_tempRoot);

            Assert.IsFalse(info.IsWorkingCopy);
            Assert.IsTrue(string.IsNullOrEmpty(info.RootPath));
        }

        [Test]
        public void WorkspaceRootResolver_WithProvidedInfo_UsesItsRoot()
        {
            var unityRoot = UnityRootResolver.NormalizePath(Path.Combine(_tempRoot, "unity"));
            var wsRoot = UnityRootResolver.NormalizePath(_tempRoot);
            var svnInfo = new WorkspaceVcsInfo
            {
                Type = WorkspaceVcsType.Svn,
                RootPath = wsRoot,
                Url = "https://svn.example.com/repo/branches/feature-x",
                IsWorkingCopy = true
            };

            var (root, status, vcs) = WorkspaceRootResolver.Resolve(unityRoot, svnInfo);

            Assert.AreEqual(wsRoot, root);
            Assert.AreEqual(WorkspaceResolutionStatus.Resolved, status);
            Assert.AreSame(svnInfo, vcs);
        }

        [Test]
        public void WorkspaceRootResolver_NoSvnRoot_FallsBackToUnityRoot()
        {
            var unityRoot = UnityRootResolver.NormalizePath(_tempRoot);

            var (root, status, vcs) = WorkspaceRootResolver.Resolve(unityRoot, new WorkspaceVcsInfo { Type = WorkspaceVcsType.Svn });

            Assert.AreEqual(unityRoot, root);
            Assert.AreEqual(WorkspaceResolutionStatus.FallbackToUnityRoot, status);
            Assert.AreEqual(WorkspaceVcsType.None, vcs.Type);
        }
    }
}
