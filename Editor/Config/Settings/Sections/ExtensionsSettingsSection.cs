using System;
using AgentCore.Editor.Extensions;
using UnityEditor;

namespace AgentCore.Editor.Config.Settings.Sections
{
    /// <summary>
    /// Configures optional AgentCore components and mounted extension settings.
    /// </summary>
    public sealed class ExtensionsSettingsSection : SettingsSectionBase
    {
        private const string OptionalComponentsFoldoutKey = "extensions.optional-components";
        private const string ExtensionSettingsFoldoutKey = "extensions.settings";

        /// <inheritdoc />
        public override string Id => "extensions";

        /// <inheritdoc />
        public override string Title => "Extensions";

        /// <inheritdoc />
        public override string Description => "Optional components and component-scoped settings.";

        /// <inheritdoc />
        public override string Category => "Extensions";

        /// <inheritdoc />
        public override int Order => 800;

        /// <inheritdoc />
        public override void Draw(AgentCoreSettingsContext context)
        {
            DrawOptionalComponents(context);
            EditorGUILayout.Space(8);
            DrawExtensionSettings(context);
        }

        private static void DrawOptionalComponents(AgentCoreSettingsContext context)
        {
            context.Ui.DrawCard(
                "Optional Components",
                "Enable or disable bundled AgentCore components. Changes update scripting define symbols for all build target groups and request Unity script recompilation immediately.",
                () =>
                {
                    // 实验性警告始终显示，不依赖 Component Cards 折叠状态
                    EditorGUILayout.HelpBox(
                        "Code Indexing is experimental. Background indexing can significantly impact Editor responsiveness on large projects. " +
                        "Enable it only if you need the search_code tool, and pause auto-index during intensive work.",
                        MessageType.Warning);
                    EditorGUILayout.Space(4);

                    var expanded = context.State.GetFoldout(OptionalComponentsFoldoutKey, true);
                    expanded = EditorGUILayout.Foldout(expanded, "Component Cards", true);
                    context.State.SetFoldout(OptionalComponentsFoldoutKey, expanded);
                    if (!expanded)
                        return;

                    EditorGUI.indentLevel++;
                    var buildTargetGroup = EditorUserBuildSettings.selectedBuildTargetGroup;
                    EditorGUILayout.LabelField($"Active Build Target Group: {buildTargetGroup}", EditorStyles.miniLabel);
                    context.Ui.DrawHelpText("Component state is synchronized across all build target groups. Switching build targets will no longer disable a component.");
                    EditorGUILayout.Space(4);

                    foreach (var component in OptionalComponentManager.GetComponents())
                    {
                        DrawOptionalComponentCard(context, component);
                        EditorGUILayout.Space(4);
                    }

                    context.Ui.DrawHelpText("After toggling a component, Unity refreshes assets and requests script recompilation. Wait for compilation to finish, then reopen AgentCore if Hub navigation does not update immediately.");
                    EditorGUI.indentLevel--;
                });
        }

        private static void DrawOptionalComponentCard(AgentCoreSettingsContext context, OptionalComponentInfo component)
        {
            if (component == null)
                return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(component.DisplayName, EditorStyles.boldLabel);
            context.Ui.DrawHelpText(component.Description);
            EditorGUILayout.LabelField($"Define: {component.DefineSymbol}", EditorStyles.miniLabel);

            EditorGUI.BeginChangeCheck();
            var enabled = EditorGUILayout.ToggleLeft("Enabled", component.Enabled);
            if (EditorGUI.EndChangeCheck())
            {
                SetComponentEnabled(component, enabled);
            }

            EditorGUILayout.EndVertical();
        }

        private static void SetComponentEnabled(OptionalComponentInfo component, bool enabled)
        {
            switch (component.Id)
            {
                case "vcs":
                    OptionalComponentManager.SetVcsEnabled(enabled);
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

        private static void DrawExtensionSettings(AgentCoreSettingsContext context)
        {
            var contributions = AgentCoreExtensionRegistry.Settings;
            if (contributions == null || contributions.Count == 0)
            {
                context.Ui.DrawCard(
                    "Extension Settings",
                    "No enabled optional component currently contributes settings.",
                    null);
                return;
            }

            context.Ui.DrawCard(
                "Extension Settings",
                "Settings provided by enabled AgentCore optional components.",
                () =>
                {
                    var expanded = context.State.GetFoldout(ExtensionSettingsFoldoutKey, true);
                    expanded = EditorGUILayout.Foldout(expanded, "Mounted Settings", true);
                    context.State.SetFoldout(ExtensionSettingsFoldoutKey, expanded);
                    if (!expanded)
                        return;

                    EditorGUI.indentLevel++;
                    foreach (var contribution in contributions)
                    {
                        DrawSettingsContribution(context, contribution);
                        EditorGUILayout.Space(4);
                    }

                    EditorGUI.indentLevel--;
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
