using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Core
{
    /// <summary>
    /// 编译监控器 - 监控 Unity 编译过程，收集编译错误
    /// 当 Agent 的工具调用修改了脚本文件时，需要等待编译完成并检查是否有错误
    /// </summary>
    /// <remarks>
    /// 每次 Watch 分配一个递增的 generation，事件回调与超时回调都捕获自己的 generation，
    /// 执行前与当前值比对：旧 Watch 遗留的超时 / 迟到的编译事件不会去结束新 Watch 的 Task。
    /// 所有退出路径（编译完成 / 超时 / Dispose）都经过 <see cref="Cleanup"/> 退订事件并取消超时计时。
    /// </remarks>
    public class CompilationWatcher : IDisposable
    {
        private TaskCompletionSource<ErrorReport> _compilationTcs;
        private readonly List<CompilerMessage> _compilerMessages = new List<CompilerMessage>();
        private readonly object _lock = new object();
        private bool _isWatching;
        private bool _disposed;

        // 当前 Watch 的代号；每次 Watch / Dispose 自增，旧闭包比对不上即视为过期
        private int _generation;

        // 当前 Watch 订阅的委托（闭包捕获 generation），退订时必须用同一实例
        private Action<object> _startedHandler;
        private Action<object> _finishedHandler;
        private Action<string, CompilerMessage[]> _assemblyFinishedHandler;

        // 当前 Watch 的超时计时取消源
        private CancellationTokenSource _timeoutCts;

        /// <summary>
        /// 编译超时时间（秒）。
        /// 默认 30 秒，对于大多数项目的增量编译已足够。
        /// </summary>
        public float CompilationTimeoutSeconds { get; set; } = 30f;

        /// <summary>
        /// 是否正在监控编译
        /// </summary>
        public bool IsWatching => _isWatching;

        /// <summary>
        /// 开始监控编译。调用后会等待下一次编译完成。
        /// </summary>
        /// <returns>编译完成后的错误报告</returns>
        public Task<ErrorReport> WaitForCompilationAsync()
        {
            if (_isWatching)
            {
                AgentCoreLog.Warning("[AgentCore] CompilationWatcher is already watching.");
                return _compilationTcs?.Task ?? Task.FromResult(new ErrorReport { Context = "Already watching" });
            }

            // 上一次 Watch 的残留（理论上 Cleanup 已处理）一律清掉，再开新代
            Cleanup();

            int generation = ++_generation;
            var tcs = new TaskCompletionSource<ErrorReport>();
            _compilationTcs = tcs;

            lock (_lock)
            {
                _compilerMessages.Clear();
            }

            _startedHandler = _ => OnCompilationStarted(generation);
            _finishedHandler = _ => OnCompilationFinished(generation, tcs);
            _assemblyFinishedHandler = (_, messages) => OnAssemblyCompilationFinished(generation, messages);

            CompilationPipeline.compilationStarted += _startedHandler;
            CompilationPipeline.compilationFinished += _finishedHandler;
            CompilationPipeline.assemblyCompilationFinished += _assemblyFinishedHandler;
            _isWatching = true;

            // 设置超时
            _timeoutCts = new CancellationTokenSource();
            StartTimeoutCheck(generation, tcs, CompilationTimeoutSeconds, _timeoutCts.Token);

            return tcs.Task;
        }

        /// <summary>
        /// 请求 Unity 刷新资产（触发编译）并等待编译完成
        /// </summary>
        public Task<ErrorReport> RefreshAndWaitAsync()
        {
            var task = WaitForCompilationAsync();

            // 在主线程请求刷新
            EditorApplication.delayCall += () =>
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            };

            return task;
        }

        private bool IsCurrent(int generation) => _isWatching && generation == _generation;

        private void OnCompilationStarted(int generation)
        {
            if (!IsCurrent(generation)) return;
            AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] Compilation started, watching for errors...");
        }

        private void OnAssemblyCompilationFinished(int generation, CompilerMessage[] messages)
        {
            if (!IsCurrent(generation) || messages == null) return;
            lock (_lock)
            {
                _compilerMessages.AddRange(messages);
            }
        }

        private void OnCompilationFinished(int generation, TaskCompletionSource<ErrorReport> tcs)
        {
            // 过期 Watch 的迟到回调：不碰当前状态（它的委托本应已退订，这里是兜底）
            if (!IsCurrent(generation)) return;

            AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] Compilation finished, generating error report...");

            Cleanup();

            var report = new ErrorReport
            {
                Context = "Post-compilation check"
            };

            lock (_lock)
            {
                foreach (var msg in _compilerMessages)
                {
                    if (msg.type == CompilerMessageType.Error)
                    {
                        var error = new ErrorInfo
                        {
                            Source = "compiler",
                            Severity = ErrorSeverity.Error,
                            Message = msg.message,
                            FilePath = msg.file,
                            Line = msg.line,
                            Column = msg.column
                        };
                        report.AddError(error);
                    }
                    else if (msg.type == CompilerMessageType.Warning)
                    {
                        // 只记录，不作为错误
                        var warning = new ErrorInfo
                        {
                            Source = "compiler",
                            Severity = ErrorSeverity.Warning,
                            Message = msg.message,
                            FilePath = msg.file,
                            Line = msg.line,
                            Column = msg.column
                        };
                        report.AddError(warning);
                    }
                }
            }

            tcs.TrySetResult(report);
        }

        private async void StartTimeoutCheck(int generation, TaskCompletionSource<ErrorReport> tcs,
            float timeoutSeconds, CancellationToken ct)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                // 本次 Watch 已正常结束 / 被 Dispose，计时作废
                return;
            }

            try
            {
                // 计时期间可能已开始新的 Watch：代号不一致则不得结束新 Watch
                if (ct.IsCancellationRequested || !IsCurrent(generation)) return;

                AgentCoreLog.Warning($"[AgentCore] Compilation watch timed out after {timeoutSeconds}s");
                Cleanup();

                var report = new ErrorReport
                {
                    Context = "Compilation timeout"
                };
                report.AddError(new ErrorInfo
                {
                    Source = "compilation_watcher",
                    Severity = ErrorSeverity.Warning,
                    Message = $"Compilation did not complete within {timeoutSeconds} seconds. This may indicate no compilation was triggered, or compilation is taking unusually long."
                });

                tcs.TrySetResult(report);
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"[AgentCore] Timeout check error: {ex.Message}");
            }
        }

        /// <summary>
        /// 退订当前 Watch 的全部事件并取消超时计时。幂等，所有退出路径都调用。
        /// </summary>
        private void Cleanup()
        {
            if (_startedHandler != null) CompilationPipeline.compilationStarted -= _startedHandler;
            if (_finishedHandler != null) CompilationPipeline.compilationFinished -= _finishedHandler;
            if (_assemblyFinishedHandler != null) CompilationPipeline.assemblyCompilationFinished -= _assemblyFinishedHandler;
            _startedHandler = null;
            _finishedHandler = null;
            _assemblyFinishedHandler = null;

            if (_timeoutCts != null)
            {
                try { _timeoutCts.Cancel(); } catch (ObjectDisposedException) { }
                _timeoutCts.Dispose();
                _timeoutCts = null;
            }

            _isWatching = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Cleanup();
            // 让任何仍在途的旧闭包都比对失败
            _generation++;

            lock (_lock)
            {
                _compilerMessages.Clear();
            }

            // 如果还有等待中的 Task，取消它
            _compilationTcs?.TrySetCanceled();
        }
    }
}
