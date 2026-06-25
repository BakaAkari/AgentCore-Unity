using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AgentCore.Editor.Utils;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Config.Settings.Sections
{
    /// <summary>
    /// Configures the primary LLM model and connection.
    /// </summary>
    public sealed class ModelSettingsSection : SettingsSectionBase
    {
        private const string ApiKeyDisplayKey = "model.apiKeyDisplay";
        private const string FetchStatusKey = "model.fetchStatus";
        private const string TestStatusKey = "model.testStatus";
        private const string FetchRunningKey = "model.fetchRunning";
        private const string TestRunningKey = "model.testRunning";
        private const string AvailableModelsKey = "model.availableModels";

        private readonly ModelSettingsService _service = new ModelSettingsService();

        /// <inheritdoc />
        public override string Id => "model";

        /// <inheritdoc />
        public override string Title => "Model";

        /// <inheritdoc />
        public override string Description => "Primary LLM endpoint, API key, model selection, and connection test.";

        /// <inheritdoc />
        public override string Category => "Core";

        /// <inheritdoc />
        public override int Order => 200;

        /// <inheritdoc />
        public override void OnActivate(AgentCoreSettingsContext context)
        {
            context.State.StringValues[ApiKeyDisplayKey] = SecureKeyStorage.HasLLMApiKey() ? "••••••••••••" : "(not set)";
        }

        /// <inheritdoc />
        public override void Draw(AgentCoreSettingsContext context)
        {
            EnsureState(context);
            var settings = context.Settings;

            context.Ui.DrawCard("LLM Provider", "Configure the OpenAI-compatible gateway used by AgentCore. This is the only required setup section.", () =>
            {
                EditorGUI.BeginChangeCheck();

                settings.llmEndpoint = EditorGUILayout.TextField(
                    new GUIContent("API Endpoint", "OpenAI 兼容 API 端点地址"),
                    settings.llmEndpoint);

                context.Ui.DrawApiKeyRow(
                    "API Key",
                    "LLM 服务的 API Key",
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

                settings.temperature = EditorGUILayout.Slider(
                    new GUIContent("Temperature", "生成温度 (0.0-2.0)"),
                    settings.temperature, 0f, 2f);

                settings.maxTokens = EditorGUILayout.IntField(
                    new GUIContent("Max Tokens", "最大输出 token 数"),
                    settings.maxTokens);
                settings.maxTokens = Mathf.Clamp(settings.maxTokens, 1, 128000);

                if (EditorGUI.EndChangeCheck())
                {
                    settings.SaveSettings();
                }

                EditorGUILayout.Space(6);
                DrawConnectionActions(context);
            });

            EditorGUILayout.Space(8);
            DrawRequestEnrichment(context);
        }

        private void DrawRequestEnrichment(AgentCoreSettingsContext context)
        {
            var settings = context.Settings;

            context.Ui.DrawCard("Request Enrichment", "Configure additional parameters injected into every LLM request. Enables reasoning/thinking chain output from supported models (Claude, DeepSeek, etc.).", () =>
            {
                EditorGUI.BeginChangeCheck();

                settings.enableReasoningOutput = EditorGUILayout.Toggle(
                    new GUIContent("Enable Reasoning Output", "向请求注入 reasoning 参数，触发模型返回思维链（reasoning_content）。支持 OpenRouter、Claude API 等。"),
                    settings.enableReasoningOutput);

                using (new EditorGUI.DisabledGroupScope(!settings.enableReasoningOutput))
                {
                    EditorGUI.indentLevel++;

                    // Reasoning Effort 下拉选择
                    var effortOptions = new[] { "(default)", "low", "medium", "high" };
                    var currentEffort = string.IsNullOrWhiteSpace(settings.reasoningEffort) ? 0
                        : System.Array.IndexOf(effortOptions, settings.reasoningEffort.Trim().ToLowerInvariant());
                    if (currentEffort < 0) currentEffort = 0;

                    var newEffort = EditorGUILayout.Popup(
                        new GUIContent("Reasoning Effort", "推理努力级别。default = 不指定，由模型自行决定。high = 更深入推理但更慢。"),
                        currentEffort, effortOptions);
                    settings.reasoningEffort = newEffort == 0 ? "" : effortOptions[newEffort];

                    settings.reasoningMaxTokens = EditorGUILayout.IntField(
                        new GUIContent("Reasoning Max Tokens", "推理过程最大 token 数。0 = 不限制，由模型决定。"),
                        settings.reasoningMaxTokens);
                    settings.reasoningMaxTokens = Mathf.Max(0, settings.reasoningMaxTokens);

                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Extra Request Body (JSON)", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(
                    "Advanced: raw JSON merged into every LLM request. Use for provider-specific parameters not covered above. Must be a valid JSON object (e.g. {\"top_p\": 0.9}).",
                    MessageType.None);
                settings.extraRequestBody = EditorGUILayout.TextArea(settings.extraRequestBody, GUILayout.MinHeight(48));

                // 验证 JSON 格式
                if (!string.IsNullOrWhiteSpace(settings.extraRequestBody))
                {
                    try
                    {
                        Newtonsoft.Json.Linq.JObject.Parse(settings.extraRequestBody);
                    }
                    catch (Newtonsoft.Json.JsonReaderException)
                    {
                        EditorGUILayout.HelpBox("Invalid JSON format. This will be ignored at runtime.", MessageType.Warning);
                    }
                }

                if (EditorGUI.EndChangeCheck())
                {
                    settings.SaveSettings();
                }
            });
        }

        private void DrawModelSelector(AgentCoreSettingsContext context)
        {
            var settings = context.Settings;
            var models = GetAvailableModels(context);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent("Model", "LLM 模型名称（点击 Fetch 从服务器获取列表，然后从下拉菜单选择）"));

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

        private static void EnsureState(AgentCoreSettingsContext context)
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
