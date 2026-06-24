using System;
using AgentCore.Editor.Tools.Safety;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.LLM;
using AgentCore.Editor.Config;
using AgentCore.Editor.Bootstrap;
using AgentCore.Editor.Cloud;
using AgentCore.Editor.Core.Compression;
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
    public partial class AgentLoop : IDisposable
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

        /// <summary>
        /// 文件变更追踪器（只读），供 UI 层在会话恢复时读取变更记录。
        /// </summary>
        public FileChangeTracker FileTracker => _fileChangeTracker;

        /// <summary>
        /// 压缩统计指标（只读），供 UI 层显示压缩信息。
        /// </summary>
        public CompressionMetrics CompressionMetrics => _compressionMetrics;

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

        /// <summary>文件变更追踪器 - 追踪当前会话中工具调用产生的文件变更</summary>
        private FileChangeTracker _fileChangeTracker;

        /// <summary>工具结果压缩器 - 自动压缩过长的工具输出</summary>
        private ToolResultCompressor _toolResultCompressor;

        /// <summary>对话历史压缩器 - 在上下文窗口接近满时压缩旧对话</summary>
        private ConversationCompressor _conversationCompressor;

        /// <summary>压缩统计指标</summary>
        private CompressionMetrics _compressionMetrics;

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
            _dispatcher = new ToolCallDispatcher(
                ToolRegistry.Instance,
                new DialogToolConfirmationProvider());
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

            // Phase 4.5: 初始化文件变更追踪器
            _fileChangeTracker = new FileChangeTracker();

            // Phase 5: 初始化上下文压缩系统
            _compressionMetrics = new CompressionMetrics();
            var compressionClient = CompressionLLMClientFactory.CreateCompressionClient();
            _toolResultCompressor = new ToolResultCompressor(compressionClient, _llmClient, _compressionMetrics);
            _conversationCompressor = new ConversationCompressor(compressionClient, _llmClient, _compressionMetrics);

            // Phase 5: 尝试从 DomainReloadState 恢复压缩统计数据
            try
            {
                var reloadState = DomainReloadState.instance;
                if (reloadState.TotalTokensSaved > 0 ||
                    reloadState.ToolResultCompressionSuccessCount > 0 ||
                    reloadState.ConversationCompressionSuccessCount > 0)
                {
                    // 计算各自节省的 token 数
                    int toolResultTokensSaved = reloadState.ToolResultOriginalTokens > 0
                        ? reloadState.ToolResultOriginalTokens - (reloadState.TotalTokensSaved -
                          (reloadState.ConversationOriginalTokens > 0 ? reloadState.ConversationOriginalTokens : 0))
                        : 0;
                    int conversationTokensSaved = reloadState.TotalTokensSaved - toolResultTokensSaved;

                    _compressionMetrics.RestoreFromPersistence(
                        reloadState.ToolResultCompressionSuccessCount,
                        reloadState.ConversationCompressionSuccessCount,
                        reloadState.ToolResultOriginalTokens,
                        reloadState.ConversationOriginalTokens,
                        toolResultTokensSaved,
                        conversationTokensSaved
                    );
                    Debug.Log($"[AgentCore] Compression metrics restored from DomainReloadState: " +
                              $"{reloadState.TotalTokensSaved} tokens saved.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentCore] Failed to restore compression metrics: {ex.Message}");
            }

            _isInitialized = true;

            // Phase 3: 会话创建始终延迟到 TryRestoreSession() 中处理。
            // 修复 #6: 之前在 WasInterrupted == false 时会立即创建新会话，
            // 这会覆盖 EditorPrefs 中保存的上一次会话 ID，导致 TryRestoreSession()
            // 无法恢复原会话。现在统一由 TryRestoreSession() → EnsureSessionExists() 负责。
            if (string.IsNullOrEmpty(SessionManager.Instance.CurrentSessionId))
            {
                Debug.Log("[AgentCore] No active session in Initialize(), deferring to TryRestoreSession().");
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

                // 发送结构化错误事件（携带异常类型、HTTP 状态码、堆栈等）
                EmitEvent(AgentEvent.ErrorEvent(ex, "LLM 对话请求"));

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
                    new List<ConversationTurn>(_conversationTurns),
                    _compressionMetrics);
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

            // 3.5 Phase 4.5: 清空文件变更追踪器和持久化数据
            _fileChangeTracker?.Clear();
            DomainReloadState.instance.ClearFileChangeRecords();

            // 3.6 Phase 5: 清空压缩统计数据
            _compressionMetrics?.Reset();
            DomainReloadState.instance.ClearCompressionMetrics();

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
            _fileChangeTracker?.Clear();

            var restoredMessages = session.ToMessages();
            var restoredTurns = session.ToConversationTurns();

            _messages.AddRange(restoredMessages);
            _conversationTurns.AddRange(restoredTurns);

            // 恢复压缩统计数据（会话级别）
            if (session.CompressionMetrics != null)
            {
                session.CompressionMetrics.RestoreToCompressionMetrics(_compressionMetrics);
                Debug.Log($"[AgentCore] Restored compression metrics: {_compressionMetrics.TotalCompressionCount} compressions, {_compressionMetrics.TotalTokensSaved} tokens saved");
            }
            else
            {
                // 旧会话没有压缩统计数据，重置为空
                _compressionMetrics.Reset();
            }

            // 修复 #7: 清理恢复的消息历史中不完整的 tool_use/tool_result 配对
            // 防止发送到 LLM API 时因缺少 tool_result 导致 400 错误
            SanitizeMessageHistory();

            // 确保已初始化（如果消息列表中有 system 消息，则认为已初始化）
            if (_messages.Count > 0 && _messages[0].Role == "system")
            {
                _isInitialized = true;
            }

            Debug.Log($"[AgentCore] Session loaded: {sessionId} ({restoredMessages.Count} messages, {restoredTurns.Count} turns)");

            // Phase 4.5: 恢复文件变更追踪数据
            TryRestoreFileChangeTracker();

            // 注意：不在此处发送 ConversationReset 事件。
            // LoadSession 仅负责数据恢复，UI 重建由调用方（如 TryRestoreSession）处理。
            // 如果在此处通过 EmitEvent 发送 ConversationReset，由于 AsyncHelper.RunOnMainThread
            // 使用 EditorApplication.delayCall（延迟执行），ClearMessages 会在调用方的
            // RebuildMessageBubbles 之后才执行，导致重建的 UI 被清除。

            return true;
        }

        /// <summary>
        /// 获取当前上下文预算信息，供 UI 层显示。
        /// </summary>
        /// <returns>上下文预算信息快照</returns>
        public ContextBudgetInfo GetContextBudget()
        {
            if (!_isInitialized || _compressionMetrics == null)
            {
                return new ContextBudgetInfo
                {
                    CurrentTokens = 0,
                    MaxTokens = 0,
                    ReservedTokens = 0,
                    AvailableTokens = 0,
                    UsagePercentage = 0f,
                    ToolResultCompressions = 0,
                    ConversationCompressions = 0,
                    TokensSaved = 0,
                    CompressionRatio = 0f,
                    IsCompressionActive = false,
                    ModelName = "Unknown"
                };
            }

            var settings = AgentCoreSettings.instance;
            var modelName = settings.llmModel;
            var maxTokens = ContextWindowManager.GetModelMaxTokens(modelName);
            var reservedTokens = settings.reserveResponseTokens;

            // 计算当前消息历史的 token 数
            int currentTokens = 0;
            foreach (var msg in _messages)
            {
                currentTokens += TokenCounter.EstimateMessageTokens(msg);
            }

            var availableTokens = maxTokens - reservedTokens - currentTokens;
            var usagePercentage = maxTokens > 0 ? (float)currentTokens / maxTokens : 0f;

            return new ContextBudgetInfo
            {
                CurrentTokens = currentTokens,
                MaxTokens = maxTokens,
                ReservedTokens = reservedTokens,
                AvailableTokens = availableTokens > 0 ? availableTokens : 0,
                UsagePercentage = usagePercentage,
                ToolResultCompressions = _compressionMetrics.ToolResultCompressionSuccessCount,
                ConversationCompressions = _compressionMetrics.ConversationCompressionSuccessCount,
                TokensSaved = _compressionMetrics.TotalTokensSaved,
                CompressionRatio = _compressionMetrics.OverallCompressionRatio,
                IsCompressionActive = _compressionMetrics.TotalCompressionCount > 0,
                ModelName = modelName
            };
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
