using System;
using System.Collections.Generic;
using System.IO;
using AgentCore.Editor.Utils;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.L10n
{
    /// <summary>
    /// 语言资源加载器: 从磁盘 JSON 加载 key -&gt; localized text 字典.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 资源位置: <c>Packages/com.agentcore.unity/Editor/L10n/Resources/{lang}.json</c>.
    /// 格式: 扁平 <c>{"key": "value", ...}</c> JSON 字典. Key 用点分层命名 (如 <c>chat.status.idle</c>).
    /// </para>
    /// <para>
    /// 缓存策略: 加载后进程内缓存, <see cref="Reload"/> 显式失效. 语言切换时由
    /// <see cref="LanguageManager"/> 调用 <see cref="Reload"/> 强制重加载.
    /// </para>
    /// <para>
    /// 缺 key fallback: 先当前语言 → 英文兜底 → 传入的 fallback → key 本身. 不抛异常.
    /// </para>
    /// </remarks>
    internal static class LanguageResourceLoader
    {
        private const string ResourceDirectory = "Packages/com.agentcore.unity/Editor/L10n/Resources";

        /// <summary>当前语言字典 (lazy-load).</summary>
        private static Dictionary<string, string> _currentDict;

        /// <summary>英文兜底字典 (lazy-load, 供缺 key 时 fallback).</summary>
        private static Dictionary<string, string> _fallbackDict;

        /// <summary>已加载的语言 code, 用于判断是否需要重加载.</summary>
        private static string _loadedLanguage;

        /// <summary>
        /// 获取 key 对应的本地化文本. 不存在时按 fallback 链尝试.
        /// </summary>
        internal static string Get(string key, string fallback)
        {
            if (string.IsNullOrEmpty(key)) return fallback ?? string.Empty;

            EnsureLoaded();

            if (_currentDict != null && _currentDict.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
            {
                return value;
            }

            // Fallback 到英文
            if (_fallbackDict != null && _fallbackDict.TryGetValue(key, out var enValue) && !string.IsNullOrEmpty(enValue))
            {
                return enValue;
            }

            // 最后 fallback: 调用方传入的 fallback, 或 key 本身
            return fallback ?? key;
        }

        /// <summary>强制重加载 (语言切换时调用).</summary>
        internal static void Reload()
        {
            _currentDict = null;
            _loadedLanguage = null;
        }

        private static void EnsureLoaded()
        {
            var lang = LanguageManager.CurrentLanguage;

            // 首次或语言切换时重加载
            if (_currentDict == null || _loadedLanguage != lang)
            {
                _currentDict = LoadLanguageFile(lang);
                _loadedLanguage = lang;
            }

            // 英文 fallback 只加载一次
            if (_fallbackDict == null)
            {
                _fallbackDict = string.Equals(lang, LanguageManager.DefaultLanguage, StringComparison.OrdinalIgnoreCase)
                    ? _currentDict
                    : LoadLanguageFile(LanguageManager.DefaultLanguage);
            }
        }

        private static Dictionary<string, string> LoadLanguageFile(string languageCode)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            var path = Path.Combine(ResourceDirectory, languageCode + ".json").Replace('\\', '/');

            try
            {
                if (!File.Exists(path))
                {
                    AgentCoreLog.Warning($"[AgentCore.L10n] Language file not found: {path}");
                    return dict;
                }

                var text = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(text))
                {
                    return dict;
                }

                var jobj = JObject.Parse(text);
                foreach (var prop in jobj.Properties())
                {
                    if (prop.Value != null && prop.Value.Type == JTokenType.String)
                    {
                        dict[prop.Name] = (string)prop.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore.L10n] Failed to load language file '{path}': {ex.Message}");
            }

            return dict;
        }
    }
}
