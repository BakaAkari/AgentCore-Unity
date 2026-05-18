using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Cloud;
using AgentCore.Editor.Config;
using AgentCore.Editor.Tools.Infrastructure;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AgentCore.Editor.Tools.Cloud
{
    /// <summary>
    /// LightRAG 知识库管理工具。
    /// 封装 LightRAGClient，让 LLM 可以通过 tool_call 查询、索引和管理知识库文档。
    /// 支持：query、index_text、index_file、list_documents、delete_document。
    /// </summary>
    [AgentTool("manage_knowledge",
        Description = "管理项目知识库。支持查询(query)、索引文本(index_text)、索引文件(index_file)、批量索引文件夹(index_folder)、自动索引项目文档(index_project_docs)、列出文档(list_documents)、删除文档(delete_document)、查询索引进度(check_index_status)。知识库基于 LightRAG 提供图谱增强的检索能力。",
        Category = "Cloud",
        RequiresMainThread = false)]
    public class LightRAGTool : IAgentTool
    {
        /// <summary>允许索引的文件扩展名</summary>
        private static readonly string[] AllowedExtensions =
        {
            ".md", ".txt", ".cs", ".json", ".xml", ".yaml", ".yml",
            ".html", ".htm", ".rst", ".pdf"
        };

        /// <summary>单文件最大大小（字节）：5MB</summary>
        private const long MaxFileSizeBytes = 5 * 1024 * 1024;

        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""query"", ""index_text"", ""index_file"", ""index_folder"", ""index_project_docs"", ""list_documents"", ""delete_document"", ""check_index_status""],
                    ""description"": ""操作类型：query(查询知识库)、index_text(索引文本)、index_file(索引单个文件)、index_folder(批量索引文件夹)、index_project_docs(自动索引项目文档)、list_documents(列出所有文档)、delete_document(删除指定文档)、check_index_status(查询索引进度)""
                },
                ""content"": {
                    ""type"": ""string"",
                    ""description"": ""【query 时必填】查询内容；【index_text 时必填】要索引的文本内容""
                },
                ""file_path"": {
                    ""type"": ""string"",
                    ""description"": ""【index_file 时必填】相对于项目根目录的文件路径（如 docs/README.md）""
                },
                ""folder_path"": {
                    ""type"": ""string"",
                    ""description"": ""【index_folder 时必填】相对于项目根目录的文件夹路径（如 docs/）""
                },
                ""recursive"": {
                    ""type"": ""boolean"",
                    ""description"": ""【index_folder 时可选】是否递归子目录，默认 true"",
                    ""default"": true
                },
                ""mode"": {
                    ""type"": ""string"",
                    ""enum"": [""local"", ""global"", ""hybrid"", ""naive""],
                    ""description"": ""【query 时可选】检索模式，默认 hybrid""
                },
                ""top_k"": {
                    ""type"": ""integer"",
                    ""description"": ""【query 时可选】返回结果数量上限，默认 5，范围 1~50"",
                    ""minimum"": 1,
                    ""maximum"": 50,
                    ""default"": 5
                },
                ""description"": {
                    ""type"": ""string"",
                    ""description"": ""【index_text 时可选】文本描述""
                },
                ""doc_id"": {
                    ""type"": ""string"",
                    ""description"": ""【delete_document 时必填】文档 ID（来自 list_documents 返回的 id 字段）""
                },
                ""track_id"": {
                    ""type"": ""string"",
                    ""description"": ""【check_index_status 时必填】上传时返回的追踪 ID""
                }
            },
            ""required"": [""action""]
        }");

        /// <summary>
        /// 工具元数据，与 [AgentTool] 特性保持一致。
        /// </summary>
        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_knowledge",
            description: "管理项目知识库。支持查询(query)、索引文本(index_text)、索引文件(index_file)、批量索引文件夹(index_folder)、自动索引项目文档(index_project_docs)、列出文档(list_documents)、删除文档(delete_document)、查询索引进度(check_index_status)。知识库基于 LightRAG 提供图谱增强的检索能力。",
            category: "Cloud",
            parametersSchema: _parametersSchema,
            requiresMainThread: false
        );

        /// <summary>
        /// 执行知识库操作。
        /// </summary>
        public async Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            ToolResponse response;

            try
            {
                // 检查 LightRAG 是否已配置
                var settings = AgentCoreSettings.instance;
                if (string.IsNullOrEmpty(settings.lightragEndpoint))
                {
                    response = ToolResponse.Fail(
                        "LightRAG 服务未配置，请在 AgentCore Settings 中设置 LightRAG Endpoint URL");
                    sw.Stop();
                    return response.ToToolResult(sw.Elapsed.TotalMilliseconds);
                }

                var action = ToolHelpers.GetRequiredString(parameters, "action").ToLowerInvariant();
                var client = LightRAGClient.FromSettings();

                switch (action)
                {
                    case "query":
                        response = await HandleQuery(client, parameters, cancellationToken);
                        break;
                    case "index_text":
                        response = await HandleIndexText(client, parameters, cancellationToken);
                        break;
                    case "index_file":
                        response = await HandleIndexFile(client, parameters, cancellationToken);
                        break;
                    case "index_folder":
                        response = await HandleIndexFolder(client, parameters, cancellationToken);
                        break;
                    case "index_project_docs":
                        response = await HandleIndexProjectDocs(client, cancellationToken);
                        break;
                    case "list_documents":
                        response = await HandleListDocuments(client, cancellationToken);
                        break;
                    case "delete_document":
                        response = await HandleDeleteDocument(client, parameters, cancellationToken);
                        break;
                    case "check_index_status":
                        response = await HandleCheckIndexStatus(client, parameters, cancellationToken);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: query, index_text, index_file, index_folder, index_project_docs, list_documents, delete_document, check_index_status");
                        break;
                }
            }
            catch (ArgumentException ex)
            {
                response = ToolResponse.Fail(ex.Message);
            }
            catch (Exception ex)
            {
                response = ToolResponse.Fail($"LightRAG 操作失败: {ex.Message}");
            }

            sw.Stop();
            return response.ToToolResult(sw.Elapsed.TotalMilliseconds);
        }

        // ─────────────────────────────────────────
        //  Action Handlers
        // ─────────────────────────────────────────

        private async Task<ToolResponse> HandleQuery(LightRAGClient client, JObject parameters, CancellationToken ct)
        {
            // LLM 可能使用 "query" 或 "content" 作为参数名，两者都支持
            var content = ToolHelpers.GetOptionalString(parameters, "query");
            if (string.IsNullOrEmpty(content))
            {
                content = ToolHelpers.GetOptionalString(parameters, "content");
            }
            if (string.IsNullOrEmpty(content))
            {
                return ToolResponse.Fail("参数 'query' 或 'content' 在 query 操作中为必填项");
            }

            var mode = ToolHelpers.GetOptionalString(parameters, "mode", "hybrid");

            // 验证 mode 值
            var validModes = new[] { "local", "global", "hybrid", "naive" };
            if (!validModes.Contains(mode.ToLowerInvariant()))
            {
                return ToolResponse.Fail(
                    $"无效的 mode 值: '{mode}'。有效值: local, global, hybrid, naive");
            }

            // 读取 top_k（1~50，默认 5）
            int topK = ToolHelpers.GetOptionalInt(parameters, "top_k", 5);
            topK = Mathf.Clamp(topK, 1, 50);

            var result = await client.QueryAsync(content, mode, topK, ct);

            if (result.Success)
            {
                var sources = result.Sources?.Select(s => new
                {
                    content = s.Content,
                    score = s.Score,
                    document_name = GetDocumentNameFromMetadata(s.Metadata)
                }).ToArray();

                return ToolResponse.OkWithData(new
                {
                    action = "query",
                    query = content,
                    mode,
                    top_k = topK,
                    response = result.Response,
                    source_count = sources?.Length ?? 0,
                    sources
                }, "知识库查询完成");
            }

            return ToolResponse.Fail($"知识库查询失败: {result.Response}");
        }

        private static string GetDocumentNameFromMetadata(Dictionary<string, object> metadata)
        {
            if (metadata == null) return "(未知来源)";
            if (metadata.TryGetValue("file_path", out var fp) && fp != null)
                return Path.GetFileName(fp.ToString()) ?? "(未知来源)";
            if (metadata.TryGetValue("source", out var src) && src != null)
                return Path.GetFileName(src.ToString()) ?? "(未知来源)";
            return "(未知来源)";
        }

        private async Task<ToolResponse> HandleIndexText(LightRAGClient client, JObject parameters, CancellationToken ct)
        {
            var content = ToolHelpers.GetOptionalString(parameters, "content");
            if (string.IsNullOrEmpty(content))
            {
                return ToolResponse.Fail("参数 'content' 在 index_text 操作中为必填项");
            }

            var description = ToolHelpers.GetOptionalString(parameters, "description");
            var success = await client.IndexTextAsync(content, description, ct);

            if (success)
            {
                return ToolResponse.OkWithData(new
                {
                    action = "index_text",
                    content_length = content.Length,
                    description = description ?? "(无描述)"
                }, "文本已成功索引到知识库");
            }

            return ToolResponse.Fail("索引文本到知识库失败，请检查 LightRAG 服务状态");
        }

        private async Task<ToolResponse> HandleIndexFile(LightRAGClient client, JObject parameters, CancellationToken ct)
        {
            var relativePath = ToolHelpers.GetRequiredString(parameters, "file_path");

            // 解析绝对路径（相对于项目根目录）
            string projectRoot = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..")).Replace('\\', '/').TrimEnd('/');
            string absolutePath = Path.GetFullPath(
                Path.Combine(projectRoot, relativePath)).Replace('\\', '/');

            // 安全校验：必须在项目根目录内
            if (!absolutePath.StartsWith(projectRoot + "/", StringComparison.OrdinalIgnoreCase))
            {
                return ToolResponse.Fail(
                    $"安全限制：只允许索引项目根目录内的文件。\n" +
                    $"项目根目录：{projectRoot}\n" +
                    $"请求路径：{absolutePath}");
            }

            // 校验文件存在
            if (!File.Exists(absolutePath))
            {
                return ToolResponse.Fail(
                    $"文件不存在：{relativePath}\n" +
                    $"（解析路径：{absolutePath}）");
            }

            // 校验扩展名
            string ext = Path.GetExtension(absolutePath).ToLowerInvariant();
            bool extAllowed = false;
            foreach (var allowed in AllowedExtensions)
            {
                if (ext == allowed) { extAllowed = true; break; }
            }
            if (!extAllowed)
            {
                return ToolResponse.Fail(
                    $"不支持的文件类型：'{ext}'。\n" +
                    $"支持的类型：{string.Join(", ", AllowedExtensions)}");
            }

            // 校验文件大小
            var fileInfo = new FileInfo(absolutePath);
            if (fileInfo.Length > MaxFileSizeBytes)
            {
                float sizeMB = fileInfo.Length / (1024f * 1024f);
                return ToolResponse.Fail(
                    $"文件过大：{sizeMB:F1}MB（限制 5MB）。\n" +
                    $"请将大文件拆分后分批索引，或使用 index_text 分段索引内容。");
            }

            // 排除目录检查（不索引 Library、Temp、.git 等）
            var excludedDirs = new[] { "/Library/", "/Temp/", "/.git/", "/obj/", "/bin/" };
            foreach (var excluded in excludedDirs)
            {
                if (absolutePath.IndexOf(excluded, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return ToolResponse.Fail(
                        $"文件位于排除目录中，不允许索引：{relativePath}");
                }
            }

            // 执行上传
            string fileName = Path.GetFileName(absolutePath);
            var result = await client.IndexFileAsync(absolutePath, ct);

            if (!result.Accepted)
            {
                return ToolResponse.Fail(
                    $"上传文件失败：{fileName}\n" +
                    $"原因：{result.ErrorMessage ?? "请检查 LightRAG 服务状态和文件格式"}");
            }

            // 上传成功，返回结果（含 track_id 供进度追踪）
            if (!string.IsNullOrEmpty(result.TrackId))
            {
                return ToolResponse.OkWithData(new
                {
                    action = "index_file",
                    file_path = relativePath,
                    file_name = fileName,
                    file_size_kb = (int)(fileInfo.Length / 1024),
                    track_id = result.TrackId,
                    note = "文件已上传，LightRAG 正在异步处理。可通过 track_id 追踪进度，或稍后使用 list_documents 查看处理结果。"
                }, $"文件已上传到知识库（处理中）：{fileName}");
            }

            return ToolResponse.OkWithData(new
            {
                action = "index_file",
                file_path = relativePath,
                file_name = fileName,
                file_size_kb = (int)(fileInfo.Length / 1024)
            }, $"文件已成功索引到知识库：{fileName}");
        }

        private async Task<ToolResponse> HandleListDocuments(LightRAGClient client, CancellationToken ct)
        {
            var docs = await client.GetDocumentsAsync(ct);

            if (docs.Count == 0)
            {
                return ToolResponse.Ok("知识库中暂无文档。可使用 index_file 或 index_text 添加文档。");
            }

            var items = docs.Select(d => new
            {
                id = d.Id,
                file_name = string.IsNullOrEmpty(d.FilePath)
                    ? "(未知)"
                    : Path.GetFileName(d.FilePath),
                file_path = d.FilePath,
                summary = d.ContentSummary,
                status = d.Status,
                chunks_count = d.ChunksCount,
                created_at = d.CreatedAt,
                error_msg = d.ErrorMsg
            }).ToArray();

            // 按状态分组统计
            int processed = docs.Count(d => string.Equals(d.Status, "processed", StringComparison.OrdinalIgnoreCase));
            int pending = docs.Count(d =>
                string.Equals(d.Status, "pending", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Status, "processing", StringComparison.OrdinalIgnoreCase));
            int failed = docs.Count(d => string.Equals(d.Status, "failed", StringComparison.OrdinalIgnoreCase));

            return ToolResponse.OkWithData(new
            {
                action = "list_documents",
                total = docs.Count,
                processed,
                pending,
                failed,
                documents = items
            }, $"知识库中共有 {docs.Count} 个文档（{processed} 已处理，{pending} 处理中，{failed} 失败）");
        }

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
                }, $"文档已从知识库中删除（ID：{docId}）");
            }

            return ToolResponse.Fail(
                $"删除文档失败（ID：{docId}）。\n" +
                $"请确认文档 ID 正确（可通过 list_documents 查看），并检查 LightRAG 服务状态。");
        }

        private async Task<ToolResponse> HandleCheckIndexStatus(LightRAGClient client, JObject parameters, CancellationToken ct)
        {
            var trackId = ToolHelpers.GetRequiredString(parameters, "track_id");
            var status = await client.TrackStatusAsync(trackId, ct);

            return ToolResponse.OkWithData(new
            {
                action = "check_index_status",
                track_id = trackId,
                status = status?.Status ?? "unknown",
                document_id = status?.DocumentId,
                error_msg = status?.ErrorMsg
            }, $"索引状态：{status?.Status ?? "unknown"}");
        }

        private async Task<ToolResponse> HandleIndexFolder(LightRAGClient client, JObject parameters, CancellationToken ct)
        {
            var relativePath = ToolHelpers.GetRequiredString(parameters, "folder_path");
            bool recursive = ToolHelpers.GetOptionalBool(parameters, "recursive", true);

            // 解析绝对路径
            string projectRoot = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..")).Replace('\\', '/').TrimEnd('/');
            string absolutePath = Path.GetFullPath(
                Path.Combine(projectRoot, relativePath)).Replace('\\', '/');

            // 安全校验
            if (!absolutePath.StartsWith(projectRoot + "/", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(absolutePath, projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                return ToolResponse.Fail(
                    $"安全限制：只允许索引项目根目录内的文件夹。\n" +
                    $"项目根目录：{projectRoot}\n" +
                    $"请求路径：{absolutePath}");
            }

            if (!Directory.Exists(absolutePath))
            {
                return ToolResponse.Fail(
                    $"文件夹不存在：{relativePath}\n" +
                    $"(解析路径：{absolutePath})");
            }

            // 收集文件
            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var files = new List<string>();
            foreach (var ext in AllowedExtensions)
            {
                try
                {
                    var found = Directory.GetFiles(absolutePath, $"*{ext}", searchOption);
                    files.AddRange(found);
                }
                catch { /* 忽略无权限目录 */ }
            }

            // 去重并排序
            files = files.Distinct().OrderBy(f => f).ToList();

            // 排除目录检查
            var excludedDirs = new[] { "/Library/", "/Temp/", "/.git/", "/obj/", "/bin/" };
            files = files.Where(f =>
            {
                foreach (var excluded in excludedDirs)
                {
                    if (f.IndexOf(excluded, StringComparison.OrdinalIgnoreCase) >= 0)
                        return false;
                }
                return true;
            }).ToList();

            if (files.Count == 0)
            {
                return ToolResponse.Ok(
                    $"文件夹中没有可索引的文件。\n" +
                    $"路径：{relativePath}\n" +
                    $"支持的类型：{string.Join(", ", AllowedExtensions)}");
            }

            // 批量索引
            int successCount = 0;
            int failCount = 0;
            var failedFiles = new List<string>();
            var trackIds = new List<string>();

            foreach (var filePath in files)
            {
                try
                {
                    var result = await client.IndexFileAsync(filePath, ct);
                    if (result.Accepted)
                    {
                        successCount++;
                        if (!string.IsNullOrEmpty(result.TrackId))
                            trackIds.Add(result.TrackId);
                    }
                    else
                    {
                        failCount++;
                        failedFiles.Add(Path.GetFileName(filePath));
                    }
                }
                catch
                {
                    failCount++;
                    failedFiles.Add(Path.GetFileName(filePath));
                }
            }

            return ToolResponse.OkWithData(new
            {
                action = "index_folder",
                folder_path = relativePath,
                recursive,
                total_files = files.Count,
                succeeded = successCount,
                failed = failCount,
                failed_files = failedFiles,
                track_ids = trackIds
            }, $"已索引 {successCount}/{files.Count} 个文件" +
               (failCount > 0 ? $"（{failCount} 个失败）" : "") +
               (trackIds.Count > 0 ? $"。可通过 check_index_status 追踪进度。" : ""));
        }

        private async Task<ToolResponse> HandleIndexProjectDocs(LightRAGClient client, CancellationToken ct)
        {
            string projectRoot = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..")).Replace('\\', '/').TrimEnd('/');

            var targets = new List<(string path, string desc)>();

            // README 文件
            foreach (var readme in new[] { "README.md", "README_CN.md", "readme.md", "Readme.md" })
            {
                var path = Path.Combine(projectRoot, readme);
                if (File.Exists(path)) targets.Add((path, readme));
            }

            // 文档目录
            var docDirs = new[]
            {
                Path.Combine(projectRoot, "docs"),
                Path.Combine(projectRoot, "plans"),
                Path.Combine(projectRoot, "Assets", "Docs"),
                Path.Combine(projectRoot, "Assets", "Documentation")
            };

            foreach (var dir in docDirs)
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    var mdFiles = Directory.GetFiles(dir, "*.md", SearchOption.AllDirectories);
                    foreach (var f in mdFiles)
                    {
                        targets.Add((f, Path.GetFileName(f)));
                    }
                }
                catch { /* 忽略 */ }
            }

            // 去重
            targets = targets.GroupBy(t => t.path).Select(g => g.First()).ToList();

            int successCount = 0;
            int failCount = 0;
            var failedFiles = new List<string>();
            var trackIds = new List<string>();

            foreach (var (filePath, _) in targets)
            {
                if (!File.Exists(filePath)) continue;

                try
                {
                    var result = await client.IndexFileAsync(filePath, ct);
                    if (result.Accepted)
                    {
                        successCount++;
                        if (!string.IsNullOrEmpty(result.TrackId))
                            trackIds.Add(result.TrackId);
                    }
                    else
                    {
                        failCount++;
                        failedFiles.Add(Path.GetFileName(filePath));
                    }
                }
                catch
                {
                    failCount++;
                    failedFiles.Add(Path.GetFileName(filePath));
                }
            }

            return ToolResponse.OkWithData(new
            {
                action = "index_project_docs",
                total_files = targets.Count,
                succeeded = successCount,
                failed = failCount,
                failed_files = failedFiles,
                track_ids = trackIds
            }, $"已索引 {successCount}/{targets.Count} 个项目文档" +
               (failCount > 0 ? $"（{failCount} 个失败）" : "") +
               (trackIds.Count > 0 ? $"。可通过 check_index_status 追踪进度。" : ""));
        }
    }
}
