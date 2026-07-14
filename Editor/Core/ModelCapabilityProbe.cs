using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Utils;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AgentCore.Editor.Core
{
    /// <summary>
    /// 模型能力探测器 — 启动时调用 /v1/models 端点，自动获取模型的实际能力参数。
    /// <para>
    /// 探测内容：
    /// <list type="bullet">
    ///   <item>max_model_len — 模型实际 context window 大小（覆盖 ContextWindowManager.ModelPrefixMap 的硬编码）</item>
    ///   <item>模型 ID — 用于验证 settings.llmModel 是否存在于服务器</item>
    /// </list>
    /// </para>
    /// <para>
    /// 设计原则：
    /// <list type="bullet">
    ///   <item>零用户配置 — 启动时自动探测，失败时 fallback 到 ModelPrefixMap</item>
    ///   <item>不持久化 — 每次启动拿最新值，模型升级/服务器配置变更自动跟进</item>
    ///   <item>线程安全 — 缓存用 volatile + lock 保护</item>
    ///   <item>非阻塞 — 异步探测，探测完成前用 fallback 值，完成后热更新</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class ModelCapabilityProbe
    {
        #region 缓存

        private static volatile bool _probeCompleted;
        private static volatile int _cachedMaxModelLen;
        private static volatile string _cachedModelId;
        private static readonly object _lock = new();

        #endregion

        #region 公开属性

        /// <summary>
        /// 探测是否已完成（成功或失败都算完成）。
        /// </summary>
        public static bool IsProbeCompleted => _probeCompleted;

        /// <summary>
        /// 获取探测到的 max_model_len。探测未完成或失败时返回 0（调用方应 fallback）。
        /// </summary>
        public static int CachedMaxModelLen => _cachedMaxModelLen;

        /// <summary>
        /// 获取探测到的模型 ID。探测未完成或失败时返回 null。
        /// </summary>
        public static string CachedModelId => _cachedModelId;

        #endregion

        #region 公开方法

        /// <summary>
        /// 异步探测模型能力。调用 /v1/models 端点，解析 max_model_len。
        /// 失败时静默 fallback 到 ContextWindowManager.ModelPrefixMap。
        /// </summary>
        /// <param name="endpoint">LLM API base URL（如 http://172.16.248.60:8000/v1）</param>
        /// <param name="apiKey">API key（可为空）</param>
        /// <param name="ct">取消令牌</param>
        public static async Task ProbeAsync(string endpoint, string apiKey, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                AgentCoreLog.Warning("[AgentCore] ModelCapabilityProbe: endpoint 为空，跳过探测");
                lock (_lock) { _probeCompleted = true; }
                return;
            }

            try
            {
                var url = $"{endpoint.TrimEnd('/')}/models";
                var client = HttpClientFactory.GetClient();
                using var request = HttpClientFactory.CreateRequest(HttpMethod.Get, url, apiKey);
                using var response = await client.SendAsync(request, ct);

                if (!response.IsSuccessStatusCode)
                {
                    AgentCoreLog.Warning($"[AgentCore] ModelCapabilityProbe: /v1/models 返回 HTTP {(int)response.StatusCode}，使用 fallback");
                    lock (_lock) { _probeCompleted = true; }
                    return;
                }

                var json = await response.Content.ReadAsStringAsync();
                var parsed = ParseModelsResponse(json);

                lock (_lock)
                {
                    _cachedMaxModelLen = parsed.maxModelLen;
                    _cachedModelId = parsed.modelId;
                    _probeCompleted = true;
                }

                if (parsed.maxModelLen > 0)
                {
                    AgentCoreLog.Info($"[AgentCore] ModelCapabilityProbe: 探测成功 model={parsed.modelId} max_model_len={parsed.maxModelLen}");
                }
                else
                {
                    AgentCoreLog.Info($"[AgentCore] ModelCapabilityProbe: /v1/models 未返回 max_model_len，使用 fallback");
                }
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] ModelCapabilityProbe: 探测失败 ({ex.Message})，使用 fallback");
                lock (_lock) { _probeCompleted = true; }
            }
        }

        /// <summary>
        /// 获取模型实际 max_model_len。优先返回探测值，fallback 到 ContextWindowManager.ModelPrefixMap。
        /// </summary>
        /// <param name="modelName">模型名称</param>
        /// <returns>max_model_len</returns>
        public static int GetMaxModelLen(string modelName)
        {
            // 探测值优先
            if (_cachedMaxModelLen > 0)
                return _cachedMaxModelLen;

            // Fallback: 前缀表
            return ContextWindowManager.GetModelMaxTokens(modelName);
        }

        #endregion

        #region 私有方法

        private static (string modelId, int maxModelLen) ParseModelsResponse(string json)
        {
            string modelId = null;
            int maxModelLen = 0;

            try
            {
                var jobj = JsonHelper.ParseObject(json);
                if (jobj != null && jobj.TryGetValue("data", out var dataToken) && dataToken is JArray arr)
                {
                    foreach (var item in arr)
                    {
                        var id = item["id"]?.ToString();
                        var len = item["max_model_len"]?.Type == JTokenType.Integer
                            ? (int)item["max_model_len"]
                            : 0;

                        // 取第一个模型的 max_model_len
                        if (!string.IsNullOrEmpty(id) && len > 0)
                        {
                            modelId = id;
                            maxModelLen = len;
                            break;
                        }

                        // 如果没有 max_model_len 字段，至少记录 model ID
                        if (modelId == null && !string.IsNullOrEmpty(id))
                        {
                            modelId = id;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] ModelCapabilityProbe: 解析响应失败 ({ex.Message})");
            }

            return (modelId, maxModelLen);
        }

        #endregion
    }
}
