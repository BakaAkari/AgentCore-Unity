using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Config.Settings.Sections
{
    /// <summary>
    /// Configures Agent runtime behavior and recovery policy.
    /// </summary>
    public sealed class AgentSettingsSection : SettingsSectionBase
    {
        private const string AdvancedFoldoutKey = "agent.advanced";

        /// <inheritdoc />
        public override string Id => "agent";

        /// <inheritdoc />
        public override string Title => "Agent";

        /// <inheritdoc />
        public override string Description => "Runtime behavior, safety checks, and self-correction policy.";

        /// <inheritdoc />
        public override string Category => "Core";

        /// <inheritdoc />
        public override int Order => 300;

        /// <inheritdoc />
        public override void Draw(AgentCoreSettingsContext context)
        {
            var settings = context.Settings;

            context.Ui.DrawCard("Self-Correction", "Common safety and recovery switches used during tool execution and script editing.", () =>
            {
                EditorGUI.BeginChangeCheck();

                settings.autoCompileCheck = EditorGUILayout.Toggle(
                    new GUIContent("Auto Compile Check", "脚本修改后自动编译检查"),
                    settings.autoCompileCheck);

                settings.autoConsoleCapture = EditorGUILayout.Toggle(
                    new GUIContent("Auto Console Capture", "每轮工具执行后自动捕获 Console 错误"),
                    settings.autoConsoleCapture);

                settings.fallbackRoutingEnabled = EditorGUILayout.Toggle(
                    new GUIContent("Fallback Routing", "启用工具失败恢复策略路由"),
                    settings.fallbackRoutingEnabled);

                if (EditorGUI.EndChangeCheck())
                {
                    settings.SaveSettings();
                }
            });

            EditorGUILayout.Space(8);

            context.Ui.DrawCard("Advanced Limits", "Limits that protect the agent from runaway tool loops and repeated failures.", () =>
            {
                var expanded = context.State.GetFoldout(AdvancedFoldoutKey);
                expanded = EditorGUILayout.Foldout(expanded, "Show advanced limits", true);
                context.State.SetFoldout(AdvancedFoldoutKey, expanded);
                if (!expanded)
                {
                    return;
                }

                EditorGUI.indentLevel++;
                EditorGUI.BeginChangeCheck();

                settings.maxToolCallRounds = EditorGUILayout.IntSlider(
                    new GUIContent("Max Tool Rounds", "硬安全上限（Token 预算是主要限制器）"),
                    settings.maxToolCallRounds, 1, 200);

                settings.maxTokenBudget = EditorGUILayout.IntField(
                    new GUIContent("Token Budget", "每轮工具循环的最大 token 消耗量（0 = 不限制）"),
                    settings.maxTokenBudget);
                settings.maxTokenBudget = Mathf.Max(0, settings.maxTokenBudget);

                settings.maxConsecutiveErrors = EditorGUILayout.IntSlider(
                    new GUIContent("Max Consecutive Errors", "连续错误上限"),
                    settings.maxConsecutiveErrors, 1, 20);

                if (EditorGUI.EndChangeCheck())
                {
                    settings.SaveSettings();
                }

                EditorGUI.indentLevel--;
            });
        }
    }
}
