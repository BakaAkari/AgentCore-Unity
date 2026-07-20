using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Safety;
using Newtonsoft.Json.Linq;
using UnityEngine;
using AgentCore.Editor.Utils;

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
                if (_initialized)
                {
                    AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] ToolAutoDiscovery: Already initialized, skipping redundant discovery.");
                    return;
                }

                AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] ToolAutoDiscovery: Rebuilding tool registry...");

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
                                AgentCoreLog.Error($"[AgentCore] Failed to register tool '{attr.Name}' ({type.FullName}): {ex.Message}");
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

                AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] ToolAutoDiscovery: Registered {registered} tools ({errors} errors).");
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

            // G.1 治理层：把 [AgentTool] 上声明的风险字段透传到 ToolMetadata。
            // 工具类的 Metadata 属性通常用旧构造创建（风险字段为默认值），
            // 这里用 RiskEnrichedTool 装饰器覆盖 Metadata，确保 ToolRegistry 中的元数据反映 Attribute 声明。
            var enriched = new RiskEnrichedTool(tool, attr);

            // 注册到 ToolRegistry
            ToolRegistry.Instance.Register(enriched);

            AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] Registered native tool: {attr.Name} [{attr.Category}] risk={attr.RiskLevel}");
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

        /// <summary>
        /// G.1 治理层 — IAgentTool 装饰器：用 [AgentTool] 特性上声明的风险字段
        /// 覆盖工具自身 Metadata 中的风险字段，<b>不修改</b>工具本身的执行逻辑。
        /// <para>
        /// 这样 51 个现有工具无需任何改动即可继承 Attribute 上的风险声明，
        /// 同时为未来在工具类内部主动构造完整 ToolMetadata 留出迁移路径。
        /// </para>
        /// </summary>
        private sealed class RiskEnrichedTool : IAgentTool
        {
            private readonly IAgentTool _inner;
            private readonly ToolMetadata _metadata;

            public RiskEnrichedTool(IAgentTool inner, AgentToolAttribute attr)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                if (attr == null) throw new ArgumentNullException(nameof(attr));

                var baseMeta = inner.Metadata
                    ?? throw new InvalidOperationException(
                        $"Tool '{inner.GetType().FullName}' returned null Metadata.");

                _metadata = baseMeta.WithRiskAndVisibility(
                    attr.RiskLevel,
                    attr.Capabilities,
                    attr.RequiresConfirmation,
                    attr.Visibility);
            }

            public ToolMetadata Metadata => _metadata;

            public Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
            {
                return _inner.ExecuteAsync(parameters, cancellationToken);
            }
        }
    }
}
