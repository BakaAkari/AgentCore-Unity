using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.Tools.Infrastructure
{
    /// <summary>
    /// 多目标操作中单个目标（路径 / GameObject / 子操作）的执行结果，
    /// 供 <see cref="ToolResponse.Partial"/> / <see cref="ToolResponse.FromItems"/> 逐项汇报。
    /// </summary>
    public class ToolItemResult
    {
        /// <summary>目标标识（资源路径、对象名、子操作序号等）</summary>
        [JsonProperty("target")]
        public string Target { get; set; }

        /// <summary>该目标是否成功</summary>
        [JsonProperty("ok")]
        public bool Ok { get; set; }

        /// <summary>失败原因（成功时为 null）</summary>
        [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
        public string Error { get; set; }

        /// <summary>可选的附加信息</summary>
        [JsonProperty("detail", NullValueHandling = NullValueHandling.Ignore)]
        public JToken Detail { get; set; }

        /// <summary>创建成功项</summary>
        public static ToolItemResult Succeeded(string target, object detail = null)
        {
            return new ToolItemResult { Target = target, Ok = true, Detail = ToolResponse.ToJToken(detail) };
        }

        /// <summary>创建失败项</summary>
        public static ToolItemResult Failed(string target, string error, object detail = null)
        {
            return new ToolItemResult
            {
                Target = target,
                Ok = false,
                Error = string.IsNullOrEmpty(error) ? "Unknown error" : error,
                Detail = ToolResponse.ToJToken(detail)
            };
        }
    }

    /// <summary>
    /// 标准化工具响应 JSON 格式。
    /// <para>
    /// 结果分三种：ok（<see cref="Ok"/> / <see cref="OkWithData"/>）、
    /// partial（<see cref="Partial"/>：部分目标成功、部分失败，已产生的修改保留）、
    /// failed（<see cref="Fail"/>）。partial 在 JSON 中为 <c>success=false, status="partial"</c>，
    /// 让模型明确知道这不是完全成功；ok / failed 的 JSON 与以前完全一致。
    /// </para>
    /// </summary>
    public class ToolResponse
    {
        /// <summary>partial 结果的 status 值</summary>
        public const string StatusPartial = "partial";

        /// <summary>失败摘要里最多逐条列出的失败项数量</summary>
        private const int MaxListedFailures = 10;

        /// <summary>是否完全成功（partial 为 false）</summary>
        [JsonProperty("success")]
        public bool Success { get; set; }

        /// <summary>结果状态：仅 partial 时为 "partial"，ok / failed 不输出该字段（保持旧格式）</summary>
        [JsonProperty("status", NullValueHandling = NullValueHandling.Ignore)]
        public string Status { get; private set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("data")]
        public JToken Data { get; set; }

        [JsonProperty("error")]
        public string Error { get; set; }

        /// <summary>逐项结果（仅 Partial / FromItems 填充）</summary>
        [JsonProperty("items", NullValueHandling = NullValueHandling.Ignore)]
        public List<ToolItemResult> Items { get; private set; }

        /// <summary>是否为部分成功</summary>
        [JsonIgnore]
        public bool IsPartial => Status == StatusPartial;

        private ToolResponse() { }

        /// <summary>
        /// 创建成功响应（无数据）
        /// </summary>
        public static ToolResponse Ok(string message = "Operation completed successfully.")
        {
            return new ToolResponse
            {
                Success = true,
                Message = message
            };
        }

        /// <summary>
        /// 创建成功响应（带数据）
        /// </summary>
        public static ToolResponse OkWithData(object data, string message = null)
        {
            return new ToolResponse
            {
                Success = true,
                Message = message ?? "Operation completed successfully.",
                Data = ToJToken(data)
            };
        }

        /// <summary>
        /// 创建失败响应
        /// </summary>
        public static ToolResponse Fail(string error)
        {
            return new ToolResponse
            {
                Success = false,
                Error = error
            };
        }

        /// <summary>
        /// 创建部分成功响应：一部分目标成功、一部分失败（已成功的修改保留）。
        /// <para>
        /// <see cref="Success"/> 为 false、<see cref="Status"/> 为 "partial"；
        /// <see cref="Error"/> 自动汇总"多少成功 / 多少失败 / 失败项及原因"，
        /// <see cref="Message"/> 前缀 "PARTIAL:" 提醒模型这不是完全成功。
        /// </para>
        /// </summary>
        /// <param name="message">对整体操作的描述（可为 null）</param>
        /// <param name="items">逐项结果</param>
        /// <param name="data">可选的附加数据</param>
        public static ToolResponse Partial(string message, IEnumerable<ToolItemResult> items, object data = null)
        {
            var list = NormalizeItems(items);
            int okCount = list.Count(i => i.Ok);
            int failCount = list.Count - okCount;
            var counts = $"{okCount} of {list.Count} item(s) succeeded, {failCount} failed";

            return new ToolResponse
            {
                Success = false,
                Status = StatusPartial,
                Message = string.IsNullOrEmpty(message)
                    ? $"PARTIAL: {counts}."
                    : $"PARTIAL: {message} ({counts}).",
                Data = ToJToken(data),
                Items = list,
                Error = BuildFailureSummary(list, "Partially succeeded")
            };
        }

        /// <summary>
        /// 根据逐项结果自动选择结果类型：全部成功 → ok（带 items），全部失败 → failed，
        /// 混合 → partial。没有任何项时视为 ok。
        /// </summary>
        public static ToolResponse FromItems(string message, IEnumerable<ToolItemResult> items, object data = null)
        {
            var list = NormalizeItems(items);
            int okCount = list.Count(i => i.Ok);

            if (okCount == list.Count)
            {
                var ok = OkWithData(data, message);
                ok.Items = list;
                return ok;
            }

            if (okCount == 0)
            {
                // 失败结果只把 Error 交给模型，因此逐项原因全部写进 Error
                var prefix = string.IsNullOrEmpty(message) ? "All items failed" : $"{message} — all items failed";
                var fail = Fail(BuildFailureSummary(list, prefix));
                fail.Items = list;
                return fail;
            }

            return Partial(message, list, data);
        }

        /// <summary>
        /// 序列化为 JSON 字符串（供 ToolResult 使用）
        /// </summary>
        public string ToJson()
        {
            var settings = new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                Formatting = Formatting.None
            };
            return JsonConvert.SerializeObject(this, settings);
        }

        /// <summary>
        /// 转换为 ToolResult（供 IAgentTool.ExecuteAsync 返回）
        /// </summary>
        public ToolResult ToToolResult(double executionTimeMs = 0)
        {
            if (Success)
            {
                return ToolResult.Ok(ToJson(), executionTimeMs);
            }
            else if (IsPartial)
            {
                // 部分成功：完整 JSON（含逐项结果）交给模型，ToolResult 标记为非完全成功
                return ToolResult.Partial(ToJson(), executionTimeMs);
            }
            else
            {
                return ToolResult.Fail(Error ?? "Unknown error", executionTimeMs);
            }
        }

        /// <summary>把任意对象转换为 JToken（null → null）</summary>
        internal static JToken ToJToken(object data)
        {
            if (data == null) return null;
            if (data is JToken jt) return jt;
            if (data is string s) return new JValue(s);
            return JToken.FromObject(data);
        }

        private static List<ToolItemResult> NormalizeItems(IEnumerable<ToolItemResult> items)
        {
            return items?.Where(i => i != null).ToList() ?? new List<ToolItemResult>();
        }

        /// <summary>
        /// 生成失败摘要："{prefix}: X of N item(s) succeeded, Y failed. Failed: a (原因); b (原因)…"
        /// </summary>
        private static string BuildFailureSummary(List<ToolItemResult> list, string prefix)
        {
            var failed = list.Where(i => !i.Ok).ToList();
            int okCount = list.Count - failed.Count;
            var sb = new StringBuilder();
            sb.Append(prefix).Append(": ")
              .Append(okCount).Append(" of ").Append(list.Count).Append(" item(s) succeeded, ")
              .Append(failed.Count).Append(" failed.");

            if (failed.Count > 0)
            {
                sb.Append(" Failed: ");
                for (int i = 0; i < failed.Count && i < MaxListedFailures; i++)
                {
                    if (i > 0) sb.Append("; ");
                    sb.Append(string.IsNullOrEmpty(failed[i].Target) ? "(unnamed)" : failed[i].Target)
                      .Append(" (").Append(failed[i].Error ?? "Unknown error").Append(')');
                }
                if (failed.Count > MaxListedFailures)
                    sb.Append("; ... and ").Append(failed.Count - MaxListedFailures).Append(" more");
                sb.Append('.');
            }

            return sb.ToString();
        }
    }
}
