using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Cloud;
using AgentCore.Editor.Config;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Tools.Safety;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.Tools.Cloud
{
    /// <summary>
    /// mem0 记忆管理工具。
    /// 封装 Mem0Client，让 LLM 可以通过 tool_call 管理长期记忆。
    /// 支持添加、搜索、列出、删除记忆操作。
    /// </summary>
    [AgentTool("manage_memory",
        Description = "管理长期记忆。支持添加(add)、搜索(search)、列出(list)、删除(delete)记忆。记忆会跨会话持久化存储在 mem0 服务中。",
        Category = "Cloud",
        RequiresMainThread = false,
        RiskLevel = ToolRiskLevel.External,
        Capabilities = ToolCapability.NetworkAccess)]
    public class Mem0Tool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""add"", ""search"", ""list"", ""delete""],
                    ""description"": ""操作类型：add(添加记忆)、search(搜索记忆)、list(列出记忆)、delete(删除记忆)""
                },
                ""content"": {
                    ""type"": ""string"",
                    ""description"": ""add 时为要记忆的内容，search 时为搜索查询文本""
                },
                ""memory_id"": {
                    ""type"": ""string"",
                    ""description"": ""delete 时必填，要删除的记忆 ID""
                },
                ""limit"": {
                    ""type"": ""integer"",
                    ""description"": ""search/list 时可选，返回结果数量上限，默认 10""
                }
            },
            ""required"": [""action""]
        }");

        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_memory",
            description: "管理长期记忆。支持添加(add)、搜索(search)、列出(list)、删除(delete)记忆。记忆会跨会话持久化存储在 mem0 服务中。",
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
                // 检查 mem0 是否已启用
                var settings = AgentCoreSettings.instance;
                if (!settings.mem0Enabled)
                {
                    response = ToolResponse.Fail(
                        "mem0 记忆服务已禁用，请在 AgentCore Settings 中启用");
                    sw.Stop();
                    return response.ToToolResult(sw.Elapsed.TotalMilliseconds);
                }

                // 检查 mem0 Endpoint 是否已配置
                if (string.IsNullOrWhiteSpace(settings.mem0Endpoint))
                {
                    response = ToolResponse.Fail(
                        "mem0 服务未配置 Endpoint URL，请在 AgentCore Settings 中设置");
                    sw.Stop();
                    return response.ToToolResult(sw.Elapsed.TotalMilliseconds);
                }

                var action = ToolHelpers.GetRequiredString(parameters, "action").ToLowerInvariant();
                var client = Mem0Client.FromSettings();

                switch (action)
                {
                    case "add":
                        response = await HandleAdd(client, parameters, cancellationToken);
                        break;
                    case "search":
                        response = await HandleSearch(client, parameters, cancellationToken);
                        break;
                    case "list":
                        response = await HandleList(client, parameters, cancellationToken);
                        break;
                    case "delete":
                        response = await HandleDelete(client, parameters, cancellationToken);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: add, search, list, delete");
                        break;
                }
            }
            catch (ArgumentException ex)
            {
                response = ToolResponse.Fail(ex.Message);
            }
            catch (Exception ex)
            {
                response = ToolResponse.Fail($"mem0 操作失败: {ex.Message}");
            }

            sw.Stop();
            return response.ToToolResult(sw.Elapsed.TotalMilliseconds);
        }

        // ─────────────────────────────────────────
        //  Action Handlers
        // ─────────────────────────────────────────

        private async Task<ToolResponse> HandleAdd(Mem0Client client, JObject parameters, CancellationToken ct)
        {
            var content = ToolHelpers.GetOptionalString(parameters, "content");
            if (string.IsNullOrEmpty(content))
            {
                return ToolResponse.Fail("参数 'content' 在 add 操作中为必填项");
            }

            var result = await client.AddMemoryAsync(content, ct: ct);

            if (result.Success)
            {
                return ToolResponse.OkWithData(new
                {
                    action = "add",
                    memory_id = result.Id,
                    message = result.Message
                }, "记忆添加成功");
            }

            return ToolResponse.Fail($"添加记忆失败: {result.Message}");
        }

        private async Task<ToolResponse> HandleSearch(Mem0Client client, JObject parameters, CancellationToken ct)
        {
            var content = ToolHelpers.GetOptionalString(parameters, "content");
            if (string.IsNullOrEmpty(content))
            {
                return ToolResponse.Fail("参数 'content' 在 search 操作中为必填项");
            }

            var limit = ToolHelpers.GetOptionalInt(parameters, "limit", 10);
            var memories = await client.SearchMemoryAsync(content, limit: limit, ct: ct);

            var results = memories.Select(m => new
            {
                id = m.Id,
                content = m.Content,
                score = m.Score,
                created_at = m.CreatedAt,
                updated_at = m.UpdatedAt
            }).ToArray();

            return ToolResponse.OkWithData(new
            {
                action = "search",
                query = content,
                count = results.Length,
                memories = results
            }, $"找到 {results.Length} 条相关记忆");
        }

        private async Task<ToolResponse> HandleList(Mem0Client client, JObject parameters, CancellationToken ct)
        {
            var limit = ToolHelpers.GetOptionalInt(parameters, "limit", 10);
            var memories = await client.ListMemoriesAsync(limit: limit, ct: ct);

            var results = memories.Select(m => new
            {
                id = m.Id,
                content = m.Content,
                created_at = m.CreatedAt,
                updated_at = m.UpdatedAt
            }).ToArray();

            return ToolResponse.OkWithData(new
            {
                action = "list",
                count = results.Length,
                memories = results
            }, $"共 {results.Length} 条记忆");
        }

        private async Task<ToolResponse> HandleDelete(Mem0Client client, JObject parameters, CancellationToken ct)
        {
            var memoryId = ToolHelpers.GetOptionalString(parameters, "memory_id");
            if (string.IsNullOrEmpty(memoryId))
            {
                return ToolResponse.Fail("参数 'memory_id' 在 delete 操作中为必填项");
            }

            var success = await client.DeleteMemoryAsync(memoryId, ct: ct);

            if (success)
            {
                return ToolResponse.OkWithData(new
                {
                    action = "delete",
                    memory_id = memoryId
                }, $"记忆 '{memoryId}' 已删除");
            }

            return ToolResponse.Fail($"删除记忆 '{memoryId}' 失败，请检查 ID 是否正确");
        }
    }
}
