using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.LLM;
using AgentCore.Editor.Config;
using AgentCore.Editor.Bootstrap;
using AgentCore.Editor.Cloud;
using AgentCore.Editor.Session;
using AgentCore.Editor.Tools;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Utils;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace AgentCore.Editor.Core
{
    /// <summary>
    /// Agent Loop — 核心对话循环引擎。
    /// <para>
    /// 核心职责：
    /// <list type="bullet">
    ///   <item>管理对话历史（LLM 消息 + UI 轮次）</item>
    ///   <item>接收用户输入 -> 构建请求 -> 调用 LLM -> 流式输出</item>
    ///   <item>工具调用循环：LLM 返回 tool_calls 时自动执行并继续对话</item>
    ///   <item>通过事件回调通知 UI 层状态变更和内容更新</item>
    /// </list>
    /// </para>
    /// <para>
    /// Phase 2 升级：
    /// <list type="bullet">
    ///   <item>从单轮对话升级为 "循环直到最终回答" 的工具调用模式</item>
    ///   <item>集成 <see cref="ToolCallDispatcher"/> 分发执行工具调用</item>
    ///   <item>集成 <see cref="ToolAutoDiscovery"/> 自动发现并注册原生工具</item>
    ///   <item>支持 <see cref="AgentCoreSettings.maxToolCallRounds"/> 防止无限循环</item>
    /// </list>
    /// </para>
    /// </summary>
    public class AgentLoop : IDisposable
    {
        #region 事件

        /// <summary>
        /// Agent 事件回调。
        /// UI 层订阅此事件以接收状态变更、流式 token、完整消息、工具调用和错误通知。
        /// 所有事件均在 Unity 主线程上触发。
        /// </summary>
        public event Action<AgentEvent> OnAgentEvent;

        #endregion

        #region 公开属性

        /// <summary>
        /// 当前 Agent 状态。
        /// </summary>
        public AgentState CurrentState { get; private set; } = AgentState.Idle;

        /// <summary>
        /// 对话轮次历史（只读视图），供 UI 层显示。
        /// </summary>
        public IReadOnlyList<ConversationTurn> ConversationHistory => _conversationTurns;

        /// <summary>
        /// LLM 消息历史（只读视图），供 SessionManager 访问。
        /// </summary>
        public IReadOnlyList<ChatMessage> Messages => _messages;

        /// <summary>
        /// 对话轮次列表（只读视图），供 SessionManager 访问。
        /// </summary>
        public IReadOnlyList<ConversationTurn> ConversationTurns => _conversationTurns;

        /// <summary>
        /// LLM 客户端（只读），供 UI 层在会话切换时触发 AutoMemory 使用。
        /// </summary>
        public ILLMClient LLMClient => _llmClient;

        #endregion

        #region 私有字段

        /// <summary>LLM 客户端（依赖注入）</summary>
        private readonly ILLMClient _llmClient;

        /// <summary>工具调用分发器</summary>
        private readonly ToolCallDispatcher _dispatcher;

        /// <summary>LLM 消息历史（包含 system/user/assistant/tool 消息，发送给 LLM）</summary>
        private readonly List<ChatMessage> _messages = new List<ChatMessage>();

        /// <summary>UI 对话轮次历史（供 UI 显示用）</summary>
        private readonly List<ConversationTurn> _conversationTurns = new List<ConversationTurn>();

        /// <summary>当前操作的取消令牌源</summary>
        private CancellationTokenSource _currentCts;

        /// <summary>是否已完成初始化</summary>
        private bool _isInitialized;

        /// <summary>Console 错误捕获器 - 在工具执行期间捕获 Unity Console 错误</summary>
        private ConsoleErrorCapture _consoleCapture;

        /// <summary>编译监控器 - 监控脚本编译过程并收集编译错误</summary>
        private CompilationWatcher _compilationWatcher;

        /// <summary>降级路由器 - LLM 请求失败时自动重试</summary>
        private FallbackRouter _fallbackRouter;

        /// <summary>默认系统提示词（Bootstrap 加载失败时的兜底方案）</summary>
        private const string DefaultSystemPrompt = "你是一个 Unity 开发助手。请用中文回复用户的问题，帮助他们解决 Unity 开发中遇到的问题。";

        /// <summary>记忆注入消息的前缀标识，用于识别和清理旧的记忆消息</summary>
        private const string MemoryMessagePrefix = "[历史记忆 - 以下是与当前对话可能相关的历史信息]";

        /// <summary>记忆召回超时时间（秒）</summary>
        private const int MemoryRecallTimeoutSeconds = 5;

        /// <summary>记忆召回最大返回条数</summary>
        private const int MemoryRecallMaxResults = 5;

        /// <summary>记忆召回搜索查询最大字符数</summary>
        private const int MemoryRecallMaxQueryLength = 200;

        /// <summary>记忆注入最大字符数（约 1000 token）</summary>
        private const int MemoryContextMaxChars = 3000;

        #endregion

        #region 构造函数

        /// <summary>
        /// 创建 Agent Loop 实例。
        /// </summary>
        /// <param name="llmClient">LLM 客户端实例（通过依赖注入传入）</param>
        /// <exception cref="ArgumentNullException">当 llmClient 为 null 时抛出</exception>
        public AgentLoop(ILLMClient llmClient)
        {
            _llmClient = llmClient ?? throw new ArgumentNullException(nameof(llmClient));
            _dispatcher = new ToolCallDispatcher(ToolRegistry.Instance);
        }

        #endregion

        #region 公开方法

        /// <summary>
        /// 初始化 Agent Loop。
        /// 加载 Bootstrap 上下文并设置系统提示词。
        /// 通过 ToolAutoDiscovery 自动发现并注册所有原生工具。
        /// 如果 Bootstrap 加载失败，将使用默认的最小化系统提示词。
        /// </summary>
        public void Initialize()
        {
            if (_isInitialized)
            {
                Debug.LogWarning("[AgentCore] AgentLoop already initialized, skipping.");
                return;
            }

            // Phase 2.5: 使用 ToolAutoDiscovery 自动发现并注册原生工具
            try
            {
                ToolAutoDiscovery.DiscoverAndRegisterAll();
                Debug.Log($"[AgentCore] ToolAutoDiscovery completed, {ToolRegistry.Instance.Count} tools registered.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentCore] ToolAutoDiscovery failed (non-fatal): {ex.Message}");
            }

            string systemPrompt;

            try
            {
                var loader = new BootstrapLoader();
                var context = loader.Load();
                systemPrompt = context.CompileSystemPrompt();

                if (string.IsNullOrWhiteSpace(systemPrompt))
                {
                    Debug.LogWarning("[AgentCore] Bootstrap returned empty system prompt, using default.");
                    systemPrompt = DefaultSystemPrompt;
                }
                else
                {
                    Debug.Log($"[AgentCore] AgentLoop initialized with Bootstrap system prompt (~{context.EstimateTokenCount()} tokens).");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AgentCore] Failed to load Bootstrap context: {ex.Message}");
                Debug.LogWarning("[AgentCore] Using default system prompt as fallback.");
                systemPrompt = DefaultSystemPrompt;
            }

            _messages.Add(ChatMessage.System(systemPrompt));

            // Phase 2 Step 11: 初始化错误收集与自我纠错基础设施
            _consoleCapture = new ConsoleErrorCapture();
            _compilationWatcher = new CompilationWatcher();
            _fallbackRouter = new FallbackRouter(AgentCoreSettings.instance);

            _isInitialized = true;

            // Phase 3: 创建新会话（如果 SessionManager 还没有活动会话）
            // 修复 #5: Domain Reload 后跳过立即创建会话，让 TryRestoreSession() 先尝试恢复
            if (string.IsNullOrEmpty(SessionManager.Instance.CurrentSessionId))
            {
                if (DomainReloadState.instance.WasInterrupted)
                {
                    Debug.Log("[AgentCore] Domain Reload detected, deferring session creation to TryRestoreSession().");
                }
                else
                {
                    SessionManager.Instance.CreateNewSession();
                }
            }

            // Domain Reload Resilience: 注册 beforeAssemblyReload 事件
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        /// <summary>
        /// 发送用户消息并获取 LLM 响应。
        /// <para>
        /// Phase 2 执行流程（工具调用循环）：
        /// <list type="number">
        ///   <item>参数校验（空消息、未初始化、非 Idle 状态）</item>
        ///   <item>添加用户消息到历史</item>
        ///   <item>进入工具调用循环：</item>
        ///   <item>  a. 构建工具定义列表</item>
        ///   <item>  b. 调用 LLM 流式接口（带 tools 参数）</item>
        ///   <item>  c. 如果返回 tool_calls → 执行工具 → 追加结果到历史 → 继续循环</item>
        ///   <item>  d. 如果返回纯文本（无 tool_calls）→ 输出最终回答 → 循环结束</item>
        ///   <item>  e. 如果达到 maxToolCallRounds → 强制结束</item>
        ///   <item>回到 Idle 状态</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <param name="userMessage">用户输入的消息文本</param>
        /// <exception cref="InvalidOperationException">当 Agent 未初始化或非 Idle 状态时抛出</exception>
        /// <exception cref="ArgumentException">当消息为空时抛出</exception>
        public async Task SendMessageAsync(string userMessage)
        {
            // 1. 参数校验
            if (string.IsNullOrWhiteSpace(userMessage))
            {
                throw new ArgumentException("[AgentCore] User message cannot be empty.", nameof(userMessage));
            }

            if (!_isInitialized)
            {
                throw new InvalidOperationException("[AgentCore] AgentLoop is not initialized. Call Initialize() first.");
            }

            if (CurrentState != AgentState.Idle)
            {
                throw new InvalidOperationException(
                    $"[AgentCore] Cannot send message while in {CurrentState} state. Wait for current operation to complete.");
            }

            // 2. 创建取消令牌
            _currentCts?.Dispose();
            _currentCts = new CancellationTokenSource();
            var ct = _currentCts.Token;

            // 3. 添加用户消息到历史
            _messages.Add(ChatMessage.User(userMessage));
            var userTurn = new ConversationTurn("user", userMessage);
            _conversationTurns.Add(userTurn);

            // 标记会话内容已变更，确保保存时更新 UpdatedAt
            SessionManager.Instance.MarkDirty();

            // 4. 创建助手轮次（流式输出占位）
            var assistantTurn = new ConversationTurn("assistant")
            {
                IsStreaming = true
            };
            _conversationTurns.Add(assistantTurn);

            try
            {
                // 5. 获取配置
                var settings = AgentCoreSettings.instance;
                int maxRounds = settings.maxToolCallRounds;
                int currentRound = 0;

                // 5.5 自动记忆召回：搜索与用户消息相关的记忆并注入上下文
                if (settings.mem0Enabled && !string.IsNullOrEmpty(settings.mem0Endpoint))
                {
                    try
                    {
                        // 先移除之前注入的记忆消息（避免累积）
                        RemoveOldMemoryMessages();

                        var memories = await SearchRelevantMemories(userMessage, ct);
                        if (memories != null && memories.Count > 0)
                        {
                            var memoryContext = FormatMemoriesAsContext(memories);
                            if (!string.IsNullOrEmpty(memoryContext))
                            {
                                InjectMemoryContext(memoryContext);
                                Debug.Log($"[AgentCore] Memory recall: injected {memories.Count} memories into context.");
                            }
                        }
                        else
                        {
                            Debug.Log("[AgentCore] Memory recall: no relevant memories found.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[AgentCore] Memory recall failed (non-blocking): {ex.Message}");
                        // 记忆召回失败不应阻塞对话
                    }
                }

                // 6. 构建工具定义列表
                var toolDefinitions = BuildToolDefinitions();

                // 7. 工具调用循环（P0-2 fix: 提取为公共方法，消除与 TriggerResumeLLMCall 的代码重复）
                await RunToolCallLoopAsync(assistantTurn, toolDefinitions, ct);

                // 8. 回到 Idle 状态
                SetState(AgentState.Idle);
            }
            catch (OperationCanceledException)
            {
                Debug.Log("[AgentCore] SendMessageAsync was cancelled.");
                assistantTurn.IsStreaming = false;
                SetState(AgentState.Idle);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AgentCore] Error during SendMessageAsync: {ex}");
                assistantTurn.IsStreaming = false;

                // 发送错误事件
                EmitEvent(AgentEvent.ErrorEvent(ex.Message));

                // 短暂进入 Error 状态后回到 Idle，确保不会卡死
                SetState(AgentState.Error);
                SetState(AgentState.Idle);
            }
            finally
            {
                // 确保取消令牌被释放
                if (_currentCts != null && !_currentCts.IsCancellationRequested)
                {
                    // 正常完成，不需要额外操作
                }
            }
        }

        /// <summary>
        /// 取消当前正在进行的操作。
        /// 如果没有操作在进行中，此方法不执行任何操作。
        /// </summary>
        public void Cancel()
        {
            if (_currentCts != null && !_currentCts.IsCancellationRequested)
            {
                Debug.Log("[AgentCore] Cancelling current operation...");
                _currentCts.Cancel();
            }

            if (CurrentState != AgentState.Idle)
            {
                SetState(AgentState.Idle);
            }
        }

        /// <summary>
        /// 重置对话历史并重新初始化。
        /// 清除所有消息历史和对话轮次，重新加载 Bootstrap 上下文。
        /// </summary>
        public void ResetConversation()
        {
            Debug.Log("[AgentCore] Resetting conversation...");

            // 1. 取消当前操作
            Cancel();

            // 2. Phase 3: 保存当前会话后创建新会话
            try
            {
                SessionManager.Instance.ForceSave(
                    new List<ChatMessage>(_messages),
                    new List<ConversationTurn>(_conversationTurns));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentCore] Failed to save session before reset: {ex.Message}");
            }

            // 2.5. Phase 3: 触发自动记忆（fire-and-forget，不阻塞重置流程）
            try
            {
                SessionManager.Instance.TriggerAutoMemory(_llmClient);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentCore] Auto-memory trigger failed (non-fatal): {ex.Message}");
            }

            // 3. 清除历史
            _messages.Clear();
            _conversationTurns.Clear();
            _isInitialized = false;

            // 4. 不再通过 EmitEvent 发送 ConversationReset 事件。
            // EmitEvent 使用 EditorApplication.delayCall 延迟执行，会导致 ClearMessages()
            // 在调用方的 RefreshSessionList() 之后才执行，造成 UI 状态混乱。
            // UI 清空的职责由调用方（OnNewSessionClicked）直接调用 ClearMessages() 承担。

            // 5. Phase 3: 创建新会话
            SessionManager.Instance.CreateNewSession();

            // 6. 重新初始化
            Initialize();
        }

        /// <summary>
        /// 加载指定会话并恢复对话状态。
        /// 供 UI 层调用，用于切换会话。
        /// </summary>
        /// <param name="sessionId">要加载的会话 ID</param>
        /// <returns>是否加载成功</returns>
        public bool LoadSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                Debug.LogWarning("[AgentCore] Cannot load session with empty Id.");
                return false;
            }

            if (CurrentState != AgentState.Idle)
            {
                Debug.LogWarning("[AgentCore] Cannot load session while agent is busy.");
                return false;
            }

            // 注意：不在此处调用 ForceSave / TriggerAutoMemory。
            // 保存当前会话的职责由调用方（ChatWindow.SwitchToSession 等）承担，
            // 避免重复保存导致空会话被写入磁盘。

            var session = SessionManager.Instance.LoadSession(sessionId);
            if (session == null)
            {
                Debug.LogWarning($"[AgentCore] Failed to load session: {sessionId}");
                return false;
            }

            // 恢复对话状态
            _messages.Clear();
            _conversationTurns.Clear();

            var restoredMessages = session.ToMessages();
            var restoredTurns = session.ToConversationTurns();

            _messages.AddRange(restoredMessages);
            _conversationTurns.AddRange(restoredTurns);

            // 确保已初始化（如果消息列表中有 system 消息，则认为已初始化）
            if (_messages.Count > 0 && _messages[0].Role == "system")
            {
                _isInitialized = true;
            }

            Debug.Log($"[AgentCore] Session loaded: {sessionId} ({restoredMessages.Count} messages, {restoredTurns.Count} turns)");

            // 注意：不在此处发送 ConversationReset 事件。
            // LoadSession 仅负责数据恢复，UI 重建由调用方（如 TryRestoreSession）处理。
            // 如果在此处通过 EmitEvent 发送 ConversationReset，由于 AsyncHelper.RunOnMainThread
            // 使用 EditorApplication.delayCall（延迟执行），ClearMessages 会在调用方的
            // RebuildMessageBubbles 之后才执行，导致重建的 UI 被清除。

            return true;
        }

        #endregion

        #region LLM 调用

        /// <summary>
        /// 调用 LLM 流式接口并返回完整的 assistant 消息。
        /// <para>
        /// 从 Phase 1 的内联逻辑提取为独立方法，支持工具定义参数。
        /// 处理流式回调中的 ContentToken、ToolCallDelta、Done 和 Error 事件。
        /// </para>
        /// </summary>
        /// <param name="assistantTurn">当前助手对话轮次（用于流式内容追加）</param>
        /// <param name="tools">工具定义列表（可为 null 或空列表表示不使用工具）</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>完整的 assistant ChatMessage（可能包含 tool_calls）</returns>
        private async Task<ChatMessage> CallLLMStreamAsync(
            ConversationTurn assistantTurn,
            List<ToolDefinition> tools,
            CancellationToken ct)
        {
            // Phase 3: 上下文窗口截断
            // 创建 _messages 的浅拷贝，对拷贝进行截断，不修改原始列表（保留完整历史用于 UI 显示）
            var settings = AgentCoreSettings.instance;
            int maxTokens = settings.maxContextTokens > 0
                ? settings.maxContextTokens
                : ContextWindowManager.GetModelMaxTokens(settings.llmModel);
            int reserveTokens = settings.reserveResponseTokens;

            var messagesSnapshot = ContextWindowManager.TrimToFit(
                _messages, maxTokens, reserveTokens);

            // 切换到 Streaming 状态
            SetState(AgentState.Streaming);

            // 传递有效的工具列表（空列表时传 null，避免 API 报错）
            var effectiveTools = (tools != null && tools.Count > 0) ? tools : null;

            // Phase 2 Step 11: 使用 FallbackRouter 包装 LLM 调用，支持自动重试
            var assistantMessage = await _fallbackRouter.ExecuteStreamWithRetryAsync(
                _llmClient,
                messagesSnapshot,
                chunk => OnStreamChunkReceived(chunk, assistantTurn, ct),
                tools: effectiveTools,
                ct: ct,
                onStatusUpdate: status => EmitEvent(AgentEvent.ErrorEvent($"[Retry] {status}"))
            );

            return assistantMessage;
        }

        #endregion

        #region 工具调用执行

        /// <summary>
        /// 构建当前可用的工具定义列表。
        /// <para>
        /// 从 <see cref="ToolRegistry"/> 获取所有已注册工具，
        /// 通过 <see cref="ToolDefinitionBuilder"/> 转换为 OpenAI function calling 格式。
        /// </para>
        /// </summary>
        /// <returns>工具定义列表，无工具时返回 null</returns>
        private List<ToolDefinition> BuildToolDefinitions()
        {
            try
            {
                var definitions = ToolDefinitionBuilder.BuildAll();
                if (definitions == null || definitions.Count == 0)
                {
                    Debug.Log("[AgentCore] No tools available, LLM will run in pure chat mode.");
                    return null;
                }

                Debug.Log($"[AgentCore] Built {definitions.Count} tool definitions for LLM.");
                return definitions;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AgentCore] Failed to build tool definitions: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 执行 LLM 返回的所有工具调用，并将结果追加到消息历史。
        /// <para>
        /// 执行流程：
        /// <list type="number">
        ///   <item>为每个 tool_call 发送 <see cref="AgentEventType.ToolCallStarted"/> 事件</item>
        ///   <item>通过 <see cref="ToolCallDispatcher"/> 串行执行所有工具调用</item>
        ///   <item>为每个结果发送 <see cref="AgentEventType.ToolCallCompleted"/> 或 <see cref="AgentEventType.ToolCallFailed"/> 事件</item>
        ///   <item>将工具结果作为 <c>role="tool"</c> 消息追加到 LLM 历史</item>
        ///   <item>更新 <see cref="ConversationTurn.ToolCalls"/> 记录</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <param name="toolCalls">LLM 返回的工具调用列表</param>
        /// <param name="assistantTurn">当前助手对话轮次</param>
        /// <param name="ct">取消令牌</param>
        private async Task ExecuteToolCallsAsync(
            List<ToolCall> toolCalls,
            ConversationTurn assistantTurn,
            CancellationToken ct)
        {
            // 初始化工具调用信息列表
            if (assistantTurn.ToolCalls == null)
            {
                assistantTurn.ToolCalls = new List<ToolCallInfo>();
            }

            // 为每个 tool_call 发送开始事件
            foreach (var tc in toolCalls)
            {
                var toolName = tc.Function?.Name ?? "(unknown)";
                var arguments = tc.Function?.Arguments ?? "{}";
                EmitEvent(AgentEvent.ToolCallStarted(toolName, arguments, assistantTurn.Id, tc.Id));
            }

            // Phase 2 Step 11: 在工具执行前启动 Console 错误捕获
            _consoleCapture.StartCapture();

            // 通过 ToolCallDispatcher 串行执行所有工具调用
            var results = await _dispatcher.DispatchAllAsync(toolCalls, ct);

            // Phase 2 Step 11: 停止 Console 错误捕获
            var consoleErrors = _consoleCapture.StopCapture();

            // Phase 2.5: 检查是否有编译相关的工具调用
            // 优先使用 ToolResult.IsCompileRelated（工具自行标记），
            // 同时通过 [AgentTool] 属性的 MayModifyScripts 进行补充判断
            bool hasCompileRelated = false;
            foreach (var result in results)
            {
                if (result.Result.IsCompileRelated)
                {
                    hasCompileRelated = true;
                    break;
                }

                // 通过 [AgentTool] 属性判断是否可能修改脚本
                var tc = result.ToolCall;
                var parameters = ParseToolArguments(tc.Function?.Arguments);
                if (IsScriptModifyingCommand(result.ToolName, parameters))
                {
                    result.Result.IsCompileRelated = true;
                    hasCompileRelated = true;
                    break;
                }
            }

            // Phase 2 Step 11: 如果有编译相关的工具调用，等待编译完成并收集编译错误
            ErrorReport compilationReport = null;
            if (hasCompileRelated)
            {
                Debug.Log("[AgentCore] Compile-related tool detected, checking compilation status...");
                try
                {
                    // 先短暂等待让 Unity 检测到文件变化并开始编译
                    await Task.Delay(2000);

                    if (EditorApplication.isCompiling)
                    {
                        // Unity 已经在编译中，等待编译完成
                        Debug.Log("[AgentCore] Compilation detected, waiting for completion...");
                        compilationReport = await _compilationWatcher.RefreshAndWaitAsync();
                    }
                    else
                    {
                        // Unity 尚未开始编译，主动触发刷新并等待
                        // 使用较短的超时，因为如果文件确实被修改了，编译应该很快开始
                        var originalTimeout = _compilationWatcher.CompilationTimeoutSeconds;
                        _compilationWatcher.CompilationTimeoutSeconds = 15f;
                        Debug.Log("[AgentCore] No compilation in progress, triggering refresh with short timeout...");
                        compilationReport = await _compilationWatcher.RefreshAndWaitAsync();
                        _compilationWatcher.CompilationTimeoutSeconds = originalTimeout;
                    }

                    if (compilationReport != null && compilationReport.HasErrors)
                    {
                        Debug.Log($"[AgentCore] Compilation report: {compilationReport.Errors.Count} issue(s) found.");
                    }
                    else
                    {
                        Debug.Log("[AgentCore] Compilation completed with no errors.");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[AgentCore] Compilation watch failed: {ex.Message}");
                }
            }

            // 处理每个结果
            foreach (var result in results)
            {
                // Phase 2 Step 11: 构建增强的工具结果内容（附加错误信息）
                string contentForLLM = result.Result.GetContentForLLM();
                string enhancedContent = BuildEnhancedToolContent(
                    contentForLLM,
                    result,
                    consoleErrors,
                    compilationReport
                );

                // 创建 ToolCallInfo 记录
                var callInfo = new ToolCallInfo(
                    result.ToolCall.Id,
                    result.ToolName,
                    result.ToolCall.Function?.Arguments ?? "{}"
                );

                callInfo.Success = result.Result.Success;
                callInfo.ExecutionTimeMs = result.ExecutionTimeMs;
                callInfo.EndTime = DateTime.UtcNow;
                callInfo.Result = enhancedContent;

                assistantTurn.ToolCalls.Add(callInfo);

                // 发送工具调用结果事件
                if (result.Result.Success)
                {
                    EmitEvent(AgentEvent.ToolCallCompleted(
                        result.ToolName,
                        contentForLLM,
                        result.ExecutionTimeMs,
                        assistantTurn.Id,
                        result.ToolCall.Id
                    ));
                }
                else
                {
                    EmitEvent(AgentEvent.ToolCallFailed(
                        result.ToolName,
                        contentForLLM,
                        result.ExecutionTimeMs,
                        assistantTurn.Id,
                        result.ToolCall.Id
                    ));
                }
            }

            // Phase 2 Step 11: 构建带错误信息的 tool messages 追加到 LLM 历史
            var toolMessages = BuildToolMessagesWithErrors(results, consoleErrors, compilationReport);
            _messages.AddRange(toolMessages);

            Debug.Log($"[AgentCore] Executed {results.Count} tool call(s), " +
                      $"added {toolMessages.Count} tool message(s) to history.");
        }

        /// <summary>
        /// 构建增强的工具结果内容，附加错误信息供 LLM 自我纠错。
        /// </summary>
        private string BuildEnhancedToolContent(
            string originalContent,
            ToolCallResult result,
            List<ErrorInfo> consoleErrors,
            ErrorReport compilationReport)
        {
            // 如果没有额外错误信息，直接返回原始内容
            bool hasConsoleErrors = consoleErrors != null && consoleErrors.Count > 0;
            bool hasCompilationErrors = compilationReport != null && compilationReport.HasErrors;

            if (!hasConsoleErrors && !hasCompilationErrors)
            {
                return originalContent;
            }

            // 只为编译相关的工具附加编译错误
            if (!result.Result.IsCompileRelated)
            {
                // 非编译相关工具只附加 console 错误
                if (!hasConsoleErrors) return originalContent;
            }

            var sb = new StringBuilder();
            sb.AppendLine(originalContent);

            // 附加 Console 错误
            if (hasConsoleErrors)
            {
                sb.AppendLine();
                sb.AppendLine("--- Console Errors Detected During Execution ---");
                int maxConsoleErrors = Math.Min(consoleErrors.Count, 5);
                for (int i = 0; i < maxConsoleErrors; i++)
                {
                    sb.Append(consoleErrors[i].FormatForLLM());
                }
                if (consoleErrors.Count > maxConsoleErrors)
                {
                    sb.AppendLine($"... and {consoleErrors.Count - maxConsoleErrors} more console errors.");
                }
            }

            // 附加编译错误（仅对编译相关工具）
            if (result.Result.IsCompileRelated && hasCompilationErrors)
            {
                sb.AppendLine();
                sb.AppendLine("--- Compilation Errors Detected ---");
                sb.AppendLine("The script modification caused compilation errors. Please fix them:");
                sb.Append(compilationReport.FormatForLLM(maxErrors: 10));
            }

            return sb.ToString();
        }

        /// <summary>
        /// 构建带错误信息的 tool messages，用于追加到 LLM 历史。
        /// 将编译错误和 console 错误合并到对应工具的结果消息中。
        /// </summary>
        private List<ChatMessage> BuildToolMessagesWithErrors(
            List<ToolCallResult> results,
            List<ErrorInfo> consoleErrors,
            ErrorReport compilationReport)
        {
            var messages = new List<ChatMessage>();

            foreach (var result in results)
            {
                string content = BuildEnhancedToolContent(
                    result.Result.GetContentForLLM(),
                    result,
                    consoleErrors,
                    compilationReport
                );

                messages.Add(ChatMessage.Tool(result.ToolCall.Id, content));
            }

            return messages;
        }

        /// <summary>
        /// 判断工具调用是否可能修改脚本文件（触发编译）。
        /// <para>
        /// Phase 2.5: 基于 <see cref="AgentToolAttribute.MayModifyScripts"/> 属性判断，
        /// 替代旧的硬编码命令名列表。对于标记了 MayModifyScripts 的工具，
        /// 进一步检查 action 参数排除只读操作（如 read、list、get_info）。
        /// </para>
        /// </summary>
        /// <param name="toolName">工具名称</param>
        /// <param name="parameters">工具参数</param>
        /// <returns>如果工具调用可能修改脚本则返回 true</returns>
        private bool IsScriptModifyingCommand(string toolName, JObject parameters)
        {
            var tool = ToolRegistry.Instance.GetTool(toolName);
            if (tool == null) return false;

            // 检查 [AgentTool] 属性的 MayModifyScripts
            var attr = tool.GetType().GetCustomAttribute<AgentToolAttribute>();
            if (attr == null || !attr.MayModifyScripts) return false;

            // 对于标记了 MayModifyScripts 的工具，进一步检查 action 参数
            // 排除只读操作（不会触发编译的操作）
            var action = parameters?["action"]?.ToString()?.ToLower();
            if (!string.IsNullOrEmpty(action))
            {
                // 这些 action 是只读操作，不会修改脚本
                if (action == "read" || action == "list" || action == "get_info" ||
                    action == "get" || action == "search" || action == "validate")
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 安全解析工具调用参数（JSON string → JObject）。
        /// </summary>
        /// <param name="arguments">JSON 格式的参数字符串</param>
        /// <returns>解析后的 JObject，解析失败时返回空 JObject</returns>
        private static JObject ParseToolArguments(string arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments))
                return new JObject();

            try
            {
                return JObject.Parse(arguments);
            }
            catch
            {
                return new JObject();
            }
        }

        #endregion

        #region 响应处理

        /// <summary>
        /// 工具调用循环的核心逻辑。
        /// <para>
        /// 从 SendMessageAsync 和 TriggerResumeLLMCall 中提取的公共方法，
        /// 包含 while 循环、连续失败检测、单工具失败追踪、强制退出、最大轮次检查、
        /// LoopCompleted 事件发送和自动保存。
        /// </para>
        /// </summary>
        /// <param name="assistantTurn">当前助手对话轮次</param>
        /// <param name="toolDefinitions">工具定义列表</param>
        /// <param name="ct">取消令牌</param>
        /// <param name="logPrefix">日志前缀（用于区分正常调用和恢复调用）</param>
        private async Task RunToolCallLoopAsync(
            ConversationTurn assistantTurn,
            List<ToolDefinition> toolDefinitions,
            CancellationToken ct,
            string logPrefix = "")
        {
            var settings = AgentCoreSettings.instance;
            int maxRounds = settings.maxToolCallRounds;
            int currentRound = 0;

            // 连续失败检测
            int consecutiveAllFailRounds = 0;
            const int maxConsecutiveFailures = 3;

            // 单工具连续失败追踪
            var perToolFailCount = new Dictionary<string, int>();
            const int maxPerToolFailures = 3;

            while (currentRound < maxRounds)
            {
                currentRound++;
                EmitEvent(AgentEvent.LoopRoundStarted(currentRound, maxRounds));

                if (ct.IsCancellationRequested)
                {
                    Debug.Log($"[AgentCore]{logPrefix} Cancelled before round {currentRound}.");
                    break;
                }

                // 调用 LLM（流式）
                SetState(AgentState.Thinking);
                var assistantMessage = await CallLLMStreamAsync(assistantTurn, toolDefinitions, ct);

                if (ct.IsCancellationRequested)
                {
                    Debug.Log($"[AgentCore]{logPrefix} Cancelled during LLM call.");
                    break;
                }

                // 检查是否有 tool_calls
                if (assistantMessage == null ||
                    assistantMessage.ToolCalls == null ||
                    assistantMessage.ToolCalls.Count == 0)
                {
                    // 纯文本回复 — 循环结束
                    HandleFinalResponse(assistantMessage, assistantTurn);
                    break;
                }

                // 有 tool_calls — 执行工具
                Debug.Log($"[AgentCore]{logPrefix} Round {currentRound}: LLM returned {assistantMessage.ToolCalls.Count} tool call(s).");
                _messages.Add(assistantMessage);

                SetState(AgentState.ExecutingTool);
                await ExecuteToolCallsAsync(assistantMessage.ToolCalls, assistantTurn, ct);

                if (ct.IsCancellationRequested)
                {
                    Debug.Log($"[AgentCore]{logPrefix} Cancelled during tool execution.");
                    break;
                }

                // 连续失败检测
                bool allToolCallsFailed = CheckAllToolCallsFailed(assistantTurn, assistantMessage.ToolCalls.Count);
                if (allToolCallsFailed)
                {
                    consecutiveAllFailRounds++;
                    Debug.LogWarning($"[AgentCore]{logPrefix} All tool calls failed in round {currentRound}. " +
                                     $"Consecutive failure rounds: {consecutiveAllFailRounds}/{maxConsecutiveFailures}");
                }
                else
                {
                    consecutiveAllFailRounds = 0;
                }

                // 单工具连续失败追踪
                UpdatePerToolFailCounts(assistantTurn, assistantMessage.ToolCalls, perToolFailCount);
                string repeatedFailTool = null;
                foreach (var kvp in perToolFailCount)
                {
                    if (kvp.Value >= maxPerToolFailures)
                    {
                        repeatedFailTool = kvp.Key;
                        break;
                    }
                }

                // 判断是否需要强制退出
                bool shouldForceExit = consecutiveAllFailRounds >= maxConsecutiveFailures || repeatedFailTool != null;
                if (shouldForceExit)
                {
                    string reason = repeatedFailTool != null
                        ? $"Tool '{repeatedFailTool}' has failed {maxPerToolFailures} consecutive times"
                        : $"All tool calls have failed consecutively {maxConsecutiveFailures} rounds";

                    Debug.LogWarning($"[AgentCore]{logPrefix} {reason}. Forcing final response.");

                    _messages.Add(ChatMessage.System(
                        "[SYSTEM] " + reason + "。" +
                        "你现在必须立即停止调用任何工具，直接用纯文本向用户解释问题。" +
                        "总结你之前尝试做什么以及哪里出了问题。不要再发起任何 tool_call。"));

                    assistantTurn.IsStreaming = true;
                    SetState(AgentState.Thinking);
                    var finalMessage = await CallLLMStreamAsync(assistantTurn, toolDefinitions, ct);

                    if (finalMessage != null && finalMessage.ToolCalls != null && finalMessage.ToolCalls.Count > 0)
                    {
                        Debug.LogWarning($"[AgentCore]{logPrefix} LLM returned {finalMessage.ToolCalls.Count} tool call(s) in final round despite stop instruction. Ignoring.");
                        finalMessage.ToolCalls.Clear();
                    }

                    HandleFinalResponse(finalMessage, assistantTurn);
                    break;
                }

                // 重置流式状态，准备下一轮
                assistantTurn.IsStreaming = true;
            }

            // 检查是否因达到最大轮次而退出
            if (currentRound >= maxRounds && CurrentState != AgentState.Idle)
            {
                Debug.LogWarning($"[AgentCore]{logPrefix} Reached max tool call rounds ({maxRounds}). Requesting LLM to summarize.");

                _messages.Add(ChatMessage.System(
                    "[SYSTEM] 你已达到工具调用上限（" + maxRounds + "轮）。" +
                    "你现在必须立即停止调用任何工具，直接用纯文本总结当前已完成的工作进度和结果。" +
                    "不要再发起任何 tool_call。"));

                assistantTurn.IsStreaming = true;
                SetState(AgentState.Thinking);
                var summaryMessage = await CallLLMStreamAsync(assistantTurn, toolDefinitions, ct);

                if (summaryMessage != null && summaryMessage.ToolCalls != null && summaryMessage.ToolCalls.Count > 0)
                {
                    Debug.LogWarning($"[AgentCore]{logPrefix} LLM returned {summaryMessage.ToolCalls.Count} tool call(s) in summary round despite stop instruction. Ignoring.");
                    summaryMessage.ToolCalls.Clear();
                }

                HandleFinalResponse(summaryMessage, assistantTurn);
            }

            // 发送循环结束事件
            EmitEvent(AgentEvent.LoopCompleted(currentRound));

            // 自动保存会话
            try
            {
                SessionManager.Instance.AutoSave(
                    new List<ChatMessage>(_messages),
                    new List<ConversationTurn>(_conversationTurns));
            }
            catch (Exception saveEx)
            {
                Debug.LogWarning($"[AgentCore]{logPrefix} Auto-save failed: {saveEx.Message}");
            }
        }

        /// <summary>
        /// 处理 LLM 的最终文本响应（无 tool_calls 的纯文本回复）。
        /// <para>
        /// 将完整的 assistant 消息添加到 LLM 历史，
        /// 更新 UI 轮次状态，并发送 <see cref="AgentEventType.AssistantMessage"/> 事件。
        /// </para>
        /// </summary>
        /// <param name="assistantMessage">LLM 返回的完整 assistant 消息</param>
        /// <param name="assistantTurn">当前助手对话轮次</param>
        private void HandleFinalResponse(ChatMessage assistantMessage, ConversationTurn assistantTurn)
        {
            // 标记流式结束
            assistantTurn.IsStreaming = false;

            // 将完整的助手消息添加到 LLM 历史
            if (assistantMessage != null)
            {
                _messages.Add(assistantMessage);

                // 确保 UI 轮次内容与 LLM 返回一致
                if (!string.IsNullOrEmpty(assistantMessage.Content))
                {
                    assistantTurn.Content = assistantMessage.Content;
                }
            }
            else
            {
                // 兜底：如果返回 null，使用流式累积的内容
                _messages.Add(ChatMessage.Assistant(assistantTurn.Content));
            }

            // 发送完整助手消息事件
            EmitEvent(AgentEvent.AssistantMessage(assistantTurn.Content, assistantTurn.Id));
        }

        /// <summary>
        /// 检查本轮所有工具调用是否全部失败。
        /// <para>
        /// 从 <see cref="ConversationTurn.ToolCalls"/> 列表末尾取最近 N 条记录
        /// （N = 本轮 LLM 返回的 tool_calls 数量），检查是否全部 <see cref="ToolCallInfo.Success"/> 为 false。
        /// </para>
        /// </summary>
        /// <param name="assistantTurn">当前助手对话轮次</param>
        /// <param name="toolCallCount">本轮 LLM 返回的 tool_calls 数量</param>
        /// <returns>如果本轮所有工具调用都失败则返回 true，否则返回 false</returns>
        private static bool CheckAllToolCallsFailed(ConversationTurn assistantTurn, int toolCallCount)
        {
            if (assistantTurn.ToolCalls == null || assistantTurn.ToolCalls.Count == 0 || toolCallCount <= 0)
            {
                return false;
            }

            // 从列表末尾取本轮的工具调用记录
            int startIndex = Math.Max(0, assistantTurn.ToolCalls.Count - toolCallCount);
            for (int i = startIndex; i < assistantTurn.ToolCalls.Count; i++)
            {
                if (assistantTurn.ToolCalls[i].Success)
                {
                    return false; // 至少有一个成功，不算全部失败
                }
            }

            return true; // 全部失败
        }

        /// <summary>
        /// 更新每个工具的连续失败计数。
        /// <para>
        /// 遍历本轮的工具调用结果，对于失败的工具增加其连续失败计数，
        /// 对于成功的工具重置其计数为 0。这样即使一轮中有其他成功的工具，
        /// 也能追踪到某个特定工具的连续失败情况。
        /// </para>
        /// </summary>
        /// <param name="assistantTurn">当前助手对话轮次</param>
        /// <param name="toolCalls">本轮 LLM 返回的工具调用列表</param>
        /// <param name="perToolFailCount">每个工具的连续失败计数字典</param>
        private static void UpdatePerToolFailCounts(
            ConversationTurn assistantTurn,
            List<ToolCall> toolCalls,
            Dictionary<string, int> perToolFailCount)
        {
            if (assistantTurn.ToolCalls == null || toolCalls == null || toolCalls.Count == 0)
                return;

            // 从列表末尾取本轮的工具调用记录
            int startIndex = Math.Max(0, assistantTurn.ToolCalls.Count - toolCalls.Count);
            for (int i = startIndex; i < assistantTurn.ToolCalls.Count; i++)
            {
                var callInfo = assistantTurn.ToolCalls[i];
                var toolName = callInfo.ToolName;
                if (string.IsNullOrEmpty(toolName)) continue;

                if (!callInfo.Success)
                {
                    // 失败：增加该工具的连续失败计数
                    if (perToolFailCount.ContainsKey(toolName))
                        perToolFailCount[toolName]++;
                    else
                        perToolFailCount[toolName] = 1;

                    Debug.LogWarning($"[AgentCore] Tool '{toolName}' failed. " +
                                     $"Consecutive failures for this tool: {perToolFailCount[toolName]}");
                }
                else
                {
                    // 成功：重置该工具的连续失败计数
                    if (perToolFailCount.ContainsKey(toolName))
                        perToolFailCount[toolName] = 0;
                }
            }
        }

        #endregion

        #region 流式回调

        /// <summary>
        /// 处理 LLM 流式回调中的单个 chunk。
        /// 此方法可能在后台线程被调用，通过 <see cref="EmitEvent"/> 确保事件在主线程派发。
        /// </summary>
        /// <param name="chunk">流式 chunk 数据</param>
        /// <param name="assistantTurn">当前助手对话轮次</param>
        /// <param name="ct">取消令牌</param>
        private void OnStreamChunkReceived(StreamChunk chunk, ConversationTurn assistantTurn, CancellationToken ct)
        {
            // 检查取消
            if (ct.IsCancellationRequested)
            {
                return;
            }

            switch (chunk.Type)
            {
                case StreamChunkType.ContentToken:
                    // 追加 token 到助手轮次内容
                    if (!string.IsNullOrEmpty(chunk.Content))
                    {
                        assistantTurn.Content += chunk.Content;
                        EmitEvent(AgentEvent.StreamToken(chunk.Content, assistantTurn.Id));
                    }
                    break;

                case StreamChunkType.Done:
                    // 流式完成，由 SendMessageAsync 的后续逻辑处理
                    Debug.Log($"[AgentCore] Stream completed. Finish reason: {chunk.FinishReason}");
                    break;

                case StreamChunkType.Error:
                    // 流式过程中的解析错误
                    Debug.LogError($"[AgentCore] Stream error: {chunk.Error}");
                    EmitEvent(AgentEvent.ErrorEvent(chunk.Error));
                    break;

                case StreamChunkType.ToolCallDelta:
                    // Phase 2：ToolCallDelta 由 OpenAICompatibleClient 内部累积，
                    // 最终通过 Done 事件返回完整的 tool_calls 列表。
                    // 此处仅记录日志用于调试。
                    Debug.Log($"[AgentCore] Received ToolCallDelta: {chunk.ToolCallDelta?.Function?.Name ?? "(accumulating)"}");
                    break;
            }
        }

        #endregion

        #region 记忆召回

        /// <summary>
        /// 移除之前注入的记忆消息，避免记忆消息在对话历史中累积。
        /// 通过消息内容前缀 <see cref="MemoryMessagePrefix"/> 来识别记忆消息。
        /// </summary>
        private void RemoveOldMemoryMessages()
        {
            for (int i = _messages.Count - 1; i >= 0; i--)
            {
                if (_messages[i].Role == "system" &&
                    _messages[i].Content != null &&
                    _messages[i].Content.StartsWith(MemoryMessagePrefix))
                {
                    _messages.RemoveAt(i);
                    Debug.Log("[AgentCore] Removed old memory injection message.");
                }
            }
        }

        /// <summary>
        /// 搜索与用户消息相关的记忆。
        /// 使用 <see cref="Mem0Client.SearchMemoryAsync"/> 进行语义搜索。
        /// <para>
        /// 关键约束：
        /// <list type="bullet">
        ///   <item>搜索查询截断到 <see cref="MemoryRecallMaxQueryLength"/> 字符</item>
        ///   <item>限制返回 <see cref="MemoryRecallMaxResults"/> 条结果</item>
        ///   <item>设置 <see cref="MemoryRecallTimeoutSeconds"/> 秒超时，避免影响响应速度</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <param name="userMessage">用户消息文本</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>匹配的记忆列表，失败时返回空列表</returns>
        private async Task<List<Mem0Memory>> SearchRelevantMemories(string userMessage, CancellationToken ct)
        {
            // 截断查询到合理长度
            var query = userMessage.Length > MemoryRecallMaxQueryLength
                ? userMessage.Substring(0, MemoryRecallMaxQueryLength)
                : userMessage;

            // 创建带超时的取消令牌
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(MemoryRecallTimeoutSeconds));

            try
            {
                var client = Mem0Client.FromSettings();
                var memories = await client.SearchMemoryAsync(
                    query: query,
                    limit: MemoryRecallMaxResults,
                    ct: timeoutCts.Token
                );
                return memories;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 仅超时导致的取消，不是用户主动取消
                Debug.LogWarning($"[AgentCore] Memory recall timed out after {MemoryRecallTimeoutSeconds}s.");
                return new List<Mem0Memory>();
            }
        }

        /// <summary>
        /// 将搜索到的记忆格式化为系统消息文本。
        /// 限制总字符数不超过 <see cref="MemoryContextMaxChars"/>（约 1000 token）。
        /// </summary>
        /// <param name="memories">记忆列表</param>
        /// <returns>格式化的记忆上下文文本，无有效内容时返回 null</returns>
        private string FormatMemoriesAsContext(List<Mem0Memory> memories)
        {
            if (memories == null || memories.Count == 0)
                return null;

            var sb = new StringBuilder();
            sb.AppendLine(MemoryMessagePrefix);

            int totalChars = sb.Length;
            int addedCount = 0;

            foreach (var memory in memories)
            {
                if (string.IsNullOrWhiteSpace(memory.Content))
                    continue;

                var line = $"- {memory.Content.Trim()}";

                // 检查是否超过最大字符限制
                if (totalChars + line.Length + 2 > MemoryContextMaxChars) // +2 for newline
                    break;

                sb.AppendLine(line);
                totalChars += line.Length + 2;
                addedCount++;
            }

            if (addedCount == 0)
                return null;

            sb.AppendLine("[请参考以上记忆辅助回答，但以当前对话上下文为准]");
            return sb.ToString();
        }

        /// <summary>
        /// 将格式化的记忆文本作为系统消息注入到 <see cref="_messages"/> 列表中。
        /// 位置：在系统提示词（index 0）之后、第一条用户消息之前。
        /// </summary>
        /// <param name="memoryContext">格式化的记忆上下文文本</param>
        private void InjectMemoryContext(string memoryContext)
        {
            if (string.IsNullOrEmpty(memoryContext))
                return;

            // 找到第一条非 system 消息的位置（即系统提示词之后）
            int insertIndex = 1; // 默认在 system prompt 之后
            for (int i = 0; i < _messages.Count; i++)
            {
                if (_messages[i].Role != "system")
                {
                    insertIndex = i;
                    break;
                }
                insertIndex = i + 1;
            }

            _messages.Insert(insertIndex, ChatMessage.System(memoryContext));
        }

        #endregion

        #region 状态管理

        /// <summary>
        /// 设置 Agent 状态并派发状态变更事件。
        /// </summary>
        /// <param name="newState">新的 Agent 状态</param>
        private void SetState(AgentState newState)
        {
            if (CurrentState == newState)
            {
                return;
            }

            var previousState = CurrentState;
            CurrentState = newState;
            Debug.Log($"[AgentCore] State: {previousState} -> {newState}");
            EmitEvent(AgentEvent.StateChanged(newState));
        }

        /// <summary>
        /// 派发 Agent 事件。
        /// 使用 <see cref="AsyncHelper.RunOnMainThread"/> 确保事件在 Unity 主线程上触发，
        /// 因为 LLM 流式回调可能在后台线程执行。
        /// </summary>
        /// <param name="evt">要派发的事件</param>
        private void EmitEvent(AgentEvent evt)
        {
            AsyncHelper.RunOnMainThread(() => OnAgentEvent?.Invoke(evt));
        }

        #endregion

        #region Domain Reload 保护

        /// <summary>
        /// AssemblyReloadEvents.beforeAssemblyReload 回调。
        /// 在 Domain Reload 前保存中断状态和对话历史。
        /// Phase 2 增强：额外保存用户消息、assistant 部分内容、tool_call ID。
        /// </summary>
        private void OnBeforeAssemblyReload()
        {
            // 1. 检查当前是否正在执行操作
            if (CurrentState == AgentState.Idle)
            {
                // Agent 空闲，无需保存中断状态
                Debug.Log("[AgentCore] beforeAssemblyReload: Agent is idle, no interruption to save.");
                return;
            }

            // 2. 映射当前 AgentState 到 InterruptPhase
            InterruptPhase phase;
            switch (CurrentState)
            {
                case AgentState.Streaming:
                case AgentState.Thinking:
                    phase = InterruptPhase.Streaming;
                    break;
                case AgentState.ExecutingTool:
                    phase = InterruptPhase.ExecutingTool;
                    break;
                default:
                    phase = InterruptPhase.None;
                    break;
            }

            // 3. 获取最后执行的工具名和 pending tool call 信息
            string lastToolName = null;
            string interruptedToolCallId = null;
            bool hadPendingToolCalls = false;
            for (int i = _messages.Count - 1; i >= 0; i--)
            {
                var msg = _messages[i];
                if (msg.Role == "assistant" && msg.ToolCalls != null && msg.ToolCalls.Count > 0)
                {
                    var lastToolCall = msg.ToolCalls[msg.ToolCalls.Count - 1];
                    lastToolName = lastToolCall.Function?.Name;
                    // 检查是否有未完成的 tool calls（assistant 发了 tool_calls 但还没有对应的 tool 结果）
                    int toolCallCount = msg.ToolCalls.Count;
                    int toolResultCount = 0;
                    for (int j = i + 1; j < _messages.Count; j++)
                    {
                        if (_messages[j].Role == "tool") toolResultCount++;
                        else break;
                    }
                    hadPendingToolCalls = toolResultCount < toolCallCount;
                    if (hadPendingToolCalls)
                    {
                        // 保存第一个未完成的 tool_call ID
                        int pendingIndex = toolResultCount;
                        if (pendingIndex < msg.ToolCalls.Count)
                        {
                            interruptedToolCallId = msg.ToolCalls[pendingIndex].Id;
                        }
                    }
                    break;
                }
            }

            // 4. Phase 2: 提取最后一条用户消息
            string pendingUserMessage = null;
            for (int i = _messages.Count - 1; i >= 0; i--)
            {
                if (_messages[i].Role == "user")
                {
                    pendingUserMessage = _messages[i].Content;
                    break;
                }
            }

            // 5. Phase 2: 提取最后一条 assistant 部分内容（从 ConversationTurns 中获取流式累积内容）
            string lastAssistantContent = null;
            for (int i = _conversationTurns.Count - 1; i >= 0; i--)
            {
                if (_conversationTurns[i].Role == "assistant" && !string.IsNullOrEmpty(_conversationTurns[i].Content))
                {
                    lastAssistantContent = _conversationTurns[i].Content;
                    break;
                }
            }

            // 6. 保存中断标记到 DomainReloadState（Phase 2 增强版）
            var sessionId = SessionManager.Instance.CurrentSessionId;
            DomainReloadState.instance.MarkInterrupted(
                sessionId,
                phase,
                lastToolName,
                hadPendingToolCalls,
                pendingUserMessage,
                lastAssistantContent,
                interruptedToolCallId
            );

            // 7. 强制保存当前对话历史到磁盘
            try
            {
                SessionManager.Instance.ForceSave(
                    new List<ChatMessage>(_messages),
                    new List<ConversationTurn>(_conversationTurns));
                Debug.Log("[AgentCore] beforeAssemblyReload: Session saved successfully.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentCore] beforeAssemblyReload: Failed to save session: {ex.Message}");
            }

            // 8. 取消当前操作的 CancellationToken
            if (_currentCts != null && !_currentCts.IsCancellationRequested)
            {
                _currentCts.Cancel();
                Debug.Log("[AgentCore] beforeAssemblyReload: Cancelled current operation.");
            }

            Debug.Log($"[AgentCore] beforeAssemblyReload: Interruption saved — state={CurrentState}, phase={phase}, " +
                      $"tool={lastToolName}, toolCallId={interruptedToolCallId}, " +
                      $"hasUserMsg={!string.IsNullOrEmpty(pendingUserMessage)}, " +
                      $"hasAssistantContent={!string.IsNullOrEmpty(lastAssistantContent)}");
        }

        /// <summary>
        /// 尝试在 Domain Reload 后恢复 Agent 工作流。
        /// <para>
        /// 根据 <see cref="DomainReloadState"/> 中记录的中断信息，决定恢复策略：
        /// <list type="bullet">
        ///   <item><b>Streaming 中断</b>：注入系统消息说明中断原因，重新发送请求让 LLM 继续</item>
        ///   <item><b>ExecutingTool 中断</b>：注入系统消息说明 tool 执行被中断，让 LLM 重新调用该 tool</item>
        ///   <item><b>WaitingCompilation 中断</b>：注入编译结果消息，让 AgentLoop 继续处理</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <returns>是否成功触发了恢复流程</returns>
        public bool TryResumeAfterReload()
        {
            var reloadState = DomainReloadState.instance;

            // 1. 检查是否有中断标记
            if (!reloadState.WasInterrupted)
            {
                Debug.Log("[AgentCore] TryResumeAfterReload: No interruption detected, skipping.");
                return false;
            }

            // 2. 确保 AgentLoop 已完全初始化
            if (!_isInitialized)
            {
                Debug.LogWarning("[AgentCore] TryResumeAfterReload: AgentLoop not initialized, cannot resume.");
                reloadState.ClearInterruption();
                return false;
            }

            // 3. 确保当前处于 Idle 状态
            if (CurrentState != AgentState.Idle)
            {
                Debug.LogWarning($"[AgentCore] TryResumeAfterReload: Agent is in {CurrentState} state, cannot resume.");
                reloadState.ClearInterruption();
                return false;
            }

            // 4. 确保消息历史不为空（至少有 system prompt）
            if (_messages.Count == 0)
            {
                Debug.LogWarning("[AgentCore] TryResumeAfterReload: Message history is empty, cannot resume.");
                reloadState.ClearInterruption();
                return false;
            }

            var phase = reloadState.InterruptPhase;
            var lastToolName = reloadState.LastToolName;
            var interruptedToolCallId = reloadState.InterruptedToolCallId;
            var lastAssistantContent = reloadState.LastAssistantContent;
            var compilationSucceeded = reloadState.CompilationSucceeded;
            var compilationErrors = reloadState.CompilationErrors;

            Debug.Log($"[AgentCore] TryResumeAfterReload: Resuming from {phase} interruption " +
                      $"(tool={lastToolName}, compilationOK={compilationSucceeded})");

            // 5. 根据中断阶段构建恢复消息
            string recoveryMessage = BuildRecoveryMessage(phase, lastToolName, interruptedToolCallId,
                compilationSucceeded, compilationErrors);

            // 6. 根据中断阶段执行恢复策略
            switch (phase)
            {
                case InterruptPhase.Streaming:
                    ResumeFromStreaming(recoveryMessage, lastAssistantContent);
                    break;

                case InterruptPhase.ExecutingTool:
                    ResumeFromExecutingTool(recoveryMessage, interruptedToolCallId, lastToolName);
                    break;

                case InterruptPhase.WaitingCompilation:
                    ResumeFromWaitingCompilation(recoveryMessage, interruptedToolCallId,
                        compilationSucceeded, compilationErrors);
                    break;

                default:
                    Debug.LogWarning($"[AgentCore] TryResumeAfterReload: Unknown phase {phase}, clearing state.");
                    reloadState.ClearInterruption();
                    return false;
            }

            // 7. 清除中断标记
            reloadState.ClearInterruption();

            Debug.Log("[AgentCore] TryResumeAfterReload: Recovery initiated successfully.");
            return true;
        }

        /// <summary>
        /// 构建 Domain Reload 恢复系统消息。
        /// </summary>
        /// <param name="phase">中断阶段</param>
        /// <param name="lastToolName">最后执行的工具名</param>
        /// <param name="toolCallId">被中断的 tool_call ID</param>
        /// <param name="compilationSucceeded">编译是否成功</param>
        /// <param name="compilationErrors">编译错误信息</param>
        /// <returns>恢复系统消息文本</returns>
        private static string BuildRecoveryMessage(
            InterruptPhase phase,
            string lastToolName,
            string toolCallId,
            bool compilationSucceeded,
            string compilationErrors)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[Domain Reload Recovery] Unity code compilation triggered a Domain Reload, which interrupted the Agent workflow.");
            sb.AppendLine($"- Interruption phase: {phase}");

            if (!string.IsNullOrEmpty(lastToolName))
            {
                sb.AppendLine($"- Last tool being used: {lastToolName}");
            }

            if (!string.IsNullOrEmpty(toolCallId))
            {
                sb.AppendLine($"- Interrupted tool_call ID: {toolCallId}");
            }

            // 编译结果
            if (compilationSucceeded)
            {
                sb.AppendLine("- Compilation result: Success");
            }
            else if (!string.IsNullOrEmpty(compilationErrors))
            {
                sb.AppendLine($"- Compilation result: Failed");
                sb.AppendLine($"- Compilation errors:\n{compilationErrors}");
            }
            else
            {
                sb.AppendLine("- Compilation result: Unknown (compilation status not captured)");
            }

            sb.AppendLine("Please continue from where you left off. If you were in the middle of executing a tool, please retry the operation.");

            return sb.ToString();
        }

        /// <summary>
        /// 从 Streaming 中断恢复：注入 assistant 部分内容和系统消息，重新调用 LLM。
        /// </summary>
        /// <param name="recoveryMessage">恢复系统消息</param>
        /// <param name="lastAssistantContent">中断前的 assistant 部分内容</param>
        private void ResumeFromStreaming(string recoveryMessage, string lastAssistantContent)
        {
            Debug.Log("[AgentCore] ResumeFromStreaming: Injecting recovery message and re-calling LLM.");

            // 如果有 assistant 部分内容，添加到消息历史中
            if (!string.IsNullOrEmpty(lastAssistantContent))
            {
                // 检查消息历史末尾是否已经有这条 assistant 消息（避免重复）
                bool alreadyHasAssistant = _messages.Count > 0 &&
                    _messages[_messages.Count - 1].Role == "assistant" &&
                    _messages[_messages.Count - 1].Content == lastAssistantContent;

                if (!alreadyHasAssistant)
                {
                    _messages.Add(ChatMessage.Assistant(lastAssistantContent));
                    Debug.Log($"[AgentCore] ResumeFromStreaming: Added partial assistant content ({lastAssistantContent.Length} chars).");
                }
            }

            // 注入恢复系统消息
            _messages.Add(ChatMessage.System(recoveryMessage));

            // 触发新的 LLM 调用（通过 SendMessageAsync 的内部机制）
            TriggerResumeLLMCall();
        }

        /// <summary>
        /// 从 ExecutingTool 中断恢复：注入系统消息说明 tool 执行被中断，让 LLM 重新调用。
        /// </summary>
        /// <param name="recoveryMessage">恢复系统消息</param>
        /// <param name="interruptedToolCallId">被中断的 tool_call ID</param>
        /// <param name="lastToolName">最后执行的工具名</param>
        private void ResumeFromExecutingTool(string recoveryMessage, string interruptedToolCallId, string lastToolName)
        {
            Debug.Log($"[AgentCore] ResumeFromExecutingTool: Tool '{lastToolName}' was interrupted (callId={interruptedToolCallId}).");

            // 如果有未完成的 tool_call，需要补充一个 tool response 以保持消息格式合法
            if (!string.IsNullOrEmpty(interruptedToolCallId))
            {
                // 检查是否已经有对应的 tool response
                bool hasResponse = false;
                for (int i = _messages.Count - 1; i >= 0; i--)
                {
                    if (_messages[i].Role == "tool" && _messages[i].ToolCallId == interruptedToolCallId)
                    {
                        hasResponse = true;
                        break;
                    }
                    // 如果遇到了 assistant 消息，说明还没有对应的 tool response
                    if (_messages[i].Role == "assistant") break;
                }

                if (!hasResponse)
                {
                    // 补充一个表示中断的 tool response
                    _messages.Add(ChatMessage.Tool(interruptedToolCallId,
                        $"[Tool execution interrupted by Domain Reload] The tool '{lastToolName}' was interrupted " +
                        "because Unity triggered a code compilation and Domain Reload. " +
                        "The tool result is unknown. Please retry the operation if needed."));
                    Debug.Log($"[AgentCore] ResumeFromExecutingTool: Added placeholder tool response for {interruptedToolCallId}.");
                }
            }

            // 注入恢复系统消息
            _messages.Add(ChatMessage.System(recoveryMessage));

            // 触发新的 LLM 调用
            TriggerResumeLLMCall();
        }

        /// <summary>
        /// 从 WaitingCompilation 中断恢复：注入编译结果作为 tool response，继续 AgentLoop。
        /// </summary>
        /// <param name="recoveryMessage">恢复系统消息</param>
        /// <param name="interruptedToolCallId">被中断的 tool_call ID</param>
        /// <param name="compilationSucceeded">编译是否成功</param>
        /// <param name="compilationErrors">编译错误信息</param>
        private void ResumeFromWaitingCompilation(
            string recoveryMessage,
            string interruptedToolCallId,
            bool compilationSucceeded,
            string compilationErrors)
        {
            Debug.Log($"[AgentCore] ResumeFromWaitingCompilation: Compilation {(compilationSucceeded ? "succeeded" : "failed")}.");

            // 如果有未完成的 tool_call，补充编译结果作为 tool response
            if (!string.IsNullOrEmpty(interruptedToolCallId))
            {
                bool hasResponse = false;
                for (int i = _messages.Count - 1; i >= 0; i--)
                {
                    if (_messages[i].Role == "tool" && _messages[i].ToolCallId == interruptedToolCallId)
                    {
                        hasResponse = true;
                        break;
                    }
                    if (_messages[i].Role == "assistant") break;
                }

                if (!hasResponse)
                {
                    string compilationResult;
                    if (compilationSucceeded)
                    {
                        compilationResult = "Compilation completed successfully. The script changes have been applied.";
                    }
                    else if (!string.IsNullOrEmpty(compilationErrors))
                    {
                        compilationResult = $"Compilation failed with errors:\n{compilationErrors}\nPlease fix the compilation errors.";
                    }
                    else
                    {
                        compilationResult = "Compilation completed (result unknown). Please verify the script changes.";
                    }

                    _messages.Add(ChatMessage.Tool(interruptedToolCallId, compilationResult));
                    Debug.Log($"[AgentCore] ResumeFromWaitingCompilation: Added compilation result as tool response.");
                }
            }

            // 注入恢复系统消息
            _messages.Add(ChatMessage.System(recoveryMessage));

            // 触发新的 LLM 调用
            TriggerResumeLLMCall();
        }

        /// <summary>
        /// 触发恢复后的 LLM 调用。
        /// 创建新的 assistant 轮次并启动异步 SendMessage 流程。
        /// </summary>
        private void TriggerResumeLLMCall()
        {
            Debug.Log("[AgentCore] TriggerResumeLLMCall: Starting resumed LLM call...");

            // 恢复调用会产生新的 LLM 回复，标记会话内容已变更
            SessionManager.Instance.MarkDirty();

            // 使用 AsyncHelper 在主线程上异步执行恢复调用
            AsyncHelper.RunAsync(
                async () =>
                {
                    // 创建取消令牌
                    _currentCts?.Dispose();
                    _currentCts = new CancellationTokenSource();
                    var ct = _currentCts.Token;

                    // 创建助手轮次（流式输出占位）
                    var assistantTurn = new ConversationTurn("assistant")
                    {
                        IsStreaming = true
                    };
                    _conversationTurns.Add(assistantTurn);

                    try
                    {
                        // 构建工具定义列表
                        var toolDefinitions = BuildToolDefinitions();

                        // P0-2 fix: 使用提取的公共方法，消除与 SendMessageAsync 的代码重复
                        // RunToolCallLoopAsync 内部已包含自动保存逻辑
                        await RunToolCallLoopAsync(assistantTurn, toolDefinitions, ct, " Resume");

                        SetState(AgentState.Idle);
                    }
                    catch (OperationCanceledException)
                    {
                        Debug.Log("[AgentCore] Resume LLM call was cancelled.");
                        assistantTurn.IsStreaming = false;
                        SetState(AgentState.Idle);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[AgentCore] Error during resume LLM call: {ex}");
                        assistantTurn.IsStreaming = false;
                        EmitEvent(AgentEvent.ErrorEvent($"Domain Reload recovery failed: {ex.Message}"));
                        SetState(AgentState.Error);
                        SetState(AgentState.Idle);
                    }
                },
                onError: ex => Debug.LogError($"[AgentCore] TriggerResumeLLMCall error: {ex.Message}")
            );
        }

        #endregion

        #region 资源清理

        /// <summary>
        /// 释放 AgentLoop 持有的所有资源。
        /// 清理 Console 错误捕获器、编译监控器等 IDisposable 资源。
        /// </summary>
        public void Dispose()
        {
            // Domain Reload Resilience: 取消注册 beforeAssemblyReload 事件
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;

            // 取消当前操作
            Cancel();

            // 释放 Console 错误捕获器
            if (_consoleCapture != null)
            {
                _consoleCapture.Dispose();
                _consoleCapture = null;
            }

            // 释放编译监控器
            if (_compilationWatcher != null)
            {
                _compilationWatcher.Dispose();
                _compilationWatcher = null;
            }

            // FallbackRouter 无需 Dispose（无事件订阅）
            _fallbackRouter = null;

            // 释放取消令牌
            if (_currentCts != null)
            {
                _currentCts.Dispose();
                _currentCts = null;
            }

            Debug.Log("[AgentCore] AgentLoop disposed.");
        }

        #endregion
    }
}

