#if AGENTCORE_VCS
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using AgentCore.Editor.Components.VCS.Tools;
using AgentCore.Editor.Components.VCS.UI;

namespace AgentCore.Tests.Editor.VCS
{
    /// <summary>
    /// Project 窗口 SVN 角标解析（<see cref="SvnOverlayStatusParser"/>）、VCS 路径 ↔ Assets 路径换算与 .meta 伴随文件、
    /// TortoiseProc 定位、远端检查退避、远端锁快照的 SessionState 往返。XML 样本取自 svn 1.14.3 的真实输出（中文 + 空格路径、needs-lock、他人锁定），
    /// 经 <see cref="SvnStatusXmlParser"/>（与 SvnAdapter 同一份解析）得到结构化结果后再生成角标。
    /// </summary>
    [TestFixture]
    public class SvnProjectWindowOverlayTests
    {
        private const string WcRoot = @"C:\wc";
        private const string UnityRoot = @"C:\wc\Game\项目 A";

        // svn status --xml（在工作副本根执行，路径相对于工作副本根，反斜杠）
        private const string StatusXml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<status>\n<target\n   path=\".\">\n" +
            "<entry\n   path=\"Game\\项目 A\\Assets\\Main.unity.meta\">\n<wc-status\n   item=\"modified\"\n   revision=\"2\"\n   props=\"none\">\n" +
            "<commit\n   revision=\"2\">\n<author>akari</author>\n</commit>\n</wc-status>\n</entry>\n" +
            "<entry\n   path=\"Game\\项目 A\\Assets\\Art\\New Tex.png\">\n<wc-status\n   item=\"unversioned\"\n   props=\"none\">\n</wc-status>\n</entry>\n" +
            "<entry\n   path=\"Game\\项目 A\\Assets\\Locked.prefab\">\n<wc-status\n   item=\"normal\"\n   props=\"none\"\n   revision=\"2\">\n" +
            "<lock>\n<token>opaquelocktoken:mine</token>\n<owner>me</owner>\n</lock>\n</wc-status>\n</entry>\n" +
            "<entry\n   path=\"Game\\项目 A\\Assets\\Shared\">\n<wc-status\n   item=\"external\"\n   props=\"none\">\n</wc-status>\n</entry>\n" +
            "<entry\n   path=\"Tools\\build.bat\">\n<wc-status\n   item=\"modified\"\n   props=\"none\">\n</wc-status>\n</entry>\n" +
            "</target>\n</status>\n";

        // svn status -u -q --xml（远端检查）：Main.unity 被 other 锁定；Locked.prefab 是自己的锁
        private const string RemoteXml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<status>\n<target\n   path=\".\">\n" +
            "<entry\n   path=\"Game\\项目 A\\Assets\\Main.unity\">\n<wc-status\n   props=\"normal\"\n   item=\"normal\"\n   revision=\"2\">\n</wc-status>\n" +
            "<repos-status\n   item=\"none\"\n   props=\"none\">\n<lock>\n<token>opaquelocktoken:71df</token>\n<owner>other</owner>\n<comment>editing</comment>\n</lock>\n</repos-status>\n</entry>\n" +
            "<entry\n   path=\"Game\\项目 A\\Assets\\Locked.prefab\">\n<wc-status\n   item=\"normal\"\n   props=\"none\">\n<lock>\n<token>opaquelocktoken:mine</token>\n</lock>\n</wc-status>\n" +
            "<repos-status\n   item=\"none\"\n   props=\"none\">\n<lock>\n<token>opaquelocktoken:mine</token>\n<owner>me</owner>\n</lock>\n</repos-status>\n</entry>\n" +
            "<against\n   revision=\"2\"/>\n</target>\n</status>\n";

        // svn propget svn:needs-lock -R --xml（目标路径为绝对正斜杠路径）
        private const string NeedsLockXml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<properties>\n<target\n   path=\"C:/wc/Game/项目 A/Assets/Main.unity\">\n" +
            "<property\n   name=\"svn:needs-lock\">*</property>\n</target>\n</properties>\n";

        private static VcsStatusResult LocalStatus(string xml)
        {
            var parsed = SvnStatusXmlParser.Parse(xml);
            return new VcsStatusResult
            {
                Success = true,
                Files = parsed.Files,
                LockedFiles = parsed.LockedFiles,
                Truncated = parsed.IsIncomplete
            };
        }

        private static VcsFileStatus Lock(string path, string state, string owner = null, string comment = null)
        {
            return new VcsFileStatus { FilePath = path, State = VcsFileState.Unmodified, LockState = state, LockOwner = owner, LockComment = comment };
        }

        [Test]
        public void LocalStatus_MapsWcRelativePathsToAssetPaths_AndMergesMeta()
        {
            var snapshot = new SvnOverlaySnapshot();
            SvnOverlayStatusParser.ApplyLocalStatus(LocalStatus(StatusXml), WcRoot, UnityRoot, snapshot);

            // .meta 的改动标在资产本身上（Project 窗口不显示 .meta）
            Assert.AreEqual(SvnOverlayFlags.Modified, snapshot.Get("Assets/Main.unity"));
            Assert.AreEqual(SvnOverlayFlags.Unversioned, snapshot.Get("Assets/Art/New Tex.png"));
            Assert.AreEqual(SvnOverlayFlags.LockedByMe, snapshot.Get("Assets/Locked.prefab"));
            Assert.AreEqual("me", snapshot.LockOwners["Assets/Locked.prefab"]);
            // external 本身不是改动；工程外的文件被丢弃
            Assert.AreEqual(SvnOverlayFlags.None, snapshot.Get("Assets/Shared"));
            Assert.IsFalse(snapshot.Flags.ContainsKey("Tools/build.bat"));
            Assert.IsFalse(snapshot.IsIncomplete);
        }

        [Test]
        public void LocalStatus_PathsRelativeToUnityRoot()
        {
            var xml = StatusXml.Replace("Game\\项目 A\\", string.Empty);
            var snapshot = new SvnOverlaySnapshot();
            SvnOverlayStatusParser.ApplyLocalStatus(LocalStatus(xml), UnityRoot, UnityRoot, snapshot);

            Assert.AreEqual(SvnOverlayFlags.Modified, snapshot.Get("assets/main.unity")); // 忽略大小写
        }

        [Test]
        public void RemoteLocks_OtherOwner_FlaggedWithOwnerAndComment_OwnLockKept()
        {
            var snapshot = new SvnOverlaySnapshot();
            SvnOverlayStatusParser.ApplyLocalStatus(LocalStatus(StatusXml), WcRoot, UnityRoot, snapshot);
            var remote = SvnStatusXmlParser.Parse(RemoteXml);
            SvnOverlayStatusParser.ApplyRemoteLocks(remote.LockedFiles, WcRoot, UnityRoot, snapshot);

            Assert.AreEqual(SvnOverlayFlags.Modified | SvnOverlayFlags.LockedByOther, snapshot.Get("Assets/Main.unity"));
            Assert.AreEqual("other", snapshot.LockOwners["Assets/Main.unity"]);
            Assert.AreEqual("editing", snapshot.LockComments["Assets/Main.unity"]);
            Assert.AreEqual(SvnOverlayFlags.LockedByMe, snapshot.Get("Assets/Locked.prefab"));
        }

        [Test]
        public void RemoteLocks_StolenOrBroken_ReplaceOwnLock_StaleEntriesIgnored()
        {
            var snapshot = new SvnOverlaySnapshot();
            SvnOverlayStatusParser.ApplyLocalStatus(null, new[]
            {
                Lock(@"Game\项目 A\Assets\A.prefab", "K", "me"),
                Lock(@"Game\项目 A\Assets\B.prefab", "K", "me"),
                Lock(@"Game\项目 A\Assets\Mine.prefab", "K", "me")
            }, WcRoot, UnityRoot, snapshot);

            SvnOverlayStatusParser.ApplyRemoteLocks(new[]
            {
                Lock(@"Game\项目 A\Assets\A.prefab", "T", "bob", "mine now"),
                Lock(@"Game\项目 A\Assets\B.prefab", "B"),
                // 远端快照比本地旧：本地已重新加锁 → 忽略 O；本地已没有锁 → 忽略 T
                Lock(@"Game\项目 A\Assets\Mine.prefab", "O", "bob"),
                Lock(@"Game\项目 A\Assets\Gone.prefab", "T", "bob")
            }, WcRoot, UnityRoot, snapshot);

            Assert.AreEqual(SvnOverlayFlags.LockLost, snapshot.Get("Assets/A.prefab"));
            Assert.AreEqual("bob", snapshot.LockOwners["Assets/A.prefab"]);
            Assert.AreEqual(SvnOverlayFlags.LockLost, snapshot.Get("Assets/B.prefab"));
            Assert.IsFalse(snapshot.LockOwners.ContainsKey("Assets/B.prefab"));
            Assert.AreEqual(SvnOverlayFlags.LockedByMe, snapshot.Get("Assets/Mine.prefab"));
            Assert.AreEqual(SvnOverlayFlags.None, snapshot.Get("Assets/Gone.prefab"));
        }

        [Test]
        public void MapState_CoversAdapterStates()
        {
            Assert.AreEqual(SvnOverlayFlags.Added, SvnOverlayStatusParser.MapState(VcsFileState.Renamed));
            Assert.AreEqual(SvnOverlayFlags.Added, SvnOverlayStatusParser.MapState(VcsFileState.Copied));
            Assert.AreEqual(SvnOverlayFlags.Missing, SvnOverlayStatusParser.MapState(VcsFileState.Missing));
            Assert.AreEqual(SvnOverlayFlags.Conflicted, SvnOverlayStatusParser.MapState(VcsFileState.Conflicted));
            Assert.AreEqual(SvnOverlayFlags.None, SvnOverlayStatusParser.MapState(VcsFileState.Unmodified));
            Assert.AreEqual(SvnOverlayFlags.None, SvnOverlayStatusParser.MapState(VcsFileState.Ignored));
        }

        [Test]
        public void NeedsLock_AbsoluteTargets()
        {
            var snapshot = new SvnOverlaySnapshot();
            SvnOverlayStatusParser.ApplyNeedsLockXml(NeedsLockXml, UnityRoot, UnityRoot, snapshot);

            Assert.AreEqual(SvnOverlayFlags.NeedsLock, snapshot.Get("Assets/Main.unity"));
        }

        [Test]
        public void NeedsLock_SharedPropgetParser_TruncationTolerant()
        {
            CollectionAssert.AreEqual(new[] { "C:/wc/Game/项目 A/Assets/Main.unity" }, SvnAdapter.ParsePropgetTargets(NeedsLockXml));

            var cut = NeedsLockXml.Substring(0, NeedsLockXml.IndexOf("</property>", StringComparison.Ordinal));
            var targets = SvnStatusXmlParser.ParsePropgetTargets(cut, out var incomplete);
            Assert.IsTrue(incomplete);
            Assert.AreEqual(1, targets.Count);

            var snapshot = new SvnOverlaySnapshot();
            SvnOverlayStatusParser.ApplyNeedsLockXml(cut, UnityRoot, UnityRoot, snapshot);
            Assert.IsTrue(snapshot.IsIncomplete);
            Assert.AreEqual(SvnOverlayFlags.NeedsLock, snapshot.Get("Assets/Main.unity"));
        }

        [Test]
        public void PropagateToFolders_MarksAncestorsOnlyForLocalChanges()
        {
            var snapshot = new SvnOverlaySnapshot();
            SvnOverlayStatusParser.ApplyLocalStatus(LocalStatus(StatusXml), WcRoot, UnityRoot, snapshot);
            SvnOverlayStatusParser.ApplyNeedsLockXml(NeedsLockXml, UnityRoot, UnityRoot, snapshot);
            SvnOverlayStatusParser.PropagateToFolders(snapshot);

            Assert.AreEqual(SvnOverlayFlags.ChildChanged, snapshot.Get("Assets/Art"));
            Assert.IsTrue((snapshot.Get("Assets") & SvnOverlayFlags.ChildChanged) != 0);
            Assert.AreEqual(SvnOverlayFlags.Modified | SvnOverlayFlags.NeedsLock, snapshot.Get("Assets/Main.unity"));
        }

        [Test]
        public void TruncatedStatus_KeepsParsedEntries_MarksIncomplete()
        {
            var cut = StatusXml.IndexOf("<entry\n   path=\"Game\\项目 A\\Assets\\Locked.prefab", StringComparison.Ordinal) + 20;
            var snapshot = new SvnOverlaySnapshot();
            SvnOverlayStatusParser.ApplyLocalStatus(LocalStatus(StatusXml.Substring(0, cut)), WcRoot, UnityRoot, snapshot);

            Assert.IsTrue(snapshot.IsIncomplete);
            Assert.AreEqual(SvnOverlayFlags.Modified, snapshot.Get("Assets/Main.unity"));
        }

        [Test]
        public void EmptyOrGarbage_NoThrow()
        {
            var snapshot = new SvnOverlaySnapshot();
            SvnOverlayStatusParser.ApplyLocalStatus(LocalStatus(null), WcRoot, UnityRoot, snapshot);
            SvnOverlayStatusParser.ApplyLocalStatus(LocalStatus("svn: E155036: too old"), WcRoot, UnityRoot, snapshot);
            SvnOverlayStatusParser.ApplyLocalStatus(null, WcRoot, UnityRoot, snapshot);
            SvnOverlayStatusParser.ApplyRemoteLocks(null, WcRoot, UnityRoot, snapshot);
            SvnOverlayStatusParser.ApplyNeedsLockXml("", WcRoot, UnityRoot, snapshot);
            Assert.IsTrue(snapshot.IsEmpty);
        }

        [Test]
        public void LockTooltip_ShowsOwnerAndComment()
        {
            var tooltip = VcsProjectWindowIntegration.BuildLockTooltip("SVN: locked by bob", "editing b", "as of the last remote check");
            StringAssert.Contains("bob", tooltip);
            StringAssert.Contains("\"editing b\"", tooltip);
            StringAssert.Contains("last remote check", tooltip);
            Assert.AreEqual("SVN: locked by you", VcsProjectWindowIntegration.BuildLockTooltip("SVN: locked by you", "  ", null));
        }

        // ── 远端锁快照（SessionState）──

        [Test]
        public void RemoteSnapshotJson_RoundTripsLocksAndRevision()
        {
            var status = new VcsSyncStatus
            {
                Success = true,
                HasRemoteChanges = true,
                RemoteChangeCount = 2,
                Summary = "Remote has 2 pending update(s).",
                RemoteRevision = "42",
                PathBase = WcRoot,
                LockedFiles = new List<VcsFileStatus>
                {
                    Lock(@"Game\项目 A\Assets\Main.unity", "O", "other", "editing"),
                    Lock(@"Game\项目 A\Assets\Locked.prefab", "K", "me"),
                    Lock(@"Game\项目 A\Assets\Stolen.prefab", "T", "bob")
                }
            };

            var restored = VcsRemoteStatusMonitor.FromSnapshotJson(VcsRemoteStatusMonitor.ToSnapshotJson(status));

            Assert.IsNotNull(restored);
            Assert.IsTrue(restored.Success);
            Assert.AreEqual("42", restored.RemoteRevision);
            Assert.AreEqual(WcRoot, restored.PathBase);
            Assert.AreEqual(2, restored.RemoteChangeCount);
            Assert.AreEqual(3, restored.LockedFiles.Count);
            Assert.AreEqual("O", restored.LockedFiles[0].LockState);
            Assert.AreEqual("other", restored.LockedFiles[0].LockOwner);
            Assert.AreEqual("editing", restored.LockedFiles[0].LockComment);
            Assert.IsNull(restored.LockedFiles[1].LockComment);
            Assert.IsNull(restored.ErrorMessage);

            // 还原后的快照可以直接生成"他人锁定"角标
            var snapshot = new SvnOverlaySnapshot();
            SvnOverlayStatusParser.ApplyRemoteLocks(restored.LockedFiles, restored.PathBase, UnityRoot, snapshot);
            Assert.AreEqual(SvnOverlayFlags.LockedByOther, snapshot.Get("Assets/Main.unity"));
        }

        [Test]
        public void RemoteSnapshotJson_EmptyOrCorrupt_ReturnsNull()
        {
            Assert.IsNull(VcsRemoteStatusMonitor.FromSnapshotJson(null));
            Assert.IsNull(VcsRemoteStatusMonitor.FromSnapshotJson(""));
            Assert.IsNull(VcsRemoteStatusMonitor.FromSnapshotJson("{not json"));
        }

        // ── 路径换算 ──

        [Test]
        public void ToUnityRelativePath_NestedProject()
        {
            Assert.AreEqual("Assets/A b/中文.prefab",
                VcsAssetPaths.ToUnityRelativePath(@"Game\项目 A\Assets\A b\中文.prefab", WcRoot, UnityRoot));
            Assert.AreEqual("Assets/X.cs", VcsAssetPaths.ToUnityRelativePath("Game/项目 A/Assets/X.cs", WcRoot, UnityRoot + "\\"));
            Assert.IsNull(VcsAssetPaths.ToUnityRelativePath(@"Game\项目 AB\Assets\X.cs", WcRoot, UnityRoot)); // 前缀相同但不是子目录
            Assert.IsNull(VcsAssetPaths.ToUnityAssetPath(@"Game\项目 A\ProjectSettings\TagManager.asset", WcRoot, UnityRoot));
            Assert.AreEqual("Packages/manifest.json", VcsAssetPaths.ToUnityAssetPath(@"Game\项目 A\Packages\manifest.json", WcRoot, UnityRoot));
        }

        [Test]
        public void AbsoluteToUnityRelativePath_RootAndOutside()
        {
            Assert.AreEqual(string.Empty, VcsAssetPaths.AbsoluteToUnityRelativePath(UnityRoot, UnityRoot));
            Assert.IsNull(VcsAssetPaths.AbsoluteToUnityRelativePath(@"C:\other\Assets\a.cs", UnityRoot));
        }

        [Test]
        public void WithMetaCompanions_AddsOnlyAvailableCompanions_PreservesOrder()
        {
            var available = new[] { "Assets/A.prefab", "Assets/A.prefab.meta", "Assets/B.mat.meta", "Assets/C.cs" };

            var result = VcsAssetPaths.WithMetaCompanions(new[] { "Assets/A.prefab", "Assets\\B.mat.meta", "Assets/C.cs", "assets/a.prefab" }, available);

            CollectionAssert.AreEqual(new[] { "Assets/A.prefab", "Assets/A.prefab.meta", "Assets\\B.mat.meta", "Assets/C.cs" }, result);
        }

        [Test]
        public void WithMetaCompanionsOnDisk_AddsExistingMeta()
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\p\Assets\Folder.meta", @"C:\p\Assets\x.png.meta" };

            var result = VcsAssetPaths.WithMetaCompanionsOnDisk(
                new[] { @"C:\p\Assets\Folder\", @"C:\p\Assets\x.png", @"C:\p\Assets\y.txt" }, existing.Contains);

            CollectionAssert.AreEqual(new[]
            {
                @"C:\p\Assets\Folder", @"C:\p\Assets\Folder.meta", @"C:\p\Assets\x.png", @"C:\p\Assets\x.png.meta", @"C:\p\Assets\y.txt"
            }, result);
        }

        // ── TortoiseProc 定位 ──

        [Test]
        public void FindTortoiseProc_PrefersRegistryProcPath()
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"D:\Tools\TSVN\bin\TortoiseProc.exe" };
            var found = VcsExternalToolLauncher.FindTortoiseProc(
                name => name == "ProcPath" ? @"D:\Tools\TSVN\bin\TortoiseProc.exe" : null,
                _ => null,
                files.Contains);

            Assert.AreEqual(@"D:\Tools\TSVN\bin\TortoiseProc.exe", found);
        }

        [Test]
        public void FindTortoiseProc_FallsBackToProgramFiles_ThenPath()
        {
            var env = new Dictionary<string, string>
            {
                ["ProgramW6432"] = @"C:\Program Files",
                ["PATH"] = @"C:\Windows;""E:\Portable TSVN\bin"""
            };

            var programFiles = VcsExternalToolLauncher.FindTortoiseProc(_ => null, n => env.TryGetValue(n, out var v) ? v : null,
                p => p == Path.Combine(@"C:\Program Files", "TortoiseSVN", "bin", "TortoiseProc.exe"));
            Assert.AreEqual(@"C:\Program Files\TortoiseSVN\bin\TortoiseProc.exe", programFiles);

            var onPath = VcsExternalToolLauncher.FindTortoiseProc(_ => null, n => env.TryGetValue(n, out var v) ? v : null,
                p => p == Path.Combine(@"E:\Portable TSVN\bin", "TortoiseProc.exe"));
            Assert.AreEqual(@"E:\Portable TSVN\bin\TortoiseProc.exe", onPath);

            Assert.IsNull(VcsExternalToolLauncher.FindTortoiseProc(_ => null, _ => null, _ => false));
        }

        // ── 远端检查退避 ──

        [Test]
        public void RemoteCheckInterval_BacksOffAndCaps()
        {
            Assert.AreEqual(TimeSpan.FromMinutes(5), VcsRemoteStatusMonitor.GetCheckInterval(5, 0));
            Assert.AreEqual(TimeSpan.FromMinutes(10), VcsRemoteStatusMonitor.GetCheckInterval(5, 1));
            Assert.AreEqual(TimeSpan.FromMinutes(40), VcsRemoteStatusMonitor.GetCheckInterval(5, 3));
            Assert.AreEqual(TimeSpan.FromMinutes(40), VcsRemoteStatusMonitor.GetCheckInterval(5, 16));
            Assert.AreEqual(TimeSpan.FromMinutes(60), VcsRemoteStatusMonitor.GetCheckInterval(30, 5));
            Assert.AreEqual(TimeSpan.FromMinutes(120), VcsRemoteStatusMonitor.GetCheckInterval(120, 5));
        }
    }
}
#endif
