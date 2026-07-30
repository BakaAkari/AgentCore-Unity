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
    /// Model &amp; Agent settings page (v1.13.0).
    /// <para>
    /// 单一真源 = Provider Profile。此页提供 profile 的完整管理界面：
    /// 顶部展示当前 active，中部是可折叠的 profile 列表（编辑/切换/复制/删除），
    /// 底部有 "Add Profile" 按钮。首次进入无 profile 时自动创建一个指向本地
    /// GLM 的 Default profile 并异步 fetch 其模型列表。
    /// </para>
    /// <para>
    /// 结构性操作（Add / Delete / Duplicate / SetActive）通过 PendingOps 延迟到
    /// OnGUI 循环结束后统一 apply，避免 IMGUI foreach 中改集合导致的 layout mismatch。
    /// </para>
    /// </summary>
    public sealed class ModelAgentSettingsPage : IAgentCoreSettingsPage
    {
        // ── EditorPrefs / State keys ──
        private const string ApiKeyDisplayKeyPrefix = "model-agent.apiKeyDisplay."; // + profileId
        private const string AvailableModelsKeyPrefix = "model-agent.availableModels."; // + profileId
        private const string FetchStatusKeyPrefix = "model-agent.fetchStatus."; // + profileId
        private const string FetchRunningKeyPrefix = "model-agent.fetchRunning."; // + profileId
        private const string TestStatusKeyPrefix = "model-agent.testStatus."; // + profileId
        private const string TestRunningKeyPrefix = "model-agent.testRunning."; // + profileId
        private const string FoldoutOpenKeyPrefix = "model-agent.foldout."; // + profileId, EditorPrefs bool
        private const string OverridesFoldoutKeyPrefix = "model-agent.overridesFoldout."; // + profileId, EditorPrefs bool

        // ── Default profile 硬编码值（首次自动创建时使用）──
        private const string DefaultProfileName = "Default (Local GLM)";
        private const string DefaultProfileEndpoint = "http://172.16.248.60:8000/v1";
        private const string DefaultProfileApiKeyPlaceholder = "sk-xxx";

        private readonly ModelSettingsService _service = new ModelSettingsService();

        // 结构性操作延迟队列
        private readonly List<Action> _pendingOps = new List<Action>();

        // 记录哪些 profile 已经触发过一次首次 auto-fetch，避免每帧重复触发
        private readonly HashSet<string> _autoFetchTriggered = new HashSet<string>();

        // ── IAgentCoreSettingsPage ──

        public string Id => "model-agent";
        public string Title => "Model & Agent";
        public string Description => "Manage LLM Provider Profiles and agent runtime behavior.";
        public int Order => 200;

        public void OnActivate(AgentCoreSettingsContext context)
        {
            EnsureDefaultProfileIfEmpty(context);
        }

        public void OnDeactivate(AgentCoreSettingsContext context) { }

        public void Draw(AgentCoreSettingsContext context)
        {
            var store = AgentCoreProviderProfiles.instance;

            // 每帧检查：如果列表被外部清空（例如用户手动删了 asset），重新自动建
            EnsureDefaultProfileIfEmpty(context);

            DrawActiveProfileCard(context, store);
            EditorGUILayout.Space(8);
            DrawProfileList(context, store);
            EditorGUILayout.Space(6);
            DrawAddProfileButton(context, store);

            EditorGUILayout.Space(12);
            DrawSelfChallengeCard(context);

            // ── 应用延迟操作 ──
            if (_pendingOps.Count > 0)
            {
                foreach (var op in _pendingOps)
                {
                    try { op?.Invoke(); }
                    catch (Exception ex) { AgentCoreLog.Warning($"[AgentCore] Pending settings op failed: {ex.Message}"); }
                }
                _pendingOps.Clear();
                GUIUtility.ExitGUI(); // 让 IMGUI 用干净的下一帧渲染
            }
        }

        // ── 首次自动建 Default profile ──

        private void EnsureDefaultProfileIfEmpty(AgentCoreSettingsContext context)
        {
            var store = AgentCoreProviderProfiles.instance;
            if (store.Profiles != null && store.Profiles.Count > 0)
                return;

            var p = ProviderProfile.Create(DefaultProfileName);
            p.endpoint = DefaultProfileEndpoint;
            p.modelName = ""; // 稍后自动 fetch 取第一个
            store.AddProfile(p);
            SecureKeyStorage.SetProfileApiKey(p.id, DefaultProfileApiKeyPlaceholder);
            store.SetActive(p.id);

            // 触发一次异步 fetch 挑第一个模型
            _ = TryAutoSelectFirstModelAsync(p.id, context);
        }

        private async Task TryAutoSelectFirstModelAsync(string profileId, AgentCoreSettingsContext context)
        {
            if (_autoFetchTriggered.Contains(profileId))
                return;
            _autoFetchTriggered.Add(profileId);

            var store = AgentCoreProviderProfiles.instance;
            var p = store.FindById(profileId);
            if (p == null) return;

            try
            {
                var models = await _service.FetchModelsAsync(p.endpoint, SecureKeyStorage.GetProfileApiKey(profileId));
                if (models != null && models.Count > 0)
                {
                    AsyncHelper.RunOnMainThread(() =>
                    {
                        // 只在当前 modelName 仍为空时才自动填，避免覆盖用户手动改动
                        var current = store.FindById(profileId);
                        if (current != null && string.IsNullOrEmpty(current.modelName))
                        {
                            store.UpdateProfile(profileId, x => x.modelName = models[0]);
                        }
                        context.State.StringValues[AvailableModelsKeyPrefix + profileId] = string.Join("\n", models);
                        context.State.SetStatus(FetchStatusKeyPrefix + profileId,
                            $"[OK] Auto-selected '{models[0]}' from {models.Count} models",
                            SettingsStatusLevel.Success);
                    });
                }
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] Auto-fetch models for '{profileId}' failed: {ex.Message}");
            }
        }

        // ── 顶部：Active Profile 卡片 ──

        private void DrawActiveProfileCard(AgentCoreSettingsContext context, AgentCoreProviderProfiles store)
        {
            context.Ui.DrawCard("Active Profile", "The LLM provider currently used by AgentCore.", () =>
            {
                var active = store.GetActive();
                if (active == null && (store.Profiles == null || store.Profiles.Count == 0))
                {
                    EditorGUILayout.HelpBox(
                        "No active Provider Profile. AgentCore requests will throw until one is selected.",
                        MessageType.Warning);
                    return;
                }

                // 单一下拉：当前选中项即 active。选择即立即切换（同一 session 内实时生效，无需重启/新建 session）。
                var names = new List<string>();
                var ids = new List<string>();
                int currentIndex = 0;
                for (int i = 0; i < store.Profiles.Count; i++)
                {
                    var pp = store.Profiles[i];
                    if (pp == null) continue;
                    names.Add(string.IsNullOrEmpty(pp.displayName) ? "(unnamed)" : pp.displayName);
                    ids.Add(pp.id);
                    if (active != null && pp.id == active.id) currentIndex = names.Count - 1;
                }

                if (names.Count == 0)
                {
                    EditorGUILayout.HelpBox(
                        "No active Provider Profile. AgentCore requests will throw until one is selected.",
                        MessageType.Warning);
                    return;
                }

                int newIndex = EditorGUILayout.Popup(new GUIContent("Active Profile", "Select the Provider Profile to use. Takes effect immediately."), currentIndex, names.ToArray());
                if (newIndex != currentIndex && newIndex >= 0 && newIndex < ids.Count)
                {
                    var targetId = ids[newIndex];
                    _pendingOps.Add(() => store.SetActive(targetId));
                }
            });
        }

        // ── 中部：Profile 列表 ──

        private void DrawProfileList(AgentCoreSettingsContext context, AgentCoreProviderProfiles store)
        {
            EditorGUILayout.LabelField("Provider Profiles", EditorStyles.boldLabel);

            // 快照拷贝，避免 foreach 中集合被 pendingOps 改
            var snapshot = new List<ProviderProfile>(store.Profiles);
            foreach (var profile in snapshot)
            {
                if (profile == null) continue;
                DrawProfileFoldout(context, store, profile);
            }
        }

        private void DrawProfileFoldout(AgentCoreSettingsContext context, AgentCoreProviderProfiles store, ProviderProfile profile)
        {
            var foldoutKey = FoldoutOpenKeyPrefix + profile.id;
            bool isOpen = EditorPrefs.GetBool(foldoutKey, false);
            bool isActive = store.ActiveProfileId == profile.id;

            var headerLabel = string.IsNullOrEmpty(profile.displayName) ? "(unnamed)" : profile.displayName;
            if (isActive) headerLabel += "   ● Active";

            EditorGUILayout.BeginVertical(GUI.skin.box);
            bool newOpen = EditorGUILayout.Foldout(isOpen, headerLabel, true, EditorStyles.foldoutHeader);
            if (newOpen != isOpen)
                EditorPrefs.SetBool(foldoutKey, newOpen);

            if (newOpen)
            {
                EditorGUI.indentLevel++;
                DrawProfileBody(context, store, profile, isActive);
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawProfileBody(AgentCoreSettingsContext context, AgentCoreProviderProfiles store, ProviderProfile profile, bool isActive)
        {
            // --- Display Name ---
            EditorGUI.BeginChangeCheck();
            var newDisplayName = EditorGUILayout.TextField("Display Name", profile.displayName ?? "");
            if (EditorGUI.EndChangeCheck())
            {
                store.UpdateProfile(profile.id, p => p.displayName = newDisplayName);
            }

            // --- Endpoint ---
            EditorGUI.BeginChangeCheck();
            var newEndpoint = EditorGUILayout.TextField(new GUIContent("Endpoint", "OpenAI-compatible API base URL"), profile.endpoint ?? "");
            if (EditorGUI.EndChangeCheck())
            {
                store.UpdateProfile(profile.id, p => p.endpoint = newEndpoint);
            }

            // --- API Key ---
            var apiKeyDisplayKey = ApiKeyDisplayKeyPrefix + profile.id;
            if (!context.State.StringValues.ContainsKey(apiKeyDisplayKey))
            {
                context.State.StringValues[apiKeyDisplayKey] =
                    SecureKeyStorage.HasProfileApiKey(profile.id) ? "••••••••••••" : "(not set)";
            }

            context.Ui.DrawApiKeyRow(
                "API Key",
                "LLM service API key (stored in EditorPrefs, not committed to git)",
                context.State.StringValues[apiKeyDisplayKey],
                "Set API Key",
                $"Enter API Key for '{profile.displayName}':",
                newKey =>
                {
                    SecureKeyStorage.SetProfileApiKey(profile.id, newKey);
                    context.State.StringValues[apiKeyDisplayKey] =
                        string.IsNullOrEmpty(newKey) ? "(not set)" : "••••••••••••";
                    context.State.ClearStatus(TestStatusKeyPrefix + profile.id);
                },
                () =>
                {
                    SecureKeyStorage.SetProfileApiKey(profile.id, string.Empty);
                    context.State.StringValues[apiKeyDisplayKey] = "(not set)";
                    context.State.ClearStatus(TestStatusKeyPrefix + profile.id);
                });

            // --- Model Selector ---
            DrawModelSelector(context, store, profile);

            // --- Connection actions ---
            EditorGUILayout.Space(4);
            DrawConnectionActions(context, profile);

            // --- Overrides 折叠区 ---
            EditorGUILayout.Space(6);
            var overridesKey = OverridesFoldoutKeyPrefix + profile.id;
            bool overridesOpen = EditorPrefs.GetBool(overridesKey, false);
            bool newOverridesOpen = EditorGUILayout.Foldout(overridesOpen, "Overrides (advanced)", true);
            if (newOverridesOpen != overridesOpen)
                EditorPrefs.SetBool(overridesKey, newOverridesOpen);
            if (newOverridesOpen)
            {
                DrawOverridesSection(store, profile);
            }

            // --- 底部操作行 ---
            EditorGUILayout.Space(6);
            EditorGUILayout.BeginHorizontal();

            bool prevEnabled = GUI.enabled;
            GUI.enabled = !isActive;
            if (GUILayout.Button(isActive ? "Active" : "Set as Active", GUILayout.Width(120)))
            {
                var targetId = profile.id;
                _pendingOps.Add(() => store.SetActive(targetId));
            }
            GUI.enabled = prevEnabled;

            if (GUILayout.Button("Duplicate", GUILayout.Width(90)))
            {
                var srcId = profile.id;
                var srcName = profile.displayName;
                _pendingOps.Add(() => DuplicateProfile(store, srcId, srcName));
            }

            GUILayout.FlexibleSpace();

            var origColor = GUI.color;
            GUI.color = new Color(1f, 0.6f, 0.6f);
            if (GUILayout.Button("Delete", GUILayout.Width(80)))
            {
                var pid = profile.id;
                var pname = profile.displayName;
                if (EditorUtility.DisplayDialog(
                    "Delete Profile",
                    $"Delete '{pname}'? Its stored API key will also be removed.",
                    "Delete",
                    "Cancel"))
                {
                    _pendingOps.Add(() => store.RemoveProfile(pid));
                }
            }
            GUI.color = origColor;

            EditorGUILayout.EndHorizontal();
        }

        // ── Overrides Section ──

        private void DrawOverridesSection(AgentCoreProviderProfiles store, ProviderProfile profile)
        {
            EditorGUI.indentLevel++;

            // Temperature
            EditorGUI.BeginChangeCheck();
            var overrideTemp = EditorGUILayout.Toggle(new GUIContent("Override Temperature", "If off, uses global default from AgentCoreSettings."), profile.overrideTemperature);
            float tempVal = profile.temperature;
            if (overrideTemp)
            {
                tempVal = EditorGUILayout.Slider("Temperature", tempVal, 0f, 2f);
            }
            if (EditorGUI.EndChangeCheck())
            {
                store.UpdateProfile(profile.id, p =>
                {
                    p.overrideTemperature = overrideTemp;
                    if (overrideTemp) p.temperature = tempVal;
                });
            }

            // MaxTokens
            EditorGUI.BeginChangeCheck();
            var overrideMax = EditorGUILayout.Toggle("Override MaxTokens", profile.overrideMaxTokens);
            int maxVal = profile.maxTokens;
            if (overrideMax)
            {
                maxVal = EditorGUILayout.IntField("Max Tokens", maxVal);
            }
            if (EditorGUI.EndChangeCheck())
            {
                store.UpdateProfile(profile.id, p =>
                {
                    p.overrideMaxTokens = overrideMax;
                    if (overrideMax) p.maxTokens = maxVal;
                });
            }

            // Reasoning
            EditorGUI.BeginChangeCheck();
            var overrideReason = EditorGUILayout.Toggle("Override Reasoning", profile.overrideReasoning);
            string effortVal = profile.reasoningEffort ?? "";
            int reasonMaxVal = profile.reasoningMaxTokens;
            bool enableReasoningOut = profile.enableReasoningOutput;
            if (overrideReason)
            {
                var effortOptions = new[] { "", "low", "medium", "high" };
                int effortIdx = Array.IndexOf(effortOptions, effortVal);
                if (effortIdx < 0) effortIdx = 0;
                effortIdx = EditorGUILayout.Popup("Reasoning Effort", effortIdx, effortOptions);
                effortVal = effortOptions[effortIdx];
                reasonMaxVal = EditorGUILayout.IntField("Reasoning MaxTokens", reasonMaxVal);
                enableReasoningOut = EditorGUILayout.Toggle("Enable Reasoning Output", enableReasoningOut);
            }
            if (EditorGUI.EndChangeCheck())
            {
                store.UpdateProfile(profile.id, p =>
                {
                    p.overrideReasoning = overrideReason;
                    if (overrideReason)
                    {
                        p.reasoningEffort = effortVal;
                        p.reasoningMaxTokens = reasonMaxVal;
                        p.enableReasoningOutput = enableReasoningOut;
                    }
                });
            }

            // ExtraRequestBody
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.LabelField(new GUIContent("Extra Request Body (JSON)", "Empty = fallthrough to global default. Non-empty = merged into request body."));
            var newExtra = EditorGUILayout.TextArea(profile.extraRequestBody ?? "", GUILayout.MinHeight(40));
            if (EditorGUI.EndChangeCheck())
            {
                store.UpdateProfile(profile.id, p => p.extraRequestBody = newExtra);
            }

            EditorGUI.indentLevel--;
        }

        // ── Model Selector（per-profile 缓存）──

        private void DrawModelSelector(AgentCoreSettingsContext context, AgentCoreProviderProfiles store, ProviderProfile profile)
        {
            var models = GetAvailableModels(context, profile.id);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent("Model", "LLM model name (click Refresh to fetch from server)"));

            if (models.Count > 0)
            {
                var currentIndex = models.IndexOf(profile.modelName ?? "");
                if (currentIndex < 0) currentIndex = 0;

                var newIndex = EditorGUILayout.Popup(currentIndex, models.ToArray());
                if (newIndex != currentIndex || profile.modelName != models[newIndex])
                {
                    var pickedModel = models[newIndex];
                    store.UpdateProfile(profile.id, p => p.modelName = pickedModel);
                }
            }
            else
            {
                EditorGUI.BeginChangeCheck();
                var newModel = EditorGUILayout.TextField(profile.modelName ?? "");
                if (EditorGUI.EndChangeCheck())
                {
                    store.UpdateProfile(profile.id, p => p.modelName = newModel);
                }
            }

            var fetchRunningKey = FetchRunningKeyPrefix + profile.id;
            GUI.enabled = !IsRunning(context, fetchRunningKey);
            if (GUILayout.Button(IsRunning(context, fetchRunningKey) ? "..." : "Refresh", GUILayout.Width(72)))
            {
                FetchModels(context, profile);
            }
            GUI.enabled = true;

            EditorGUILayout.EndHorizontal();

            context.Ui.DrawStatusLabel(
                context.State.GetStatusMessage(FetchStatusKeyPrefix + profile.id),
                context.State.GetStatusLevel(FetchStatusKeyPrefix + profile.id),
                miniLabel: true);
        }

        // ── Connection Actions ──

        private void DrawConnectionActions(AgentCoreSettingsContext context, ProviderProfile profile)
        {
            var testRunningKey = TestRunningKeyPrefix + profile.id;
            EditorGUILayout.BeginHorizontal();
            GUI.enabled = !IsRunning(context, testRunningKey);
            if (GUILayout.Button(IsRunning(context, testRunningKey) ? "Testing..." : "Test Connection", GUILayout.Width(140)))
            {
                TestConnection(context, profile);
            }
            GUI.enabled = true;

            context.Ui.DrawStatusLabel(
                context.State.GetStatusMessage(TestStatusKeyPrefix + profile.id),
                context.State.GetStatusLevel(TestStatusKeyPrefix + profile.id));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        // ── 底部：Add Profile ──

        private void DrawAddProfileButton(AgentCoreSettingsContext context, AgentCoreProviderProfiles store)
        {
            if (GUILayout.Button("+ Add Profile", GUILayout.Height(24)))
            {
                var name = EditorInputDialog.Show("Add Profile", "New profile display name:", "New Profile");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _pendingOps.Add(() =>
                    {
                        var p = ProviderProfile.Create(name);
                        store.AddProfile(p);
                        // 新建 profile 默认打开其 foldout, 方便用户立即编辑
                        EditorPrefs.SetBool(FoldoutOpenKeyPrefix + p.id, true);
                    });
                }
            }
        }

        // ── Self-Challenge 卡片（v1.5.0+ 保留）──

        private void DrawSelfChallengeCard(AgentCoreSettingsContext context)
        {
            var settings = context.Settings;
            context.Ui.DrawCard(
                "Self-Challenge",
                "Have the agent challenge its own understanding before responding and self-review before output. Slightly increases latency and token usage but reduces misunderstanding and hallucination. Enabled by default.",
                () =>
                {
                    EditorGUI.BeginChangeCheck();

                    settings.selfChallengeEnabled = EditorGUILayout.Toggle(
                        new GUIContent("Enable Self-Challenge", "When enabled, the agent challenges its understanding of your request before acting and self-reviews before output."),
                        settings.selfChallengeEnabled);

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

        // ── 结构性操作实现 ──

        private void DuplicateProfile(AgentCoreProviderProfiles store, string sourceId, string sourceName)
        {
            var src = store.FindById(sourceId);
            if (src == null) return;

            var copy = ProviderProfile.Create($"{sourceName} (copy)");
            copy.endpoint = src.endpoint;
            copy.modelName = src.modelName;
            copy.overrideTemperature = src.overrideTemperature;
            copy.temperature = src.temperature;
            copy.overrideMaxTokens = src.overrideMaxTokens;
            copy.maxTokens = src.maxTokens;
            copy.overrideReasoning = src.overrideReasoning;
            copy.reasoningEffort = src.reasoningEffort;
            copy.reasoningMaxTokens = src.reasoningMaxTokens;
            copy.enableReasoningOutput = src.enableReasoningOutput;
            copy.extraRequestBody = src.extraRequestBody;

            store.AddProfile(copy);
            // 一并复制 apiKey，让副本可直接使用
            var srcKey = SecureKeyStorage.GetProfileApiKey(sourceId);
            if (!string.IsNullOrEmpty(srcKey))
                SecureKeyStorage.SetProfileApiKey(copy.id, srcKey);
            EditorPrefs.SetBool(FoldoutOpenKeyPrefix + copy.id, true);
        }

        // ── Async Fetch / Test ──

        private void FetchModels(AgentCoreSettingsContext context, ProviderProfile profile)
        {
            var runningKey = FetchRunningKeyPrefix + profile.id;
            var statusKey = FetchStatusKeyPrefix + profile.id;
            SetRunning(context, runningKey, true);
            context.State.SetStatus(statusKey, "Fetching models...", SettingsStatusLevel.Loading);

            var endpoint = profile.endpoint;
            var apiKey = SecureKeyStorage.GetProfileApiKey(profile.id);
            var profileId = profile.id;

            AsyncHelper.RunAsync(async () =>
            {
                try
                {
                    var models = await _service.FetchModelsAsync(endpoint, apiKey);
                    AsyncHelper.RunOnMainThread(() =>
                    {
                        context.State.StringValues[AvailableModelsKeyPrefix + profileId] =
                            string.Join("\n", models ?? new List<string>());
                        context.State.SetStatus(
                            statusKey,
                            (models != null && models.Count > 0) ? $"[OK] Found {models.Count} models" : "[OK] No models returned",
                            SettingsStatusLevel.Success);
                        SetRunning(context, runningKey, false);
                    });
                }
                catch (Exception ex)
                {
                    AsyncHelper.RunOnMainThread(() =>
                    {
                        context.State.SetStatus(statusKey, $"[FAIL] {ex.Message}", SettingsStatusLevel.Error);
                        SetRunning(context, runningKey, false);
                    });
                }
            });
        }

        private void TestConnection(AgentCoreSettingsContext context, ProviderProfile profile)
        {
            var runningKey = TestRunningKeyPrefix + profile.id;
            var statusKey = TestStatusKeyPrefix + profile.id;
            SetRunning(context, runningKey, true);
            context.State.SetStatus(statusKey, "Testing...", SettingsStatusLevel.Loading);

            var endpoint = profile.endpoint;
            var apiKey = SecureKeyStorage.GetProfileApiKey(profile.id);

            AsyncHelper.RunAsync(async () =>
            {
                try
                {
                    var message = await _service.TestConnectionAsync(endpoint, apiKey);
                    AsyncHelper.RunOnMainThread(() =>
                    {
                        context.State.SetStatus(statusKey, message, SettingsStatusLevel.Success);
                        SetRunning(context, runningKey, false);
                    });
                }
                catch (Exception ex)
                {
                    AsyncHelper.RunOnMainThread(() =>
                    {
                        context.State.SetStatus(statusKey, $"[FAIL] {ex.Message}", SettingsStatusLevel.Error);
                        SetRunning(context, runningKey, false);
                    });
                }
            });
        }

        // ── Helpers ──

        private static List<string> GetAvailableModels(AgentCoreSettingsContext context, string profileId)
        {
            var key = AvailableModelsKeyPrefix + profileId;
            if (!context.State.StringValues.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
                return new List<string>();
            return new List<string>(raw.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private static bool IsRunning(AgentCoreSettingsContext context, string key)
            => context.State.RunningTasks.Contains(key);

        private static void SetRunning(AgentCoreSettingsContext context, string key, bool running)
        {
            if (running) context.State.RunningTasks.Add(key);
            else context.State.RunningTasks.Remove(key);
        }
    }
}
