using System;
using System.Reflection;
using UnityEngine;

namespace AgentCore.Editor.Tools.Infrastructure
{
    /// <summary>
    /// 自动发现并注册所有带 [AgentTool] 属性的原生工具类。
    /// </summary>
    public static class ToolAutoDiscovery
    {
        private static bool _initialized;
        private static readonly object _lock = new object();

        /// <summary>
        /// 已发现的工具数量
        /// </summary>
        public static int DiscoveredCount { get; private set; }

        /// <summary>
        /// 扫描所有程序集，发现并注册当前已编译进域内的工具。
        /// </summary>
        /// <remarks>
        /// 发现过程会先重建 <see cref="ToolRegistry"/>，避免可选组件被禁用或重新编译后留下旧工具实例。
        /// </remarks>
        public static void DiscoverAndRegisterAll()
        {
            lock (_lock)
            {
                Debug.Log("[AgentCore] ToolAutoDiscovery: Rebuilding tool registry...");

                ToolRegistry.Instance.Clear();

                int registered = 0;
                int errors = 0;

                // 扫描所有程序集中带 [AgentTool] 属性的类
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    // 跳过系统程序集
                    var assemblyName = assembly.GetName().Name;
                    if (assemblyName.StartsWith("System") ||
                        assemblyName.StartsWith("mscorlib") ||
                        assemblyName.StartsWith("Unity.") ||
                        assemblyName.StartsWith("UnityEngine.") ||
                        assemblyName.StartsWith("UnityEditor."))
                        continue;

                    try
                    {
                        foreach (var type in assembly.GetTypes())
                        {
                            var attr = type.GetCustomAttribute<AgentToolAttribute>();
                            if (attr == null) continue;

                            try
                            {
                                RegisterToolType(type, attr);
                                registered++;
                            }
                            catch (Exception ex)
                            {
                                Debug.LogError($"[AgentCore] Failed to register tool '{attr.Name}' ({type.FullName}): {ex.Message}");
                                errors++;
                            }
                        }
                    }
                    catch (ReflectionTypeLoadException)
                    {
                        // 某些程序集可能无法加载类型，跳过
                    }
                }

                DiscoveredCount = registered;
                _initialized = true;

                Debug.Log($"[AgentCore] ToolAutoDiscovery: Registered {registered} tools ({errors} errors).");
            }
        }

        /// <summary>
        /// 注册单个工具类型
        /// </summary>
        private static void RegisterToolType(Type type, AgentToolAttribute attr)
        {
            // 验证类型实现了 IAgentTool
            if (!typeof(IAgentTool).IsAssignableFrom(type))
            {
                throw new InvalidOperationException(
                    $"Type '{type.FullName}' has [AgentTool] attribute but does not implement IAgentTool.");
            }

            // 验证有无参数构造函数
            if (type.GetConstructor(Type.EmptyTypes) == null)
            {
                throw new InvalidOperationException(
                    $"Type '{type.FullName}' must have a parameterless constructor.");
            }

            // 创建实例
            var tool = (IAgentTool)Activator.CreateInstance(type);

            // 注册到 ToolRegistry
            ToolRegistry.Instance.Register(tool);

            Debug.Log($"[AgentCore] Registered native tool: {attr.Name} [{attr.Category}]");
        }

        /// <summary>
        /// 重置发现状态并清空工具注册表（用于测试或重新加载）。
        /// </summary>
        public static void Reset()
        {
            lock (_lock)
            {
                _initialized = false;
                DiscoveredCount = 0;
                ToolRegistry.Instance.Clear();
            }
        }
    }
}
