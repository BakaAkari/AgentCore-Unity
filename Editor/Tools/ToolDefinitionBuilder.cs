using System;
using System.Collections.Generic;
using System.Linq;
using AgentCore.Editor.Config;
using AgentCore.Editor.LLM;
using Newtonsoft.Json.Linq;
using UnityEngine;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Tools
{
    /// <summary>
    /// 工具定义构建器 — 将 AgentCore 的 <see cref="ToolMetadata"/> 转换为 OpenAI function calling 格式的
    /// <see cref="ToolDefinition"/>。
    /// <para>
    /// 这是一个纯转换层，不包含业务逻辑，仅负责数据格式映射：
    /// <list type="bullet">
    ///   <item><see cref="ToolMetadata"/> → <see cref="ToolDefinition"/>（单个转换）</item>
    ///   <item><see cref="ToolRegistry"/> → <c>List&lt;ToolDefinition&gt;</c>（批量转换）</item>
    ///   <item>支持按分类、按名称白名单过滤构建</item>
    /// </list>
    /// </para>
    /// <para>
    /// 设计要点：
    /// <list type="bullet">
    ///   <item>Description 截断到 1024 字符以符合 OpenAI API 限制</item>
    ///   <item>ParametersSchema 直接透传（已经是 JSON Schema 格式的 JObject）</item>
    ///   <item>空 schema 时生成 <c>{"type": "object", "properties": {}}</c></item>
    /// </list>
    /// </para>
    /// </summary>
    public static class ToolDefinitionBuilder
    {
        #region 常量

        /// <summary>日志前缀</summary>
        private const string LogPrefix = "[AgentCore] ToolDefinitionBuilder: ";

        /// <summary>OpenAI API 对 function description 的最大长度限制</summary>
        private const int MaxDescriptionLength = 1024;

        #endregion

        #region 单个构建

        /// <summary>
        /// 将单个 <see cref="ToolMetadata"/> 转换为 <see cref="ToolDefinition"/>。
        /// </summary>
        /// <param name="metadata">工具元数据</param>
        /// <returns>OpenAI function calling 格式的工具定义</returns>
        /// <exception cref="ArgumentNullException">metadata 为 null 时抛出</exception>
        public static ToolDefinition Build(ToolMetadata metadata)
        {
            if (metadata == null)
                throw new ArgumentNullException(nameof(metadata));

            return new ToolDefinition
            {
                Type = "function",
                Function = new FunctionDefinition
                {
                    Name = metadata.Name,
                    Description = TruncateDescription(metadata.Description),
                    Parameters = EnsureValidSchema(metadata.ParametersSchema)
                }
            };
        }

        /// <summary>
        /// 将单个 <see cref="IAgentTool"/> 转换为 <see cref="ToolDefinition"/>。
        /// 等价于 <c>Build(tool.Metadata)</c>。
        /// </summary>
        /// <param name="tool">工具实例</param>
        /// <returns>OpenAI function calling 格式的工具定义</returns>
        /// <exception cref="ArgumentNullException">tool 或 tool.Metadata 为 null 时抛出</exception>
        public static ToolDefinition Build(IAgentTool tool)
        {
            if (tool == null)
                throw new ArgumentNullException(nameof(tool));
            if (tool.Metadata == null)
                throw new ArgumentNullException(nameof(tool), "Tool.Metadata cannot be null");

            return Build(tool.Metadata);
        }

        #endregion

        #region 批量构建

        /// <summary>
        /// 将 <see cref="ToolRegistry"/> 中所有已注册工具转换为 <see cref="ToolDefinition"/> 列表。
        /// </summary>
        /// <returns>所有工具的 ToolDefinition 列表</returns>
        public static List<ToolDefinition> BuildAll()
        {
            var tools = ToolRegistry.Instance.GetAllTools();
            if (tools == null || tools.Count == 0)
            {
                AgentCoreLog.Warning($"{LogPrefix}No tools registered in ToolRegistry, returning empty list");
                return new List<ToolDefinition>();
            }

            var definitions = new List<ToolDefinition>(tools.Count);
            foreach (var tool in tools)
            {
                try
                {
                    definitions.Add(Build(tool));
                }
                catch (Exception ex)
                {
                    AgentCoreLog.Error($"{LogPrefix}Failed to build definition for tool '{tool.Metadata?.Name}': {ex.Message}");
                }
            }

            AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Built {definitions.Count} tool definitions from ToolRegistry");
            return definitions;
        }

        /// <summary>
        /// 将 <see cref="ToolRegistry"/> 中所有已注册且未被禁用的工具转换为 <see cref="ToolDefinition"/> 列表。
        /// <para>
        /// 根据 <see cref="AgentCoreSettings"/> 中的 <c>disabledToolCategories</c> 和 <c>disabledTools</c>
        /// 过滤掉被禁用的工具，仅构建启用状态的工具定义。
        /// </para>
        /// </summary>
        /// <returns>启用状态的工具定义列表</returns>
        public static List<ToolDefinition> BuildAllEnabled()
        {
            var tools = ToolRegistry.Instance.GetAllTools();
            if (tools == null || tools.Count == 0)
            {
                AgentCoreLog.Warning($"{LogPrefix}No tools registered in ToolRegistry, returning empty list");
                return new List<ToolDefinition>();
            }

            var settings = AgentCoreSettings.instance;
            var definitions = new List<ToolDefinition>(tools.Count);
            int skippedCount = 0;

            foreach (var tool in tools)
            {
                try
                {
                    var meta = tool.Metadata;
                    if (meta == null) continue;

                    // 检查工具是否被禁用
                    if (settings.IsToolDisabled(meta.Name, meta.Category))
                    {
                        skippedCount++;
                        continue;
                    }

                    definitions.Add(Build(tool));
                }
                catch (Exception ex)
                {
                    AgentCoreLog.Error($"{LogPrefix}Failed to build definition for tool '{tool.Metadata?.Name}': {ex.Message}");
                }
            }

            if (skippedCount > 0)
            {
                AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Built {definitions.Count} tool definitions ({skippedCount} disabled tools skipped)");
            }
            else
            {
                AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Built {definitions.Count} tool definitions from ToolRegistry");
            }

            return definitions;
        }

        /// <summary>
        /// 按分类构建工具定义列表（过滤模式）。
        /// </summary>
        /// <param name="category">分类名称（大小写敏感），如 "Core"、"Meta"、"Scripting"</param>
        /// <returns>属于指定分类的工具定义列表</returns>
        /// <exception cref="ArgumentNullException">category 为 null 或空时抛出</exception>
        public static List<ToolDefinition> BuildByCategory(string category)
        {
            if (string.IsNullOrWhiteSpace(category))
                throw new ArgumentNullException(nameof(category));

            var tools = ToolRegistry.Instance.GetToolsByCategory(category);
            if (tools == null || tools.Count == 0)
            {
                AgentCoreLog.Warning($"{LogPrefix}No tools found for category '{category}'");
                return new List<ToolDefinition>();
            }

            var definitions = new List<ToolDefinition>(tools.Count);
            foreach (var tool in tools)
            {
                try
                {
                    definitions.Add(Build(tool));
                }
                catch (Exception ex)
                {
                    AgentCoreLog.Error($"{LogPrefix}Failed to build definition for tool '{tool.Metadata?.Name}': {ex.Message}");
                }
            }

            AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Built {definitions.Count} tool definitions for category '{category}'");
            return definitions;
        }

        /// <summary>
        /// 按名称列表构建工具定义（白名单模式）。
        /// 仅构建指定名称的工具，忽略不存在的名称。
        /// </summary>
        /// <param name="toolNames">工具名称列表</param>
        /// <returns>匹配的工具定义列表</returns>
        /// <exception cref="ArgumentNullException">toolNames 为 null 时抛出</exception>
        public static List<ToolDefinition> BuildByNames(IEnumerable<string> toolNames)
        {
            if (toolNames == null)
                throw new ArgumentNullException(nameof(toolNames));

            var nameList = toolNames.ToList();
            if (nameList.Count == 0)
            {
                return new List<ToolDefinition>();
            }

            var registry = ToolRegistry.Instance;
            var definitions = new List<ToolDefinition>(nameList.Count);
            var missingNames = new List<string>();

            foreach (var name in nameList)
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var tool = registry.GetTool(name);
                if (tool == null)
                {
                    missingNames.Add(name);
                    continue;
                }

                try
                {
                    definitions.Add(Build(tool));
                }
                catch (Exception ex)
                {
                    AgentCoreLog.Error($"{LogPrefix}Failed to build definition for tool '{name}': {ex.Message}");
                }
            }

            if (missingNames.Count > 0)
            {
                AgentCoreLog.Warning($"{LogPrefix}Tools not found in registry: {string.Join(", ", missingNames)}");
            }

            AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Built {definitions.Count}/{nameList.Count} tool definitions by name whitelist");
            return definitions;
        }

        /// <summary>
        /// 从元数据列表直接构建工具定义（不经过 ToolRegistry 查询）。
        /// 适用于已有元数据列表的场景。
        /// </summary>
        /// <param name="metadataList">工具元数据列表</param>
        /// <returns>工具定义列表</returns>
        /// <exception cref="ArgumentNullException">metadataList 为 null 时抛出</exception>
        public static List<ToolDefinition> BuildFromMetadata(IEnumerable<ToolMetadata> metadataList)
        {
            if (metadataList == null)
                throw new ArgumentNullException(nameof(metadataList));

            var definitions = new List<ToolDefinition>();
            foreach (var metadata in metadataList)
            {
                if (metadata == null) continue;

                try
                {
                    definitions.Add(Build(metadata));
                }
                catch (Exception ex)
                {
                    AgentCoreLog.Error($"{LogPrefix}Failed to build definition for metadata '{metadata.Name}': {ex.Message}");
                }
            }

            return definitions;
        }

        #endregion

        #region 内部辅助方法

        /// <summary>
        /// 截断描述文本以符合 OpenAI API 的长度限制。
        /// </summary>
        /// <param name="description">原始描述文本</param>
        /// <returns>截断后的描述文本</returns>
        private static string TruncateDescription(string description)
        {
            if (string.IsNullOrEmpty(description))
                return string.Empty;

            if (description.Length <= MaxDescriptionLength)
                return description;

            // 截断并添加省略号提示
            return description.Substring(0, MaxDescriptionLength - 3) + "...";
        }

        /// <summary>
        /// 确保参数 schema 是有效的 JSON Schema 对象。
        /// 如果输入为 null 或空，返回最小有效 schema。
        /// </summary>
        /// <param name="schema">原始参数 schema</param>
        /// <returns>有效的 JSON Schema JObject</returns>
        private static JObject EnsureValidSchema(JObject schema)
        {
            if (schema == null || !schema.HasValues)
            {
                // 返回最小有效 JSON Schema
                return new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject()
                };
            }

            // 确保顶层有 "type": "object"
            if (schema["type"] == null)
            {
                schema = schema.DeepClone() as JObject;
                if (schema != null)
                {
                    schema["type"] = "object";
                }
            }

            return schema;
        }

        #endregion
    }
}
