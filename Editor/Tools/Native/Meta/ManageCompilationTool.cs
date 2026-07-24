using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Core;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Tools.Safety;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace AgentCore.Editor.Tools.Native.Meta
{
    /// <summary>
    /// Manage Unity script compilation lifecycle for the agent (G07, v1.9.4).
    /// Provides query + write actions over <see cref="CompilationPipeline"/>.
    ///
    /// Actions:
    /// - get_status: return whether Editor is currently compiling and the last compile's error summary
    /// - get_last_errors: return cached compiler messages from the most recent compilation (no side effect)
    /// - request_compilation: trigger a script compilation (async — does NOT block, use wait_for_compilation to await)
    /// - wait_for_compilation: block until the next compile finishes (up to timeout_seconds) and return errors
    /// - get_assemblies: list all project assemblies (name / outputPath / sourceFiles / defines / assemblyReferences)
    ///
    /// Why a separate tool (not on manage_editor):
    /// - manage_editor already has 9 actions across 4 categories (state / play / windows / settings)
    /// - Compilation lifecycle deserves its own SOUL description to guide LLM into a request/poll workflow
    /// </summary>
    [AgentTool("manage_compilation",
        Description = "Unity script compilation lifecycle: check if the Editor is compiling, read the last compile's errors, trigger recompilation, wait for compilation to finish, or list assemblies. " +
                      "Actions: get_status (fast, cached — check is_compiling and last error count), " +
                      "get_last_errors (return cached compiler messages from the most recent compile, incl. file/line/column), " +
                      "request_compilation (fire-and-forget CompilationPipeline.RequestScriptCompilation — poll get_status after), " +
                      "wait_for_compilation (block up to timeout_seconds, returns full error/warning report), " +
                      "get_assemblies (list project assemblies via CompilationPipeline.GetAssemblies — filter by assemblies_type=Editor|Player). " +
                      "USE FOR: verifying scripts compiled cleanly after manage_script edits, inspecting asmdef layout, waiting on Editor-driven recompiles. " +
                      "NOT FOR: reading Console log entries (use read_console), refreshing AssetDatabase (use manage_editor:refresh), " +
                      "shell-level msbuild/dotnet (use terminal — this tool only reflects Unity's own CompilationPipeline). " +
                      "ACTIVATE WHEN: user asks 'did it compile', 'is Unity still compiling', 'trigger a recompile', 'list all asmdef', 'why doesn't my script compile'.",
        Category = "Meta",
        RequiresMainThread = true,
        RiskLevel = ToolRiskLevel.Low,
        Capabilities = ToolCapability.ReadProject,
        ReadOnlyActions = new[] { "get_status", "get_last_errors", "get_assemblies" })]
    public class ManageCompilationTool : IAgentTool
    {
        #region Schema

        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""get_status"", ""get_last_errors"", ""request_compilation"", ""wait_for_compilation"", ""get_assemblies""],
                    ""description"": ""Compilation action to perform.""
                },
                ""timeout_seconds"": {
                    ""type"": ""number"",
                    ""description"": ""(wait_for_compilation) Max seconds to wait. Default 30. Set higher for large domain reloads.""
                },
                ""assemblies_type"": {
                    ""type"": ""string"",
                    ""enum"": [""Editor"", ""Player""],
                    ""description"": ""(get_assemblies) Filter to Editor-only or Player-only assemblies. Omit to return both.""
                },
                ""include_source_files"": {
                    ""type"": ""boolean"",
                    ""description"": ""(get_assemblies) Include source file list per assembly. Default false (source lists can be very long).""
                },
                ""include_defines"": {
                    ""type"": ""boolean"",
                    ""description"": ""(get_assemblies) Include compile define constants per assembly. Default true.""
                }
            },
            ""required"": [""action""]
        }");

        #endregion

        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_compilation",
            description: "Unity script compilation lifecycle: status, last errors, request, wait, assemblies.",
            category: "Meta",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        // ─── Static state: cache the most recent compile's messages so get_last_errors is instant ──
        // Populated by an EditorApplication delayCall subscriber (see EnsureSubscribed).
        // v1.9.4 (G07): Watching CompilationPipeline events at the process level — separate from
        // CompilationWatcher (which is per-tool-call and one-shot). This is the "always-on log tail".
        private static readonly object _cacheLock = new object();
        private static readonly List<CompilerMessage> _lastMessages = new List<CompilerMessage>();
        private static bool _subscribed;
        private static DateTime _lastFinishUtc = DateTime.MinValue;
        private static DateTime _lastStartUtc = DateTime.MinValue;

        [InitializeOnLoadMethod]
        private static void EnsureSubscribed()
        {
            if (_subscribed) return;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            _subscribed = true;
        }

        private static void OnCompilationStarted(object context)
        {
            lock (_cacheLock)
            {
                _lastMessages.Clear();
                _lastStartUtc = DateTime.UtcNow;
            }
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            if (messages == null || messages.Length == 0) return;
            lock (_cacheLock)
            {
                _lastMessages.AddRange(messages);
            }
        }

        private static void OnCompilationFinished(object context)
        {
            lock (_cacheLock)
            {
                _lastFinishUtc = DateTime.UtcNow;
            }
        }

        public Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            ToolResponse response;

            try
            {
                var action = ToolHelpers.GetRequiredString(parameters, "action").ToLowerInvariant();

                switch (action)
                {
                    case "get_status":
                        response = HandleGetStatus();
                        break;
                    case "get_last_errors":
                        response = HandleGetLastErrors();
                        break;
                    case "request_compilation":
                        response = HandleRequestCompilation();
                        break;
                    case "wait_for_compilation":
                        // Async wait — but ToolResponse.Ok/Fail returns quickly; we await inline.
                        return HandleWaitForCompilationAsync(parameters, sw);
                    case "get_assemblies":
                        response = HandleGetAssemblies(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: get_status, get_last_errors, request_compilation, wait_for_compilation, get_assemblies");
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

        #region Action handlers

        private ToolResponse HandleGetStatus()
        {
            int errorCount = 0;
            int warningCount = 0;
            DateTime lastFinish;
            DateTime lastStart;
            lock (_cacheLock)
            {
                foreach (var m in _lastMessages)
                {
                    if (m.type == CompilerMessageType.Error) errorCount++;
                    else if (m.type == CompilerMessageType.Warning) warningCount++;
                }
                lastFinish = _lastFinishUtc;
                lastStart = _lastStartUtc;
            }

            var data = new JObject
            {
                ["is_compiling"] = EditorApplication.isCompiling,
                ["is_updating"] = EditorApplication.isUpdating,
                ["error_count"] = errorCount,
                ["warning_count"] = warningCount,
                ["has_errors"] = errorCount > 0,
                ["last_compile_started_utc"] = lastStart == DateTime.MinValue ? null : (JToken)lastStart.ToString("o"),
                ["last_compile_finished_utc"] = lastFinish == DateTime.MinValue ? null : (JToken)lastFinish.ToString("o")
            };

            string summary;
            if (EditorApplication.isCompiling)
            {
                summary = "Editor is currently compiling scripts. Call wait_for_compilation or poll get_status.";
            }
            else if (errorCount > 0)
            {
                summary = $"Last compile: FAILED ({errorCount} error(s), {warningCount} warning(s)). Use get_last_errors for details.";
            }
            else if (lastFinish == DateTime.MinValue)
            {
                summary = "No compilation observed yet in this Editor session (subscription started at [InitializeOnLoad]).";
            }
            else
            {
                summary = $"Last compile: OK ({warningCount} warning(s)) at {lastFinish:o}.";
            }
            return ToolResponse.OkWithData(data, summary);
        }

        private ToolResponse HandleGetLastErrors()
        {
            var errors = new JArray();
            var warnings = new JArray();
            lock (_cacheLock)
            {
                foreach (var m in _lastMessages)
                {
                    var entry = new JObject
                    {
                        ["message"] = m.message,
                        ["file"] = m.file,
                        ["line"] = m.line,
                        ["column"] = m.column,
                        ["type"] = m.type.ToString()
                    };
                    if (m.type == CompilerMessageType.Error) errors.Add(entry);
                    else if (m.type == CompilerMessageType.Warning) warnings.Add(entry);
                }
            }

            var data = new JObject
            {
                ["error_count"] = errors.Count,
                ["warning_count"] = warnings.Count,
                ["errors"] = errors,
                ["warnings"] = warnings
            };
            var summary = errors.Count > 0
                ? $"{errors.Count} error(s), {warnings.Count} warning(s) from last compile."
                : $"No errors from last compile ({warnings.Count} warning(s)).";
            return ToolResponse.OkWithData(data, summary);
        }

        private ToolResponse HandleRequestCompilation()
        {
            // Do NOT wait — return immediately so the agent can poll get_status.
            // v1.4.7 lesson: PlayerSettings changes must be flushed before RequestScriptCompilation
            // (see OptionalComponentManager.cs:301). For a plain user-initiated recompile this doesn't
            // apply, but we still SaveAssets() to flush pending imports first.
            AssetDatabase.SaveAssets();
            CompilationPipeline.RequestScriptCompilation();

            var data = new JObject
            {
                ["requested"] = true,
                ["is_compiling"] = EditorApplication.isCompiling,
                ["note"] = "CompilationPipeline.RequestScriptCompilation() enqueues a recompile. Unity may coalesce with pending recompiles. Poll get_status or call wait_for_compilation to observe completion."
            };
            return ToolResponse.OkWithData(data, "Requested script compilation. Poll get_status or call wait_for_compilation to await completion.");
        }

        private async Task<ToolResult> HandleWaitForCompilationAsync(JObject parameters, Stopwatch sw)
        {
            var timeoutSeconds = ToolHelpers.GetOptionalFloat(parameters, "timeout_seconds", 30f);
            if (timeoutSeconds <= 0) timeoutSeconds = 30f;

            // Fast path: not compiling, return current cached errors.
            if (!EditorApplication.isCompiling)
            {
                var fastData = new JObject
                {
                    ["was_compiling"] = false,
                    ["note"] = "Editor was not compiling when wait_for_compilation was called; returning cached errors from last compile.",
                    ["errors"] = BuildMessagesArray(CompilerMessageType.Error),
                    ["warnings"] = BuildMessagesArray(CompilerMessageType.Warning)
                };
                var fastResp = ToolResponse.OkWithData(fastData, "No active compilation. Returned cached last-compile errors.");
                sw.Stop();
                return fastResp.ToToolResult(sw.Elapsed.TotalMilliseconds);
            }

            // Slow path: use one-shot CompilationWatcher.
            using (var watcher = new CompilationWatcher { CompilationTimeoutSeconds = timeoutSeconds })
            {
                ErrorReport report;
                try
                {
                    report = await watcher.WaitForCompilationAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    return ToolResponse.Fail($"wait_for_compilation failed: {ex.Message}").ToToolResult(sw.Elapsed.TotalMilliseconds);
                }

                var errors = new JArray();
                var warnings = new JArray();
                if (report != null && report.Errors != null)
                {
                    foreach (var e in report.Errors)
                    {
                        var entry = new JObject
                        {
                            ["message"] = e.Message,
                            ["file"] = e.FilePath,
                            ["line"] = e.Line,
                            ["column"] = e.Column,
                            ["source"] = e.Source
                        };
                        if (e.Severity == ErrorSeverity.Error) errors.Add(entry);
                        else warnings.Add(entry);
                    }
                }

                var data = new JObject
                {
                    ["was_compiling"] = true,
                    ["timeout_seconds"] = timeoutSeconds,
                    ["error_count"] = errors.Count,
                    ["warning_count"] = warnings.Count,
                    ["errors"] = errors,
                    ["warnings"] = warnings
                };
                var summary = errors.Count > 0
                    ? $"Compilation finished with {errors.Count} error(s), {warnings.Count} warning(s)."
                    : $"Compilation finished cleanly ({warnings.Count} warning(s)).";
                var resp = ToolResponse.OkWithData(data, summary);
                sw.Stop();
                return resp.ToToolResult(sw.Elapsed.TotalMilliseconds);
            }
        }

        private ToolResponse HandleGetAssemblies(JObject parameters)
        {
            var assemblyKindStr = ToolHelpers.GetOptionalString(parameters, "assemblies_type");
            var includeSourceFiles = ToolHelpers.GetOptionalBool(parameters, "include_source_files", false);
            var includeDefines = ToolHelpers.GetOptionalBool(parameters, "include_defines", true);

            AssembliesType[] kindsToQuery;
            if (string.IsNullOrEmpty(assemblyKindStr))
            {
                kindsToQuery = new[] { AssembliesType.Editor, AssembliesType.Player };
            }
            else if (string.Equals(assemblyKindStr, "Editor", StringComparison.OrdinalIgnoreCase))
            {
                kindsToQuery = new[] { AssembliesType.Editor };
            }
            else if (string.Equals(assemblyKindStr, "Player", StringComparison.OrdinalIgnoreCase))
            {
                kindsToQuery = new[] { AssembliesType.Player };
            }
            else
            {
                return ToolResponse.Fail(
                    $"Invalid 'assemblies_type': '{assemblyKindStr}'. Valid: Editor, Player, or omit for both.");
            }

            var assembliesArray = new JArray();
            var seenNames = new HashSet<string>();
            foreach (var kind in kindsToQuery)
            {
                Assembly[] asms;
                try { asms = CompilationPipeline.GetAssemblies(kind); }
                catch (Exception ex)
                {
                    return ToolResponse.Fail($"CompilationPipeline.GetAssemblies({kind}) failed: {ex.Message}");
                }
                if (asms == null) continue;
                foreach (var a in asms)
                {
                    if (a == null || string.IsNullOrEmpty(a.name)) continue;
                    // Dedup across Editor+Player queries by assembly name
                    if (!seenNames.Add(a.name)) continue;

                    var entry = new JObject
                    {
                        ["name"] = a.name,
                        ["output_path"] = a.outputPath,
                        ["flags"] = a.flags.ToString(),
                        ["assembly_kind"] = kind.ToString(),
                        ["source_file_count"] = a.sourceFiles != null ? a.sourceFiles.Length : 0,
                        ["reference_count"] = a.assemblyReferences != null ? a.assemblyReferences.Length : 0
                    };

                    if (includeDefines && a.defines != null && a.defines.Length > 0)
                    {
                        entry["defines"] = new JArray(a.defines);
                    }
                    if (includeSourceFiles && a.sourceFiles != null)
                    {
                        entry["source_files"] = new JArray(a.sourceFiles);
                    }
                    if (a.assemblyReferences != null && a.assemblyReferences.Length > 0)
                    {
                        var refs = new JArray();
                        foreach (var r in a.assemblyReferences)
                        {
                            if (r != null && !string.IsNullOrEmpty(r.name)) refs.Add(r.name);
                        }
                        entry["references"] = refs;
                    }

                    assembliesArray.Add(entry);
                }
            }

            var data = new JObject
            {
                ["count"] = assembliesArray.Count,
                ["assemblies"] = assembliesArray,
                ["assemblies_type"] = assemblyKindStr ?? "Editor+Player",
                ["include_source_files"] = includeSourceFiles,
                ["include_defines"] = includeDefines
            };
            return ToolResponse.OkWithData(data, $"Listed {assembliesArray.Count} project assembly/assemblies.");
        }

        #endregion

        #region Helpers

        private static JArray BuildMessagesArray(CompilerMessageType filterType)
        {
            var arr = new JArray();
            lock (_cacheLock)
            {
                foreach (var m in _lastMessages)
                {
                    if (m.type != filterType) continue;
                    arr.Add(new JObject
                    {
                        ["message"] = m.message,
                        ["file"] = m.file,
                        ["line"] = m.line,
                        ["column"] = m.column,
                        ["type"] = m.type.ToString()
                    });
                }
            }
            return arr;
        }

        #endregion
    }
}
