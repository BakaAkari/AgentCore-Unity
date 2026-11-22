using System;
using System.IO;
using System.Linq;
using AgentCore.Editor.Extensions;
using AgentCore.Editor.L10n;
using AgentCore.Editor.Tools;
using AgentCore.Editor.UI;
using AgentCore.Editor.Utils;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Config.Settings.Pages
{
    /// <summary>
    /// Dashboard page showing setup status, quick actions, and package info.
    /// </summary>
    public sealed class DashboardSettingsPage : IAgentCoreSettingsPage
    {
        /// <inheritdoc />
        public string Id => "dashboard";

        /// <inheritdoc />
        public string Title => Loc.Tr("settings.dashboard.title", "Dashboard");

        /// <inheritdoc />
        public string Description => Loc.Tr(
            "settings.dashboard.description",
            "Overview of AgentCore configuration status and quick actions.");

        /// <inheritdoc />
        public int Order => 100;

        /// <inheritdoc />
        public void OnActivate(AgentCoreSettingsContext context) { }

        /// <inheritdoc />
        public void OnDeactivate(AgentCoreSettingsContext context) { }

        /// <inheritdoc />
        public void Draw(AgentCoreSettingsContext context)
        {
            // v1.4.2: package info merged into the bottom of Setup Status to save a full card.
            DrawSetupStatusCard(context);
            EditorGUILayout.Space(8);
            DrawLanguageCard(context);
            EditorGUILayout.Space(8);
            DrawQuickActionsCard(context);
            EditorGUILayout.Space(8);
            DrawLogVerbosityCard(context);
        }

        /// <summary>
        /// v1.9.0+: 语言 (Language) 设置卡片.
        /// UI 语言用 EditorPrefs 全局持久化, 独立于 AgentCoreSettings.
        /// </summary>
        private static void DrawLanguageCard(AgentCoreSettingsContext context)
        {
            context.Ui.DrawCard(
                AgentCore.Editor.L10n.Loc.Tr("settings.language.card.title", "Language"),
                AgentCore.Editor.L10n.Loc.Tr(
                    "settings.language.card.description",
                    "UI language for the AgentCore editor plugin. Stored globally across projects. Switching applies immediately to all AgentCore windows."),
                () =>
                {
                    var supported = AgentCore.Editor.L10n.LanguageManager.SupportedLanguages;

                    // 构建下拉选项 (display name)
                    var displayNames = new string[supported.Count];
                    var codes = new string[supported.Count];
                    var current = AgentCore.Editor.L10n.LanguageManager.CurrentLanguage;
                    int currentIndex = 0;
                    for (int i = 0; i < supported.Count; i++)
                    {
                        displayNames[i] = supported[i].DisplayName;
                        codes[i] = supported[i].Code;
                        if (string.Equals(supported[i].Code, current, StringComparison.OrdinalIgnoreCase))
                            currentIndex = i;
                    }

                    var newIndex = EditorGUILayout.Popup(
                        new GUIContent(
                            AgentCore.Editor.L10n.Loc.Tr("settings.language.field.label", "Interface language")),
                        currentIndex,
                        displayNames);
                    if (newIndex != currentIndex && newIndex >= 0 && newIndex < codes.Length)
                    {
                        AgentCore.Editor.L10n.LanguageManager.SetLanguage(codes[newIndex]);
                        AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] UI language changed to {codes[newIndex]}");
                    }

                    EditorGUILayout.Space(4);

                    // LLM 语言跟随开关
                    var currentFollow = AgentCore.Editor.L10n.LanguageManager.LlmFollowUiLanguage;
                    var newFollow = EditorGUILayout.ToggleLeft(
                        new GUIContent(
                            AgentCore.Editor.L10n.Loc.Tr("settings.language.llmFollow.label", "LLM replies follow UI language"),
                            AgentCore.Editor.L10n.Loc.Tr(
                                "settings.language.llmFollow.tooltip",
                                "When enabled, the assistant is instructed to reply in the same language as the UI. Disable to let the model decide by user input.")),
                        currentFollow);
                    if (newFollow != currentFollow)
                    {
                        AgentCore.Editor.L10n.LanguageManager.LlmFollowUiLanguage = newFollow;
                    }
                });
        }

        /// <summary>
        /// v1.6.5+: 日志详细程度下拉菜单。
        /// </summary>
        private static void DrawLogVerbosityCard(AgentCoreSettingsContext context)
        {
            context.Ui.DrawCard(
                Loc.Tr("settings.dashboard.logCard.title", "Log Verbosity"),
                Loc.Tr(
                    "settings.dashboard.logCard.description",
                    "Controls the verbosity of [AgentCore]-prefixed logs. If heavy logging during responses lags the Editor, switch to Warning or Error."),
                () =>
                {
                    var settings = context.Settings;
                    var current = settings.logLevel;
                    var newLevel = (AgentCore.Editor.Utils.LogLevel)EditorGUILayout.EnumPopup(
                        new GUIContent(
                            Loc.Tr("settings.dashboard.logLevel", "Log Level"),
                            Loc.Tr(
                                "settings.dashboard.logLevelTooltip",
                                "Silent: fully silent (use with caution) · Error: errors only · Warning: default, warnings + errors · Info: key business events · Debug: everything, incl. streaming token / per-event high-frequency logs")),
                        current);
                    if (newLevel != current)
                    {
                        settings.logLevel = newLevel;
                        settings.SaveSettings();
                        AgentCore.Editor.Utils.AgentCoreLog.Invalidate();
                        AgentCoreLog.Info($"[AgentCore] Log level changed: {current} -> {newLevel}");
                    }
                });
        }

        private static void DrawSetupStatusCard(AgentCoreSettingsContext context)
        {
            var settings = context.Settings;

            context.Ui.DrawCard(
                Loc.Tr("settings.dashboard.setup.title", "Setup Status"),
                Loc.Tr("settings.dashboard.setup.description", "Current configuration state for core services."),
                () =>
            {
                // LLM — required, so missing active profile is an Error.
                DrawServiceStatusRow(context,
                    Loc.Tr("settings.dashboard.service.llm", "LLM"),
                    isEnabled: ActiveModelConfig.IsUsingProfile,
                    enabledDetail: ActiveModelConfig.IsUsingProfile ? ActiveModelConfig.ModelName : "",
                    disabledIsError: true);

                // Memory (mem0) — optional.
                DrawServiceStatusRow(context,
                    Loc.Tr("settings.dashboard.service.memory", "Memory"),
                    isEnabled: settings.mem0Enabled,
                    enabledDetail: settings.mem0Endpoint,
                    disabledIsError: false);

                // Knowledge (LightRAG) — optional.
                DrawServiceStatusRow(context,
                    Loc.Tr("settings.dashboard.service.knowledge", "Knowledge"),
                    isEnabled: settings.lightragEnabled,
                    enabledDetail: settings.lightragEndpoint,
                    disabledIsError: false);

                // VCS — optional component.
                DrawServiceStatusRow(context,
                    Loc.Tr("settings.dashboard.service.vcs", "VCS"),
                    isEnabled: OptionalComponentManager.IsVcsEnabled(),
                    enabledDetail: null,
                    disabledIsError: false);

                // Tools — informational count.
                var allTools = ToolRegistry.Instance.GetAllTools();
                if (allTools != null && allTools.Count > 0)
                {
                    var enabledCount = allTools.Count(tool =>
                        tool?.Metadata != null && !settings.IsToolDisabled(tool.Metadata.Name, tool.Metadata.Category));
                    context.Ui.DrawStatusLabel(
                        Loc.Tr("settings.dashboard.toolsEnabled", "Tools: {0}/{1} enabled", enabledCount, allTools.Count),
                        SettingsStatusLevel.None,
                        miniLabel: true);
                }
                else
                {
                    context.Ui.DrawStatusLabel(
                        Loc.Tr("settings.dashboard.toolsNotInitialized", "Tools: not initialized yet"),
                        SettingsStatusLevel.Warning,
                        miniLabel: true);
                }

                // Package footer — merged in from the removed Package card.
                EditorGUILayout.Space(6);
                var footerStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleLeft,
                };
                footerStyle.normal.textColor = new Color(0.5f, 0.5f, 0.5f);
                EditorGUILayout.LabelField($"com.agentcore.unity  v{GetPackageVersion()}", footerStyle);
            });
        }

        /// <summary>
        /// Draws a single status row with a leading badge + service name + optional detail.
        /// Disabled services are dimmed (gray) and clearly labeled instead of using the default label color.
        /// </summary>
        private static void DrawServiceStatusRow(
            AgentCoreSettingsContext context,
            string serviceName,
            bool isEnabled,
            string enabledDetail,
            bool disabledIsError)
        {
            string text;
            SettingsStatusLevel level;

            if (isEnabled)
            {
                text = string.IsNullOrEmpty(enabledDetail)
                    ? Loc.Tr("settings.dashboard.status.enabled", "[ON]  {0}: Enabled", serviceName)
                    : Loc.Tr("settings.dashboard.status.enabledDetail", "[ON]  {0}: Enabled ({1})", serviceName, enabledDetail);
                level = SettingsStatusLevel.Success;
            }
            else if (disabledIsError)
            {
                text = Loc.Tr("settings.dashboard.status.missing", "[FAIL] {0}: Missing configuration", serviceName);
                level = SettingsStatusLevel.Error;
            }
            else
            {
                text = Loc.Tr("settings.dashboard.status.disabled", "[OFF] {0}: Disabled", serviceName);
                level = SettingsStatusLevel.None;
            }

            context.Ui.DrawStatusLabel(text, level, miniLabel: true);
        }

        private static void DrawQuickActionsCard(AgentCoreSettingsContext context)
        {
            // 所有 Quick Actions 按钮统一宽度，避免每行按钮宽度不一致（此前混用 140/150）
            const float ButtonWidth = 150f;

            context.Ui.DrawCard(
                Loc.Tr("settings.dashboard.quick.title", "Quick Actions"),
                Loc.Tr("settings.dashboard.quick.description", "Common shortcuts and maintenance actions."),
                () =>
            {
                EditorGUILayout.BeginHorizontal();

                if (GUILayout.Button(Loc.Tr("settings.dashboard.quick.openChat", "Open Chat Window"), GUILayout.Width(ButtonWidth)))
                {
                    ChatWindow.ShowWindow();
                }

                if (GUILayout.Button(Loc.Tr("settings.dashboard.quick.refreshTools", "Refresh Tool Registry"), GUILayout.Width(ButtonWidth)))
                {
                    AgentCore.Editor.Tools.Infrastructure.ToolAutoDiscovery.DiscoverAndRegisterAll();
                    AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] Tool registry refreshed.");
                }

                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();

                if (GUILayout.Button(Loc.Tr("settings.dashboard.quick.openLogs", "Open Logs"), GUILayout.Width(ButtonWidth)))
                {
                    var logPath = GetEditorLogPath();
                    if (!string.IsNullOrEmpty(logPath) && File.Exists(logPath))
                    {
                        EditorUtility.OpenWithDefaultApp(logPath);
                    }
                    else
                    {
                        EditorUtility.DisplayDialog(
                            Loc.Tr("settings.dashboard.quick.openLogs", "Open Logs"),
                            Loc.Tr("settings.dashboard.logs.notFound", "Could not locate the Unity Editor log file."),
                            Loc.Tr("common.ok", "OK"));
                    }
                }

                if (GUILayout.Button(Loc.Tr("settings.dashboard.reset.button", "Reset Settings"), GUILayout.Width(ButtonWidth)))
                {
                    if (EditorUtility.DisplayDialog(
                        Loc.Tr("settings.dashboard.reset.title", "Reset Settings"),
                        Loc.Tr("settings.dashboard.reset.message", "This will reset all AgentCore settings to their default values.\n\nThis action cannot be undone."),
                        Loc.Tr("settings.dashboard.reset.confirm", "Reset"),
                        Loc.Tr("common.cancel", "Cancel")))
                    {
                        context.Settings.ResetToDefaults();
                        AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] Settings reset to defaults.");
                    }
                }

                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();

                if (GUILayout.Button(Loc.Tr("settings.dashboard.clearKeys.button", "Clear Secure Keys"), GUILayout.Width(ButtonWidth)))
                {
                    if (EditorUtility.DisplayDialog(
                        Loc.Tr("settings.dashboard.clearKeys.title", "Clear Secure Keys"),
                        Loc.Tr("settings.dashboard.clearKeys.message", "This will clear all stored API keys (LLM, mem0, LightRAG).\n\nThis action cannot be undone."),
                        Loc.Tr("settings.dashboard.clearKeys.confirm", "Clear"),
                        Loc.Tr("common.cancel", "Cancel")))
                    {
                        SecureKeyStorage.ClearAll();
                        AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] All secure keys cleared.");
                    }
                }

                if (GUILayout.Button(Loc.Tr("settings.dashboard.clearRules.button", "Clear Learned Rules"), GUILayout.Width(ButtonWidth)))
                {
                    if (EditorUtility.DisplayDialog(
                        Loc.Tr("settings.dashboard.clearRules.title", "Clear Learned Request Rules"),
                        Loc.Tr(
                            "settings.dashboard.clearRules.message",
                            "This will forget all auto-learned request field restrictions (e.g. which endpoints reject 'reasoning' or 'temperature').\n\n" +
                            "Each affected model will re-learn on its next call (one recoverable 400 retry)."),
                        Loc.Tr("settings.dashboard.clearRules.confirm", "Clear"),
                        Loc.Tr("common.cancel", "Cancel")))
                    {
                        AgentCore.Editor.LLM.RequestPruningRegistry.ClearAll();
                    }
                }

                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            });
        }

        private static string GetEditorLogPath()
        {
            var os = SystemInfo.operatingSystemFamily;
            if (os == OperatingSystemFamily.Windows)
            {
                var localAppData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(localAppData, "Unity", "Editor", "Editor.log");
            }
            else if (os == OperatingSystemFamily.MacOSX)
            {
                var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal);
                return Path.Combine(home, "Library", "Logs", "Unity", "Editor.log");
            }
            else
            {
                var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal);
                return Path.Combine(home, ".config", "unity3d", "Editor.log");
            }
        }

        private static string GetPackageVersion()
        {
            try
            {
                var packagePath = "Packages/com.agentcore.unity/package.json";
                if (File.Exists(packagePath))
                {
                    var json = File.ReadAllText(packagePath);
                    var jobj = JsonHelper.ParseObject(json);
                    return JsonHelper.GetString(jobj, "version", "unknown");
                }

                var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(AgentCoreSettings).Assembly);
                if (packageInfo != null)
                {
                    return packageInfo.version;
                }
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] Failed to read package version: {ex.Message}");
            }

            return "unknown";
        }
    }
}
