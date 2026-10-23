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
        /// v1.14.9: 当前会话级 Reasoning Effort 快捷覆盖（chat 面板下拉选择，类似 Codex/Claude Code
        /// 的思考强度切换）。null = 不覆盖，跟随全局/Profile 设置
        /// (<see cref="AgentCore.Editor.Config.ActiveModelConfig.ReasoningEffort"/>)。
        /// 由 <see cref="SetReasoningEffortOverride"/> 设置，随当前会话持久化（见
        /// <see cref="Session.SessionData.ReasoningEffortOverride"/>）。
        /// </summary>
        public string ReasoningEffortOverride { get; private set; }

        /// <summary>
        /// v1.14.10: 设置当前会话的 Reasoning Effort 快捷覆盖（UI 下拉菜单调用入口）。
        /// <para>
        /// 修复此前的断层：<see cref="Session.SessionManager.SetReasoningEffortOverride"/> 只会
        /// 落盘 + 更新 SessionManager 内部的 _currentSession 缓存，不会驱动 AgentLoop 的运行时
        /// <see cref="ReasoningEffortOverride"/> 属性——导致用户选完档位立刻发消息，实际用的还是
        /// 旧值（要等下次 ApplyLoadedSession，即切换会话或重启才会生效）。本方法一次调用同时驱动
        /// 两处同步，UI 层不需要记住"要调两个地方"的顺序。
        /// </para>
        /// </summary>
        /// <param name="level">"auto"/"off"/"low"/"medium"/"high"；null 或 "auto" = 清除覆盖。</param>
        /// <returns>是否成功（当前无活跃会话时返回 false，见
        /// <see cref="Session.SessionManager.SetReasoningEffortOverride"/> 的失败条件）。</returns>
        public bool SetReasoningEffortOverride(string level)
        {
            var sessionId = SessionManager.Instance.CurrentSessionId;
            if (string.IsNullOrEmpty(sessionId))
            {
                AgentCoreLog.Warning("[AgentCore] SetReasoningEffortOverride: no active session, cannot persist override.");
                return false;
            }

            bool persisted = SessionManager.Instance.SetReasoningEffortOverride(sessionId, level);
            if (!persisted) return false;

            // 与 SessionManager 内部的归一化规则保持一致（"auto"/null/空 → null），避免运行时
            // 属性和持久化值出现不一致的两套真相来源。
            var normalized = string.IsNullOrWhiteSpace(level) || level.Trim().ToLowerInvariant() == "auto"
                ? null
                : level.Trim().ToLowerInvariant();
            ReasoningEffortOverride = normalized;

            return true;
        }

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

        /// <summary>
        /// 当前运行（同一时刻至多一个，见 <see cref="ActiveRun"/>）。被取消但尚未收尾的旧 run
        /// 在新 run 登记后即失去当前身份，其后续写入由 <see cref="IsCurrentRun"/> 拦下。
        /// </summary>
        private ActiveRun _activeRun;

        /// <summary>是否已完成初始化</summary>
        private bool _isInitialized;

        /// <summary>
        /// 进行中的初始化 Task（重入保护）：初始化未完成时再次调用 <see cref="InitializeAsync"/>
        /// 直接返回同一个 Task，不会并行跑两遍 CompleteInitialize（重复追加 system 消息 / 重复订阅事件）。
        /// </summary>
        private Task _initTask;

        /// <summary>
        /// 是否已 Dispose。初始化的每个 await 之后都检查：窗口关闭 / 重建期间仍在途的初始化
        /// 不得再订阅全局事件或覆盖新 AgentLoop 注入的静态状态。
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// 最近一次把作用域状态注入到 RequestToolsTool / LoadSkillTool 静态字段的实例。
        /// Dispose 时只有自己仍是持有者才清空，避免旧实例清掉新实例的注入。
        /// </summary>
        private static AgentLoop s_scopeStateOwner;

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

        /// <summary>v1.17.0: 会话内工具顺序稳定器（工具定义处于 prompt 最前端，顺序抖动会让缓存整体失效）。</summary>
        private readonly ToolOrderStabilizer _toolOrderStabilizer = new();

        /// <summary>v1.17.0: 最近一次构建的工具定义（REVISE 重生成复用，保持 tools 前缀不变以命中缓存）。</summary>
        private List<ToolDefinition> _lastToolDefinitions;

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
        public Task InitializeAsync(CancellationToken ct = default)
        {
            if (_disposed)
            {
                AgentCoreLog.Warning("[AgentCore] InitializeAsync called on a disposed AgentLoop, ignored.");
                return Task.CompletedTask;
            }

            // 重入保护：初始化仍在进行中时，复用同一个 Task
            if (_initTask != null && !_initTask.IsCompleted)
            {
                AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] InitializeAsync already in progress, awaiting the existing initialization.");
                return _initTask;
            }

            _initTask = InitializeCoreAsync(ct);
            return _initTask;
        }

        /// <summary>
        /// <see cref="InitializeAsync"/> 的实际实现。每个 await 之后检查 <see cref="_disposed"/>。
        /// </summary>
        private async Task InitializeCoreAsync(CancellationToken ct)
        {
            // [DIAG-BUG1] 整个 InitializeAsync 总耗时 + 各阶段分段耗时，用于定位"打开 Chat 窗口后
            // 长时间初始化很慢"的卡点在哪一步。同时用于 SlowOperationDiagnostics 自动落盘（见下方）。
            var diagSw = System.Diagnostics.Stopwatch.StartNew();
            var diagBreakdown = new System.Text.StringBuilder();
            void DiagStep(string label)
            {
                var line = $"[AgentCore][DIAG] {label}, elapsed={diagSw.ElapsedMilliseconds}ms";
                AgentCoreLog.Warning(line);
                diagBreakdown.AppendLine(line);
            }
            DiagStep("InitializeAsync: START");

            if (!TryBeginInitialize())
            {
                DiagStep("InitializeAsync: TryBeginInitialize returned false (already initialized)");
                return;
            }
            DiagStep("InitializeAsync: TryBeginInitialize (ToolAutoDiscovery) done");

            // v1.14.3: 必须在 CompleteInitialize（内部同步访问 ActiveModelConfig.ModelName）之前
            // 完成，否则 fetch 尚未返回时 modelName 仍为空，请求会带 model="" 被网关拒绝
            // （HTTP 400 "Model name not specified"）。新项目安装后若用户直接打开 Chat Window，
            // 此调用负责补齐 v1.14.2 遗留的空 modelName 缺口。
            // 用 8s 超时包一层：底层 HttpClientFactory 默认超时 300s，若不加限制，网络不通/慢时
            // 会让 Chat Window 打开卡住数分钟。超时后放弃等待、继续初始化——fetch 仍在后台跑，
            // 跑完后台会异步更新 profile.modelName（ActiveModelConfig.ModelName 不缓存，实时读取），
            // 之后的请求即可用上；只有超时窗口内发出的第一条消息可能因 modelName 仍空而失败一次。
            using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
            {
                var ensureTask = AgentCoreProviderProfiles.instance.EnsureDefaultProfileWithAutoModelAsync();
                // v1.15.0: 视觉「从未配置」时自动填默认端点 + 选第一个模型。软状态，失败不阻塞；
                // 与主模型同享 8s 超时窗口（视觉 fetch 同样可能网络慢），不额外延长初始化。
                var ensureVisionTask = VisionModelConfig.EnsureDefaultWithAutoModelAsync();
                var delayTask = Task.Delay(Timeout.Infinite, timeoutCts.Token);
                var winner = await Task.WhenAny(
                    Task.WhenAll(ensureTask, ensureVisionTask), delayTask);
                DiagStep($"InitializeAsync: EnsureDefaultProfile+Vision auto-config race done (winner={(winner == delayTask ? "8s-timeout" : "ensure-done")})");

                // 超时场景下 ensureTask 可能仍在后台跑；即便内部已 try/catch，此处额外吸收一层，
                // 避免 unobserved task exception（不阻塞，不影响本次初始化流程）。
                _ = Task.WhenAll(ensureTask, ensureVisionTask).ContinueWith(t =>
                {
                    if (t.Exception != null)
                        AgentCoreLog.Warning($"[AgentCore] EnsureDefault auto-config background continuation error: {t.Exception.Message}");
                }, TaskScheduler.Default);
            }

            if (_disposed)
            {
                DiagStep("InitializeAsync: AgentLoop disposed during auto-config, abort");
                return;
            }

            // BUG1 预防：LoadBootstrapSystemPromptAsync 内部含磁盘扫描（脚本统计/命名空间分布/
            // 用户文件读取），此前唯一没有超时保护的一段——若磁盘异常慢（网络盘/云同步盘/AV拦截），
            // 会导致初始化无限期挂起，用户看到的"初始化中…"永远不结束、发送按钮永远不可用。
            // 用 5s 超时包一层（正常路径实测远低于此，见诊断包：903ms/233ms/26ms）：超时即放弃、
            // 降级为默认 system prompt，保证初始化流程本身永不真正卡死；用户仍能正常使用
            // （只是首轮缺少项目上下文，可后续手动刷新）。
            string systemPrompt;
            using (var bootstrapTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, bootstrapTimeoutCts.Token))
            {
                try
                {
                    systemPrompt = await LoadBootstrapSystemPromptAsync(linkedCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && bootstrapTimeoutCts.IsCancellationRequested)
                {
                    AgentCoreLog.Error("[AgentCore] Bootstrap loading exceeded 5s timeout — falling back to default system prompt. " +
                        "This usually indicates a slow disk / cloud-synced project folder / antivirus interference. " +
                        "See Library/AgentCore/slow_init_diagnostics.log for details.");
                    _deferredContext = null;
                    systemPrompt = DefaultSystemPrompt;
                }
            }
            DiagStep("InitializeAsync: LoadBootstrapSystemPromptAsync done");

            if (_disposed)
            {
                // 窗口关闭 / AgentLoop 被替换：不再注册全局事件、不再注入静态状态
                DiagStep("InitializeAsync: AgentLoop disposed during bootstrap loading, abort");
                return;
            }

            CompleteInitialize(systemPrompt);
            DiagStep("InitializeAsync: CompleteInitialize done, TOTAL");

            // BUG1 预防：超过 3s 自动落盘一份持久诊断报告（不依赖用户手动导出/碰巧截图）。
            // 阈值选 3s：正常路径通常 <1s（见诊断包实测：903ms/233ms/26ms），3s 足以过滤噪音
            // 又能捕捉到真正的异常慢启动。
            SlowOperationDiagnostics.ReportIfSlow("InitializeAsync", diagSw.ElapsedMilliseconds, 3000, diagBreakdown.ToString());
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
                var diagToolSw = System.Diagnostics.Stopwatch.StartNew();
                ToolAutoDiscovery.DiscoverAndRegisterAll();
                AgentCoreLog.Warning($"[AgentCore][DIAG] ToolAutoDiscovery.DiscoverAndRegisterAll took {diagToolSw.ElapsedMilliseconds}ms, {ToolRegistry.Instance.Count} tools registered.");
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
            // ResetConversation 后会再次进入这里：先释放上一轮的实例（解除 logMessageReceived / 编译事件订阅）
            _consoleCapture?.Dispose();
            _compilationWatcher?.Dispose();
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
            s_scopeStateOwner = this;

            // ADR-18 Skill System: 初始化 Skill 作用域状态并注入到 LoadSkillTool
            InitializeSkillContext();

            _isInitialized = true;

            // v1.14.3: EnsureDefaultProfile 逻辑已上移至 InitializeAsync（await 完成后才进入
            // CompleteInitialize），确保 modelName 在此处已就位，不再在这里重复调用。

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
            // 先 -= 再 +=：ResetConversation 后重新初始化不会重复订阅（否则一次重载会保存两遍）
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
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
        /// <param name="userTurnId">
        /// 可选：预先生成的 user turn ID。用于 Fork 按钮场景——UI 层需要在调用本方法之前
        /// 就把消息气泡的 <c>MessageId</c> 定下来（用户点击发送按钮的瞬间即创建气泡，
        /// 早于本方法内部真正创建 <see cref="ConversationTurn"/>），若不传入同一个 ID，
        /// UI 侧气泡 ID 与真实 turn.Id 会不一致，导致气泡上挂的 Fork 按钮找不到对应的 turn。
        /// 传 null 时内部自动生成新 GUID（原有行为不变）。
        /// </param>
        /// <exception cref="InvalidOperationException">当 Agent 未初始化或非 Idle 状态时抛出</exception>
        /// <exception cref="ArgumentException">当消息为空时抛出</exception>
        public async Task SendMessageAsync(string userMessage, string userTurnId = null, string imageDataUrl = null)
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

            // 2. 登记本次运行（同步完成，早于任何 await）：已有未取消的运行则抛出，杜绝两个 loop 并发写 _messages
            var run = BeginRun("send");
            var ct = run.Token;

            // 3. 添加用户消息到历史
            // v1.16: 用户图支持。图不塞进主模型上下文（base64 是噪音，且主模型多不支持看图）；
            // 改为主模型收到一句短提示去调 vision_analyze source=user_image，工具再从 UserImageStore 读图。
            var hasUserImage = !string.IsNullOrEmpty(imageDataUrl);
            var llmUserContent = hasUserImage
                ? userMessage + "\n\n[AgentCore] 用户本轮上传了一张图像（按钮/粘贴的附件）。如需查看或分析这张图，调用 vision_analyze 工具并设 source=user_image（配合 prompt），视觉模型会描述它。"
                : userMessage;
            _messages.Add(ChatMessage.User(llmUserContent));
            if (hasUserImage)
                AgentCore.Editor.Core.UserImageStore.Set(imageDataUrl);
            var userTurn = new ConversationTurn("user", userMessage)
            {
                // Fork 支持：user turn 在这一刻即完成（不会再有后续消息追加到它身上），
                // 直接记录快照。
                MessageEndIndex = _messages.Count
            };
            if (hasUserImage)
                userTurn.ImageDataUrl = imageDataUrl;
            // UI 层可能在调用本方法之前就已经用同一个 ID 创建了消息气泡（见 userTurnId 参数注释），
            // 这里覆盖掉构造函数生成的默认 GUID，确保气泡 MessageId 与真实 turn.Id 一致。
            if (!string.IsNullOrEmpty(userTurnId))
            {
                userTurn.Id = userTurnId;
            }
            _conversationTurns.Add(userTurn);

            // 检查点：本轮工具写入的原内容记到这条用户消息名下，供"回退到这条消息之前"
            CheckpointStore.BeginTurn(SessionManager.Instance.CurrentSessionId, userTurn.Id);

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

                // 同步置忙：记忆召回是本方法第一个 await，必须在它之前离开 Idle，
                // 否则 UI 的状态闸门在召回期间仍放行，连点发送会并发出第二个 run。
                // （Node A 的 Continuation 判定读的是进入本方法时的状态，已在上方同步完成。）
                SetState(AgentState.Thinking);

                // 5.5 自动记忆召回：搜索与用户消息相关的记忆并注入上下文
                if (settings.mem0Enabled && !string.IsNullOrEmpty(settings.mem0Endpoint))
                {
                    try
                    {
                        // v1.17.0: 不再删除旧记忆消息（会破坏缓存前缀）；FormatMemoriesAsContext 只返回新条目

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

                // 记忆召回的 catch 会吞掉取消；在往 _messages 插入首轮上下文前确认本 run 仍然有效
                ct.ThrowIfCancellationRequested();

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

                // Node B 审阅 / REVISE 重生成属于本 run，等它结束再统一复位状态
                await AwaitRunTailAsync(run);
            }
            catch (OperationCanceledException)
            {
                HandleRunCancelled(run, assistantTurn);
            }
            catch (Exception ex)
            {
                HandleRunFailed(run, assistantTurn, ex, "LLM 对话请求");
            }
            finally
            {
                // 8. 回到 Idle（WaitingForClarification / ask_user 挂起除外，见 EndRun）
                EndRun(run);
            }
        }

        /// <summary>
        /// 是否有未被取消的运行（UI 据此判断忙碌，比单看 <see cref="CurrentState"/> 更早生效）。
        /// </summary>
        public bool IsRunActive => _activeRun != null && !_activeRun.IsCancelled;

        /// <summary>当前运行的取消令牌；无运行时为 <see cref="CancellationToken.None"/>。</summary>
        private CancellationToken CurrentRunToken => _activeRun?.Token ?? CancellationToken.None;

        /// <summary>
        /// 登记一个新运行。必须在调用方的第一个 await 之前同步调用。
        /// 已有未取消的运行时抛出；已取消但仍在收尾的旧运行就此失去当前身份。
        /// </summary>
        private ActiveRun BeginRun(string label)
        {
            if (_activeRun != null && !_activeRun.IsCancelled)
            {
                throw new InvalidOperationException(
                    $"[AgentCore] Cannot start '{label}': run '{_activeRun.Label}' is still active.");
            }

            var run = new ActiveRun(label);
            _activeRun = run;
            return run;
        }

        /// <summary>
        /// 给定令牌是否属于当前运行。旧运行被取消且新运行已登记后返回 false，
        /// 旧运行在 await 之后写 <c>_messages</c> / 改状态前必须先过这一关。
        /// Unity 主线程有 UnitySynchronizationContext，检查与写入之间没有 await 即为原子。
        /// </summary>
        private bool IsCurrentRun(CancellationToken ct)
        {
            return _activeRun != null && _activeRun.Token == ct;
        }

        private static async Task AwaitRunTailAsync(ActiveRun run)
        {
            var tail = run.Tail;
            if (tail != null)
                await tail;
        }

        private void HandleRunCancelled(ActiveRun run, ConversationTurn assistantTurn)
        {
            AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] Run '{run.Label}' was cancelled.");
            assistantTurn.IsStreaming = false;
            // BUG2 fix: 取消发生在 reasoning 流式过程中时必须显式收尾 reasoning 状态，
            // 否则 ThinkingDrawer 的计时器（250ms Every）永久悬空运行。
            // 已被新运行取代时 reasoning 运行时字段属于新运行，不能动。
            if (_activeRun == run)
            {
                CompleteReasoningIfNeeded(assistantTurn);
                // 取消可能停在工具执行中途：补齐缺失的 tool_result，让之后存盘的会话本身就合法
                SanitizeMessageHistory();
            }
        }

        private void HandleRunFailed(ActiveRun run, ConversationTurn assistantTurn, Exception ex, string context)
        {
            AgentCoreLog.Error($"[AgentCore] Error during run '{run.Label}': {ex}");
            assistantTurn.IsStreaming = false;
            if (_activeRun != run)
                return;

            CompleteReasoningIfNeeded(assistantTurn);
            // 发送结构化错误事件（携带异常类型、HTTP 状态码、堆栈等）
            EmitEvent(AgentEvent.ErrorEvent(ex, context));
            SetState(AgentState.Error);
        }

        /// <summary>
        /// 运行收尾（任何路径都经过这里）。仍是当前运行时清空登记并复位状态：
        /// WaitingForClarification（Node A 反问）和 ask_user 挂起是运行的合法终态，保留给 UI；
        /// 其余（含 Node B / REVISE 失败后残留的 Thinking）一律回 Idle。
        /// </summary>
        private void EndRun(ActiveRun run)
        {
            if (_activeRun == run)
            {
                _activeRun = null;
                if (CurrentState != AgentState.WaitingForClarification &&
                    string.IsNullOrEmpty(_pendingUserInputToolCallId))
                {
                    SetState(AgentState.Idle);
                }
            }

            run.Done.TrySetResult(true);
        }

        /// <summary>
        /// 取消当前正在进行的操作。
        /// 如果没有操作在进行中，此方法不执行任何操作。
        /// <para>
        /// 被取消的运行保持登记直到它自己收尾，期间新的发送可以立即开始（见 <see cref="BeginRun"/>）。
        /// </para>
        /// </summary>
        public void Cancel()
        {
            var run = _activeRun;
            if (run != null && !run.IsCancelled)
            {
                AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] Cancelling run '{run.Label}'...");
                run.Cancel();
            }

            // ask_user 挂起时取消 = 放弃提问；否则挂起 id 残留会让下一轮 loop 结束时误判仍在等待
            AbandonPendingUserInput();

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

            // 1. 取消并注销当前操作：历史即将清空，被取消的运行不能再把结果写进新会话
            Cancel();
            _activeRun = null;

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
                SessionManager.Instance.TriggerAutoMemory(CompressionLLMClientFactory.CreateCompressionClient());
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] Auto-memory trigger failed (non-fatal): {ex.Message}");
            }

            // 3. 清除历史
            _messages.Clear();
            _conversationTurns.Clear();
            _isInitialized = false;

            // v1.14.9(修正 v2): 新会话必须清空"假完成恢复"状态，否则旧会话遗留的
            // _blankRecoveryAttempted=true 会让新会话第一轮的真实故障被跳过拦截。
            _blankRecoveryAttempted = false;
            _consecutiveSilentRounds = 0;

            // v1.14.9: 新会话的 Reasoning Effort 覆盖回到 auto（null），不继承上一个会话的选择——
            // 与 Codex/Claude Code 的"新对话默认强度"行为一致，避免用户忘记自己上一轮开了 high。
            ReasoningEffortOverride = null;

            // 3.5 Phase 4.5: 清空文件变更追踪器和持久化数据
            _fileChangeTracker?.Clear();
            DomainReloadState.instance.ClearFileChangeRecords();

            // 3.6 Phase 5: 清空压缩统计数据
            _compressionMetrics?.Reset();
            DomainReloadState.instance.ClearCompressionMetrics();

            // 3.7 ADR-18: 清空 Skill 加载状态（skill message 会随 _messages.Clear() 一并清空）
            ResetSkillContext();
            _toolOrderStabilizer.Reset();
            _lastToolDefinitions = null;

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
        /// 把对话回退到某条用户消息之前：删除该消息及之后的所有消息与轮次（原地截断当前会话）。
        /// 截断点取该用户消息在 <c>_messages</c> 中的实际位置（按 <see cref="ConversationTurn.MessageEndIndex"/> 定位，
        /// 再核对角色与内容），之前的历史天然完整，不会留下没有结果的 tool_calls。
        /// </summary>
        /// <param name="userTurnId">用户消息的 turn.Id（即气泡 MessageId）。</param>
        /// <param name="userText">输出：被回退的用户消息原文，供 UI 放回输入框。</param>
        /// <returns>找不到可靠的截断点或 Agent 非空闲时返回 false，历史不变。</returns>
        public bool TruncateBeforeTurn(string userTurnId, out string userText)
        {
            userText = null;
            if (CurrentState != AgentState.Idle || string.IsNullOrEmpty(userTurnId))
                return false;

            int turnIndex = _conversationTurns.FindIndex(t => t.Id == userTurnId);
            if (turnIndex < 0 || _conversationTurns[turnIndex].Role != "user")
                return false;

            var turn = _conversationTurns[turnIndex];
            int cut = -1;
            int hint = turn.MessageEndIndex > 0 ? Math.Min(turn.MessageEndIndex, _messages.Count) - 1 : _messages.Count - 1;
            // MessageEndIndex 之后可能有 system 消息插在它前面（记忆 / 技能注入），所以从提示位置向后再向前找
            for (int i = hint; i < _messages.Count && cut < 0; i++)
            {
                if (IsUserMessageFor(_messages[i], turn))
                    cut = i;
            }
            for (int i = Math.Min(hint, _messages.Count - 1); i >= 0 && cut < 0; i--)
            {
                if (IsUserMessageFor(_messages[i], turn))
                    cut = i;
            }
            if (cut < 0)
            {
                AgentCoreLog.Warning($"[AgentCore] Rewind: user message for turn {userTurnId} not found in history.");
                return false;
            }

            userText = turn.Content;
            _messages.RemoveRange(cut, _messages.Count - cut);
            _conversationTurns.RemoveRange(turnIndex, _conversationTurns.Count - turnIndex);
            SessionManager.Instance.MarkDirty();
            AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] Rewound conversation to before turn {userTurnId} ({_messages.Count} messages kept).");
            return true;
        }

        private static bool IsUserMessageFor(ChatMessage message, ConversationTurn turn)
        {
            return message.Role == "user" && message.Content != null && !string.IsNullOrEmpty(turn.Content) &&
                   message.Content.StartsWith(turn.Content, StringComparison.Ordinal);
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

            // Bug 2 修复 (2026-08-04): 会话切换路径此前只检查 CurrentState==Idle，
            // 没检查 _isInitialized —— 但 CurrentState 在构造函数里就已默认是 Idle，
            // 而 _isInitialized 及其依赖字段（如 _compressionMetrics）要等 InitializeAsync()
            // 异步链跑完才就位。这两个状态之间存在一段"看起来空闲但其实没初始化完"的窗口。
            // 若用户在此窗口内触发会话切换（网络慢/Bootstrap加载慢时更容易撞上），会进入
            // ApplyLoadedSession 对着仍是 null 的 _compressionMetrics 调用实例方法，
            // 抛 NullReferenceException，导致会话切换半途失败、UI 卡在错误状态。
            // 对齐发消息路径（TryBeginSendMessage）已有的同款检查，补齐这道防线。
            if (!_isInitialized)
            {
                AgentCoreLog.Warning("[AgentCore] Cannot load session while AgentLoop is not yet initialized. Wait for initialization to complete.");
                return false;
            }

            if (CurrentState != AgentState.Idle || IsRunActive)
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

            // 恢复对话状态。注销残留的已取消运行，防止它收尾时把旧会话的工具结果写进新会话
            if (_activeRun != null && _activeRun.IsCancelled)
                _activeRun = null;
            _messages.Clear();
            _conversationTurns.Clear();
            _fileChangeTracker?.Clear();

            // v1.14.9(修正 v2): 切换/加载会话必须清空"假完成恢复"状态。现在触发条件是
            // "单次空回复即拦截"，不再依赖历史里上一条消息是否也是空的，所以这里不需要
            // 按恢复后的历史回填任何东西——无条件清零即可，任何会话加载完成后的第一次
            // 空回复都会被立刻拦截。
            _blankRecoveryAttempted = false;
            _consecutiveSilentRounds = 0;

            // v1.14.9: 还原该会话保存的 Reasoning Effort 覆盖（会话粘滞设计——用户在这个会话里
            // 选过 high，切走再切回来应该还是 high，不会静默丢失选择）。
            ReasoningEffortOverride = session.ReasoningEffortOverride;

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
            if (_disposed) return;
            // 先置标志：仍在途的 InitializeAsync 在下一个 await 之后看到它即放弃，不再订阅事件
            _disposed = true;

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

            // G.3: 只有自己仍是静态作用域状态的持有者时才清空，不影响已接手的新实例
            if (ReferenceEquals(s_scopeStateOwner, this))
            {
                AgentCore.Editor.Tools.Native.Meta.RequestToolsTool.SetScopeState(null);
                s_scopeStateOwner = null;
            }

            // 取消并注销当前运行
            if (_activeRun != null)
            {
                _activeRun.Cancel();
                _activeRun = null;
            }

            AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] AgentLoop disposed.");
        }

        #endregion
    }
}
