using System;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Core;
using AgentCore.Editor.Utils;
using UnityEditor;

namespace AgentCore.Editor.Session
{
    /// <summary>
    /// 会话自动命名控制器 — 订阅 <see cref="AgentLoop"/> 状态事件，在每一轮对话结束后
    /// debounce 触发一次「智能标题更新」。
    /// <para>
    /// 触发链路：非 Idle → Idle（一轮完成）→ 启动 3 秒 debounce 计时器 → 期间若再次进入
    /// 非 Idle 则取消 → 到期调用 <see cref="SessionAutoTitleService.GenerateTitleAsync"/>
    /// （<c>allowKeep=true</c>，LLM 可返回 KEEP 表示无需更新）。
    /// </para>
    /// <para>
    /// 与 UI 解耦：命中重命名时仅抛出 <see cref="TitleAutoUpdated"/> 事件，由 UI 层自行刷新列表。
    /// StateChanged 与 <see cref="EditorApplication.update"/> 均在主线程触发，无需加锁。
    /// </para>
    /// </summary>
    public sealed class SessionAutoTitleController : IDisposable
    {
        private const string LogPrefix = "[SessionAutoTitle] ";

        /// <summary>debounce 延迟（秒）：Idle 后等待这么久没有新一轮开始才触发。</summary>
        private const double DebounceSeconds = 3.0;

        /// <summary>触发自动命名所需的最小消息数（内容太少不值得命名）。</summary>
        private const int MinMessageCount = 2;

        private readonly AgentLoop _agentLoop;

        /// <summary>是否已 Start（幂等保护）。</summary>
        private bool _started;

        /// <summary>debounce 到期的绝对时间戳（EditorApplication.timeSinceStartup 基准）；&lt;0 表示无挂起计时器。</summary>
        private double _fireAtTime = -1.0;

        /// <summary>上一次观察到的状态，用于判定「非 Idle → Idle」的下降沿。</summary>
        private AgentState _lastState = AgentState.Idle;

        /// <summary>当前进行中的标题生成的取消源；null 表示当前无进行中的生成。</summary>
        private CancellationTokenSource _cts;

        /// <summary>是否有一次标题生成正在进行（重入保护：进行中则跳过新触发，避免抖动 LLM）。</summary>
        private bool _generationInFlight;

        /// <summary>
        /// 当自动命名成功更新了某会话标题时触发，参数为会话 ID。
        /// UI 层订阅此事件以刷新列表，控制器本身不依赖任何 UI 类型。
        /// </summary>
        public event Action<string> TitleAutoUpdated;

        /// <param name="agentLoop">要订阅状态事件的 AgentLoop 实例。</param>
        public SessionAutoTitleController(AgentLoop agentLoop)
        {
            _agentLoop = agentLoop;
        }

        /// <summary>
        /// 启动控制器：订阅 AgentLoop 事件与 EditorApplication.update。幂等（重复调用无副作用）。
        /// </summary>
        public void Start()
        {
            if (_started) return;
            if (_agentLoop == null)
            {
                AgentCoreLog.Warning($"{LogPrefix}Controller Start skipped: AgentLoop is null.");
                return;
            }

            _started = true;
            _lastState = _agentLoop.CurrentState;
            _fireAtTime = -1.0;

            _agentLoop.OnAgentEvent += HandleAgentEvent;
            EditorApplication.update += OnEditorUpdate;
        }

        /// <summary>
        /// 停止控制器：取消订阅、清挂起计时器、取消进行中的生成。幂等（重复调用无副作用）。
        /// </summary>
        public void Stop()
        {
            if (!_started) return;
            _started = false;

            if (_agentLoop != null)
            {
                _agentLoop.OnAgentEvent -= HandleAgentEvent;
            }
            EditorApplication.update -= OnEditorUpdate;

            _fireAtTime = -1.0;

            // 取消可能仍在进行的标题生成
            CancelCurrentGeneration();
        }

        /// <summary>
        /// <see cref="IDisposable"/> 实现，等价于 <see cref="Stop"/>。
        /// </summary>
        public void Dispose()
        {
            Stop();
        }

        /// <summary>
        /// 处理 AgentLoop 事件：仅关心 StateChanged。
        /// - 进入非 Idle：取消挂起的 debounce 计时器（新一轮开始了）。
        /// - 从非 Idle 落到 Idle：启动 / 重启 debounce 计时器。
        /// </summary>
        private void HandleAgentEvent(AgentEvent evt)
        {
            if (evt == null || evt.Type != AgentEventType.StateChanged) return;

            var newState = evt.State;
            var wasNonIdle = _lastState != AgentState.Idle;
            _lastState = newState;

            if (newState != AgentState.Idle)
            {
                // 新一轮开始（或中途状态切换）：取消挂起的触发。
                _fireAtTime = -1.0;
                return;
            }

            // newState == Idle
            if (wasNonIdle)
            {
                // 非 Idle → Idle：一轮完成，启动 debounce 计时器。
                _fireAtTime = EditorApplication.timeSinceStartup + DebounceSeconds;
            }
        }

        /// <summary>
        /// 主线程 tick：检查 debounce 计时器是否到期，到期则触发自动命名。
        /// </summary>
        private void OnEditorUpdate()
        {
            if (_fireAtTime < 0.0) return;
            if (EditorApplication.timeSinceStartup < _fireAtTime) return;

            // 到期：先清计时器（一次性），再触发。
            _fireAtTime = -1.0;
            TriggerAutoTitle();
        }

        /// <summary>
        /// 触发一次自动命名。重入保护：若已有生成在进行则跳过（不排队，避免抖动 LLM）。
        /// 前置校验：当前会话存在、未被手动命名、消息数达到阈值。
        /// </summary>
        private void TriggerAutoTitle()
        {
            // 重入保护：进行中则跳过。
            if (_generationInFlight)
            {
                AgentCoreLog.Info($"{LogPrefix}Auto-title skipped: a generation is already in flight.");
                return;
            }

            var sessionId = SessionManager.Instance?.CurrentSessionId;
            if (string.IsNullOrEmpty(sessionId)) return;

            SessionData session;
            try
            {
                session = SessionStorage.Load(sessionId);
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"{LogPrefix}Auto-title skipped: failed to load session {sessionId}: {ex.Message}");
                return;
            }

            if (session == null)
            {
                return;
            }

            // 尊重用户意图：手动改过标题的会话不自动覆盖。
            if (session.TitleManuallySet)
            {
                return;
            }

            // 内容太少不值得命名（第一条用户消息刚发完）。
            if (session.MessageCount < MinMessageCount)
            {
                return;
            }

            // 取消上一次可能仍挂起的生成，创建新的取消源。
            CancelCurrentGeneration();
            _cts = new CancellationTokenSource();
            _generationInFlight = true;

            RunGenerationAsync(sessionId, session.Title, _cts.Token);
        }

        /// <summary>
        /// 异步执行标题生成并处理结果。fire-and-forget（async void）——
        /// SessionAutoTitleService 内部已处理主线程续体，结果处理仍用 delayCall 兜底确保存储/事件安全。
        /// </summary>
        private async void RunGenerationAsync(string sessionId, string currentTitle, CancellationToken ct)
        {
            string result = null;
            try
            {
                result = await SessionAutoTitleService.GenerateTitleAsync(
                    sessionId, currentTitle: currentTitle, allowKeep: true, ct: ct);
            }
            catch (OperationCanceledException)
            {
                // 被取消（Stop 或新一轮触发）：静默丢弃。
                _generationInFlight = false;
                return;
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"{LogPrefix}Auto-title generation failed for {sessionId}: {ex.Message}");
                _generationInFlight = false;
                return;
            }

            // 若期间已被取消，不落地结果。
            if (ct.IsCancellationRequested)
            {
                _generationInFlight = false;
                return;
            }

            var capturedResult = result;
            EditorApplication.delayCall += () =>
            {
                _generationInFlight = false;

                if (capturedResult == SessionAutoTitleService.KEEP_TITLE_SENTINEL)
                {
                    AgentCoreLog.Info($"{LogPrefix}Auto-title: current title kept for {sessionId}.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(capturedResult))
                {
                    AgentCoreLog.Warning($"{LogPrefix}Auto-title produced no usable title for {sessionId}.");
                    return;
                }

                // 与当前标题相同则无需改动（防御性，SanitizeTitle 后可能巧合相同）。
                if (capturedResult == currentTitle)
                {
                    return;
                }

                var ok = SessionManager.Instance.RenameSession(sessionId, capturedResult, manuallySet: false);
                if (ok)
                {
                    AgentCoreLog.Info($"{LogPrefix}Auto-title updated {sessionId}: \"{capturedResult}\"");
                    try
                    {
                        TitleAutoUpdated?.Invoke(sessionId);
                    }
                    catch (Exception ex)
                    {
                        AgentCoreLog.Warning($"{LogPrefix}TitleAutoUpdated handler threw: {ex.Message}");
                    }
                }
            };
        }

        /// <summary>
        /// 取消并释放当前进行中的 CancellationTokenSource（若有）。
        /// </summary>
        private void CancelCurrentGeneration()
        {
            if (_cts != null)
            {
                try
                {
                    _cts.Cancel();
                }
                catch (ObjectDisposedException) { }
                _cts.Dispose();
                _cts = null;
            }
        }
    }
}
