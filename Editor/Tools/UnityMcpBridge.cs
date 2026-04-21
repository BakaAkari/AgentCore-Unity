using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace AgentCore.Editor.Tools
{
    /// <summary>
    /// Unity MCP 桥接器 — 连接 AgentCore 工具系统与 unity-mcp CommandRegistry。
    /// <para>
    /// 职责：
    /// <list type="number">
    ///   <item>通过 <see cref="IToolDiscoveryService"/> 发现 unity-mcp 已注册的所有工具命令</item>
    ///   <item>将每个 MCP 命令包装为 <see cref="IAgentTool"/> 实现（<see cref="McpToolWrapper"/>）</item>
    ///   <item>通过 <see cref="CommandRegistry.InvokeCommandAsync"/> 执行工具调用</item>
    ///   <item>将所有 MCP 工具批量注册到 <see cref="ToolRegistry"/></item>
    /// </list>
    /// </para>
    /// <para>
    /// 使用方式：在 AgentLoop 初始化时调用 <c>UnityMcpBridge.Instance.Initialize()</c>。
    /// </para>
    /// </summary>
    public class UnityMcpBridge
    {
        #region 常量

        /// <summary>日志前缀</summary>
        private const string LogPrefix = "[AgentCore] UnityMcpBridge: ";

        /// <summary>MCP 工具分类名称</summary>
        private const string McpCategory = "unity-mcp";

        /// <summary>
        /// 始终视为脚本修改的命令名称集合（无需检查 action 参数）。
        /// 匹配这些命令时，<see cref="ToolResult.IsCompileRelated"/> 将被设置为 true，
        /// 以触发自动编译检查流程。
        /// </summary>
        private static readonly HashSet<string> AlwaysCompileRelatedCommands = new(StringComparer.OrdinalIgnoreCase)
        {
            "create_script",
            "delete_script",
            "execute_code",
            "script_apply_edits",
            "apply_text_edits"
        };

        /// <summary>
        /// 需要根据 action 参数判断是否涉及脚本修改的命令。
        /// Key: 命令名称, Value: 会触发编译的 action 集合。
        /// </summary>
        private static readonly Dictionary<string, HashSet<string>> ConditionalCompileCommands = new(StringComparer.OrdinalIgnoreCase)
        {
            {
                "manage_script", new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "create", "write", "delete"
                }
            }
        };

        #endregion

        #region 单例

        private static UnityMcpBridge _instance;

        /// <summary>
        /// 全局唯一的桥接器实例。
        /// </summary>
        public static UnityMcpBridge Instance => _instance ??= new UnityMcpBridge();

        /// <summary>
        /// 私有构造函数，防止外部实例化。
        /// </summary>
        private UnityMcpBridge() { }

        #endregion

        #region 私有字段

        /// <summary>是否已完成初始化</summary>
        private bool _isInitialized;

        /// <summary>已包装的 MCP 工具列表</summary>
        private readonly List<McpToolWrapper> _mcpTools = new();

        /// <summary>线程安全锁</summary>
        private readonly object _lock = new();

        /// <summary>
        /// 静态 schema 映射表缓存。
        /// 从 <see cref="McpToolSchemas.GetToolSchemas()"/> 加载，
        /// 用于在 ToolDiscovery 返回空 schema 时提供正确的参数定义。
        /// </summary>
        private static Dictionary<string, string> _schemaCache;

        #endregion

        #region 属性

        /// <summary>
        /// 已发现并注册的 MCP 工具数量。
        /// </summary>
        public int ToolCount
        {
            get
            {
                lock (_lock)
                {
                    return _mcpTools.Count;
                }
            }
        }

        /// <summary>
        /// 桥接器是否已初始化。
        /// </summary>
        public bool IsInitialized
        {
            get
            {
                lock (_lock)
                {
                    return _isInitialized;
                }
            }
        }

        #endregion

        #region 初始化

        /// <summary>
        /// 初始化桥接器 — 发现并注册所有 MCP 工具到 <see cref="ToolRegistry"/>。
        /// <para>
        /// 流程：
        /// <list type="number">
        ///   <item>确保 <see cref="CommandRegistry"/> 已初始化</item>
        ///   <item>通过 <see cref="IToolDiscoveryService"/> 发现所有可用工具</item>
        ///   <item>为每个工具创建 <see cref="McpToolWrapper"/> 包装器</item>
        ///   <item>批量注册到 <see cref="ToolRegistry"/></item>
        /// </list>
        /// </para>
        /// <para>重复调用是安全的（幂等），第二次调用会直接返回。</para>
        /// </summary>
        public void Initialize()
        {
            lock (_lock)
            {
                if (_isInitialized) return;
            }

            try
            {
                // 1. 确保 CommandRegistry 已初始化（注册所有 handler）
                EnsureCommandRegistryInitialized();

                // 2. 通过 IToolDiscoveryService 发现所有工具
                var discoveredTools = DiscoverMcpTools();
                if (discoveredTools == null || discoveredTools.Count == 0)
                {
                    Debug.LogWarning($"{LogPrefix}No MCP tools discovered. Bridge initialized with 0 tools.");
                    lock (_lock) { _isInitialized = true; }
                    return;
                }

                // 3. 为每个工具创建 McpToolWrapper
                var wrappers = new List<McpToolWrapper>();
                foreach (var mcpMeta in discoveredTools)
                {
                    if (string.IsNullOrWhiteSpace(mcpMeta.Name))
                    {
                        Debug.LogWarning($"{LogPrefix}Skipping tool with empty name (class: {mcpMeta.ClassName})");
                        continue;
                    }

                    var metadata = ConvertToAgentMetadata(mcpMeta);
                    var wrapper = new McpToolWrapper(metadata, this);
                    wrappers.Add(wrapper);
                }

                // 4. 批量注册到 ToolRegistry
                lock (_lock)
                {
                    _mcpTools.Clear();
                    _mcpTools.AddRange(wrappers);
                }

                ToolRegistry.Instance.RegisterRange(wrappers);

                lock (_lock) { _isInitialized = true; }

                Debug.Log($"{LogPrefix}Initialized — registered {wrappers.Count} MCP tools to ToolRegistry");
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogPrefix}Initialization failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// 刷新工具列表 — 重新发现并注册所有 MCP 工具。
        /// <para>
        /// 会先注销所有已注册的 MCP 工具，然后重新发现并注册。
        /// 适用于 unity-mcp 工具集发生变化后的场景。
        /// </para>
        /// </summary>
        public void Refresh()
        {
            Debug.Log($"{LogPrefix}Refreshing MCP tools...");

            // 注销已有的 MCP 工具
            UnregisterAllMcpTools();

            lock (_lock)
            {
                _isInitialized = false;
                _mcpTools.Clear();
            }

            // 重新初始化
            Initialize();
        }

        #endregion

        #region 工具执行

        /// <summary>
        /// 执行 MCP 工具命令，返回统一的 <see cref="ToolResult"/>。
        /// <para>
        /// 内部调用 <see cref="CommandRegistry.InvokeCommandAsync"/>，
        /// 并将 unity-mcp 的响应格式（<see cref="SuccessResponse"/>、
        /// <see cref="ErrorResponse"/>、<see cref="PendingResponse"/>）
        /// 统一转换为 <see cref="ToolResult"/>。
        /// </para>
        /// </summary>
        /// <param name="commandName">MCP 命令名称</param>
        /// <param name="parameters">命令参数（JSON 对象）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>工具执行结果</returns>
        public async Task<ToolResult> ExecuteToolAsync(
            string commandName,
            JObject parameters,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(commandName))
            {
                return ToolResult.Fail("Command name cannot be null or empty");
            }

            var stopwatch = Stopwatch.StartNew();

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 调用 CommandRegistry.InvokeCommandAsync
                var rawResult = await CommandRegistry.InvokeCommandAsync(
                    commandName,
                    parameters ?? new JObject()
                );

                stopwatch.Stop();

                // 解析响应
                var result = ParseResponse(rawResult, commandName, stopwatch.Elapsed.TotalMilliseconds);

                // 标记脚本修改相关命令（需要传入 parameters 以精确判断 action）
                if (IsScriptModifyingCommand(commandName, parameters))
                {
                    result.IsCompileRelated = true;
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                return ToolResult.Fail(
                    $"Command '{commandName}' was cancelled",
                    stopwatch.Elapsed.TotalMilliseconds
                );
            }
            catch (InvalidOperationException ex)
            {
                // CommandRegistry 抛出：未知命令
                stopwatch.Stop();
                Debug.LogWarning($"{LogPrefix}Command '{commandName}' failed: {ex.Message}");
                return ToolResult.Fail(ex.Message, stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                Debug.LogError($"{LogPrefix}Command '{commandName}' threw exception: {ex.Message}\n{ex.StackTrace}");
                return ToolResult.Fail(
                    $"Exception executing '{commandName}': {ex.Message}",
                    stopwatch.Elapsed.TotalMilliseconds
                );
            }
        }

        #endregion

        #region 响应解析

        /// <summary>
        /// 解析 unity-mcp 的原始响应为统一的 <see cref="ToolResult"/>。
        /// <para>
        /// 支持以下响应类型：
        /// <list type="bullet">
        ///   <item><see cref="SuccessResponse"/> — 成功响应，提取 Message 和 Data</item>
        ///   <item><see cref="ErrorResponse"/> — 错误响应，提取 Error 信息</item>
        ///   <item><see cref="PendingResponse"/> — 异步轮询响应（Phase 2 暂不处理轮询）</item>
        ///   <item>其他 object — 序列化为 JSON 字符串</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <param name="rawResult">CommandRegistry 返回的原始结果</param>
        /// <param name="commandName">命令名称（用于日志）</param>
        /// <param name="executionTimeMs">执行耗时（毫秒）</param>
        /// <returns>统一的工具执行结果</returns>
        internal ToolResult ParseResponse(object rawResult, string commandName, double executionTimeMs)
        {
            // null 结果视为成功（某些命令无返回值）
            if (rawResult == null)
            {
                return ToolResult.Ok(
                    $"Command '{commandName}' executed successfully (no output)",
                    executionTimeMs
                );
            }

            // SuccessResponse
            if (rawResult is SuccessResponse successResponse)
            {
                var output = FormatSuccessResponse(successResponse);
                return ToolResult.Ok(output, executionTimeMs);
            }

            // ErrorResponse
            if (rawResult is ErrorResponse errorResponse)
            {
                var errorMsg = !string.IsNullOrEmpty(errorResponse.Error)
                    ? errorResponse.Error
                    : "Unknown error from MCP command";
                return ToolResult.Fail(errorMsg, executionTimeMs);
            }

            // PendingResponse — Phase 2 暂不处理轮询，返回 pending 状态信息
            if (rawResult is PendingResponse pendingResponse)
            {
                var pendingMsg = FormatPendingResponse(pendingResponse, commandName);
                return ToolResult.Ok(pendingMsg, executionTimeMs);
            }

            // IMcpResponse 接口兜底（处理未来可能新增的响应类型）
            if (rawResult is IMcpResponse mcpResponse)
            {
                if (mcpResponse.Success)
                {
                    return ToolResult.Ok(
                        SerializeToJson(rawResult),
                        executionTimeMs
                    );
                }
                else
                {
                    return ToolResult.Fail(
                        SerializeToJson(rawResult),
                        executionTimeMs
                    );
                }
            }

            // 原始 object — 尝试序列化为 JSON
            var serialized = SerializeToJson(rawResult);
            return ToolResult.Ok(serialized, executionTimeMs);
        }

        #endregion

        #region 工具发现（内部方法）

        /// <summary>
        /// 确保 CommandRegistry 已初始化。
        /// </summary>
        private void EnsureCommandRegistryInitialized()
        {
            try
            {
                CommandRegistry.Initialize();
                Debug.Log($"{LogPrefix}CommandRegistry initialized");
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogPrefix}Failed to initialize CommandRegistry: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 通过 <see cref="IToolDiscoveryService"/> 发现所有可用的 MCP 工具。
        /// <para>
        /// 优先使用 <see cref="MCPServiceLocator.ToolDiscovery"/> 获取工具列表。
        /// 如果 ToolDiscoveryService 不可用，则回退到反射方式直接读取
        /// <see cref="CommandRegistry"/> 的内部 <c>_handlers</c> 字典。
        /// </para>
        /// </summary>
        /// <returns>发现的工具元数据列表</returns>
        private List<MCPForUnity.Editor.Services.ToolMetadata> DiscoverMcpTools()
        {
            // 优先方案：通过 IToolDiscoveryService
            try
            {
                var discoveryService = MCPServiceLocator.ToolDiscovery;
                if (discoveryService != null)
                {
                    var tools = discoveryService.DiscoverAllTools();
                    if (tools != null && tools.Count > 0)
                    {
                        Debug.Log($"{LogPrefix}Discovered {tools.Count} tools via IToolDiscoveryService");
                        return tools;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LogPrefix}IToolDiscoveryService failed, falling back to reflection: {ex.Message}");
            }

            // 回退方案：通过反射读取 CommandRegistry._handlers
            return DiscoverToolsViaReflection();
        }

        /// <summary>
        /// 通过反射读取 <see cref="CommandRegistry"/> 内部的 <c>_handlers</c> 字典，
        /// 构建工具元数据列表。
        /// <para>
        /// 这是当 <see cref="IToolDiscoveryService"/> 不可用时的回退方案。
        /// <c>_handlers</c> 是 <c>private static Dictionary&lt;string, HandlerInfo&gt;</c>，
        /// <c>HandlerInfo</c> 是 <c>internal class</c>，需要通过反射访问。
        /// </para>
        /// </summary>
        /// <returns>从反射中构建的工具元数据列表，失败时返回空列表</returns>
        private List<MCPForUnity.Editor.Services.ToolMetadata> DiscoverToolsViaReflection()
        {
            var result = new List<MCPForUnity.Editor.Services.ToolMetadata>();

            try
            {
                // 获取 _handlers 字段
                var handlersField = typeof(CommandRegistry).GetField(
                    "_handlers",
                    BindingFlags.NonPublic | BindingFlags.Static
                );

                if (handlersField == null)
                {
                    Debug.LogError($"{LogPrefix}Reflection fallback failed: Cannot find '_handlers' field on CommandRegistry");
                    return result;
                }

                var handlersValue = handlersField.GetValue(null);
                if (handlersValue == null)
                {
                    Debug.LogWarning($"{LogPrefix}Reflection fallback: '_handlers' is null");
                    return result;
                }

                // _handlers 是 Dictionary<string, HandlerInfo>，HandlerInfo 是 internal
                // 通过 IDictionary 接口遍历
                if (handlersValue is not IDictionary handlers)
                {
                    Debug.LogError($"{LogPrefix}Reflection fallback: '_handlers' is not IDictionary (type: {handlersValue.GetType().FullName})");
                    return result;
                }

                // 获取 HandlerInfo 类型信息（用于读取 CommandName 属性）
                Type handlerInfoType = null;
                PropertyInfo commandNameProp = null;

                foreach (DictionaryEntry entry in handlers)
                {
                    var key = entry.Key as string;
                    if (string.IsNullOrWhiteSpace(key)) continue;

                    // 延迟获取 HandlerInfo 类型信息
                    if (handlerInfoType == null && entry.Value != null)
                    {
                        handlerInfoType = entry.Value.GetType();
                        commandNameProp = handlerInfoType.GetProperty("CommandName",
                            BindingFlags.Public | BindingFlags.Instance);
                    }

                    // 构建最小化的 ToolMetadata
                    var toolMeta = new MCPForUnity.Editor.Services.ToolMetadata
                    {
                        Name = key,
                        Description = $"Unity MCP tool: {key}",
                        Parameters = new List<ParameterMetadata>()
                    };

                    result.Add(toolMeta);
                }

                Debug.Log($"{LogPrefix}Discovered {result.Count} tools via reflection fallback");
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogPrefix}Reflection fallback failed: {ex.Message}\n{ex.StackTrace}");
            }

            return result;
        }

        #endregion

        #region 元数据转换

        /// <summary>
        /// 将 unity-mcp 的 <see cref="MCPForUnity.Editor.Services.ToolMetadata"/>
        /// 转换为 AgentCore 的 <see cref="ToolMetadata"/>。
        /// <para>
        /// 当 ToolDiscovery 返回的参数列表为空时（unity-mcp v9.5.3 的已知问题），
        /// 会从 <see cref="McpToolSchemas"/> 静态映射表中查找正确的参数 schema。
        /// </para>
        /// </summary>
        /// <param name="mcpMeta">unity-mcp 工具元数据</param>
        /// <returns>AgentCore 工具元数据</returns>
        private ToolMetadata ConvertToAgentMetadata(MCPForUnity.Editor.Services.ToolMetadata mcpMeta)
        {
            // 先尝试从 ToolDiscovery 获取的参数构建 schema
            var parametersSchema = BuildParametersSchema(mcpMeta.Parameters);

            // 如果 ToolDiscovery 返回的 schema 为空（没有 properties），
            // 则从静态映射表中查找
            if (IsSchemaEmpty(parametersSchema))
            {
                var staticSchema = GetStaticSchema(mcpMeta.Name);
                if (staticSchema != null)
                {
                    parametersSchema = staticSchema;
                }
            }

            return new ToolMetadata(
                name: mcpMeta.Name,
                description: mcpMeta.Description ?? $"Unity MCP tool: {mcpMeta.Name}",
                category: McpCategory,
                parametersSchema: parametersSchema,
                requiresMainThread: true
            );
        }

        /// <summary>
        /// 判断参数 schema 是否为空（没有定义任何属性）。
        /// </summary>
        /// <param name="schema">JSON Schema 对象</param>
        /// <returns>如果 schema 没有定义任何 properties 则返回 true</returns>
        private static bool IsSchemaEmpty(JObject schema)
        {
            if (schema == null) return true;

            var properties = schema["properties"] as JObject;
            return properties == null || !properties.HasValues;
        }

        /// <summary>
        /// 从 <see cref="McpToolSchemas"/> 静态映射表中获取指定工具的参数 schema。
        /// <para>
        /// 使用延迟初始化的缓存，避免每次调用都重新构建映射表。
        /// 如果工具不在映射表中，返回宽松的 fallback schema。
        /// </para>
        /// </summary>
        /// <param name="toolName">工具名称</param>
        /// <returns>解析后的 JSON Schema 对象，解析失败时返回 null</returns>
        private static JObject GetStaticSchema(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName))
                return null;

            try
            {
                // 延迟初始化 schema 缓存
                if (_schemaCache == null)
                {
                    _schemaCache = McpToolSchemas.GetToolSchemas();
                    Debug.Log($"{LogPrefix}Loaded {_schemaCache.Count} static tool schemas from McpToolSchemas");
                }

                // 查找工具 schema
                string schemaJson;
                if (!_schemaCache.TryGetValue(toolName, out schemaJson))
                {
                    // 工具不在映射表中，使用 fallback schema
                    schemaJson = McpToolSchemas.FallbackSchema;
                    Debug.Log($"{LogPrefix}Tool '{toolName}' not in static schema map, using fallback schema");
                }

                return JObject.Parse(schemaJson);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LogPrefix}Failed to parse static schema for tool '{toolName}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 从 unity-mcp 的 <see cref="ParameterMetadata"/> 列表构建 JSON Schema 格式的参数描述。
        /// <para>
        /// 生成格式：
        /// <code>
        /// {
        ///   "type": "object",
        ///   "properties": {
        ///     "paramName": { "type": "string", "description": "..." }
        ///   },
        ///   "required": ["requiredParam1"]
        /// }
        /// </code>
        /// </para>
        /// </summary>
        /// <param name="parameters">unity-mcp 参数元数据列表</param>
        /// <returns>JSON Schema 对象</returns>
        private JObject BuildParametersSchema(List<ParameterMetadata> parameters)
        {
            var schema = new JObject
            {
                ["type"] = "object"
            };

            if (parameters == null || parameters.Count == 0)
            {
                schema["properties"] = new JObject();
                return schema;
            }

            var properties = new JObject();
            var required = new JArray();

            foreach (var param in parameters)
            {
                if (string.IsNullOrWhiteSpace(param.Name)) continue;

                var propSchema = new JObject
                {
                    ["type"] = MapParameterType(param.Type)
                };

                if (!string.IsNullOrEmpty(param.Description))
                {
                    propSchema["description"] = param.Description;
                }

                if (!string.IsNullOrEmpty(param.DefaultValue))
                {
                    propSchema["default"] = param.DefaultValue;
                }

                properties[param.Name] = propSchema;

                if (param.Required)
                {
                    required.Add(param.Name);
                }
            }

            schema["properties"] = properties;

            if (required.Count > 0)
            {
                schema["required"] = required;
            }

            return schema;
        }

        /// <summary>
        /// 将 unity-mcp 的参数类型字符串映射为 JSON Schema 类型。
        /// </summary>
        /// <param name="mcpType">unity-mcp 参数类型（如 "string"、"int"、"bool"）</param>
        /// <returns>JSON Schema 类型字符串</returns>
        private static string MapParameterType(string mcpType)
        {
            if (string.IsNullOrEmpty(mcpType))
                return "string";

            return mcpType.ToLowerInvariant() switch
            {
                "string" => "string",
                "integer" or "int" => "integer",
                "number" or "float" or "double" or "decimal" => "number",
                "boolean" or "bool" => "boolean",
                "object" or "json" or "jobject" => "object",
                "array" or "list" => "array",
                _ => "string"
            };
        }

        #endregion

        #region 辅助方法

        /// <summary>
        /// 判断命令是否涉及脚本修改。
        /// <para>
        /// 对于始终修改脚本的命令（如 create_script、delete_script 等），直接返回 true。
        /// 对于需要根据 action 参数判断的命令（如 manage_script），
        /// 只有 write/create/delete 等修改操作才返回 true，read/list 等只读操作返回 false。
        /// </para>
        /// </summary>
        /// <param name="commandName">命令名称</param>
        /// <param name="parameters">命令参数（用于检查 action 字段）</param>
        /// <returns>是否涉及脚本修改</returns>
        public static bool IsScriptModifyingCommand(string commandName, JObject parameters = null)
        {
            if (string.IsNullOrWhiteSpace(commandName))
                return false;

            // 始终视为编译相关的命令
            if (AlwaysCompileRelatedCommands.Contains(commandName))
                return true;

            // 需要根据 action 参数判断的命令
            if (ConditionalCompileCommands.TryGetValue(commandName, out var modifyingActions))
            {
                var action = parameters?["action"]?.ToString()?.ToLowerInvariant();
                if (string.IsNullOrEmpty(action))
                {
                    // 无法确定 action 时，保守地视为编译相关
                    Debug.LogWarning($"{LogPrefix}Command '{commandName}' has no 'action' parameter, " +
                                     "conservatively marking as compile-related.");
                    return true;
                }

                return modifyingActions.Contains(action);
            }

            return false;
        }

        /// <summary>
        /// 格式化 <see cref="SuccessResponse"/> 为可读字符串。
        /// </summary>
        private static string FormatSuccessResponse(SuccessResponse response)
        {
            if (response.Data != null)
            {
                return SerializeToJson(response.Data);
            }

            return response.Message ?? "Command executed successfully";
        }

        /// <summary>
        /// 格式化 <see cref="PendingResponse"/> 为可读字符串。
        /// </summary>
        private static string FormatPendingResponse(PendingResponse response, string commandName)
        {
            var msg = response.Message ?? $"Command '{commandName}' is pending";
            return $"[Pending] {msg} (poll interval: {response.PollIntervalSeconds}s)";
        }

        /// <summary>
        /// 将对象安全地序列化为 JSON 字符串。
        /// </summary>
        private static string SerializeToJson(object obj)
        {
            try
            {
                return JsonConvert.SerializeObject(obj, Formatting.Indented);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LogPrefix}JSON serialization failed: {ex.Message}");
                return obj?.ToString() ?? "(null)";
            }
        }

        /// <summary>
        /// 注销所有已注册的 MCP 工具。
        /// </summary>
        private void UnregisterAllMcpTools()
        {
            List<McpToolWrapper> toolsToRemove;
            lock (_lock)
            {
                toolsToRemove = new List<McpToolWrapper>(_mcpTools);
            }

            var registry = ToolRegistry.Instance;
            foreach (var tool in toolsToRemove)
            {
                registry.Unregister(tool.Metadata.Name);
            }

            Debug.Log($"{LogPrefix}Unregistered {toolsToRemove.Count} MCP tools");
        }

        /// <summary>
        /// 获取所有已注册的 MCP 工具名称列表。
        /// </summary>
        /// <returns>MCP 工具名称的只读列表</returns>
        public IReadOnlyList<string> GetRegisteredToolNames()
        {
            lock (_lock)
            {
                return _mcpTools.Select(t => t.Metadata.Name).ToList().AsReadOnly();
            }
        }

        #endregion
    }

    #region McpToolWrapper — MCP 工具包装器

    /// <summary>
    /// MCP 工具包装器 — 将 unity-mcp CommandRegistry 命令包装为 <see cref="IAgentTool"/> 实现。
    /// <para>
    /// 每个实例对应一个 MCP 命令，通过 <see cref="UnityMcpBridge.ExecuteToolAsync"/> 
    /// 委托执行实际的命令调用。
    /// </para>
    /// </summary>
    internal class McpToolWrapper : IAgentTool
    {
        /// <summary>工具元数据</summary>
        public ToolMetadata Metadata { get; }

        /// <summary>桥接器引用，用于委托执行</summary>
        private readonly UnityMcpBridge _bridge;

        /// <summary>
        /// 创建 MCP 工具包装器。
        /// </summary>
        /// <param name="metadata">工具元数据</param>
        /// <param name="bridge">桥接器实例</param>
        /// <exception cref="ArgumentNullException">metadata 或 bridge 为 null 时抛出</exception>
        public McpToolWrapper(ToolMetadata metadata, UnityMcpBridge bridge)
        {
            Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        }

        /// <summary>
        /// 异步执行 MCP 工具命令。
        /// <para>
        /// 委托给 <see cref="UnityMcpBridge.ExecuteToolAsync"/> 执行，
        /// 该方法内部调用 <see cref="CommandRegistry.InvokeCommandAsync"/> 
        /// 并处理响应解析和异常捕获。
        /// </para>
        /// </summary>
        /// <param name="parameters">工具参数（JSON 对象）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>工具执行结果</returns>
        public async Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
        {
            return await _bridge.ExecuteToolAsync(Metadata.Name, parameters, cancellationToken);
        }
    }

    #endregion
}
