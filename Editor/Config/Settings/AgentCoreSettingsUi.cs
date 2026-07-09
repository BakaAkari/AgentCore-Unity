using System;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Config.Settings
{
    /// <summary>
    /// Provides shared IMGUI drawing helpers for AgentCore settings pages.
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
            try
            {
                EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
                DrawHelpText(description);

                if (!string.IsNullOrWhiteSpace(description))
                {
                    EditorGUILayout.Space(4);
                }

                try
                {
                    drawContent?.Invoke();
                }
                catch (Exception ex)
                {
                    // Defensive handling: exception in content callback must not break outer layout balance.
                    UnityEngine.Debug.LogException(ex);
                    EditorGUILayout.HelpBox(
                        $"Error drawing this card content: {ex.Message}\nSee Console for details.",
                        UnityEditor.MessageType.Error);
                }
            }
            finally
            {
                EditorGUILayout.EndVertical();
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Service Card — unified pattern for optional cloud services
        // (mem0 / LightRAG / Compression LLM).
        //
        // Layout invariants:
        //   1. Header row always visible: [Bold Title] [Status Badge]
        //   2. Description always visible below title
        //   3. Enable toggle always visible
        //   4. Configuration body only renders when enabled — reduces default
        //      density for un-configured optional services
        //   5. When disabled, drawEnabledBody is NOT invoked at all, so callers
        //      don't need to guard field visibility themselves
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Draws a standardized service card whose body only renders when the service is enabled.
        /// </summary>
        /// <param name="title">Service display name (e.g. "Memory Service").</param>
        /// <param name="description">Short description shown under the title.</param>
        /// <param name="enabled">Current enabled state.</param>
        /// <param name="onEnabledChanged">Callback invoked when the enabled toggle changes; caller persists the value.</param>
        /// <param name="statusHint">Optional status hint shown to the right of the title when enabled (e.g. endpoint URL).</param>
        /// <param name="drawEnabledBody">Body callback invoked only when <paramref name="enabled"/> is true.</param>
        public void DrawServiceCard(
            string title,
            string description,
            bool enabled,
            Action<bool> onEnabledChanged,
            string statusHint,
            Action drawEnabledBody)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            try
            {
                // Header: title + status badge
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                DrawServiceStatusBadge(enabled);
                EditorGUILayout.EndHorizontal();

                DrawHelpText(description);

                if (enabled && !string.IsNullOrEmpty(statusHint))
                {
                    var hintStyle = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };
                    hintStyle.normal.textColor = GetStatusColor(SettingsStatusLevel.Success);
                    EditorGUILayout.LabelField(statusHint, hintStyle);
                }

                EditorGUILayout.Space(4);

                // Enable toggle — always visible
                EditorGUI.BeginChangeCheck();
                var newEnabled = EditorGUILayout.ToggleLeft(
                    new GUIContent(enabled ? "Enabled" : "Enable this service"),
                    enabled);
                if (EditorGUI.EndChangeCheck())
                {
                    onEnabledChanged?.Invoke(newEnabled);
                }

                // Body only when enabled
                if (enabled)
                {
                    EditorGUILayout.Space(4);
                    try
                    {
                        drawEnabledBody?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogException(ex);
                        EditorGUILayout.HelpBox(
                            $"Error drawing this service card content: {ex.Message}\nSee Console for details.",
                            UnityEditor.MessageType.Error);
                    }
                }
            }
            finally
            {
                EditorGUILayout.EndVertical();
            }
        }

        /// <summary>
        /// Draws a compact [Enabled] / [Disabled] badge suitable for card headers and status lines.
        /// </summary>
        /// <param name="enabled">Whether the target is enabled.</param>
        /// <param name="width">Badge width in pixels.</param>
        public void DrawServiceStatusBadge(bool enabled, float width = 74f)
        {
            var text = enabled ? "● Enabled" : "○ Disabled";
            var style = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight };
            style.normal.textColor = enabled ? GetStatusColor(SettingsStatusLevel.Success) : new Color(0.55f, 0.55f, 0.55f);
            EditorGUILayout.LabelField(text, style, GUILayout.Width(width));
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

        /// <summary>
        /// Draws a horizontal button row with equal widths, avoiding truncation when labels vary in length.
        /// </summary>
        /// <param name="labels">Button labels; each also acts as the returned selection sentinel.</param>
        /// <returns>The clicked label, or null when nothing was clicked this frame.</returns>
        public string DrawEqualWidthButtonRow(params string[] labels)
        {
            if (labels == null || labels.Length == 0)
                return null;

            string clicked = null;

            EditorGUILayout.BeginHorizontal();
            foreach (var label in labels)
            {
                if (GUILayout.Button(label, GUILayout.MinWidth(120), GUILayout.ExpandWidth(true)))
                {
                    clicked = label;
                }
            }
            EditorGUILayout.EndHorizontal();

            return clicked;
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
