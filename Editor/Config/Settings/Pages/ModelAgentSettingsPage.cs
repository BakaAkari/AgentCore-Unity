using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCore.Editor.Config.Settings;
using AgentCore.Editor.LLM;
using AgentCore.Editor.L10n;
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
        private const string AdvancedFoldoutKeyPrefix = "model-agent.advancedFoldout."; // + profileId, EditorPrefs bool

        // v1.14.2: Default profile 硬编码值（名称/endpoint/apiKey）已下沉至
        // AgentCoreProviderProfiles.EnsureDefaultProfileIfEmpty（数据层单一入口）。
        // 此处不再重复定义，避免两份硬编码值漂移。

        private readonly ModelSettingsService _service = new ModelSettingsService();

        // 结构性操作延迟队列
        private readonly List<Action> _pendingOps = new List<Action>();

        // 记录哪些 profile 已经触发过一次首次 auto-fetch，避免每帧重复触发
        private readonly HashSet<string> _autoFetchTriggered = new HashSet<string>();

        // ── Vision Model 卡片状态（独立单一配置，不参与 profile context 机制）──
        private readonly List<string> _visionModels = new List<string>();
        private bool _visionFetchRunning;
        private string _visionFetchStatus;
        private bool _visionTestRunning;
        private string _visionTestStatus;
        private bool _visionApiKeyDirty;
        private string _visionApiKeyInput;

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

            EditorGUILayout.Space(12);
            DrawVisionModelCard(context);

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
            bool created = store.EnsureDefaultProfileIfEmpty();
            if (!created)
                return;

            var active = store.GetActive();
            if (active == null)
                return;

            // 触发一次异步 fetch 挑第一个模型（UI 层职责，数据层不做网络调用）
            _ = TryAutoSelectFirstModelAsync(active.id, context);
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
                var models = await _service.FetchModelsAsync(p, SecureKeyStorage.GetProfileApiKey(profileId));
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
            context.Ui.DrawCard(Loc.Tr("settings.model.activeProfile", "当前模型配置"), "The LLM provider currently used by AgentCore.", () =>
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
                    names.Add(string.IsNullOrEmpty(pp.displayName) ? Loc.Tr("settings.model.unnamed", "（未命名）") : pp.displayName);
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

                int newIndex = EditorGUILayout.Popup(new GUIContent(Loc.Tr("settings.model.activeProfile", "当前模型配置"), Loc.Tr("settings.model.activeHint", "选择使用的模型配置，立即生效。")), currentIndex, names.ToArray());
                if (newIndex != currentIndex && newIndex >= 0 && newIndex < ids.Count)
                {
                    var targetId = ids[newIndex];
                    _pendingOps.Add(() => store.SetActive(targetId));
                }

                // v1.17.0: 辅助任务（对话压缩 / 自动记忆）可指定更便宜的 profile
                var utilNames = new List<string> { "(same as active)" };
                utilNames.AddRange(names);
                int utilIndex = Math.Max(0, ids.IndexOf(store.UtilityProfileId) + 1);
                int newUtilIndex = EditorGUILayout.Popup(
                    new GUIContent(Loc.Tr("settings.model.utility", "辅助任务模型"), Loc.Tr("settings.model.utilityHint", "用于对话压缩、自动记忆等辅助任务，可选择成本更低或速度更快的模型。")),
                    utilIndex, utilNames.ToArray());
                if (newUtilIndex != utilIndex)
                {
                    var utilId = newUtilIndex == 0 ? "" : ids[newUtilIndex - 1];
                    _pendingOps.Add(() => store.SetUtility(utilId));
                }

                // v1.17.0: 会话用量 / 缓存命中 / 估算费用（来自供应商返回的 usage）
                EditorGUILayout.LabelField(Loc.Tr("settings.model.usage", "会话用量"), AgentCore.Editor.LLM.UsageLedger.FormatSessionSummary(), EditorStyles.wordWrappedMiniLabel);
            });
        }

        // ── 中部：Profile 列表 ──

        private void DrawProfileList(AgentCoreSettingsContext context, AgentCoreProviderProfiles store)
        {
            EditorGUILayout.LabelField(Loc.Tr("settings.model.profiles", "模型配置"), EditorStyles.boldLabel);

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

            var headerLabel = string.IsNullOrEmpty(profile.displayName) ? Loc.Tr("settings.model.unnamed", "（未命名）") : profile.displayName;
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
            var newDisplayName = EditorGUILayout.TextField(Loc.Tr("settings.model.displayName", "显示名称"), profile.displayName ?? "");
            if (EditorGUI.EndChangeCheck())
            {
                store.UpdateProfile(profile.id, p => p.displayName = newDisplayName);
            }

            // --- Endpoint ---
            EditorGUI.BeginChangeCheck();
            var newEndpoint = EditorGUILayout.TextField(new GUIContent(Loc.Tr("settings.model.endpoint", "服务地址"), Loc.Tr("settings.model.endpointHint", "模型服务的接口基础地址")), profile.endpoint ?? "");
            if (EditorGUI.EndChangeCheck())
            {
                store.UpdateProfile(profile.id, p => p.endpoint = newEndpoint);
            }

            var format = DrawStringPopup(
                new GUIContent(Loc.Tr("settings.model.format", "接口格式"), Loc.Tr("settings.model.formatHint", "首次连接时自动探测未知网关，并记住验证成功的协议。手动选择优先。验证使用简短请求，可能产生少量费用。")),
                profile.apiFormat, ApiFormatOptions);
            if (format != null) store.UpdateProfile(profile.id, p => p.apiFormat = format);

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
                    store.UpdateProfile(profile.id, p => { p.verifiedConnection = null; p.verifiedCredential = null; p.connectionFailed = false; });
                    context.State.StringValues[apiKeyDisplayKey] =
                        string.IsNullOrEmpty(newKey) ? "(not set)" : "••••••••••••";
                    context.State.ClearStatus(TestStatusKeyPrefix + profile.id);
                },
                () =>
                {
                    SecureKeyStorage.SetProfileApiKey(profile.id, string.Empty);
                    store.UpdateProfile(profile.id, p => { p.verifiedConnection = null; p.verifiedCredential = null; p.connectionFailed = false; });
                    context.State.StringValues[apiKeyDisplayKey] = "(not set)";
                    context.State.ClearStatus(TestStatusKeyPrefix + profile.id);
                });

            // --- Model Selector ---
            DrawModelSelector(context, store, profile);

            // --- Connection actions ---
            EditorGUILayout.Space(4);
            DrawConnectionActions(context, profile);

            // v1.17.0: Connection details and overrides stay out of the default setup flow.
            EditorGUILayout.Space(6);
            var advancedKey = AdvancedFoldoutKeyPrefix + profile.id;
            bool advancedOpen = EditorPrefs.GetBool(advancedKey, false);
            bool newAdvancedOpen = EditorGUILayout.Foldout(advancedOpen,
                Loc.Tr("settings.model.advanced", "高级设置"), true);
            if (newAdvancedOpen != advancedOpen)
                EditorPrefs.SetBool(advancedKey, newAdvancedOpen);
            if (newAdvancedOpen)
            {
                EditorGUI.indentLevel++;
                DrawProtocolSection(store, profile);
                EditorGUILayout.Space(6);
                DrawOverridesSection(store, profile);
                EditorGUI.indentLevel--;
            }

            // --- 底部操作行 ---
            EditorGUILayout.Space(6);
            EditorGUILayout.BeginHorizontal();

            bool prevEnabled = GUI.enabled;
            GUI.enabled = !isActive;
            if (GUILayout.Button(isActive ? Loc.Tr("settings.model.active", "当前使用") : Loc.Tr("settings.model.activate", "设为当前"), GUILayout.Width(120)))
            {
                var targetId = profile.id;
                _pendingOps.Add(() => store.SetActive(targetId));
            }
            GUI.enabled = prevEnabled;

            if (GUILayout.Button(Loc.Tr("settings.model.duplicate", "复制"), GUILayout.Width(90)))
            {
                var srcId = profile.id;
                var srcName = profile.displayName;
                _pendingOps.Add(() => DuplicateProfile(store, srcId, srcName));
            }

            GUILayout.FlexibleSpace();

            var origColor = GUI.color;
            GUI.color = new Color(1f, 0.6f, 0.6f);
            if (GUILayout.Button(Loc.Tr("settings.model.delete", "删除"), GUILayout.Width(80)))
            {
                var pid = profile.id;
                var pname = profile.displayName;
                if (EditorUtility.DisplayDialog(
                    "Delete Profile",
                    $"Delete '{pname}'? Its stored API key will also be removed.",
                    Loc.Tr("settings.model.delete", "删除"),
                    "Cancel"))
                {
                    _pendingOps.Add(() => store.RemoveProfile(pid));
                }
            }
            GUI.color = origColor;

            EditorGUILayout.EndHorizontal();
        }

        // ── v1.17.0: Protocol Section ──

        private static readonly string[] ApiFormatOptions = { ApiFormats.Auto, ApiFormats.OpenAI, ApiFormats.Anthropic };
        private static readonly string[] DialectOptions =
        {
            OpenAIDialects.Auto, OpenAIDialects.Generic, OpenAIDialects.OpenAI, OpenAIDialects.DeepSeek,
            OpenAIDialects.OpenRouter, OpenAIDialects.LiteLLM, OpenAIDialects.Vllm
        };
        private static readonly string[] AuthSchemeOptions = { AuthSchemes.Auto, AuthSchemes.Bearer, AuthSchemes.XApiKey };
        private static readonly string[] CacheTtlOptions = { "5m", "1h" };

        private void DrawProtocolSection(AgentCoreProviderProfiles store, ProviderProfile profile)
        {
            var pid = profile.id;

            var resolvedFormat = ActiveModelConfig.ResolveApiFormat(profile);
            if (resolvedFormat == ApiFormats.Anthropic)
            {
                var ttl = DrawStringPopup(
                    new GUIContent(Loc.Tr("settings.model.cacheTtl", "缓存保留时间"), Loc.Tr("settings.model.cacheTtlHint", "提示词缓存的保留时间。延长保留时间可能增加缓存写入费用，适合对话间隔较长的场景。")),
                    string.IsNullOrEmpty(profile.cacheTtl) ? "5m" : profile.cacheTtl, CacheTtlOptions);
                if (ttl != null) store.UpdateProfile(pid, p => p.cacheTtl = ttl);
            }
            else
            {
                var dialect = DrawStringPopup(
                    new GUIContent(Loc.Tr("settings.model.dialect", "兼容模式"), Loc.Tr("settings.model.dialectHint", "处理服务端的兼容差异。默认自动识别；特殊网关可手动选择对应模式。")),
                    profile.dialect, DialectOptions);
                if (dialect != null) store.UpdateProfile(pid, p => p.dialect = dialect);

                var resolvedDialect = ActiveModelConfig.ResolveDialect(profile);
                if (ModelRegistry.GetFamily(profile.modelName) == ModelFamily.Anthropic
                    && resolvedDialect != OpenAIDialects.OpenRouter && resolvedDialect != OpenAIDialects.LiteLLM)
                {
                    EditorGUILayout.HelpBox(
                        Loc.Tr("settings.model.cacheWarning", "此网关的缓存行为尚未确认。请根据实际缓存用量判断，不要仅凭协议推断是否命中。"),
                        MessageType.Warning);
                }
            }

            var auth = DrawStringPopup(
                new GUIContent(Loc.Tr("settings.model.auth", "认证方式"), Loc.Tr("settings.model.authHint", "自动记住连接验证成功的认证方式。仅在网关有特殊要求时手动指定。")),
                profile.authScheme, AuthSchemeOptions);
            if (auth != null) store.UpdateProfile(pid, p => p.authScheme = auth);

            DrawFallbackList(store, profile);
        }

        /// <summary>绘制字符串下拉；值改变时返回新值，否则返回 null。未知值显示为第一个选项。</summary>
        private static string DrawStringPopup(GUIContent label, string current, string[] options)
        {
            int idx = Math.Max(0, Array.IndexOf(options, string.IsNullOrEmpty(current) ? options[0] : current));
            var labels = options.Select(option => option == "auto" ? Loc.Tr("settings.model.auto", "自动")
                : option == "generic" ? Loc.Tr("settings.model.generic", "通用")
                : option == "5m" ? Loc.Tr("settings.model.fiveMinutes", "5 分钟")
                : option == "1h" ? Loc.Tr("settings.model.oneHour", "1 小时") : option).ToArray();
            int newIdx = EditorGUILayout.Popup(label, idx, labels);
            return newIdx != idx ? options[newIdx] : null;
        }

        private void DrawFallbackList(AgentCoreProviderProfiles store, ProviderProfile profile)
        {
            var pid = profile.id;
            var current = profile.fallbackProfileIds ?? new List<string>();

            string NameOf(string id) => store.Profiles.FirstOrDefault(p => p?.id == id)?.displayName ?? Loc.Tr("settings.model.missing", "（已缺失）");

            EditorGUILayout.LabelField(
                new GUIContent(Loc.Tr("settings.model.fallback", "备用模型配置"), Loc.Tr("settings.model.fallbackHint", "当前配置重试后仍失败时，按顺序尝试备用配置。")),
                new GUIContent(current.Count == 0 ? Loc.Tr("settings.model.none", "（无）") : string.Join(" -> ", current.Select(NameOf))));

            EditorGUI.indentLevel++;
            for (int i = 0; i < current.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"{i + 1}. {NameOf(current[i])}");
                int removeAt = i;
                if (GUILayout.Button(Loc.Tr("settings.model.remove", "移除"), GUILayout.Width(70)))
                    _pendingOps.Add(() => store.UpdateProfile(pid, p => p.fallbackProfileIds?.RemoveAt(removeAt)));
                EditorGUILayout.EndHorizontal();
            }

            var candidates = store.Profiles.Where(p => p != null && p.id != pid && !current.Contains(p.id)).ToList();
            if (candidates.Count > 0)
            {
                var labels = new List<string> { Loc.Tr("settings.model.addFallback", "添加备用模型…") };
                labels.AddRange(candidates.Select(p => string.IsNullOrEmpty(p.displayName) ? Loc.Tr("settings.model.unnamed", "（未命名）") : p.displayName));
                int pick = EditorGUILayout.Popup(" ", 0, labels.ToArray());
                if (pick > 0)
                {
                    var addId = candidates[pick - 1].id;
                    _pendingOps.Add(() => store.UpdateProfile(pid, p => (p.fallbackProfileIds ??= new List<string>()).Add(addId)));
                }
            }
            EditorGUI.indentLevel--;
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
            EditorGUILayout.PrefixLabel(new GUIContent(Loc.Tr("settings.model.model", "模型"), Loc.Tr("settings.model.modelHint", "模型名称，点击刷新可从服务端获取。")));

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
            if (GUILayout.Button(IsRunning(context, fetchRunningKey) ? "..." : Loc.Tr("settings.model.refresh", "刷新"), GUILayout.Width(72)))
            {
                FetchModels(context, profile);
            }
            GUI.enabled = true;

            EditorGUILayout.EndHorizontal();
        }

        // ── Connection Actions ──

        private void DrawConnectionActions(AgentCoreSettingsContext context, ProviderProfile profile)
        {
            var testRunningKey = TestRunningKeyPrefix + profile.id;
            EditorGUILayout.BeginHorizontal();
            GUI.enabled = !IsRunning(context, testRunningKey);
            if (GUILayout.Button(IsRunning(context, testRunningKey) ? Loc.Tr("settings.model.testing", "正在测试…") : Loc.Tr("settings.model.test", "测试连接"), GUILayout.Width(140)))
            {
                TestConnection(context, profile);
            }
            GUI.enabled = true;

            // 刷新 + 测试状态统一显示在 Test Connection 按钮右侧（与 vision 卡片同款：内联、不换行、不占上下布局）
            context.Ui.DrawStatusLabel(
                context.State.GetStatusMessage(FetchStatusKeyPrefix + profile.id),
                context.State.GetStatusLevel(FetchStatusKeyPrefix + profile.id),
                miniLabel: true);
            context.Ui.DrawStatusLabel(
                LocalizeConnectionStatus(context.State.GetStatusMessage(TestStatusKeyPrefix + profile.id)),
                context.State.GetStatusLevel(TestStatusKeyPrefix + profile.id),
                miniLabel: true);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private static string LocalizeConnectionStatus(string message)
        {
            if (string.IsNullOrEmpty(message)) return message;
            bool failed = message.StartsWith("[FAIL] ", StringComparison.Ordinal);
            var detail = failed ? message.Substring(7) : message;
            string key = detail switch
            {
                "No model profile configured." => "settings.model.connection.noProfile",
                "Select a model before verifying the connection." => "settings.model.connection.selectModel",
                "Enter a valid HTTP(S) endpoint." => "settings.model.connection.invalidUrl",
                "Connection not verified. Check the endpoint, model and API key, or select API Format manually." => "settings.model.connection.unverified",
                "Testing..." => "settings.model.testing",
                _ => detail
            };
            var text = Loc.Tr(key, detail);
            var match = System.Text.RegularExpressions.Regex.Match(detail,
                @"^Connection not verified: service returned HTTP (\d{3})\. Retry later\.$");
            if (match.Success)
                text = Loc.Tr("settings.model.connection.serviceError", "服务返回 HTTP {0}，连接尚未验证，请稍后重试。", match.Groups[1].Value);
            return failed ? Loc.Tr("settings.model.connection.failed", "连接失败：{0}", text) : text;
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
            copy.apiFormat = src.apiFormat;
            copy.dialect = src.dialect;
            copy.cacheTtl = src.cacheTtl;
            copy.authScheme = src.authScheme;
            copy.fallbackProfileIds = src.fallbackProfileIds == null ? new List<string>() : new List<string>(src.fallbackProfileIds);
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
                    var models = await _service.FetchModelsAsync(profile, apiKey);
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
            context.State.SetStatus(statusKey, "settings.model.testing", SettingsStatusLevel.Loading);

            var endpoint = profile.endpoint;
            var apiKey = SecureKeyStorage.GetProfileApiKey(profile.id);

            AsyncHelper.RunAsync(async () =>
            {
                try
                {
                    var message = await _service.TestConnectionAsync(profile, apiKey);
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

        // ── Vision Model 卡片（v1.15.0：独立单一配置，不可热切换）──

        private void DrawVisionModelCard(AgentCoreSettingsContext context)
        {
            var settings = context.Settings;
            context.Ui.DrawCard(
                "Vision Model",
                "Optional vision-capable model (independent of the main model profile). Lets the agent capture Game/Scene View and get a text description to self-correct its visual work. Disabled by default. Not hot-swappable — a single fixed configuration save and apply.",
                () =>
                {
                    EditorGUI.BeginChangeCheck();

                    bool wasEnabled = settings.visionEnabled;
                    settings.visionEnabled = EditorGUILayout.Toggle(
                        new GUIContent("Enable Vision Model", "When enabled, vision_analyze tool is available to the agent (capture Game/Scene View → vision model text description)."),
                        settings.visionEnabled);

                    // 启用瞬间 false→true 且 model 仍空 → 自动 fetch baseURL 列表取第一个模型。
                    // （v1.15.2 起不写死模型名，取服务端第一个；见 VisionModelConfig.EnsureDefaultWithAutoModelAsync）
                    if (!wasEnabled && settings.visionEnabled && string.IsNullOrWhiteSpace(settings.visionModel))
                    {
                        AsyncHelper.RunAsync(async () => await VisionModelConfig.EnsureDefaultWithAutoModelAsync());
                    }

                    bool prevEnabled = GUI.enabled;
                    GUI.enabled = prevEnabled && settings.visionEnabled;
                    settings.visionEndpoint = EditorGUILayout.TextField(
                        new GUIContent(Loc.Tr("settings.model.endpoint", "服务地址"), "Vision model OpenAI-compatible base URL (independent from main model)."),
                        settings.visionEndpoint ?? "");

                    DrawVisionApiKeyField();

                    // Model 选择: 有刷新出的列表 → Popup(选即写回), 否则 → TextField(手输)。与主模型 DrawModelSelector 同款"二选一", 不并列两个控件。
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.PrefixLabel(new GUIContent(Loc.Tr("settings.model.model", "模型"), "Vision-capable model name. When a fetched list is available you pick from the dropdown (writes back immediately); otherwise type it manually."));
                    if (_visionModels.Count > 0)
                    {
                        var currentIdx = _visionModels.FindIndex(m =>
                            string.Equals(m, settings.visionModel?.Trim(), StringComparison.OrdinalIgnoreCase));
                        if (currentIdx < 0) currentIdx = 0;
                        int pickedIdx = EditorGUILayout.Popup(currentIdx, _visionModels.ToArray());
                        if (pickedIdx >= 0 && pickedIdx < _visionModels.Count &&
                            !string.Equals(_visionModels[pickedIdx], settings.visionModel?.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            settings.visionModel = _visionModels[pickedIdx];
                            // 选中即保存（与主模型 DrawModelSelector 的 picked→UpdateProfile 同语义），
                            // 不依赖外层 EndChangeCheck 的隐式触发，确保切换后立刻落盘生效。
                            settings.SaveSettings();
                        }
                    }
                    else
                    {
                        // 手输: 直接赋值, 由卡片外层 EndChangeCheck(见下)统一 SaveSettings。
                        settings.visionModel = EditorGUILayout.TextField(settings.visionModel ?? "");
                    }
                    GUI.enabled = prevEnabled && settings.visionEnabled && !_visionFetchRunning;
                    if (GUILayout.Button(_visionFetchRunning ? "..." : "Refresh Models", GUILayout.Width(120)))
                    {
                        TriggerVisionFetchModels(settings);
                    }
                    GUI.enabled = prevEnabled;
                    EditorGUILayout.EndHorizontal();

                    if (EditorGUI.EndChangeCheck())
                    {
                        settings.SaveSettings();
                    }

                    // Test Connection（刷新/测试状态内联显示在本行右侧, 与主模型 Provider Profiles 同款, 不换行不占上下布局）
                    EditorGUILayout.BeginHorizontal();
                    GUI.enabled = prevEnabled && settings.visionEnabled && !_visionTestRunning;
                    if (GUILayout.Button(_visionTestRunning ? Loc.Tr("settings.model.testing", "正在测试…") : Loc.Tr("settings.model.test", "测试连接"), GUILayout.Width(150)))
                    {
                        TriggerVisionTestConnection(settings);
                    }
                    GUI.enabled = prevEnabled;
                    context.Ui.DrawStatusLabel(_visionFetchStatus, VisionStatusLevel(_visionFetchStatus, _visionFetchRunning), miniLabel: true);
                    context.Ui.DrawStatusLabel(_visionTestStatus, VisionStatusLevel(_visionTestStatus, _visionTestRunning), miniLabel: true);
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.EndHorizontal();
                });
        }

        private void DrawVisionApiKeyField()
        {
            // 首次绘制: 若已存 key, 显示掩码占位; 未存则空。
            if (_visionApiKeyInput == null)
            {
                _visionApiKeyInput = SecureKeyStorage.HasVisionApiKey() ? "••••••••" : "";
                _visionApiKeyDirty = false;
            }

            string display = _visionApiKeyInput;
            var newInput = EditorGUILayout.PasswordField(
                new GUIContent("API Key", "Vision model API key (optional — some local vision servers are keyless). Stored per-user, never committed."),
                display);

            if (newInput != display)
            {
                _visionApiKeyInput = newInput;
                _visionApiKeyDirty = true;

                // 用户在掩码上输入新值(或清空): 立即写入 SecureKeyStorage。
                // 占位串(••••••••)保持已存 key 不变; 换成了任意其他新值则覆盖/清除。
                string stored = SecureKeyStorage.GetVisionApiKey();
                if (!string.Equals(newInput, "••••••••", StringComparison.Ordinal) && newInput != stored)
                {
                    if (string.IsNullOrEmpty(newInput))
                    {
                        if (SecureKeyStorage.HasVisionApiKey()) SecureKeyStorage.DeleteVisionApiKey();
                    }
                    else
                    {
                        SecureKeyStorage.SetVisionApiKey(newInput);
                    }
                }
            }
        }

        private void TriggerVisionFetchModels(AgentCoreSettings settings)
        {
            if (string.IsNullOrWhiteSpace(settings.visionEndpoint))
            {
                _visionFetchStatus = "[FAIL] Enter a Vision Endpoint first, then Refresh Models.";
                return;
            }
            var endpoint = settings.visionEndpoint.Trim();
            var apiKey = SecureKeyStorage.GetVisionApiKey();
            _visionFetchRunning = true;
            _visionFetchStatus = "Fetching models...";

            AsyncHelper.RunAsync(async () =>
            {
                try
                {
                    var models = await _service.FetchModelsAsync(endpoint, apiKey);
                    AsyncHelper.RunOnMainThread(() =>
                    {
                        _visionModels.Clear();
                        if (models != null) _visionModels.AddRange(models);
                        _visionFetchStatus = (models != null && models.Count > 0)
                            ? $"[OK] Found {models.Count} models"
                            : "[OK] Endpoint reachable, but returned no models.";
                        _visionFetchRunning = false;
                    });
                }
                catch (Exception ex)
                {
                    AsyncHelper.RunOnMainThread(() =>
                    {
                        _visionFetchStatus = $"[FAIL] {ex.Message}";
                        _visionFetchRunning = false;
                    });
                }
            });
        }

        private void TriggerVisionTestConnection(AgentCoreSettings settings)
        {
            if (string.IsNullOrWhiteSpace(settings.visionEndpoint))
            {
                _visionTestStatus = "[FAIL] Enter a Vision Endpoint first, then Test Connection.";
                return;
            }
            var endpoint = settings.visionEndpoint.Trim();
            var apiKey = SecureKeyStorage.GetVisionApiKey();
            _visionTestRunning = true;
            _visionTestStatus = Loc.Tr("settings.model.testing", "正在测试…");

            AsyncHelper.RunAsync(async () =>
            {
                try
                {
                    var message = await _service.TestConnectionAsync(endpoint, apiKey);
                    AsyncHelper.RunOnMainThread(() =>
                    {
                        _visionTestStatus = message;
                        _visionTestRunning = false;
                    });
                }
                catch (Exception ex)
                {
                    AsyncHelper.RunOnMainThread(() =>
                    {
                        _visionTestStatus = $"[FAIL] {ex.Message}";
                        _visionTestRunning = false;
                    });
                }
            });
        }

        /// <summary>
        /// 把 vision 刷新/测试的本地状态字符串 + 运行标记映射为 <see cref="SettingsStatusLevel"/>，
        /// 供 <see cref="AgentCoreSettingsUi.DrawStatusLabel"/> 着色。对齐主模型 Provider Profiles 的状态级语义：
        /// 运行中=Loading，[FAIL]=Error，[OK]=Success，空/其他=None。
        /// </summary>
        private static SettingsStatusLevel VisionStatusLevel(string status, bool running)
        {
            if (running)
                return SettingsStatusLevel.Loading;
            if (string.IsNullOrEmpty(status))
                return SettingsStatusLevel.None;
            if (status.StartsWith("[FAIL]"))
                return SettingsStatusLevel.Error;
            if (status.StartsWith("[OK]"))
                return SettingsStatusLevel.Success;
            return SettingsStatusLevel.None;
        }
    }
}
