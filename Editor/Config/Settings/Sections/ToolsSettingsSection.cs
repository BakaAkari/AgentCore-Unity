using System.Collections.Generic;
using System.Linq;
using AgentCore.Editor.Tools;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Config.Settings.Sections
{
    /// <summary>
    /// Configures which registered tools are exposed to the LLM.
    /// </summary>
    public sealed class ToolsSettingsSection : SettingsSectionBase
    {
        private const string CategoryFoldoutPrefix = "tools.category.";

        /// <inheritdoc />
        public override string Id => "tools";

        /// <inheritdoc />
        public override string Title => "Tools";

        /// <inheritdoc />
        public override string Description => "Tool exposure presets, categories, and individual tool visibility.";

        /// <inheritdoc />
        public override string Category => "Extensions";

        /// <inheritdoc />
        public override int Order => 900;

        /// <inheritdoc />
        public override void Draw(AgentCoreSettingsContext context)
        {
            context.Ui.DrawCard(
                "Tools",
                "Enable or disable tool categories to reduce prompt size and focus Agent capabilities.",
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
            context.Ui.DrawHelpText("Safe Mode disables FileSystem and Scripting tools. Full Mode clears all category and tool disable rules.");
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
    }
}
