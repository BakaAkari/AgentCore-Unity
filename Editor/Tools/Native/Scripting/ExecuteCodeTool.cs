using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Tools.Safety;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Tools.Native.Scripting
{
    /// <summary>
    /// Execute a C# code block inside the Editor via Mono.CSharp.Evaluator.
    /// <para>
    /// Contract (v1.7.27, refined from v1.7.26 rewrite — see CHANGELOG):
    /// - Single entry point, no <c>action</c> parameter. You pass a multi-statement C# block via <c>code</c>.
    /// - Return-value semantics follow Mono.CSharp REPL: if the last thing in your block is an EXPRESSION
    ///   (no trailing semicolon), its value is returned. If the last thing is a statement, no value is returned.
    ///   Example (returns 42):        <c>var x = 40; x + 2</c>
    ///   Example (returns "hello"):   <c>"hello"</c>
    ///   Example (returns nothing):   <c>Debug.Log("ok");</c>
    /// - The <c>__result</c> convention from v1.7.0-v1.7.25 is GONE. Do not use it.
    /// - Compile errors are captured via a <c>StreamReportPrinter</c> bound to a <c>StringWriter</c>
    ///   (see <see cref="TryBuildEvaluator"/>), then split by regex: only lines matching
    ///   <c>error CS&lt;digits&gt;</c> mark the call as a failure; warnings and continuation lines
    ///   (e.g. Unity 2022.3's ~80 CS1685 mscorlib duplicates) are surfaced in <c>data.warnings</c>
    ///   but do NOT block success.
    /// - <c>Debug.Log</c> emitted while the code runs is captured via
    ///   <c>Application.logMessageReceivedThreaded</c> (single subscription — the Threaded variant
    ///   covers main-thread logs too, so subscribing to both channels would double-capture) and
    ///   returned in <c>data.output</c>.
    /// - The Evaluator is built purely via reflection against the built-in Mono.CSharp assembly —
    ///   no asmdef change, no compile-time dependency.
    /// </para>
    /// </summary>
    [AgentTool("execute_code",
        Description = "Execute a multi-statement C# code block inside the Unity Editor. " +
            "Return-value semantics: if the last thing in your block is an expression (no trailing semicolon), its value is returned; " +
            "otherwise nothing is returned. Example returning 42: 'var x = 40; x + 2'. Example returning nothing: 'Debug.Log(\"ok\");'. " +
            "Compile errors are reported as failures (not silent nulls). Debug.Log output during execution is captured in 'output'. " +
            "Supports LINQ, loops, control flow, and any Unity Editor API. " +
            "Use for one-off batch scene edits, hierarchy queries, computed transforms, and ad-hoc Editor API exploration — do not create throwaway .cs files for such tasks. " +
            "Note: RESTRICTED tool — must be activated via request_tools before use. Requires user confirmation.",
        Category = "Scripting",
        Visibility = ToolVisibility.Restricted,
        RequiresMainThread = true,
        MayModifyScripts = false,
        RiskLevel = ToolRiskLevel.CodeExecution,
        Capabilities = ToolCapability.ExecuteCode,
        RequiresConfirmation = true,
        // v1.13+ 白名单反转: execute_code 无 discrete action 字段(自由代码块),用 "*" 整工具级放行。
        // 安全性不依赖白名单机制本身,而是工具自带的 ContainsPlaymodeForbiddenApi 静态代码扫描
        // (拦截 SaveAssets/SaveScene/File.Write 等落盘 API 字面调用,见 execute_code 源码 L640+)。
        PlaymodeRuntimeSafeActions = new[] { "*" })]
    public class ExecuteCodeTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"
{
            ""type"": ""object"",
            ""properties"": {
                ""code"": {
                    ""type"": ""string"",
                    ""description"": ""Multi-statement C# block. If the last item is an expression (no trailing ';'), its value is returned; otherwise nothing is returned. E.g. 'var x = 40; x + 2' returns 42. Compile errors are reported as failures.""
                },
                ""context"": {
                    ""type"": ""string"",
                    ""enum"": [""editor"", ""scene""],
                    ""description"": ""Execution context (default: 'editor'). Purely informational — currently unused by the executor.""
                }
            },
            ""required"": [""code""]
        }");

        /// <summary>
        /// Namespaces auto-imported for every invocation. Kept in sync with the assemblies referenced
        /// in <see cref="ConfigureEvaluator"/>.
        /// <para>
        /// Restored to the full v1.7.0 set in v1.7.27 after the diagnostic classifier was fixed
        /// (see the error/warning split in <see cref="HandleRun"/>). The 5 rounds of CS0433/CS0246/
        /// CS0234 failures were NOT caused by these usings — they were caused by the classifier
        /// treating CS1685 warning continuation lines as errors. With that fixed, the full using
        /// set works. Assembly coverage:
        ///   - System, System.IO, System.Text, System.Text.RegularExpressions, System.Collections.Generic
        ///     → mscorlib.dll + System.dll (both in GetDefaultReferences)
        ///   - System.Linq → System.Core.dll (typeof(Enumerable).Assembly)
        ///   - UnityEngine, UnityEngine.SceneManagement → UnityEngine.CoreModule.dll (typeof(GameObject).Assembly)
        ///   - UnityEditor → UnityEditor.CoreModule.dll (typeof(EditorApplication).Assembly)
        ///   - UnityEditor.SceneManagement → UnityEditor.SceneManagerModule.dll
        ///     (typeof(EditorSceneManager).Assembly) — separate module, needs explicit reference.
        /// </para>
        /// </summary>
        private const string DefaultUsings =
            "using System; using System.IO; using System.Text; using System.Text.RegularExpressions; " +
            "using System.Linq; using System.Collections.Generic; " +
            "using UnityEngine; using UnityEngine.SceneManagement; " +
            "using UnityEditor; using UnityEditor.SceneManagement;";

        /// <summary>
        /// Human-readable list of pre-imported namespaces + return-value semantics + Mono.CSharp
        /// limitations — appended to error messages so the agent can self-correct without guessing.
        /// </summary>
        private const string EnvironmentHint =
            "Return-value: last item returned only if expression WITHOUT trailing ';' (e.g. 'var x=40; x+2' returns 42; 'Debug.Log(\"ok\");' returns nothing). " +
            "Pre-imported: System, System.IO, System.Text, System.Text.RegularExpressions, System.Linq, System.Collections.Generic, UnityEngine, UnityEngine.SceneManagement, UnityEditor, UnityEditor.SceneManagement. " +
            "Assemblies referenced: UnityEngine.CoreModule/AssetBundle/JSONSerialize/ImageConversion/Physics, UnityEditor.CoreModule/SceneManagerModule, System.Core, UnityEngine.UI (if present), URP/HDRP/PostProcessing packages (if present — use fully-qualified names like UnityEngine.Rendering.Universal.Bloom). " +
            "'Object' is ambiguous: qualify as UnityEngine.Object (e.g. UnityEngine.Object.DestroyImmediate(x)). " +
            "Mono.CSharp limits: no async/await, no top-level return, no C#8+ (records/switch expressions/using declarations/target-typed new). Use classic statements.";

        public ToolMetadata Metadata => new ToolMetadata(
            name: "execute_code",
            description: "Execute a multi-statement C# code block via Mono.CSharp.Evaluator. Last expression is the return value.",
            category: "Scripting",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        public Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ToolResponse response;

            try
            {
                response = HandleRun(parameters);
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

        #region Handler

        /// <summary>
        /// Compile and run the agent's C# block. Compile errors are captured from the
        /// StreamReportPrinter's StringWriter sink (see <see cref="TryBuildEvaluator"/>), split
        /// by regex into errors vs warnings, and only true errors mark the call as a failure.
        /// Debug.Log output is captured separately via <c>logMessageReceivedThreaded</c>.
        /// </summary>
        private ToolResponse HandleRun(JObject parameters)
        {
            var code = ToolHelpers.GetRequiredString(parameters, "code");
            var context = ToolHelpers.GetOptionalString(parameters, "context", "editor");

            if (string.IsNullOrWhiteSpace(code))
                return ToolResponse.Fail("Code is empty. Pass a C# block via the 'code' parameter.");

            // Reject legacy 'action' parameter loudly instead of silently ignoring — agents trained
            // on the v1.7.0-v1.7.25 schema still emit it. Same for the '__result =' pattern.
            if (parameters["action"] != null)
            {
                var actionValue = parameters["action"].ToString();
                if (!string.IsNullOrEmpty(actionValue) && !actionValue.Equals("run", StringComparison.OrdinalIgnoreCase))
                {
                    return ToolResponse.Fail(
                        $"The 'action' parameter is no longer supported (was '{actionValue}'). " +
                        "execute_code now has a single entry point. Just pass 'code'. " + EnvironmentHint);
                }
                // action='run' is tolerated silently for one release cycle to ease migration.
            }

            if (ContainsDangerousPattern(code))
                return ToolResponse.Fail(
                    "Code contains potentially dangerous operations (Process.Start, File.Delete, AppDomain.Unload, etc.). " +
                    "Remove the dangerous call before running.");

            // v1.12+ ModifyRuntimeState: Play Mode 中禁止用户代码直接调用落盘/Domain Reload API,
            // 否则会绕过 PlaymodeWriteInterceptor 把运行时脏状态写入磁盘。运行时修改应停留在内存。
            if (UnityEditor.EditorApplication.isPlaying && ContainsPlaymodeForbiddenApi(code, out var forbiddenApi))
                return ToolResponse.Fail(
                    $"Code contains forbidden API '{forbiddenApi}' while in Play Mode. " +
                    "Runtime code execution must not persist to disk or trigger Domain Reload — " +
                    "such changes conflict with runtime-only mutation semantics. " +
                    "Modify in-memory objects directly instead (e.g. component fields, ScriptableObject instances); " +
                    "exit Play Mode first if you need to persist changes.");

            if (!TryBuildEvaluator(out object evaluator, out Type evType, out StringWriter errorSink, out string buildError))
                return ToolResponse.Fail("Failed to initialize Mono.CSharp evaluator: " + buildError);

            var runMethod = evType.GetMethod("Run", new[] { typeof(string) });
            var evaluateMethod = evType.GetMethods()
                .FirstOrDefault(m => m.Name == "Evaluate" && m.GetParameters().Length == 3);

            if (runMethod == null || evaluateMethod == null)
                return ToolResponse.Fail("Mono.CSharp.Evaluator API mismatch: Run/Evaluate methods not found.");

            if (!ConfigureEvaluator(evaluator, evType, runMethod, out string configError))
                return ToolResponse.Fail("Evaluator setup failed: " + configError);

            // Capture Debug.Log output for the duration of the run.
            // logMessageReceivedThreaded fires for messages emitted on ANY thread (including the
            // main thread), so subscribing to it alone gives us complete coverage. The earlier
            // v1.7.26/v1.7.27-dev code also subscribed to the plain `logMessageReceived` — that
            // caused every main-thread log to be captured TWICE (Case B smoke output showed
            // `[Log] SMOKE_B` duplicated). Threaded-only is the correct single subscription.
            var capturedLogs = new List<string>();
            var logLock = new object();
            Application.LogCallback logHandler = (msg, stack, type) =>
            {
                lock (logLock) capturedLogs.Add($"[{type}] {msg}");
            };
            Application.logMessageReceivedThreaded += logHandler;

            string runtimeError = null;
            string parseTailError = null;
            object resultValue = null;
            bool resultSet = false;

            try
            {
                // The 3-arg Evaluate handles both multi-statement blocks and a trailing expression:
                //   int Evaluate(string input, out object result, out bool result_set);
                // If the last item is an expression, result_set=true and result carries the value.
                // If the last item is a statement, result_set=false and result is null.
                // Compile errors go to the StreamReportPrinter we wired into the CompilerContext
                // (captured via `errorSink`, see TryBuildEvaluator) — NOT to Console.Error, which
                // was our v1.7.26 initial mistake.
                var args = new object[] { code, null, null };
                var remaining = evaluateMethod.Invoke(evaluator, args);
                resultValue = args[1];
                resultSet = args[2] is bool b && b;

                // Non-null return from Evaluate means part of the input could not be parsed — this
                // is a compile/parse failure (unbalanced braces, unterminated string, etc.), not a
                // runtime exception. Route it to the compile-error channel below so the response
                // classification stays consistent.
                if (remaining is string tail && !string.IsNullOrWhiteSpace(tail))
                {
                    parseTailError = "Evaluator could not parse the tail of your code: " + tail.Trim();
                }
            }
            catch (Exception ex)
            {
                runtimeError = Unwrap(ex).Message;
            }
            finally
            {
                Application.logMessageReceivedThreaded -= logHandler;
            }

            var sinkText = errorSink?.ToString().Trim() ?? string.Empty;

            // The StreamReportPrinter captures ALL compiler diagnostics: errors (CS0xxx error)
            // AND warnings (CSxxxx warning). In a stock Unity 2022.3 install, Mono.CSharp emits
            // ~80+ CS1685 "predefined type defined multiple times" WARNINGS on every evaluation
            // because the unityjit-win32 mscorlib and the Managed/ mscorlib both define the same
            // BCL types. These warnings DO NOT block compilation — spike + first smoke round
            // proved a clean expression like `var x = 40; x + 2` still returns 42 with warnings
            // present. So we must split the sink: only actual *errors* make the call a failure;
            // warnings are surfaced for reference but never block success.
            //
            // CRITICAL: every CS1685 warning is followed by a continuation line like
            //   "C:\...\System.Core.dll (Location of the symbol related to previous warning)"
            // that contains NEITHER "error CS" NOR "warning CS". Treating such lines as errors
            // (previous "safe default" behaviour) caused EVERY evaluation to be marked FAIL even
            // when the primary diagnostics were all warnings. So the safe default is now the
            // OPPOSITE: only lines that explicitly say `error CS<digits>` count as errors;
            // anything else is a warning-adjacent line and gets grouped with warnings.
            var errorLines = new List<string>();
            var warningLines = new List<string>();
            var errorPattern = new System.Text.RegularExpressions.Regex(@"\berror\s+CS\d+\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            foreach (var line in sinkText.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                if (errorPattern.IsMatch(trimmed))
                    errorLines.Add(trimmed);
                else
                    warningLines.Add(trimmed); // warnings + continuation lines like "(Location of the symbol...)"
            }
            // Parse-tail failures (Evaluate returned a non-null remaining string) are compile-level
            // failures, not runtime exceptions — merge them into errorLines so the response is
            // classified as `compileError`, matching the "compile errors are failures" contract.
            if (!string.IsNullOrEmpty(parseTailError))
                errorLines.Add(parseTailError);
            var compileErrors = string.Join("\n", errorLines);
            var compileWarnings = string.Join("\n", warningLines);

            var data = new JObject { ["context"] = context };
            if (capturedLogs.Count > 0)
                data["output"] = new JArray(capturedLogs);
            if (warningLines.Count > 0)
                data["warnings"] = compileWarnings;

            // Only actual compile *errors* block success. Warnings (CS1685 etc.) are surfaced
            // in data.warnings but do not turn a working evaluation into a failure.
            if (!string.IsNullOrEmpty(compileErrors))
            {
                data["compileError"] = compileErrors;
                data["hint"] = EnvironmentHint;
                return ToolResponse.Fail(
                    "Compile error: " + compileErrors +
                    (capturedLogs.Count > 0 ? " (see 'output' for logs emitted before the failure)" : "") +
                    " " + EnvironmentHint);
            }

            if (!string.IsNullOrEmpty(runtimeError))
            {
                data["error"] = runtimeError;
                data["hint"] = EnvironmentHint;
                return ToolResponse.Fail(
                    "Runtime error: " + runtimeError +
                    (capturedLogs.Count > 0 ? " (see 'output' for logs captured before the error)" : "") +
                    " " + EnvironmentHint);
            }

            if (resultSet)
            {
                if (resultValue != null)
                {
                    data["result"] = FormatResult(resultValue);
                    data["resultType"] = resultValue.GetType().FullName;
                }
                else
                {
                    data["result"] = JValue.CreateNull();
                    data["resultType"] = "null";
                }
                return ToolResponse.OkWithData(data, "Code executed, result captured.");
            }

            // No trailing expression = no return value. This is a NORMAL success case, not an error —
            // e.g. the user wrote `Debug.Log("x");` or a pure loop.
            data["result"] = JValue.CreateNull();
            data["resultType"] = "void";
            return ToolResponse.OkWithData(data,
                "Code executed. No return value (last item was a statement, not an expression). " +
                (capturedLogs.Count > 0
                    ? "See 'output' for captured Debug.Log entries."
                    : "To return a value, end the block with an expression (no trailing ';'). Example: 'var x = 40; x + 2'."));
        }

        #endregion

        #region Evaluator Setup

        /// <summary>
        /// Reference the Unity + BCL assemblies the imported namespaces live in, then apply the
        /// default <c>using</c> block. Called once per fresh Evaluator instance.
        /// </summary>
        private static bool ConfigureEvaluator(object evaluator, Type evType, MethodInfo runMethod, out string error)
        {
            error = null;
            try
            {
                var refAssembly = evType.GetMethod("ReferenceAssembly", new[] { typeof(Assembly) });
                if (refAssembly != null)
                {
                    // Assemblies covering DefaultUsings namespaces. Each assembly is referenced
                    // exactly once — de-dupe via a HashSet keyed on Assembly identity so that
                    // types living in the same DLL (e.g. GameObject and Scene both in
                    // UnityEngine.CoreModule) don't produce CS0433 "type defined in multiple
                    // assemblies" errors.
                    //
                    //   typeof(GameObject).Assembly              = UnityEngine.CoreModule
                    //     covers: UnityEngine, UnityEngine.SceneManagement
                    //   typeof(EditorApplication).Assembly       = UnityEditor.CoreModule
                    //     covers: UnityEditor
                    //   typeof(EditorSceneManager).Assembly      = UnityEditor.SceneManagerModule
                    //     covers: UnityEditor.SceneManagement
                    //   typeof(Enumerable).Assembly              = System.Core
                    //     covers: System.Linq
                    //   typeof(ImageConversion).Assembly         = UnityEngine.ImageConversionModule
                    //     covers: Texture2D.EncodeToPNG / EncodeToJPG / LoadImage extension methods
                    //   typeof(JsonUtility).Assembly             = UnityEngine.JSONSerializeModule
                    //     covers: JsonUtility.ToJson / FromJson (agents often reach for this)
                    //   typeof(AssetBundle).Assembly             = UnityEngine.AssetBundleModule
                    //     covers: AssetBundle load/unload
                    //   typeof(Physics).Assembly                 = UnityEngine.PhysicsModule
                    //     covers: Physics.Raycast, Rigidbody, Colliders
                    //   typeof(UI.Image).Assembly (if present)   = UnityEngine.UI (uGUI package)
                    //     covers: legacy UGUI Image/Text/Button (probed via reflection because UGUI is a package that may not be installed)
                    //
                    // System / System.IO / System.Text / System.Text.RegularExpressions /
                    // System.Collections.Generic all live in mscorlib.dll + System.dll, both of
                    // which Mono.CSharp's GetDefaultReferences() already loads at Evaluator init.
                    var seen = new HashSet<Assembly>();
                    var refs = new List<Assembly>
                    {
                        typeof(GameObject).Assembly,
                        typeof(EditorApplication).Assembly,
                        typeof(UnityEditor.SceneManagement.EditorSceneManager).Assembly,
                        typeof(Enumerable).Assembly,
                        typeof(ImageConversion).Assembly,
                        typeof(JsonUtility).Assembly,
                        typeof(AssetBundle).Assembly,
                        typeof(Physics).Assembly,
                    };
                    // UGUI is an optional package — probe via type-load without hard reference.
                    var ugui = Type.GetType("UnityEngine.UI.Image, UnityEngine.UI", throwOnError: false);
                    if (ugui != null) refs.Add(ugui.Assembly);

                    // Bug Y (v1.11+): probe URP/HDRP/PostProcessing packages so agents can call
                    // Volume/Bloom/Camera post-processing APIs directly without Assembly.Load boilerplate.
                    // Each is optional — probe via Type.GetType so uninstalled packages are silently skipped.
                    // Agents should use fully-qualified names (e.g. UnityEngine.Rendering.Universal.Bloom)
                    // since DefaultUsings intentionally does NOT import these namespaces.
                    var urpProbes = new[]
                    {
                        "UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset, Unity.RenderPipelines.Universal.Runtime",
                        "UnityEngine.Rendering.Volume, Unity.RenderPipelines.Core.Runtime",
                        "UnityEngine.Rendering.HighDefinition.HDRenderPipelineAsset, Unity.RenderPipelines.HighDefinition.Runtime",
                        "UnityEngine.Rendering.PostProcessing.PostProcessVolume, Unity.Postprocessing.Runtime",
                    };
                    foreach (var probe in urpProbes)
                    {
                        var probedType = Type.GetType(probe, throwOnError: false);
                        if (probedType != null) refs.Add(probedType.Assembly);
                    }

                    foreach (var asm in refs)
                    {
                        if (asm != null && seen.Add(asm))
                            refAssembly.Invoke(evaluator, new object[] { asm });
                    }
                }
                runMethod.Invoke(evaluator, new object[] { DefaultUsings });
                return true;
            }
            catch (Exception ex)
            {
                error = Unwrap(ex).Message;
                return false;
            }
        }

        /// <summary>
        /// Build a Mono.CSharp.Evaluator instance purely via reflection. A fresh Evaluator is
        /// constructed per call (perfect variable isolation between calls).
        /// <para>
        /// Wires a <c>StreamReportPrinter(TextWriter)</c> into the CompilerContext so compile
        /// errors flow into <paramref name="errorSink"/> — this is the ONLY reliable way to
        /// capture Mono.CSharp diagnostics. The <c>ConsoleReportPrinter</c> variant we used in
        /// the v1.7.26 first-cut snapshots its <c>TextWriter</c> from <c>Console.Error</c> at
        /// ctor time, so a later <c>Console.SetError</c> can NOT redirect it — we would end up
        /// with a silent success/null on any compile error (Case C smoke failure).
        /// </para>
        /// </summary>
        private static bool TryBuildEvaluator(out object evaluator, out Type evType, out StringWriter errorSink, out string error)
        {
            evaluator = null;
            evType = null;
            errorSink = new StringWriter();
            error = null;
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Mono.CSharp")
                    ?? Assembly.Load("Mono.CSharp");

                evType = asm.GetType("Mono.CSharp.Evaluator");
                var settingsType = asm.GetType("Mono.CSharp.CompilerSettings");
                var ctxType = asm.GetType("Mono.CSharp.CompilerContext");
                var reportType = asm.GetType("Mono.CSharp.Report");
                var streamPrinterType = asm.GetType("Mono.CSharp.StreamReportPrinter");

                if (evType == null || settingsType == null || ctxType == null || streamPrinterType == null)
                {
                    error = "core Mono.CSharp types missing " +
                        $"(Evaluator={evType!=null}, Settings={settingsType!=null}, Context={ctxType!=null}, StreamPrinter={streamPrinterType!=null}).";
                    return false;
                }

                var settings = Activator.CreateInstance(settingsType);

                // Bind our sink into a StreamReportPrinter. Public ctor signature in Mono.CSharp
                // is StreamReportPrinter(TextWriter). Search flexibly so we don't break across
                // Mono versions (which sometimes flip parameter order or add a bool).
                object printer = null;
                foreach (var pc in streamPrinterType.GetConstructors())
                {
                    var pp = pc.GetParameters();
                    if (pp.Length >= 1 && typeof(TextWriter).IsAssignableFrom(pp[0].ParameterType))
                    {
                        var callArgs = new object[pp.Length];
                        callArgs[0] = errorSink;
                        for (int i = 1; i < pp.Length; i++)
                            callArgs[i] = pp[i].HasDefaultValue ? pp[i].DefaultValue :
                                (pp[i].ParameterType.IsValueType ? Activator.CreateInstance(pp[i].ParameterType) : null);
                        printer = pc.Invoke(callArgs);
                        break;
                    }
                }
                if (printer == null)
                {
                    error = "StreamReportPrinter(TextWriter) ctor not found.";
                    return false;
                }

                // CompilerContext ctor: (CompilerSettings, ReportPrinter) [older]
                //                    or (CompilerSettings, Report)            [newer]
                object ctx = null;
                foreach (var c in ctxType.GetConstructors())
                {
                    var ps = c.GetParameters();
                    if (ps.Length != 2 || ps[0].ParameterType != settingsType) continue;

                    if (ps[1].ParameterType.IsInstanceOfType(printer))
                    {
                        ctx = c.Invoke(new[] { settings, printer });
                        break;
                    }
                    if (reportType != null && ps[1].ParameterType == reportType)
                    {
                        var rc = reportType.GetConstructors()
                            .FirstOrDefault(x => x.GetParameters().Length == 1);
                        if (rc != null)
                        {
                            ctx = c.Invoke(new[] { settings, rc.Invoke(new[] { printer }) });
                            break;
                        }
                    }
                }

                if (ctx == null)
                {
                    error = "could not construct CompilerContext (no matching ctor).";
                    return false;
                }

                var evCtor = evType.GetConstructors().FirstOrDefault(
                    c => c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType == ctxType);
                if (evCtor == null)
                {
                    error = "no Evaluator(CompilerContext) ctor found.";
                    return false;
                }

                evaluator = evCtor.Invoke(new[] { ctx });
                return true;
            }
            catch (Exception ex)
            {
                error = Unwrap(ex).Message;
                return false;
            }
        }

        /// <summary>
        /// Unwrap nested TargetInvocationException / reflection exceptions to the real root cause.
        /// </summary>
        private static Exception Unwrap(Exception ex)
        {
            while (ex is TargetInvocationException tie && tie.InnerException != null)
                ex = tie.InnerException;
            return ex;
        }

        #endregion

        #region Result Formatting

        /// <summary>
        /// Format a result object for JSON output. Truncates enumerables to keep responses bounded.
        /// </summary>
        private static JToken FormatResult(object result)
        {
            if (result == null) return JValue.CreateNull();

            if (result is string s) return new JValue(s);
            if (result is bool b) return new JValue(b);
            if (result is int i) return new JValue(i);
            if (result is long l) return new JValue(l);
            if (result is float f) return new JValue(f);
            if (result is double d) return new JValue(d);
            if (result is decimal dec) return new JValue(dec);

            if (result is Vector3 v3) return ToolHelpers.Vector3ToJson(v3);
            if (result is Vector2 v2) return new JObject { ["x"] = v2.x, ["y"] = v2.y };
            if (result is Color color) return new JValue($"#{ColorUtility.ToHtmlStringRGBA(color)}");
            if (result is Quaternion q) return ToolHelpers.QuaternionToJson(q);

            if (result is GameObject go) return ToolHelpers.SerializeGameObject(go);
            if (result is Component comp) return ToolHelpers.SerializeComponent(comp);

            if (result is Enum e) return new JValue(e.ToString());

            if (result is System.Collections.IEnumerable enumerable)
            {
                var arr = new JArray();
                int count = 0;
                foreach (var item in enumerable)
                {
                    if (count >= 100)
                    {
                        arr.Add("[... truncated]");
                        break;
                    }
                    arr.Add(FormatResult(item));
                    count++;
                }
                return arr;
            }

            return new JValue(result.ToString());
        }

        #endregion

        #region Security

        /// <summary>
        /// Reject code containing patterns the tool is not permitted to execute (process launch,
        /// file/dir deletion, assembly loading, unmanaged interop). This is a coarse first-line
        /// guard — the primary safety net is user confirmation via <c>RequiresConfirmation</c>.
        /// </summary>
        private static bool ContainsDangerousPattern(string code)
        {
            var dangerous = new[]
            {
                "Process.Start",
                "Process.Kill",
                "File.Delete",
                "File.Move",
                "Directory.Delete",
                "Environment.Exit",
                "AppDomain.Unload",
                "Assembly.Load",
                "Activator.CreateInstance",
                "Runtime.InteropServices",
                "DllImport",
                "unsafe",
                "Marshal."
            };

            return dangerous.Any(p => code.Contains(p));
        }

        /// <summary>
        /// v1.12+ ModifyRuntimeState: Play Mode 中额外禁止的落盘 / Domain Reload API。
        /// <para>
        /// execute_code 在 Play Mode 中允许执行 (运行时 REPL 是核心价值),但用户代码若直接调用
        /// 落盘 API 会绕过 <see cref="Safety.PlaymodeWriteInterceptor"/> 的拦截,把运行时脏状态写入磁盘,
        /// 破坏"运行时修改退出即消失"的语义。此处做静态扫描,命中即拒绝执行。
        /// </para>
        /// <para>
        /// 反射绕过 (如 typeof(AssetDatabase).GetMethod("SaveAssets")) 无法被字符串扫描覆盖 ——
        /// 这是已知残余风险 (plans §7.1),接受:反射写盘属于蓄意规避,非误用;且需用户确认才能执行。
        /// </para>
        /// </summary>
        private static bool ContainsPlaymodeForbiddenApi(string code, out string hit)
        {
            hit = null;
            string[] forbidden =
            {
                "SaveAssets",
                "SaveAssetIfDirty",
                "SaveScene",
                "SaveOpenScenes",
                "CreateAsset",
                "SaveAsPrefabAsset",
                "ApplyPrefabInstance",
                "File.WriteAllText",
                "File.WriteAllBytes",
                "File.AppendAll",
                "EditorApplication.Exit",
                "AssetDatabase.ImportAsset"
            };
            foreach (var p in forbidden)
            {
                if (code.Contains(p))
                {
                    hit = p;
                    return true;
                }
            }
            return false;
        }

        #endregion
    }
}
