using System;
using System.Collections.Generic;
using UnityEngine;

namespace AgentCore.Editor.Core
{
    /// <summary>
    /// Unity Console 错误自动捕获器
    /// 监听 Application.logMessageReceived，收集错误日志
    /// </summary>
    public class ConsoleErrorCapture : IDisposable
    {
        private readonly List<ErrorInfo> _capturedErrors = new List<ErrorInfo>();
        private readonly object _lock = new object();
        private bool _isCapturing;
        private bool _disposed;

        // 配置
        public bool CaptureWarnings { get; set; } = false;
        public int MaxCapturedErrors { get; set; } = 50;

        /// <summary>
        /// 当前捕获的错误数量
        /// </summary>
        public int CapturedCount
        {
            get { lock (_lock) { return _capturedErrors.Count; } }
        }

        /// <summary>
        /// 是否正在捕获
        /// </summary>
        public bool IsCapturing => _isCapturing;

        /// <summary>
        /// 开始捕获 Console 错误
        /// </summary>
        public void StartCapture()
        {
            if (_isCapturing) return;

            lock (_lock)
            {
                _capturedErrors.Clear();
            }

            Application.logMessageReceived += OnLogMessageReceived;
            _isCapturing = true;
        }

        /// <summary>
        /// 停止捕获并返回收集到的错误
        /// </summary>
        public List<ErrorInfo> StopCapture()
        {
            if (!_isCapturing) return new List<ErrorInfo>();

            Application.logMessageReceived -= OnLogMessageReceived;
            _isCapturing = false;

            lock (_lock)
            {
                var result = new List<ErrorInfo>(_capturedErrors);
                _capturedErrors.Clear();
                return result;
            }
        }

        /// <summary>
        /// 获取当前捕获的错误快照（不停止捕获）
        /// </summary>
        public List<ErrorInfo> GetSnapshot()
        {
            lock (_lock)
            {
                return new List<ErrorInfo>(_capturedErrors);
            }
        }

        /// <summary>
        /// 清除已捕获的错误
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _capturedErrors.Clear();
            }
        }

        /// <summary>
        /// 生成错误报告
        /// </summary>
        public ErrorReport GenerateReport(string context = null)
        {
            var report = new ErrorReport
            {
                Context = context ?? "Console capture"
            };

            lock (_lock)
            {
                foreach (var error in _capturedErrors)
                {
                    report.AddError(error);
                }
            }

            return report;
        }

        private void OnLogMessageReceived(string condition, string stackTrace, LogType type)
        {
            // 只捕获错误级别的日志
            bool shouldCapture = type switch
            {
                LogType.Error => true,
                LogType.Exception => true,
                LogType.Assert => true,
                LogType.Warning => CaptureWarnings,
                _ => false
            };

            if (!shouldCapture) return;

            // 过滤掉 AgentCore 自身的日志，避免循环
            if (condition != null && condition.Contains("[AgentCore]")) return;

            var errorInfo = ErrorInfoCollector.FromConsoleLog(condition, stackTrace, type);

            lock (_lock)
            {
                if (_capturedErrors.Count < MaxCapturedErrors)
                {
                    _capturedErrors.Add(errorInfo);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_isCapturing)
            {
                Application.logMessageReceived -= OnLogMessageReceived;
                _isCapturing = false;
            }

            lock (_lock)
            {
                _capturedErrors.Clear();
            }
        }
    }
}
