using System;
using System.Collections.Generic;
using System.Linq;
using AgentCore.Editor.Config;
using AgentCore.Editor.Tools.Infrastructure;
using UnityEngine;

namespace AgentCore.Editor.Tools
{
    /// <summary>
    /// 工具作用域解析器（G.3 ActiveToolScope）。
    /// <para>
    /// 根据工具的 <see cref="ToolVisibility"/> 和当前 <see cref="ToolScopeState"/>，
    /// 决定哪些工具应当被包含在本轮 LLM 调用的 tool definitions 中。
    /// </para>
    /// <para>
    /// 解析规则：
    /// <list type="bullet">
    ///   <item><see cref="ToolVisibility.AlwaysVisible"/> — 始终包含</item>
    ///   <item><see cref="ToolVisibility.OnDemand"/> — 仅当其分类已被 <see cref="ToolScopeState"/> 激活时包含</item>
    ///   <item><see cref="ToolVisibility.Restricted"/> — 仅当用户未禁用该工具且其分类已被激活时包含</item>
    /// </list>
    /// </para>
    /// <para>
    /// 当 <c>toolScopingEnabled = false</c> 时，所有非 Restricted 工具都会被暴露（退化到旧行为）。
    /// </para>
    /// </summary>
    public static class ToolScopeResolver
    {
        /// <summary>
        /// 解析当前应暴露给 LLM 的工具列表。
        /// </summary>
        /// <param name="scopeState">当前会话的作用域状态</param>
        /// <returns>应暴露的工具 metadata 列表</returns>
        public static List<ToolMetadata> ResolveVisibleTools(ToolScopeState scopeState)
        {
            var settings = AgentCoreSettings.instance;
            var allTools = ToolRegistry.Instance.GetAllTools();
            var result = new List<ToolMetadata>(allTools.Count);

            bool scopingEnabled = settings.toolScopingEnabled;

            foreach (var tool in allTools)
            {
                var meta = tool.Metadata;
                if (meta == null) continue;

                // 先检查是否被用户禁用（与 BuildAllEnabled 保持一致的过滤逻辑）
                if (settings.IsToolDisabled(meta.Name, meta.Category))
                    continue;

                // 根据可见性和作用域状态决定是否包含
                switch (meta.Visibility)
                {
                    case ToolVisibility.AlwaysVisible:
                        result.Add(meta);
                        break;

                    case ToolVisibility.OnDemand:
                        if (!scopingEnabled || (scopeState != null && scopeState.IsCategoryActivated(meta.Category)))
                            result.Add(meta);
                        break;

                    case ToolVisibility.Restricted:
                        // Restricted 工具只在 scoping 启用且分类被激活时暴露
                        // 如果 scoping 关闭，Restricted 工具也不自动暴露（保持安全默认）
                        if (scopeState != null && scopeState.IsCategoryActivated(meta.Category))
                            result.Add(meta);
                        break;

                    default:
                        result.Add(meta);
                        break;
                }
            }

            return result;
        }

        /// <summary>
        /// 获取所有可被激活的 OnDemand 分类及其工具计数（供 request_tools 元工具使用）。
        /// </summary>
        /// <param name="scopeState">当前作用域状态（用于标记已激活分类）</param>
        /// <returns>分类信息列表</returns>
        public static List<CategoryInfo> GetAvailableCategories(ToolScopeState scopeState)
        {
            var settings = AgentCoreSettings.instance;
            var allTools = ToolRegistry.Instance.GetAllTools();

            // 收集 OnDemand 和 Restricted 分类
            var categoryMap = new Dictionary<string, CategoryInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (var tool in allTools)
            {
                var meta = tool.Metadata;
                if (meta == null) continue;
                if (settings.IsToolDisabled(meta.Name, meta.Category)) continue;

                if (meta.Visibility == ToolVisibility.OnDemand || meta.Visibility == ToolVisibility.Restricted)
                {
                    if (!categoryMap.TryGetValue(meta.Category, out var info))
                    {
                        info = new CategoryInfo
                        {
                            Category = meta.Category,
                            Visibility = meta.Visibility,
                            ToolCount = 0,
                            ToolNames = new List<string>(),
                            IsActivated = scopeState != null && scopeState.IsCategoryActivated(meta.Category)
                        };
                        categoryMap[meta.Category] = info;
                    }
                    info.ToolCount++;
                    info.ToolNames.Add(meta.Name);
                }
            }

            return categoryMap.Values.OrderBy(c => c.Category).ToList();
        }

        /// <summary>
        /// OnDemand/Restricted 分类信息。
        /// </summary>
        public class CategoryInfo
        {
            /// <summary>分类名称</summary>
            public string Category { get; set; }

            /// <summary>该分类的可见性级别</summary>
            public ToolVisibility Visibility { get; set; }

            /// <summary>该分类下的可用工具数量</summary>
            public int ToolCount { get; set; }

            /// <summary>该分类下的工具名称列表</summary>
            public List<string> ToolNames { get; set; }

            /// <summary>该分类是否已被当前会话激活</summary>
            public bool IsActivated { get; set; }
        }
    }
}
