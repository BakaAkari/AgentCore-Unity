using System;
using System.Collections.Generic;
using AgentCore.Editor.Components.VCS.Tools;
using AgentCore.Editor.Components.VCS.UI;
using NUnit.Framework;
using UnityEditor;

namespace AgentCore.Tests.Editor.VCS
{
    /// <summary>
    /// Unit tests for VCS Domain Reload SessionState caches:
    /// <see cref="VcsProjectWindowIntegration"/> overlay snapshot persistence and
    /// <see cref="VersionControlPanel"/> panel state cache serialization, caps, and corruption guards.
    /// </summary>
    [TestFixture]
    public class VcsReloadCacheTests
    {
        private const string TestOverlaySessionKey = "AgentCore.Tests.VCS.ProjectWindow.OverlaySnapshot";
        private const string TestPanelCacheSessionKey = "AgentCore.Tests.VCS.Panel.Cache";

        private SvnOverlaySnapshot _originalInMemoryOverlay;

        [SetUp]
        public void SetUp()
        {
            _originalInMemoryOverlay = VcsProjectWindowIntegration.GetInMemorySnapshotForTests();
            SessionState.EraseString(TestOverlaySessionKey);
            SessionState.EraseString(TestPanelCacheSessionKey);
        }

        [TearDown]
        public void TearDown()
        {
            SessionState.EraseString(TestOverlaySessionKey);
            SessionState.EraseString(TestPanelCacheSessionKey);

            // Restore whatever snapshot existed before the test without mutating production SessionState.
            if (_originalInMemoryOverlay == null)
            {
                VcsProjectWindowIntegration.ClearInMemorySnapshotForTests();
            }
            else
            {
                VcsProjectWindowIntegration.SaveOverlaySnapshotToSession(_originalInMemoryOverlay, TestOverlaySessionKey);
                VcsProjectWindowIntegration.RestoreOverlaySnapshotFromSession(TestOverlaySessionKey);
                SessionState.EraseString(TestOverlaySessionKey);
            }
        }

        #region VcsProjectWindowIntegration Overlay Cache

        [Test]
        public void OverlaySnapshot_SessionRoundTrip_RestoresFlagsAndLocksAfterInMemoryClear()
        {
            var snapshot = new SvnOverlaySnapshot();
            snapshot.Add("Assets/Scripts/Player.cs", SvnOverlayFlags.Modified | SvnOverlayFlags.NeedsLock);
            snapshot.Add("Assets/Prefabs/Hero.prefab", SvnOverlayFlags.LockedByOther);
            snapshot.SetLockInfo("Assets/Prefabs/Hero.prefab", "teammate", "editing combat prefab");
            snapshot.Add("Assets/Scenes/Main.unity", SvnOverlayFlags.LockedByMe | SvnOverlayFlags.Added);
            snapshot.SetLockInfo("Assets/Scenes/Main.unity", "me", null);
            snapshot.Add("Assets/Scripts", SvnOverlayFlags.ChildChanged);

            VcsProjectWindowIntegration.SaveOverlaySnapshotToSession(snapshot, TestOverlaySessionKey);

            // Simulate Domain Reload wiping static in-memory field.
            VcsProjectWindowIntegration.ClearInMemorySnapshotForTests();
            Assert.IsNull(VcsProjectWindowIntegration.GetInMemorySnapshotForTests());

            VcsProjectWindowIntegration.RestoreOverlaySnapshotFromSession(TestOverlaySessionKey);
            var restored = VcsProjectWindowIntegration.GetInMemorySnapshotForTests();

            Assert.IsNotNull(restored);
            Assert.IsFalse(restored.IsIncomplete);
            Assert.AreEqual(SvnOverlayFlags.Modified | SvnOverlayFlags.NeedsLock, restored.Get("Assets/Scripts/Player.cs"));
            Assert.AreEqual(SvnOverlayFlags.LockedByOther, restored.Get("Assets/Prefabs/Hero.prefab"));
            Assert.AreEqual("teammate", restored.LockOwners["Assets/Prefabs/Hero.prefab"]);
            Assert.AreEqual("editing combat prefab", restored.LockComments["Assets/Prefabs/Hero.prefab"]);
            Assert.AreEqual(SvnOverlayFlags.LockedByMe | SvnOverlayFlags.Added, restored.Get("Assets/Scenes/Main.unity"));
            Assert.AreEqual("me", restored.LockOwners["Assets/Scenes/Main.unity"]);
            Assert.IsFalse(restored.LockComments.ContainsKey("Assets/Scenes/Main.unity"));
            Assert.AreEqual(SvnOverlayFlags.ChildChanged, restored.Get("Assets/Scripts"));
        }

        [Test]
        public void OverlaySnapshot_EnforcesMaxEntriesCap_PrioritizesDirectEntriesAndMarksIncomplete()
        {
            var snapshot = new SvnOverlaySnapshot();
            for (int i = 0; i < 12; i++)
                snapshot.Add($"Assets/Folder{i}", SvnOverlayFlags.ChildChanged);
            for (int i = 0; i < 10; i++)
                snapshot.Add($"Assets/File{i}.cs", SvnOverlayFlags.Modified);

            var json = VcsProjectWindowIntegration.SerializeOverlaySnapshot(snapshot, maxEntries: 12);
            var restored = VcsProjectWindowIntegration.DeserializeOverlaySnapshot(json);

            Assert.IsNotNull(restored);
            Assert.AreEqual(12, restored.Flags.Count);
            Assert.IsTrue(restored.IsIncomplete);

            // All 10 direct file entries must be kept before ChildChanged-only folder markers.
            for (int i = 0; i < 10; i++)
                Assert.AreEqual(SvnOverlayFlags.Modified, restored.Get($"Assets/File{i}.cs"));
        }

        [Test]
        public void OverlaySnapshot_CorruptedOrEmptyJson_ReturnsNullSafely()
        {
            Assert.IsNull(VcsProjectWindowIntegration.DeserializeOverlaySnapshot(null));
            Assert.IsNull(VcsProjectWindowIntegration.DeserializeOverlaySnapshot(string.Empty));
            Assert.IsNull(VcsProjectWindowIntegration.DeserializeOverlaySnapshot("{not valid json"));
            Assert.IsNull(VcsProjectWindowIntegration.DeserializeOverlaySnapshot("{}"));
            Assert.IsNull(VcsProjectWindowIntegration.DeserializeOverlaySnapshot("{\"SchemaVersion\":0,\"Entries\":[]}"));

            SessionState.SetString(TestOverlaySessionKey, "{corrupted-overlay-payload");
            Assert.IsNull(VcsProjectWindowIntegration.LoadOverlaySnapshotFromSession(TestOverlaySessionKey));
        }

        #endregion

        #region VersionControlPanel Cache

        [Test]
        public void PanelCache_SessionRoundTrip_PreservesStatusFilesCommitsSelectionAndMessages()
        {
            var files = new List<VcsFileStatus>
            {
                new VcsFileStatus
                {
                    FilePath = "Assets/Scripts/Core.cs",
                    State = VcsFileState.Modified,
                    StateDescription = "Modified",
                    StatusCode = "M",
                    LockState = "K",
                    LockOwner = "devA",
                    LockComment = "working",
                    NeedsLock = true
                },
                new VcsFileStatus
                {
                    FilePath = "Assets/Data/NewConfig.json",
                    State = VcsFileState.Untracked,
                    StateDescription = "Untracked"
                },
                new VcsFileStatus
                {
                    FilePath = "Assets/Scenes/Battle.unity",
                    State = VcsFileState.Conflicted,
                    StateDescription = "Conflicted"
                }
            };

            var commits = new List<VcsCommit>
            {
                new VcsCommit
                {
                    Revision = "1042",
                    Author = "devA",
                    Date = "2026-11-24 10:00",
                    Message = "feat: persist panel caches across reload"
                },
                new VcsCommit
                {
                    Revision = "1041",
                    Author = "devB",
                    Date = "2026-11-23 18:30",
                    Message = "fix: resolve svn overlay lock tooltip"
                }
            };

            var selected = new[] { "Assets/Scripts/Core.cs", "Assets/Scenes/Battle.unity" };

            var snapshot = VersionControlPanel.BuildCacheSnapshot(
                VcsType.Svn,
                branch: "trunk",
                revision: "1042",
                files: files,
                statusTruncated: false,
                selectedFiles: selected,
                commits: commits,
                visibleCommitCount: 30,
                commitLogError: "offline timeout",
                lastMessage: "Working copy status failed: server unreachable",
                lastMessageIsError: true,
                commitMessageDraft: "wip: reload cache draft");

            VersionControlPanel.SaveCacheToSession(snapshot, TestPanelCacheSessionKey);
            var restored = VersionControlPanel.LoadCacheFromSession(TestPanelCacheSessionKey);

            Assert.IsNotNull(restored);
            Assert.AreEqual((int)VcsType.Svn, restored.VcsTypeValue);
            Assert.AreEqual("trunk", restored.Branch);
            Assert.AreEqual("1042", restored.Revision);
            Assert.IsFalse(restored.StatusTruncated);
            Assert.AreEqual(30, restored.VisibleCommitCount);
            Assert.AreEqual("offline timeout", restored.CommitLogError);
            Assert.AreEqual("Working copy status failed: server unreachable", restored.LastMessage);
            Assert.IsTrue(restored.LastMessageIsError);
            Assert.AreEqual("wip: reload cache draft", restored.CommitMessageDraft);
            CollectionAssert.AreEqual(selected, restored.SelectedFiles);

            var restoredFiles = VersionControlPanel.ExtractFileStatuses(restored);
            Assert.AreEqual(3, restoredFiles.Count);
            Assert.AreEqual("Assets/Scripts/Core.cs", restoredFiles[0].FilePath);
            Assert.AreEqual(VcsFileState.Modified, restoredFiles[0].State);
            Assert.AreEqual("K", restoredFiles[0].LockState);
            Assert.AreEqual("devA", restoredFiles[0].LockOwner);
            Assert.IsTrue(restoredFiles[0].NeedsLock);
            Assert.AreEqual(VcsFileState.Conflicted, restoredFiles[2].State);

            var restoredCommits = VersionControlPanel.ExtractCommits(restored);
            Assert.AreEqual(2, restoredCommits.Count);
            Assert.AreEqual("1042", restoredCommits[0].Revision);
            Assert.AreEqual("devA", restoredCommits[0].Author);
            Assert.AreEqual("feat: persist panel caches across reload", restoredCommits[0].Message);
        }

        [Test]
        public void PanelCache_EnforcesFileAndCommitCaps_AndTruncatesLongCommitMessage()
        {
            var files = new List<VcsFileStatus>();
            for (int i = 0; i < VersionControlPanel.MaxCachedFiles + 40; i++)
                files.Add(new VcsFileStatus { FilePath = $"Assets/File_{i}.cs", State = VcsFileState.Modified });

            var longMessage = new string('x', VersionControlPanel.MaxCachedCommitMessageLength + 250);
            var commits = new List<VcsCommit>();
            for (int i = 0; i < VersionControlPanel.MaxCachedCommits + 25; i++)
                commits.Add(new VcsCommit { Revision = $"r{i}", Author = "bot", Date = "2026-11-24", Message = longMessage });

            var snapshot = VersionControlPanel.BuildCacheSnapshot(
                VcsType.Git,
                "main",
                "abc1234",
                files,
                statusTruncated: false,
                selectedFiles: null,
                commits: commits,
                visibleCommitCount: 10,
                commitLogError: null,
                lastMessage: null,
                lastMessageIsError: false);

            var json = VersionControlPanel.SerializeCache(snapshot);
            var restored = VersionControlPanel.DeserializeCache(json);

            Assert.IsNotNull(restored);
            Assert.AreEqual(VersionControlPanel.MaxCachedFiles, restored.Files.Count);
            Assert.IsTrue(restored.StatusTruncated);
            Assert.AreEqual(VersionControlPanel.MaxCachedCommits, restored.Commits.Count);
            Assert.AreEqual(VersionControlPanel.MaxCachedCommitMessageLength, restored.Commits[0].Message.Length);
        }

        [Test]
        public void PanelCache_CorruptedOrEmptyJson_ReturnsNullSafely()
        {
            Assert.IsNull(VersionControlPanel.DeserializeCache(null));
            Assert.IsNull(VersionControlPanel.DeserializeCache(string.Empty));
            Assert.IsNull(VersionControlPanel.DeserializeCache("{corrupted"));
            Assert.IsNull(VersionControlPanel.DeserializeCache("{}"));

            SessionState.SetString(TestPanelCacheSessionKey, "<<<not-json>>>");
            Assert.IsNull(VersionControlPanel.LoadCacheFromSession(TestPanelCacheSessionKey));
        }

        #endregion
    }
}
