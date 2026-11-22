using System;
using System.Collections.Generic;
using AgentCore.Editor.Utils;
using UnityEditor;

namespace AgentCore.Editor.Config
{
    /// <summary>
    /// API Key 安全存储。
    /// 使用 EditorPrefs 存储在操作系统级别，不进入项目文件或版本控制。
    /// <para>
    /// v1.18：Windows 上值经 DPAPI（CurrentUser）加密后再写入（见 <see cref="SecretProtector"/>）；
    /// 旧版写入的明文在首次读取时透明迁移为密文，读取旧值始终可用。其他平台保持明文。
    /// 读取不设任何确认 / 拦截。
    /// </para>
    /// </summary>
    public static class SecureKeyStorage
    {
        private const string MEM0_API_KEY = "AgentCore_Mem0_ApiKey";
        private const string LIGHTRAG_API_KEY = "AgentCore_LightRAG_ApiKey";
        private const string VISION_API_KEY = "AgentCore_Vision_ApiKey";

        // v1.13.0: Provider Profile 分键前缀。每个 profile 的 apiKey 存储为
        // "AgentCore_ProfileKey_<profileId>"。LLM apiKey 单一真源 = 各 profile 分键存储。
        internal const string PROFILE_KEY_PREFIX = "AgentCore_ProfileKey_";

        // --- mem0 API Key（Phase 3 使用）---

        /// <summary>
        /// 设置 mem0 API Key。
        /// </summary>
        public static void SetMem0ApiKey(string key)
            => WriteSecret(MEM0_API_KEY, key);

        /// <summary>
        /// 获取 mem0 API Key。
        /// </summary>
        public static string GetMem0ApiKey()
            => ReadSecret(MEM0_API_KEY);

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
            => WriteSecret(LIGHTRAG_API_KEY, key);

        /// <summary>
        /// 获取 LightRAG API Key。
        /// </summary>
        public static string GetLightRAGApiKey()
            => ReadSecret(LIGHTRAG_API_KEY);

        /// <summary>
        /// 检查是否已设置 LightRAG API Key。
        /// </summary>
        public static bool HasLightRAGApiKey()
            => !string.IsNullOrEmpty(GetLightRAGApiKey());

        // --- Vision API Key (视觉模型独立配置, v1.15.0) ---
        // 与主模型 Provider Profile 完全隔离的固定单键:视觉模型只有一个配置,不可热切换。
        // 不进 git,不参与 profile 分键。

        /// <summary>
        /// 设置视觉模型 API Key(固定单键,与主模型 profile 隔离)。
        /// </summary>
        public static void SetVisionApiKey(string key)
            => WriteSecret(VISION_API_KEY, key);

        /// <summary>
        /// 获取视觉模型 API Key。
        /// </summary>
        public static string GetVisionApiKey()
            => ReadSecret(VISION_API_KEY);

        /// <summary>
        /// 检查是否已设置视觉模型 API Key。
        /// </summary>
        public static bool HasVisionApiKey()
            => !string.IsNullOrEmpty(GetVisionApiKey());

        /// <summary>
        /// 删除视觉模型 API Key。
        /// </summary>
        public static void DeleteVisionApiKey()
            => EditorPrefs.DeleteKey(VISION_API_KEY);

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
            WriteSecret(PROFILE_KEY_PREFIX + profileId, key);
        }

        /// <summary>
        /// 获取指定 profile 的 API Key。profileId 为空返回空字符串。
        /// </summary>
        public static string GetProfileApiKey(string profileId)
        {
            if (string.IsNullOrEmpty(profileId))
                return "";
            return ReadSecret(PROFILE_KEY_PREFIX + profileId);
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

        // --- 静态加密 + 旧明文迁移（v1.18）---

        private static readonly object WarnLock = new();
        private static readonly HashSet<string> WarnedKeys = new();

        /// <summary>
        /// 把 EditorPrefs 中存储的原始值还原为明文（纯函数，不读写 EditorPrefs）。
        /// <para>
        /// - 加密值：解密后返回；解密失败返回空字符串（原值保持不动，不删除）。<br/>
        /// - 旧版明文：原样返回；若当前平台可加密，<paramref name="migrated"/> 给出应写回的密文。
        /// </para>
        /// </summary>
        internal static string Reveal(string stored, out string migrated, out bool unreadable)
        {
            migrated = null;
            unreadable = false;
            if (string.IsNullOrEmpty(stored))
                return "";

            if (SecretProtector.IsProtected(stored))
            {
                if (SecretProtector.TryUnprotect(stored, out var plain))
                    return plain;
                unreadable = true;
                return "";
            }

            if (SecretProtector.TryProtect(stored, out var protectedValue))
                migrated = protectedValue;
            return stored;
        }

        /// <summary>明文 → 存储值。可加密时返回密文，否则（非 Windows / 加密失败）返回明文。</summary>
        internal static string Conceal(string plain)
        {
            if (string.IsNullOrEmpty(plain))
                return "";
            return SecretProtector.TryProtect(plain, out var protectedValue) ? protectedValue : plain;
        }

        private static string ReadSecret(string prefKey)
        {
            var stored = EditorPrefs.GetString(prefKey, "");
            var plain = Reveal(stored, out var migrated, out var unreadable);

            if (migrated != null)
            {
                // 旧版明文透明迁移：写回密文。写入失败（如非主线程调用）不影响本次读取，下次再迁移。
                try
                {
                    EditorPrefs.SetString(prefKey, migrated);
                }
                catch (Exception)
                {
                    // 保留明文，下次读取再试
                }
            }
            else if (unreadable)
            {
                lock (WarnLock)
                {
                    if (WarnedKeys.Add(prefKey))
                    {
                        AgentCoreLog.Warning($"[AgentCore] Stored secret '{prefKey}' could not be decrypted for the current Windows user. " +
                            "The stored value is kept untouched; set the key again in Preferences > AgentCore if needed.");
                    }
                }
            }

            return plain;
        }

        private static void WriteSecret(string prefKey, string plain)
        {
            EditorPrefs.SetString(prefKey, Conceal(plain));
            lock (WarnLock)
            {
                WarnedKeys.Remove(prefKey);
            }
        }
    }
}
