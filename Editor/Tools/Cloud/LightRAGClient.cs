using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using AgentCore.Editor.Config;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Cloud
{
    // ─────────────────────────────────────────────
    //  数据模型
    // ─────────────────────────────────────────────

    /// <summary>
    /// LightRAG 查询结果。
    /// </summary>
    [Serializable]
    public class LightRAGQueryResult
    {
        public bool Success;
        public string Response;
        public List<LightRAGSource> Sources;
    }

    /// <summary>
    /// LightRAG 查询来源条目。
    /// </summary>
    [Serializable]
    public class LightRAGSource
    {
        [JsonProperty("content")]
        public string Content;

        [JsonProperty("score")]
        public float Score;

        [JsonProperty("metadata")]
        public Dictionary<string, object> Metadata;
    }

    /// <summary>
    /// LightRAG 健康状态信息。
    /// </summary>
    [Serializable]
    public class LightRAGHealthInfo
    {
        public bool IsHealthy;
        public string Version;
        public int DocumentCount;
    }

    // ─────────────────────────────────────────────
    //  内部 API 响应模型
    // ─────────────────────────────────────────────

    [Serializable]
    internal class RAGQueryResponse
    {
        [JsonProperty("response")]
        public string Response;

        /// <summary>LightRAG v1.4.x 使用 "references" 字段</summary>
        [JsonProperty("references")]
        public List<LightRAGSource> References;

        /// <summary>兼容旧版本的 "sources" 字段</summary>
        [JsonProperty("sources")]
        public List<LightRAGSource> Sources;

        /// <summary>获取来源列表（优先 references，回退 sources）</summary>
        public List<LightRAGSource> GetSources() => References ?? Sources;
    }

    [Serializable]
    internal class RAGIndexResponse
    {
        [JsonProperty("status")]
        public string Status;

        [JsonProperty("document_id")]
        public string DocumentId;
    }

    [Serializable]
    internal class RAGHealthResponse
    {
        [JsonProperty("status")]
        public string Status;

        /// <summary>LightRAG v1.4.x 使用 "core_version" 字段</summary>
        [JsonProperty("core_version")]
        public string CoreVersion;

        /// <summary>兼容旧版本的 "version" 字段</summary>
        [JsonProperty("version")]
        public string Version;

        /// <summary>LightRAG v1.4.x 使用 "api_version" 字段</summary>
        [JsonProperty("api_version")]
        public string ApiVersion;

        [JsonProperty("document_count")]
        public int DocumentCount;
    }

    // ─────────────────────────────────────────────
    //  客户端
    // ─────────────────────────────────────────────

    /// <summary>
    /// LightRAG 云服务 HTTP 客户端。
    /// 封装 LightRAG REST API 调用，提供知识库的索引和查询操作。
    /// </summary>
    public class LightRAGClient
    {
        private readonly string _baseUrl;
        private readonly string _apiKey;

        /// <summary>请求超时时间（秒）</summary>
        private const int TimeoutSeconds = 30;

        /// <summary>
        /// 创建 LightRAGClient 实例。
        /// </summary>
        /// <param name="baseUrl">LightRAG 服务端点 URL</param>
        /// <param name="apiKey">API Key（可为空）</param>
        public LightRAGClient(string baseUrl, string apiKey)
        {
            _baseUrl = baseUrl?.TrimEnd('/')
                ?? throw new ArgumentNullException(nameof(baseUrl));
            _apiKey = apiKey;
        }

        /// <summary>
        /// 从 AgentCoreSettings 创建客户端实例。
        /// </summary>
        public static LightRAGClient FromSettings()
        {
            var settings = AgentCoreSettings.instance;
            return new LightRAGClient(
                settings.lightragEndpoint,
                SecureKeyStorage.GetLightRAGApiKey()
            );
        }

        // ─────────────────────────────────────────
        //  公共 API
        // ─────────────────────────────────────────

        /// <summary>
        /// 查询知识库。
        /// </summary>
        /// <param name="query">查询文本</param>
        /// <param name="mode">检索模式：naive / local / global / hybrid</param>
        /// <param name="topK">返回结果数量上限</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>查询结果</returns>
        public async Task<LightRAGQueryResult> QueryAsync(
            string query,
            string mode = "hybrid",
            int topK = 5,
            CancellationToken ct = default)
        {
            try
            {
                var payload = new JObject
                {
                    ["query"] = query,
                    ["mode"] = mode,
                    ["top_k"] = topK
                };

                var response = await PostAsync<RAGQueryResponse>("/query", payload, ct);

                return new LightRAGQueryResult
                {
                    Success = true,
                    Response = response?.Response ?? "",
                    Sources = response?.GetSources() ?? new List<LightRAGSource>()
                };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AgentCore] LightRAGClient.QueryAsync failed: {ex.Message}");
                return new LightRAGQueryResult
                {
                    Success = false,
                    Response = ex.Message,
                    Sources = new List<LightRAGSource>()
                };
            }
        }

        /// <summary>
        /// 索引文本到知识库。
        /// </summary>
        /// <param name="text">要索引的文本内容</param>
        /// <param name="description">文档描述（可选）</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>是否索引成功</returns>
        public async Task<bool> IndexTextAsync(
            string text,
            string description = null,
            CancellationToken ct = default)
        {
            try
            {
                var payload = new JObject
                {
                    ["text"] = text
                };

                if (!string.IsNullOrEmpty(description))
                {
                    payload["description"] = description;
                }

                var response = await PostAsync<RAGIndexResponse>("/documents/text", payload, ct);
                return response != null;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AgentCore] LightRAGClient.IndexTextAsync failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 索引文件到知识库（multipart/form-data 上传）。
        /// </summary>
        /// <param name="filePath">本地文件路径</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>是否索引成功</returns>
        public async Task<bool> IndexFileAsync(
            string filePath,
            CancellationToken ct = default)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    Debug.LogError($"[AgentCore] LightRAGClient.IndexFileAsync: File not found: {filePath}");
                    return false;
                }

                var url = $"{_baseUrl}/documents/upload";
                var client = HttpClientFactory.GetClient();

                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                if (!string.IsNullOrEmpty(_apiKey))
                {
                    request.Headers.Authorization =
                        new AuthenticationHeaderValue("Bearer", _apiKey);
                }

                var fileBytes = File.ReadAllBytes(filePath);
                var fileName = Path.GetFileName(filePath);

                using var formContent = new MultipartFormDataContent();
                var fileContent = new ByteArrayContent(fileBytes);
                fileContent.Headers.ContentType =
                    new MediaTypeHeaderValue("application/octet-stream");
                formContent.Add(fileContent, "file", fileName);

                request.Content = formContent;

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

                var response = await client.SendAsync(request, cts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    Debug.LogError(
                        $"[AgentCore] LightRAGClient.IndexFileAsync error: " +
                        $"{(int)response.StatusCode} {response.ReasonPhrase} - {responseBody}");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AgentCore] LightRAGClient.IndexFileAsync failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 测试与 LightRAG 服务的连接。
        /// </summary>
        /// <param name="ct">取消令牌</param>
        /// <returns>连接是否成功</returns>
        public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
        {
            try
            {
                var healthInfo = await GetHealthAsync(ct);
                return healthInfo.IsHealthy;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentCore] LightRAGClient.TestConnectionAsync failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取 LightRAG 服务的健康状态和统计信息。
        /// </summary>
        /// <param name="ct">取消令牌</param>
        /// <returns>健康状态信息</returns>
        public async Task<LightRAGHealthInfo> GetHealthAsync(CancellationToken ct = default)
        {
            try
            {
                var url = $"{_baseUrl}/health";
                var response = await GetAsync<RAGHealthResponse>(url, ct);

                return new LightRAGHealthInfo
                {
                    IsHealthy = response != null &&
                                (string.Equals(response.Status, "ok", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(response.Status, "healthy", StringComparison.OrdinalIgnoreCase)),
                    Version = response?.CoreVersion ?? response?.Version ?? "unknown",
                    DocumentCount = response?.DocumentCount ?? 0
                };
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentCore] LightRAGClient.GetHealthAsync failed: {ex.Message}");
                return new LightRAGHealthInfo
                {
                    IsHealthy = false,
                    Version = "unknown",
                    DocumentCount = 0
                };
            }
        }

        // ─────────────────────────────────────────
        //  内部 HTTP 辅助方法
        // ─────────────────────────────────────────

        private async Task<T> PostAsync<T>(string path, JObject payload, CancellationToken ct)
        {
            var url = $"{_baseUrl}{path}";
            var client = HttpClientFactory.GetClient();
            using var request = HttpClientFactory.CreateRequest(HttpMethod.Post, url, _apiKey);

            var json = payload.ToString(Formatting.None);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

            var response = await client.SendAsync(request, cts.Token);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"LightRAG API error: {(int)response.StatusCode} {response.ReasonPhrase} - {responseBody}");
            }

            return JsonConvert.DeserializeObject<T>(responseBody);
        }

        private async Task<T> GetAsync<T>(string url, CancellationToken ct)
        {
            var client = HttpClientFactory.GetClient();
            using var request = HttpClientFactory.CreateRequest(HttpMethod.Get, url, _apiKey);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

            var response = await client.SendAsync(request, cts.Token);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"LightRAG API error: {(int)response.StatusCode} {response.ReasonPhrase} - {responseBody}");
            }

            return JsonConvert.DeserializeObject<T>(responseBody);
        }
    }
}
