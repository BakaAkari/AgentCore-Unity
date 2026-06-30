using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Tools.Safety;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.Tools.Native.Meta
{
    /// <summary>
    /// 元工具：允许 LLM 发现和激活按需工具分类（G.3 ActiveToolScope）。
    /// <para>
    /// 此工具始终可见（AlwaysVisible），LLM 在需要使用特定分类工具时，
    /// 先调用此工具的 "list" action 查看可用分类，再调用 "activate" action 激活所需分类。
    /// 激活后，该分类的工具将在后续轮次中对 LLM 可见。
    /// </para>
    /// </summary>
    [AgentTool("request_tools",
        Description = "Discover and activate additional tool categories that are not visible by default. " +
            "action:list — shows all available categories with tool names and descriptions (use when you need a tool not in your current set). " +
            "action:activate — enables a category for this session (tools become available in your next response). " +
            "Categories include: Specialized (UI, physics, audio, lighting, camera, terrain, timeline, etc.), " +
            "Extended (build, packages, tests, profiler, optimization, navigation, input, etc.), " +
            "Cloud (memory, knowledge base), Scripting (execute_code — restricted). " +
            "Always check request_tools list before telling the user you cannot do something — the capability may be in an unactivated category.",
        Category = "Meta",
        RequiresMainThread = false,
        RiskLevel = ToolRiskLevel.Low,
        Capabilities = ToolCapability.None,
        Visibility = ToolVisibility.AlwaysVisible)]
    public class RequestToolsTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""list"", ""activate""],
                    ""description"": ""Action to perform: 'list' to see available tool categories, 'activate' to enable categories for this session""
                },
                ""categories"": {
                    ""type"": ""array"",
                    ""items"": { ""type"": ""string"" },
                    ""description"": ""(activate only) List of category names to activate""
                }
            },
            ""required"": [""action""]
        }");

        /// <summary>Tool metadata for auto-discovery registration.</summary>
        public ToolMetadata Metadata => new ToolMetadata(
            name: "request_tools",
            description: "Discover and activate additional tool categories. Use 'list' to see available categories, 'activate' to enable them.",
            category: "Meta",
            parametersSchema: _parametersSchema,
            requiresMainThread: false
        );

        /// <summary>
        /// 当前会话的作用域状态引用。
        /// 由 AgentLoop 在初始化时通过 <see cref="SetScopeState"/> 注入。
        /// </summary>
        private static ToolScopeState _scopeState;

        /// <summary>
        /// 注入当前会话的 ToolScopeState 引用。
        /// 每次新会话或 Domain Reload 恢复后由 AgentLoop 调用。
        /// </summary>
        public static void SetScopeState(ToolScopeState state)
        {
            _scopeState = state;
        }

        public Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            ToolResponse response;

            try
            {
                var action = ToolHelpers.GetRequiredString(parameters, "action").ToLowerInvariant();
                switch (action)
                {
                    case "list":
                        response = HandleList();
                        break;
                    case "activate":
                        response = HandleActivate(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail($"Unknown action: '{action}'. Valid actions: list, activate");
                        break;
                }
            }
            catch (Exception ex)
            {
                response = ToolResponse.Fail($"Error: {ex.Message}");
            }

            sw.Stop();
            return Task.FromResult(response.ToToolResult(sw.Elapsed.TotalMilliseconds));
        }

        private ToolResponse HandleList()
        {
            var state = _scopeState;
            var categories = ToolScopeResolver.GetAvailableCategories(state);

            if (categories.Count == 0)
            {
                return ToolResponse.Ok("All tool categories are already visible. No additional categories to activate.");
            }

            var result = new JObject
            {
                ["total_categories"] = categories.Count,
                ["categories"] = new JArray(categories.Select(c => new JObject
                {
                    ["name"] = c.Category,
                    ["visibility"] = c.Visibility.ToString(),
                    ["tool_count"] = c.ToolCount,
                    ["tools"] = new JArray(c.ToolNames),
                    ["activated"] = c.IsActivated
                }))
            };

            return ToolResponse.OkWithData(result,
                $"Found {categories.Count} on-demand categories. Use 'activate' action with category names to enable them.");
        }

        private ToolResponse HandleActivate(JObject parameters)
        {
            var state = _scopeState;
            if (state == null)
            {
                return ToolResponse.Fail("Tool scoping is not initialized. Cannot activate categories.");
            }

            var categoriesToken = parameters["categories"];
            if (categoriesToken == null || categoriesToken.Type != JTokenType.Array)
            {
                return ToolResponse.Fail("'categories' parameter is required and must be an array of category names.");
            }

            var requestedCategories = categoriesToken.ToObject<List<string>>();
            if (requestedCategories == null || requestedCategories.Count == 0)
            {
                return ToolResponse.Fail("'categories' array must not be empty.");
            }

            // 验证请求的分类是否存在于可激活列表中
            var available = ToolScopeResolver.GetAvailableCategories(state);
            var availableNames = new HashSet<string>(available.Select(c => c.Category), StringComparer.OrdinalIgnoreCase);

            var activated = new List<string>();
            var alreadyActive = new List<string>();
            var notFound = new List<string>();

            foreach (var cat in requestedCategories)
            {
                if (string.IsNullOrWhiteSpace(cat)) continue;

                if (!availableNames.Contains(cat))
                {
                    notFound.Add(cat);
                    continue;
                }

                if (state.IsCategoryActivated(cat))
                {
                    alreadyActive.Add(cat);
                }
                else
                {
                    state.ActivateCategory(cat);
                    activated.Add(cat);
                }
            }

            var resultObj = new JObject
            {
                ["activated"] = new JArray(activated),
                ["already_active"] = new JArray(alreadyActive),
                ["not_found"] = new JArray(notFound)
            };

            string message;
            if (activated.Count > 0)
            {
                message = $"Activated {activated.Count} category(ies): {string.Join(", ", activated)}. " +
                          "Tools from these categories will be available in subsequent calls.";
            }
            else if (alreadyActive.Count > 0)
            {
                message = "All requested categories are already activated.";
            }
            else
            {
                message = $"No categories were activated. Not found: {string.Join(", ", notFound)}";
            }

            return ToolResponse.OkWithData(resultObj, message);
        }
    }
}
