using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Config.Settings.Sections
{
    /// <summary>
    /// Configures AgentCore interface preferences.
    /// </summary>
    public sealed class InterfaceSettingsSection : SettingsSectionBase
    {
        /// <inheritdoc />
        public override string Id => "interface";

        /// <inheritdoc />
        public override string Title => "Interface";

        /// <inheritdoc />
        public override string Description => "Chat window presentation and interaction preferences.";

        /// <inheritdoc />
        public override string Category => "Interface";

        /// <inheritdoc />
        public override int Order => 1000;

        /// <inheritdoc />
        public override void Draw(AgentCoreSettingsContext context)
        {
            var settings = context.Settings;
            context.Ui.DrawCard("Chat Presentation", "Controls how the chat window presents streaming responses and tool execution details.", () =>
            {
                EditorGUI.BeginChangeCheck();

                settings.streamingEnabled = EditorGUILayout.Toggle(
                    new GUIContent("Streaming", "启用流式输出（逐字显示）"),
                    settings.streamingEnabled);

                settings.showToolCallDetails = EditorGUILayout.Toggle(
                    new GUIContent("Show Tool Details", "显示工具调用详情"),
                    settings.showToolCallDetails);

                if (EditorGUI.EndChangeCheck())
                {
                    settings.SaveSettings();
                }
            });
        }
    }
}
