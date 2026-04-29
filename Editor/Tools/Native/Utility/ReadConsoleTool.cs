using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Tools.Native.Utility
{
    /// <summary>
    /// Read Unity Console log entries — errors, warnings, and messages.
    /// Uses reflection to access internal UnityEditor.LogEntries API.
    /// Critical for diagnosing compilation errors and runtime issues.
    /// </summary>
    [AgentTool("read_console",
        Description = "Read Unity Editor Console logs — compilation errors, warnings, and messages. Essential for diagnosing script issues.",
        Category = "Utility",
        RequiresMainThread = true,
        MayModifyScripts = false)]
    public class ReadConsoleTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""get_errors"", ""get_warnings"", ""get_all"", ""get_count"", ""clear""],
                    ""description"": ""Action: get_errors (compilation/runtime errors only), get_warnings (warnings only), get_all (all logs), get_count (count by type), clear (clear console)""
                },
                ""max_entries"": {
                    ""type"": ""integer"",
                    ""description"": ""Maximum number of entries to return (default: 50, max: 200)""
                },
                ""search"": {
                    ""type"": ""string"",
                    ""description"": ""Optional text filter — only return entries containing this text (case-insensitive)""
                }
            },
            ""required"": [""action""]
        }");

        // Cached reflection info
        private static Type _logEntriesType;
        private static Type _logEntryType;
        private static MethodInfo _getCountMethod;
        private static MethodInfo _startGettingEntriesMethod;
        private static MethodInfo _endGettingEntriesMethod;
        private static MethodInfo _getEntryInternalMethod;
        private static FieldInfo _conditionField;
        private static FieldInfo _fileField;
        private static FieldInfo _lineField;
        private static FieldInfo _modeField;
        private static MethodInfo _clearMethod;
        private static bool _reflectionInitialized;
        private static bool _reflectionFailed;

        public ToolMetadata Metadata => new ToolMetadata(
            name: "read_console",
            description: "Read Unity Editor Console logs — compilation errors, warnings, and messages. Essential for diagnosing script issues.",
            category: "Utility",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        public Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ToolResponse response;

            try
            {
                var action = ToolHelpers.GetRequiredString(parameters, "action").ToLowerInvariant();

                switch (action)
                {
                    case "get_errors":
                        response = HandleGetEntries(parameters, LogFilter.Error);
                        break;
                    case "get_warnings":
                        response = HandleGetEntries(parameters, LogFilter.Warning);
                        break;
                    case "get_all":
                        response = HandleGetEntries(parameters, LogFilter.All);
                        break;
                    case "get_count":
                        response = HandleGetCount();
                        break;
                    case "clear":
                        response = HandleClear();
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: get_errors, get_warnings, get_all, get_count, clear");
                        break;
                }
            }
            catch (ArgumentException ex)
            {
                response = ToolResponse.Fail(ex.Message);
            }
            catch (Exception ex)
            {
                response = ToolResponse.Fail($"Unexpected error: {ex.Message}");
            }

            sw.Stop();
            return Task.FromResult(response.ToToolResult(sw.Elapsed.TotalMilliseconds));
        }

        #region Action Handlers

        private ToolResponse HandleGetEntries(JObject parameters, LogFilter filter)
        {
            if (!EnsureReflection())
                return ToolResponse.Fail("Cannot access Unity Console API via reflection. Unity version may be incompatible.");

            int maxEntries = ToolHelpers.GetOptionalInt(parameters, "max_entries", 50);
            maxEntries = Mathf.Clamp(maxEntries, 1, 200);
            string searchText = ToolHelpers.GetOptionalString(parameters, "search");

            var entries = ReadLogEntries(filter, maxEntries, searchText);

            // Also check compilation status
            bool compilationFailed = EditorUtility.scriptCompilationFailed;

            var data = new JObject
            {
                ["compilationFailed"] = compilationFailed,
                ["filter"] = filter.ToString(),
                ["totalConsoleEntries"] = GetTotalCount(),
                ["returnedEntries"] = entries.Count,
                ["maxEntries"] = maxEntries
            };

            if (!string.IsNullOrEmpty(searchText))
                data["searchFilter"] = searchText;

            var entriesArray = new JArray();
            foreach (var entry in entries)
            {
                entriesArray.Add(entry.ToJson());
            }
            data["entries"] = entriesArray;

            string summary;
            if (compilationFailed)
            {
                int errorCount = 0;
                foreach (var e in entries)
                    if (e.Type == ConsoleEntryType.Error || e.Type == ConsoleEntryType.CompilerError)
                        errorCount++;
                summary = $"⚠️ COMPILATION FAILED — {errorCount} error(s) found. {entries.Count} entries returned.";
            }
            else
            {
                summary = $"Console: {entries.Count} entries returned (filter: {filter}).";
            }

            return ToolResponse.OkWithData(data, summary);
        }

        private ToolResponse HandleGetCount()
        {
            if (!EnsureReflection())
                return ToolResponse.Fail("Cannot access Unity Console API via reflection.");

            // Read all entries to count by type
            var allEntries = ReadLogEntries(LogFilter.All, 10000, null);

            int errors = 0, warnings = 0, logs = 0, compilerErrors = 0;
            foreach (var entry in allEntries)
            {
                switch (entry.Type)
                {
                    case ConsoleEntryType.CompilerError:
                        compilerErrors++;
                        break;
                    case ConsoleEntryType.Error:
                        errors++;
                        break;
                    case ConsoleEntryType.Warning:
                        warnings++;
                        break;
                    case ConsoleEntryType.Log:
                        logs++;
                        break;
                }
            }

            var data = new JObject
            {
                ["compilationFailed"] = EditorUtility.scriptCompilationFailed,
                ["totalEntries"] = allEntries.Count,
                ["compilerErrors"] = compilerErrors,
                ["runtimeErrors"] = errors,
                ["warnings"] = warnings,
                ["logs"] = logs
            };

            string summary = EditorUtility.scriptCompilationFailed
                ? $"⚠️ COMPILATION FAILED — {compilerErrors} compiler error(s), {errors} runtime error(s), {warnings} warning(s), {logs} log(s)"
                : $"Console: {compilerErrors + errors} error(s), {warnings} warning(s), {logs} log(s)";

            return ToolResponse.OkWithData(data, summary);
        }

        private ToolResponse HandleClear()
        {
            if (!EnsureReflection())
                return ToolResponse.Fail("Cannot access Unity Console API via reflection.");

            try
            {
                _clearMethod?.Invoke(null, null);
                return ToolResponse.Ok("Console cleared.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Failed to clear console: {ex.Message}");
            }
        }

        #endregion

        #region Log Reading

        private List<ConsoleEntry> ReadLogEntries(LogFilter filter, int maxEntries, string searchText)
        {
            var results = new List<ConsoleEntry>();

            int totalCount = GetTotalCount();
            if (totalCount == 0)
                return results;

            // Create a LogEntry instance for reading
            object logEntry;
            try
            {
                logEntry = Activator.CreateInstance(_logEntryType);
            }
            catch
            {
                return results;
            }

            // Start reading entries
            try
            {
                _startGettingEntriesMethod.Invoke(null, null);

                for (int i = 0; i < totalCount && results.Count < maxEntries; i++)
                {
                    try
                    {
                        // GetEntryInternal(int row, LogEntry outputEntry) -> bool
                        var success = _getEntryInternalMethod.Invoke(null, new object[] { i, logEntry });
                        if (success is bool b && !b)
                            continue;

                        var entry = ExtractEntry(logEntry, i);
                        if (entry == null)
                            continue;

                        // Apply type filter
                        if (!MatchesFilter(entry, filter))
                            continue;

                        // Apply text search filter
                        if (!string.IsNullOrEmpty(searchText))
                        {
                            bool matches = entry.Message?.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0
                                        || entry.File?.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0;
                            if (!matches)
                                continue;
                        }

                        results.Add(entry);
                    }
                    catch
                    {
                        // Skip entries that fail to read
                    }
                }
            }
            finally
            {
                try
                {
                    _endGettingEntriesMethod.Invoke(null, null);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }

            return results;
        }

        private ConsoleEntry ExtractEntry(object logEntry, int index)
        {
            try
            {
                string condition = _conditionField?.GetValue(logEntry) as string ?? "";
                string file = _fileField?.GetValue(logEntry) as string ?? "";
                int line = 0;
                if (_lineField != null)
                {
                    var lineVal = _lineField.GetValue(logEntry);
                    if (lineVal is int l) line = l;
                }

                int mode = 0;
                if (_modeField != null)
                {
                    var modeVal = _modeField.GetValue(logEntry);
                    if (modeVal is int m) mode = m;
                }

                var entryType = ClassifyEntry(mode, condition);

                return new ConsoleEntry
                {
                    Index = index,
                    Message = condition,
                    File = file,
                    Line = line,
                    Mode = mode,
                    Type = entryType
                };
            }
            catch
            {
                return null;
            }
        }

        private static ConsoleEntryType ClassifyEntry(int mode, string condition)
        {
            // Unity LogEntry mode flags (from Unity source):
            // Error = 1 << 0 = 1
            // Assert = 1 << 1 = 2
            // Log = 1 << 2 = 4
            // Fatal = 1 << 4 = 16
            // AssetImportError = 1 << 6 = 64
            // AssetImportWarning = 1 << 7 = 128
            // ScriptingError = 1 << 8 = 256
            // ScriptingWarning = 1 << 9 = 512
            // ScriptingLog = 1 << 10 = 1024
            // ScriptCompileError = 1 << 11 = 2048
            // ScriptCompileWarning = 1 << 12 = 4096
            // StickyError = 1 << 13 = 8192

            // Compiler errors
            if ((mode & (1 << 11)) != 0) // ScriptCompileError = 2048
                return ConsoleEntryType.CompilerError;

            // Compiler warnings
            if ((mode & (1 << 12)) != 0) // ScriptCompileWarning = 4096
                return ConsoleEntryType.CompilerWarning;

            // Runtime/scripting errors
            if ((mode & 1) != 0 || (mode & (1 << 8)) != 0 || (mode & (1 << 4)) != 0 || (mode & (1 << 6)) != 0)
                return ConsoleEntryType.Error;

            // Warnings
            if ((mode & (1 << 9)) != 0 || (mode & (1 << 7)) != 0)
                return ConsoleEntryType.Warning;

            // Fallback: check condition text for common patterns
            if (condition != null)
            {
                if (condition.StartsWith("Assets/") && condition.Contains("error CS"))
                    return ConsoleEntryType.CompilerError;
                if (condition.StartsWith("Assets/") && condition.Contains("warning CS"))
                    return ConsoleEntryType.CompilerWarning;
            }

            return ConsoleEntryType.Log;
        }

        private static bool MatchesFilter(ConsoleEntry entry, LogFilter filter)
        {
            switch (filter)
            {
                case LogFilter.Error:
                    return entry.Type == ConsoleEntryType.Error
                        || entry.Type == ConsoleEntryType.CompilerError;
                case LogFilter.Warning:
                    return entry.Type == ConsoleEntryType.Warning
                        || entry.Type == ConsoleEntryType.CompilerWarning;
                case LogFilter.All:
                default:
                    return true;
            }
        }

        private int GetTotalCount()
        {
            try
            {
                var result = _getCountMethod.Invoke(null, null);
                return result is int count ? count : 0;
            }
            catch
            {
                return 0;
            }
        }

        #endregion

        #region Reflection Setup

        private static bool EnsureReflection()
        {
            if (_reflectionInitialized)
                return !_reflectionFailed;

            _reflectionInitialized = true;

            try
            {
                // Find LogEntries type (internal class in UnityEditor)
                _logEntriesType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.LogEntries");
                if (_logEntriesType == null)
                {
                    // Try alternative name used in some Unity versions
                    _logEntriesType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditorInternal.LogEntries");
                }

                if (_logEntriesType == null)
                {
                    Debug.LogWarning("[AgentCore] ReadConsoleTool: Cannot find LogEntries type.");
                    _reflectionFailed = true;
                    return false;
                }

                // Find LogEntry type
                _logEntryType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.LogEntry");
                if (_logEntryType == null)
                {
                    _logEntryType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditorInternal.LogEntry");
                }

                if (_logEntryType == null)
                {
                    Debug.LogWarning("[AgentCore] ReadConsoleTool: Cannot find LogEntry type.");
                    _reflectionFailed = true;
                    return false;
                }

                // Get methods
                var bindingFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

                _getCountMethod = _logEntriesType.GetMethod("GetCount", bindingFlags);
                _startGettingEntriesMethod = _logEntriesType.GetMethod("StartGettingEntries", bindingFlags);
                _endGettingEntriesMethod = _logEntriesType.GetMethod("EndGettingEntries", bindingFlags);
                _clearMethod = _logEntriesType.GetMethod("Clear", bindingFlags);

                // GetEntryInternal has different signatures across Unity versions
                _getEntryInternalMethod = _logEntriesType.GetMethod("GetEntryInternal", bindingFlags);

                if (_getCountMethod == null || _startGettingEntriesMethod == null ||
                    _endGettingEntriesMethod == null || _getEntryInternalMethod == null)
                {
                    Debug.LogWarning("[AgentCore] ReadConsoleTool: Cannot find required LogEntries methods.");
                    _reflectionFailed = true;
                    return false;
                }

                // Get LogEntry fields
                var fieldFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                _conditionField = _logEntryType.GetField("condition", fieldFlags)
                               ?? _logEntryType.GetField("message", fieldFlags);
                _fileField = _logEntryType.GetField("file", fieldFlags);
                _lineField = _logEntryType.GetField("line", fieldFlags);
                _modeField = _logEntryType.GetField("mode", fieldFlags);

                if (_conditionField == null)
                {
                    Debug.LogWarning("[AgentCore] ReadConsoleTool: Cannot find LogEntry.condition field.");
                    _reflectionFailed = true;
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentCore] ReadConsoleTool reflection init failed: {ex.Message}");
                _reflectionFailed = true;
                return false;
            }
        }

        #endregion

        #region Data Types

        private enum LogFilter
        {
            Error,
            Warning,
            All
        }

        private enum ConsoleEntryType
        {
            Log,
            Warning,
            Error,
            CompilerError,
            CompilerWarning
        }

        private class ConsoleEntry
        {
            public int Index;
            public string Message;
            public string File;
            public int Line;
            public int Mode;
            public ConsoleEntryType Type;

            public JObject ToJson()
            {
                var json = new JObject
                {
                    ["index"] = Index,
                    ["type"] = Type.ToString(),
                    ["message"] = TruncateMessage(Message, 500)
                };

                if (!string.IsNullOrEmpty(File))
                {
                    json["file"] = File;
                    if (Line > 0)
                        json["line"] = Line;
                }

                return json;
            }

            private static string TruncateMessage(string msg, int maxLength)
            {
                if (string.IsNullOrEmpty(msg) || msg.Length <= maxLength)
                    return msg;
                return msg.Substring(0, maxLength) + "... (truncated)";
            }
        }

        #endregion
    }
}
