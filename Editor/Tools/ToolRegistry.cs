using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Tools
{
    /// <summary>
    /// 工具注册表 — 管理所有可用的 Agent 工具。
    /// <para>
    /// 作为工具系统的中心注册表，负责：
    /// <list type="bullet">
    ///   <item>注册/注销工具（<see cref="IAgentTool"/> 实现）</item>
    ///   <item>按名称、分类查询工具</item>
    ///   <item>工具变更事件通知（供 UI 层响应）</item>
    /// </list>
    /// </para>
    /// <para>
    /// 线程安全：所有公开方法均通过 lock 保证线程安全。
    /// </para>
    /// <para>
    /// Phase 2.5: 所有原生工具通过 <see cref="Infrastructure.ToolAutoDiscovery"/> 自动发现并注册到此注册表。
    /// <see cref="ToolCallDispatcher"/> 和 <see cref="ToolDefinitionBuilder"/> 均从此注册表获取工具。
    /// </para>
    /// </summary>
    public class ToolRegistry
    {
        #region 常量

        /// <summary>日志前缀</summary>
        private const string LogPrefix = "[AgentCore] ToolRegistry: ";

        #endregion

        #region 单例

        /// <summary>
        /// 全局唯一的工具注册表实例。
        /// </summary>
        public static ToolRegistry Instance { get; } = new ToolRegistry();

        /// <summary>
        /// 私有构造函数，防止外部实例化。
        /// </summary>
        private ToolRegistry() { }

        #endregion

        #region 私有字段

        /// <summary>已注册的工具字典（key: 工具名称）</summary>
        private readonly Dictionary<string, IAgentTool> _tools = new();

        /// <summary>线程安全锁</summary>
        private readonly object _lock = new();

        #endregion

        #region 事件

        /// <summary>
        /// 工具注册事件。当新工具被注册时触发。
        /// </summary>
        public event Action<IAgentTool> OnToolRegistered;

        /// <summary>
        /// 工具注销事件。当工具被移除时触发（参数为工具名称）。
        /// </summary>
        public event Action<string> OnToolUnregistered;

        #endregion

        #region 属性

        /// <summary>
        /// 已注册工具数量。
        /// </summary>
        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _tools.Count;
                }
            }
        }

        #endregion

        #region 注册 / 注销

        /// <summary>
        /// 注册一个工具。如果同名工具已存在，将覆盖并发出警告。
        /// </summary>
        /// <param name="tool">要注册的工具实例</param>
        /// <exception cref="ArgumentNullException">tool 为 null 时抛出</exception>
        /// <exception cref="ArgumentException">工具名称为空时抛出</exception>
        public void Register(IAgentTool tool)
        {
            if (tool == null)
                throw new ArgumentNullException(nameof(tool));

            if (tool.Metadata == null)
                throw new ArgumentException("Tool.Metadata cannot be null", nameof(tool));

            var name = tool.Metadata.Name;
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Tool name cannot be null or empty", nameof(tool));

            lock (_lock)
            {
                if (_tools.ContainsKey(name))
                {
                    AgentCoreLog.Warning($"{LogPrefix}Overwriting existing tool '{name}'");
                }

                _tools[name] = tool;
            }

            AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Registered tool '{name}' (category: {tool.Metadata.Category})");

            // 事件在锁外触发，避免死锁
            OnToolRegistered?.Invoke(tool);
        }

        /// <summary>
        /// 批量注册工具。
        /// </summary>
        /// <param name="tools">要注册的工具集合</param>
        /// <exception cref="ArgumentNullException">tools 为 null 时抛出</exception>
        public void RegisterRange(IEnumerable<IAgentTool> tools)
        {
            if (tools == null)
                throw new ArgumentNullException(nameof(tools));

            foreach (var tool in tools)
            {
                Register(tool);
            }
        }

        /// <summary>
        /// 注销指定名称的工具。
        /// </summary>
        /// <param name="toolName">要注销的工具名称</param>
        /// <returns>是否成功注销（工具不存在时返回 false）</returns>
        public bool Unregister(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName))
                return false;

            bool removed;
            lock (_lock)
            {
                removed = _tools.Remove(toolName);
            }

            if (removed)
            {
                AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Unregistered tool '{toolName}'");
                OnToolUnregistered?.Invoke(toolName);
            }
            else
            {
                AgentCoreLog.Warning($"{LogPrefix}Attempted to unregister unknown tool '{toolName}'");
            }

            return removed;
        }

        #endregion

        #region 查询

        /// <summary>
        /// 根据名称获取工具。
        /// </summary>
        /// <param name="name">工具名称</param>
        /// <returns>工具实例，未找到时返回 null</returns>
        public IAgentTool GetTool(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            lock (_lock)
            {
                _tools.TryGetValue(name, out var tool);
                return tool;
            }
        }

        /// <summary>
        /// 检查是否存在指定名称的工具。
        /// </summary>
        /// <param name="name">工具名称</param>
        /// <returns>工具是否已注册</returns>
        public bool HasTool(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            lock (_lock)
            {
                return _tools.ContainsKey(name);
            }
        }

        /// <summary>
        /// 获取所有已注册工具的只读列表。
        /// </summary>
        /// <returns>所有工具的快照列表</returns>
        public IReadOnlyList<IAgentTool> GetAllTools()
        {
            lock (_lock)
            {
                return _tools.Values.ToList().AsReadOnly();
            }
        }

        /// <summary>
        /// 获取所有已注册工具的名称列表。
        /// </summary>
        /// <returns>工具名称的快照列表</returns>
        public IReadOnlyList<string> GetAllToolNames()
        {
            lock (_lock)
            {
                return _tools.Keys.ToList().AsReadOnly();
            }
        }

        /// <summary>
        /// 按分类获取工具列表。
        /// </summary>
        /// <param name="category">分类名称（大小写敏感）</param>
        /// <returns>属于指定分类的工具快照列表</returns>
        public IReadOnlyList<IAgentTool> GetToolsByCategory(string category)
        {
            if (string.IsNullOrWhiteSpace(category))
                return new List<IAgentTool>().AsReadOnly();

            lock (_lock)
            {
                return _tools.Values
                    .Where(t => string.Equals(t.Metadata.Category, category, StringComparison.Ordinal))
                    .ToList()
                    .AsReadOnly();
            }
        }

        /// <summary>
        /// 获取所有已注册工具的分类列表（去重）。
        /// </summary>
        /// <returns>分类名称的快照列表</returns>
        public IReadOnlyList<string> GetCategories()
        {
            lock (_lock)
            {
                return _tools.Values
                    .Select(t => t.Metadata.Category)
                    .Distinct()
                    .OrderBy(c => c)
                    .ToList()
                    .AsReadOnly();
            }
        }

        #endregion

        #region 工具信息导出

        /// <summary>
        /// 获取所有已注册工具的元数据列表。
        /// 用于生成 TOOLS.md 或构建 ToolDefinition。
        /// </summary>
        /// <returns>所有工具元数据的快照列表</returns>
        public IReadOnlyList<ToolMetadata> GetAllToolMetadata()
        {
            lock (_lock)
            {
                return _tools.Values
                    .Select(t => t.Metadata)
                    .ToList()
                    .AsReadOnly();
            }
        }

        #endregion

        #region 管理

        /// <summary>
        /// 清空所有已注册工具。主要用于测试场景。
        /// </summary>
        public void Clear()
        {
            List<string> removedNames;

            lock (_lock)
            {
                removedNames = _tools.Keys.ToList();
                _tools.Clear();
            }

            AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Cleared all tools ({removedNames.Count} removed)");

            // 逐个触发注销事件
            foreach (var name in removedNames)
            {
                OnToolUnregistered?.Invoke(name);
            }
        }

        #endregion
    }
}
