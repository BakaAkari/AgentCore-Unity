using System;
using System.Threading;
using AgentCore.Editor.Components.Indexing.Core;
using AgentCore.Editor.Components.Indexing.Models;
using AgentCore.Editor.Config;
using AgentCore.Editor.Config.Settings;
using AgentCore.Editor.Config.Settings.Pages;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Components.Indexing.UI
{
    /// <summary>
    /// Dedicated settings page for the Code Indexing optional component.
    /// Appears as a top-level tab in the AgentCore Project Settings hub when the
    /// Indexing component is enabled (<c>AGENTCORE_INDEXING</c> define is active).
    /// </summary>
    public sealed class IndexingSettingsPage : IAgentCoreSettingsPage
    {
        // ── State ────────────────────────────────────────────────────────────────
        private bool _isIndexing;
        private string _lastIndexResult;
        private IndexingStats _cachedStats;
        private bool _statsDirty = true;

        // ── IAgentCoreSettingsPage ───────────────────────────────────────────────

        /// <inheritdoc />
        public string Id => "code-indexing";

        /// <inheritdoc />
        public string Title => "Code Indexing";

        /// <inheritdoc />
        public string Description => "Manage the C# symbol index used by the search_code tool. " +
                                     "Symbols are extracted via Roslyn — no LLM is required for indexing.";

        /// <inheritdoc />
        public int Order => 700;

        /// <inheritdoc />
        public void OnActivate(AgentCoreSettingsContext context)
        {
            // Trigger a stats refresh whenever the page is opened.
            _statsDirty = true;
        }

        /// <inheritdoc />
        public void OnDeactivate(AgentCoreSettingsContext context) { }

        /// <inheritdoc />
        public void Draw(AgentCoreSettingsContext context)
        {
            DrawLlmConfigCard(context);
            EditorGUILayout.Space(8);
            DrawWorkspaceCard(context);
            EditorGUILayout.Space(8);
            DrawIndexStatsCard(context);
            EditorGUILayout.Space(8);
            DrawIndexActionsCard(context);
        }

        // ── Card: LLM Configuration ──────────────────────────────────────────────

        private static void DrawLlmConfigCard(AgentCoreSettingsContext context)
        {
            context.Ui.DrawCard(
                "LLM Configuration",
                "Current LLM settings used by the AgentCore agent. " +
                "The indexer itself does not call the LLM — this information is shown for reference.",
                () =>
                {
                    var settings = context.Settings;

                    var endpointConfigured = !string.IsNullOrWhiteSpace(settings.llmEndpoint);
                    EditorGUILayout.LabelField("Endpoint", endpointConfigured ? settings.llmEndpoint : "(not configured)");

                    var modelConfigured = !string.IsNullOrWhiteSpace(settings.llmModel);
                    EditorGUILayout.LabelField("Model", modelConfigured ? settings.llmModel : "(not configured)");

                    EditorGUILayout.LabelField("Temperature", settings.temperature.ToString("F2"));
                    EditorGUILayout.LabelField("Max Tokens", settings.maxTokens.ToString());

                    EditorGUILayout.Space(4);

                    var statusText = endpointConfigured && modelConfigured
                        ? "LLM is configured."
                        : "LLM endpoint or model is missing — configure them in the Model & Agent tab.";
                    var statusLevel = endpointConfigured && modelConfigured
                        ? SettingsStatusLevel.Success
                        : SettingsStatusLevel.Warning;
                    context.Ui.DrawStatusLabel(statusText, statusLevel, miniLabel: true);
                });
        }

        // ── Card: Workspace ──────────────────────────────────────────────────────

        private static void DrawWorkspaceCard(AgentCoreSettingsContext context)
        {
            context.Ui.DrawCard(
                "Workspace",
                "The workspace root that will be scanned during indexing.",
                () =>
                {
                    try
                    {
                        var workspace = IndexWorkspaceResolver.Resolve();
                        if (workspace == null)
                        {
                            EditorGUILayout.HelpBox(
                                "No workspace detected. Configure a workspace root in the Workspace tab first.",
                                MessageType.Warning);
                            return;
                        }

                        EditorGUILayout.LabelField("Workspace Root", workspace.WorkspaceRoot);
                        EditorGUILayout.LabelField("Unity Root", workspace.UnityRoot);
                        EditorGUILayout.LabelField("Workspace Hash", workspace.WorkspaceHash);
                    }
                    catch (Exception ex)
                    {
                        EditorGUILayout.HelpBox($"Failed to resolve workspace: {ex.Message}", MessageType.Error);
                    }
                });
        }

        // ── Card: Index Statistics ───────────────────────────────────────────────

        private void DrawIndexStatsCard(AgentCoreSettingsContext context)
        {
            context.Ui.DrawCard(
                "Index Statistics",
                "Current state of the symbol index for the active workspace.",
                () =>
                {
                    if (_statsDirty)
                        RefreshStats();

                    if (_cachedStats == null)
                    {
                        EditorGUILayout.LabelField(
                            "No index found. Run a Full Index to get started.",
                            EditorStyles.miniLabel);
                    }
                    else
                    {
                        EditorGUILayout.LabelField("Total Files",   _cachedStats.TotalFiles.ToString());
                        EditorGUILayout.LabelField("Total Symbols", _cachedStats.TotalSymbols.ToString());
                        EditorGUILayout.LabelField("Total Roots",   _cachedStats.TotalRoots.ToString());

                        if (_cachedStats.LastFullIndexAt.HasValue)
                            EditorGUILayout.LabelField(
                                "Last Full Index",
                                _cachedStats.LastFullIndexAt.Value.ToString("yyyy-MM-dd HH:mm:ss"));

                        if (_cachedStats.LastIncrementalIndexAt.HasValue)
                            EditorGUILayout.LabelField(
                                "Last Incremental",
                                _cachedStats.LastIncrementalIndexAt.Value.ToString("yyyy-MM-dd HH:mm:ss"));
                    }

                    EditorGUILayout.Space(4);

                    if (GUILayout.Button("Refresh Stats", GUILayout.Width(120)))
                        _statsDirty = true;
                });
        }

        // ── Card: Index Actions ──────────────────────────────────────────────────

        private void DrawIndexActionsCard(AgentCoreSettingsContext context)
        {
            context.Ui.DrawCard(
                "Index Actions",
                "Manually trigger indexing operations for the current workspace.",
                () =>
                {
                    if (!string.IsNullOrEmpty(_lastIndexResult))
                    {
                        var msgType = _lastIndexResult.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
                            ? MessageType.Error
                            : MessageType.Info;
                        EditorGUILayout.HelpBox(_lastIndexResult, msgType);
                        EditorGUILayout.Space(4);
                    }

                    using (new EditorGUI.DisabledScope(_isIndexing))
                    {
                        EditorGUILayout.BeginHorizontal();

                        if (GUILayout.Button(_isIndexing ? "Indexing…" : "Full Index", GUILayout.Width(120)))
                            RunIndexAsync(incremental: false);

                        if (GUILayout.Button("Incremental", GUILayout.Width(120)))
                            RunIndexAsync(incremental: true);

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

                    EditorGUILayout.Space(4);
                    EditorGUILayout.HelpBox(
                        "Full Index — scans all roots and rebuilds the entire symbol database.\n" +
                        "Incremental — only re-indexes files changed since the last run.\n" +
                        "Clear Index — removes all stored index data for the current workspace.",
                        MessageType.None);
                });
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
