using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
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
                    Debug.LogWarning($"[AgentCore] Fallback EnsureVcsDefaultForCurrentProject failed: {ex.Message}");
                }
            };
        }

        // --- 版本迁移 ---
        [SerializeField] private int settingsVersion = 0;
        [SerializeField] private bool vcsDefaultEnabled = false;
        private const int CurrentVersion = 18;

        // ═══════════════════════════════════════════════════════════════
        // 用户 UI 可见字段 (共 ~10 个, 极简哲学: 只保留必要)
        // ═══════════════════════════════════════════════════════════════

        // --- LLM connection (Model & Agent page, essential config) ---
        [Tooltip("LLM API endpoint (OpenAI-compatible)")]
        public string llmEndpoint = "http://172.16.248.60:8000/v1";

        [Tooltip("LLM model name")]
        public string llmModel = "auto";

        [Tooltip("Sampling temperature (0.0-2.0)")]
        [Range(0f, 2f)]
        public float temperature = 0.7f;

        [Tooltip("Max output tokens")]
        public int maxTokens = 16000;

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

        // ═══════════════════════════════════════════════════════════════
        // 内部字段 [HideInInspector] — 用户不可见, 由工程侧写死最优值
        // 保留字段以维持 SessionData 反序列化兼容与内部代码引用
        // ═══════════════════════════════════════════════════════════════

        // --- Agent Runtime (原 Model & Agent 页面 Agent Runtime 卡片) ---
        [HideInInspector]
        public int maxToolCallRounds = 200;

        [HideInInspector]
        public int maxTokenBudget = 0;

        [HideInInspector]
        public bool fallbackRoutingEnabled = true;

        // --- Context Budget (原 Context & Memory 页面 Context Budget 卡片) ---
        [HideInInspector]
        public int maxContextTokens = 0;

        [HideInInspector]
        public int reserveResponseTokens = 32000;

        // --- Self-Correction (原 Self Correction 卡片) ---
        [HideInInspector]
        public bool autoCompileCheck = true;

        [HideInInspector]
        public bool autoConsoleCapture = true;

        [HideInInspector]
        public int maxConsecutiveErrors = 5;

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

        // --- Memory Auto (原 Auto Memory 折叠区) ---
        [HideInInspector]
        public bool autoMemoryEnabled = false;

        [HideInInspector]
        public int autoMemoryMinTurns = 3;

        // --- 工具管理 (Tools & Extensions 页面) ---
        [HideInInspector]
        public List<string> disabledToolCategories = new List<string>();

        [HideInInspector]
        public List<string> disabledTools = new List<string> { "execute_code" };

        [HideInInspector]
        public bool toolScopingEnabled = true;

        // --- 上下文压缩内部参数 (原 Compression 卡片) ---
        [HideInInspector]
        public bool useSeparateCompressionLLM = false;

        [HideInInspector]
        public string compressionLLMEndpoint = "";

        [HideInInspector]
        public string compressionLLMModel = "claude-3-haiku-20240307";

        [HideInInspector]
        public int toolResultCompressionThreshold = 2000;

        [HideInInspector]
        public int toolResultTargetTokens = 500;

        [HideInInspector]
        [Range(0.3f, 0.95f)]
        public float conversationCompressionTrigger = 0.7f;

        // --- Workspace (auto-detect 保持内部化, override 字段已删除) ---
        [HideInInspector]
        public bool workspaceAutoDetectEnabled = true;

        [HideInInspector]
        public int workspaceConfigVersion = 0;

        // --- Request Enrichment ---
        [HideInInspector]
        public bool enableReasoningOutput = false;

        [HideInInspector]
        public string reasoningEffort = "";

        [HideInInspector]
        public int reasoningMaxTokens = 0;

        [HideInInspector]
        public string extraRequestBody = "";

        // --- UI 偏好 (原 Chat UI 卡片) ---
        [HideInInspector]
        public bool streamingEnabled = true;

        [HideInInspector]
        public bool showToolCallDetails = true;

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
                        Debug.LogError($"[AgentCore] Failed to fetch models: {ex.Message}");
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
                Debug.Log("[AgentCore] Settings migrated v0→v1: mem0Endpoint updated");
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
            if (settingsVersion < 16 && !vcsDefaultEnabled)
            {
                EditorApplication.delayCall += () => ApplyVcsDefaultEnablement(this, retry: true);
            }

            // v17: Phase 9 Self-Challenge 骨架字段(已在 v18 中整合)
            // v18: ADR-17 极简即开即用 — 清理已删除字段的孤儿数据
            if (settingsVersion < 18)
            {
                Debug.Log("[AgentCore] Settings migrated v17→v18: ADR-17 minimalism refactor — 9 fields removed, 25+ fields hidden. selfChallengeEnabled remains as unified control.");
            }

            settingsVersion = CurrentVersion;

            EditorApplication.delayCall += () =>
            {
                try { Save(true); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[AgentCore] Deferred settings save failed: {ex.Message}");
                }
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
                    Debug.Log("[AgentCore] VCS enabled by default; Code Indexing remains disabled (experimental)");
                }
                settings.vcsDefaultEnabled = true;
                settings.Save(true);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentCore] Failed to apply VCS default enablement: {ex.Message}");
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
                Debug.LogWarning("[AgentCore] LLM endpoint is empty, cannot fetch models.");
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
                        Save(true);
                        Debug.Log($"[AgentCore] Fetched models, set default to: {firstModel}");
                    }
                }
                else
                {
                    Debug.LogWarning($"[AgentCore] No models found in response: {content}");
                }
            }
            else
            {
                Debug.LogError($"[AgentCore] Failed to fetch models: {response.StatusCode} {response.ReasonPhrase}");
            }
        }

        /// <summary>
        /// 重置所有设置为默认值。
        /// </summary>
        public void ResetToDefaults()
        {
            llmEndpoint = "http://172.16.248.60:8000/v1";
            llmModel = "auto";
            temperature = 0.7f;
            maxTokens = 16000;
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
            fallbackRoutingEnabled = true;
            maxContextTokens = 0;
            reserveResponseTokens = 32000;
            autoCompileCheck = true;
            autoConsoleCapture = true;
            maxConsecutiveErrors = 5;
            toolFailWarningThreshold = 3;
            toolFailBlockThreshold = 6;
            allToolsFailBlockThreshold = 4;
            bootstrapEnabled = true;
            autoProjectContext = true;
            autoMemoryEnabled = false;
            autoMemoryMinTurns = 3;
            disabledToolCategories = new List<string>();
            disabledTools = new List<string> { "execute_code" };
            toolScopingEnabled = true;
            useSeparateCompressionLLM = false;
            compressionLLMEndpoint = "";
            compressionLLMModel = "claude-3-haiku-20240307";
            toolResultCompressionThreshold = 2000;
            toolResultTargetTokens = 500;
            conversationCompressionTrigger = 0.7f;
            workspaceAutoDetectEnabled = true;
            workspaceConfigVersion = 0;
            enableReasoningOutput = false;
            reasoningEffort = "";
            reasoningMaxTokens = 0;
            extraRequestBody = "";
            streamingEnabled = true;
            showToolCallDetails = true;
            settingsVersion = CurrentVersion;
            Save(true);
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
            Save(true);
            OnSettingsChanged?.Invoke();
        }

        /// <summary>
        /// 获取完整的 LLM API Chat Completions URL。
        /// </summary>
        public string GetChatCompletionsUrl()
        {
            var baseUrl = llmEndpoint.TrimEnd('/');
            return $"{baseUrl}/chat/completions";
        }
    }
}
