using System;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.UI.Context
{
    /// <summary>
    /// 采集 Unity Console 最近的 log entries（error/warning 优先）。
    /// 使用反射访问内部 UnityEditor.LogEntries API。
    /// 独立于 ReadConsoleTool 的完整实现，仅取最近 N 条，用于快捷键注入场景。
    /// </summary>
    public static class ConsoleContextCollector
    {
        private static Type _logEntriesType;
        private static Type _logEntryType;
        private static MethodInfo _startGettingEntries;
        private static MethodInfo _endGettingEntries;
        private static MethodInfo _getCount;
        private static MethodInfo _getEntryInternal;
        private static FieldInfo _messageField;
        private static FieldInfo _fileField;
        private static FieldInfo _lineField;
        private static FieldInfo _modeField;
        private static bool _reflectionInitialized;
        private static bool _reflectionFailed;

        /// <summary>
        /// 采集最近的 error/warning。
        /// </summary>
        /// <param name="preferErrorsOnly">true 时只取 error+exception，false 时取 error+warning</param>
        public static ContextIngestResult Collect(bool preferErrorsOnly = false)
        {
            if (!EnsureReflection())
                return ContextIngestResult.OkWithWarning(
                    "Console",
                    "(Console reflection unavailable on this Unity version)",
                    "Console access failed; falling back to no context.");

            var entries = ReadRecentEntries(preferErrorsOnly);
            if (entries == null || entries.Count == 0)
            {
                return ContextIngestResult.OkWithWarning(
                    "Console (empty)",
                    "(no matching console entries)",
                    "No error/warning entries in Console.");
            }

            var label = $"Console: last {entries.Count} {(preferErrorsOnly ? "errors" : "issues")}";
            var sb = new StringBuilder(1024);

            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                sb.Append("[").Append(SeverityLabel(e.Mode)).Append("] ");
                if (!string.IsNullOrEmpty(e.File))
                {
                    sb.Append(e.File);
                    if (e.Line > 0) sb.Append(':').Append(e.Line);
                    sb.Append(' ');
                }
                sb.Append('\n');
                sb.Append(ContextIngestFormatter.TruncateValue(e.Message ?? string.Empty,
                    ContextIngestLimits.ConsoleMessageMaxLength));
                sb.Append('\n');
                if (i < entries.Count - 1) sb.Append('\n');
            }

            return ContextIngestResult.Ok(label, sb.ToString());
        }

        // ---------- 反射初始化 ----------

        private static bool EnsureReflection()
        {
            if (_reflectionInitialized) return !_reflectionFailed;
            _reflectionInitialized = true;

            try
            {
                var editorAsm = typeof(UnityEditor.Editor).Assembly;
                _logEntriesType = editorAsm.GetType("UnityEditor.LogEntries")
                                  ?? editorAsm.GetType("UnityEditorInternal.LogEntries");
                _logEntryType = editorAsm.GetType("UnityEditor.LogEntry")
                                ?? editorAsm.GetType("UnityEditorInternal.LogEntry");
                if (_logEntriesType == null || _logEntryType == null)
                {
                    _reflectionFailed = true;
                    return false;
                }

                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                _startGettingEntries = _logEntriesType.GetMethod("StartGettingEntries", flags);
                _endGettingEntries = _logEntriesType.GetMethod("EndGettingEntries", flags);
                _getCount = _logEntriesType.GetMethod("GetCount", flags);
                _getEntryInternal = _logEntriesType.GetMethod("GetEntryInternal", flags);

                _messageField = _logEntryType.GetField("message",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _fileField = _logEntryType.GetField("file",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _lineField = _logEntryType.GetField("line",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _modeField = _logEntryType.GetField("mode",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (_startGettingEntries == null || _endGettingEntries == null ||
                    _getCount == null || _getEntryInternal == null ||
                    _messageField == null || _modeField == null)
                {
                    _reflectionFailed = true;
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] ConsoleContextCollector reflection init failed: {ex.Message}");
                _reflectionFailed = true;
                return false;
            }
        }

        // ---------- 读取 ----------

        private struct Entry
        {
            public string Message;
            public string File;
            public int Line;
            public int Mode;
        }

        private static System.Collections.Generic.List<Entry> ReadRecentEntries(bool errorsOnly)
        {
            var result = new System.Collections.Generic.List<Entry>();

            try
            {
                _startGettingEntries.Invoke(null, null);
                var total = (int)_getCount.Invoke(null, null);
                if (total == 0)
                {
                    _endGettingEntries.Invoke(null, null);
                    return result;
                }

                var logEntry = Activator.CreateInstance(_logEntryType);

                // 从最新往前扫，取 errorsOnly? error : error+warning
                for (int i = total - 1; i >= 0 && result.Count < ContextIngestLimits.ConsoleMaxEntries; i--)
                {
                    var success = _getEntryInternal.Invoke(null, new object[] { i, logEntry });
                    if (success is bool ok && !ok) continue;

                    var mode = (int)(_modeField.GetValue(logEntry) ?? 0);
                    var severity = ClassifyMode(mode);
                    if (severity == Severity.Log) continue;
                    if (errorsOnly && severity == Severity.Warning) continue;

                    result.Add(new Entry
                    {
                        Message = (string)_messageField.GetValue(logEntry),
                        File = _fileField?.GetValue(logEntry) as string,
                        Line = (int)(_lineField?.GetValue(logEntry) ?? 0),
                        Mode = mode
                    });
                }

                _endGettingEntries.Invoke(null, null);
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] ConsoleContextCollector read failed: {ex.Message}");
                try { _endGettingEntries?.Invoke(null, null); } catch { /* ignore */ }
            }

            // 结果按时间正序（最老在前，最新在后），便于 LLM 理解事件时序
            result.Reverse();
            return result;
        }

        private enum Severity { Log, Warning, Error }

        /// <summary>
        /// 根据 LogEntry.mode 位判断严重级。
        /// Unity 官方未导出常量，采用与 ReadConsoleTool 一致的位标志。
        /// </summary>
        private static Severity ClassifyMode(int mode)
        {
            // 参考 UnityEngine.LogType 与 Console 内部 mode bit 定义：
            //   Error / Exception / Assert / Fatal 位 → Error
            //   ScriptingWarning / Warning 位 → Warning
            //   其他 → Log
            const int errorBits = 0x1 | 0x2 | 0x100 | 0x200 | 0x400 | 0x100000 | 0x800000;
            const int warningBits = 0x8 | 0x200000;

            if ((mode & errorBits) != 0) return Severity.Error;
            if ((mode & warningBits) != 0) return Severity.Warning;
            return Severity.Log;
        }

        private static string SeverityLabel(int mode)
        {
            switch (ClassifyMode(mode))
            {
                case Severity.Error: return "ERROR";
                case Severity.Warning: return "WARN";
                default: return "LOG";
            }
        }
    }
}
