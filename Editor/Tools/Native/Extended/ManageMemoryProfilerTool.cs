using System;
using System.Collections.Generic;
using System.Diagnostics;
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

namespace AgentCore.Editor.Tools.Native.Extended
{
    [AgentTool("manage_memory_profiler",
        Description = "Unity Memory Profiler: capture / list / analyze / diff .snap files. " +
                      "Actions: take_memory_snapshot (async, no package needed), list_memory_snapshots (scan MemoryCaptures/), " +
                      "analyze_memory_snapshot (top-N objects — REQUIRES com.unity.memoryprofiler), " +
                      "diff_memory_snapshots (compare two — REQUIRES com.unity.memoryprofiler). " +
                      "PREREQUISITE for analyze/diff: install com.unity.memoryprofiler via Package Manager. " +
                      "ACTIVATE WHEN: user mentions 'memory profiler', 'memory snapshot', '.snap file'.",
        Category = "Extended",
        Visibility = ToolVisibility.OnDemand,
        RequiresMainThread = true,
        RiskLevel = ToolRiskLevel.Low,
        Capabilities = ToolCapability.WriteProjectFiles | ToolCapability.ReadProject,
        ReadOnlyActions = new[] { "list_memory_snapshots", "analyze_memory_snapshot", "diff_memory_snapshots" })]
    public class ManageMemoryProfilerTool : IAgentTool
    {
        private const string DefaultSnapshotSubdir = "MemoryCaptures";

        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""take_memory_snapshot"", ""list_memory_snapshots"", ""analyze_memory_snapshot"", ""diff_memory_snapshots""]
                },
                ""path"": { ""type"": ""string"", ""description"": ""File path (relative to project root or absolute). For take: output .snap file. For analyze/diff: input .snap file(s).""  },
                ""path_a"": { ""type"": ""string"" },
                ""path_b"": { ""type"": ""string"" },
                ""top_n"": { ""type"": ""integer"", ""description"": ""(analyze/diff) Top-N entries by size. Default 50."" },
                ""wait_seconds"": { ""type"": ""number"", ""description"": ""(take) Max wait for snapshot completion. Default 60."" },
                ""include_hierarchy_info"": { ""type"": ""boolean"" }
            },
            ""required"": [""action""]
        }");

        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_memory_profiler",
            description: "Memory Profiler: take/list/analyze/diff snapshots.",
            category: "Extended",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        public Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var action = ToolHelpers.GetRequiredString(parameters, "action").ToLowerInvariant();
                switch (action)
                {
                    case "take_memory_snapshot": return HandleTakeAsync(parameters, sw);
                    case "list_memory_snapshots": return WrapSync(HandleList(parameters), sw);
                    case "analyze_memory_snapshot": return WrapSync(HandleAnalyze(parameters), sw);
                    case "diff_memory_snapshots": return WrapSync(HandleDiff(parameters), sw);
                    default:
                        return WrapSync(ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid: take_memory_snapshot, list_memory_snapshots, analyze_memory_snapshot, diff_memory_snapshots."),
                            sw);
                }
            }
            catch (Exception ex)
            {
                return WrapSync(ToolResponse.Fail($"Unexpected error: {ex.Message}"), sw);
            }
        }

        private static Task<ToolResult> WrapSync(ToolResponse r, Stopwatch sw)
        {
            sw.Stop();
            return Task.FromResult(r.ToToolResult(sw.Elapsed.TotalMilliseconds));
        }

        private static string GetProjectRoot()
        {
            return PathUtils.ProjectRoot;
        }

        private static string ResolveSnapshotPath(string path, bool ensureExtension)
        {
            if (string.IsNullOrEmpty(path))
            {
                var dir = Path.Combine(GetProjectRoot(), DefaultSnapshotSubdir);
                Directory.CreateDirectory(dir);
                path = Path.Combine(dir, $"snapshot-{DateTime.Now:yyyyMMdd-HHmmss}.snap");
            }
            else if (!Path.IsPathRooted(path))
            {
                path = Path.Combine(GetProjectRoot(), path);
            }
            if (ensureExtension && !path.EndsWith(".snap", StringComparison.OrdinalIgnoreCase))
            {
                path += ".snap";
            }
            return path;
        }

        private static string GetDefaultSnapshotFolder()
        {
            return PathUtils.ToUnityPath(Path.Combine(GetProjectRoot(), DefaultSnapshotSubdir));
        }

        // ─────────────── take_memory_snapshot ────────────────
        private async Task<ToolResult> HandleTakeAsync(JObject parameters, Stopwatch sw)
        {
            var path = ToolHelpers.GetOptionalString(parameters, "path");
            path = ResolveSnapshotPath(path, ensureExtension: true);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var waitSeconds = ToolHelpers.GetOptionalFloat(parameters, "wait_seconds", 60f);
            if (waitSeconds <= 0f) waitSeconds = 60f;

            // Reflect UnityEngine.Profiling.Memory.Experimental.MemoryProfiler.TakeSnapshot
            var mpType = Type.GetType("UnityEngine.Profiling.Memory.Experimental.MemoryProfiler, UnityEngine");
            if (mpType == null)
            {
                sw.Stop();
                return ToolResponse.Fail("UnityEngine.Profiling.Memory.Experimental.MemoryProfiler type not found. Is this a supported Unity version?")
                    .ToToolResult(sw.Elapsed.TotalMilliseconds);
            }

            // Look for TakeSnapshot(string, Action<string, bool>) — most-common signature
            var takeMethod = FindTakeSnapshotMethod(mpType);
            if (takeMethod == null)
            {
                sw.Stop();
                return ToolResponse.Fail("MemoryProfiler.TakeSnapshot method not found via reflection. API may have changed.")
                    .ToToolResult(sw.Elapsed.TotalMilliseconds);
            }

            var tcs = new TaskCompletionSource<(string finalPath, bool success)>();
            var callbackDelegate = BuildCallbackDelegate(takeMethod, tcs);
            if (callbackDelegate == null)
            {
                sw.Stop();
                return ToolResponse.Fail("Could not build callback delegate matching TakeSnapshot signature. API drift.")
                    .ToToolResult(sw.Elapsed.TotalMilliseconds);
            }

            try
            {
                InvokeTakeSnapshot(takeMethod, path, callbackDelegate);
            }
            catch (Exception ex)
            {
                sw.Stop();
                return ToolResponse.Fail($"TakeSnapshot invocation failed: {ex.Message}")
                    .ToToolResult(sw.Elapsed.TotalMilliseconds);
            }

            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(waitSeconds));
            var completed = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);

            if (completed == timeoutTask)
            {
                sw.Stop();
                return ToolResponse.Fail($"TakeSnapshot did not complete within {waitSeconds}s. The Editor may still be capturing — check MemoryCaptures/ manually.")
                    .ToToolResult(sw.Elapsed.TotalMilliseconds);
            }

            var (finalPath, success) = tcs.Task.Result;
            var data = new JObject
            {
                ["path"] = PathUtils.ToUnityPath(finalPath ?? path),
                ["success"] = success,
                ["captured_at_utc"] = DateTime.UtcNow.ToString("o")
            };
            if (!string.IsNullOrEmpty(finalPath) && File.Exists(finalPath))
            {
                var fi = new FileInfo(finalPath);
                data["size_bytes"] = fi.Length;
            }
            sw.Stop();
            var resp = success
                ? ToolResponse.OkWithData(data, $"Memory snapshot captured: {finalPath}")
                : ToolResponse.Fail($"Snapshot callback returned success=false. Path: {finalPath}");
            return resp.ToToolResult(sw.Elapsed.TotalMilliseconds);
        }

        private static MethodInfo FindTakeSnapshotMethod(Type mpType)
        {
            // Native-bound; GetMethods() often empty. Try GetMethod with common signatures.
            foreach (var m in mpType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic))
            {
                if (m.Name != "TakeSnapshot") continue;
                return m;
            }
            // Fallback: try known signatures explicitly (Unity 2022.3.x)
            var callbackAction2 = typeof(Action<string, bool>);
            var callbackAction3 = Type.GetType("System.Action`3[[System.String, mscorlib],[System.Boolean, mscorlib],[UnityEngine.Rendering.DebugScreenCapture, UnityEngine]]", false);
            foreach (var m in mpType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy))
            {
                if (m.Name.IndexOf("TakeSnapshot", StringComparison.OrdinalIgnoreCase) >= 0) return m;
            }
            return null;
        }

        private static Delegate BuildCallbackDelegate(MethodInfo takeMethod, TaskCompletionSource<(string, bool)> tcs)
        {
            var ps = takeMethod.GetParameters();
            if (ps.Length < 2) return null;
            var callbackParamType = ps[1].ParameterType;

            // We match on the callback's generic argument count.
            if (callbackParamType.IsGenericType)
            {
                var args = callbackParamType.GetGenericArguments();
                if (args.Length == 2 && args[0] == typeof(string) && args[1] == typeof(bool))
                {
                    Action<string, bool> cb = (p, ok) => tcs.TrySetResult((p, ok));
                    return cb;
                }
                if (args.Length == 3 && args[0] == typeof(string) && args[1] == typeof(bool))
                {
                    // Third argument is DebugScreenCapture — we ignore it.
                    var third = args[2];
                    var actionType = typeof(Action<,,>).MakeGenericType(typeof(string), typeof(bool), third);
                    var method = typeof(ManageMemoryProfilerTool).GetMethod(nameof(SnapshotCallback3), BindingFlags.NonPublic | BindingFlags.Static);
                    if (method == null) return null;
                    var generic = method.MakeGenericMethod(third);
                    return Delegate.CreateDelegate(actionType, tcs, generic);
                }
            }
            return null;
        }

        private static void SnapshotCallback3<TCapture>(TaskCompletionSource<(string, bool)> tcs, string path, bool success, TCapture _)
        {
            tcs.TrySetResult((path, success));
        }

        private static void InvokeTakeSnapshot(MethodInfo takeMethod, string path, Delegate callback)
        {
            var ps = takeMethod.GetParameters();
            var args = new object[ps.Length];
            args[0] = path;
            args[1] = callback;
            for (int i = 2; i < ps.Length; i++)
            {
                if (ps[i].HasDefaultValue) args[i] = ps[i].DefaultValue;
                else if (ps[i].ParameterType.IsValueType) args[i] = Activator.CreateInstance(ps[i].ParameterType);
                else args[i] = null;
            }
            takeMethod.Invoke(null, args);
        }

        // ─────────────── list_memory_snapshots ────────────────
        private static ToolResponse HandleList(JObject parameters)
        {
            var folder = ToolHelpers.GetOptionalString(parameters, "path");
            if (string.IsNullOrEmpty(folder)) folder = GetDefaultSnapshotFolder();
            else if (!Path.IsPathRooted(folder)) folder = Path.Combine(GetProjectRoot(), folder);

            var data = new JObject
            {
                ["folder"] = PathUtils.ToUnityPath(folder),
                ["exists"] = Directory.Exists(folder)
            };
            var arr = new JArray();
            if (Directory.Exists(folder))
            {
                foreach (var f in Directory.GetFiles(folder, "*.snap", SearchOption.TopDirectoryOnly))
                {
                    var fi = new FileInfo(f);
                    arr.Add(new JObject
                    {
                        ["name"] = fi.Name,
                        ["path"] = PathUtils.ToUnityPath(fi.FullName),
                        ["size_bytes"] = fi.Length,
                        ["last_modified_utc"] = fi.LastWriteTimeUtc.ToString("o")
                    });
                }
            }
            data["count"] = arr.Count;
            data["snapshots"] = arr;
            return ToolResponse.OkWithData(data, $"Found {arr.Count} .snap file(s) in {folder}.");
        }

        // ─────────────── analyze_memory_snapshot ────────────────
        private static ToolResponse HandleAnalyze(JObject parameters)
        {
            var path = ToolHelpers.GetRequiredString(parameters, "path");
            path = ResolveSnapshotPath(path, ensureExtension: false);
            if (!File.Exists(path))
                return ToolResponse.Fail($"Snapshot file not found: {path}");

            var topN = ToolHelpers.GetOptionalInt(parameters, "top_n", 50);
            if (topN <= 0) topN = 50;

            var (snapshot, err) = LoadPackedSnapshotReflectively(path);
            if (err != null) return ToolResponse.Fail(err);
            if (snapshot == null) return ToolResponse.Fail("PackedMemorySnapshot.Load returned null.");

            var data = new JObject
            {
                ["path"] = PathUtils.ToUnityPath(path),
                ["top_n"] = topN,
                ["snapshot_type"] = snapshot.GetType().FullName
            };

            var counts = ExtractEntryCounts(snapshot);
            data["entry_counts"] = CountsToJObject(counts);

            TryDispose(snapshot);
            return ToolResponse.OkWithData(data,
                $"Loaded snapshot ({counts.Count} entry categories reported). NOTE: detailed top-N size ranking requires the CachedSnapshot API from com.unity.memoryprofiler; only entry counts are exposed without it.");
        }

        // ─────────────── diff_memory_snapshots ────────────────
        private static ToolResponse HandleDiff(JObject parameters)
        {
            var pathA = ToolHelpers.GetRequiredString(parameters, "path_a");
            var pathB = ToolHelpers.GetRequiredString(parameters, "path_b");
            pathA = ResolveSnapshotPath(pathA, ensureExtension: false);
            pathB = ResolveSnapshotPath(pathB, ensureExtension: false);
            if (!File.Exists(pathA)) return ToolResponse.Fail($"Snapshot A not found: {pathA}");
            if (!File.Exists(pathB)) return ToolResponse.Fail($"Snapshot B not found: {pathB}");

            var (a, errA) = LoadPackedSnapshotReflectively(pathA);
            if (errA != null) return ToolResponse.Fail(errA);
            var (b, errB) = LoadPackedSnapshotReflectively(pathB);
            if (errB != null) { TryDispose(a); return ToolResponse.Fail(errB); }

            var countsA = ExtractEntryCounts(a);
            var countsB = ExtractEntryCounts(b);

            var diff = new JObject();
            var keys = new HashSet<string>();
            foreach (var kv in countsA) keys.Add(kv.Key);
            foreach (var kv in countsB) keys.Add(kv.Key);
            foreach (var key in keys)
            {
                countsA.TryGetValue(key, out var vA);
                countsB.TryGetValue(key, out var vB);
                long a64 = vA != null && vA.Type == JTokenType.Integer ? vA.Value<long>() : 0L;
                long b64 = vB != null && vB.Type == JTokenType.Integer ? vB.Value<long>() : 0L;
                diff[key] = new JObject
                {
                    ["a"] = a64,
                    ["b"] = b64,
                    ["delta"] = b64 - a64
                };
            }

            var data = new JObject
            {
                ["path_a"] = PathUtils.ToUnityPath(pathA),
                ["path_b"] = PathUtils.ToUnityPath(pathB),
                ["entry_count_diff"] = diff,
                ["file_size_a_bytes"] = new FileInfo(pathA).Length,
                ["file_size_b_bytes"] = new FileInfo(pathB).Length,
                ["file_size_delta_bytes"] = new FileInfo(pathB).Length - new FileInfo(pathA).Length
            };

            TryDispose(a);
            TryDispose(b);
            return ToolResponse.OkWithData(data,
                $"Diffed A={Path.GetFileName(pathA)} vs B={Path.GetFileName(pathB)}. See entry_count_diff for per-category delta.");
        }

        // ─────────────── reflective PackedMemorySnapshot helpers ────────────────
        private static (object snapshot, string error) LoadPackedSnapshotReflectively(string path)
        {
            var packedType = Type.GetType("UnityEditor.Profiling.Memory.Experimental.PackedMemorySnapshot, UnityEditor")
                          ?? Type.GetType("UnityEditor.Profiling.Memory.Experimental.PackedMemorySnapshot, UnityEditor.CoreModule");
            if (packedType == null)
                return (null, "UnityEditor.Profiling.Memory.Experimental.PackedMemorySnapshot type not found. Update Unity or check API drift.");

            var loadMethod = packedType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            if (loadMethod == null)
                return (null, "PackedMemorySnapshot.Load(string) method not found.");

            try
            {
                var snapshot = loadMethod.Invoke(null, new object[] { path });
                return (snapshot, null);
            }
            catch (Exception ex)
            {
                return (null, $"PackedMemorySnapshot.Load failed: {ex.InnerException?.Message ?? ex.Message}");
            }
        }

        private static Dictionary<string, JToken> ExtractEntryCounts(object snapshot)
        {
            var result = new Dictionary<string, JToken>();
            if (snapshot == null) return result;
            var t = snapshot.GetType();
            var propNames = new[]
            {
                "nativeObjects", "nativeTypes", "typeDescriptions",
                "gcHandles", "nativeAllocations", "nativeMemoryLabels",
                "nativeMemoryRegions", "managedHeapSections", "managedStacks",
                "connections", "fieldDescriptions", "nativeCallstackSymbols",
                "nativeAllocationSites", "nativeRootReferences"
            };
            foreach (var name in propNames)
            {
                var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (p == null) { result[name] = null; continue; }
                object entries;
                try { entries = p.GetValue(snapshot); }
                catch { result[name] = null; continue; }
                if (entries == null) { result[name] = null; continue; }
                long? count = TryReadCount(entries);
                result[name] = count.HasValue ? (JToken)count.Value : null;
            }
            return result;
        }

        private static long? TryReadCount(object entries)
        {
            if (entries == null) return null;
            var et = entries.GetType();
            foreach (var member in new[] { "GetNumEntries", "Count", "Length" })
            {
                var mi = et.GetMethod(member, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (mi != null)
                {
                    try { return Convert.ToInt64(mi.Invoke(entries, null)); } catch { }
                }
                var pi = et.GetProperty(member, BindingFlags.Public | BindingFlags.Instance);
                if (pi != null)
                {
                    try { return Convert.ToInt64(pi.GetValue(entries)); } catch { }
                }
                var fi = et.GetField(member, BindingFlags.Public | BindingFlags.Instance);
                if (fi != null)
                {
                    try { return Convert.ToInt64(fi.GetValue(entries)); } catch { }
                }
            }
            return null;
        }

        private static void TryDispose(object snapshot)
        {
            if (snapshot == null) return;
            try
            {
                if (snapshot is IDisposable d) { d.Dispose(); return; }
                var m = snapshot.GetType().GetMethod("Dispose", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (m != null) m.Invoke(snapshot, null);
            }
            catch { /* best-effort */ }
        }

        private static JObject CountsToJObject(Dictionary<string, JToken> counts)
        {
            var o = new JObject();
            foreach (var kv in counts)
            {
                o[kv.Key] = kv.Value;
            }
            return o;
        }
    }
}
