using System;
using System.IO;
using AgentCore.Editor.Workspace;
using AgentCore.Editor.Workspace.Config;
using AgentCore.Editor.Workspace.Resolution;
using AgentCore.Editor.Workspace.Safety;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Config.Settings.Pages
{
    /// <summary>
    /// Settings page for Workspace detection, scope roots, and path policy configuration.
    /// </summary>
    public sealed class WorkspaceSettingsPage : IAgentCoreSettingsPage
    {
        // ── Cached state (refreshed on OnActivate) ──────────────────────────
        private WorkspaceContext _cachedContext;
        private bool _isRefreshing;
        private string _refreshError;

        // ── Foldout state ────────────────────────────────────────────────────
        private bool _showScopeRoots = true;
        private bool _showManualOverrides;
        private bool _showSafetyNotes;

        // ─────────────────────────────────────────────────────────────────────
        // IAgentCoreSettingsPage
        // ─────────────────────────────────────────────────────────────────────

        /// <inheritdoc />
        public string Id => "workspace";

        /// <inheritdoc />
        public string Title => "Workspace";

        /// <inheritdoc />
        public string Description => "Workspace root detection, scope roots, and path safety policy.";

        /// <inheritdoc />
        public int Order => 500;

        /// <inheritdoc />
        public void OnActivate(AgentCoreSettingsContext context)
        {
            _cachedContext = WorkspaceContextService.GetCurrent();
            _refreshError = null;
        }

        /// <inheritdoc />
        public void OnDeactivate(AgentCoreSettingsContext context) { }

        /// <inheritdoc />
        public void Draw(AgentCoreSettingsContext context)
        {
            DrawOverviewCard(context);
            EditorGUILayout.Space(8);
            DrawDetectionActionsCard(context);
            EditorGUILayout.Space(8);
            DrawScopeRootsCard(context);
            EditorGUILayout.Space(8);
            DrawManualOverridesCard(context);
            EditorGUILayout.Space(8);
            DrawSafetyNotesCard(context);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Card: Workspace Overview
        // ─────────────────────────────────────────────────────────────────────

        private void DrawOverviewCard(AgentCoreSettingsContext context)
        {
            context.Ui.DrawCard("Workspace Overview", "Current workspace resolution result.", () =>
            {
                var ctx = _cachedContext;

                if (ctx == null)
                {
                    context.Ui.DrawStatusLabel("Not resolved yet.", SettingsStatusLevel.Warning, miniLabel: true);
                    return;
                }

                // Status
                var statusLevel = ctx.IsValid ? SettingsStatusLevel.Success : SettingsStatusLevel.Error;
                context.Ui.DrawStatusLabel($"Status: {ctx.Status}", statusLevel, miniLabel: true);

                if (!string.IsNullOrEmpty(ctx.ErrorMessage))
                {
                    context.Ui.DrawStatusLabel($"Error: {ctx.ErrorMessage}", SettingsStatusLevel.Error, miniLabel: true);
                }

                EditorGUILayout.Space(4);

                // Workspace Root
                DrawReadOnlyRow("Workspace Root", string.IsNullOrEmpty(ctx.WorkspaceRoot) ? "(not detected)" : ctx.WorkspaceRoot);

                // Unity Root
                DrawReadOnlyRow("Unity Root", string.IsNullOrEmpty(ctx.UnityRoot) ? "(not detected)" : ctx.UnityRoot);

                // Unity Relative Path
                if (!string.IsNullOrEmpty(ctx.UnityRootRelativePath))
                    DrawReadOnlyRow("Unity Relative", ctx.UnityRootRelativePath);

                // Fingerprint
                if (!string.IsNullOrEmpty(ctx.Fingerprint))
                    DrawReadOnlyRow("Fingerprint", ctx.Fingerprint);

                // VCS info
                if (ctx.Vcs != null && ctx.Vcs.Type != WorkspaceVcsType.None)
                {
                    EditorGUILayout.Space(4);
                    DrawReadOnlyRow("VCS Type", ctx.Vcs.Type.ToString());
                    if (!string.IsNullOrEmpty(ctx.Vcs.BranchId))
                        DrawReadOnlyRow("Branch", ctx.Vcs.BranchId);
                    if (!string.IsNullOrEmpty(ctx.Vcs.Revision))
                        DrawReadOnlyRow("Revision", ctx.Vcs.Revision);
                    if (!string.IsNullOrEmpty(ctx.Vcs.Url))
                        DrawReadOnlyRow("SVN URL", ctx.Vcs.Url);
                }

                // Resolved at
                if (ctx.ResolvedAt != default)
                {
                    EditorGUILayout.Space(4);
                    var localTime = ctx.ResolvedAt.ToLocalTime();
                    DrawReadOnlyRow("Resolved At", localTime.ToString("yyyy-MM-dd HH:mm:ss"));
                }
            });
        }

        // ─────────────────────────────────────────────────────────────────────
        // Card: Detection Actions
        // ─────────────────────────────────────────────────────────────────────

        private void DrawDetectionActionsCard(AgentCoreSettingsContext context)
        {
            context.Ui.DrawCard("Detection Actions", "Manually trigger workspace re-detection.", () =>
            {
                EditorGUILayout.BeginHorizontal();

                GUI.enabled = !_isRefreshing;

                if (GUILayout.Button("Refresh Workspace", GUILayout.Width(150)))
                {
                    _isRefreshing = true;
                    _refreshError = null;
                    try
                    {
                        _cachedContext = WorkspaceContextService.Refresh();
                    }
                    catch (Exception ex)
                    {
                        _refreshError = ex.Message;
                    }
                    finally
                    {
                        _isRefreshing = false;
                    }
                }

                if (GUILayout.Button("Invalidate Cache", GUILayout.Width(130)))
                {
                    WorkspaceContextService.InvalidateCache();
                    _cachedContext = null;
                    _refreshError = null;
                }

                GUI.enabled = true;

                EditorGUILayout.EndHorizontal();

                if (!string.IsNullOrEmpty(_refreshError))
                {
                    EditorGUILayout.Space(4);
                    context.Ui.DrawStatusLabel($"Refresh failed: {_refreshError}", SettingsStatusLevel.Error, miniLabel: true);
                }

            });
        }

        // ─────────────────────────────────────────────────────────────────────
        // Card: Scope Roots
        // ─────────────────────────────────────────────────────────────────────

        private void DrawScopeRootsCard(AgentCoreSettingsContext context)
        {
            _showScopeRoots = EditorGUILayout.Foldout(_showScopeRoots, "Scope Roots", true, EditorStyles.foldoutHeader);
            if (!_showScopeRoots)
                return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            context.Ui.DrawHelpText("Detected business sub-roots under the workspace. These are used to scope tool operations.");
            EditorGUILayout.Space(4);

            var ctx = _cachedContext;
            if (ctx == null || ctx.Roots == null || ctx.Roots.Count == 0)
            {
                context.Ui.DrawStatusLabel("No scope roots detected.", SettingsStatusLevel.Warning, miniLabel: true);
            }
            else
            {
                // Header row
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Display Name", EditorStyles.miniLabel, GUILayout.Width(140));
                EditorGUILayout.LabelField("Scope", EditorStyles.miniLabel, GUILayout.Width(90));
                EditorGUILayout.LabelField("Role", EditorStyles.miniLabel, GUILayout.Width(130));
                EditorGUILayout.LabelField("Relative Path", EditorStyles.miniLabel);
                EditorGUILayout.EndHorizontal();

                var lineStyle = new GUIStyle(EditorStyles.miniLabel) { wordWrap = false };

                foreach (var root in ctx.Roots)
                {
                    if (root == null) continue;

                    EditorGUILayout.BeginHorizontal();

                    // Enabled indicator
                    var nameColor = root.IsEnabled ? Color.white : Color.gray;
                    var prevColor = GUI.contentColor;
                    GUI.contentColor = nameColor;

                    EditorGUILayout.LabelField(root.DisplayName ?? root.Id ?? "?", lineStyle, GUILayout.Width(140));
                    EditorGUILayout.LabelField(root.ScopeType.ToString(), lineStyle, GUILayout.Width(90));
                    EditorGUILayout.LabelField(root.Role.ToString(), lineStyle, GUILayout.Width(130));
                    EditorGUILayout.LabelField(root.RelativePath ?? "(unknown)", lineStyle);

                    GUI.contentColor = prevColor;

                    // Risk badge
                    var risk = WorkspacePathPolicy.GetRisk(root.Role);
                    DrawRiskBadge(risk);

                    EditorGUILayout.EndHorizontal();
                }
            }

            EditorGUILayout.EndVertical();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Card: Manual Overrides
        // ─────────────────────────────────────────────────────────────────────

        private void DrawManualOverridesCard(AgentCoreSettingsContext context)
        {
            _showManualOverrides = EditorGUILayout.Foldout(_showManualOverrides, "Manual Overrides", true, EditorStyles.foldoutHeader);
            if (!_showManualOverrides)
                return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            // ADR-17: Workspace Root / Unity Root Path Override fields removed
            //   Workspace uses auto-detection (SVN probe + UnityRoot fallback)
            //   If enterprise users really need manual override, use internal API or env vars
            context.Ui.DrawHelpText(
                "Workspace uses auto-detection (SVN working copy probe + UnityRoot fallback). If detection fails, check SVN state or use 'Refresh Workspace'.");
            EditorGUILayout.Space(4);

            // workspace.json config file actions
            var ctx = _cachedContext;
            var workspaceRoot = ctx?.WorkspaceRoot;
            var hasWorkspaceRoot = !string.IsNullOrEmpty(workspaceRoot);

            EditorGUILayout.BeginHorizontal();

            GUI.enabled = hasWorkspaceRoot;

            if (GUILayout.Button("Open workspace.json", GUILayout.Width(160)))
            {
                var configPath = WorkspaceConfigStorage.GetConfigPath(workspaceRoot);
                if (!File.Exists(configPath))
                {
                    // Create default config
                    WorkspaceConfigStorage.Save(workspaceRoot, new WorkspaceConfig());
                }
                EditorUtility.RevealInFinder(configPath);
            }

            if (GUILayout.Button("Reset workspace.json", GUILayout.Width(160)))
            {
                if (EditorUtility.DisplayDialog(
                    "Reset workspace.json",
                    $"This will overwrite .agentcore/workspace.json in:\n{workspaceRoot}\n\nContinue?",
                    "Reset", "Cancel"))
                {
                    WorkspaceConfigStorage.Save(workspaceRoot, new WorkspaceConfig());
                    WorkspaceContextService.InvalidateCache();
                    _cachedContext = WorkspaceContextService.Refresh();
                }
            }

            GUI.enabled = true;

            EditorGUILayout.EndHorizontal();

            if (!hasWorkspaceRoot)
            {
                context.Ui.DrawStatusLabel(
                    "Workspace root not resolved — refresh workspace first.",
                    SettingsStatusLevel.Warning, miniLabel: true);
            }

            EditorGUILayout.EndVertical();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Card: Safety Notes
        // ─────────────────────────────────────────────────────────────────────

        private void DrawSafetyNotesCard(AgentCoreSettingsContext context)
        {
            _showSafetyNotes = EditorGUILayout.Foldout(_showSafetyNotes, "Path Safety Policy", true, EditorStyles.foldoutHeader);
            if (!_showSafetyNotes)
                return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            context.Ui.DrawHelpText(
                "AgentCore enforces write-safety rules based on each scope root's Role. " +
                "Read-only roles block all write operations; high-risk roles require explicit confirmation.");
            EditorGUILayout.Space(4);

            // Role → Risk table
            var roles = (WorkspaceRootRole[])Enum.GetValues(typeof(WorkspaceRootRole));
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Role", EditorStyles.miniLabel, GUILayout.Width(180));
            EditorGUILayout.LabelField("Risk Level", EditorStyles.miniLabel, GUILayout.Width(100));
            EditorGUILayout.LabelField("Write Allowed", EditorStyles.miniLabel, GUILayout.Width(100));
            EditorGUILayout.EndHorizontal();

            foreach (var role in roles)
            {
                var risk = WorkspacePathPolicy.GetRisk(role);
                var writeAllowed = WorkspacePathPolicy.IsWriteAllowed(role);

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(role.ToString(), EditorStyles.miniLabel, GUILayout.Width(180));

                var prevColor = GUI.contentColor;
                GUI.contentColor = GetRiskColor(risk);
                EditorGUILayout.LabelField(risk.ToString(), EditorStyles.miniLabel, GUILayout.Width(100));
                GUI.contentColor = prevColor;

                GUI.contentColor = writeAllowed ? Color.green : Color.red;
                EditorGUILayout.LabelField(writeAllowed ? "Yes" : "No", EditorStyles.miniLabel, GUILayout.Width(100));
                GUI.contentColor = prevColor;

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndVertical();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────

        private static void DrawReadOnlyRow(string label, string value)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(label, EditorStyles.miniLabel);
            var style = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };
            EditorGUILayout.SelectableLabel(value, style, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.EndHorizontal();
        }

        private static void DrawRiskBadge(WorkspaceOperationRisk risk)
        {
            var prev = GUI.contentColor;
            GUI.contentColor = GetRiskColor(risk);
            EditorGUILayout.LabelField($"[{risk}]", EditorStyles.miniLabel, GUILayout.Width(80));
            GUI.contentColor = prev;
        }

        private static Color GetRiskColor(WorkspaceOperationRisk risk)
        {
            switch (risk)
            {
                case WorkspaceOperationRisk.Safe:      return Color.green;
                case WorkspaceOperationRisk.LowRisk:   return new Color(0.6f, 1f, 0.4f);
                case WorkspaceOperationRisk.MediumRisk: return new Color(1f, 0.8f, 0.2f);
                case WorkspaceOperationRisk.HighRisk:  return new Color(1f, 0.4f, 0.1f);
                case WorkspaceOperationRisk.Blocked:   return Color.red;
                default:                               return Color.white;
            }
        }
    }
}
