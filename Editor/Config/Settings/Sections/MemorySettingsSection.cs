using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Config.Settings.Sections
{
    /// <summary>
    /// Configures long-term memory service integration.
    /// </summary>
    public sealed class MemorySettingsSection : SettingsSectionBase
    {
        private const string ApiKeyDisplayKey = "memory.apiKeyDisplay";

        /// <inheritdoc />
        public override string Id => "memory";

        /// <inheritdoc />
        public override string Title => "Memory";

        /// <inheritdoc />
        public override string Description => "Long-term memory service settings.";

        /// <inheritdoc />
        public override string Category => "Intelligence";

        /// <inheritdoc />
        public override int Order => 500;

        /// <inheritdoc />
        public override void OnActivate(AgentCoreSettingsContext context)
        {
            context.State.StringValues[ApiKeyDisplayKey] = SecureKeyStorage.HasMem0ApiKey() ? "••••••••••••" : "(not set)";
        }

        /// <inheritdoc />
        public override void Draw(AgentCoreSettingsContext context)
        {
            EnsureState(context);
            var settings = context.Settings;

            context.Ui.DrawCard("Memory Service - mem0", "Stores cross-session user and project memory in a mem0-compatible service. Optional.", () =>
            {
                EditorGUI.BeginChangeCheck();

                settings.mem0Enabled = EditorGUILayout.Toggle(
                    new GUIContent("Enabled", "启用 mem0 记忆服务"),
                    settings.mem0Enabled);

                settings.mem0Endpoint = EditorGUILayout.TextField(
                    new GUIContent("Endpoint URL", "mem0 服务端点地址"),
                    settings.mem0Endpoint);

                context.Ui.DrawApiKeyRow(
                    "API Key",
                    "mem0 服务的 API Key",
                    context.State.StringValues[ApiKeyDisplayKey],
                    "Set mem0 API Key",
                    "Enter your mem0 API Key:",
                    newKey =>
                    {
                        SecureKeyStorage.SetMem0ApiKey(newKey);
                        context.State.StringValues[ApiKeyDisplayKey] = string.IsNullOrEmpty(newKey) ? "(not set)" : "••••••••••••";
                    },
                    () =>
                    {
                        SecureKeyStorage.SetMem0ApiKey(string.Empty);
                        context.State.StringValues[ApiKeyDisplayKey] = "(not set)";
                    });

                GUI.enabled = false;
                EditorGUILayout.TextField(
                    new GUIContent("User ID", "系统自动生成的唯一用户标识（用于 mem0 记忆隔离）"),
                    settings.EffectiveUserId);
                GUI.enabled = true;

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Auto Memory", EditorStyles.miniLabel);

                settings.autoMemoryEnabled = EditorGUILayout.Toggle(
                    new GUIContent("Enabled", "会话结束时自动提取关键信息存入 mem0"),
                    settings.autoMemoryEnabled);

                settings.autoMemoryMinTurns = EditorGUILayout.IntSlider(
                    new GUIContent("Min Turns", "触发自动记忆的最小用户对话轮次"),
                    settings.autoMemoryMinTurns, 1, 20);

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
                context.State.StringValues[ApiKeyDisplayKey] = SecureKeyStorage.HasMem0ApiKey() ? "••••••••••••" : "(not set)";
            }
        }
    }
}
