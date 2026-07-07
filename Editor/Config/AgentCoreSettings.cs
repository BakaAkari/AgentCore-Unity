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
    /// </summary>
    [InitializeOnLoad]
    [FilePath("AgentCore/Settings.asset", FilePathAttribute.Location.PreferencesFolder)]
    public class AgentCoreSettings : ScriptableSingleton<AgentCoreSettings>
    {
        static AgentCoreSettings()
        {
            // 强制在 Editor 启动后期加载一次 Settings，确保版本迁移（包括默认启用 VCS）被执行。
            // ScriptableSingleton OnEnable 可能在 Editor 启动早期触发，部分 Editor 服务尚未就绪。
            EditorApplication.delayCall += () =>
            {
                var settings = instance;
                if (settings != null)
                    settings.MigrateSettings();

                // v1.4.3: 每个项目独立检查 VCS 默认启用状态。
                // 修复"跨项目共享 Settings.asset 导致新项目 VCS 未自动启用"问题（见
                // OptionalComponentManager.EnsureVcsDefaultForCurrentProject 的详细说明）。
                // 这里独立于 MigrateSettings 触发，因为版本号迁移只在 settingsVersion 落后时生效，
                // 而项目级检查需要每个新项目都跑一次，即使 settingsVersion 已经是最新。
                Extensions.OptionalComponentManager.EnsureVcsDefaultForCurrentProject();
            };
        }

        // --- 版本迁移 ---
        [SerializeField] private int settingsVersion = 0;
        [SerializeField] private bool vcsDefaultEnabled = false;
        private const int CurrentVersion = 16;

        // --- LLM 配置 ---
        [Header("LLM Configuration")]
        [Tooltip("LLM API 端点地址（OpenAI 兼容）")]
        public string llmEndpoint = "http://172.16.248.60:8000/v1";

        [Tooltip("LLM 模型名称")]
        public string llmModel = "auto";

        [Tooltip("生成温度 (0.0-2.0)")]
        [Range(0f, 2f)]
        public float temperature = 0.7f;

        [Tooltip("最大输出 token 数")]
        public int maxTokens = 16000;

        // --- Agent 行为 ---
        [Header("Agent Behavior")]
        [Tooltip("最大工具调用轮次（硬上限安全网，防止无限循环）")]
        public int maxToolCallRounds = 200;

        [Tooltip("单次任务 Token 预算（0 = 不限制，正数 = 累计消耗达到此值后触发软着陆总结）")]
        public int maxTokenBudget = 0;

        [Tooltip("上下文窗口 token 上限（0 = 自动根据模型名称推断）")]
        public int maxContextTokens = 0;

        [Tooltip("为 AI 回复预留的 token 数（现代 LLM 输出能力强，建议 32000）")]
        public int reserveResponseTokens = 32000;

        // --- 自主纠错配置 ---
        [Header("Self-Correction")]
        [Tooltip("脚本修改后自动编译检查")]
        public bool autoCompileCheck = true;

        [Tooltip("每轮工具执行后自动捕获 Console 错误")]
        public bool autoConsoleCapture = true;

        [Tooltip("启用 Fallback 策略路由")]
        public bool fallbackRoutingEnabled = true;

        [Tooltip("连续错误上限，超过后请求用户介入")]
        public int maxConsecutiveErrors = 5;

        [Tooltip("单工具连续失败警告阈值（达到后 LLM 收到降级提示但不中断）")]
        public int toolFailWarningThreshold = 3;

        [Tooltip("单工具连续失败阻断阈值（达到后强制中断工具循环）")]
        public int toolFailBlockThreshold = 6;

        [Tooltip("全工具连续失败轮次阻断阈值（所有工具同时失败的连续轮次）")]
        public int allToolsFailBlockThreshold = 4;

        // --- Bootstrap Files 配置 ---
        [Header("Bootstrap Files")]
        [Tooltip("启用 Bootstrap Files 系统")]
        public bool bootstrapEnabled = true;

        [Tooltip("自动收集项目上下文")]
        public bool autoProjectContext = true;

        // --- mem0 配置（Phase 3 使用，Phase 1 预留）---
        [Header("Memory Service - mem0")]
        [Tooltip("启用 mem0 记忆服务")]
        public bool mem0Enabled = false;

        [Tooltip("mem0 服务端点")]
        public string mem0Endpoint = "";

        [Tooltip("启用自动记忆策略（会话结束时自动提取关键信息存入 mem0）")]
        public bool autoMemoryEnabled = false;

        [Tooltip("触发自动记忆的最小用户对话轮次")]
        public int autoMemoryMinTurns = 3;

        // --- LightRAG 配置（Phase 3 使用，Phase 1 预留）---
        [Header("Knowledge Base - LightRAG")]
        [Tooltip("启用 LightRAG 知识库")]
        public bool lightragEnabled = false;

        [Tooltip("LightRAG 服务端点")]
        public string lightragEndpoint = "";

        // --- 用户标识 ---
        [Header("User")]
        [Tooltip("用户 ID（用于 mem0 记忆隔离，留空则自动使用系统唯一标识）")]
        public string userId = "";

        /// <summary>
        /// 获取有效的用户 ID。
        /// 始终使用系统自动生成的唯一标识符，忽略手动配置的 userId 字段。
        /// </summary>
        public string EffectiveUserId => GenerateSystemUserId();

        /// <summary>
        /// 基于系统信息生成唯一用户 ID。
        /// 使用 MachineName + UserName + ProductName 的 SHA256 哈希前 16 位，
        /// 格式为 "unity-{hash}"，确保跨会话稳定且隐私安全。
        /// 加入 ProductName 实现项目级记忆隔离：同一用户的不同项目拥有不同 ID。
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
                    // 取前 8 字节（16 个十六进制字符），足够唯一且简短
                    for (int i = 0; i < 8; i++)
                        sb.Append(bytes[i].ToString("x2"));
                    return $"unity-{sb}";
                }
            }
            catch
            {
                // 极端情况下的回退
                return "unity-agent";
            }
        }

        // --- 工具管理 ---
        [Header("Tool Management")]
        [Tooltip("禁用的工具分类列表（整个分类下的所有工具都不会发送给 LLM）")]
        public List<string> disabledToolCategories = new List<string>();

        [Tooltip("禁用的单个工具名称列表（不会发送给 LLM）")]
        public List<string> disabledTools = new List<string> { "execute_code" };

        [Tooltip("启用工具作用域管理（G.3 ActiveToolScope）— 按需暴露工具，降低 token 消耗")]
        public bool toolScopingEnabled = true;

        /// <summary>
        /// 检查指定工具是否被禁用。
        /// 工具被禁用的条件：工具名称在 disabledTools 中，或工具所属分类在 disabledToolCategories 中。
        /// </summary>
        /// <param name="toolName">工具名称</param>
        /// <param name="category">工具分类</param>
        /// <returns>工具是否被禁用</returns>
        public bool IsToolDisabled(string toolName, string category)
        {
            if (!string.IsNullOrEmpty(toolName) && disabledTools != null && disabledTools.Contains(toolName))
                return true;
            if (!string.IsNullOrEmpty(category) && disabledToolCategories != null && disabledToolCategories.Contains(category))
                return true;
            return false;
        }

        // --- 上下文压缩配置 ---
        [Header("Context Compression")]
        [Tooltip("启用上下文压缩系统")]
        public bool compressionEnabled = true;

        [Tooltip("使用独立的压缩 LLM（而非共享主 LLM）")]
        public bool useSeparateCompressionLLM = false;

        [Tooltip("压缩 LLM API 端点（仅在 useSeparateCompressionLLM=true 时使用）")]
        public string compressionLLMEndpoint = "";

        [Tooltip("压缩 LLM 模型名称（仅在 useSeparateCompressionLLM=true 时使用）")]
        public string compressionLLMModel = "claude-3-haiku-20240307";

        [Tooltip("工具结果压缩阈值（超过此 token 数的工具结果将被压缩）")]
        public int toolResultCompressionThreshold = 2000;

        [Tooltip("工具结果压缩目标 token 数")]
        public int toolResultTargetTokens = 500;

        [Tooltip("对话压缩触发比例（上下文使用率超过此值时触发对话压缩，0.0-1.0）")]
        [Range(0.3f, 0.95f)]
        public float conversationCompressionTrigger = 0.7f;

        // --- Workspace 配置 ---
        [Header("Workspace")]
        [Tooltip("启用 Workspace 自动检测（从 UnityRoot 向上识别 SVN 工作副本根）")]
        public bool workspaceAutoDetectEnabled = true;

        [Tooltip("手动指定 WorkspaceRoot 绝对路径（留空则自动检测）")]
        public string workspaceRootOverride = "";

        [Tooltip("手动指定 UnityRoot 相对于 WorkspaceRoot 的路径（留空则自动推断）")]
        public string unityRootRelativePathOverride = "";

        [Tooltip("Workspace 配置版本（内部使用，用于检测 workspace.json 变更）")]
        public int workspaceConfigVersion = 0;

        // --- 请求增强配置 ---
        [Header("Request Enrichment")]
        [Tooltip("启用 Reasoning 输出（向 LLM 请求中注入 reasoning 参数，触发思维链返回）。仅 OpenRouter 等兼容 provider 支持，Bedrock/Ollama 等不支持时须关闭。")]
        public bool enableReasoningOutput = false;

        [Tooltip("推理努力级别（low/medium/high），留空表示不指定（由模型决定）")]
        public string reasoningEffort = "";

        [Tooltip("推理最大 token 数（0 = 不限制，由模型决定）")]
        public int reasoningMaxTokens = 0;

        [Tooltip("额外请求体 JSON（深度合并到每个 LLM 请求中，高级用户自定义参数）")]
        [TextArea(3, 8)]
        public string extraRequestBody = "";

        // --- UI 偏好 ---
        [Header("UI Preferences")]
        [Tooltip("启用流式输出")]
        public bool streamingEnabled = true;

        [Tooltip("显示工具调用详情")]
        public bool showToolCallDetails = true;

        /// <summary>
        /// ScriptableSingleton 加载后自动调用。
        /// 执行版本迁移逻辑，确保旧配置自动更新。
        /// </summary>
        private void OnEnable()
        {
            if (settingsVersion < CurrentVersion)
            {
                MigrateSettings();
            }

            // 防御性清除：无论版本号如何，始终确保 userId 字段为空。
            // 这防止了旧序列化值（如 "akari"）在迁移已完成后仍然残留的情况。
            if (!string.IsNullOrEmpty(userId))
            {
                Debug.Log($"[AgentCore] OnEnable: clearing stale userId '{userId}', EffectiveUserId will use system-generated ID");
                userId = "";
                // v1.4.0 fix: defer Save(true) out of OnEnable (Unity forbids saving a
                // ScriptableSingleton while it is still being loaded).
                EditorApplication.delayCall += () =>
                {
                    try { Save(true); }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[AgentCore] Deferred userId clear save failed: {ex.Message}");
                    }
                };
            }

            // 如果模型设置为自动获取，则在延迟调用中异步获取模型列表
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
            // v0 -> v1: 修正 mem0 默认端点（旧值 18910 → 新值 8765）
            if (settingsVersion < 1)
            {
                if (mem0Endpoint == "http://localhost:18910")
                {
                    mem0Endpoint = "http://localhost:8765";
                    Debug.Log("[AgentCore] Settings migrated v0→v1: mem0Endpoint updated to http://localhost:8765");
                }
            }

            // v1 -> v2: userId 默认值从 "unity-agent" 改为空（自动生成系统 ID）
            if (settingsVersion < 2)
            {
                if (userId == "unity-agent")
                {
                    userId = "";
                    Debug.Log("[AgentCore] Settings migrated v1→v2: userId cleared, will auto-generate from system ID");
                }
            }

            // v2 -> v3: 强制覆盖 userId 为系统生成 ID，不再允许自定义
            if (settingsVersion < 3)
            {
                if (!string.IsNullOrEmpty(userId))
                {
                    Debug.Log($"[AgentCore] Settings migrated v2→v3: userId '{userId}' cleared, EffectiveUserId now always uses system-generated ID");
                    userId = "";
                }
            }

            // v3 -> v4: 初始化工具管理列表
            if (settingsVersion < 4)
            {
                if (disabledToolCategories == null) disabledToolCategories = new List<string>();
                if (disabledTools == null) disabledTools = new List<string>();
                Debug.Log("[AgentCore] Settings migrated v3→v4: initialized tool management lists");
            }

            // v4 -> v5: 更新默认值以适配 Claude 系列模型
            if (settingsVersion < 5)
            {
                // 仅当用户仍使用旧默认值时才迁移，避免覆盖用户自定义配置
                if (llmModel == "deepseek-chat")
                {
                    llmModel = "claude-sonnet-4-5";
                    Debug.Log("[AgentCore] Settings migrated v4→v5: llmModel updated to claude-sonnet-4-5");
                }
                if (maxTokens == 4096)
                {
                    maxTokens = 16000;
                    Debug.Log("[AgentCore] Settings migrated v4→v5: maxTokens updated to 16000");
                }
                if (reserveResponseTokens == 2000)
                {
                    reserveResponseTokens = 8000;
                    Debug.Log("[AgentCore] Settings migrated v4→v5: reserveResponseTokens updated to 8000");
                }
            }

            // v5 -> v6: 初始化上下文压缩配置
            if (settingsVersion < 6)
            {
                // 新字段使用声明时的默认值，无需额外迁移逻辑
                // 仅记录日志表明迁移已执行
                Debug.Log("[AgentCore] Settings migrated v5→v6: context compression settings initialized");
            }

            // v6 -> v7: Settings UI 重构为 Dashboard + 4 Pages（无数据迁移，仅标记版本）
            if (settingsVersion < 7)
            {
                Debug.Log("[AgentCore] Settings migrated v6→v7: settings UI restructured to Dashboard + 4 Pages");
            }

            // v7 -> v8: 新增 Workspace 基础设施字段（使用声明时默认值，无需额外迁移）
            if (settingsVersion < 8)
            {
                Debug.Log("[AgentCore] Settings migrated v7→v8: workspace infrastructure fields initialized");
            }

            // v8 -> v9: 更新默认值以适配现代大 context LLM（Claude 200K / DeepSeek 128K / Kimi 128K 等）
            if (settingsVersion < 9)
            {
                // reserveResponseTokens: 8000 → 32000（现代 LLM 输出能力更强，预留更多空间）
                if (reserveResponseTokens == 8000)
                {
                    reserveResponseTokens = 32000;
                    Debug.Log("[AgentCore] Settings migrated v8→v9: reserveResponseTokens updated to 32000");
                }
                // toolResultCompressionThreshold: 1000 → 2000（避免过度压缩中等长度工具结果）
                if (toolResultCompressionThreshold == 1000)
                {
                    toolResultCompressionThreshold = 2000;
                    Debug.Log("[AgentCore] Settings migrated v8→v9: toolResultCompressionThreshold updated to 2000");
                }
                // toolResultTargetTokens: 200 → 500（保留更多工具结果细节）
                if (toolResultTargetTokens == 200)
                {
                    toolResultTargetTokens = 500;
                    Debug.Log("[AgentCore] Settings migrated v8→v9: toolResultTargetTokens updated to 500");
                }
            }

            // v9 → v10: execute_code 默认禁用（仅影响新安装；已有用户保持其现有配置不变）
            // 不修改已有用户的 disabledTools — 他们如果主动启用了 execute_code 则保持启用
            if (settingsVersion < 10)
            {
                Debug.Log("[AgentCore] Settings migrated v9→v10: execute_code now default-disabled for new installs (existing config preserved)");
            }

            // v10 → v11: 新增 Request Enrichment 字段（enableReasoningOutput 默认关闭，需手动开启）
            if (settingsVersion < 11)
            {
                Debug.Log("[AgentCore] Settings migrated v10→v11: request enrichment fields initialized (reasoning output disabled by default, enable in Settings if your provider supports it)");
            }

            // v11 → v12: 可选服务默认值对齐（新安装用户 endpoint 为空，autoMemoryEnabled 为 false）
            // 不迁移现有用户的已配置值 — 仅影响新安装
            if (settingsVersion < 12)
            {
                Debug.Log("[AgentCore] Settings migrated v11→v12: optional service defaults aligned (no data migration for existing users)");
            }

            // v12 → v13: 默认启用 VCS 可选组件；Code Indexing 保持禁用（实验性，需用户手动启用）
            if (settingsVersion < 13)
            {
                // 使用 delayCall 避免在 ScriptableSingleton OnEnable 期间触发重编译。
                // 同时安排一次延迟重试，防止首次调用时 Editor 尚未完全就绪。
                EditorApplication.delayCall += () => ApplyVcsDefaultEnablement(this, retry: true);
            }

            // v13 → v14: Token Budget 模式 — maxToolCallRounds 升级为 200 安全网，新增 maxTokenBudget
            if (settingsVersion < 14)
            {
                // 旧默认值 50 → 提升为 200（token budget 是真正的限制器）
                if (maxToolCallRounds <= 50)
                {
                    maxToolCallRounds = 200;
                    Debug.Log("[AgentCore] Settings migrated v13→v14: maxToolCallRounds raised to 200 (token budget is now the primary limiter)");
                }
                // maxTokenBudget 字段默认 0（不限制），无需迁移
                Debug.Log("[AgentCore] Settings migrated v13→v14: token budget system initialized (maxTokenBudget=0 means unlimited)");
            }

            // v14 → v15: 工具连续失败安全机制改进 — 两级响应 + 可配置阈值
            if (settingsVersion < 15)
            {
                // 新字段有合理默认值，无需迁移旧值
                // toolFailWarningThreshold = 3, toolFailBlockThreshold = 6, allToolsFailBlockThreshold = 4
                Debug.Log("[AgentCore] Settings migrated v14→v15: tool failure safety mechanism upgraded (warning/block two-level response)");
            }

            // v15 → v16: 确保 VCS 默认启用已应用（修复部分环境下 v12→v13 迁移未触发的问题）
            if (settingsVersion < 16)
            {
                if (!vcsDefaultEnabled)
                {
                    EditorApplication.delayCall += () => ApplyVcsDefaultEnablement(this, retry: true);
                }
                else
                {
                    Debug.Log("[AgentCore] Settings migrated v15→v16: VCS default enablement already applied");
                }
            }

            settingsVersion = CurrentVersion;

            // v1.4.0 fix: Save(true) cannot run inside ScriptableSingleton.OnEnable — Unity emits
            // "You may not pass in objects that are already persistent" because the asset file
            // is already being loaded when OnEnable fires. Defer to the next editor update.
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
        /// 应用 VCS 默认启用（v12→v13 / v15→v16 迁移）。
        /// 包装在 try/catch 中，并在首次调用后安排一次重试，以应对 Editor 启动早期
        /// PlayerSettings 尚未完全就绪的情况。成功后持久化 vcsDefaultEnabled 标记。
        /// </summary>
        /// <param name="settings">当前 Settings 实例。</param>
        /// <param name="retry">是否安排延迟重试。</param>
        private static void ApplyVcsDefaultEnablement(AgentCoreSettings settings, bool retry)
        {
            if (settings == null)
                return;

            try
            {
                if (!OptionalComponentManager.IsVcsEnabled())
                {
                    OptionalComponentManager.SetVcsEnabled(true);
                    Debug.Log("[AgentCore] VCS enabled by default; Code Indexing remains disabled (experimental, enable manually in Extensions settings if needed)");
                }
                else
                {
                    Debug.Log("[AgentCore] VCS already enabled; Code Indexing remains disabled (experimental, enable manually in Extensions settings if needed)");
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
                else
                {
                    Debug.LogError("[AgentCore] VCS default enablement could not be applied. You can enable it manually in Project Settings > AgentCore > Extensions.");
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
                // 解析 JSON 响应，假设返回格式为 { "data": [{ "id": "model1" }, { "id": "model2" }] }
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
            llmEndpoint = "http://localhost:4000/v1";
            llmModel = "claude-sonnet-4-5";
            temperature = 0.7f;
            maxTokens = 16000;
            maxToolCallRounds = 200;
            maxTokenBudget = 0;
            maxContextTokens = 0;
            reserveResponseTokens = 16000;
            autoCompileCheck = true;
            autoConsoleCapture = true;
            fallbackRoutingEnabled = true;
            maxConsecutiveErrors = 5;
            toolFailWarningThreshold = 3;
            toolFailBlockThreshold = 6;
            allToolsFailBlockThreshold = 4;
            bootstrapEnabled = true;
            autoProjectContext = true;
            mem0Enabled = false;
            mem0Endpoint = "";
            autoMemoryEnabled = false;
            autoMemoryMinTurns = 3;
            lightragEnabled = false;
            lightragEndpoint = "";
            disabledToolCategories = new List<string>();
            disabledTools = new List<string> { "execute_code" };
            toolScopingEnabled = true;
            compressionEnabled = true;
            useSeparateCompressionLLM = false;
            compressionLLMEndpoint = "";
            compressionLLMModel = "claude-3-haiku-20240307";
            toolResultCompressionThreshold = 2000;
            toolResultTargetTokens = 500;
            conversationCompressionTrigger = 0.7f;
            workspaceAutoDetectEnabled = true;
            workspaceRootOverride = "";
            unityRootRelativePathOverride = "";
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
        /// 当 SaveSettings() 被调用时触发，用于通知 UI 等订阅者刷新状态。
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
