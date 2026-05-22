using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Config.Settings.Sections
{
    /// <summary>
    /// Configures knowledge base and RAG service integration.
    /// </summary>
    public sealed class KnowledgeSettingsSection : SettingsSectionBase
    {
        private const string ApiKeyDisplayKey = "knowledge.apiKeyDisplay";

        /// <inheritdoc />
        public override string Id => "knowledge";

        /// <inheritdoc />
        public override string Title => "Knowledge";

        /// <inheritdoc />
        public override string Description => "Knowledge base and retrieval service settings.";

        /// <inheritdoc />
        public override string Category => "Intelligence";

        /// <inheritdoc />
        public override int Order => 600;

        /// <inheritdoc />
        public override void OnActivate(AgentCoreSettingsContext context)
        {
            context.State.StringValues[ApiKeyDisplayKey] = SecureKeyStorage.HasLightRAGApiKey() ? "••••••••••••" : "(not set)";
        }

        /// <inheritdoc />
        public override void Draw(AgentCoreSettingsContext context)
        {
            EnsureState(context);
            var settings = context.Settings;

            context.Ui.DrawCard("Knowledge Base - LightRAG", "Queries and indexes external project knowledge through a LightRAG-compatible service. Optional.", () =>
            {
                EditorGUI.BeginChangeCheck();

                settings.lightragEnabled = EditorGUILayout.Toggle(
                    new GUIContent("Enabled", "启用 LightRAG 知识库"),
                    settings.lightragEnabled);

                settings.lightragEndpoint = EditorGUILayout.TextField(
                    new GUIContent("Endpoint URL", "LightRAG 服务端点地址"),
                    settings.lightragEndpoint);

                context.Ui.DrawApiKeyRow(
                    "API Key",
                    "LightRAG 服务的 API Key",
                    context.State.StringValues[ApiKeyDisplayKey],
                    "Set LightRAG API Key",
                    "Enter your LightRAG API Key:",
                    newKey =>
                    {
                        SecureKeyStorage.SetLightRAGApiKey(newKey);
                        context.State.StringValues[ApiKeyDisplayKey] = string.IsNullOrEmpty(newKey) ? "(not set)" : "••••••••••••";
                    },
                    () =>
                    {
                        SecureKeyStorage.SetLightRAGApiKey(string.Empty);
                        context.State.StringValues[ApiKeyDisplayKey] = "(not set)";
                    });

                if (EditorGUI.EndChangeCheck())
                {
                    settings.SaveSettings();
                }
            });
        }

        private static void EnsureState(AgentCoreSettingsContext context)
        {
            if (!context.State.StringValues.ContainsKey(ApiKeyDisplayKey))
            {
                context.State.StringValues[ApiKeyDisplayKey] = SecureKeyStorage.HasLightRAGApiKey() ? "••••••••••••" : "(not set)";
            }
        }
    }
}
