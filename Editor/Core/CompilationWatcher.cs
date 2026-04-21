using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace AgentCore.Editor.Core
{
    /// <summary>
    /// 编译监控器 - 监控 Unity 编译过程，收集编译错误
    /// 当 Agent 的工具调用修改了脚本文件时，需要等待编译完成并检查是否有错误
    /// </summary>
    public class CompilationWatcher : IDisposable
    {
        private TaskCompletionSource<ErrorReport> _compilationTcs;
        private readonly List<CompilerMessage> _compilerMessages = new List<CompilerMessage>();
        private readonly object _lock = new object();
        private bool _isWatching;
        private bool _disposed;

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
                Debug.LogWarning("[AgentCore] CompilationWatcher is already watching.");
                return _compilationTcs?.Task ?? Task.FromResult(new ErrorReport { Context = "Already watching" });
            }

            _compilationTcs = new TaskCompletionSource<ErrorReport>();

            lock (_lock)
            {
                _compilerMessages.Clear();
            }

            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            _isWatching = true;

            // 设置超时
            StartTimeoutCheck();

            return _compilationTcs.Task;
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

        private void OnCompilationStarted(object context)
        {
            Debug.Log("[AgentCore] Compilation started, watching for errors...");
        }

        private void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            lock (_lock)
            {
                _compilerMessages.AddRange(messages);
            }
        }

        private void OnCompilationFinished(object context)
        {
            Debug.Log("[AgentCore] Compilation finished, generating error report...");

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

            _compilationTcs?.TrySetResult(report);
        }

        private async void StartTimeoutCheck()
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(CompilationTimeoutSeconds));

                if (_isWatching)
                {
                    Debug.LogWarning($"[AgentCore] Compilation watch timed out after {CompilationTimeoutSeconds}s");
                    Cleanup();

                    var report = new ErrorReport
                    {
                        Context = "Compilation timeout"
                    };
                    report.AddError(new ErrorInfo
                    {
                        Source = "compilation_watcher",
                        Severity = ErrorSeverity.Warning,
                        Message = $"Compilation did not complete within {CompilationTimeoutSeconds} seconds. This may indicate no compilation was triggered, or compilation is taking unusually long."
                    });

                    _compilationTcs?.TrySetResult(report);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AgentCore] Timeout check error: {ex.Message}");
            }
        }

        private void Cleanup()
        {
            if (!_isWatching) return;

            CompilationPipeline.compilationStarted -= OnCompilationStarted;
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompilationFinished;
            _isWatching = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Cleanup();

            lock (_lock)
            {
                _compilerMessages.Clear();
            }

            // 如果还有等待中的 Task，取消它
            _compilationTcs?.TrySetCanceled();
        }
    }
}
