using UnityEditor;

namespace AgentCore.Editor.Config
{
    /// <summary>
    /// API Key 安全存储。
    /// 使用 EditorPrefs 存储在操作系统级别，不进入项目文件或版本控制。
    /// </summary>
    public static class SecureKeyStorage
    {
        private const string LLM_API_KEY = "AgentCore_LLM_ApiKey";
        private const string COMPRESSION_LLM_API_KEY = "AgentCore_CompressionLLM_ApiKey";
        private const string MEM0_API_KEY = "AgentCore_Mem0_ApiKey";
        private const string LIGHTRAG_API_KEY = "AgentCore_LightRAG_ApiKey";

        // --- LLM API Key ---

        /// <summary>
        /// 设置 LLM API Key。
        /// </summary>
        public static void SetLLMApiKey(string key)
            => EditorPrefs.SetString(LLM_API_KEY, key ?? "");

        /// <summary>
        /// 获取 LLM API Key。
        /// </summary>
        public static string GetLLMApiKey()
            => EditorPrefs.GetString(LLM_API_KEY, "");

        /// <summary>
        /// 检查是否已设置 LLM API Key。
        /// </summary>
        public static bool HasLLMApiKey()
            => !string.IsNullOrEmpty(GetLLMApiKey());

        // --- Compression LLM API Key ---

        /// <summary>
        /// 设置压缩 LLM API Key。
        /// </summary>
        public static void SetCompressionLLMApiKey(string key)
            => EditorPrefs.SetString(COMPRESSION_LLM_API_KEY, key ?? "");

        /// <summary>
        /// 获取压缩 LLM API Key。
        /// </summary>
        public static string GetCompressionLLMApiKey()
            => EditorPrefs.GetString(COMPRESSION_LLM_API_KEY, "");

        /// <summary>
        /// 检查是否已设置压缩 LLM API Key。
        /// </summary>
        public static bool HasCompressionLLMApiKey()
            => !string.IsNullOrEmpty(GetCompressionLLMApiKey());

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

        /// <summary>
        /// 清除所有存储的 API Key。
        /// </summary>
        public static void ClearAll()
        {
            EditorPrefs.DeleteKey(LLM_API_KEY);
            EditorPrefs.DeleteKey(COMPRESSION_LLM_API_KEY);
            EditorPrefs.DeleteKey(MEM0_API_KEY);
            EditorPrefs.DeleteKey(LIGHTRAG_API_KEY);
        }
    }
}
