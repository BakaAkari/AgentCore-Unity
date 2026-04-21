using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.Tools.Infrastructure
{
    /// <summary>
    /// 标准化工具响应 JSON 格式。
    /// </summary>
    public class ToolResponse
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("data")]
        public JToken Data { get; set; }

        [JsonProperty("error")]
        public string Error { get; set; }

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
            JToken jData;
            if (data is JToken jt)
                jData = jt;
            else if (data is string s)
                jData = new JValue(s);
            else
                jData = JToken.FromObject(data);

            return new ToolResponse
            {
                Success = true,
                Message = message ?? "Operation completed successfully.",
                Data = jData
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
            else
            {
                return ToolResult.Fail(Error ?? "Unknown error", executionTimeMs);
            }
        }
    }
}
