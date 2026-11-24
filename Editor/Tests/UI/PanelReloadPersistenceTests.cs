using System;
using System.Collections.Generic;
using AgentCore.Editor.Cloud;
using AgentCore.Editor.Components.Indexing.Core;
using AgentCore.Editor.Components.Indexing.Models;
using AgentCore.Editor.Components.Indexing.UI;
using AgentCore.Editor.UI.Components;
using NUnit.Framework;
using UnityEditor;

namespace AgentCore.Tests.Editor.UI
{
    /// <summary>
    /// Unit tests for Domain Reload persistence caches across Indexing, Memory, and KnowledgeBase panels.
    /// All tests use isolated SessionState / EditorPrefs keys cleaned up in <see cref="TearDown"/>.
    /// </summary>
    [TestFixture]
    public class PanelReloadPersistenceTests
    {
        private const string TestIndexingStatusSessionKey = "AgentCore.Tests.Indexing.StatusBus.Snapshot";
        private const string TestIndexingDurableLastSuccessKey = "AgentCore.Tests.Indexing.StatusBus.LastSuccessUtc";
        private const string TestIndexingPanelSessionKey = "AgentCore.Tests.Indexing.Panel.Cache";
        private const string TestIndexingDurableStatsKey = "AgentCore.Tests.Indexing.Panel.LastStats";
        private const string TestMemoryPanelSessionKey = "AgentCore.Tests.Memory.Panel.Cache";
        private const string TestKnowledgeBaseSessionKey = "AgentCore.Tests.KnowledgeBase.Panel.Cache";

        [SetUp]
        public void SetUp()
        {
            CleanupTestKeys();
        }

        [TearDown]
        public void TearDown()
        {
            CleanupTestKeys();
            // Restore live IndexingStatusBus from production keys so tests leave zero in-memory drift.
            IndexingStatusBus.RestoreFromStorage();
        }

        private static void CleanupTestKeys()
        {
            SessionState.EraseString(TestIndexingStatusSessionKey);
            SessionState.EraseString(TestIndexingPanelSessionKey);
            SessionState.EraseString(TestMemoryPanelSessionKey);
            SessionState.EraseString(TestKnowledgeBaseSessionKey);
            EditorPrefs.DeleteKey(TestIndexingDurableLastSuccessKey);
            EditorPrefs.DeleteKey(TestIndexingDurableStatsKey);
        }

        #region IndexingStatusBus & IndexingPanel

        [Test]
        public void IndexingStatusBus_InterruptedRunningState_DowngradesSafely_AndPreservesMetrics()
        {
            var lastSuccessUtc = new DateTime(2026, 11, 24, 2, 30, 0, DateTimeKind.Utc);
            var runningWithDirty = new IndexingStatusSnapshot
            {
                State = IndexingBackgroundState.Running,
                DirtyFileCount = 5,
                ProcessedFiles = 12,
                TotalFiles = 20,
                CurrentFile = "Assets/Scripts/Gameplay.cs",
                LastError = "previous transient timeout",
                LastSuccessAt = lastSuccessUtc,
                ConsecutiveFailures = 2
            };

            IndexingStatusBus.SaveSnapshotToSession(runningWithDirty, TestIndexingStatusSessionKey);
            IndexingStatusBus.ResetInMemoryForTests();
            IndexingStatusBus.RestoreFromStorage(TestIndexingStatusSessionKey, TestIndexingDurableLastSuccessKey);

            var restored = IndexingStatusBus.Current;
            Assert.AreEqual(IndexingBackgroundState.Pending, restored.State);
            Assert.IsNull(restored.CurrentFile);
            Assert.AreEqual(5, restored.DirtyFileCount);
            Assert.AreEqual(12, restored.ProcessedFiles);
            Assert.AreEqual(20, restored.TotalFiles);
            Assert.AreEqual(lastSuccessUtc, restored.LastSuccessAt);
            Assert.AreEqual("previous transient timeout", restored.LastError);
            Assert.AreEqual(2, restored.ConsecutiveFailures);

            // When DirtyFileCount is 0, an interrupted Running state must downgrade to Idle.
            var runningNoDirty = new IndexingStatusSnapshot
            {
                State = IndexingBackgroundState.Running,
                DirtyFileCount = 0,
                ProcessedFiles = 20,
                TotalFiles = 20,
                CurrentFile = "Assets/Scripts/Done.cs",
                LastSuccessAt = lastSuccessUtc
            };
            IndexingStatusBus.SaveSnapshotToSession(runningNoDirty, TestIndexingStatusSessionKey);
            IndexingStatusBus.ResetInMemoryForTests();
            IndexingStatusBus.RestoreFromStorage(TestIndexingStatusSessionKey, TestIndexingDurableLastSuccessKey);

            var restoredIdle = IndexingStatusBus.Current;
            Assert.AreEqual(IndexingBackgroundState.Idle, restoredIdle.State);
            Assert.IsNull(restoredIdle.CurrentFile);
            Assert.AreEqual(lastSuccessUtc, restoredIdle.LastSuccessAt);
        }

        [Test]
        public void IndexingStatusBus_DurableLastSuccess_SurvivesSessionWipe_AndCorruptedJsonFallsBack()
        {
            var durableUtc = new DateTime(2026, 11, 23, 15, 45, 10, DateTimeKind.Utc);
            IndexingStatusBus.SaveDurableLastSuccessUtc(durableUtc, TestIndexingDurableLastSuccessKey);
            SessionState.SetString(TestIndexingStatusSessionKey, "{corrupted-indexing-status");

            IndexingStatusBus.ResetInMemoryForTests();
            IndexingStatusBus.RestoreFromStorage(TestIndexingStatusSessionKey, TestIndexingDurableLastSuccessKey);

            var restored = IndexingStatusBus.Current;
            Assert.AreEqual(IndexingBackgroundState.Idle, restored.State);
            Assert.AreEqual(durableUtc, restored.LastSuccessAt);
            Assert.IsNull(IndexingStatusBus.DeserializeSnapshot("{not-json"));
            Assert.IsNull(IndexingStatusBus.DeserializeSnapshot("{}"));
        }

        [Test]
        public void IndexingPanel_CacheAndDurableStats_RoundTrip_AndCorruptedJsonFallback()
        {
            var fullUtc = new DateTime(2026, 11, 24, 1, 0, 0, DateTimeKind.Utc);
            var incUtc = new DateTime(2026, 11, 24, 2, 15, 0, DateTimeKind.Utc);
            var scopeUtc = new DateTime(2026, 11, 24, 2, 20, 0, DateTimeKind.Utc);

            var stats = new IndexingStats
            {
                StoreBackend = "sqlite",
                TotalFiles = 128,
                TotalSymbols = 2450,
                EnabledRootCount = 3,
                ErrorFileCount = 2,
                LastFullIndexAt = fullUtc,
                LastIncrementalIndexAt = incUtc,
                LastScopeIndexAt = scopeUtc
            };

            var cacheDto = IndexingPanel.BuildCacheDto(
                stats,
                lastManualResult: "Done — 128 files, 2450 symbols (840 ms)",
                lastManualResultIsError: false,
                lastManualResultAtUtc: incUtc);

            IndexingPanel.SaveCacheToSession(cacheDto, TestIndexingPanelSessionKey);
            var restoredCache = IndexingPanel.LoadCacheFromSession(TestIndexingPanelSessionKey);

            Assert.IsNotNull(restoredCache);
            Assert.IsTrue(restoredCache.HasStats);
            Assert.AreEqual("Done — 128 files, 2450 symbols (840 ms)", restoredCache.LastManualResult);
            Assert.IsFalse(restoredCache.LastManualResultIsError);

            var restoredStats = IndexingPanel.ExtractStats(restoredCache);
            Assert.IsNotNull(restoredStats);
            Assert.AreEqual("sqlite", restoredStats.StoreBackend);
            Assert.AreEqual(128, restoredStats.TotalFiles);
            Assert.AreEqual(2450, restoredStats.TotalSymbols);
            Assert.AreEqual(3, restoredStats.EnabledRootCount);
            Assert.AreEqual(2, restoredStats.ErrorFileCount);
            Assert.AreEqual(fullUtc, restoredStats.LastFullIndexAt);
            Assert.AreEqual(incUtc, restoredStats.LastIncrementalIndexAt);
            Assert.AreEqual(scopeUtc, restoredStats.LastScopeIndexAt);

            // Durable EditorPrefs round-trip (survives Editor restart when SessionState is empty).
            IndexingPanel.SaveDurableStats(stats, TestIndexingDurableStatsKey);
            var durableStats = IndexingPanel.LoadDurableStats(TestIndexingDurableStatsKey);
            Assert.IsNotNull(durableStats);
            Assert.AreEqual(128, durableStats.TotalFiles);
            Assert.AreEqual(2450, durableStats.TotalSymbols);
            Assert.AreEqual(fullUtc, durableStats.LastFullIndexAt);

            // Corrupted JSON guard.
            SessionState.SetString(TestIndexingPanelSessionKey, "{{corrupt");
            Assert.IsNull(IndexingPanel.LoadCacheFromSession(TestIndexingPanelSessionKey));
            Assert.IsNull(IndexingPanel.DeserializeCache("{}"));
        }

        #endregion

        #region MemoryPanel Cache

        [Test]
        public void MemoryPanel_Cache_SessionRoundTrip_PreservesConnectionItemsAndFilters()
        {
            var memories = new List<Mem0Memory>
            {
                new Mem0Memory
                {
                    Id = "mem-1",
                    Content = "Use Unity 2022.3.50f1 C# 9.0 syntax.",
                    UserId = "dev-user",
                    CreatedAt = "2026-11-24T01:00:00Z",
                    UpdatedAt = "2026-11-24T01:05:00Z",
                    State = "active"
                },
                new Mem0Memory
                {
                    Id = "mem-2",
                    Content = "Never modify files outside writable scope.",
                    UserId = "dev-user",
                    CreatedAt = "2026-11-24T02:00:00Z",
                    State = "active"
                }
            };

            var dto = MemoryPanel.BuildCacheDto(
                endpoint: "http://localhost:8765",
                userId: "dev-user",
                hasConnectionStatus: true,
                connectionStatusText: "连接状态：已连接，用户存在",
                connectionState: MemoryPanel.ConnectionStateOk,
                showCreateUserButton: false,
                hasLoadedMemories: true,
                memories: memories,
                searchQuery: "Unity",
                selectedMemoryId: "mem-1",
                addMemoryDraft: "Draft memory text");

            MemoryPanel.SaveCacheToSession(dto, TestMemoryPanelSessionKey);
            var restored = MemoryPanel.LoadCacheFromSession(TestMemoryPanelSessionKey);

            Assert.IsNotNull(restored);
            Assert.AreEqual("http://localhost:8765", restored.Endpoint);
            Assert.AreEqual("dev-user", restored.UserId);
            Assert.IsTrue(restored.HasConnectionStatus);
            Assert.AreEqual("连接状态：已连接，用户存在", restored.ConnectionStatusText);
            Assert.AreEqual(MemoryPanel.ConnectionStateOk, restored.ConnectionState);
            Assert.IsFalse(restored.ShowCreateUserButton);
            Assert.IsTrue(restored.HasLoadedMemories);
            Assert.AreEqual("Unity", restored.SearchQuery);
            Assert.AreEqual("mem-1", restored.SelectedMemoryId);
            Assert.AreEqual("Draft memory text", restored.AddMemoryDraft);

            var restoredMemories = MemoryPanel.ToMem0Memories(restored);
            Assert.AreEqual(2, restoredMemories.Count);
            Assert.AreEqual("mem-1", restoredMemories[0].Id);
            Assert.AreEqual("Use Unity 2022.3.50f1 C# 9.0 syntax.", restoredMemories[0].Content);
            Assert.AreEqual("active", restoredMemories[1].State);
        }

        [Test]
        public void MemoryPanel_Cache_EnforcesItemAndContentCaps_AndCorruptedJsonFallback()
        {
            var hugeContent = new string('a', MemoryPanel.MaxCachedContentLength + 300);
            var memories = new List<Mem0Memory>();
            for (int i = 0; i < MemoryPanel.MaxCachedMemories + 35; i++)
            {
                memories.Add(new Mem0Memory { Id = $"m-{i}", Content = hugeContent });
            }

            var dto = MemoryPanel.BuildCacheDto(
                endpoint: "http://localhost:8765",
                userId: "u1",
                hasConnectionStatus: true,
                connectionStatusText: "Connected",
                connectionState: MemoryPanel.ConnectionStateOk,
                showCreateUserButton: false,
                hasLoadedMemories: true,
                memories: memories);

            var json = MemoryPanel.SerializeCache(dto);
            var restored = MemoryPanel.DeserializeCache(json);

            Assert.IsNotNull(restored);
            Assert.AreEqual(MemoryPanel.MaxCachedMemories, restored.Memories.Count);
            Assert.AreEqual(MemoryPanel.MaxCachedContentLength, restored.Memories[0].Content.Length);

            SessionState.SetString(TestMemoryPanelSessionKey, "{bad-memory-json");
            Assert.IsNull(MemoryPanel.LoadCacheFromSession(TestMemoryPanelSessionKey));
            Assert.IsNull(MemoryPanel.DeserializeCache("{}"));
            Assert.IsNull(MemoryPanel.DeserializeCache(string.Empty));
        }

        #endregion

        #region KnowledgeBasePanel Cache

        [Test]
        public void KnowledgeBasePanel_Cache_SessionRoundTrip_PreservesConnectionDocsAndLastResult()
        {
            var docs = new List<LightRAGDocument>
            {
                new LightRAGDocument
                {
                    Id = "doc-1",
                    FilePath = "Docs/Architecture.md",
                    ContentSummary = "Overview of AgentCore Unity architecture.",
                    ContentLength = 4096,
                    Status = "processed",
                    ChunksCount = 12
                },
                new LightRAGDocument
                {
                    Id = "doc-2",
                    FilePath = "Docs/Broken.md",
                    Status = "failed",
                    ErrorMsg = "Unsupported encoding"
                }
            };

            var dto = KnowledgeBasePanel.BuildCacheDto(
                endpoint: "http://localhost:9621",
                connectionStatus: KnowledgeBasePanel.ConnectionStatus.Connected,
                indexStatus: KnowledgeBasePanel.IndexStatus.Success,
                lastIndexedFile: "Docs/Architecture.md",
                lastIndexSummary: "索引完成：Architecture.md",
                hasLoadedDocuments: true,
                documents: docs,
                searchQuery: "Architecture",
                selectedDocumentId: "doc-1");

            KnowledgeBasePanel.SaveCacheToSession(dto, TestKnowledgeBaseSessionKey);
            var restored = KnowledgeBasePanel.LoadCacheFromSession(TestKnowledgeBaseSessionKey);

            Assert.IsNotNull(restored);
            Assert.AreEqual("http://localhost:9621", restored.Endpoint);
            Assert.AreEqual((int)KnowledgeBasePanel.ConnectionStatus.Connected, restored.ConnectionStatusValue);
            Assert.AreEqual((int)KnowledgeBasePanel.IndexStatus.Success, restored.IndexStatusValue);
            Assert.AreEqual("Docs/Architecture.md", restored.LastIndexedFile);
            Assert.AreEqual("索引完成：Architecture.md", restored.LastIndexSummary);
            Assert.IsTrue(restored.HasLoadedDocuments);
            Assert.AreEqual("Architecture", restored.SearchQuery);
            Assert.AreEqual("doc-1", restored.SelectedDocumentId);

            var restoredDocs = KnowledgeBasePanel.ToDocuments(restored);
            Assert.AreEqual(2, restoredDocs.Count);
            Assert.AreEqual("doc-1", restoredDocs[0].Id);
            Assert.AreEqual(12, restoredDocs[0].ChunksCount);
            Assert.AreEqual("Unsupported encoding", restoredDocs[1].ErrorMsg);
        }

        [Test]
        public void KnowledgeBasePanel_Cache_SanitizesTransientStates_EnforcesCap_AndHandlesCorruption()
        {
            var longSummary = new string('s', KnowledgeBasePanel.MaxCachedSummaryLength + 200);
            var docs = new List<LightRAGDocument>();
            for (int i = 0; i < KnowledgeBasePanel.MaxCachedDocuments + 25; i++)
            {
                docs.Add(new LightRAGDocument
                {
                    Id = $"d-{i}",
                    FilePath = $"Docs/File_{i}.md",
                    ContentSummary = longSummary,
                    Status = "processed"
                });
            }

            var dto = KnowledgeBasePanel.BuildCacheDto(
                endpoint: "http://localhost:9621",
                connectionStatus: KnowledgeBasePanel.ConnectionStatus.Testing,
                indexStatus: KnowledgeBasePanel.IndexStatus.Processing,
                lastIndexedFile: "Docs/File_0.md",
                lastIndexSummary: "Processing...",
                hasLoadedDocuments: true,
                documents: docs);

            var json = KnowledgeBasePanel.SerializeCache(dto);
            var restored = KnowledgeBasePanel.DeserializeCache(json);

            Assert.IsNotNull(restored);
            Assert.AreEqual((int)KnowledgeBasePanel.ConnectionStatus.Unknown, restored.ConnectionStatusValue);
            Assert.AreEqual((int)KnowledgeBasePanel.IndexStatus.Idle, restored.IndexStatusValue);
            Assert.AreEqual(KnowledgeBasePanel.MaxCachedDocuments, restored.Documents.Count);
            Assert.AreEqual(KnowledgeBasePanel.MaxCachedSummaryLength, restored.Documents[0].ContentSummary.Length);

            SessionState.SetString(TestKnowledgeBaseSessionKey, "not-valid-json");
            Assert.IsNull(KnowledgeBasePanel.LoadCacheFromSession(TestKnowledgeBaseSessionKey));
            Assert.IsNull(KnowledgeBasePanel.DeserializeCache("{}"));
            Assert.IsNull(KnowledgeBasePanel.DeserializeCache(null));
        }

        #endregion
    }
}
