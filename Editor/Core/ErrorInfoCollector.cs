using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace AgentCore.Editor.Core
{
    /// <summary>
    /// 错误严重级别
    /// </summary>
    public enum ErrorSeverity
    {
        Info,
        Warning,
        Error,
        Fatal
    }

    /// <summary>
    /// 结构化错误信息
    /// </summary>
    public class ErrorInfo
    {
        public string Source { get; set; }          // 错误来源: "compiler", "runtime", "tool", "console"
        public ErrorSeverity Severity { get; set; }
        public string Message { get; set; }
        public string StackTrace { get; set; }
        public string FilePath { get; set; }        // 相关文件路径（如果有）
        public int Line { get; set; }               // 行号（如果有）
        public int Column { get; set; }             // 列号（如果有）
        public DateTime Timestamp { get; set; }
        public Dictionary<string, string> Metadata { get; set; } // 额外元数据

        public ErrorInfo()
        {
            Timestamp = DateTime.UtcNow;
            Metadata = new Dictionary<string, string>();
        }

        /// <summary>
        /// 格式化为 LLM 可读的文本
        /// </summary>
        public string FormatForLLM()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[{Severity}] {Source}: {Message}");
            if (!string.IsNullOrEmpty(FilePath))
            {
                sb.Append($"  File: {FilePath}");
                if (Line > 0) sb.Append($" (Line {Line}");
                if (Column > 0) sb.Append($", Col {Column}");
                if (Line > 0) sb.Append(")");
                sb.AppendLine();
            }
            if (!string.IsNullOrEmpty(StackTrace))
            {
                // 只取前5行堆栈
                var lines = StackTrace.Split('\n');
                var maxLines = Math.Min(lines.Length, 5);
                sb.AppendLine("  Stack (top 5):");
                for (int i = 0; i < maxLines; i++)
                {
                    sb.AppendLine($"    {lines[i].Trim()}");
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// 错误收集报告
    /// </summary>
    public class ErrorReport
    {
        public List<ErrorInfo> Errors { get; private set; } = new List<ErrorInfo>();
        public DateTime CollectedAt { get; set; } = DateTime.UtcNow;
        public string Context { get; set; } // 收集上下文描述

        public bool HasErrors => Errors.Count > 0;
        public bool HasFatalErrors => Errors.Exists(e => e.Severity == ErrorSeverity.Fatal);

        public void AddError(ErrorInfo error)
        {
            Errors.Add(error);
        }

        /// <summary>
        /// 格式化整个报告为 LLM 可读文本
        /// </summary>
        public string FormatForLLM(int maxErrors = 10)
        {
            if (!HasErrors) return "No errors detected.";

            var sb = new StringBuilder();
            sb.AppendLine($"=== Error Report ({Context ?? "unknown context"}) ===");
            sb.AppendLine($"Total errors: {Errors.Count}");

            // 按严重级别排序，Fatal 优先
            var sorted = new List<ErrorInfo>(Errors);
            sorted.Sort((a, b) => b.Severity.CompareTo(a.Severity));

            var count = Math.Min(sorted.Count, maxErrors);
            for (int i = 0; i < count; i++)
            {
                sb.AppendLine($"\n--- Error {i + 1}/{Errors.Count} ---");
                sb.Append(sorted[i].FormatForLLM());
            }

            if (Errors.Count > maxErrors)
            {
                sb.AppendLine($"\n... and {Errors.Count - maxErrors} more errors (truncated)");
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// 错误信息收集器 - 从各种来源收集错误
    /// </summary>
    public static class ErrorInfoCollector
    {
        /// <summary>
        /// 从 Unity Console 日志条目创建 ErrorInfo
        /// </summary>
        public static ErrorInfo FromConsoleLog(string condition, string stackTrace, LogType logType)
        {
            var severity = logType switch
            {
                LogType.Error => ErrorSeverity.Error,
                LogType.Exception => ErrorSeverity.Fatal,
                LogType.Assert => ErrorSeverity.Error,
                LogType.Warning => ErrorSeverity.Warning,
                _ => ErrorSeverity.Info
            };

            var error = new ErrorInfo
            {
                Source = "console",
                Severity = severity,
                Message = condition,
                StackTrace = stackTrace
            };

            // 尝试从消息中解析文件路径和行号
            // 常见格式: "Assets/Scripts/Foo.cs(10,5): error CS1234: ..."
            TryParseFileLocation(condition, error);

            return error;
        }

        /// <summary>
        /// 从编译器错误创建 ErrorInfo
        /// </summary>
        public static ErrorInfo FromCompilerError(string message, string filePath = null, int line = 0, int column = 0)
        {
            return new ErrorInfo
            {
                Source = "compiler",
                Severity = ErrorSeverity.Error,
                Message = message,
                FilePath = filePath,
                Line = line,
                Column = column
            };
        }

        /// <summary>
        /// 从工具执行结果创建 ErrorInfo
        /// </summary>
        public static ErrorInfo FromToolResult(string toolName, string errorMessage, string details = null)
        {
            var error = new ErrorInfo
            {
                Source = "tool",
                Severity = ErrorSeverity.Error,
                Message = $"Tool '{toolName}' failed: {errorMessage}"
            };

            if (!string.IsNullOrEmpty(details))
            {
                error.Metadata["details"] = details;
            }

            error.Metadata["tool_name"] = toolName;
            return error;
        }

        /// <summary>
        /// 从异常创建 ErrorInfo
        /// </summary>
        public static ErrorInfo FromException(Exception ex, string context = null)
        {
            return new ErrorInfo
            {
                Source = context ?? "exception",
                Severity = ErrorSeverity.Fatal,
                Message = ex.Message,
                StackTrace = ex.StackTrace
            };
        }

        /// <summary>
        /// 尝试从错误消息中解析文件位置
        /// </summary>
        private static void TryParseFileLocation(string message, ErrorInfo error)
        {
            if (string.IsNullOrEmpty(message)) return;

            // 匹配: path(line,col): ...
            var match = System.Text.RegularExpressions.Regex.Match(
                message,
                @"^(.+?)\((\d+),(\d+)\):\s*"
            );

            if (match.Success)
            {
                error.FilePath = match.Groups[1].Value;
                if (int.TryParse(match.Groups[2].Value, out int line))
                    error.Line = line;
                if (int.TryParse(match.Groups[3].Value, out int col))
                    error.Column = col;
            }
        }
    }
}
