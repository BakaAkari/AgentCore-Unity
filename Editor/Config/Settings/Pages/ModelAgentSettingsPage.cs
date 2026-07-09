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
        public int Order => 200;

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

            // ADR-17 极简: 已删除 Agent Runtime 和 Self Correction 两个卡片
            //   - Max Tool Rounds / Token Budget / Fallback Routing → 内部常量 (maxToolCallRounds=200, maxTokenBudget=0, fallbackRoutingEnabled=true)
            //   - Auto Compile Check / Auto Console Capture / Max Consecutive Errors → 默认开启, 用户不需要管理

            // ── Self-Challenge ──
            // v1.5.0-alpha 极简哲学: 一个开关控制整个双节点机制, 内部策略由工程侧决定最优值。
            context.Ui.DrawCard(
                "Self-Challenge",
                "在每次对话时让 Agent 挑战自己对你需求的理解, 输出前再自审一遍。会稍微增加响应时间与 token 用量, 但降低误解和幻觉。默认启用。",
                () =>
                {
                    EditorGUI.BeginChangeCheck();

                    settings.selfChallengeEnabled = EditorGUILayout.Toggle(
                        new GUIContent("Enable Self-Challenge", "启用后 Agent 会在每次对话前挑战自己对需求的理解, 输出前也会自审一遍。关闭则回到不带自挑战的行为。"),
                        settings.selfChallengeEnabled);

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

                // ADR-17: llmModel="auto" 时显示实际选中的模型
                if (settings.llmModel == "auto" && models.Count > 0)
                {
                    var firstNonAuto = models.Find(m => m != "auto");
                    if (!string.IsNullOrEmpty(firstNonAuto))
                    {
                        GUILayout.Label($"→ {firstNonAuto}", EditorStyles.miniLabel, GUILayout.MaxWidth(200));
                    }
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
