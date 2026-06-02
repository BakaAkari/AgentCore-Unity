using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AgentCore.Editor.Config.Settings;
using AgentCore.Editor.Utils;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Config.Settings.Pages
{
    /// <summary>
    /// Model &amp; Agent settings page — core LLM connection and agent runtime behavior.
    /// </summary>
    public sealed class ModelAgentSettingsPage : IAgentCoreSettingsPage
    {
        private const string ApiKeyDisplayKey = "model-agent.apiKeyDisplay";
        private const string FetchStatusKey = "model-agent.fetchStatus";
        private const string TestStatusKey = "model-agent.testStatus";
        private const string FetchRunningKey = "model-agent.fetchRunning";
        private const string TestRunningKey = "model-agent.testRunning";
        private const string AvailableModelsKey = "model-agent.availableModels";

        private readonly ModelSettingsService _service = new ModelSettingsService();

        /// <inheritdoc />
        public string Id => "model-agent";

        /// <inheritdoc />
        public string Title => "Model & Agent";

        /// <inheritdoc />
        public string Description => "Configure the LLM connection and agent runtime behavior.";

        /// <inheritdoc />
        public void OnActivate(AgentCoreSettingsContext context)
        {
            EnsureApiKeyDisplay(context);
        }

        /// <inheritdoc />
        public void OnDeactivate(AgentCoreSettingsContext context) { }

        /// <inheritdoc />
        public void Draw(AgentCoreSettingsContext context)
        {
            EnsureApiKeyDisplay(context);
            var settings = context.Settings;

            // ── Model Connection ──
            context.Ui.DrawCard("Model Connection", "Configure the OpenAI-compatible gateway used by AgentCore.", () =>
            {
                EditorGUI.BeginChangeCheck();

                settings.llmEndpoint = EditorGUILayout.TextField(
                    new GUIContent("Endpoint", "OpenAI-compatible API base URL"),
                    settings.llmEndpoint);

                context.Ui.DrawApiKeyRow(
                    "API Key",
                    "LLM service API key",
                    context.State.StringValues[ApiKeyDisplayKey],
                    "Set API Key",
                    "Enter your LLM API Key:",
                    newKey =>
                    {
                        SecureKeyStorage.SetLLMApiKey(newKey);
                        context.State.StringValues[ApiKeyDisplayKey] = string.IsNullOrEmpty(newKey) ? "(not set)" : "••••••••••••";
                        context.State.ClearStatus(TestStatusKey);
                    },
                    () =>
                    {
                        SecureKeyStorage.SetLLMApiKey(string.Empty);
                        context.State.StringValues[ApiKeyDisplayKey] = "(not set)";
                        context.State.ClearStatus(TestStatusKey);
                    });

                DrawModelSelector(context);

                if (EditorGUI.EndChangeCheck())
                {
                    settings.SaveSettings();
                }

                EditorGUILayout.Space(6);
                DrawConnectionActions(context);
            });

            EditorGUILayout.Space(8);

            // ── Generation ──
            context.Ui.DrawCard("Generation", null, () =>
            {
                EditorGUI.BeginChangeCheck();

                settings.temperature = EditorGUILayout.Slider(
                    new GUIContent("Temperature", "Sampling temperature (0.0–2.0)"),
                    settings.temperature, 0f, 2f);

                settings.maxTokens = EditorGUILayout.IntField(
                    new GUIContent("Max Tokens", "Maximum output tokens per response"),
                    settings.maxTokens);
                settings.maxTokens = Mathf.Clamp(settings.maxTokens, 1, 128000);

                if (EditorGUI.EndChangeCheck())
                {
                    settings.SaveSettings();
                }
            });

            EditorGUILayout.Space(8);

            // ── Agent Runtime ──
            context.Ui.DrawCard("Agent Runtime", null, () =>
            {
                EditorGUI.BeginChangeCheck();

                settings.maxToolCallRounds = EditorGUILayout.IntSlider(
                    new GUIContent("Max Tool Rounds", "Maximum tool-call rounds to prevent infinite loops"),
                    settings.maxToolCallRounds, 1, 50);

                settings.fallbackRoutingEnabled = EditorGUILayout.Toggle(
                    new GUIContent("Fallback Routing", "Enable failure-recovery strategy routing"),
                    settings.fallbackRoutingEnabled);

                if (EditorGUI.EndChangeCheck())
                {
                    settings.SaveSettings();
                }
            });

            EditorGUILayout.Space(8);

            // ── Self Correction ──
            context.Ui.DrawCard("Self Correction", "Safety and recovery switches used during tool execution.", () =>
            {
                EditorGUI.BeginChangeCheck();

                settings.autoCompileCheck = EditorGUILayout.Toggle(
                    new GUIContent("Auto Compile Check", "Automatically compile after script modifications"),
                    settings.autoCompileCheck);

                settings.autoConsoleCapture = EditorGUILayout.Toggle(
                    new GUIContent("Auto Console Capture", "Capture Console errors after each tool round"),
                    settings.autoConsoleCapture);

                settings.maxConsecutiveErrors = EditorGUILayout.IntSlider(
                    new GUIContent("Max Consecutive Errors", "Pause and request user intervention after this many consecutive errors"),
                    settings.maxConsecutiveErrors, 1, 20);

                if (EditorGUI.EndChangeCheck())
                {
                    settings.SaveSettings();
                }
            });
        }

        // ── Model Selector ──

        private void DrawModelSelector(AgentCoreSettingsContext context)
        {
            var settings = context.Settings;
            var models = GetAvailableModels(context);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent("Model", "LLM model name (click Fetch to populate from server)"));

            if (models.Count > 0)
            {
                var currentIndex = models.IndexOf(settings.llmModel);
                if (currentIndex < 0)
                {
                    currentIndex = 0;
                }

                var newIndex = EditorGUILayout.Popup(currentIndex, models.ToArray());
                if (newIndex != currentIndex || settings.llmModel != models[newIndex])
                {
                    settings.llmModel = models[newIndex];
                    settings.SaveSettings();
                }
            }
            else
            {
                settings.llmModel = EditorGUILayout.TextField(settings.llmModel);
            }

            GUI.enabled = !IsRunning(context, FetchRunningKey) && !IsRunning(context, TestRunningKey);
            if (GUILayout.Button(IsRunning(context, FetchRunningKey) ? "..." : "Fetch", GUILayout.Width(56)))
            {
                FetchModels(context);
            }
            GUI.enabled = true;

            EditorGUILayout.EndHorizontal();

            context.Ui.DrawStatusLabel(
                context.State.GetStatusMessage(FetchStatusKey),
                context.State.GetStatusLevel(FetchStatusKey),
                miniLabel: true);
        }

        // ── Connection Actions ──

        private void DrawConnectionActions(AgentCoreSettingsContext context)
        {
            EditorGUILayout.BeginHorizontal();
            GUI.enabled = !IsRunning(context, TestRunningKey);
            if (GUILayout.Button(IsRunning(context, TestRunningKey) ? "Testing..." : "Test Connection", GUILayout.Width(120)))
            {
                TestConnection(context);
            }
            GUI.enabled = true;

            context.Ui.DrawStatusLabel(
                context.State.GetStatusMessage(TestStatusKey),
                context.State.GetStatusLevel(TestStatusKey));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        // ── Async Operations ──

        private void FetchModels(AgentCoreSettingsContext context)
        {
            SetRunning(context, FetchRunningKey, true);
            context.State.SetStatus(FetchStatusKey, "Fetching models...", SettingsStatusLevel.Loading);
            var endpoint = context.Settings.llmEndpoint;
            var apiKey = SecureKeyStorage.GetLLMApiKey();

            AsyncHelper.RunAsync(async () =>
            {
                try
                {
                    var models = await _service.FetchModelsAsync(endpoint, apiKey);
                    AsyncHelper.RunOnMainThread(() =>
                    {
                        context.State.StringValues[AvailableModelsKey] = string.Join("\n", models);
                        context.State.SetStatus(
                            FetchStatusKey,
                            models.Count > 0 ? $"[OK] Found {models.Count} models" : "[OK] No models returned",
                            SettingsStatusLevel.Success);
                        SetRunning(context, FetchRunningKey, false);
                    });
                }
                catch (Exception ex)
                {
                    AsyncHelper.RunOnMainThread(() =>
                    {
                        context.State.SetStatus(FetchStatusKey, $"[FAIL] {ex.Message}", SettingsStatusLevel.Error);
                        SetRunning(context, FetchRunningKey, false);
                    });
                }
            });
        }

        private void TestConnection(AgentCoreSettingsContext context)
        {
            SetRunning(context, TestRunningKey, true);
            context.State.SetStatus(TestStatusKey, "Testing...", SettingsStatusLevel.Loading);
            var endpoint = context.Settings.llmEndpoint;
            var apiKey = SecureKeyStorage.GetLLMApiKey();

            AsyncHelper.RunAsync(async () =>
            {
                try
                {
                    var message = await _service.TestConnectionAsync(endpoint, apiKey);
                    AsyncHelper.RunOnMainThread(() =>
                    {
                        context.State.SetStatus(TestStatusKey, message, SettingsStatusLevel.Success);
                        SetRunning(context, TestRunningKey, false);
                    });
                }
                catch (Exception ex)
                {
                    AsyncHelper.RunOnMainThread(() =>
                    {
                        context.State.SetStatus(TestStatusKey, $"[FAIL] {ex.Message}", SettingsStatusLevel.Error);
                        SetRunning(context, TestRunningKey, false);
                    });
                }
            });
        }

        // ── Helpers ──

        private static void EnsureApiKeyDisplay(AgentCoreSettingsContext context)
        {
            if (!context.State.StringValues.ContainsKey(ApiKeyDisplayKey))
            {
                context.State.StringValues[ApiKeyDisplayKey] = SecureKeyStorage.HasLLMApiKey() ? "••••••••••••" : "(not set)";
            }
        }

        private static List<string> GetAvailableModels(AgentCoreSettingsContext context)
        {
            if (!context.State.StringValues.TryGetValue(AvailableModelsKey, out var raw) || string.IsNullOrWhiteSpace(raw))
            {
                return new List<string>();
            }

            return new List<string>(raw.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries));
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
