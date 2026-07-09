using System;
using System.Collections.Generic;
using System.Linq;
using AgentCore.Editor.Extensions;
using AgentCore.Editor.Tools;
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
        public string Title => "Tools & Extensions";

        /// <inheritdoc />
        public string Description => "Control which tools are exposed to the LLM and manage optional components.";

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
            DrawOptionalComponentsCard(context);
            EditorGUILayout.Space(8);
            DrawOtherExtensionSettingsCard(context);
        }

        // ── Capability Overview ──

        private static void DrawCapabilityOverviewCard(AgentCoreSettingsContext context)
        {
            var settings = context.Settings;

            context.Ui.DrawCard("Capability Overview", "Summary of currently available agent capabilities.", () =>
            {
                var allTools = ToolRegistry.Instance.GetAllTools();
                if (allTools != null && allTools.Count > 0)
                {
                    var enabledCount = allTools.Count(tool =>
                        tool?.Metadata != null && !settings.IsToolDisabled(tool.Metadata.Name, tool.Metadata.Category));
                    EditorGUILayout.LabelField($"Tools enabled: {enabledCount}/{allTools.Count}", EditorStyles.miniLabel);
                }
                else
                {
                    EditorGUILayout.LabelField("Tools not initialized yet.", EditorStyles.miniLabel);
                }

                var vcsEnabled = OptionalComponentManager.IsVcsEnabled();
                EditorGUILayout.LabelField($"VCS component: {(vcsEnabled ? "enabled" : "disabled")}", EditorStyles.miniLabel);
            });
        }

        // ── Tool Visibility ──

        private static void DrawToolVisibilityCard(AgentCoreSettingsContext context)
        {
            context.Ui.DrawCard(
                "Tool Visibility",
                "Toggle tool categories to reduce prompt size.",
                () => DrawToolManagement(context));
        }

        private static void DrawToolManagement(AgentCoreSettingsContext context)
        {
            var settings = context.Settings;
            EnsureToolLists(settings);

            var allTools = ToolRegistry.Instance.GetAllTools();
            if (allTools == null || allTools.Count == 0)
            {
                EditorGUILayout.HelpBox("No registered tools are available yet. Tools become available after AgentLoop initialization.", MessageType.Info);
                return;
            }

            var totalCount = allTools.Count;
            var enabledCount = allTools.Count(tool => IsToolEnabled(settings, tool));
            EditorGUILayout.LabelField(
                $"Registered {totalCount} tools, {enabledCount} enabled, {totalCount - enabledCount} disabled",
                EditorStyles.miniLabel);

            EditorGUILayout.Space(3);
            DrawPresetActions(context, allTools);
            context.Ui.DrawHelpText("Safe Mode disables FileSystem and Scripting tools. Full Mode clears all disable rules.");
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
            if (GUILayout.Button("Enable All", GUILayout.Width(90)))
            {
                settings.disabledToolCategories.Clear();
                settings.disabledTools.Clear();
                settings.SaveSettings();
            }

            if (GUILayout.Button("Disable All", GUILayout.Width(90)))
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

            if (GUILayout.Button("Safe Mode", GUILayout.Width(90)))
            {
                ApplyToolPreset(settings, allTools, "Safe");
            }

            if (GUILayout.Button("Full Mode", GUILayout.Width(90)))
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
                EditorGUILayout.HelpBox("This category is disabled as a whole. Enable the category before managing individual tools.", MessageType.Info);
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

        // ── Optional Components ──

        private static void DrawOptionalComponentsCard(AgentCoreSettingsContext context)
        {
            context.Ui.DrawCard(
                "Optional Components",
                "Enable/disable optional components. Toggling triggers script recompile.",
                () =>
                {
                    foreach (var component in OptionalComponentManager.GetComponents())
                    {
                        DrawOptionalComponentCard(context, component);
                        EditorGUILayout.Space(4);
                    }
                });
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
            var enabled = EditorGUILayout.ToggleLeft("Enabled", component.Enabled);
            if (EditorGUI.EndChangeCheck())
            {
                SetComponentEnabled(component, enabled);
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
                    EditorGUILayout.LabelField("No in-page settings. Configure via the AgentCore Hub panel if available.", EditorStyles.miniLabel);
                }
                return;
            }

            if (!component.Enabled)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Settings appear after enabling this component.", EditorStyles.miniLabel);
                return;
            }

            EditorGUILayout.Space(4);
            var foldoutKey = ComponentSettingsFoldoutPrefix + component.Id;
            var expanded = context.State.GetFoldout(foldoutKey, true);
            expanded = EditorGUILayout.Foldout(expanded, "Settings", true);
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
                EditorGUILayout.HelpBox($"Failed to draw extension settings '{contribution.Id}': {ex.Message}", MessageType.Warning);
            }
        }

        private static void SetComponentEnabled(OptionalComponentInfo component, bool enabled)
        {
            switch (component.Id)
            {
                case "vcs":
                    OptionalComponentManager.SetVcsEnabled(enabled);
                    // v1.4.3: 记录用户手动意图，避免下次 Editor 启动时项目级 auto-enable
                    // 逻辑覆盖用户选择（尤其重要：用户主动禁用后不希望被自动重新启用）。
                    OptionalComponentManager.RecordVcsUserIntent(enabled);
                    break;
                case "indexing":
                    OptionalComponentManager.SetIndexingEnabled(enabled);
                    break;
                default:
                    EditorUtility.DisplayDialog(
                        "Unsupported Optional Component",
                        $"AgentCore does not know how to toggle optional component '{component.Id}'.",
                        "OK");
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
                "Other Extension Settings",
                "Settings contributed by extensions that are not bound to a specific optional component.",
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
                EditorGUILayout.HelpBox($"Failed to draw extension settings '{contribution.Id}': {ex.Message}", MessageType.Warning);
            }

            EditorGUILayout.EndVertical();
        }
    }
}
