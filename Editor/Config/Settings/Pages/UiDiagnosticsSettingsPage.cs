using System;
using System.IO;
using AgentCore.Editor.Bootstrap;
using AgentCore.Editor.Tools;
using AgentCore.Editor.Cloud;
using AgentCore.Editor.Utils;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Config.Settings.Pages
{
    /// <summary>
    /// UI &amp; Diagnostics settings page — chat preferences, diagnostics, and maintenance.
    /// </summary>
    public sealed class UiDiagnosticsSettingsPage : IAgentCoreSettingsPage
    {
        private const string TestLlmRunningKey = "ui-diagnostics.testLlmRunning";
        private const string TestLlmStatusKey = "ui-diagnostics.testLlmStatus";
        private const string TestMem0RunningKey = "ui-diagnostics.testMem0Running";
        private const string TestMem0StatusKey = "ui-diagnostics.testMem0Status";
        private const string TestLightRAGRunningKey = "ui-diagnostics.testLightRAGRunning";
        private const string TestLightRAGStatusKey = "ui-diagnostics.testLightRAGStatus";

        private readonly ModelSettingsService _llmService = new ModelSettingsService();

        /// <inheritdoc />
        public string Id => "ui-diagnostics";

        /// <inheritdoc />
        public string Title => "UI & Diagnostics";

        /// <inheritdoc />
        public string Description => "Chat UI preferences, connection diagnostics, and maintenance actions.";

        /// <inheritdoc />
        public int Order => 600;

        /// <inheritdoc />
        public void OnActivate(AgentCoreSettingsContext context) { }

        /// <inheritdoc />
        public void OnDeactivate(AgentCoreSettingsContext context) { }

        /// <inheritdoc />
        public void Draw(AgentCoreSettingsContext context)
        {
            DrawChatUiCard(context);
            EditorGUILayout.Space(8);
            DrawDiagnosticsCard(context);
            EditorGUILayout.Space(8);
            DrawMaintenanceCard(context);
        }

        // ── Chat UI ──

        private static void DrawChatUiCard(AgentCoreSettingsContext context)
        {
            var settings = context.Settings;

            context.Ui.DrawCard("Chat UI", "Presentation and interaction preferences for the chat window.", () =>
            {
                EditorGUI.BeginChangeCheck();

                settings.streamingEnabled = EditorGUILayout.Toggle(
                    new GUIContent("Streaming Output", "Stream LLM responses token-by-token"),
                    settings.streamingEnabled);

                settings.showToolCallDetails = EditorGUILayout.Toggle(
                    new GUIContent("Show Tool Call Details", "Display detailed tool execution information"),
                    settings.showToolCallDetails);

                if (EditorGUI.EndChangeCheck())
                {
                    settings.SaveSettings();
                }
            });
        }

        // ── Diagnostics ──

        private void DrawDiagnosticsCard(AgentCoreSettingsContext context)
        {
            context.Ui.DrawCard("Diagnostics", "Connection checks and troubleshooting utilities.", () =>
            {
                DrawTestButton(context, "Test LLM", TestLlmRunningKey, TestLlmStatusKey, TestLlmConnection);
                DrawTestButton(context, "Test mem0", TestMem0RunningKey, TestMem0StatusKey, TestMem0Connection);
                DrawTestButton(context, "Test LightRAG", TestLightRAGRunningKey, TestLightRAGStatusKey, TestLightRAGConnection);

                EditorGUILayout.Space(4);

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Refresh Tool Registry", GUILayout.Width(150)))
                {
                    AgentCore.Editor.Tools.Infrastructure.ToolAutoDiscovery.DiscoverAndRegisterAll();
                    Debug.Log("[AgentCore] Tool registry refreshed.");
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Open Logs", GUILayout.Width(150)))
                {
                    var logPath = GetEditorLogPath();
                    if (!string.IsNullOrEmpty(logPath) && File.Exists(logPath))
                    {
                        EditorUtility.OpenWithDefaultApp(logPath);
                    }
                    else
                    {
                        EditorUtility.DisplayDialog("Open Logs", "Could not locate the Unity Editor log file.", "OK");
                    }
                }
                EditorGUILayout.EndHorizontal();
            });
        }

        private static void DrawTestButton(AgentCoreSettingsContext context, string label, string runningKey, string statusKey, Action<AgentCoreSettingsContext> testAction)
        {
            EditorGUILayout.BeginHorizontal();
            GUI.enabled = !IsRunning(context, runningKey);
            if (GUILayout.Button(IsRunning(context, runningKey) ? "Testing..." : label, GUILayout.Width(120)))
            {
                testAction(context);
            }
            GUI.enabled = true;

            context.Ui.DrawStatusLabel(
                context.State.GetStatusMessage(statusKey),
                context.State.GetStatusLevel(statusKey));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        // ── Test Actions ──

        private void TestLlmConnection(AgentCoreSettingsContext context)
        {
            SetRunning(context, TestLlmRunningKey, true);
            context.State.SetStatus(TestLlmStatusKey, "Testing...", SettingsStatusLevel.Loading);
            var endpoint = context.Settings.llmEndpoint;
            var apiKey = SecureKeyStorage.GetLLMApiKey();

            AsyncHelper.RunAsync(async () =>
            {
                try
                {
                    var message = await _llmService.TestConnectionAsync(endpoint, apiKey);
                    AsyncHelper.RunOnMainThread(() =>
                    {
                        context.State.SetStatus(TestLlmStatusKey, message, SettingsStatusLevel.Success);
                        SetRunning(context, TestLlmRunningKey, false);
                    });
                }
                catch (Exception ex)
                {
                    AsyncHelper.RunOnMainThread(() =>
                    {
                        context.State.SetStatus(TestLlmStatusKey, $"[FAIL] {ex.Message}", SettingsStatusLevel.Error);
                        SetRunning(context, TestLlmRunningKey, false);
                    });
                }
            });
        }

        private static void TestMem0Connection(AgentCoreSettingsContext context)
        {
            SetRunning(context, TestMem0RunningKey, true);
            context.State.SetStatus(TestMem0StatusKey, "Testing...", SettingsStatusLevel.Loading);
            var endpoint = context.Settings.mem0Endpoint;
            var apiKey = SecureKeyStorage.GetMem0ApiKey();
            var userId = context.Settings.EffectiveUserId;

            AsyncHelper.RunAsync(async () =>
            {
                try
                {
                    var client = new Mem0Client(endpoint, apiKey, userId);
                    var (success, message) = await client.TestConnectionAsync();
                    AsyncHelper.RunOnMainThread(() =>
                    {
                        context.State.SetStatus(
                            TestMem0StatusKey,
                            success ? $"[OK] {message}" : $"[FAIL] {message}",
                            success ? SettingsStatusLevel.Success : SettingsStatusLevel.Error);
                        SetRunning(context, TestMem0RunningKey, false);
                    });
                }
                catch (Exception ex)
                {
                    AsyncHelper.RunOnMainThread(() =>
                    {
                        context.State.SetStatus(TestMem0StatusKey, $"[FAIL] {ex.Message}", SettingsStatusLevel.Error);
                        SetRunning(context, TestMem0RunningKey, false);
                    });
                }
            });
        }

        private static void TestLightRAGConnection(AgentCoreSettingsContext context)
        {
            SetRunning(context, TestLightRAGRunningKey, true);
            context.State.SetStatus(TestLightRAGStatusKey, "Testing...", SettingsStatusLevel.Loading);
            var endpoint = context.Settings.lightragEndpoint;
            var apiKey = SecureKeyStorage.GetLightRAGApiKey();

            AsyncHelper.RunAsync(async () =>
            {
                try
                {
                    var client = new LightRAGClient(endpoint, apiKey);
                    var success = await client.TestConnectionAsync();
                    AsyncHelper.RunOnMainThread(() =>
                    {
                        context.State.SetStatus(
                            TestLightRAGStatusKey,
                            success ? "[OK] Connected" : "[FAIL] Unhealthy",
                            success ? SettingsStatusLevel.Success : SettingsStatusLevel.Error);
                        SetRunning(context, TestLightRAGRunningKey, false);
                    });
                }
                catch (Exception ex)
                {
                    AsyncHelper.RunOnMainThread(() =>
                    {
                        context.State.SetStatus(TestLightRAGStatusKey, $"[FAIL] {ex.Message}", SettingsStatusLevel.Error);
                        SetRunning(context, TestLightRAGRunningKey, false);
                    });
                }
            });
        }

        // ── Maintenance ──

        private static void DrawMaintenanceCard(AgentCoreSettingsContext context)
        {
            context.Ui.DrawCard("Maintenance", "Reset settings, clear stored keys, and manage context files.", () =>
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Reset Settings", GUILayout.Width(140)))
                {
                    if (EditorUtility.DisplayDialog(
                        "Reset Settings",
                        "This will reset all AgentCore settings to their default values.\n\nThis action cannot be undone.",
                        "Reset",
                        "Cancel"))
                    {
                        context.Settings.ResetToDefaults();
                        Debug.Log("[AgentCore] Settings reset to defaults.");
                    }
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Clear Secure Keys", GUILayout.Width(140)))
                {
                    if (EditorUtility.DisplayDialog(
                        "Clear Secure Keys",
                        "This will clear all stored API keys (LLM, mem0, LightRAG, Compression LLM).\n\nThis action cannot be undone.",
                        "Clear",
                        "Cancel"))
                    {
                        SecureKeyStorage.ClearAll();
                        Debug.Log("[AgentCore] All secure keys cleared.");
                    }
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Context Files", EditorStyles.miniLabel);

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Open PROJECT.md", GUILayout.Width(130)))
                {
                    OpenOrCreateUserFile("PROJECT.md");
                }

                if (GUILayout.Button("Open SOUL.ext.md", GUILayout.Width(130)))
                {
                    OpenOrCreateUserFile("SOUL.ext.md");
                }
                EditorGUILayout.EndHorizontal();
            });
        }

        private static void OpenOrCreateUserFile(string fileName)
        {
            var filePath = BootstrapLoader.FindUserFilePath(fileName);
            if (filePath == null)
            {
                CreateUserFile(fileName);
                filePath = BootstrapLoader.FindUserFilePath(fileName);
            }

            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                System.Diagnostics.Process.Start(filePath);
            }
        }

        private static void CreateUserFile(string fileName)
        {
            var filePath = BootstrapLoader.GetDefaultUserFilePath(fileName);
            if (filePath == null)
            {
                Debug.LogError("[AgentCore] Cannot determine project root directory.");
                return;
            }

            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            try
            {
                File.WriteAllText(filePath, BootstrapLoader.GenerateUserFileTemplate(fileName), System.Text.Encoding.UTF8);
                AssetDatabase.Refresh();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AgentCore] Failed to create {fileName}: {ex.Message}");
            }
        }

        // ── Helpers ──

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

        private static bool IsRunning(AgentCoreSettingsContext context, string key)
        {
            return context.State.RunningTasks.Contains(key);
        }

        private static void SetRunning(AgentCoreSettingsContext context, string key, bool running)
        {
            if (running)
            {
                context.State.RunningTasks.Add(key);
            }
            else
            {
                context.State.RunningTasks.Remove(key);
            }
        }
    }
}
