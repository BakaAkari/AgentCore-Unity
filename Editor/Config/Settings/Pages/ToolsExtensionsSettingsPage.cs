using System;
using System.Collections.Generic;
using System.Linq;
using AgentCore.Editor.Extensions;
using AgentCore.Editor.L10n;
using AgentCore.Editor.Tools;
using AgentCore.Editor.Tools.Infrastructure;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Config.Settings.Pages
{
    /// <summary>
    /// Tools &amp; Extensions settings page — tool visibility, optional components, and extension settings.
    /// </summary>
    public sealed class ToolsExtensionsSettingsPage : IAgentCoreSettingsPage
    {
        private const string CategoryFoldoutPrefix = "tools-extensions.category.";
        private const string ComponentSettingsFoldoutPrefix = "tools-extensions.component-settings.";

        /// <inheritdoc />
        public string Id => "tools-extensions";

        /// <inheritdoc />
        public string Title => Loc.Tr("settings.tools.title", "Tools & Extensions");

        /// <inheritdoc />
        public string Description => Loc.Tr(
            "settings.tools.description",
            "Control which tools are exposed to the LLM and manage optional components.");

        /// <inheritdoc />
        public int Order => 400;

        /// <inheritdoc />
        public void OnActivate(AgentCoreSettingsContext context) { }

        /// <inheritdoc />
        public void OnDeactivate(AgentCoreSettingsContext context) { }

        /// <inheritdoc />
        public void Draw(AgentCoreSettingsContext context)
        {
            DrawCapabilityOverviewCard(context);
            EditorGUILayout.Space(8);
            DrawToolVisibilityCard(context);
            EditorGUILayout.Space(8);
            DrawTextSearchCard(context);
            EditorGUILayout.Space(8);
            DrawOptionalComponentsCard(context);
            EditorGUILayout.Space(8);
            DrawOtherExtensionSettingsCard(context);
        }

        // ── Capability Overview ──

        private static void DrawCapabilityOverviewCard(AgentCoreSettingsContext context)
        {
            var settings = context.Settings;

            context.Ui.DrawCard(
                Loc.Tr("settings.tools.overview.title", "Capability Overview"),
                Loc.Tr("settings.tools.overview.description", "Summary of currently available agent capabilities."),
                () =>
            {
                var allTools = ToolRegistry.Instance.GetAllTools();
                if (allTools != null && allTools.Count > 0)
                {
                    var enabledCount = allTools.Count(tool =>
                        tool?.Metadata != null && !settings.IsToolDisabled(tool.Metadata.Name, tool.Metadata.Category));
                    EditorGUILayout.LabelField(Loc.Tr("settings.tools.overview.toolsEnabled", "Tools enabled: {0}/{1}", enabledCount, allTools.Count), EditorStyles.miniLabel);
                }
                else
                {
                    EditorGUILayout.LabelField(Loc.Tr("settings.tools.overview.notInitialized", "Tools not initialized yet."), EditorStyles.miniLabel);
                }

                var vcsEnabled = OptionalComponentManager.IsVcsEnabled();
                EditorGUILayout.LabelField(vcsEnabled
                    ? Loc.Tr("settings.tools.overview.vcsEnabled", "VCS component: enabled")
                    : Loc.Tr("settings.tools.overview.vcsDisabled", "VCS component: disabled"), EditorStyles.miniLabel);
            });
        }

        // ── Tool Visibility ──

        private static void DrawToolVisibilityCard(AgentCoreSettingsContext context)
        {
            context.Ui.DrawCard(
                Loc.Tr("settings.tools.visibility.title", "Tool Visibility"),
                Loc.Tr("settings.tools.visibility.description", "Toggle tool categories to reduce prompt size."),
                () => DrawToolManagement(context));
        }

        private static void DrawToolManagement(AgentCoreSettingsContext context)
        {
            var settings = context.Settings;
            EnsureToolLists(settings);

            var allTools = ToolRegistry.Instance.GetAllTools();
            if (allTools == null || allTools.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    Loc.Tr(
                        "settings.tools.visibility.noTools",
                        "No registered tools are available yet. Tools become available after AgentLoop initialization."),
                    MessageType.Info);
                return;
            }

            var totalCount = allTools.Count;
            var enabledCount = allTools.Count(tool => IsToolEnabled(settings, tool));
            EditorGUILayout.LabelField(
                Loc.Tr(
                    "settings.tools.visibility.summary",
                    "Registered {0} tools, {1} enabled, {2} disabled",
                    totalCount,
                    enabledCount,
                    totalCount - enabledCount),
                EditorStyles.miniLabel);

            EditorGUILayout.Space(3);
            DrawPresetActions(context, allTools);
            context.Ui.DrawHelpText(Loc.Tr(
                "settings.tools.visibility.presetHelp",
                "Safe Mode disables FileSystem and Scripting tools. Full Mode clears all disable rules."));
            EditorGUILayout.Space(3);

            var groupedTools = allTools
                .Where(tool => tool?.Metadata != null)
                .GroupBy(tool => tool.Metadata.Category ?? "default")
                .OrderBy(group => group.Key)
                .ToList();

            foreach (var group in groupedTools)
            {
                DrawToolCategory(context, group.Key, group.OrderBy(tool => tool.Metadata.Name).ToList());
            }
        }

        private static void DrawPresetActions(AgentCoreSettingsContext context, IReadOnlyList<IAgentTool> allTools)
        {
            var settings = context.Settings;

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(Loc.Tr("settings.tools.visibility.enableAll", "Enable All"), GUILayout.MinWidth(90)))
            {
                settings.disabledToolCategories.Clear();
                settings.disabledTools.Clear();
                settings.SaveSettings();
            }

            if (GUILayout.Button(Loc.Tr("settings.tools.visibility.disableAll", "Disable All"), GUILayout.MinWidth(90)))
            {
                settings.disabledToolCategories.Clear();
                settings.disabledTools.Clear();
                foreach (var tool in allTools)
                {
                    var meta = tool?.Metadata;
                    if (meta != null && !settings.disabledTools.Contains(meta.Name))
                    {
                        settings.disabledTools.Add(meta.Name);
                    }
                }

                settings.SaveSettings();
            }

            if (GUILayout.Button(Loc.Tr("settings.tools.visibility.safeMode", "Safe Mode"), GUILayout.MinWidth(90)))
            {
                ApplyToolPreset(settings, allTools, "Safe");
            }

            if (GUILayout.Button(Loc.Tr("settings.tools.visibility.fullMode", "Full Mode"), GUILayout.MinWidth(90)))
            {
                ApplyToolPreset(settings, allTools, "Full");
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private static void DrawToolCategory(AgentCoreSettingsContext context, string category, IReadOnlyList<IAgentTool> toolsInCategory)
        {
            var settings = context.Settings;
            var foldoutKey = CategoryFoldoutPrefix + category;
            var categoryDisabled = settings.disabledToolCategories.Contains(category);
            var enabledInCategory = toolsInCategory.Count(tool => IsToolEnabled(settings, tool));

            EditorGUILayout.BeginHorizontal();
            var expanded = context.State.GetFoldout(foldoutKey, false);
            expanded = EditorGUILayout.Foldout(expanded, $"{category} ({enabledInCategory}/{toolsInCategory.Count})", true);
            context.State.SetFoldout(foldoutKey, expanded);

            EditorGUI.BeginChangeCheck();
            var categoryEnabled = EditorGUILayout.Toggle(!categoryDisabled, GUILayout.Width(20));
            if (EditorGUI.EndChangeCheck())
            {
                SetCategoryEnabled(settings, category, categoryEnabled);
            }

            EditorGUILayout.EndHorizontal();

            if (!expanded)
                return;

            EditorGUI.indentLevel++;
            if (categoryDisabled)
            {
                EditorGUILayout.HelpBox(
                    Loc.Tr(
                        "settings.tools.visibility.categoryDisabled",
                        "This category is disabled as a whole. Enable the category before managing individual tools."),
                    MessageType.Info);
            }

            var previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled && !categoryDisabled;

            foreach (var tool in toolsInCategory)
            {
                DrawToolToggle(settings, tool);
            }

            GUI.enabled = previousEnabled;
            EditorGUI.indentLevel--;
        }

        private static void DrawToolToggle(AgentCoreSettings settings, IAgentTool tool)
        {
            var meta = tool?.Metadata;
            if (meta == null)
                return;

            var toolDisabled = settings.disabledTools.Contains(meta.Name);
            EditorGUI.BeginChangeCheck();
            var toolEnabled = EditorGUILayout.ToggleLeft(
                new GUIContent(meta.Name, TruncateForTooltip(meta.Description, 200)),
                !toolDisabled);

            if (EditorGUI.EndChangeCheck())
            {
                SetToolEnabled(settings, meta.Name, toolEnabled);
            }
        }

        private static void SetCategoryEnabled(AgentCoreSettings settings, string category, bool enabled)
        {
            if (enabled)
            {
                settings.disabledToolCategories.Remove(category);
            }
            else if (!settings.disabledToolCategories.Contains(category))
            {
                settings.disabledToolCategories.Add(category);
            }

            settings.SaveSettings();
        }

        private static void SetToolEnabled(AgentCoreSettings settings, string toolName, bool enabled)
        {
            if (enabled)
            {
                settings.disabledTools.Remove(toolName);
            }
            else if (!settings.disabledTools.Contains(toolName))
            {
                settings.disabledTools.Add(toolName);
            }

            settings.SaveSettings();
        }

        private static void ApplyToolPreset(AgentCoreSettings settings, IReadOnlyList<IAgentTool> allTools, string preset)
        {
            EnsureToolLists(settings);
            settings.disabledToolCategories.Clear();
            settings.disabledTools.Clear();

            if (preset == "Safe")
            {
                foreach (var tool in allTools)
                {
                    var meta = tool?.Metadata;
                    if (meta == null)
                        continue;

                    if (meta.Category == "FileSystem" || meta.Category == "Scripting")
                    {
                        if (!settings.disabledTools.Contains(meta.Name))
                        {
                            settings.disabledTools.Add(meta.Name);
                        }
                    }
                }
            }

            settings.SaveSettings();
        }

        private static bool IsToolEnabled(AgentCoreSettings settings, IAgentTool tool)
        {
            var meta = tool?.Metadata;
            return meta != null && !settings.IsToolDisabled(meta.Name, meta.Category);
        }

        private static void EnsureToolLists(AgentCoreSettings settings)
        {
            if (settings.disabledToolCategories == null)
            {
                settings.disabledToolCategories = new List<string>();
            }

            if (settings.disabledTools == null)
            {
                settings.disabledTools = new List<string>();
            }
        }

        private static string TruncateForTooltip(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            return text.Length <= maxLength ? text : text.Substring(0, maxLength) + "...";
        }

        // ── Text Search (ripgrep) ──

        /// <summary>
        /// ripgrep 检测结果（只读缓存，重绘时不探测）、可选的自定义路径与 "Re-detect"。
        /// </summary>
        private static void DrawTextSearchCard(AgentCoreSettingsContext context)
        {
            context.Ui.DrawCard(
                Loc.Tr("settings.tools.rg.title", "Text Search (ripgrep)"),
                Loc.Tr(
                    "settings.tools.rg.description",
                    "manage_file search_content and search_code search_text use ripgrep when it is installed (fast, no regex backtracking, honours .gitignore); otherwise the built-in .NET scanner."),
                () =>
                {
                    var availability = RgLocator.Availability;
                    var level = availability == RgAvailability.Available
                        ? SettingsStatusLevel.Success
                        : availability == RgAvailability.Unavailable ? SettingsStatusLevel.Warning : SettingsStatusLevel.None;
                    context.Ui.DrawStatusLabel(RgLocator.Describe(), level, miniLabel: true);

                    var probeError = RgLocator.ProbeError;
                    if (!string.IsNullOrEmpty(probeError))
                        context.Ui.DrawHelpText(probeError);

                    EditorGUILayout.BeginHorizontal();
                    var customPath = EditorGUILayout.DelayedTextField(
                        new GUIContent(
                            Loc.Tr("settings.tools.rg.path", "rg path (optional)"),
                            Loc.Tr(
                                "settings.tools.rg.pathTooltip",
                                "Full path to rg / rg.exe, or the folder containing it. Leave empty to search PATH and common install folders. Saved per user (EditorPrefs).")),
                        RgLocator.CustomPath);
                    if (customPath != RgLocator.CustomPath)
                        RgLocator.CustomPath = customPath;

                    if (GUILayout.Button(Loc.Tr("settings.tools.rg.browse", "Browse"), GUILayout.MinWidth(64)))
                    {
                        var picked = EditorUtility.OpenFilePanel(Loc.Tr("settings.tools.rg.selectExe", "Select ripgrep executable"), string.Empty, Application.platform == RuntimePlatform.WindowsEditor ? "exe" : string.Empty);
                        if (!string.IsNullOrEmpty(picked))
                            RgLocator.CustomPath = picked;
                    }
                    EditorGUILayout.EndHorizontal();

                    if (GUILayout.Button(new GUIContent(
                        Loc.Tr("settings.tools.rg.redetect", "Re-detect"),
                        Loc.Tr(
                            "settings.tools.rg.redetectTooltip",
                            "Check for ripgrep again in the background (custom path, package Plugins/rg, PATH, common install folders)."))))
                    {
                        _ = RgLocator.Refresh();
                    }
                });
        }

        // ── Optional Components ──

        private static void DrawOptionalComponentsCard(AgentCoreSettingsContext context)
        {
            context.Ui.DrawCard(
                Loc.Tr("settings.tools.components.title", "Optional Components"),
                Loc.Tr(
                    "settings.tools.components.description",
                    "Enable/disable optional components for this project on this machine. Takes effect immediately: no recompile, ProjectSettings are not modified."),
                () =>
                {
                    DrawLegacyDefinesNotice();

                    foreach (var component in OptionalComponentManager.GetComponents())
                    {
                        DrawOptionalComponentCard(context, component);
                        EditorGUILayout.Space(4);
                    }
                });
        }

        private static void DrawLegacyDefinesNotice()
        {
            if (!OptionalComponentManager.HasLegacyScriptingDefines())
                return;

            EditorGUILayout.HelpBox(
                Loc.Tr(
                    "settings.tools.legacy.notice",
                    "ProjectSettings still contain legacy AGENTCORE_VCS / AGENTCORE_INDEXING / AGENTCORE_MCP scripting defines from an older AgentCore version. They no longer control anything and can be removed."),
                MessageType.Info);
            if (GUILayout.Button(Loc.Tr("settings.tools.legacy.remove", "Remove Legacy Scripting Defines from ProjectSettings")))
            {
                // Deferred out of the IMGUI pass: the dialog and the resulting ProjectSettings write
                // must not run between Layout and Repaint.
                EditorApplication.delayCall += ConfirmRemoveLegacyDefines;
            }
            EditorGUILayout.Space(4);
        }

        private static void ConfirmRemoveLegacyDefines()
        {
            if (!EditorUtility.DisplayDialog(
                    Loc.Tr("settings.tools.legacy.dialogTitle", "Remove Legacy Scripting Defines"),
                    Loc.Tr(
                        "settings.tools.legacy.dialogMessage",
                        "Remove AGENTCORE_VCS, AGENTCORE_INDEXING and AGENTCORE_MCP from the Scripting Define Symbols of every build target and save ProjectSettings?\n\nOther defines are left untouched. This modifies ProjectSettings/ProjectSettings.asset and triggers a script recompile."),
                    Loc.Tr("settings.tools.legacy.dialogRemove", "Remove"),
                    Loc.Tr("common.cancel", "Cancel")))
                return;

            OptionalComponentManager.RemoveLegacyScriptingDefines();
        }

        private static void DrawOptionalComponentCard(AgentCoreSettingsContext context, OptionalComponentInfo component)
        {
            if (component == null)
                return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(component.DisplayName, EditorStyles.boldLabel);
            context.Ui.DrawHelpText(component.Description);
            // ADR-17: Hide "Define: AGENTCORE_XXX" — scripting define symbols are engineering detail

            EditorGUI.BeginChangeCheck();
            var enabled = EditorGUILayout.ToggleLeft(Loc.Tr("settings.tools.components.enabled", "Enabled"), component.Enabled);
            if (EditorGUI.EndChangeCheck())
            {
                // Toggling rebuilds the extension registry this page draws from; apply it after the IMGUI pass.
                EditorApplication.delayCall += () => SetComponentEnabled(component, enabled);
            }

            DrawInlineComponentSettings(context, component);

            EditorGUILayout.EndVertical();
        }

        private static void DrawInlineComponentSettings(AgentCoreSettingsContext context, OptionalComponentInfo component)
        {
            var ownedContributions = AgentCoreExtensionRegistry.Settings
                .Where(c => c != null && string.Equals(c.OwnerComponentId, component.Id, StringComparison.Ordinal))
                .OrderBy(c => c.Order)
                .ToList();

            if (ownedContributions.Count == 0)
            {
                if (component.Enabled)
                {
                    // Component enabled but contributes no in-page settings (e.g. Indexing — configured in Hub panel).
                    EditorGUILayout.Space(4);
                    EditorGUILayout.LabelField(Loc.Tr(
                            "settings.tools.components.noInPage",
                            "No in-page settings. Configure via the AgentCore Hub panel if available."), EditorStyles.miniLabel);
                }
                return;
            }

            if (!component.Enabled)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField(Loc.Tr("settings.tools.components.afterEnable", "Settings appear after enabling this component."), EditorStyles.miniLabel);
                return;
            }

            EditorGUILayout.Space(4);
            var foldoutKey = ComponentSettingsFoldoutPrefix + component.Id;
            var expanded = context.State.GetFoldout(foldoutKey, true);
            expanded = EditorGUILayout.Foldout(expanded, Loc.Tr("settings.tools.components.settings", "Settings"), true);
            context.State.SetFoldout(foldoutKey, expanded);

            if (!expanded)
                return;

            EditorGUI.indentLevel++;
            foreach (var contribution in ownedContributions)
            {
                DrawInlineContributionBody(contribution);
                EditorGUILayout.Space(4);
            }
            EditorGUI.indentLevel--;
        }

        private static void DrawInlineContributionBody(IAgentCoreSettingsContribution contribution)
        {
            // Render only the contribution body — its title/description are conveyed by the
            // surrounding component card to avoid double-labelling.
            try
            {
                contribution.DrawGUI();
            }
            catch (Exception ex)
            {
                EditorGUILayout.HelpBox(Loc.Tr(
                        "settings.tools.extensionDrawFailed",
                        "Failed to draw extension settings '{0}': {1}",
                        contribution.Id,
                        ex.Message), MessageType.Warning);
            }
        }

        private static void SetComponentEnabled(OptionalComponentInfo component, bool enabled)
        {
            switch (component.Id)
            {
                case OptionalComponentManager.VcsComponentId:
                case OptionalComponentManager.IndexingComponentId:
                case OptionalComponentManager.McpComponentId:
                    OptionalComponentManager.SetComponentEnabled(component.Id, enabled);
                    break;
                default:
                    EditorUtility.DisplayDialog(
                        Loc.Tr("settings.tools.components.unsupportedTitle", "Unsupported Optional Component"),
                        Loc.Tr(
                            "settings.tools.components.unsupportedMessage",
                            "AgentCore does not know how to toggle optional component '{0}'.",
                            component.Id),
                        Loc.Tr("common.ok", "OK"));
                    break;
            }
        }

        // ── Other Extension Settings (contributions without an owner component) ──

        private static void DrawOtherExtensionSettingsCard(AgentCoreSettingsContext context)
        {
            // Contributions that explicitly belong to an optional component are rendered
            // inline inside their component card (see DrawInlineComponentSettings).
            // This card only collects orphan contributions (OwnerComponentId == null).
            var orphanContributions = AgentCoreExtensionRegistry.Settings
                .Where(c => c != null && string.IsNullOrEmpty(c.OwnerComponentId))
                .OrderBy(c => c.Order)
                .ToList();

            if (orphanContributions.Count == 0)
            {
                // Hide the card entirely when nothing to show — keeps the page focused on
                // the structurally meaningful sections (Components, Tool Visibility).
                return;
            }

            context.Ui.DrawCard(
                Loc.Tr("settings.tools.other.title", "Other Extension Settings"),
                Loc.Tr(
                    "settings.tools.other.description",
                    "Settings contributed by extensions that are not bound to a specific optional component."),
                () =>
                {
                    foreach (var contribution in orphanContributions)
                    {
                        DrawSettingsContribution(context, contribution);
                        EditorGUILayout.Space(4);
                    }
                });
        }

        private static void DrawSettingsContribution(AgentCoreSettingsContext context, IAgentCoreSettingsContribution contribution)
        {
            if (contribution == null)
                return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(contribution.Title, EditorStyles.boldLabel);
            if (!string.IsNullOrWhiteSpace(contribution.Description))
            {
                context.Ui.DrawHelpText(contribution.Description);
            }

            EditorGUILayout.Space(4);

            try
            {
                contribution.DrawGUI();
            }
            catch (Exception ex)
            {
                EditorGUILayout.HelpBox(Loc.Tr(
                        "settings.tools.extensionDrawFailed",
                        "Failed to draw extension settings '{0}': {1}",
                        contribution.Id,
                        ex.Message), MessageType.Warning);
            }

            EditorGUILayout.EndVertical();
        }
    }
}
