# AgentCore RAG 功能补齐与强化设计

> 状态：已完成（Phase 5.2）。本文为 RAG 补齐阶段历史设计参考；后续 RAG 新需求需新建计划或在 `ROADMAP.md` 中重新立项。
> 目标版本：0.4.x 起逐步落地。
> 相关代码：`LightRAGClient`、`LightRAGTool`、`KnowledgeBasePanel`、`AgentCoreSettings`。
>
> **修订说明**：
> - v1（草案）：API 端点未验证，功能范围较宽泛
> - v2（本版）：基于实际 API 测试，聚焦 P0+P1 范围，新增文档管理功能

---

## 1. 当前状态（已完成）

### 1.1 已实现能力

| 模块 | 已有功能 |
|------|---------|
| `LightRAGClient` | `QueryAsync`、`IndexTextAsync`、`IndexFileAsync`（已修复 dispose bug）、`TestConnectionAsync`、`GetHealthAsync` |
| `LightRAGTool` | `query`、`index_text`、`index_file` actions |
| `KnowledgeBasePanel` | 状态显示、测试连接、索引单文档、上次索引结果、Ask Agent 按钮 |
| `AgentCoreSettings` | `lightragEnabled`、`lightragEndpoint`、API Key 存储 |

### 1.2 已修复 Bug

**`LightRAGClient.IndexFileAsync` NullReferenceException**（已修复）

- **根因**：`using var formContent` + `using var request` 的 C# 逆序 dispose 问题。`formContent` 在 `request` 之前被 dispose，但 `request.Content = formContent`，导致 `SendAsync` 后读取 response 时出现 `ObjectDisposedException`
- **修复**：移除 `formContent` 的 `using`，由 `request` 统一负责 dispose 其 Content

### 1.3 已确认的 LightRAG API（端口 9621）

通过 `/openapi.json` 和实际 curl 测试确认：

```
GET  /health                              → 健康检查
POST /query                               → 查询知识库
POST /documents/text                      → 索引文本
POST /documents/upload                    → 上传文件（multipart）
GET  /documents                           → 列出所有文档（按状态分组）
POST /documents/paginated                 → 分页列出文档
GET  /documents/status_counts             → 各状态文档数量
GET  /documents/track_status/{track_id}  → 轮询索引进度
DELETE /documents/delete_document         → 删除文档（by doc_id）
```

**`GET /documents` 响应格式**（已验证）：

```json
{
  "statuses": {
    "processed": [
      {
        "id": "doc-xxx",
        "file_path": "beautify-urp-documentation.md",
        "content_summary": "...",
        "content_length": 14295,
        "status": "processed",
        "created_at": "2026-05-09T...",
        "updated_at": "2026-05-09T...",
        "track_id": "upload_xxx",
        "chunks_count": 6,
        "error_msg": null
      }
    ],
    "pending": [...],
    "failed": [...]
  }
}
```

**重要发现**：上传成功（HTTP 200）≠ 索引完成。LightRAG 异步处理文档，需要通过 `track_id` 轮询 `/documents/track_status/{track_id}` 确认真实进度。

---

## 2. 目标范围（P0 + P1）

### P0：文档列表 + 删除

| 功能 | 说明 |
|------|------|
| `LightRAGClient.GetDocumentsAsync()` | 调用 `GET /documents`，返回所有文档列表 |
| `LightRAGClient.DeleteDocumentAsync(docId)` | 调用 `DELETE /documents/delete_document` |
| `LightRAGTool.list_documents` action | LLM 可查询知识库中有哪些文档 |
| `LightRAGTool.delete_document` action | LLM 可删除指定文档 |
| `KnowledgeBasePanel` 文档列表区块 | 刷新按钮 + ScrollView + 每条显示名称/摘要/状态 + 删除按钮 |

### P1：track_id 轮询真实进度

| 功能 | 说明 |
|------|------|
| `LightRAGClient.TrackStatusAsync(trackId)` | 调用 `GET /documents/track_status/{track_id}` |
| `IndexFileAsync` 返回 `track_id` | 上传成功后返回 track_id 而非 bool |
| `KnowledgeBasePanel` 轮询进度 | 上传后持续轮询，直到 `status = processed` 或 `failed` |
| UI 显示"处理中"状态 | 区分"已上传"和"已索引"两个阶段 |

### 暂不实现（后续 Phase）

- 批量目录索引（`index_folder`、`index_project_docs`）
- 自动查询策略
- 索引历史列表（本地持久化）
- 代码索引
- 拖拽文件索引

---

## 3. 数据模型设计

### 3.1 新增 C# 数据类（在 `LightRAGClient.cs` 中）

```csharp
/// <summary>
/// LightRAG 文档条目（来自 GET /documents）。
/// </summary>
[Serializable]
public class LightRAGDocument
{
    [JsonProperty("id")]
    public string Id;

    [JsonProperty("file_path")]
    public string FilePath;

    [JsonProperty("content_summary")]
    public string ContentSummary;

    [JsonProperty("content_length")]
    public int ContentLength;

    [JsonProperty("status")]
    public string Status;  // "processed" | "pending" | "failed"

    [JsonProperty("created_at")]
    public string CreatedAt;

    [JsonProperty("updated_at")]
    public string UpdatedAt;

    [JsonProperty("track_id")]
    public string TrackId;

    [JsonProperty("chunks_count")]
    public int ChunksCount;

    [JsonProperty("error_msg")]
    public string ErrorMsg;
}

/// <summary>
/// GET /documents 的完整响应。
/// </summary>
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
/// GET /documents/track_status/{track_id} 的响应。
/// </summary>
[Serializable]
public class LightRAGTrackStatus
{
    [JsonProperty("status")]
    public string Status;  // "pending" | "processing" | "processed" | "failed"

    [JsonProperty("error_msg")]
    public string ErrorMsg;

    [JsonProperty("document_id")]
    public string DocumentId;
}

/// <summary>
/// IndexFileAsync 的返回结果（P1 修改后）。
/// </summary>
public class LightRAGIndexResult
{
    public bool Accepted;      // HTTP 200 上传成功
    public string TrackId;     // 用于轮询进度
    public string ErrorMessage;
}
```

### 3.2 修改 `IndexFileAsync` 返回类型

当前返回 `Task<bool>`，P1 修改为返回 `Task<LightRAGIndexResult>`，以便传递 `track_id`。

**注意**：`LightRAGTool.HandleIndexFile` 和 `KnowledgeBasePanel.IndexFileAsync` 都调用了此方法，需要同步更新。

---

## 4. LightRAGClient 新增方法

### 4.1 `GetDocumentsAsync`

```csharp
/// <summary>
/// 获取知识库中所有文档列表。
/// </summary>
/// <param name="ct">取消令牌</param>
/// <returns>所有文档列表（processed + pending + failed 合并）</returns>
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
        Debug.LogError($"[AgentCore] LightRAGClient.GetDocumentsAsync failed: {ex.Message}");
        return new List<LightRAGDocument>();
    }
}
```

### 4.2 `DeleteDocumentAsync`

```csharp
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
        var url = $"{_baseUrl}/documents/delete_document";
        var client = HttpClientFactory.GetClient();
        using var request = HttpClientFactory.CreateRequest(HttpMethod.Delete, url, _apiKey);

        // DELETE 请求携带 doc_id 作为 query param
        // 实际参数格式需根据 API 确认（query string 或 body）
        // 根据 openapi.json 确认后填写
        var payload = new JObject { ["id"] = docId };
        request.Content = new StringContent(
            payload.ToString(Formatting.None), Encoding.UTF8, "application/json");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

        var response = await client.SendAsync(request, cts.Token);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Debug.LogError(
                $"[AgentCore] LightRAGClient.DeleteDocumentAsync error: " +
                $"{(int)response.StatusCode} {response.ReasonPhrase} - {responseBody}");
            return false;
        }
        return true;
    }
    catch (Exception ex)
    {
        Debug.LogError($"[AgentCore] LightRAGClient.DeleteDocumentAsync failed: {ex.Message}");
        return false;
    }
}
```

> **实现前确认**：通过 `curl -X DELETE http://localhost:9621/documents/delete_document` 确认参数格式（query string `?id=xxx` 还是 JSON body）。

### 4.3 `TrackStatusAsync`

```csharp
/// <summary>
/// 轮询文档索引进度。
/// </summary>
/// <param name="trackId">上传时返回的 track_id</param>
/// <param name="ct">取消令牌</param>
/// <returns>当前索引状态</returns>
public async Task<LightRAGTrackStatus> TrackStatusAsync(string trackId, CancellationToken ct = default)
{
    try
    {
        var url = $"{_baseUrl}/documents/track_status/{Uri.EscapeDataString(trackId)}";
        return await GetAsync<LightRAGTrackStatus>(url, ct);
    }
    catch (Exception ex)
    {
        Debug.LogError($"[AgentCore] LightRAGClient.TrackStatusAsync failed: {ex.Message}");
        return new LightRAGTrackStatus { Status = "failed", ErrorMsg = ex.Message };
    }
}
```

### 4.4 修改 `IndexFileAsync` 返回类型（P1）

将返回类型从 `Task<bool>` 改为 `Task<LightRAGIndexResult>`：

```csharp
public async Task<LightRAGIndexResult> IndexFileAsync(
    string filePath,
    CancellationToken ct = default)
{
    // ... 现有校验逻辑不变 ...

    var response = await client.SendAsync(request, cts.Token);
    var responseBody = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
    {
        Debug.LogError(...);
        return new LightRAGIndexResult { Accepted = false, ErrorMessage = responseBody };
    }

    // 解析 track_id
    string trackId = null;
    try
    {
        var json = JObject.Parse(responseBody);
        trackId = json["track_id"]?.ToString()
               ?? json["id"]?.ToString();  // 兼容不同字段名
    }
    catch { /* 解析失败时 trackId 为 null */ }

    return new LightRAGIndexResult { Accepted = true, TrackId = trackId };
}
```

---

## 5. LightRAGTool 新增 Actions

### 5.1 更新 `_parametersSchema`

在现有 `enum` 中新增 `list_documents`、`delete_document`：

```json
{
  "action": {
    "type": "string",
    "enum": ["query", "index_text", "index_file", "list_documents", "delete_document"],
    "description": "操作类型：query(查询)、index_text(索引文本)、index_file(索引文件)、list_documents(列出文档)、delete_document(删除文档)"
  },
  "doc_id": {
    "type": "string",
    "description": "delete_document 时必填，文档 ID（来自 list_documents 返回的 id 字段）"
  }
}
```

### 5.2 `HandleListDocuments`

```csharp
private async Task<ToolResponse> HandleListDocuments(LightRAGClient client, CancellationToken ct)
{
    var docs = await client.GetDocumentsAsync(ct);

    if (docs.Count == 0)
    {
        return ToolResponse.Ok("知识库中暂无文档。");
    }

    var items = docs.Select(d => new
    {
        id = d.Id,
        file_name = Path.GetFileName(d.FilePath),
        summary = d.ContentSummary,
        status = d.Status,
        chunks = d.ChunksCount,
        created_at = d.CreatedAt
    }).ToArray();

    return ToolResponse.OkWithData(new
    {
        action = "list_documents",
        total = docs.Count,
        documents = items
    }, $"知识库中共有 {docs.Count} 个文档");
}
```

### 5.3 `HandleDeleteDocument`

```csharp
private async Task<ToolResponse> HandleDeleteDocument(LightRAGClient client, JObject parameters, CancellationToken ct)
{
    var docId = ToolHelpers.GetRequiredString(parameters, "doc_id");

    bool success = await client.DeleteDocumentAsync(docId, ct);

    if (success)
    {
        return ToolResponse.OkWithData(new
        {
            action = "delete_document",
            doc_id = docId
        }, $"文档已从知识库中删除：{docId}");
    }

    return ToolResponse.Fail($"删除文档失败：{docId}。请确认文档 ID 正确，并检查 LightRAG 服务状态。");
}
```

### 5.4 更新 `Description` 和 `Metadata`

```csharp
[AgentTool("manage_knowledge",
    Description = "管理项目知识库。支持查询(query)、索引文本(index_text)、索引文件(index_file)、列出文档(list_documents)、删除文档(delete_document)。知识库基于 LightRAG 提供图谱增强的检索能力。",
    Category = "Cloud",
    RequiresMainThread = false)]
```

---

## 6. KnowledgeBasePanel UI 设计

### 6.1 新增"知识库文档"区块

在现有"添加知识"区块之后，新增"知识库文档"区块：

```text
┌─────────────────────────────────────────────────────────┐
│ Knowledge Base                                          │
├─────────────────────────────────────────────────────────┤
│ 状态                                                    │
│   LightRAG: ✓ 已启用                                    │
│   Endpoint: http://localhost:9621                       │
│   连接: ✓ 已连接                                        │
│   [测试连接]  [打开设置]                                 │
├─────────────────────────────────────────────────────────┤
│ 添加知识                                                │
│   [+ 索引文档...]                                       │
│   支持 .md .txt .cs .json .xml .yaml 等格式，最大 5MB   │
├─────────────────────────────────────────────────────────┤
│ 知识库文档                              [↻ 刷新]        │
│   ┌─────────────────────────────────────────────────┐  │
│   │ 📄 beautify-urp-documentation.md    [processed] │  │
│   │    Beautify 3 是一个 URP 后处理插件...           │  │
│   │                                        [🗑 删除] │  │
│   ├─────────────────────────────────────────────────┤  │
│   │ 📄 README.md                         [pending]  │  │
│   │    处理中...                                     │  │
│   │                                        [🗑 删除] │  │
│   └─────────────────────────────────────────────────┘  │
│   共 2 个文档（1 已处理，1 处理中）                      │
├─────────────────────────────────────────────────────────┤
│ 上次索引结果                                            │
│   ✓ 索引成功：README.md（处理中，等待 LightRAG 完成）   │
│   [💬 向 Agent 询问此文档]                              │
└─────────────────────────────────────────────────────────┘
```

### 6.2 新增 UI 元素

```csharp
// 知识库文档区块
private VisualElement _documentsSection;
private Button _refreshDocumentsButton;
private ScrollView _documentsScrollView;
private Label _documentsSummaryLabel;

// 进度轮询状态
private string _pendingTrackId = null;
private CancellationTokenSource _pollCts = null;
```

### 6.3 文档列表条目结构

每个文档条目（`DocumentListItem`）包含：

```text
┌─────────────────────────────────────────────────────────┐
│ 📄 {file_name}                          [{status_badge}]│
│    {content_summary 截断到 80 字符}                      │
│                                              [🗑 删除]  │
└─────────────────────────────────────────────────────────┘
```

状态徽章颜色：
- `processed` → 绿色
- `pending` / `processing` → 黄色
- `failed` → 红色

### 6.4 刷新逻辑

```csharp
private async void OnRefreshDocumentsClicked()
{
    _refreshDocumentsButton.SetEnabled(false);
    _documentsScrollView.Clear();
    _documentsSummaryLabel.text = "加载中...";

    try
    {
        var client = LightRAGClient.FromSettings();
        var docs = await client.GetDocumentsAsync(_cts.Token);
        RenderDocumentList(docs);
    }
    catch (Exception ex)
    {
        _documentsSummaryLabel.text = $"加载失败：{ex.Message}";
    }
    finally
    {
        _refreshDocumentsButton.SetEnabled(true);
    }
}
```

### 6.5 删除逻辑

```csharp
private async void OnDeleteDocumentClicked(string docId, string fileName)
{
    bool confirm = EditorUtility.DisplayDialog(
        "确认删除",
        $"确定要从知识库中删除文档「{fileName}」吗？\n\n此操作不可撤销。",
        "删除", "取消");

    if (!confirm) return;

    var client = LightRAGClient.FromSettings();
    bool success = await client.DeleteDocumentAsync(docId, _cts.Token);

    if (success)
    {
        // 刷新列表
        OnRefreshDocumentsClicked();
    }
    else
    {
        EditorUtility.DisplayDialog("删除失败",
            $"无法删除文档「{fileName}」，请检查 LightRAG 服务状态。", "确定");
    }
}
```

### 6.6 track_id 轮询逻辑（P1）

上传文件后，如果获得了 `track_id`，启动后台轮询：

```csharp
private async Task PollIndexProgressAsync(string trackId, string fileName)
{
    _pollCts?.Cancel();
    _pollCts = new CancellationTokenSource();
    _pollCts.CancelAfter(TimeSpan.FromMinutes(5)); // 最长等待 5 分钟

    var client = LightRAGClient.FromSettings();
    int pollIntervalMs = 2000; // 每 2 秒轮询一次

    while (!_pollCts.Token.IsCancellationRequested)
    {
        await Task.Delay(pollIntervalMs, _pollCts.Token);

        var status = await client.TrackStatusAsync(trackId, _pollCts.Token);

        if (status.Status == "processed")
        {
            // 索引完成
            _lastIndexSummary = $"✓ 索引完成：{fileName}";
            _lastResultLabel.text = _lastIndexSummary;
            _lastResultLabel.AddToClassList("kb-panel__result--success");
            _askAgentButton.style.display = DisplayStyle.Flex;
            // 刷新文档列表
            OnRefreshDocumentsClicked();
            break;
        }
        else if (status.Status == "failed")
        {
            // 索引失败
            _lastIndexSummary = $"✗ 索引失败：{fileName}\n原因：{status.ErrorMsg ?? "未知错误"}";
            _lastResultLabel.text = _lastIndexSummary;
            _lastResultLabel.AddToClassList("kb-panel__result--failed");
            break;
        }
        else
        {
            // 仍在处理中（pending / processing）
            _progressLabel.text = $"LightRAG 处理中：{fileName}（{status.Status}）...";
        }
    }

    _progressOverlay.style.display = DisplayStyle.None;
}
```

**UI 状态流转**（P1 修改后）：

```
用户点击"索引文档"
  → 显示进度遮罩："正在上传：{fileName}..."
  → IndexFileAsync 返回 LightRAGIndexResult
    → Accepted = false → 显示上传失败，结束
    → Accepted = true, TrackId = null → 显示"已上传（无法追踪进度）"，结束
    → Accepted = true, TrackId = "upload_xxx"
      → 更新进度遮罩："LightRAG 处理中：{fileName}..."
      → 启动 PollIndexProgressAsync(trackId)
        → 轮询直到 processed / failed / 超时
        → 完成后刷新文档列表
```

---

## 7. 实现阶段规划（修订版）

### Phase RAG-Doc-1：P0 文档列表与删除

**目标**：用户和 LLM 都能查看知识库中有哪些文档，并可删除。

**任务清单**：

1. **`LightRAGClient`**：
   - 新增数据类：`LightRAGDocument`、`RAGDocumentsResponse`、`RAGDocumentStatuses`
   - 新增方法：`GetDocumentsAsync()`
   - 新增方法：`DeleteDocumentAsync(docId)`（实现前先确认 DELETE 参数格式）

2. **`LightRAGTool`**：
   - 更新 `_parametersSchema`：新增 `list_documents`、`delete_document` 枚举值和 `doc_id` 参数
   - 新增 `HandleListDocuments()` handler
   - 新增 `HandleDeleteDocument()` handler
   - 更新 `switch` 分发和 `Description`/`Metadata`

3. **`KnowledgeBasePanel`**：
   - 新增"知识库文档"区块（`_documentsSection`）
   - 新增刷新按钮（`_refreshDocumentsButton`）
   - 新增 `ScrollView`（`_documentsScrollView`）
   - 新增文档摘要标签（`_documentsSummaryLabel`）
   - 实现 `OnRefreshDocumentsClicked()`
   - 实现 `RenderDocumentList(docs)`（含每条的删除按钮）
   - 实现 `OnDeleteDocumentClicked(docId, fileName)`（含确认对话框）
   - `OnActivated()` 时自动刷新文档列表

**验收标准**：
- [ ] `KnowledgeBasePanel` 显示知识库中所有文档（名称 + 摘要 + 状态）
- [ ] 点击刷新按钮可重新加载文档列表
- [ ] 点击删除按钮弹出确认对话框，确认后删除并刷新列表
- [ ] Chat 中调用 `manage_knowledge(list_documents)` 返回文档列表
- [ ] Chat 中调用 `manage_knowledge(delete_document, doc_id=xxx)` 可删除文档
- [ ] LightRAG 未启用或未配置时，文档列表区块显示提示而非报错

### Phase RAG-Doc-2：P1 track_id 轮询真实进度

**目标**：上传文件后，UI 显示真实的索引进度，直到 LightRAG 完成处理。

**任务清单**：

1. **`LightRAGClient`**：
   - 新增数据类：`LightRAGTrackStatus`、`LightRAGIndexResult`
   - 新增方法：`TrackStatusAsync(trackId)`
   - 修改 `IndexFileAsync` 返回类型：`Task<bool>` → `Task<LightRAGIndexResult>`

2. **`LightRAGTool`**：
   - 更新 `HandleIndexFile`：适配新的 `LightRAGIndexResult` 返回类型
   - 在返回结果中包含 `track_id`（供用户手动查询进度）

3. **`KnowledgeBasePanel`**：
   - 新增 `_pendingTrackId` 和 `_pollCts` 字段
   - 修改 `IndexFileAsync`：适配新返回类型，获取 `track_id`
   - 实现 `PollIndexProgressAsync(trackId, fileName)`
   - 进度遮罩区分"上传中"和"处理中"两个阶段
   - 索引完成后自动刷新文档列表

**验收标准**：
- [ ] 上传文件后，进度遮罩显示"LightRAG 处理中..."而非立即消失
- [ ] 轮询到 `status = processed` 时，显示"✓ 索引完成"并刷新文档列表
- [ ] 轮询到 `status = failed` 时，显示"✗ 索引失败"和错误原因
- [ ] 5 分钟超时后停止轮询，显示"处理超时，请稍后刷新文档列表"
- [ ] `track_id` 为 null 时（服务不返回），降级为旧行为（显示"已上传"）

---

## 8. 实现注意事项

### 8.1 DELETE 参数格式确认

实现 `DeleteDocumentAsync` 前，需要通过以下命令确认参数格式：

```bash
# 方式一：query string
curl -X DELETE "http://localhost:9621/documents/delete_document?id=doc-xxx"

# 方式二：JSON body
curl -X DELETE "http://localhost:9621/documents/delete_document" \
  -H "Content-Type: application/json" \
  -d '{"id": "doc-xxx"}'
```

根据实际响应选择正确的实现方式。

### 8.2 `IndexFileAsync` 返回的 track_id 字段名

上传响应中 `track_id` 的字段名需要通过实际测试确认：

```bash
curl -X POST "http://localhost:9621/documents/upload" \
  -F "file=@README.md" | python -m json.tool
```

可能的字段名：`track_id`、`id`、`document_id`。代码中应做多字段兼容。

### 8.3 文档列表的 CSS 样式

新增的文档列表条目需要在 `ChatWindow.uss` 中添加对应样式：

```css
.kb-panel__doc-item { ... }
.kb-panel__doc-item__name { ... }
.kb-panel__doc-item__summary { ... }
.kb-panel__doc-item__status-badge { ... }
.kb-panel__doc-item__status-badge--processed { color: #4CAF50; }
.kb-panel__doc-item__status-badge--pending { color: #FF9800; }
.kb-panel__doc-item__status-badge--failed { color: #F44336; }
.kb-panel__doc-item__delete-btn { ... }
```

### 8.4 并发安全

- `OnRefreshDocumentsClicked` 和 `PollIndexProgressAsync` 可能并发执行
- 刷新时应取消正在进行的刷新请求（使用独立的 `_refreshCts`）
- 轮询时应取消上一次轮询（`_pollCts?.Cancel()` 后重新创建）

### 8.5 `KnowledgeBasePanel` 的 `_cts` 管理

当前 `_cts` 被测试连接和索引文件共用，需要拆分：

| 字段 | 用途 |
|------|------|
| `_connectionCts` | 测试连接操作 |
| `_indexCts` | 文件上传操作 |
| `_pollCts` | track_id 轮询 |
| `_refreshCts` | 文档列表刷新 |

---

## 9. 后续 Phase（参考）

### Phase RAG-3：批量索引

- `index_folder` action
- `index_project_docs` action（扫描 README.md、docs/、plans/ 等）
- `KnowledgeBasePanel` 新增"索引项目文档"按钮
- 批量结果显示（indexed / failed / skipped）

### Phase RAG-4：查询体验强化

- 更新 `SOUL.md` / `TOOLS.md.template`，明确何时使用知识库
- `query` 支持 `top_k` 参数
- 查询结果展示来源文档名称

### Phase RAG-5：Settings 扩展

- `lightragIncludePatterns`（默认 `*.md,*.txt,*.json,*.yaml,*.yml`）
- `lightragExcludePatterns`（默认排除 Library、Temp、.git 等）
- `lightragMaxFileSizeMb`（默认 5MB）
- `lightragAutoQueryEnabled`（默认 false）

---

## 10. 成功标准

P0 + P1 完成后，应达到：

1. 用户打开 Knowledge Base 面板，能看到 LightRAG 中已有哪些文档（名称 + 摘要 + 状态）
2. 用户能删除不需要的文档（带确认对话框）
3. 用户上传文档后，UI 显示真实的处理进度，而非立即显示"成功"
4. LLM 能通过 `list_documents` 查询知识库文档列表
5. LLM 能通过 `delete_document` 删除指定文档
6. 所有操作失败时，用户能看到清晰的错误原因
7. 服务未配置时，相关功能优雅降级（显示提示，不报错）
