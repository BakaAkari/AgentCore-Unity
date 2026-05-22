using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Config.Settings.Sections
{
    /// <summary>
    /// Configures context budget and compression behavior.
    /// </summary>
    public sealed class ContextManagementSettingsSection : SettingsSectionBase
    {
        private const string CompressionAdvancedFoldoutKey = "context-management.compression-llm";

        /// <inheritdoc />
        public override string Id => "context-management";

        /// <inheritdoc />
        public override string Title => "Context Management";

        /// <inheritdoc />
        public override string Description => "Context budget, response reserve, and compression policy.";

        /// <inheritdoc />
        public override string Category => "Intelligence";

        /// <inheritdoc />
        public override int Order => 700;

        /// <inheritdoc />
        public override void Draw(AgentCoreSettingsContext context)
        {
            var settings = context.Settings;

            context.Ui.DrawCard("Context Budget", "Controls the maximum prompt budget and reserved response tokens.", () =>
            {
                EditorGUI.BeginChangeCheck();

                settings.maxContextTokens = EditorGUILayout.IntField(
                    new GUIContent("Max Context Tokens", "上下文窗口 token 上限（0 = 自动根据模型推断）"),
                    settings.maxContextTokens);
                settings.maxContextTokens = Mathf.Max(settings.maxContextTokens, 0);

                settings.reserveResponseTokens = EditorGUILayout.IntField(
                    new GUIContent("Reserve Response Tokens", "为 AI 回复预留的 token 数"),
                    settings.reserveResponseTokens);
                settings.reserveResponseTokens = Mathf.Clamp(settings.reserveResponseTokens, 500, 16000);

                if (EditorGUI.EndChangeCheck())
                {
                    settings.SaveSettings();
                }
            });

            EditorGUILayout.Space(8);

            context.Ui.DrawCard("Compression", "Intelligently compresses tool results and conversation history instead of truncating them when the context window fills up.", () =>
            {
                EditorGUI.BeginChangeCheck();

                settings.compressionEnabled = EditorGUILayout.Toggle(
                    new GUIContent("Enabled", "启用上下文压缩系统（禁用后将回退到简单截断）"),
                    settings.compressionEnabled);

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Tool Result Compression", EditorStyles.miniLabel);

                settings.toolResultCompressionThreshold = EditorGUILayout.IntField(
                    new GUIContent("Threshold (tokens)", "工具结果超过此 token 数时触发压缩"),
                    settings.toolResultCompressionThreshold);

                settings.toolResultTargetTokens = EditorGUILayout.IntField(
                    new GUIContent("Target (tokens)", "压缩后的目标 token 数"),
                    settings.toolResultTargetTokens);

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Conversation Compression", EditorStyles.miniLabel);

                settings.conversationCompressionTrigger = EditorGUILayout.Slider(
                    new GUIContent("Trigger Ratio", "上下文使用率超过此比例时触发对话压缩 (0.3-0.95)"),
                    settings.conversationCompressionTrigger, 0.3f, 0.95f);

                var advanced = context.State.GetFoldout(CompressionAdvancedFoldoutKey);
                advanced = EditorGUILayout.Foldout(advanced, "Separate Compression LLM", true);
                context.State.SetFoldout(CompressionAdvancedFoldoutKey, advanced);
                if (advanced)
                {
                    EditorGUI.indentLevel++;
                    settings.useSeparateCompressionLLM = EditorGUILayout.Toggle(
                        new GUIContent("Use Separate LLM", "使用独立的 LLM 进行压缩"),
                        settings.useSeparateCompressionLLM);

                    if (settings.useSeparateCompressionLLM)
                    {
                        settings.compressionLLMEndpoint = EditorGUILayout.TextField(
                            new GUIContent("Endpoint", "压缩 LLM API 端点"),
                            settings.compressionLLMEndpoint);

                        settings.compressionLLMModel = EditorGUILayout.TextField(
                            new GUIContent("Model", "压缩 LLM 模型名称"),
                            settings.compressionLLMModel);

                        context.Ui.DrawApiKeyRow(
                            "API Key",
                            "压缩 LLM API Key",
                            SecureKeyStorage.HasCompressionLLMApiKey() ? "••••••••" : "(not set)",
                            "Set Compression LLM API Key",
                            "Enter your compression LLM API key:",
                            SecureKeyStorage.SetCompressionLLMApiKey,
                            () => SecureKeyStorage.SetCompressionLLMApiKey(string.Empty));
                    }

                    EditorGUI.indentLevel--;
                }

                if (EditorGUI.EndChangeCheck())
                {
                    settings.SaveSettings();
                }
            });
        }
    }
}
