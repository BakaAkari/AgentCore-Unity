using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.Tools
{
    #region ToolMetadata — 工具元数据

    /// <summary>
    /// 工具元数据 — 描述一个工具的名称、参数 schema 等信息。
    /// <para>
    /// 这是 AgentCore 内部的工具描述格式，与 <c>ToolDefinition</c>（OpenAI API 格式）分离，
    /// 以实现工具系统与 LLM 具体格式的解耦。Step 3 的 <c>ToolDefinitionBuilder</c> 负责
    /// <c>ToolMetadata</c> → <c>ToolDefinition</c> 的转换。
    /// </para>
    /// </summary>
    public class ToolMetadata
    {
        /// <summary>工具名称，如 "manage_script"、"read_file"</summary>
        public string Name { get; }

        /// <summary>工具描述，供 LLM 理解工具用途</summary>
        public string Description { get; }

        /// <summary>工具分类，如 "Core"、"Meta"、"Scripting"、"Specialized"、"Utility"</summary>
        public string Category { get; }

        /// <summary>JSON Schema 格式的参数描述</summary>
        public JObject ParametersSchema { get; }

        /// <summary>是否需要在 Unity 主线程执行</summary>
        public bool RequiresMainThread { get; }

        /// <summary>
        /// 创建工具元数据实例。
        /// </summary>
        /// <param name="name">工具名称，全局唯一</param>
        /// <param name="description">工具描述</param>
        /// <param name="category">工具分类</param>
        /// <param name="parametersSchema">JSON Schema 格式的参数描述</param>
        /// <param name="requiresMainThread">是否需要在 Unity 主线程执行，默认 true</param>
        /// <exception cref="ArgumentNullException">name 或 description 为 null 时抛出</exception>
        public ToolMetadata(
            string name,
            string description,
            string category,
            JObject parametersSchema,
            bool requiresMainThread = true)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Description = description ?? throw new ArgumentNullException(nameof(description));
            Category = category ?? "default";
            ParametersSchema = parametersSchema ?? new JObject();
            RequiresMainThread = requiresMainThread;
        }
    }

    #endregion

    #region ToolResult — 工具执行结果

    /// <summary>
    /// 工具执行结果 — 封装工具调用的成功/失败状态及输出内容。
    /// <para>
    /// 使用静态工厂方法 <see cref="Ok"/> / <see cref="Fail"/> 创建实例，
    /// 包含执行时间用于性能监控。
    /// </para>
    /// </summary>
    public class ToolResult
    {
        /// <summary>执行是否成功</summary>
        public bool Success { get; }

        /// <summary>成功时的输出内容（返回给 LLM 的文本）</summary>
        public string Output { get; }

        /// <summary>失败时的错误信息</summary>
        public string Error { get; }

        /// <summary>执行耗时（毫秒）</summary>
        public double ExecutionTimeMs { get; }

        /// <summary>是否涉及脚本编译（用于自动编译检查）</summary>
        public bool IsCompileRelated { get; set; }

        /// <summary>
        /// 私有构造函数，通过静态工厂方法创建实例。
        /// </summary>
        private ToolResult(bool success, string output, string error, double executionTimeMs)
        {
            Success = success;
            Output = output;
            Error = error;
            ExecutionTimeMs = executionTimeMs;
        }

        /// <summary>
        /// 创建成功结果。
        /// </summary>
        /// <param name="output">输出内容</param>
        /// <param name="executionTimeMs">执行耗时（毫秒）</param>
        /// <returns>成功的 <see cref="ToolResult"/> 实例</returns>
        public static ToolResult Ok(string output, double executionTimeMs = 0)
        {
            return new ToolResult(true, output, null, executionTimeMs);
        }

        /// <summary>
        /// 创建失败结果。
        /// </summary>
        /// <param name="error">错误信息</param>
        /// <param name="executionTimeMs">执行耗时（毫秒）</param>
        /// <returns>失败的 <see cref="ToolResult"/> 实例</returns>
        public static ToolResult Fail(string error, double executionTimeMs = 0)
        {
            return new ToolResult(false, null, error, executionTimeMs);
        }

        /// <summary>
        /// 获取返回给 LLM 的内容文本。
        /// 成功时返回 Output，失败时返回格式化的错误信息。
        /// </summary>
        public string GetContentForLLM()
        {
            return Success
                ? Output ?? string.Empty
                : $"[Error] {Error ?? "Unknown error"}";
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return Success
                ? $"ToolResult.Ok({ExecutionTimeMs:F1}ms): {Truncate(Output, 100)}"
                : $"ToolResult.Fail({ExecutionTimeMs:F1}ms): {Truncate(Error, 100)}";
        }

        /// <summary>截断字符串用于日志显示</summary>
        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return "(empty)";
            return value.Length <= maxLength ? value : value.Substring(0, maxLength) + "...";
        }
    }

    #endregion

    #region IAgentTool — 工具接口

    /// <summary>
    /// Agent 工具接口 — 所有自建工具的统一抽象。
    /// <para>
    /// Phase 2 仅预留接口定义，不实现具体的自建工具。
    /// 实现此接口的工具通过 <see cref="ToolRegistry"/> 注册后，
    /// 即可被 Agent Loop 发现和调用。
    /// </para>
    /// <example>
    /// <code>
    /// public class MyCustomTool : IAgentTool
    /// {
    ///     public ToolMetadata Metadata => new ToolMetadata(
    ///         name: "my_custom_tool",
    ///         description: "A custom tool example",
    ///         category: "custom",
    ///         parametersSchema: JObject.Parse(@"{ ""type"": ""object"", ""properties"": {} }")
    ///     );
    ///
    ///     public async Task&lt;ToolResult&gt; ExecuteAsync(JObject parameters, CancellationToken cancellationToken)
    ///     {
    ///         // 工具逻辑
    ///         return ToolResult.Ok("Done");
    ///     }
    /// }
    /// </code>
    /// </example>
    /// </summary>
    public interface IAgentTool
    {
        /// <summary>工具元数据，描述工具的名称、参数等信息</summary>
        ToolMetadata Metadata { get; }

        /// <summary>
        /// 异步执行工具。
        /// </summary>
        /// <param name="parameters">工具参数（JSON 对象）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>工具执行结果</returns>
        Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default);
    }

    #endregion
}
