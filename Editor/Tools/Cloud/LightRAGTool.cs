using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Cloud;
using AgentCore.Editor.Config;
using AgentCore.Editor.Tools.Infrastructure;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.Tools.Cloud
{
    /// <summary>
    /// LightRAG 知识库管理工具。
    /// 封装 LightRAGClient，让 LLM 可以通过 tool_call 查询和索引知识库。
    /// 支持查询和索引文本操作。
    /// </summary>
    [AgentTool("manage_knowledge",
        Description = "管理项目知识库。支持查询(query)和索引文本(index_text)。知识库基于 LightRAG 提供图谱增强的检索能力。",
        Category = "Cloud",
        RequiresMainThread = false)]
    public class LightRAGTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""query"", ""index_text""],
                    ""description"": ""操作类型：query(查询知识库)、index_text(索引文本到知识库)""
                },
                ""content"": {
                    ""type"": ""string"",
                    ""description"": ""query 时为查询内容，index_text 时为要索引的文本""
                },
                ""mode"": {
                    ""type"": ""string"",
                    ""enum"": [""local"", ""global"", ""hybrid"", ""naive""],
                    ""description"": ""query 时可选，检索模式，默认 hybrid""
                },
                ""description"": {
                    ""type"": ""string"",
                    ""description"": ""index_text 时可选，文本描述""
                }
            },
            ""required"": [""action"", ""content""]
        }");

        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_knowledge",
            description: "管理项目知识库。支持查询(query)和索引文本(index_text)。知识库基于 LightRAG 提供图谱增强的检索能力。",
            category: "Cloud",
            parametersSchema: _parametersSchema,
            requiresMainThread: false
        );

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
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: query, index_text");
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
            var content = ToolHelpers.GetOptionalString(parameters, "content");
            if (string.IsNullOrEmpty(content))
            {
                return ToolResponse.Fail("参数 'content' 在 query 操作中为必填项");
            }

            var mode = ToolHelpers.GetOptionalString(parameters, "mode", "hybrid");

            // 验证 mode 值
            var validModes = new[] { "local", "global", "hybrid", "naive" };
            if (!validModes.Contains(mode.ToLowerInvariant()))
            {
                return ToolResponse.Fail(
                    $"无效的 mode 值: '{mode}'。有效值: local, global, hybrid, naive");
            }

            var result = await client.QueryAsync(content, mode, ct: ct);

            if (result.Success)
            {
                var sources = result.Sources?.Select(s => new
                {
                    content = s.Content,
                    score = s.Score
                }).ToArray();

                return ToolResponse.OkWithData(new
                {
                    action = "query",
                    query = content,
                    mode,
                    response = result.Response,
                    source_count = sources?.Length ?? 0,
                    sources
                }, "知识库查询完成");
            }

            return ToolResponse.Fail($"知识库查询失败: {result.Response}");
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
    }
}
