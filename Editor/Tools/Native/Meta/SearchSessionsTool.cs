using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Session;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Tools.Safety;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AgentCore.Editor.Tools.Native.Meta
{
    /// <summary>
    /// 跨会话检索工具（v1.14.9+ Session tag 互通）。
    /// <para>
    /// 背景：用户反馈希望"同一个 tag 下的聊天内容能互通"。设计上刻意选择
    /// "按需检索片段"而非"自动把其他会话内容注入 system prompt"——
    /// 后者会让 token 消耗随 tag 下会话数量线性增长且不可控，前者由 agent
    /// 自主判断是否需要跨会话上下文，只在真正需要时才发起检索，且返回的是
    /// 片段（snippet）而不是整份会话内容。
    /// </para>
    /// <para>
    /// 强制要求 <c>tag</c> 参数——不支持无 tag 的全局搜索，避免检索范围失控。
    /// 结果按命中相关度排序并截断到 <c>limit</c>，扫描的会话文件数也硬顶
    /// <c>max_scan</c>——同一 tag 下即使有上百个会话，也不会一次性全量扫描/返回。
    /// </para>
    /// <para>
    /// 只返回片段级结果；agent 判断某条结果确实相关后，应通过其他会话管理能力
    /// （UI 侧切换/加载）精确查看该 session 的完整内容，而不是依赖本工具返回全文。
    /// </para>
    /// </summary>
    [AgentTool("search_sessions",
        Description = "跨会话全文检索——在指定 tag 下的其他会话历史中搜索关键词，返回命中片段（snippet），" +
                      "不是整份会话内容。用于'同一 tag 下会话互通'场景：需要参考同 tag 下其他会话讨论过的内容时使用。" +
                      "必须指定 tag（不支持无 tag 的全局搜索）。结果按命中次数+更新时间排序，数量和扫描范围均有上限，" +
                      "tag 下会话很多时可能无法覆盖全部——返回结果会标明是否发生了扫描截断（scan_truncated）。" +
                      "若某条结果看起来相关但片段不够，需要更多上下文时，应使用 session 管理能力显式加载该 session_id 的完整内容，" +
                      "而不是假设本工具会返回全文。",
        Category = "Meta",
        RequiresMainThread = false,
        RiskLevel = ToolRiskLevel.ReadOnly,
        Capabilities = ToolCapability.None)]
    public class SearchSessionsTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""tag"": {
                    ""type"": ""string"",
                    ""description"": ""必填。要检索的会话 tag（大小写不敏感精确匹配），只在打了该 tag 的会话范围内搜索。""
                },
                ""query"": {
                    ""type"": ""string"",
                    ""description"": ""必填。检索关键词（大小写不敏感子串匹配）。""
                },
                ""limit"": {
                    ""type"": ""integer"",
                    ""description"": ""最多返回的命中会话数，默认 10，上限 50。""
                },
                ""max_scan"": {
                    ""type"": ""integer"",
                    ""description"": ""最多扫描的会话文件数（按最近更新优先），默认 50，上限 200。""
                }
            },
            ""required"": [""tag"", ""query""]
        }");

        public ToolMetadata Metadata => new ToolMetadata(
            name: "search_sessions",
            description: "跨会话全文检索：在指定 tag 下的其他会话历史中搜索关键词，返回命中片段而非整份内容。",
            category: "Meta",
            parametersSchema: _parametersSchema,
            requiresMainThread: false
        );

        public Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ToolResponse response;

            try
            {
                string tag = ToolHelpers.GetRequiredString(parameters, "tag").Trim();
                string query = ToolHelpers.GetRequiredString(parameters, "query").Trim();

                if (string.IsNullOrEmpty(tag))
                {
                    response = ToolResponse.Fail("tag 不能为空。");
                }
                else if (string.IsNullOrEmpty(query))
                {
                    response = ToolResponse.Fail("query 不能为空。");
                }
                else
                {
                    int limit = ToolHelpers.GetOptionalInt(parameters, "limit", 10);
                    limit = Mathf.Clamp(limit, 1, 50);

                    int maxScan = ToolHelpers.GetOptionalInt(parameters, "max_scan", 50);
                    maxScan = Mathf.Clamp(maxScan, 1, 200);

                    // 排除调用方自己当前正在进行的会话——检索"其他"会话，
                    // 搜自己当前对话没有意义（当前对话本身就在上下文里）。
                    string excludeSessionId = SessionManager.Instance.CurrentSessionId;

                    var result = SessionStorage.SearchSessions(tag, query, limit, maxScan, excludeSessionId);
                    response = BuildResponse(tag, query, result);
                }
            }
            catch (System.ArgumentException ex)
            {
                response = ToolResponse.Fail(ex.Message);
            }
            catch (System.Exception ex)
            {
                response = ToolResponse.Fail($"检索失败: {ex.Message}");
            }

            sw.Stop();
            return Task.FromResult(response.ToToolResult(sw.Elapsed.TotalMilliseconds));
        }

        private static ToolResponse BuildResponse(string tag, string query, Session.SessionSearchResult result)
        {
            var hitsArray = new JArray();
            foreach (var hit in result.Hits)
            {
                var snippetsArray = new JArray();
                foreach (var s in hit.Snippets)
                {
                    snippetsArray.Add(new JObject
                    {
                        ["role"] = s.Role,
                        ["snippet"] = s.Snippet
                    });
                }

                hitsArray.Add(new JObject
                {
                    ["session_id"] = hit.SessionId,
                    ["title"] = hit.Title,
                    ["updated_at"] = hit.UpdatedAt,
                    ["match_count"] = hit.MatchCount,
                    ["snippets"] = snippetsArray
                });
            }

            var data = new JObject
            {
                ["tag"] = tag,
                ["query"] = query,
                ["matched_tag_session_count"] = result.MatchedTagSessionCount,
                ["scanned_session_count"] = result.ScannedSessionCount,
                ["scan_truncated"] = result.ScanTruncated,
                ["hits"] = hitsArray
            };

            string summary;
            if (result.Hits.Count == 0)
            {
                summary = result.MatchedTagSessionCount == 0
                    ? $"tag '{tag}' 下没有找到任何会话。"
                    : $"tag '{tag}' 下扫描了 {result.ScannedSessionCount} 个会话，没有找到匹配 '{query}' 的内容。";
            }
            else
            {
                summary = $"tag '{tag}' 下找到 {result.Hits.Count} 个会话命中 '{query}'"
                    + (result.ScanTruncated
                        ? $"（注意：该 tag 下共有 {result.MatchedTagSessionCount} 个会话，本次仅扫描了最近更新的 {result.ScannedSessionCount} 个，可能未覆盖全部——如需扩大范围可提高 max_scan）。"
                        : "。");
            }

            return ToolResponse.OkWithData(data, summary);
        }
    }
}
