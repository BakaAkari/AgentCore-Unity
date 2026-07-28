using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using AgentCore.Editor.Core;
using AgentCore.Editor.Extensions;
using AgentCore.Editor.Utils;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Config
{
    /// <summary>
    /// AgentCore 插件的全局设置。
    /// 使用 ScriptableSingleton 存储在 Library/ 目录下，不进版本控制。
    /// 通过 Project Settings > AgentCore 面板访问。
    /// <para>
    /// ADR-17 极简即开即用哲学: 用户只感知必要字段, 其余内部化。
    /// 保留字段但 [HideInInspector] 的字段仅供内部代码引用, 不给用户 UI。
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    [FilePath("AgentCore/Settings.asset", FilePathAttribute.Location.PreferencesFolder)]
    public class AgentCoreSettings : ScriptableSingleton<AgentCoreSettings>
    {
        static AgentCoreSettings()
        {
            EditorApplication.delayCall += () =>
            {
                var settings = instance;
                if (settings != null)
                    settings.MigrateSettings();

                try
                {
                    OptionalComponentManager.EnsureVcsDefaultForCurrentProject();
                }
                catch (Exception ex)
                {
                    AgentCoreLog.Warning($"[AgentCore] Fallback EnsureVcsDefaultForCurrentProject failed: {ex.Message}");
                }
            };
        }

        // --- 版本迁移 ---
        /// <summary>Current settings schema version. Bumped whenever migrations add/remove fields.</summary>
        public const int CurrentVersion = 21;

        /// <summary>Persisted schema version of this settings asset. Compared against <see cref="CurrentVersion"/> to trigger MigrateSettings.</summary>
        [SerializeField] private int settingsVersion = 0;

        // ═══════════════════════════════════════════════════════════════
        // 用户 UI 可见字段 (共 ~10 个, 极简哲学: 只保留必要)
        // ═══════════════════════════════════════════════════════════════

        // --- LLM connection (Model & Agent page, essential config) ---
        [Tooltip("LLM API endpoint (OpenAI-compatible)")]
        public string llmEndpoint = "http://172.16.248.60:8000/v1";

        [Tooltip("LLM model name")]
        public string llmModel = "glm-5.2";

        // v1.6.5+: temperature 和 maxTokens 已自适应化，不再暴露给用户
        // temperature 默认 0.7，maxTokens 由 ModelCapabilityProbe 探测的 max_model_len 自动计算
        [HideInInspector]
        public float temperature = 0.7f;

        [HideInInspector]
        public int maxTokens = 8192;

        // --- Self-Challenge (ADR-17: single toggle) ---
        [Tooltip("Enable Self-Challenge — Node A challenges intent + Node B reviews draft; +10~50% tokens per turn")]
        public bool selfChallengeEnabled = true;

        // --- Self-Challenge Model-Tier Escape (ADR: self-challenge-model-tier-escape) ---
        // 高级模型(claude-opus / o-series / gpt-5 / deepseek-r / gemini-2.5)具备 native reasoning,
        // 自挑战与其重复 → 默认逃逸,跳过 Node A + Node B,依赖 native thinking。
        // 热插拔:每轮实时读取,无需重启。selfChallengeEnabled=false 时此开关无意义。
        [Tooltip("Enable model-tier escape — advanced models with native reasoning skip Self-Challenge to avoid duplicate thinking cost")]
        public bool selfChallengeEscapeEnabled = true;

        // --- Memory / Knowledge Base (optional cloud services) ---
        [Tooltip("Enable mem0 memory service")]
        public bool mem0Enabled = false;

        [Tooltip("mem0 service endpoint")]
        public string mem0Endpoint = "";

        [Tooltip("Enable LightRAG knowledge base")]
        public bool lightragEnabled = false;

        [Tooltip("LightRAG service endpoint")]
        public string lightragEndpoint = "";

        // --- Compression (UI exposes toggle only) ---
        [Tooltip("Enable context compression")]
        public bool compressionEnabled = true;

        // --- Log Level (v1.6.5+, control Debug.Log verbosity) ---
        [Tooltip("Log verbosity — Silent: no output; Error: errors only; Warning: default, errors + warnings; Info: incl. key business events; Debug: incl. high-frequency details (streaming token, per-event)")]
        public AgentCore.Editor.Utils.LogLevel logLevel = AgentCore.Editor.Utils.LogLevel.Warning;

        // ═══════════════════════════════════════════════════════════════
        // 内部字段 [HideInInspector] — 用户不可见, 由工程侧写死最优值
        // 保留字段以维持 SessionData 反序列化兼容与内部代码引用
        // ═══════════════════════════════════════════════════════════════

        // --- Agent Runtime (原 Model & Agent 页面 Agent Runtime 卡片) ---
        [HideInInspector]
        public int maxToolCallRounds = 200;

        [HideInInspector]
        public int maxTokenBudget = 0;

        // --- Context Budget (原 Context & Memory 页面 Context Budget 卡片) ---
        [HideInInspector]
        public int maxContextTokens = 0;

        [HideInInspector]
        public int reserveResponseTokens = 32000; // v1.6.5+: 由 ApplyAdaptiveDefaults 动态覆盖

        // v1.6.5+: reserveResponseTokens 占 max_model_len 的比例
        // 200K context → 8K reserve；1M context → 32K reserve
        private const float ReserveRatio = 0.04f;
        private const int ReserveMin = 4096;
        private const int ReserveMax = 65536;

        // --- Self-Correction (原 Self Correction 卡片) ---
        [HideInInspector]
        public int toolFailWarningThreshold = 3;

        [HideInInspector]
        public int toolFailBlockThreshold = 6;

        [HideInInspector]
        public int allToolsFailBlockThreshold = 4;

        // --- Bootstrap Files (原 Context Sources 卡片) ---
        [HideInInspector]
        public bool bootstrapEnabled = true;

        [HideInInspector]
        public bool autoProjectContext = true;

        /// <summary>
        /// ADR-18 Skill System: 是否启用 Skill 加载机制（默认启用）。
        /// 关闭后 <c>load_skill</c> 工具会返回错误，已加载 skill 保持不变（不主动卸载）。
        /// </summary>
        [HideInInspector]
        public bool skillsEnabled = true;

        // --- Memory Auto (原 Auto Memory 折叠区) ---
        [HideInInspector]
        public bool autoMemoryEnabled = false;

        [HideInInspector]
        public int autoMemoryMinTurns = 3;

        // --- 工具管理 (Tools & Extensions 页面) ---
        [HideInInspector]
        public List<string> disabledToolCategories = new List<string>();

        [HideInInspector]
        public List<string> disabledTools = new List<string>();

        [HideInInspector]
        public bool toolScopingEnabled = true;

        // --- 上下文压缩内部参数 (原 Compression 卡片) ---
        [HideInInspector]
        public int toolResultCompressionThreshold = 2000;

        [HideInInspector]
        public int toolResultTargetTokens = 500;

        /// <summary>
        /// Hard cap for tool result content in characters (v1.11+, Bug C).
        /// Absolute upper bound applied to ALL tool output after compression/short-path,
        /// preventing LLM-compression itself from returning oversized output (observed
        /// GLM reasoning-explosion returning 800k+ chars). Default 20000 chars.
        /// Set to 0 to disable.
        /// </summary>
        [HideInInspector]
        public int toolResultHardCapChars = 20000;

        [HideInInspector]
        [Range(0.3f, 0.95f)]
        public float conversationCompressionTrigger = 0.7f;

        // --- Request Enrichment ---
        [HideInInspector]
        public bool enableReasoningOutput = true;  // GLM-5.2 适配:逃逸 Self-Challenge 后依赖 native reasoning,注入空 reasoning:{} 触发 reasoning_content 返回

        [HideInInspector]
        public string reasoningEffort = "low";

        [HideInInspector]
        public int reasoningMaxTokens = 2048;

        [HideInInspector]
        public string extraRequestBody = "";

        // ═══════════════════════════════════════════════════════════════
        // 已删除的字段 (v18 迁移会 discard 旧数据):
        //   - intentChallengeEnabled / answerChallengeEnabled — 合并到 selfChallengeEnabled
        //   - answerChallengeMaxRetries / allowAgentClarificationQuestions / legacySelfChallengeDisabled /
        //     selfChallengeCardCountForcedExpansion — 常量化到 SelfChallengeConfig
        //   - workspaceRootOverride / unityRootRelativePathOverride — 依赖自动检测
        //   - userId — deprecated, 始终使用 EffectiveUserId (系统生成)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// 获取有效的用户 ID。
        /// 始终使用系统自动生成的唯一标识符。
        /// </summary>
        public string EffectiveUserId => GenerateSystemUserId();

        /// <summary>
        /// 基于系统信息生成唯一用户 ID。
        /// </summary>
        public static string GenerateSystemUserId()
        {
            try
            {
                var raw = $"{Environment.MachineName}:{Environment.UserName}:{Application.productName}";
                using (var sha256 = SHA256.Create())
                {
                    var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(raw));
                    var sb = new StringBuilder();
                    for (int i = 0; i < 8; i++)
                        sb.Append(bytes[i].ToString("x2"));
                    return $"unity-{sb}";
                }
            }
            catch
            {
                return "unity-agent";
            }
        }

        /// <summary>
        /// 检查指定工具是否被禁用。
        /// </summary>
        public bool IsToolDisabled(string toolName, string category)
        {
            if (!string.IsNullOrEmpty(toolName) && disabledTools != null && disabledTools.Contains(toolName))
                return true;
            if (!string.IsNullOrEmpty(category) && disabledToolCategories != null && disabledToolCategories.Contains(category))
                return true;
            return false;
        }

        /// <summary>
        /// ScriptableSingleton 加载后自动调用。执行版本迁移。
        /// </summary>
        private void OnEnable()
        {
            if (settingsVersion < CurrentVersion)
            {
                MigrateSettings();
            }

            // 若模型设置为自动获取, 则在延迟调用中异步获取模型列表
            if (llmModel == "auto")
            {
                EditorApplication.delayCall += async () =>
                {
                    try
                    {
                        await FetchModelsAsync();
                    }
                    catch (Exception ex)
                    {
                        AgentCoreLog.Error($"[AgentCore] Failed to fetch models: {ex.Message}");
                    }
                };
            }
        }

        /// <summary>
        /// 执行设置版本迁移。
        /// </summary>
        private void MigrateSettings()
        {
            // v0 -> v1: 修正 mem0 默认端点
            if (settingsVersion < 1 && mem0Endpoint == "http://localhost:18910")
            {
                mem0Endpoint = "http://localhost:8765";
                AgentCoreLog.Info("[AgentCore] Settings migrated v0→v1: mem0Endpoint updated");
            }

            // v1-v16: 历史迁移(逻辑保留但精简日志)
            if (settingsVersion < 5 && llmModel == "deepseek-chat") llmModel = "claude-sonnet-4-5";
            if (settingsVersion < 5 && maxTokens == 4096) maxTokens = 16000;
            if (settingsVersion < 5 && reserveResponseTokens == 2000) reserveResponseTokens = 8000;
            if (settingsVersion < 9 && reserveResponseTokens == 8000) reserveResponseTokens = 32000;
            if (settingsVersion < 9 && toolResultCompressionThreshold == 1000) toolResultCompressionThreshold = 2000;
            if (settingsVersion < 9 && toolResultTargetTokens == 200) toolResultTargetTokens = 500;
            if (settingsVersion < 13)
            {
                EditorApplication.delayCall += () => ApplyVcsDefaultEnablement(this, retry: true);
            }
            if (settingsVersion < 14 && maxToolCallRounds <= 50) maxToolCallRounds = 200;
            if (settingsVersion < 16)
            {
                EditorApplication.delayCall += () => ApplyVcsDefaultEnablement(this, retry: true);
            }

            // v17: Phase 9 Self-Challenge 骨架字段(已在 v18 中整合)
            // v18: ADR-17 极简即开即用 — 清理已删除字段的孤儿数据
            if (settingsVersion < 18)
            {
                AgentCoreLog.Info("[AgentCore] Settings migrated v17→v18: ADR-17 minimalism refactor — 9 fields removed, 25+ fields hidden. selfChallengeEnabled remains as unified control.");
            }

            // v19: GLM-5.2 reasoning optimization — limit thinking chain to prevent 370s+ response times
            if (settingsVersion < 19)
            {
                maxTokens = 8192;
                reasoningEffort = "low";
                reasoningMaxTokens = 2048;
                AgentCoreLog.Info("[AgentCore] Settings migrated v18→v19: reasoning optimization (maxTokens=8192, reasoningEffort=low, reasoningMaxTokens=2048)");
            }

            // v20: 清理死字段 — 删除 12 个无引用的 HideInInspector 字段 + Compression LLM 残留
            if (settingsVersion < 20)
            {
                AgentCoreLog.Info("[AgentCore] Settings migrated v19→v20: dead field cleanup (12 fields removed, disabledTools default cleared)");
            }

            // v21: 修复 v20 遗留 bug — v20 声称清理 disabledTools 默认值但实际没执行 Clear。
            // 强制从 disabledTools 移除历史遗留的 "execute_code"(v1.7.21 起 SOUL §2.10 要求 execute_code:run 可用,
            // 但历史 settings 里 execute_code 被硬编码写入 disabledTools 导致 ToolRegistry 过滤掉该工具,
            // 使 SOUL 引导的能力不可用)。只精准移除 execute_code 单项,不清空整个 disabledTools 以尊重用户其它禁用意图。
            if (settingsVersion < 21)
            {
                if (disabledTools != null && disabledTools.Remove("execute_code"))
                {
                    AgentCoreLog.Info("[AgentCore] Settings migrated v20→v21: removed 'execute_code' from disabledTools (v20 migration bug fix; execute_code:run is required by SOUL §2.10 as of v1.7.21).");
                }
            }

            settingsVersion = CurrentVersion;

            EditorApplication.delayCall += () =>
            {
                SafeSave(true);
            };
        }

        /// <summary>
        /// 应用 VCS 默认启用。
        /// </summary>
        private static void ApplyVcsDefaultEnablement(AgentCoreSettings settings, bool retry)
        {
            if (settings == null) return;

            try
            {
                if (!OptionalComponentManager.IsVcsEnabled())
                {
                    OptionalComponentManager.SetVcsEnabled(true);
                    AgentCoreLog.Info("[AgentCore] VCS enabled by default; Code Indexing remains disabled (experimental)");
                }
                settings.SafeSave(true);
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] Failed to apply VCS default enablement: {ex.Message}");
                if (retry)
                {
                    EditorApplication.delayCall += () => ApplyVcsDefaultEnablement(settings, retry: false);
                }
            }
        }

        /// <summary>
        /// 异步获取 LLM 模型列表并设置第一个为默认模型。
        /// </summary>
        private async Task FetchModelsAsync()
        {
            if (string.IsNullOrEmpty(llmEndpoint))
            {
                AgentCoreLog.Warning("[AgentCore] LLM endpoint is empty, cannot fetch models.");
                return;
            }

            var client = HttpClientFactory.GetClient();
            var request = HttpClientFactory.CreateRequest(HttpMethod.Get, llmEndpoint + "/models");
            var response = await client.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var jobj = JsonHelper.ParseObject(content);
                if (jobj != null && jobj.TryGetValue("data", out var jarr) && jarr is JArray models && models.Count > 0)
                {
                    var firstModel = models[0]["id"]?.ToString();
                    if (!string.IsNullOrEmpty(firstModel))
                    {
                        llmModel = firstModel;
                        SafeSave(true);
                        AgentCoreLog.Info($"[AgentCore] Fetched models, set default to: {firstModel}");
                    }
                }
                else
                {
                    AgentCoreLog.Warning($"[AgentCore] No models found in response: {content}");
                }
            }
            else
            {
                AgentCoreLog.Error($"[AgentCore] Failed to fetch models: {response.StatusCode} {response.ReasonPhrase}");
            }
        }

        /// <summary>
        /// 重置所有设置为默认值。
        /// </summary>
        public void ResetToDefaults()
        {
            llmEndpoint = "http://172.16.248.60:8000/v1";
            llmModel = "glm-5.2";
            temperature = 0.7f;
            maxTokens = 8192;
            selfChallengeEnabled = true;
            selfChallengeEscapeEnabled = true;  // ADR: model-tier escape 默认开启
            mem0Enabled = false;
            mem0Endpoint = "";
            lightragEnabled = false;
            lightragEndpoint = "";
            compressionEnabled = true;

            // 隐藏字段: 恢复最优默认值
            maxToolCallRounds = 200;
            maxTokenBudget = 0;
            maxContextTokens = 0;
            reserveResponseTokens = 32000;
            toolFailWarningThreshold = 3;
            toolFailBlockThreshold = 6;
            allToolsFailBlockThreshold = 4;
            bootstrapEnabled = true;
            autoProjectContext = true;
            skillsEnabled = true;
            autoMemoryEnabled = false;
            autoMemoryMinTurns = 3;
            disabledToolCategories = new List<string>();
            disabledTools = new List<string>();
            toolScopingEnabled = true;
            toolResultCompressionThreshold = 2000;
            toolResultTargetTokens = 500;
            conversationCompressionTrigger = 0.7f;
            enableReasoningOutput = true;  // GLM-5.2 适配:逃逸 Self-Challenge 后依赖 native reasoning
            reasoningEffort = "low";
            reasoningMaxTokens = 2048;
            extraRequestBody = "";
            settingsVersion = CurrentVersion;
            SafeSave(true);
        }

        /// <summary>
        /// 设置变更事件。
        /// </summary>
        public static event Action OnSettingsChanged;

        /// <summary>
        /// 保存设置到磁盘并通知订阅者。
        /// </summary>
        public void SaveSettings()
        {
            SafeSave(true);
            OnSettingsChanged?.Invoke();
        }

        /// <summary>
        /// Safe wrapper around <see cref="ScriptableSingleton{T}.Save(bool)"/> that ensures the
        /// preferences directory exists and swallows IO/access failures so the Editor is never
        /// blocked by a corrupt or missing preferences root.
        /// </summary>
        /// <remarks>
        /// See <see cref="PreferencesFolderPathHelper"/> for the reason this exists: Unity's
        /// underlying <c>SaveToSerializedFileAndForget</c> Move step fails hard when the target
        /// parent directory (e.g. <c>%APPDATA%/Unity/Editor-*.x/Preferences/AgentCore/</c>) does
        /// not exist, which has been observed to leave the Editor stuck on fresh installs.
        /// </remarks>
        internal void SafeSave(bool saveAsText)
        {
            if (!PreferencesFolderPathHelper.EnsureAgentCoreDirectory())
            {
                AgentCoreLog.Warning("[AgentCore] Skipping AgentCoreSettings.Save — preferences directory not available.");
                return;
            }
            try
            {
                Save(saveAsText);
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] AgentCoreSettings.Save failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取完整的 LLM API Chat Completions URL。
        /// </summary>
        public string GetChatCompletionsUrl()
        {
            var baseUrl = llmEndpoint.TrimEnd('/');
            return $"{baseUrl}/chat/completions";
        }

        /// <summary>
        /// v1.6.5+: 根据模型实际能力参数自适应调整配置。
        /// 在每次 LLM 调用前由 AgentLoop 调用，确保 maxTokens / reserveResponseTokens
        /// 与 ModelCapabilityProbe 探测到的 max_model_len 匹配。
        /// </summary>
        public void ApplyAdaptiveDefaults()
        {
            int maxModelLen = ModelCapabilityProbe.GetMaxModelLen(llmModel);

            // reserveResponseTokens = max_model_len * 4%，clamp [4096, 65536]
            int adaptiveReserve = Mathf.RoundToInt(maxModelLen * ReserveRatio);
            adaptiveReserve = Mathf.Clamp(adaptiveReserve, ReserveMin, ReserveMax);
            reserveResponseTokens = adaptiveReserve;
        }

        /// <summary>
        /// v1.6.5+: 计算实际发送给 LLM API 的 max_tokens 值。
        /// GLM-5.2 等 native reasoning 模型中，SGLang 的 max_tokens 是
        /// reasoning + content 的总上限。如果只发 maxTokens，reasoning 会吃光预算，
        /// 导致 content 为空（finish_reason=length）。
        /// 解决：max_tokens = maxTokens (content) + reasoningMaxTokens (reasoning 预算)
        /// </summary>
        public int GetEffectiveMaxTokens()
        {
            if (enableReasoningOutput && reasoningMaxTokens > 0)
                return maxTokens + reasoningMaxTokens;
            return maxTokens;
        }

        /// <summary>
        /// v1.6.5+: 计算指定 content 预算下的 effective max_tokens。
        /// 用于压缩等需要较小 content 输出的场景：传入 contentMaxTokens 替代 settings.maxTokens。
        /// reasoning 预算仍从 settings.reasoningMaxTokens 获取。
        /// </summary>
        /// <param name="contentMaxTokens">content 部分的 token 预算</param>
        public int GetEffectiveMaxTokens(int contentMaxTokens)
        {
            if (enableReasoningOutput && reasoningMaxTokens > 0)
                return contentMaxTokens + reasoningMaxTokens;
            return contentMaxTokens;
        }
    }
}
