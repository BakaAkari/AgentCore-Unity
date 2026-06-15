using System;
using System.Threading;
using AgentCore.Editor.Components.Indexing.Core;
using AgentCore.Editor.Components.Indexing.Models;
using AgentCore.Editor.Config;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.Components.Indexing.UI
{
    /// <summary>
    /// Code Indexing Hub panel.
    /// Displays index status, workspace info, LLM reference info, and provides
    /// Full Index / Incremental / Clear Index operations.
    /// Mounted into the AgentCore Hub via <see cref="IndexingPanelContribution"/>.
    /// </summary>
    public sealed class IndexingPanel : VisualElement
    {
        // ── USS class names ──────────────────────────────────────────────────────
        private const string RootClass        = "indexing-panel";
        private const string SectionClass     = "indexing-section";
        private const string SectionHeaderClass = "indexing-section-header";
        private const string SectionContentClass = "indexing-section-content";
        private const string SectionHeaderRowClass = "indexing-section-header-row";
        private const string LabelRowClass    = "indexing-label-row";
        private const string LabelKeyClass    = "indexing-label-key";
        private const string LabelValueClass  = "indexing-label-value";
        private const string ButtonClass      = "indexing-button";
        private const string ButtonRowClass   = "indexing-button-row";
        private const string StatusLabelClass = "indexing-status-label";
        private const string StatusOkClass    = "indexing-status-ok";
        private const string StatusErrorClass = "indexing-status-error";
        private const string HelpTextClass    = "indexing-help-text";

        // ── State ────────────────────────────────────────────────────────────────
        private bool _isIndexing;
        private string _lastIndexResult;
        private IndexingStats _cachedStats;
        private bool _statsDirty = true;
        private bool _isPanelActive;

        // ── UI references ────────────────────────────────────────────────────────
        private Label _statusLabel;
        private VisualElement _statsContent;
        private Button _fullIndexButton;
        private Button _incrementalButton;
        private Button _clearButton;
        private Button _refreshStatsButton;

        // ── Constructor ──────────────────────────────────────────────────────────

        /// <summary>
        /// Creates the Code Indexing panel and builds its UI.
        /// </summary>
        public IndexingPanel()
        {
            AddToClassList(RootClass);
            BuildUI();
        }

        // ── Public lifecycle (called by contribution) ────────────────────────────

        /// <summary>Called by <see cref="IndexingPanelContribution.OnActivated"/> when the panel gains focus.</summary>
        public void OnActivated()
        {
            _isPanelActive = true;
            _statsDirty = true;
            RefreshStats();
        }

        /// <summary>Called by <see cref="IndexingPanelContribution.OnDeactivated"/> when the panel loses focus.</summary>
        public void OnDeactivated()
        {
            _isPanelActive = false;
        }

        // ── UI construction ──────────────────────────────────────────────────────

        private void BuildUI()
        {
            var mainScroll = new ScrollView(ScrollViewMode.Vertical);
            mainScroll.AddToClassList("indexing-main-scroll-view");
            mainScroll.style.flexGrow = 1;
            mainScroll.style.flexShrink = 1;
            Add(mainScroll);

            // ── Header ──
            var header = new VisualElement();
            header.AddToClassList("panel-header");

            var title = new Label("Code Indexing");
            title.AddToClassList("panel-title");
            header.Add(title);

            var headerActions = new VisualElement();
            headerActions.AddToClassList("indexing-header-actions");

            _refreshStatsButton = new Button(OnRefreshStatsClicked) { text = "Refresh" };
            _refreshStatsButton.AddToClassList(ButtonClass);
            _refreshStatsButton.tooltip = "Reload index statistics from disk.";
            headerActions.Add(_refreshStatsButton);

            header.Add(headerActions);
            mainScroll.Add(header);

            // ── Status message ──
            _statusLabel = new Label();
            _statusLabel.AddToClassList(StatusLabelClass);
            _statusLabel.style.display = DisplayStyle.None;
            mainScroll.Add(_statusLabel);

            // ── LLM Reference ──
            mainScroll.Add(BuildLlmReferenceSection());

            // ── Workspace ──
            mainScroll.Add(BuildWorkspaceSection());

            // ── Index Statistics ──
            mainScroll.Add(BuildIndexStatsSection());

            // ── Index Actions ──
            mainScroll.Add(BuildIndexActionsSection());
        }

        // ── Section builders ─────────────────────────────────────────────────────

        private VisualElement BuildLlmReferenceSection()
        {
            var section = CreateSection("LLM CONFIGURATION");
            var content = section.Q<VisualElement>(className: SectionContentClass);

            var settings = AgentCoreSettings.instance;

            var endpointConfigured = !string.IsNullOrWhiteSpace(settings.llmEndpoint);
            var modelConfigured    = !string.IsNullOrWhiteSpace(settings.llmModel);

            content.Add(CreateLabelRow("Endpoint",    endpointConfigured ? settings.llmEndpoint : "(not configured)"));
            content.Add(CreateLabelRow("Model",       modelConfigured    ? settings.llmModel    : "(not configured)"));
            content.Add(CreateLabelRow("Temperature", settings.temperature.ToString("F2")));
            content.Add(CreateLabelRow("Max Tokens",  settings.maxTokens.ToString()));

            var helpText = new Label(endpointConfigured && modelConfigured
                ? "LLM is configured. The indexer itself does not call the LLM."
                : "LLM endpoint or model is missing — configure them in Project Settings > AgentCore > Model & Agent.");
            helpText.AddToClassList(HelpTextClass);
            if (!endpointConfigured || !modelConfigured)
                helpText.AddToClassList(StatusErrorClass);
            content.Add(helpText);

            return section;
        }

        private VisualElement BuildWorkspaceSection()
        {
            var section = CreateSection("WORKSPACE");
            var content = section.Q<VisualElement>(className: SectionContentClass);

            try
            {
                var workspace = IndexWorkspaceResolver.ResolveFromCurrent();
                if (workspace == null)
                {
                    var warn = new Label("No workspace detected. Configure a workspace root in Project Settings > AgentCore > Workspace.");
                    warn.AddToClassList(HelpTextClass);
                    warn.AddToClassList(StatusErrorClass);
                    content.Add(warn);
                }
                else
                {
                    content.Add(CreateLabelRow("Workspace Root", workspace.WorkspaceRoot));
                    content.Add(CreateLabelRow("Unity Root",     workspace.UnityRoot));
                    content.Add(CreateLabelRow("Hash",           workspace.Fingerprint));

#if AGENTCORE_SQLITE
                    var dbPath      = IndexStoreFactory.GetDbPath(workspace.WorkspaceRoot);
                    var backendLabel = System.IO.File.Exists(dbPath) ? "SQLite" : "SQLite (not yet created)";
#else
                    var backendLabel = "JSONL";
#endif
                    content.Add(CreateLabelRow("Index Backend", backendLabel));
                }
            }
            catch (Exception ex)
            {
                var err = new Label($"Failed to resolve workspace: {ex.Message}");
                err.AddToClassList(HelpTextClass);
                err.AddToClassList(StatusErrorClass);
                content.Add(err);
            }

            return section;
        }

        private VisualElement BuildIndexStatsSection()
        {
            var section = CreateSection("INDEX STATISTICS");
            var content = section.Q<VisualElement>(className: SectionContentClass);

            _statsContent = new VisualElement();
            _statsContent.AddToClassList("indexing-stats-content");
            content.Add(_statsContent);

            // Populate will be called by RefreshStats
            RenderStats();

            return section;
        }

        private VisualElement BuildIndexActionsSection()
        {
            var section = CreateSection("INDEX ACTIONS");
            var content = section.Q<VisualElement>(className: SectionContentClass);

            var buttonRow = new VisualElement();
            buttonRow.AddToClassList(ButtonRowClass);

            _fullIndexButton = new Button(OnFullIndexClicked) { text = "Full Index" };
            _fullIndexButton.AddToClassList(ButtonClass);
            _fullIndexButton.tooltip = "Scan all roots and rebuild the entire symbol database.";
            buttonRow.Add(_fullIndexButton);

            _incrementalButton = new Button(OnIncrementalClicked) { text = "Incremental" };
            _incrementalButton.AddToClassList(ButtonClass);
            _incrementalButton.tooltip = "Re-index only files changed since the last run.";
            buttonRow.Add(_incrementalButton);

            _clearButton = new Button(OnClearIndexClicked) { text = "Clear Index" };
            _clearButton.AddToClassList(ButtonClass);
            _clearButton.AddToClassList("indexing-danger-button");
            _clearButton.tooltip = "Remove all stored index data for the current workspace.";
            buttonRow.Add(_clearButton);

            content.Add(buttonRow);

            var help = new Label(
                "Full Index — scans all roots and rebuilds the entire symbol database.\n" +
                "Incremental — only re-indexes files changed since the last run.\n" +
                "Clear Index — removes all stored index data for the current workspace.");
            help.AddToClassList(HelpTextClass);
            content.Add(help);

            return section;
        }

        // ── Section helper ────────────────────────────────────────────────────────

        private VisualElement CreateSection(string title)
        {
            var section = new VisualElement();
            section.AddToClassList(SectionClass);

            var header = new Label(title);
            header.AddToClassList(SectionHeaderClass);
            section.Add(header);

            var content = new VisualElement();
            content.AddToClassList(SectionContentClass);
            section.Add(content);

            return section;
        }

        private VisualElement CreateLabelRow(string key, string value)
        {
            var row = new VisualElement();
            row.AddToClassList(LabelRowClass);

            var keyLabel = new Label(key);
            keyLabel.AddToClassList(LabelKeyClass);
            row.Add(keyLabel);

            var valueLabel = new Label(value ?? "—");
            valueLabel.AddToClassList(LabelValueClass);
            valueLabel.tooltip = value ?? string.Empty;
            row.Add(valueLabel);

            return row;
        }

        // ── Stats rendering ──────────────────────────────────────────────────────

        private void RenderStats()
        {
            if (_statsContent == null)
                return;

            _statsContent.Clear();

            if (_cachedStats == null)
            {
                var empty = new Label("No index found. Run a Full Index to get started.");
                empty.AddToClassList(HelpTextClass);
                _statsContent.Add(empty);
                return;
            }

            _statsContent.Add(CreateLabelRow("Backend",      _cachedStats.StoreBackend ?? "unknown"));
            _statsContent.Add(CreateLabelRow("Total Files",  _cachedStats.TotalFiles.ToString()));
            _statsContent.Add(CreateLabelRow("Total Symbols",_cachedStats.TotalSymbols.ToString()));
            _statsContent.Add(CreateLabelRow("Enabled Roots",_cachedStats.EnabledRootCount.ToString()));

            if (_cachedStats.ErrorFileCount > 0)
                _statsContent.Add(CreateLabelRow("Parse Errors", _cachedStats.ErrorFileCount.ToString()));

            if (_cachedStats.LastFullIndexAt.HasValue)
                _statsContent.Add(CreateLabelRow(
                    "Last Full Index",
                    _cachedStats.LastFullIndexAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")));

            if (_cachedStats.LastIncrementalIndexAt.HasValue)
                _statsContent.Add(CreateLabelRow(
                    "Last Incremental",
                    _cachedStats.LastIncrementalIndexAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")));
        }

        // ── Button handlers ──────────────────────────────────────────────────────

        private void OnRefreshStatsClicked()
        {
            _statsDirty = true;
            RefreshStats();
        }

        private void OnFullIndexClicked()        => RunIndexAsync(incremental: false);
        private void OnIncrementalClicked()      => RunIndexAsync(incremental: true);

        private void OnClearIndexClicked()
        {
            var confirmed = EditorUtility.DisplayDialog(
                "Clear Code Index",
                "This will delete all indexed symbol data for the current workspace. Are you sure?",
                "Clear", "Cancel");
            if (confirmed)
                ClearIndexAsync();
        }

        // ── Async operations ──────────────────────────────────────────────────────

        private void SetButtonsEnabled(bool enabled)
        {
            _fullIndexButton?.SetEnabled(enabled);
            _incrementalButton?.SetEnabled(enabled);
            _clearButton?.SetEnabled(enabled);
            _refreshStatsButton?.SetEnabled(enabled);
        }

        private void ShowStatus(string message, bool isError)
        {
            if (_statusLabel == null)
                return;

            _statusLabel.text = message;
            _statusLabel.style.display = DisplayStyle.Flex;
            _statusLabel.RemoveFromClassList(StatusOkClass);
            _statusLabel.RemoveFromClassList(StatusErrorClass);
            _statusLabel.AddToClassList(isError ? StatusErrorClass : StatusOkClass);

            if (!isError)
            {
                schedule.Execute(() =>
                {
                    if (_statusLabel != null)
                        _statusLabel.style.display = DisplayStyle.None;
                }).ExecuteLater(6000);
            }
        }

        private void RunIndexAsync(bool incremental)
        {
            if (_isIndexing)
                return;

            _isIndexing = true;
            SetButtonsEnabled(false);

            if (_fullIndexButton != null)
                _fullIndexButton.text = incremental ? "Full Index" : "Indexing…";
            if (_incrementalButton != null)
                _incrementalButton.text = incremental ? "Indexing…" : "Incremental";

            var cts = new CancellationTokenSource();
            _ = RunIndexInternalAsync(incremental, cts.Token);
        }

        private async System.Threading.Tasks.Task RunIndexInternalAsync(bool incremental, CancellationToken ct)
        {
            try
            {
                var workspace = IndexWorkspaceResolver.ResolveFromCurrent();
                if (workspace == null)
                {
                    ShowStatus("Error: No workspace detected.", true);
                    return;
                }

                using var store = IndexStoreFactory.Create(workspace.WorkspaceRoot);
                if (store == null)
                {
                    ShowStatus("Error: Failed to create index store.", true);
                    return;
                }

                var indexer = new CodebaseIndexer(store);
                IndexingProgress result;

                if (incremental)
                    result = await indexer.RunIncrementalIndexAsync(null, ct);
                else
                    result = await indexer.RunFullIndexAsync(null, ct);

                if (result.IsCompleted && result.IsSuccess)
                {
                    var ms = result.ElapsedSeconds * 1000.0;
                    ShowStatus($"Done — {result.ProcessedFiles} files, {result.ExtractedSymbols} symbols ({ms:F0} ms)", false);
                }
                else if (result.IsCompleted && !result.IsSuccess)
                {
                    ShowStatus($"Error: {result.ErrorMessage}", true);
                }
                else
                {
                    ShowStatus("Indexing cancelled.", false);
                }

                _statsDirty = true;
                RefreshStats();
            }
            catch (OperationCanceledException)
            {
                ShowStatus("Indexing cancelled.", false);
            }
            catch (Exception ex)
            {
                ShowStatus($"Error: {ex.Message}", true);
            }
            finally
            {
                _isIndexing = false;
                SetButtonsEnabled(true);
                if (_fullIndexButton != null) _fullIndexButton.text = "Full Index";
                if (_incrementalButton != null) _incrementalButton.text = "Incremental";
            }
        }

        private async void ClearIndexAsync()
        {
            SetButtonsEnabled(false);
            try
            {
                var workspace = IndexWorkspaceResolver.ResolveFromCurrent();
                if (workspace == null)
                {
                    ShowStatus("Error: No workspace detected.", true);
                    return;
                }

                using var store = IndexStoreFactory.Create(workspace.WorkspaceRoot);
                if (store == null)
                {
                    ShowStatus("Error: Failed to create index store.", true);
                    return;
                }

                var workspaceId = await store.UpsertWorkspaceAsync(workspace, CancellationToken.None);
                await store.ClearWorkspaceIndexAsync(workspaceId, CancellationToken.None);

                ShowStatus("Index cleared.", false);
                _cachedStats = null;
                _statsDirty  = false;
                RenderStats();
            }
            catch (Exception ex)
            {
                ShowStatus($"Error clearing index: {ex.Message}", true);
            }
            finally
            {
                SetButtonsEnabled(true);
            }
        }

        private async void RefreshStats()
        {
            if (!_statsDirty)
                return;

            _statsDirty = false;

            try
            {
                var workspace = IndexWorkspaceResolver.ResolveFromCurrent();
                if (workspace == null)
                {
                    _cachedStats = null;
                    RenderStats();
                    return;
                }

                using var store = IndexStoreFactory.Create(workspace.WorkspaceRoot);
                if (store == null)
                {
                    _cachedStats = null;
                    RenderStats();
                    return;
                }

                var ws = await store.GetWorkspaceByFingerprintAsync(workspace.Fingerprint, CancellationToken.None);
                if (ws == null)
                {
                    _cachedStats = null;
                    RenderStats();
                    return;
                }

                var stats = await store.GetStatsAsync(ws.Id, CancellationToken.None);
                _cachedStats = (stats != null && stats.TotalFiles > 0) ? stats : null;
                RenderStats();
            }
            catch
            {
                _cachedStats = null;
                RenderStats();
            }
        }
    }
}
