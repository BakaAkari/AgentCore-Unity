using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AgentCore.Editor.Utils
{
    /// <summary>
    /// JSON 序列化/反序列化工具类，封装 Newtonsoft.Json 常用操作。
    /// 统一错误处理，避免 JSON 解析异常导致 Agent Loop 中断。
    /// </summary>
    public static class JsonHelper
    {
        private static readonly JsonSerializerSettings DefaultSettings = new()
        {
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.None,
            DateFormatString = "yyyy-MM-ddTHH:mm:ssZ"
        };

        private static readonly JsonSerializerSettings PrettySettings = new()
        {
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.Indented,
            DateFormatString = "yyyy-MM-ddTHH:mm:ssZ"
        };

        /// <summary>
        /// 将对象序列化为 JSON 字符串。
        /// </summary>
        public static string Serialize(object obj, bool pretty = false)
        {
            try
            {
                return JsonConvert.SerializeObject(obj, pretty ? PrettySettings : DefaultSettings);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AgentCore] JSON serialize error: {ex.Message}");
                return "{}";
            }
        }

        /// <summary>
        /// 将 JSON 字符串反序列化为指定类型。
        /// 失败时返回 default(T) 而非抛出异常。
        /// </summary>
        public static T Deserialize<T>(string json)
        {
            if (string.IsNullOrEmpty(json)) return default;

            try
            {
                return JsonConvert.DeserializeObject<T>(json, DefaultSettings);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AgentCore] JSON deserialize error: {ex.Message}\nJSON: {Truncate(json, 200)}");
                return default;
            }
        }

        /// <summary>
        /// 安全解析 JSON 字符串为 JObject。
        /// 失败时返回 null 而非抛出异常。
        /// </summary>
        public static JObject ParseObject(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;

            try
            {
                return JObject.Parse(json);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AgentCore] JSON parse error: {ex.Message}\nJSON: {Truncate(json, 200)}");
                return null;
            }
        }

        /// <summary>
        /// 安全解析 JSON 字符串为 JArray。
        /// 失败时返回 null 而非抛出异常。
        /// </summary>
        public static JArray ParseArray(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;

            try
            {
                return JArray.Parse(json);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AgentCore] JSON array parse error: {ex.Message}\nJSON: {Truncate(json, 200)}");
                return null;
            }
        }

        /// <summary>
        /// 从 JObject 中安全获取字符串值。
        /// </summary>
        public static string GetString(JObject obj, string key, string defaultValue = null)
        {
            if (obj == null) return defaultValue;
            var token = obj[key];
            return token?.Type == JTokenType.String ? token.Value<string>() : defaultValue;
        }

        /// <summary>
        /// 从 JObject 中安全获取整数值。
        /// </summary>
        public static int GetInt(JObject obj, string key, int defaultValue = 0)
        {
            if (obj == null) return defaultValue;
            var token = obj[key];
            return token?.Type == JTokenType.Integer ? token.Value<int>() : defaultValue;
        }

        /// <summary>
        /// 从 JObject 中安全获取布尔值。
        /// </summary>
        public static bool GetBool(JObject obj, string key, bool defaultValue = false)
        {
            if (obj == null) return defaultValue;
            var token = obj[key];
            return token?.Type == JTokenType.Boolean ? token.Value<bool>() : defaultValue;
        }

        /// <summary>
        /// 截断字符串用于日志输出。
        /// </summary>
        private static string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength) return text;
            return text.Substring(0, maxLength) + "...";
        }
    }
}
