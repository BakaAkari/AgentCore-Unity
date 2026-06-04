using System;
using System.Threading;
using AgentCore.Editor.Components.Indexing.Core;
using AgentCore.Editor.Components.Indexing.Models;
using AgentCore.Editor.Extensions;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Components.Indexing.UI
{
    /// <summary>
    /// Provides Project Settings UI for the optional Code Indexing component.
    /// Mounted in the Extensions settings section via <see cref="IAgentCoreSettingsContribution"/>.
    /// </summary>
    public sealed class IndexingSettingsContribution : IAgentCoreSettingsContribution
    {
        // ── State ────────────────────────────────────────────────────────────────
        private bool _isIndexing;
        private string _lastIndexResult;
        private IndexingStats _cachedStats;
        private bool _statsDirty = true;

        // ── IAgentCoreSettingsContribution ───────────────────────────────────────

        /// <inheritdoc />
        public string Id => "indexing-settings";

        /// <inheritdoc />
        public string Title => "Code Indexing";

        /// <inheritdoc />
        public string Description => "Configuration and status for the C# symbol index used by the search_code tool.";

        /// <inheritdoc />
        public int Order => 400;

        /// <inheritdoc />
        public void DrawGUI()
        {
            DrawWorkspaceInfo();
            EditorGUILayout.Space(6f);
            DrawIndexStats();
            EditorGUILayout.Space(6f);
            DrawIndexActions();
        }

        // ── Private draw helpers ─────────────────────────────────────────────────

        private static void DrawWorkspaceInfo()
        {
            EditorGUILayout.LabelField("Workspace", EditorStyles.boldLabel);

            try
            {
                var workspace = IndexWorkspaceResolver.Resolve();
                if (workspace == null)
                {
                    EditorGUILayout.HelpBox("No workspace detected. Configure a workspace root first.", MessageType.Warning);
                    return;
                }

                EditorGUILayout.LabelField("Workspace Root", workspace.WorkspaceRoot, EditorStyles.miniLabel);
                EditorGUILayout.LabelField("Unity Root", workspace.UnityRoot, EditorStyles.miniLabel);
                EditorGUILayout.LabelField("Workspace Hash", workspace.WorkspaceHash, EditorStyles.miniLabel);
            }
            catch (Exception ex)
            {
                EditorGUILayout.HelpBox($"Failed to resolve workspace: {ex.Message}", MessageType.Error);
            }
        }

        private void DrawIndexStats()
        {
            EditorGUILayout.LabelField("Index Statistics", EditorStyles.boldLabel);

            if (_statsDirty)
            {
                RefreshStats();
            }

            if (_cachedStats == null)
            {
                EditorGUILayout.LabelField("No index found. Run a full index to get started.", EditorStyles.miniLabel);
                return;
            }

            EditorGUILayout.LabelField($"Total Files:   {_cachedStats.TotalFiles}", EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"Total Symbols: {_cachedStats.TotalSymbols}", EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"Total Roots:   {_cachedStats.TotalRoots}", EditorStyles.miniLabel);

            if (_cachedStats.LastFullIndexAt.HasValue)
                EditorGUILayout.LabelField($"Last Full Index:        {_cachedStats.LastFullIndexAt.Value:yyyy-MM-dd HH:mm:ss}", EditorStyles.miniLabel);
            if (_cachedStats.LastIncrementalIndexAt.HasValue)
                EditorGUILayout.LabelField($"Last Incremental Index: {_cachedStats.LastIncrementalIndexAt.Value:yyyy-MM-dd HH:mm:ss}", EditorStyles.miniLabel);

            if (GUILayout.Button("Refresh Stats", GUILayout.Width(120)))
            {
                _statsDirty = true;
            }
        }

        private void DrawIndexActions()
        {
            EditorGUILayout.LabelField("Index Actions", EditorStyles.boldLabel);

            if (!string.IsNullOrEmpty(_lastIndexResult))
            {
                var msgType = _lastIndexResult.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
                    ? MessageType.Error
                    : MessageType.Info;
                EditorGUILayout.HelpBox(_lastIndexResult, msgType);
            }

            using (new EditorGUI.DisabledScope(_isIndexing))
            {
                EditorGUILayout.BeginHorizontal();

                if (GUILayout.Button(_isIndexing ? "Indexing…" : "Full Index", GUILayout.Width(120)))
                {
                    RunIndexAsync(incremental: false);
                }

                if (GUILayout.Button("Incremental", GUILayout.Width(120)))
                {
                    RunIndexAsync(incremental: true);
                }

                if (GUILayout.Button("Clear Index", GUILayout.Width(100)))
                {
                    if (EditorUtility.DisplayDialog(
                            "Clear Code Index",
                            "This will delete all indexed symbol data for the current workspace. Are you sure?",
                            "Clear", "Cancel"))
                    {
                        ClearIndexAsync();
                    }
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.HelpBox(
                "Full Index: scans all roots and rebuilds the entire symbol database.\n" +
                "Incremental: only re-indexes files changed since the last run.\n" +
                "Clear Index: removes all stored index data for the current workspace.",
                MessageType.None);
        }

        // ── Async operations ─────────────────────────────────────────────────────

        private void RunIndexAsync(bool incremental)
        {
            if (_isIndexing)
                return;

            _isIndexing = true;
            _lastIndexResult = null;

            var cts = new CancellationTokenSource();

            _ = RunIndexInternalAsync(incremental, cts.Token);
        }

        private async System.Threading.Tasks.Task RunIndexInternalAsync(bool incremental, CancellationToken ct)
        {
            try
            {
                var workspace = IndexWorkspaceResolver.Resolve();
                if (workspace == null)
                {
                    _lastIndexResult = "Error: No workspace detected.";
                    return;
                }

                using var store = new JsonlIndexStore(workspace);
                await store.InitializeAsync(ct);

                var indexer = new CodebaseIndexer(store);

                IndexingProgress result;
                if (incremental)
                    result = await indexer.RunIncrementalIndexAsync(null, ct);
                else
                    result = await indexer.RunFullIndexAsync(null, ct);

                if (result.IsCompleted)
                    _lastIndexResult = $"Done — {result.IndexedFiles} files, {result.IndexedSymbols} symbols ({result.ElapsedMs:F0} ms)";
                else if (result.IsFailed)
                    _lastIndexResult = $"Error: {result.ErrorMessage}";
                else
                    _lastIndexResult = "Indexing cancelled.";

                _statsDirty = true;
            }
            catch (OperationCanceledException)
            {
                _lastIndexResult = "Indexing cancelled.";
            }
            catch (Exception ex)
            {
                _lastIndexResult = $"Error: {ex.Message}";
            }
            finally
            {
                _isIndexing = false;
            }
        }

        private async void ClearIndexAsync()
        {
            try
            {
                var workspace = IndexWorkspaceResolver.Resolve();
                if (workspace == null)
                {
                    _lastIndexResult = "Error: No workspace detected.";
                    return;
                }

                using var store = new JsonlIndexStore(workspace);
                await store.InitializeAsync(CancellationToken.None);
                await store.ClearAsync(CancellationToken.None);

                _lastIndexResult = "Index cleared.";
                _cachedStats = null;
                _statsDirty = false;
            }
            catch (Exception ex)
            {
                _lastIndexResult = $"Error clearing index: {ex.Message}";
            }
        }

        private async void RefreshStats()
        {
            _statsDirty = false;

            try
            {
                var workspace = IndexWorkspaceResolver.Resolve();
                if (workspace == null)
                {
                    _cachedStats = null;
                    return;
                }

                using var store = new JsonlIndexStore(workspace);
                await store.InitializeAsync(CancellationToken.None);

                var stats = await store.GetStatsAsync(CancellationToken.None);
                _cachedStats = stats.TotalFiles > 0 ? stats : null;
            }
            catch
            {
                _cachedStats = null;
            }
        }
    }
}
