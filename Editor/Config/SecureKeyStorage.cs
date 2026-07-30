using UnityEditor;

namespace AgentCore.Editor.Config
{
    /// <summary>
    /// API Key 安全存储。
    /// 使用 EditorPrefs 存储在操作系统级别，不进入项目文件或版本控制。
    /// </summary>
    public static class SecureKeyStorage
    {
        private const string MEM0_API_KEY = "AgentCore_Mem0_ApiKey";
        private const string LIGHTRAG_API_KEY = "AgentCore_LightRAG_ApiKey";

        // v1.13.0: Provider Profile 分键前缀。每个 profile 的 apiKey 存储为
        // "AgentCore_ProfileKey_<profileId>"。LLM apiKey 单一真源 = 各 profile 分键存储。
        private const string PROFILE_KEY_PREFIX = "AgentCore_ProfileKey_";

        // --- mem0 API Key（Phase 3 使用）---

        /// <summary>
        /// 设置 mem0 API Key。
        /// </summary>
        public static void SetMem0ApiKey(string key)
            => EditorPrefs.SetString(MEM0_API_KEY, key ?? "");

        /// <summary>
        /// 获取 mem0 API Key。
        /// </summary>
        public static string GetMem0ApiKey()
            => EditorPrefs.GetString(MEM0_API_KEY, "");

        /// <summary>
        /// 检查是否已设置 mem0 API Key。
        /// </summary>
        public static bool HasMem0ApiKey()
            => !string.IsNullOrEmpty(GetMem0ApiKey());

        // --- LightRAG API Key（Phase 3 使用）---

        /// <summary>
        /// 设置 LightRAG API Key。
        /// </summary>
        public static void SetLightRAGApiKey(string key)
            => EditorPrefs.SetString(LIGHTRAG_API_KEY, key ?? "");

        /// <summary>
        /// 获取 LightRAG API Key。
        /// </summary>
        public static string GetLightRAGApiKey()
            => EditorPrefs.GetString(LIGHTRAG_API_KEY, "");

        /// <summary>
        /// 检查是否已设置 LightRAG API Key。
        /// </summary>
        public static bool HasLightRAGApiKey()
            => !string.IsNullOrEmpty(GetLightRAGApiKey());

        // --- Provider Profile API Key（v1.13.0）---
        // 按 profileId 分键存储，不进 git，不与 legacy LLM_API_KEY 冲突。
        // profileId 为空视为无效（legacy 模式），相关方法安全 no-op / 返回空。

        /// <summary>
        /// 设置指定 profile 的 API Key。profileId 为空则 no-op。
        /// </summary>
        public static void SetProfileApiKey(string profileId, string key)
        {
            if (string.IsNullOrEmpty(profileId))
                return;
            EditorPrefs.SetString(PROFILE_KEY_PREFIX + profileId, key ?? "");
        }

        /// <summary>
        /// 获取指定 profile 的 API Key。profileId 为空返回空字符串。
        /// </summary>
        public static string GetProfileApiKey(string profileId)
        {
            if (string.IsNullOrEmpty(profileId))
                return "";
            return EditorPrefs.GetString(PROFILE_KEY_PREFIX + profileId, "");
        }

        /// <summary>
        /// 检查指定 profile 是否已设置 API Key。profileId 为空返回 false。
        /// </summary>
        public static bool HasProfileApiKey(string profileId)
            => !string.IsNullOrEmpty(GetProfileApiKey(profileId));

        /// <summary>
        /// 删除指定 profile 的 API Key。profileId 为空则 no-op。
        /// 删除 profile 时由 <c>AgentCoreProviderProfiles.RemoveProfile</c> 调用，避免 EditorPrefs 键膨胀。
        /// </summary>
        public static void DeleteProfileApiKey(string profileId)
        {
            if (string.IsNullOrEmpty(profileId))
                return;
            EditorPrefs.DeleteKey(PROFILE_KEY_PREFIX + profileId);
        }

        /// <summary>
        /// 清除所有存储的 API Key。
        /// </summary>
        public static void ClearAll()
        {
            EditorPrefs.DeleteKey(MEM0_API_KEY);
            EditorPrefs.DeleteKey(LIGHTRAG_API_KEY);
        }
    }
}
