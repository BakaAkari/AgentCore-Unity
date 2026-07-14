using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AgentCore.Editor.Config.Settings;
using AgentCore.Editor.Core;
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

            // ── Model Info (v1.6.5+: 自适应，不再暴露 temperature/maxTokens) ──
            context.Ui.DrawCard("Model Info", null, () =>
            {
                var probeReady = ModelCapabilityProbe.IsProbeCompleted;
                var maxLen = ModelCapabilityProbe.CachedMaxModelLen;
                var probeModel = ModelCapabilityProbe.CachedModelId;

                if (probeReady && maxLen > 0)
                {
                    EditorGUILayout.LabelField("Context Window", $"{maxLen:N0} tokens");
                    if (!string.IsNullOrEmpty(probeModel))
                        EditorGUILayout.LabelField("Detected Model", probeModel);

                    // 显示自适应计算结果 — 显示 effective 值 (content + reasoning)
                    var s = AgentCoreSettings.instance;
                    EditorGUILayout.LabelField("Max Output Tokens", $"{s.GetEffectiveMaxTokens():N0}");
                    if (s.enableReasoningOutput && s.reasoningMaxTokens > 0)
                    {
                        EditorGUILayout.LabelField("  Content", $"{s.maxTokens:N0}", EditorStyles.miniLabel);
                        EditorGUILayout.LabelField("  Reasoning", $"{s.reasoningMaxTokens:N0}", EditorStyles.miniLabel);
                    }
                    EditorGUILayout.LabelField("Reserve Tokens", $"{s.reserveResponseTokens:N0}");
                }
                else if (probeReady)
                {
                    EditorGUILayout.HelpBox(
                        "Model capability probe completed but server did not return max_model_len. Using fallback values from model prefix table.",
                        MessageType.Info);
                }
                else
                {
                    EditorGUILayout.LabelField("Context Window", "Detecting...");
                }
            });

            EditorGUILayout.Space(8);

            // ADR-17: Removed Agent Runtime and Self Correction cards
            //   - Max Tool Rounds / Token Budget → internal constants (maxToolCallRounds=200, maxTokenBudget=0)
            //   - Self-correction thresholds → internal constants with optimal defaults

            // ── Self-Challenge ──
            // v1.5.0-alpha minimalism: one toggle controls the whole dual-node mechanism; internal policies use best defaults.
            context.Ui.DrawCard(
                "Self-Challenge",
                "Have the agent challenge its own understanding before responding and self-review before output. Slightly increases latency and token usage but reduces misunderstanding and hallucination. Enabled by default.",
                () =>
                {
                    EditorGUI.BeginChangeCheck();

                    settings.selfChallengeEnabled = EditorGUILayout.Toggle(
                        new GUIContent("Enable Self-Challenge", "When enabled, the agent challenges its understanding of your request before acting and self-reviews before output."),
                        settings.selfChallengeEnabled);

                    // ADR: self-challenge-model-tier-escape — 高级模型自动跳过(灰醒:总开关关闭时无意义)
                    bool prevEnabled = GUI.enabled;
                    GUI.enabled = settings.selfChallengeEnabled;
                    settings.selfChallengeEscapeEnabled = EditorGUILayout.Toggle(
                        new GUIContent("Skip for Advanced Models", "Advanced models with native reasoning (e.g. Claude Opus, o-series, GPT-5) skip Self-Challenge to avoid duplicate thinking cost."),
                        settings.selfChallengeEscapeEnabled);
                    GUI.enabled = prevEnabled;

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
                        GUILayout.Label($"-> {firstNonAuto}", EditorStyles.miniLabel, GUILayout.MaxWidth(200));
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
