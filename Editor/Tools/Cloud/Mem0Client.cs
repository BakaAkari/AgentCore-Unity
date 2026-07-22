using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
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
    //  连接状态枚举
    // ─────────────────────────────────────────────

    /// <summary>
    /// mem0 服务连接状态。
    /// 用于区分不同类型的连接结果，便于 UI 显示不同颜色和图标。
    /// </summary>
    public enum Mem0ConnectionStatus
    {
        /// <summary>连接成功</summary>
        Connected,
        /// <summary>服务不可达（网络不通、连接被拒绝）</summary>
        Unreachable,
        /// <summary>连接超时</summary>
        Timeout,
        /// <summary>服务返回错误</summary>
        Error,
        /// <summary>用户不存在（业务层面）</summary>
        UserNotFound
    }

    // ─────────────────────────────────────────────
    //  数据模型
    // ─────────────────────────────────────────────

    /// <summary>
    /// OpenMemory 记忆条目。
    /// 适配 OpenMemory MCP 的 MemoryResponse 格式。
    /// </summary>
    [Serializable]
    public class Mem0Memory
    {
        [JsonProperty("id")]
        public string Id;

        /// <summary>
        /// 记忆内容。OpenMemory 返回字段名为 "content"。
        /// </summary>
        [JsonProperty("content")]
        public string Content;

        [JsonProperty("user_id")]
        public string UserId;

        [JsonProperty("created_at")]
        public string CreatedAt;

        [JsonProperty("updated_at")]
        public string UpdatedAt;

        [JsonProperty("state")]
        public string State;

        [JsonProperty("app_id")]
        public string AppId;

        [JsonProperty("app_name")]
        public string AppName;

        [JsonProperty("categories")]
        public List<string> Categories;

        [JsonProperty("metadata_")]
        public Dictionary<string, object> Metadata;

        /// <summary>搜索时的相关性分数（仅 search/filter 返回）</summary>
        [JsonProperty("score")]
        public float? Score;
    }

    /// <summary>
    /// 添加记忆的返回结果。
    /// </summary>
    [Serializable]
    public class Mem0AddResult
    {
        public string Id;
        public bool Success;
        public string Message;
    }

    // ─────────────────────────────────────────────
    //  内部 API 响应模型（适配 OpenMemory）
    // ─────────────────────────────────────────────

    /// <summary>
    /// OpenMemory 创建记忆的响应。
    /// POST /api/v1/memories/ 返回的结果。
    /// </summary>
    [Serializable]
    internal class Mem0AddResponse
    {
        /// <summary>mem0 标准格式的 results 字段</summary>
        [JsonProperty("results")]
        public List<Mem0Memory> Results;

        /// <summary>OpenMemory 可能直接返回的 message 字段</summary>
        [JsonProperty("message")]
        public string Message;
    }

    /// <summary>
    /// OpenMemory 分页响应格式。
    /// 适配 Page[MemoryResponse] 结构：{items, total, page, size, pages}
    /// </summary>
    [Serializable]
    internal class OpenMemoryPageResponse
    {
        [JsonProperty("items")]
        public List<Mem0Memory> Items;

        [JsonProperty("total")]
        public int? Total;

        [JsonProperty("page")]
        public int? Page;

        [JsonProperty("size")]
        public int? Size;

        [JsonProperty("pages")]
        public int? Pages;
    }

    // ─────────────────────────────────────────────
    //  客户端
    // ─────────────────────────────────────────────

    /// <summary>
    /// OpenMemory MCP REST API 客户端。
    /// 封装 OpenMemory REST API 调用，提供记忆的增删查操作。
    /// API 基础路径：/api/v1/memories/
    /// </summary>
    public class Mem0Client
    {
        private readonly string _baseUrl;
        private readonly string _apiKey;
        private readonly string _userId;

        /// <summary>常规请求超时时间（秒）</summary>
        private const int TimeoutSeconds = 30;

        /// <summary>连接测试超时时间（秒）— 比常规请求短</summary>
        private const int ConnectionTestTimeoutSeconds = 10;

        /// <summary>OpenMemory API 路径前缀</summary>
        private const string ApiPrefix = "/api/v1";

        /// <summary>
        /// 创建 Mem0Client 实例。
        /// </summary>
        /// <param name="baseUrl">OpenMemory 服务端点 URL（不能为空）</param>
        /// <param name="apiKey">API Key（可为空）</param>
        /// <param name="userId">用户 ID</param>
        public Mem0Client(string baseUrl, string apiKey, string userId)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new ArgumentException("baseUrl cannot be null or empty", nameof(baseUrl));

            _baseUrl = baseUrl.TrimEnd('/');
            _apiKey = apiKey;

            // 防御性检查：始终使用系统生成的 ID（基于 MachineName + UserName + ProductName 哈希，实现项目级记忆隔离），
            // 忽略任何非 "unity-" 前缀的旧值
            var systemId = AgentCoreSettings.GenerateSystemUserId();
            if (!string.IsNullOrEmpty(userId) && userId.StartsWith("unity-"))
            {
                _userId = userId;
            }
            else
            {
                if (!string.IsNullOrEmpty(userId))
                {
                    AgentCoreLog.Warning($"[AgentCore] Mem0Client: 忽略非系统格式的 userId '{userId}'，使用系统生成 ID '{systemId}'");
                }
                _userId = systemId;
            }
        }

        /// <summary>
        /// 从 AgentCoreSettings 创建客户端实例。
        /// </summary>
        /// <exception cref="InvalidOperationException">当 mem0 Endpoint 未配置时抛出</exception>
        public static Mem0Client FromSettings()
        {
            var settings = AgentCoreSettings.instance;

            if (string.IsNullOrWhiteSpace(settings.mem0Endpoint))
            {
                throw new InvalidOperationException(
                    "mem0 Endpoint URL 未配置，请在 Project Settings > AgentCore 中设置");
            }

            return new Mem0Client(
                settings.mem0Endpoint,
                SecureKeyStorage.GetMem0ApiKey(),
                settings.EffectiveUserId
            );
        }

        // ─────────────────────────────────────────
        //  公共 API
        // ─────────────────────────────────────────

        /// <summary>
        /// 添加记忆到 OpenMemory。
        /// OpenMemory CreateMemoryRequest: {user_id, text, metadata, infer, app}
        /// </summary>
        /// <param name="content">记忆内容</param>
        /// <param name="userId">用户 ID（为空则使用默认）</param>
        /// <param name="metadata">可选的元数据</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>添加结果</returns>
        public async Task<Mem0AddResult> AddMemoryAsync(
            string content,
            string userId = null,
            Dictionary<string, string> metadata = null,
            CancellationToken ct = default)
        {
            return await AddMemoryAsync(content, userId, metadata, allowAutoRegister: true, ct);
        }

        /// <summary>
        /// 添加记忆的内部实现，带自动注册控制。
        /// 最小可用性修复：OpenMemory 对未注册的 user_id 返回 404 "User not found"。
        /// 当 add 因用户未注册失败时，自动调用 CreateUserAsync 隐式注册该用户后重试一次，
        /// 让 add 开箱即用，无需用户手动去服务端注册。allowAutoRegister 防止无限递归。
        /// </summary>
        private async Task<Mem0AddResult> AddMemoryAsync(
            string content,
            string userId,
            Dictionary<string, string> metadata,
            bool allowAutoRegister,
            CancellationToken ct = default)
        {
            try
            {
                var uid = userId ?? _userId;
                // OpenMemory 使用 {text, user_id, metadata, app} 格式
                var payload = new JObject
                {
                    ["text"] = content,
                    ["user_id"] = uid,
                    ["app"] = "agentcore"
                };

                if (metadata != null && metadata.Count > 0)
                {
                    var metaObj = new JObject();
                    foreach (var kvp in metadata)
                        metaObj[kvp.Key] = kvp.Value;
                    payload["metadata"] = metaObj;
                }

                AgentCore.Editor.Utils.AgentCoreLog.Debug($"[AgentCore] AddMemoryAsync: user_id={uid}, content_length={content?.Length ?? 0}, app=agentcore");
                var responseBody = await PostRawAsync($"{ApiPrefix}/memories/", payload, ct);

                // 检测 null 或空响应 — OpenMemory REST API 已知问题：
                // POST /api/v1/memories/ 可能返回 HTTP 200 + body="null"，
                // 此时记忆实际上未被持久化。
                if (string.IsNullOrWhiteSpace(responseBody) || responseBody.Trim() == "null")
                {
                    AgentCoreLog.Warning($"[AgentCore] AddMemoryAsync: 服务返回空响应 (body={responseBody})。" +
                        "记忆可能未被保存。这是 OpenMemory REST API 的已知问题，" +
                        "请确认 user_id '{uid}' 已通过 MCP SSE 注册。");
                    return new Mem0AddResult
                    {
                        Id = null,
                        Success = false,
                        Message = $"服务返回空响应，记忆可能未保存。请确认用户 '{uid}' 已在 OpenMemory 中注册。"
                    };
                }

                // 尝试解析响应 - OpenMemory 可能返回多种格式
                try
                {
                    var response = JsonConvert.DeserializeObject<Mem0AddResponse>(responseBody);
                    if (response?.Results != null && response.Results.Count > 0)
                    {
                        AgentCore.Editor.Utils.AgentCoreLog.Debug($"[AgentCore] AddMemoryAsync: success, id={response.Results[0].Id}");
                        return new Mem0AddResult
                        {
                            Id = response.Results[0].Id,
                            Success = true,
                            Message = "Memory added successfully"
                        };
                    }

                    AgentCoreLog.Warning($"[AgentCore] AddMemoryAsync: HTTP 200 但无 results，response={responseBody}");
                    return new Mem0AddResult
                    {
                        Id = null,
                        Success = true,
                        Message = response?.Message ?? "Memory processed (no results returned)"
                    };
                }
                catch (Exception parseEx)
                {
                    // 如果响应格式不匹配，记录详细信息以便调试
                    AgentCoreLog.Warning($"[AgentCore] AddMemoryAsync: 响应解析失败 ({parseEx.Message})，原始响应={responseBody}");
                    return new Mem0AddResult
                    {
                        Id = null,
                        Success = true,
                        Message = "Memory processed successfully (response format unexpected)"
                    };
                }
            }
            catch (Exception ex)
            {
                // 最小可用性修复：检测 "用户未注册" 错误（OpenMemory 返回 404 + "User not found"）。
                // 首次遇到时自动注册用户再重试一次 add，让记忆功能开箱即用。
                if (allowAutoRegister && IsUserNotFoundError(ex))
                {
                    AgentCoreLog.Info($"[AgentCore] AddMemoryAsync: user '{userId ?? _userId}' 未注册，尝试自动注册后重试...");
                    try
                    {
                        var (created, regMsg) = await CreateUserAsync(ct);
                        if (created)
                        {
                            AgentCoreLog.Info($"[AgentCore] AddMemoryAsync: 用户自动注册成功（{regMsg}），重试 add");
                            // 重试一次，禁止再次自动注册以防无限递归
                            return await AddMemoryAsync(content, userId, metadata, allowAutoRegister: false, ct);
                        }
                        AgentCoreLog.Warning($"[AgentCore] AddMemoryAsync: 自动注册用户失败（{regMsg}）");
                        return new Mem0AddResult
                        {
                            Id = null,
                            Success = false,
                            Message = $"用户未注册且自动注册失败: {regMsg}"
                        };
                    }
                    catch (Exception regEx)
                    {
                        AgentCoreLog.Warning($"[AgentCore] AddMemoryAsync: 自动注册过程异常: {regEx.Message}");
                        // 落到下方通用失败返回
                    }
                }

                AgentCoreLog.Error($"[AgentCore] Mem0Client.AddMemoryAsync failed: {ex.Message}\n{ex.StackTrace}");
                return new Mem0AddResult
                {
                    Id = null,
                    Success = false,
                    Message = ex.Message
                };
            }
        }

        /// <summary>
        /// 判断异常是否为 OpenMemory "用户未注册" 错误（HTTP 404 + "User not found"）。
        /// </summary>
        private static bool IsUserNotFoundError(Exception ex)
        {
            var msg = ex.Message?.ToLowerInvariant() ?? string.Empty;
            return msg.Contains("404") &&
                   (msg.Contains("user not found") || msg.Contains("not found"));
        }

        /// <summary>
        /// 搜索/过滤记忆。
        /// OpenMemory FilterMemoriesRequest: {user_id, search_query, page, size, app_ids, ...}
        /// 端点：POST /api/v1/memories/filter
        /// </summary>
        /// <param name="query">搜索查询文本</param>
        /// <param name="userId">用户 ID（为空则使用默认）</param>
        /// <param name="limit">返回结果数量上限</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>匹配的记忆列表</returns>
        public async Task<List<Mem0Memory>> SearchMemoryAsync(
            string query,
            string userId = null,
            int limit = 10,
            CancellationToken ct = default)
        {
            try
            {
                // OpenMemory filter 使用 {user_id, search_query, size} 格式
                var payload = new JObject
                {
                    ["user_id"] = userId ?? _userId,
                    ["search_query"] = query,
                    ["size"] = limit
                };

                var response = await PostAsync<OpenMemoryPageResponse>(
                    $"{ApiPrefix}/memories/filter", payload, ct);
                return response?.Items ?? new List<Mem0Memory>();
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"[AgentCore] Mem0Client.SearchMemoryAsync failed: {ex.Message}");
                return new List<Mem0Memory>();
            }
        }

        /// <summary>
        /// 列出用户的所有记忆。
        /// 使用 POST /api/v1/memories/filter 端点（替代有 Bug 的 GET /api/v1/memories/）。
        /// 返回分页格式：{items, total, page, size, pages}
        /// </summary>
        /// <param name="userId">用户 ID（为空则使用默认）</param>
        /// <param name="limit">返回结果数量上限</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>记忆列表</returns>
        public async Task<List<Mem0Memory>> ListMemoriesAsync(
            string userId = null,
            int limit = 50,
            CancellationToken ct = default)
        {
            try
            {
                var uid = userId ?? _userId;
                // 使用 POST /api/v1/memories/filter 替代有 Bug 的 GET /api/v1/memories/
                // GET 端点对已存在用户返回 500 Internal Server Error（OpenMemory 服务端 Bug）
                var payload = new JObject
                {
                    ["user_id"] = uid,
                    ["size"] = limit
                };

                AgentCore.Editor.Utils.AgentCoreLog.Debug($"[AgentCore] ListMemoriesAsync: POST {ApiPrefix}/memories/filter body={payload.ToString(Formatting.None)}");
                var response = await PostAsync<OpenMemoryPageResponse>(
                    $"{ApiPrefix}/memories/filter", payload, ct);
                var items = response?.Items ?? new List<Mem0Memory>();
                AgentCore.Editor.Utils.AgentCoreLog.Debug($"[AgentCore] ListMemoriesAsync: returned {items.Count} items (total={response?.Total ?? 0})");
                return items;
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"[AgentCore] Mem0Client.ListMemoriesAsync failed: {ex.Message}");
                return new List<Mem0Memory>();
            }
        }

        /// <summary>
        /// 删除指定记忆。
        /// OpenMemory DeleteMemoriesRequest: {memory_ids: [uuid], user_id}
        /// 端点：DELETE /api/v1/memories/（带请求体）
        /// </summary>
        /// <param name="memoryId">记忆 ID</param>
        /// <param name="userId">用户 ID（为空则使用默认）</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>是否删除成功</returns>
        public async Task<bool> DeleteMemoryAsync(
            string memoryId,
            string userId = null,
            CancellationToken ct = default)
        {
            try
            {
                // OpenMemory 使用 DELETE /api/v1/memories/ + body {memory_ids, user_id}
                var url = $"{_baseUrl}{ApiPrefix}/memories/";
                var payload = new JObject
                {
                    ["memory_ids"] = new JArray { memoryId },
                    ["user_id"] = userId ?? _userId
                };
                await DeleteWithBodyAsync(url, payload, ct);
                return true;
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"[AgentCore] Mem0Client.DeleteMemoryAsync failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 测试与 mem0 服务的连接。
        /// 尝试多个端点以兼容 OpenMemory 和标准 mem0：
        /// 1. GET /api/v1/config/ — OpenMemory
        /// 2. GET /v1/health/ — 标准 mem0
        /// 使用较短的超时时间（10 秒）。
        /// </summary>
        /// <param name="ct">取消令牌</param>
        /// <returns>(success, message) 元组，包含连接结果和详细信息</returns>
        public async Task<(bool success, string message)> TestConnectionAsync(CancellationToken ct = default)
        {
            // 尝试多个端点以兼容不同的 mem0 部署
            var endpoints = new[]
            {
                (path: $"{_baseUrl}{ApiPrefix}/config/", serverType: "OpenMemory"),
                (path: $"{_baseUrl}/v1/health/", serverType: "mem0 Standard"),
            };

            string lastError = null;

            foreach (var (path, serverType) in endpoints)
            {
                try
                {
                    var client = HttpClientFactory.GetClient();
                    using var request = HttpClientFactory.CreateRequest(HttpMethod.Get, path, _apiKey);

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(ConnectionTestTimeoutSeconds));

                    var response = await client.SendAsync(request, cts.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        return (true, $"连接成功（{serverType}）");
                    }

                    lastError = $"HTTP {(int)response.StatusCode}";
                }
                catch (HttpRequestException ex) when (IsConnectionRefused(ex))
                {
                    lastError = $"无法连接到 mem0 服务: {_baseUrl}，请检查 Endpoint URL 是否正确";
                }
                catch (TaskCanceledException)
                {
                    lastError = $"连接超时（{ConnectionTestTimeoutSeconds}s），请检查服务是否运行";
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }
            }

            AgentCoreLog.Warning($"[AgentCore] Mem0Client.TestConnectionAsync failed: {lastError}");
            return (false, lastError ?? "无法连接到 mem0 服务");
        }

        /// <summary>
        /// 检测 User ID 是否在 OpenMemory 中已注册。
        /// 使用 POST /api/v1/memories/filter 端点判断：
        /// - HTTP 200 → 用户存在（返回分页数据）
        /// - HTTP 404 → 用户不存在
        /// 注意：GET /api/v1/memories/ 端点存在 OpenMemory 服务端 Bug（对已存在用户返回 500），
        /// 因此改用 POST filter 端点，该端点经验证可靠。
        /// </summary>
        /// <param name="ct">取消令牌</param>
        /// <returns>(exists, message, status) 元组</returns>
        public async Task<(bool exists, string message, Mem0ConnectionStatus status)> CheckUserExistsAsync(
            CancellationToken ct = default)
        {
            try
            {
                var url = $"{_baseUrl}{ApiPrefix}/memories/filter";
                var client = HttpClientFactory.GetClient();
                using var request = HttpClientFactory.CreateRequest(HttpMethod.Post, url, _apiKey);

                // 使用 FilterMemoriesRequest 格式：{user_id, size}
                var payload = new JObject
                {
                    ["user_id"] = _userId,
                    ["size"] = 1
                };
                var json = payload.ToString(Formatting.None);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(ConnectionTestTimeoutSeconds));

                AgentCore.Editor.Utils.AgentCoreLog.Debug($"[AgentCore] CheckUserExistsAsync: POST {url} body={json}");
                var response = await client.SendAsync(request, cts.Token);
                var responseBody = await response.Content.ReadAsStringAsync();
                AgentCore.Editor.Utils.AgentCoreLog.Debug($"[AgentCore] CheckUserExistsAsync: HTTP {(int)response.StatusCode} body={responseBody}");

                if (response.IsSuccessStatusCode)
                {
                    // 尝试解析获取记忆数量信息
                    try
                    {
                        var pageResp = JsonConvert.DeserializeObject<OpenMemoryPageResponse>(responseBody);
                        var total = pageResp?.Total ?? 0;
                        return (true, $"用户存在（共 {total} 条记忆）", Mem0ConnectionStatus.Connected);
                    }
                    catch
                    {
                        return (true, "用户存在", Mem0ConnectionStatus.Connected);
                    }
                }

                // HTTP 404 — 用户不存在
                if ((int)response.StatusCode == 404)
                {
                    return (false, "用户不存在", Mem0ConnectionStatus.UserNotFound);
                }

                // 其他 HTTP 错误
                return (false, $"服务返回错误: HTTP {(int)response.StatusCode} - {responseBody}",
                    Mem0ConnectionStatus.Error);
            }
            catch (HttpRequestException ex) when (IsConnectionRefused(ex))
            {
                return (false,
                    $"无法连接到 mem0 服务: {_baseUrl}，请检查 Endpoint URL 是否正确",
                    Mem0ConnectionStatus.Unreachable);
            }
            catch (TaskCanceledException)
            {
                return (false,
                    $"连接超时（{ConnectionTestTimeoutSeconds}s），请检查服务是否运行",
                    Mem0ConnectionStatus.Timeout);
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] Mem0Client.CheckUserExistsAsync failed: {ex.Message}");
                return (false, $"查询失败: {ex.Message}", Mem0ConnectionStatus.Error);
            }
        }

        /// <summary>
        /// 创建/注册用户。
        /// 优先通过 REST API 直接添加一条初始记忆来隐式创建用户（简单可靠）。
        /// 如果 REST 方式失败，回退到 MCP SSE 方式。
        /// </summary>
        /// <param name="ct">取消令牌</param>
        /// <returns>(success, message) 元组</returns>
        public async Task<(bool success, string message)> CreateUserAsync(CancellationToken ct = default)
        {
            // 方式 1: 通过 REST API 添加初始记忆来隐式创建用户
            try
            {
                // allowAutoRegister:false — 这是注册流程本身，禁止再触发自动注册以防无限递归
                var result = await AddMemoryAsync(
                    $"User {_userId} registered via AgentCore Unity plugin.",
                    _userId,
                    metadata: new Dictionary<string, string>
                    {
                        ["source"] = "user_registration",
                        ["created_by"] = "agentcore"
                    },
                    allowAutoRegister: false,
                    ct: ct);

                if (result.Success)
                {
                    // 验证用户是否创建成功
                    var (exists, msg, _) = await CheckUserExistsAsync(ct);
                    if (exists)
                    {
                        return (true, $"用户创建成功！{msg}");
                    }

                    // REST 添加成功但验证失败 — 可能需要等待
                    return (true, "初始记忆已添加，用户可能需要稍后验证");
                }
            }
            catch (Exception ex)
            {
                AgentCore.Editor.Utils.AgentCoreLog.Debug($"[AgentCore] REST user creation failed, trying MCP SSE fallback: {ex.Message}");
            }

            // 方式 2: 回退到 MCP SSE 方式
            return await CreateUserViaMcpAsync("agentcore", ct);
        }

        /// <summary>
        /// 通过 MCP SSE 连接在 OpenMemory 中创建/注册用户（回退方式）。
        /// 流程：
        /// 1. 建立 SSE 连接 GET /mcp/{clientName}/sse/{userId}，获取 session_id
        ///    （SSE 连接必须保持活跃，否则 session 会失效）
        /// 2. 发送 initialize JSON-RPC 请求
        /// 3. 发送 notifications/initialized 通知
        /// 4. 调用 add_memories 工具写入一条初始记忆（触发用户注册）
        /// 5. 等待 SSE 响应确认处理完成
        /// 6. 断开 SSE 连接
        /// 7. 验证用户是否创建成功
        /// </summary>
        /// <param name="clientName">MCP 客户端名称</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>(success, message) 元组</returns>
        private async Task<(bool success, string message)> CreateUserViaMcpAsync(
            string clientName = "agentcore",
            CancellationToken ct = default)
        {
            HttpResponseMessage sseResponse = null;
            Stream sseStream = null;
            StreamReader sseReader = null;

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(45));

                var sseUrl = $"{_baseUrl}/mcp/{Uri.EscapeDataString(clientName)}/sse/{Uri.EscapeDataString(_userId)}";

                // Step 1: 建立 SSE 连接（必须保持活跃直到所有消息处理完成）
                var client = HttpClientFactory.GetClient();
                var sseRequest = new HttpRequestMessage(HttpMethod.Get, sseUrl);
                sseRequest.Headers.Accept.Add(
                    new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

                sseResponse = await client.SendAsync(sseRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                sseResponse.EnsureSuccessStatusCode();

                sseStream = await sseResponse.Content.ReadAsStreamAsync();
                sseReader = new StreamReader(sseStream);

                // 从 SSE 事件中提取 session_id
                var sessionId = await ExtractSessionIdFromSseAsync(sseReader, cts.Token);
                if (string.IsNullOrEmpty(sessionId))
                {
                    return (false, "无法建立 MCP SSE 连接（未获取到 session_id）");
                }

                AgentCore.Editor.Utils.AgentCoreLog.Debug($"[AgentCore] MCP SSE connected, session_id: {sessionId}");

                var messagesUrl = $"{_baseUrl}/mcp/messages/?session_id={sessionId}";

                // Step 2: 发送 initialize 请求
                var initPayload = new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = 1,
                    ["method"] = "initialize",
                    ["params"] = new JObject
                    {
                        ["protocolVersion"] = "2024-11-05",
                        ["capabilities"] = new JObject(),
                        ["clientInfo"] = new JObject
                        {
                            ["name"] = clientName,
                            ["version"] = "1.0.0"
                        }
                    }
                };
                await PostMcpMessageAsync(messagesUrl, initPayload, cts.Token);

                // 等待 SSE 返回 initialize 响应
                await WaitForSseResponseAsync(sseReader, expectedId: 1, cts.Token);

                // Step 3: 发送 initialized 通知
                var notifPayload = new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = "notifications/initialized"
                };
                await PostMcpMessageAsync(messagesUrl, notifPayload, cts.Token);
                await Task.Delay(500, cts.Token);

                // Step 4: 调用 add_memories 工具写入初始记忆
                // 注意：OpenMemory MCP 的 add_memories 工具参数名为 "text"（非 "content"）
                var addMemPayload = new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = 2,
                    ["method"] = "tools/call",
                    ["params"] = new JObject
                    {
                        ["name"] = "add_memories",
                        ["arguments"] = new JObject
                        {
                            ["text"] = $"User {_userId} registered via AgentCore Unity plugin."
                        }
                    }
                };
                await PostMcpMessageAsync(messagesUrl, addMemPayload, cts.Token);

                // 等待 SSE 返回 add_memories 响应（可能需要较长时间处理）
                var addResult = await WaitForSseResponseAsync(sseReader, expectedId: 2, cts.Token,
                    timeoutSeconds: 15);

                // 检查 add_memories 是否返回错误
                if (addResult != null)
                {
                    try
                    {
                        var resultObj = JObject.Parse(addResult);
                        var isError = resultObj.SelectToken("result.isError")?.Value<bool>() ?? false;
                        if (isError)
                        {
                            var errorText = resultObj.SelectToken("result.content[0].text")?.Value<string>()
                                            ?? "未知错误";
                            AgentCoreLog.Warning($"[AgentCore] MCP add_memories returned error: {errorText}");
                            return (false, $"MCP add_memories 失败: {errorText}");
                        }
                    }
                    catch (Exception parseEx)
                    {
                        AgentCoreLog.Warning($"[AgentCore] Failed to parse add_memories response: {parseEx.Message}");
                    }
                }

                // 额外等待服务器处理
                await Task.Delay(2000, cts.Token);

                // Step 5: 验证用户是否创建成功
                var (exists, msg, _) = await CheckUserExistsAsync(cts.Token);
                if (exists)
                {
                    return (true, $"用户创建成功！{msg}");
                }

                return (false, $"MCP 流程已完成但用户验证失败: {msg}。请尝试通过 MCP 客户端（如 Claude Desktop）连接一次以完成注册。");
            }
            catch (OperationCanceledException)
            {
                return (false, "操作超时");
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"[AgentCore] Mem0Client.CreateUserViaMcpAsync failed: {ex.Message}");
                return (false, $"创建失败: {ex.Message}");
            }
            finally
            {
                // 安全关闭 SSE 连接
                try { sseReader?.Dispose(); } catch { /* ignore */ }
                try { sseStream?.Dispose(); } catch { /* ignore */ }
                try { sseResponse?.Dispose(); } catch { /* ignore */ }
            }
        }

        // ─────────────────────────────────────────
        //  MCP SSE 辅助方法
        // ─────────────────────────────────────────

        /// <summary>
        /// 从 SSE 流中提取 session_id（不关闭连接）。
        /// </summary>
        private async Task<string> ExtractSessionIdFromSseAsync(StreamReader reader, CancellationToken ct)
        {
            // 读取 SSE 事件直到获取 session_id（最多读 10 行）
            for (int i = 0; i < 10; i++)
            {
                var line = await ReadLineWithTimeoutAsync(reader, ct, TimeSpan.FromSeconds(5));
                if (line == null) break;

                // SSE data 行格式: "data: /mcp/messages/?session_id=xxx"
                if (line.StartsWith("data:"))
                {
                    var data = line.Substring(5).Trim();
                    var sidIdx = data.IndexOf("session_id=", StringComparison.Ordinal);
                    if (sidIdx >= 0)
                    {
                        return data.Substring(sidIdx + "session_id=".Length).Trim();
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// 等待 SSE 流中返回指定 id 的 JSON-RPC 响应。
        /// </summary>
        /// <param name="reader">SSE 流读取器</param>
        /// <param name="expectedId">期望的 JSON-RPC 响应 id</param>
        /// <param name="ct">取消令牌</param>
        /// <param name="timeoutSeconds">单次等待超时秒数</param>
        /// <returns>匹配的 JSON-RPC 响应字符串，超时返回 null</returns>
        private async Task<string> WaitForSseResponseAsync(
            StreamReader reader, int expectedId, CancellationToken ct, int timeoutSeconds = 10)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

            while (DateTime.UtcNow < deadline)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;

                var line = await ReadLineWithTimeoutAsync(reader, ct,
                    remaining < TimeSpan.FromSeconds(3) ? remaining : TimeSpan.FromSeconds(3));

                if (line == null) continue;

                // SSE data 行包含 JSON-RPC 响应
                if (line.StartsWith("data:"))
                {
                    var data = line.Substring(5).Trim();
                    if (string.IsNullOrEmpty(data)) continue;

                    try
                    {
                        var jsonResp = JObject.Parse(data);
                        var respId = jsonResp["id"]?.Value<int>();
                        if (respId == expectedId)
                        {
                            AgentCore.Editor.Utils.AgentCoreLog.Debug($"[AgentCore] MCP SSE received response for id={expectedId}");
                            return data;
                        }
                    }
                    catch
                    {
                        // 非 JSON 数据，跳过
                    }
                }
            }

            AgentCoreLog.Warning($"[AgentCore] MCP SSE timeout waiting for response id={expectedId}");
            return null;
        }

        /// <summary>
        /// 带超时的 StreamReader.ReadLineAsync。
        /// </summary>
        private async Task<string> ReadLineWithTimeoutAsync(StreamReader reader, CancellationToken ct, TimeSpan timeout)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            try
            {
                var readTask = reader.ReadLineAsync();
                var delayTask = Task.Delay(timeout, cts.Token);
                var completed = await Task.WhenAny(readTask, delayTask);

                if (completed == readTask)
                {
                    return await readTask;
                }

                return null; // 超时
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        /// <summary>
        /// 向 MCP messages 端点发送 JSON-RPC 消息。
        /// </summary>
        private async Task<string> PostMcpMessageAsync(string messagesUrl, JObject payload, CancellationToken ct)
        {
            var client = HttpClientFactory.GetClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, messagesUrl);
            request.Content = new StringContent(
                payload.ToString(Formatting.None),
                Encoding.UTF8,
                "application/json");

            var response = await client.SendAsync(request, ct);
            return await response.Content.ReadAsStringAsync();
        }

        // ─────────────────────────────────────────
        //  内部 HTTP 辅助方法
        // ─────────────────────────────────────────

        /// <summary>
        /// 判断 HttpRequestException 是否为连接被拒绝（网络不通）。
        /// </summary>
        private static bool IsConnectionRefused(HttpRequestException ex)
        {
            // 检查内部异常是否为 SocketException（连接被拒绝）
            if (ex.InnerException is System.Net.Sockets.SocketException)
                return true;

            // 某些平台上可能没有 SocketException，检查消息
            var msg = ex.Message.ToLowerInvariant();
            return msg.Contains("connection refused") ||
                   msg.Contains("no connection could be made") ||
                   msg.Contains("actively refused");
        }

        /// <summary>
        /// POST 请求并反序列化响应。
        /// </summary>
        private async Task<T> PostAsync<T>(string path, JObject payload, CancellationToken ct)
        {
            var responseBody = await PostRawAsync(path, payload, ct);
            return JsonConvert.DeserializeObject<T>(responseBody);
        }

        /// <summary>
        /// POST 请求并返回原始响应字符串。
        /// 包含详细的请求/响应日志以便调试 OpenMemory API 问题。
        /// </summary>
        private async Task<string> PostRawAsync(string path, JObject payload, CancellationToken ct)
        {
            var url = $"{_baseUrl}{path}";
            var client = HttpClientFactory.GetClient();
            using var request = HttpClientFactory.CreateRequest(HttpMethod.Post, url, _apiKey);

            var json = payload.ToString(Formatting.None);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            AgentCore.Editor.Utils.AgentCoreLog.Debug($"[AgentCore] POST {url} body={TruncateForLog(json, 500)}");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

            var response = await client.SendAsync(request, cts.Token);
            var responseBody = await response.Content.ReadAsStringAsync();

            AgentCore.Editor.Utils.AgentCoreLog.Debug($"[AgentCore] POST {path} → HTTP {(int)response.StatusCode} body={TruncateForLog(responseBody, 500)}");

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"OpenMemory API error: {(int)response.StatusCode} {response.ReasonPhrase} - {responseBody}");
            }

            return responseBody;
        }

        /// <summary>
        /// 截断字符串用于日志输出，避免过长的日志。
        /// </summary>
        private static string TruncateForLog(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text)) return "(empty)";
            return text.Length <= maxLength ? text : text.Substring(0, maxLength) + "...(truncated)";
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
                    $"OpenMemory API error: {(int)response.StatusCode} {response.ReasonPhrase} - {responseBody}");
            }

            return JsonConvert.DeserializeObject<T>(responseBody);
        }

        /// <summary>
        /// DELETE 请求带请求体（OpenMemory 的删除 API 需要 body）。
        /// </summary>
        private async Task DeleteWithBodyAsync(string url, JObject payload, CancellationToken ct)
        {
            var client = HttpClientFactory.GetClient();
            using var request = HttpClientFactory.CreateRequest(HttpMethod.Delete, url, _apiKey);

            var json = payload.ToString(Formatting.None);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

            var response = await client.SendAsync(request, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException(
                    $"OpenMemory API error: {(int)response.StatusCode} {response.ReasonPhrase} - {responseBody}");
            }
        }
    }
}
