using System;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Config.Settings
{
    /// <summary>
    /// Provides shared IMGUI drawing helpers for AgentCore settings sections.
    /// </summary>
    public sealed class AgentCoreSettingsUi
    {
        /// <summary>
        /// Draws wrapped help text using a consistent mini-label style.
        /// </summary>
        /// <param name="text">The text to draw.</param>
        public void DrawHelpText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            var style = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };
            EditorGUILayout.LabelField(text, style);
        }

        /// <summary>
        /// Draws a status label with a color derived from the status level.
        /// </summary>
        /// <param name="text">The status text.</param>
        /// <param name="level">The status level.</param>
        /// <param name="miniLabel">Whether to use mini-label styling.</param>
        public void DrawStatusLabel(string text, SettingsStatusLevel level, bool miniLabel = false)
        {
            if (string.IsNullOrEmpty(text))
                return;

            var baseStyle = miniLabel ? EditorStyles.miniLabel : EditorStyles.label;
            var style = new GUIStyle(baseStyle) { wordWrap = true };
            style.normal.textColor = GetStatusColor(level);
            EditorGUILayout.LabelField(text, style);
        }

        /// <summary>
        /// Draws a consistent section title block.
        /// </summary>
        /// <param name="title">The section title.</param>
        /// <param name="description">The section description.</param>
        public void DrawSectionTitle(string title, string description)
        {
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            DrawHelpText(description);
            EditorGUILayout.Space(8);
        }

        /// <summary>
        /// Draws a card with a bold title, optional description, and body content.
        /// </summary>
        /// <param name="title">The card title.</param>
        /// <param name="description">The card description.</param>
        /// <param name="drawContent">The card body drawing callback.</param>
        public void DrawCard(string title, string description, Action drawContent)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            DrawHelpText(description);

            if (!string.IsNullOrWhiteSpace(description))
            {
                EditorGUILayout.Space(4);
            }

            drawContent?.Invoke();
            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// Draws a masked API key row with set and clear actions.
        /// </summary>
        /// <param name="label">The field label.</param>
        /// <param name="tooltip">The field tooltip.</param>
        /// <param name="displayValue">The masked display value.</param>
        /// <param name="dialogTitle">The input dialog title.</param>
        /// <param name="dialogPrompt">The input dialog prompt.</param>
        /// <param name="setAction">The callback used to set the new key.</param>
        /// <param name="clearAction">The callback used to clear the key.</param>
        public void DrawApiKeyRow(
            string label,
            string tooltip,
            string displayValue,
            string dialogTitle,
            string dialogPrompt,
            Action<string> setAction,
            Action clearAction)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent(label, tooltip));
            EditorGUILayout.LabelField(displayValue, GUILayout.Width(120));

            if (GUILayout.Button("Set", GUILayout.Width(44)))
            {
                var newKey = AgentCore.Editor.Config.EditorInputDialog.Show(dialogTitle, dialogPrompt, string.Empty);
                if (newKey != null)
                {
                    setAction?.Invoke(newKey);
                }
            }

            if (GUILayout.Button("Clear", GUILayout.Width(54)))
            {
                clearAction?.Invoke();
            }

            EditorGUILayout.EndHorizontal();
        }

        private static Color GetStatusColor(SettingsStatusLevel level)
        {
            switch (level)
            {
                case SettingsStatusLevel.Success:
                    return new Color(0.2f, 0.8f, 0.2f);
                case SettingsStatusLevel.Warning:
                    return new Color(1f, 0.6f, 0f);
                case SettingsStatusLevel.Error:
                    return Color.red;
                case SettingsStatusLevel.Loading:
                    return Color.gray;
                default:
                    return EditorStyles.label.normal.textColor;
            }
        }
    }
}
