using System;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Config
{
    /// <summary>
    /// AgentCore 插件的全局设置。
    /// 使用 ScriptableSingleton 存储在 Library/ 目录下，不进版本控制。
    /// 通过 Project Settings > AgentCore 面板访问。
    /// </summary>
    [FilePath("AgentCore/Settings.asset", FilePathAttribute.Location.PreferencesFolder)]
    public class AgentCoreSettings : ScriptableSingleton<AgentCoreSettings>
    {
        // --- 版本迁移 ---
        [SerializeField] private int settingsVersion = 0;
        private const int CurrentVersion = 3;

        // --- LLM 配置 ---
        [Header("LLM Configuration")]
        [Tooltip("LLM API 端点地址（OpenAI 兼容）")]
        public string llmEndpoint = "http://localhost:4000/v1";

        [Tooltip("LLM 模型名称")]
        public string llmModel = "deepseek-chat";

        [Tooltip("生成温度 (0.0-2.0)")]
        [Range(0f, 2f)]
        public float temperature = 0.7f;

        [Tooltip("最大输出 token 数")]
        public int maxTokens = 4096;

        // --- Agent 行为 ---
        [Header("Agent Behavior")]
        [Tooltip("最大工具调用轮次（防止无限循环）")]
        public int maxToolCallRounds = 50;

        [Tooltip("上下文窗口 token 上限（0 = 自动根据模型名称推断）")]
        public int maxContextTokens = 0;

        [Tooltip("为 AI 回复预留的 token 数")]
        public int reserveResponseTokens = 2000;

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
        public string mem0Endpoint = "http://localhost:8765";

        [Tooltip("启用自动记忆策略（会话结束时自动提取关键信息存入 mem0）")]
        public bool autoMemoryEnabled = true;

        [Tooltip("触发自动记忆的最小用户对话轮次")]
        public int autoMemoryMinTurns = 3;

        // --- LightRAG 配置（Phase 3 使用，Phase 1 预留）---
        [Header("Knowledge Base - LightRAG")]
        [Tooltip("启用 LightRAG 知识库")]
        public bool lightragEnabled = false;

        [Tooltip("LightRAG 服务端点")]
        public string lightragEndpoint = "http://localhost:18920";

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
                Save(true);
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

            settingsVersion = CurrentVersion;
            Save(true);
        }

        /// <summary>
        /// 保存设置到磁盘。
        /// </summary>
        public void SaveSettings()
        {
            Save(true);
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
