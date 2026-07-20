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
    //  数据模型 — 查询
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
    //  数据模型 — 文档管理
    // ─────────────────────────────────────────────

    /// <summary>
    /// LightRAG 知识库中的文档条目（来自 GET /documents）。
    /// </summary>
    [Serializable]
    public class LightRAGDocument
    {
        /// <summary>文档唯一 ID，用于删除操作</summary>
        [JsonProperty("id")]
        public string Id;

        /// <summary>文档文件路径（服务端存储路径或原始文件名）</summary>
        [JsonProperty("file_path")]
        public string FilePath;

        /// <summary>文档内容摘要（由 LightRAG 自动生成）</summary>
        [JsonProperty("content_summary")]
        public string ContentSummary;

        /// <summary>文档内容字节长度</summary>
        [JsonProperty("content_length")]
        public int ContentLength;

        /// <summary>索引状态：processed / pending / processing / failed</summary>
        [JsonProperty("status")]
        public string Status;

        /// <summary>创建时间（ISO 8601 字符串）</summary>
        [JsonProperty("created_at")]
        public string CreatedAt;

        /// <summary>最后更新时间（ISO 8601 字符串）</summary>
        [JsonProperty("updated_at")]
        public string UpdatedAt;

        /// <summary>上传时返回的追踪 ID，用于轮询索引进度</summary>
        [JsonProperty("track_id")]
        public string TrackId;

        /// <summary>文档被分割的块数量</summary>
        [JsonProperty("chunks_count")]
        public int ChunksCount;

        /// <summary>索引失败时的错误信息</summary>
        [JsonProperty("error_msg")]
        public string ErrorMsg;
    }

    /// <summary>
    /// 文件上传（IndexFileAsync）的返回结果。
    /// 区分"上传成功"和"索引完成"两个阶段。
    /// </summary>
    public class LightRAGIndexResult
    {
        /// <summary>HTTP 上传是否被服务端接受（200 OK）</summary>
        public bool Accepted;

        /// <summary>
        /// 用于轮询索引进度的追踪 ID。
        /// 为 null 时表示服务端未返回 track_id，无法追踪进度。
        /// </summary>
        public string TrackId;

        /// <summary>上传失败时的错误信息</summary>
        public string ErrorMessage;
    }

    /// <summary>
    /// GET /documents/track_status/{track_id} 的响应。
    /// </summary>
    [Serializable]
    public class LightRAGTrackStatus
    {
        /// <summary>当前状态：pending / processing / processed / failed</summary>
        [JsonProperty("status")]
        public string Status;

        /// <summary>失败时的错误信息</summary>
        [JsonProperty("error_msg")]
        public string ErrorMsg;

        /// <summary>处理完成后的文档 ID</summary>
        [JsonProperty("document_id")]
        public string DocumentId;
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

    [Serializable]
    internal class RAGDocumentsResponse
    {
        [JsonProperty("statuses")]
        public RAGDocumentStatuses Statuses;
    }

    [Serializable]
    internal class RAGDocumentStatuses
    {
        [JsonProperty("processed")]
        public List<LightRAGDocument> Processed;

        [JsonProperty("pending")]
        public List<LightRAGDocument> Pending;

        [JsonProperty("failed")]
        public List<LightRAGDocument> Failed;
    }

    /// <summary>
    /// POST /documents/upload 的响应（用于提取 track_id）。
    /// </summary>
    [Serializable]
    internal class RAGUploadResponse
    {
        /// <summary>LightRAG v1.4.x 返回的追踪 ID</summary>
        [JsonProperty("track_id")]
        public string TrackId;

        /// <summary>兼容字段：部分版本使用 "id"</summary>
        [JsonProperty("id")]
        public string Id;

        /// <summary>兼容字段：部分版本使用 "document_id"</summary>
        [JsonProperty("document_id")]
        public string DocumentId;

        [JsonProperty("status")]
        public string Status;

        /// <summary>获取 track_id（多字段兼容）</summary>
        public string GetTrackId() => TrackId ?? Id ?? DocumentId;
    }

    // ─────────────────────────────────────────────
    //  客户端
    // ─────────────────────────────────────────────

    /// <summary>
    /// LightRAG 云服务 HTTP 客户端。
    /// 封装 LightRAG REST API 调用，提供知识库的索引、查询和文档管理操作。
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
        //  公共 API — 查询与索引
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
                AgentCoreLog.Error($"[AgentCore] LightRAGClient.QueryAsync failed: {ex.Message}");
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
                AgentCoreLog.Error($"[AgentCore] LightRAGClient.IndexTextAsync failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 上传文件到知识库（multipart/form-data）。
        /// 注意：上传成功（Accepted = true）不等于索引完成，
        /// 需要通过 TrackStatusAsync(result.TrackId) 轮询真实进度。
        /// </summary>
        /// <param name="filePath">本地文件路径</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>上传结果，包含 TrackId 用于进度追踪</returns>
        public async Task<LightRAGIndexResult> IndexFileAsync(
            string filePath,
            CancellationToken ct = default)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    AgentCoreLog.Error($"[AgentCore] LightRAGClient.IndexFileAsync: File not found: {filePath}");
                    return new LightRAGIndexResult
                    {
                        Accepted = false,
                        ErrorMessage = $"文件不存在：{filePath}"
                    };
                }

                var url = $"{_baseUrl}/documents/upload";
                var client = HttpClientFactory.GetClient();

                var fileBytes = File.ReadAllBytes(filePath);
                var fileName = Path.GetFileName(filePath);

                // 注意：不对 formContent 使用 using，由 request 负责 dispose 其 Content，
                // 避免 using var 逆序 dispose 导致 Content 在 SendAsync 完成前被释放。
                var formContent = new MultipartFormDataContent();
                var fileContent = new ByteArrayContent(fileBytes);
                fileContent.Headers.ContentType =
                    new MediaTypeHeaderValue("application/octet-stream");
                formContent.Add(fileContent, "file", fileName);

                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                if (!string.IsNullOrEmpty(_apiKey))
                {
                    request.Headers.Authorization =
                        new AuthenticationHeaderValue("Bearer", _apiKey);
                }
                request.Content = formContent;

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

                var response = await client.SendAsync(request, cts.Token);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    AgentCoreLog.Error(
                        $"[AgentCore] LightRAGClient.IndexFileAsync error: " +
                        $"{(int)response.StatusCode} {response.ReasonPhrase} - {responseBody}");
                    return new LightRAGIndexResult
                    {
                        Accepted = false,
                        ErrorMessage = $"HTTP {(int)response.StatusCode}: {responseBody}"
                    };
                }

                // 解析 track_id（多字段兼容）
                string trackId = null;
                try
                {
                    var uploadResp = JsonConvert.DeserializeObject<RAGUploadResponse>(responseBody);
                    trackId = uploadResp?.GetTrackId();
                }
                catch
                {
                    // 解析失败时 trackId 保持 null，降级为无进度追踪模式
                }

                return new LightRAGIndexResult
                {
                    Accepted = true,
                    TrackId = trackId
                };
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"[AgentCore] LightRAGClient.IndexFileAsync failed: {ex.Message}");
                return new LightRAGIndexResult
                {
                    Accepted = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        // ─────────────────────────────────────────
        //  公共 API — 文档管理
        // ─────────────────────────────────────────

        /// <summary>
        /// 获取知识库中所有文档列表（processed + pending + failed 合并）。
        /// </summary>
        /// <param name="ct">取消令牌</param>
        /// <returns>所有文档列表；失败时返回空列表</returns>
        public async Task<List<LightRAGDocument>> GetDocumentsAsync(CancellationToken ct = default)
        {
            try
            {
                var url = $"{_baseUrl}/documents";
                var response = await GetAsync<RAGDocumentsResponse>(url, ct);

                var all = new List<LightRAGDocument>();
                if (response?.Statuses != null)
                {
                    if (response.Statuses.Processed != null) all.AddRange(response.Statuses.Processed);
                    if (response.Statuses.Pending != null)   all.AddRange(response.Statuses.Pending);
                    if (response.Statuses.Failed != null)    all.AddRange(response.Statuses.Failed);
                }
                return all;
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"[AgentCore] LightRAGClient.GetDocumentsAsync failed: {ex.Message}");
                return new List<LightRAGDocument>();
            }
        }

        /// <summary>
        /// 删除知识库中的指定文档。
        /// </summary>
        /// <param name="docId">文档 ID（来自 LightRAGDocument.Id）</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>是否删除成功</returns>
        public async Task<bool> DeleteDocumentAsync(string docId, CancellationToken ct = default)
        {
            try
            {
                // LightRAG v1.4.x DELETE /documents/delete_document
                // 参数通过 JSON body 传递：{"id": "xxx"}（query string 会返回 422）
                var url = $"{_baseUrl}/documents/delete_document";
                var client = HttpClientFactory.GetClient();
                using var request = HttpClientFactory.CreateRequest(HttpMethod.Delete, url, _apiKey);
                var payload = new JObject { ["doc_ids"] = new JArray(docId) };
                request.Content = new StringContent(
                    payload.ToString(Newtonsoft.Json.Formatting.None),
                    System.Text.Encoding.UTF8,
                    "application/json");

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

                var response = await client.SendAsync(request, cts.Token);
                var responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    AgentCoreLog.Error(
                        $"[AgentCore] LightRAGClient.DeleteDocumentAsync error: " +
                        $"{(int)response.StatusCode} {response.ReasonPhrase} - {responseBody}");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"[AgentCore] LightRAGClient.DeleteDocumentAsync failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 轮询文档索引进度。
        /// 上传成功后，LightRAG 异步处理文档，需要通过此方法追踪真实进度。
        /// </summary>
        /// <param name="trackId">上传时返回的 track_id</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>当前索引状态；失败时返回 status = "failed"</returns>
        public async Task<LightRAGTrackStatus> TrackStatusAsync(
            string trackId,
            CancellationToken ct = default)
        {
            try
            {
                var url = $"{_baseUrl}/documents/track_status/{Uri.EscapeDataString(trackId)}";
                return await GetAsync<LightRAGTrackStatus>(url, ct);
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"[AgentCore] LightRAGClient.TrackStatusAsync failed: {ex.Message}");
                return new LightRAGTrackStatus
                {
                    Status = "failed",
                    ErrorMsg = ex.Message
                };
            }
        }

        // ─────────────────────────────────────────
        //  公共 API — 连接与健康
        // ─────────────────────────────────────────

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
                AgentCoreLog.Warning($"[AgentCore] LightRAGClient.TestConnectionAsync failed: {ex.Message}");
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
                AgentCoreLog.Warning($"[AgentCore] LightRAGClient.GetHealthAsync failed: {ex.Message}");
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
