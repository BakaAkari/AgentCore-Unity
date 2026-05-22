using System;
using System.Collections.Generic;
using System.Linq;
using AgentCore.Editor.Config.Settings;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.Config
{
    /// <summary>
    /// Provides the AgentCore Project Settings shell.
    /// </summary>
    public class AgentCoreSettingsProvider : SettingsProvider
    {
        private AgentCoreSettingsContext _settingsContext;
        private IReadOnlyList<IAgentCoreSettingsSection> _settingsSections;

        private AgentCoreSettingsProvider(string path, SettingsScope scope)
            : base(path, scope) { }

        /// <summary>
        /// Creates the AgentCore Project Settings provider.
        /// </summary>
        /// <returns>The configured settings provider instance.</returns>
        [SettingsProvider]
        public static SettingsProvider CreateSettingsProvider()
        {
            return new AgentCoreSettingsProvider("Project/AgentCore", SettingsScope.Project)
            {
                label = "AgentCore",
                keywords = new[] { "agent", "ai", "llm", "chat", "agentcore", "mem0", "lightrag", "tools", "extensions" }
            };
        }

        /// <inheritdoc />
        public override void OnActivate(string searchContext, VisualElement rootElement)
        {
            InitializeSettingsShell();
        }

        /// <inheritdoc />
        public override void OnGUI(string searchContext)
        {
            EnsureSettingsShell();

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("AgentCore Settings", EditorStyles.boldLabel);
            _settingsContext.Ui.DrawHelpText("Configure AgentCore through modular settings sections. Built-in features and optional components are mounted through the same settings shell.");
            EditorGUILayout.Space(8);

            DrawSettingsShell();
        }

        private void InitializeSettingsShell()
        {
            _settingsContext = AgentCoreSettingsContext.Create();
            AgentCoreSettingsRegistry.Refresh();
            _settingsSections = AgentCoreSettingsRegistry.Sections;
        }

        private void EnsureSettingsShell()
        {
            if (_settingsContext == null || _settingsContext.Settings == null)
            {
                InitializeSettingsShell();
            }

            if (_settingsSections == null || _settingsSections.Count == 0)
            {
                AgentCoreSettingsRegistry.Refresh();
                _settingsSections = AgentCoreSettingsRegistry.Sections;
            }
        }

        private void DrawSettingsShell()
        {
            var visibleSections = _settingsSections
                .Where(section => section != null && section.IsVisible(_settingsContext))
                .OrderBy(section => section.Order)
                .ThenBy(section => section.Id, StringComparer.Ordinal)
                .ToList();

            if (visibleSections.Count == 0)
            {
                EditorGUILayout.HelpBox("No AgentCore settings sections are available.", MessageType.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(_settingsContext.State.SelectedSectionId) ||
                visibleSections.All(section => section.Id != _settingsContext.State.SelectedSectionId))
            {
                SelectSettingsSection(visibleSections[0].Id);
            }

            EditorGUILayout.BeginHorizontal();
            DrawSettingsNavigation(visibleSections);
            GUILayout.Space(12);
            DrawSelectedSettingsSection(visibleSections);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawSettingsNavigation(IReadOnlyList<IAgentCoreSettingsSection> visibleSections)
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(210));

            var currentCategory = string.Empty;
            foreach (var section in visibleSections)
            {
                var category = string.IsNullOrWhiteSpace(section.Category) ? "General" : section.Category;
                if (!string.Equals(currentCategory, category, StringComparison.Ordinal))
                {
                    if (!string.IsNullOrEmpty(currentCategory))
                    {
                        EditorGUILayout.Space(6);
                    }

                    currentCategory = category;
                    EditorGUILayout.LabelField(currentCategory, EditorStyles.miniBoldLabel);
                }

                var isSelected = _settingsContext.State.SelectedSectionId == section.Id;
                var previousColor = GUI.backgroundColor;
                if (isSelected)
                {
                    GUI.backgroundColor = new Color(0.35f, 0.55f, 0.9f, 1f);
                }

                if (GUILayout.Button(section.Title, EditorStyles.miniButton, GUILayout.Height(24)))
                {
                    SelectSettingsSection(section.Id);
                }

                GUI.backgroundColor = previousColor;
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndVertical();
        }

        private void DrawSelectedSettingsSection(IReadOnlyList<IAgentCoreSettingsSection> visibleSections)
        {
            var selected = visibleSections.FirstOrDefault(section => section.Id == _settingsContext.State.SelectedSectionId) ?? visibleSections[0];

            EditorGUILayout.BeginVertical();
            _settingsContext.Ui.DrawSectionTitle(selected.Title, selected.Description);

            try
            {
                selected.Draw(_settingsContext);
            }
            catch (Exception ex)
            {
                EditorGUILayout.HelpBox($"Failed to draw settings section '{selected.Id}': {ex.Message}", MessageType.Error);
            }

            EditorGUILayout.EndVertical();
        }

        private void SelectSettingsSection(string sectionId)
        {
            if (_settingsContext == null || _settingsContext.State.SelectedSectionId == sectionId)
                return;

            var previousSection = AgentCoreSettingsRegistry.GetSection(_settingsContext.State.SelectedSectionId);
            previousSection?.OnDeactivate(_settingsContext);

            _settingsContext.State.SelectedSectionId = sectionId;

            var nextSection = AgentCoreSettingsRegistry.GetSection(sectionId);
            nextSection?.OnActivate(_settingsContext);
        }
    }

    /// <summary>
    /// Simple modal input dialog for secure setting values.
    /// </summary>
    public class EditorInputDialog : EditorWindow
    {
        private string _value = string.Empty;
        private string _message = string.Empty;
        private bool _confirmed;

        /// <summary>
        /// Shows a blocking modal input dialog.
        /// </summary>
        /// <param name="title">Dialog title.</param>
        /// <param name="message">Prompt message.</param>
        /// <param name="defaultValue">Default text value.</param>
        /// <returns>The entered value, or null when cancelled.</returns>
        public static string Show(string title, string message, string defaultValue = "")
        {
            var window = CreateInstance<EditorInputDialog>();
            window.titleContent = new GUIContent(title);
            window._message = message;
            window._value = defaultValue ?? string.Empty;
            window.position = new Rect(Screen.width / 2f, Screen.height / 2f, 420, 120);
            window.ShowModalUtility();
            return window._confirmed ? window._value : null;
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField(_message, EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space(8);
            GUI.SetNextControlName("input");
            _value = EditorGUILayout.TextField(_value);
            EditorGUI.FocusTextInControl("input");

            EditorGUILayout.Space(8);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("OK", GUILayout.Width(80)))
            {
                _confirmed = true;
                Close();
            }

            if (GUILayout.Button("Cancel", GUILayout.Width(80)))
            {
                _confirmed = false;
                Close();
            }

            EditorGUILayout.EndHorizontal();
        }
    }
}
