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
        /// 是否已完成初始化。
        /// UI 层在异步初始化（<see cref="InitializeAsync"/>）尚未完成时据此拦截发送等操作。
        /// </summary>
        public bool IsInitialized => _isInitialized;

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

        /// <summary>工具作用域状态 - 追踪当前会话中 LLM 已激活的 OnDemand 分类（G.3 ActiveToolScope）</summary>
        private ToolScopeState _toolScopeState;

        /// <summary>§3.3 延迟注入内容 — Bootstrap Deferred sections，首轮用户消息时注入</summary>
        private string _deferredContext;

        /// <summary>工具结果压缩器 - 自动压缩过长的工具输出</summary>
        private ToolResultCompressor _toolResultCompressor;

        /// <summary>对话历史压缩器 - 在上下文窗口接近满时压缩旧对话</summary>
        private ConversationCompressor _conversationCompressor;

        /// <summary>压缩统计指标</summary>
        private CompressionMetrics _compressionMetrics;

        /// <summary>可见规划 trace 提取器；仅用于当前 assistant turn 的流式 content 清洗。</summary>
        private readonly VisiblePlanningTraceExtractor _visiblePlanningTraceExtractor = new VisiblePlanningTraceExtractor();

        /// <summary>当前 assistant turn 的 reasoning 计时起点。</summary>
        private DateTime? _reasoningStartedUtc;

        /// <summary>当前 assistant turn 是否正在接收 reasoning / planning trace。</summary>
        private bool _reasoningActive;

        /// <summary>当前 assistant turn 是否已经发出 reasoning 完成事件。</summary>
        private bool _reasoningCompleted;

        /// <summary>当前 assistant turn 的 reasoning 来源。</summary>
        private ThinkingTraceSource _activeReasoningSource = ThinkingTraceSource.None;

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
        /// <param name="confirmationProvider">工具确认提供者；为 null 时使用 fail-safe 自动拒绝语义。</param>
        /// <exception cref="ArgumentNullException">当 llmClient 为 null 时抛出</exception>
        public AgentLoop(ILLMClient llmClient, IToolConfirmationProvider confirmationProvider = null)
        {
            _llmClient = llmClient ?? throw new ArgumentNullException(nameof(llmClient));
            _dispatcher = new ToolCallDispatcher(
                ToolRegistry.Instance,
                confirmationProvider);
        }

        #endregion

        #region 公开方法

        /// <summary>
        /// 异步初始化 Agent Loop（#10 HIGH 性能修复）。
        /// <para>
        /// Bootstrap 上下文经
        /// <see cref="BootstrapLoader.LoadAsync"/> 加载：文件读取用异步 I/O，重量级项目扫描
        /// （脚本统计 / 命名空间分布）经 <c>ProjectContextCollector.CollectHeavyAsync</c> 移出主线程，
        /// 窗口首次打开不再因项目上下文收集而卡顿。
        /// </para>
        /// await 续体回到主线程后再执行 <see cref="CompleteInitialize"/>，其中的 Unity API / 事件注册均在主线程完成。
        /// </summary>
        /// <param name="ct">取消令牌</param>
        public async Task InitializeAsync(CancellationToken ct = default)
        {
            if (!TryBeginInitialize()) return;
            var systemPrompt = await LoadBootstrapSystemPromptAsync(ct);
            CompleteInitialize(systemPrompt);
        }

        /// <summary>
        /// 初始化前置：重入检查 + 工具自动发现。返回 false 表示已初始化，应跳过。
        /// 供 <see cref="InitializeAsync"/> 使用。
        /// </summary>
        private bool TryBeginInitialize()
        {
            if (_isInitialized)
            {
                AgentCoreLog.Warning("[AgentCore] AgentLoop already initialized, skipping.");
                return false;
            }

            // Phase 2.5: 使用 ToolAutoDiscovery 自动发现并注册原生工具
            try
            {
                ToolAutoDiscovery.DiscoverAndRegisterAll();
                AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] ToolAutoDiscovery completed, {ToolRegistry.Instance.Count} tools registered.");
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] ToolAutoDiscovery failed (non-fatal): {ex.Message}");
            }

            return true;
        }

        /// <summary>
        /// 异步加载 Bootstrap 上下文并编译为 system prompt（#10）。加载失败时返回默认 prompt。
        /// </summary>
        private async Task<string> LoadBootstrapSystemPromptAsync(CancellationToken ct)
        {
            try
            {
                var loader = new BootstrapLoader();
                var context = await loader.LoadAsync(ct);
                return CompileBootstrapPrompt(context);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"[AgentCore] Failed to load Bootstrap context: {ex.Message}");
                AgentCoreLog.Warning("[AgentCore] Using default system prompt as fallback.");
                _deferredContext = null;
                return DefaultSystemPrompt;
            }
        }

        /// <summary>
        /// 将 Bootstrap 上下文编译为 system prompt，并保存延迟注入内容。空 prompt 时降级为默认。
        /// </summary>
        private string CompileBootstrapPrompt(BootstrapContext context)
        {
            var systemPrompt = context.CompileSystemPrompt();

            // §3.3: 保存延迟注入内容，首轮用户消息时注入
            _deferredContext = context.CompileDeferredContext();

            if (string.IsNullOrWhiteSpace(systemPrompt))
            {
                AgentCoreLog.Warning("[AgentCore] Bootstrap returned empty system prompt, using default.");
                return DefaultSystemPrompt;
            }

            var deferredInfo = _deferredContext != null
                ? $", deferred ~{context.EstimateDeferredTokenCount()} tokens"
                : "";
            AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] AgentLoop initialized with Bootstrap system prompt (~{context.EstimateTokenCount()} tokens{deferredInfo}).");
            return systemPrompt;
        }

        /// <summary>
        /// 初始化收尾：追加语言指令、写入 system 消息、初始化各子系统、恢复压缩统计、置初始化标志。
        /// 供 <see cref="InitializeAsync"/> 使用；须在主线程调用。
        /// </summary>
        private void CompleteInitialize(string systemPrompt)
        {
            // v1.9.0+: 追加 UI 语言指令到 system prompt (LlmFollowUiLanguage=true 时).
            // 关闭跟随时返回空串, 由模型按用户输入语言自行判断.
            // 语言切换在新会话/重启窗口后生效, 不在运行中会话动态改写 messages[0].
            var langInstruction = AgentCore.Editor.L10n.LanguageManager.GetLlmLanguageInstruction();
            if (!string.IsNullOrEmpty(langInstruction))
            {
                systemPrompt = systemPrompt + "\n\n" + langInstruction;
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
                    AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] Compression metrics restored from DomainReloadState: " +
                              $"{reloadState.TotalTokensSaved} tokens saved.");
                }
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] Failed to restore compression metrics: {ex.Message}");
            }

            // G.3 ActiveToolScope: 初始化工具作用域状态并注入到 RequestToolsTool
            _toolScopeState = new ToolScopeState();
            AgentCore.Editor.Tools.Native.Meta.RequestToolsTool.SetScopeState(_toolScopeState);

            // ADR-18 Skill System: 初始化 Skill 作用域状态并注入到 LoadSkillTool
            InitializeSkillContext();

            _isInitialized = true;

            // v1.14.2: 新项目安装后若用户直接打开 Chat Window（不先经过 Settings 面板），
            // Provider Profile 列表为空，下面对 ActiveModelConfig 的访问会抛
            // InvalidOperationException 导致初始化失败。EnsureDefaultProfileIfEmpty 是
            // 幂等的单一入口（与 ModelAgentSettingsPage 共用），此处仅需确保有 profile 存在，
            // 不负责挑选具体模型名（留空，交由 ModelCapabilityProbe/用户在 Settings 页选择）。
            AgentCore.Editor.Config.AgentCoreProviderProfiles.instance.EnsureDefaultProfileIfEmpty();

            // v1.6.5+: 自适应参数调整 + 异步探测模型能力
            // ApplyAdaptiveDefaults 在初始化时调用一次，确保 reserveResponseTokens 与模型能力匹配
            // ProbeAsync 异步探测 /v1/models → max_model_len，覆盖 ContextWindowManager 硬编码
            AgentCoreSettings.instance.ApplyAdaptiveDefaults();
            _ = ModelCapabilityProbe.ProbeAsync(
                ActiveModelConfig.Endpoint,
                ActiveModelConfig.ApiKey);

            // Phase 3: 会话创建始终延迟到 TryRestoreSession() 中处理。
            // 修复 #6: 之前在 WasInterrupted == false 时会立即创建新会话，
            // 这会覆盖 EditorPrefs 中保存的上一次会话 ID，导致 TryRestoreSession()
            // 无法恢复原会话。现在统一由 TryRestoreSession() → EnsureSessionExists() 负责。
            if (string.IsNullOrEmpty(SessionManager.Instance.CurrentSessionId))
            {
                AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] No active session in Initialize(), deferring to TryRestoreSession().");
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

            // Phase 9: 允许 Idle 或 WaitingForClarification 状态下发送消息
            //   Idle → 正常新一轮
            //   WaitingForClarification → 走 Node A Continuation 模式
            if (CurrentState != AgentState.Idle && CurrentState != AgentState.WaitingForClarification)
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

            // Phase 9: 为本轮 SelfChallenge 准备数据 + 判定是否 Node A 触发
            SetCurrentSelfChallengeTurnId(assistantTurn.Id);
            var selfChallengeData = PrepareSelfChallengeDataForNewTurn(userMessage);
            if (selfChallengeData != null)
            {
                assistantTurn.SelfChallenge = selfChallengeData;
            }

            try
            {
                // 5. 获取配置
                var settings = AgentCoreSettings.instance;

                // Phase 9: 追加 Node A instruction 到 messages 里(作为独立 system message, 位置在 user message 之后)
                var nodeAInstruction = BuildNodeAInstructionForCurrentTurn();
                if (!string.IsNullOrEmpty(nodeAInstruction))
                {
                    _messages.Add(ChatMessage.System(nodeAInstruction));
                    AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore][SelfChallenge] Node A instruction injected (~{nodeAInstruction.Length / 3} tokens estimated).");
                }

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
                                AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] Memory recall: injected {memories.Count} memories into context.");
                            }
                        }
                        else
                        {
                            AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] Memory recall: no relevant memories found.");
                        }
                    }
                    catch (Exception ex)
                    {
                        AgentCoreLog.Warning($"[AgentCore] Memory recall failed (non-blocking): {ex.Message}");
                        // 记忆召回失败不应阻塞对话
                    }
                }

                // ADR-19: 首次让出主线程一次，确保 UI 已渲染 PendingIndicator，
                //   随后进入 HTTP 请求 / 首轮 Cold-Start 快照阶段。
                //   Yield 通过 UnitySynchronizationContext 回到主线程，不改变线程亲和性。
                await System.Threading.Tasks.Task.Yield();

                // §3.3 + §3.6: 会话首轮自动注入 Deferred Context + Workspace 运行时快照
                if (IsFirstUserMessage())
                {
                    // §3.3 Deferred Context: Active Tools List + Decision Tree + PROJECT + Workspace
                    if (!string.IsNullOrEmpty(_deferredContext))
                    {
                        _messages.Insert(_messages.Count - 1, ChatMessage.System(_deferredContext));
                        AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] Deferred context injected for first message (~{_deferredContext.Length / 3} tokens).");
                        _deferredContext = null; // 注入后释放，避免重复注入
                    }

                    // §3.6 Cold-Start Elimination: Workspace 运行时快照
                    try
                    {
                        var snapshot = WorkspaceSnapshotBuilder.Build();
                        if (!string.IsNullOrEmpty(snapshot))
                        {
                            _messages.Insert(_messages.Count - 1, ChatMessage.System(snapshot));
                            AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] Cold-start snapshot injected for first message.");
                        }
                    }
                    catch (Exception ex)
                    {
                        AgentCoreLog.Warning($"[AgentCore] Cold-start snapshot failed (non-blocking): {ex.Message}");
                    }
                }

                // ADR-18: 每轮发送前同步 skill messages 到 _messages 集合
                if (AgentCoreSettings.instance.skillsEnabled)
                {
                    try
                    {
                        SyncSkillMessages();
                    }
                    catch (Exception ex)
                    {
                        AgentCoreLog.Warning($"[AgentCore][Skills] SyncSkillMessages failed (non-blocking): {ex.Message}");
                    }
                }

                // 6. 构建工具定义列表
                var toolDefinitions = BuildToolDefinitions();

                // 7. 工具调用循环（P0-2 fix: 提取为公共方法，消除与 TriggerResumeLLMCall 的代码重复）
                await RunToolCallLoopAsync(assistantTurn, toolDefinitions, ct);

                // 8. 回到 Idle 状态
                //   WaitingForClarification 由 HandleNodeAConclusionForFinalResponse 设置, 不覆盖
                //   ReviewingAnswer 由 HandleFinalResponse Node B 触发设置, 由 TriggerNodeBAsync 完成时恢复, 不覆盖
                if (CurrentState != AgentState.WaitingForClarification &&
                    CurrentState != AgentState.ReviewingAnswer)
                {
                    SetState(AgentState.Idle);
                }
            }
            catch (OperationCanceledException)
            {
                AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] SendMessageAsync was cancelled.");
                assistantTurn.IsStreaming = false;
                SetState(AgentState.Idle);
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"[AgentCore] Error during SendMessageAsync: {ex}");
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
                AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] Cancelling current operation...");
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
            AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] Resetting conversation...");

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
                AgentCoreLog.Warning($"[AgentCore] Failed to save session before reset: {ex.Message}");
            }

            // 2.5. Phase 3: 触发自动记忆（fire-and-forget，不阻塞重置流程）
            try
            {
                SessionManager.Instance.TriggerAutoMemory(_llmClient);
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] Auto-memory trigger failed (non-fatal): {ex.Message}");
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

            // 3.7 ADR-18: 清空 Skill 加载状态（skill message 会随 _messages.Clear() 一并清空）
            ResetSkillContext();

            // 4. 不再通过 EmitEvent 发送 ConversationReset 事件。
            // EmitEvent 使用 EditorApplication.delayCall 延迟执行，会导致 ClearMessages()
            // 在调用方的 RefreshSessionList() 之后才执行，造成 UI 状态混乱。
            // UI 清空的职责由调用方（OnNewSessionClicked）直接调用 ClearMessages() 承担。

            // 5. Phase 3: 创建新会话
            SessionManager.Instance.CreateNewSession();

            // 6. 重新初始化（fire-and-forget：Initialize 已异步化为 InitializeAsync）
            AsyncHelper.RunAsync(
                async () => await InitializeAsync(),
                onError: ex => AgentCore.Editor.Utils.AgentCoreLog.Error($"[AgentCore] Re-initialization after reset failed: {ex.Message}"));
        }

        /// <summary>
        /// 加载指定会话并恢复对话状态。
        /// 供 UI 层调用，用于切换会话。
        /// </summary>
        /// <param name="sessionId">要加载的会话 ID</param>
        /// <returns>是否加载成功</returns>
        public bool LoadSession(string sessionId)
        {
            if (!TryBeginLoadSession(sessionId)) return false;

            // 注意：不在此处调用 ForceSave / TriggerAutoMemory。
            // 保存当前会话的职责由调用方（ChatWindow.SwitchToSession 等）承担，
            // 避免重复保存导致空会话被写入磁盘。

            var session = SessionManager.Instance.LoadSession(sessionId);
            return ApplyLoadedSession(session, sessionId);
        }

        /// <summary>
        /// 加载指定会话并恢复对话状态（异步版本，#1 CRITICAL 性能修复）。
        /// <para>
        /// 与同步 <see cref="LoadSession"/> 行为一致，但会话文件读取经
        /// <see cref="SessionManager.LoadSessionAsync"/> 异步执行，切换会话时不再阻塞主线程
        /// （消除 50–200ms 卡顿）。反序列化后的状态恢复须在主线程完成，由 await 续体保证。
        /// </para>
        /// </summary>
        /// <param name="sessionId">要加载的会话 ID</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>是否加载成功</returns>
        public async Task<bool> LoadSessionAsync(string sessionId, CancellationToken ct = default)
        {
            if (!TryBeginLoadSession(sessionId)) return false;

            // 注意：不在此处调用 ForceSave / TriggerAutoMemory。
            // 保存当前会话的职责由调用方（ChatWindow.SwitchToSession 等）承担。

            var session = await SessionManager.Instance.LoadSessionAsync(sessionId, ct);
            return ApplyLoadedSession(session, sessionId);
        }

        /// <summary>
        /// LoadSession 前置校验：空 Id / Agent 忙检查。返回 false 表示应中止加载。
        /// 供同步 <see cref="LoadSession"/> 与异步 <see cref="LoadSessionAsync"/> 共用。
        /// </summary>
        private bool TryBeginLoadSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                AgentCoreLog.Warning("[AgentCore] Cannot load session with empty Id.");
                return false;
            }

            if (CurrentState != AgentState.Idle)
            {
                AgentCoreLog.Warning("[AgentCore] Cannot load session while agent is busy.");
                return false;
            }

            return true;
        }

        /// <summary>
        /// 将已加载的 <see cref="SessionData"/> 应用到当前对话状态。须在主线程调用。
        /// 供同步 <see cref="LoadSession"/> 与异步 <see cref="LoadSessionAsync"/> 共用。
        /// </summary>
        private bool ApplyLoadedSession(SessionData session, string sessionId)
        {
            if (session == null)
            {
                AgentCoreLog.Warning($"[AgentCore] Failed to load session: {sessionId}");
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
                AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] Restored compression metrics: {_compressionMetrics.TotalCompressionCount} compressions, {_compressionMetrics.TotalTokensSaved} tokens saved");
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

            AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] Session loaded: {sessionId} ({restoredMessages.Count} messages, {restoredTurns.Count} turns)");

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
            using var _gcb = AgentCore.Editor.Utils.AgentCoreProfilerMarkers.GetContextBudget.Auto();

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
            var modelName = ActiveModelConfig.ModelName;
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

        #region Cold-Start 辅助

        /// <summary>
        /// 判断当前是否为会话的首条用户消息。
        /// 检查 _messages 中是否仅有 1 条 user 角色消息（即当前刚添加的这条）。
        /// </summary>
        private bool IsFirstUserMessage()
        {
            int userCount = 0;
            for (int i = 0; i < _messages.Count; i++)
            {
                if (_messages[i].Role == "user")
                {
                    userCount++;
                    if (userCount > 1) return false;
                }
            }
            return userCount == 1;
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

            // ADR-18: 释放 Skill Context（解除事件订阅 + 清空 tool 引用）
            DisposeSkillContext();

            // 释放取消令牌
            if (_currentCts != null)
            {
                _currentCts.Dispose();
                _currentCts = null;
            }

            AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] AgentLoop disposed.");
        }

        #endregion
    }
}
