using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Tools.Safety;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace AgentCore.Editor.Tools.Native.Safety
{
    /// <summary>
    /// 查询本次 Play Mode 会话内 Agent 做的运行时内存修改 (v1.12+ ModifyRuntimeState)。
    /// <para>
    /// 数据源 <see cref="PlaymodeChangeLog"/>。纯只读工具,无副作用,不落盘。
    /// 帮助 Agent 自我审视"我在 Play 中改了什么内存值 (未落盘)",
    /// 判断哪些改动值得退出 Play Mode 后永久应用。
    /// </para>
    /// <para>
    /// 记录在退出 Play Mode 时自动清空 —— 非 Play Mode 中查询通常返回空列表。
    /// </para>
    /// </summary>
    [AgentTool("get_playmode_changes",
        Description = "Query runtime in-memory changes made during the current Play Mode session. " +
                      "Returns a list of write operations that were intercepted (not persisted to disk) while in Play Mode. " +
                      "USE FOR: reviewing what runtime mutations were applied during Play Mode testing, " +
                      "deciding which changes are worth re-applying persistently after exiting Play Mode. " +
                      "Records are auto-cleared when exiting Play Mode. Returns empty list outside Play Mode. " +
                      "Read-only, no side effects.",
        Category = "Safety",
        Visibility = ToolVisibility.OnDemand,
        RequiresMainThread = true,
        RiskLevel = ToolRiskLevel.Low)]
    public class GetPlaymodeChangesTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""limit"": { ""type"": ""integer"", ""description"": ""Max number of most-recent records to return (default: 100)"" }
            },
            ""required"": []
        }");

        public ToolMetadata Metadata => new ToolMetadata(
            name: "get_playmode_changes",
            description: "Query runtime in-memory changes made during the current Play Mode session (read-only).",
            category: "Safety",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        public Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            ToolResponse response;

            try
            {
                int limit = ToolHelpers.GetOptionalInt(parameters, "limit", 100);
                if (limit <= 0) limit = 100;

                var all = PlaymodeChangeLog.GetChanges();
                var selected = all.Count > limit
                    ? all.Skip(all.Count - limit).ToList()
                    : all.ToList();

                var items = new JArray();
                foreach (var c in selected)
                {
                    items.Add(new JObject
                    {
                        ["timestamp"] = c.Timestamp.ToString("HH:mm:ss"),
                        ["tool"] = c.Tool,
                        ["action"] = c.Action,
                        ["target"] = c.Target,
                        ["details"] = c.Details
                    });
                }

                var data = new JObject
                {
                    ["is_playing"] = EditorApplication.isPlaying,
                    ["total_recorded"] = all.Count,
                    ["returned"] = items.Count,
                    ["changes"] = items
                };

                string msg = EditorApplication.isPlaying
                    ? $"{all.Count} runtime change(s) recorded this Play Mode session (in-memory only, not persisted)."
                    : "Not in Play Mode. Runtime change log is empty (records are cleared on exiting Play Mode).";

                response = ToolResponse.OkWithData(data, msg);
            }
            catch (Exception ex)
            {
                response = ToolResponse.Fail($"Unexpected error: {ex.Message}");
            }

            sw.Stop();
            return Task.FromResult(response.ToToolResult(sw.Elapsed.TotalMilliseconds));
        }
    }
}
