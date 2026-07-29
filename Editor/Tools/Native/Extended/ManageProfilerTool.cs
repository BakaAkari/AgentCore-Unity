using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Profiling;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEditor;
using Newtonsoft.Json.Linq;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Tools.Safety;

namespace AgentCore.Editor.Tools.Native.Extended
{
    /// <summary>
    /// Access Unity Profiler data for performance analysis and optimization.
    /// Provides frame stats, memory info, rendering stats, and profiler recording control.
    /// </summary>
    [AgentTool("manage_profiler",
        Description = "Unity Profiler data access for performance diagnostics. " +
                      "Actions: get_stats (CPU/GPU time, draw calls, triangles, batches — single-frame UnityStats snapshot), " +
                      "get_memory (total/used/reserved memory breakdown: Mono heap, temp allocator, graphics driver), " +
                      "get_rendering_stats (shader passes, render targets, shadow casters, batches), " +
                      "start_recording / stop_recording (control Profiler.enabled + binary log capture), " +
                      "list_available_stats (enumerate all ProfilerRecorder stat names across categories), " +
                      "sample_recorder (multi-frame ProfilerRecorder time-series sampling — mean/min/max/p95/median + per-frame values), " +
                      "get_frame_range (current Profiler buffer's [firstFrameIndex, lastFrameIndex]), " +
                      "read_frame (read a specific past frame's marker hierarchy — top-N markers by self-time, per-thread), " + "list_draw_events (enumerate GPU draw events via Unity FrameDebugger — event type, shader, vertex/index count), " + "get_draw_event (detailed data for one draw event — shader keywords, blend/depth/raster/stencil state, render target, mesh info), " + "disable_frame_debugger (exit FrameDebugger mode — GameView returns to normal). " +
                      "USE FOR: diagnosing frame rate drops, checking memory usage, understanding rendering costs, " +
                      "measuring before/after impact of optimizations, grabbing single-frame stats for a scene, " +
                      "multi-frame time-series to see spikes/trends, drilling into a specific captured frame's marker tree. " +
                      "NOT FOR: CPU timeline/flame chart (open Unity Profiler window manually), Frame Debugger draw event list (use list_draw_events when available). " +
                      "Requires Play Mode for meaningful frame data — call manage_editor:play_mode first if project is stopped. " +
                      "ACTIVATE WHEN: user mentions 'performance', 'frame rate', 'FPS', 'memory usage', 'profiler', 'draw calls', 'optimization metrics', '抓帧', '性能', '掉帧', '内存占用'.",
        Category = "extended",
        RequiresMainThread = true,
        RiskLevel = ToolRiskLevel.Low,
        Capabilities = ToolCapability.ReadProject,
        Visibility = ToolVisibility.AlwaysVisible)]
    public class ManageProfilerTool : IAgentTool
    {
        #region Schema

        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""get_stats"", ""get_memory"", ""start_recording"", ""stop_recording"", ""get_rendering_stats"", ""list_available_stats"", ""sample_recorder"", ""get_frame_range"", ""read_frame"", ""list_draw_events"", ""get_draw_event"", ""disable_frame_debugger""],
                    ""description"": ""Action to perform""
                },
                ""log_file"": {
                    ""type"": ""string"",
                    ""description"": ""File path to save profiler data (for start_recording action, optional)""
                },
                ""category"": {
                    ""type"": ""string"",
                    ""description"": ""ProfilerCategory name (for list_available_stats filter or sample_recorder). Common: Render, Scripts, Memory, Gui, Physics, Animation, Ai, Audio, Video, Particles, Vr, FileIO, Internal. Case-insensitive.""
                },
                ""limit"": {
                    ""type"": ""integer"",
                    ""description"": ""Max stats to return per category (for list_available_stats, v1.11+). Default 100, use higher value to see more. Prevents oversized output (some Unity versions expose 500+ stats).""
                },
                ""stat_name"": {
                    ""type"": ""string"",
                    ""description"": ""ProfilerRecorder stat name to sample (for sample_recorder). Examples: 'Main Thread', 'CPU Total Frame Time', 'GC.Alloc', 'Draw Calls Count', 'Triangles Count', 'System Used Memory'. Use list_available_stats to discover valid names.""
                },
                ""frame_count"": {
                    ""type"": ""integer"",
                    ""description"": ""Number of frames to sample (for sample_recorder). Default 60, max 600. Sampling runs on EditorApplication.update — requires Play Mode or a repainting Editor for meaningful non-zero values.""
                },
                ""capacity"": {
                    ""type"": ""integer"",
                    ""description"": ""ProfilerRecorder ring buffer size (for sample_recorder). Default equals frame_count. Larger = more history retained per recorder.""
                },
                ""frame_index"": {
                    ""type"": ""integer"",
                    ""description"": ""Absolute Profiler frame index to read (for read_frame). Use get_frame_range to see valid [first, last] range. Negative values are treated as relative to last (e.g. -1 = last recorded frame).""
                },
                ""thread_index"": {
                    ""type"": ""integer"",
                    ""description"": ""Thread index within the frame (for read_frame). 0 = Main Thread (default). Use higher values for Render Thread, Job Worker threads, etc. Enumerated per-frame.""
                },
                ""max_markers"": {
                    ""type"": ""integer"",
                    ""description"": ""Maximum top-level markers to return per frame (for read_frame). Default 30, max 200. Markers are sorted by self-time descending. Also applies to per-level top-N when depth>1.""
                },
                ""depth"": {
                    ""type"": ""integer"",
                    ""description"": ""Recursion depth for read_frame marker tree (v1.8.4+). Default 1 (root only, backward-compatible). Max 5 to avoid response bloat. Each level keeps top-max_markers children by total_ms. Use depth=3-4 to drill into EditorLoop / Render / Physics sub-markers.""
                },
                ""min_ms"": {
                    ""type"": ""number"",
                    ""description"": ""Filter threshold for read_frame (v1.8.4+). Skip markers whose total_ms < min_ms. Default 0 (no filter). Recommended 0.5 or 1.0 when using depth>=3 to cut noise.""
                },
                ""event_index"": {
                    ""type"": ""integer"",
                    ""description"": ""FrameDebugger event index (for get_draw_event). 0-based. Use list_draw_events to see valid range. Negative values are relative to end (e.g. -1 = last event).""
                },
                ""max_events"": {
                    ""type"": ""integer"",
                    ""description"": ""Maximum events to return (for list_draw_events). Default 200. Events are truncated with a hint if the frame has more.""
                },
                ""enable_if_needed"": {
                    ""type"": ""boolean"",
                    ""description"": ""If true (default), auto-enable FrameDebugger when no events are captured yet. Warning: enabling puts GameView into a special debug mode until disable_frame_debugger is called. Requires Play Mode.""
                }
            },
            ""required"": [""action""]
        }");

        #endregion

        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_profiler",
            description: "Access Unity Profiler data for performance analysis and optimization",
            category: "extended",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        public async Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            ToolResponse response;

            try
            {
                var action = ToolHelpers.GetRequiredString(parameters, "action").ToLowerInvariant();

                switch (action)
                {
                    case "get_stats":
                        response = HandleGetStats();
                        break;
                    case "get_memory":
                        response = HandleGetMemory();
                        break;
                    case "start_recording":
                        response = HandleStartRecording(parameters);
                        break;
                    case "stop_recording":
                        response = HandleStopRecording();
                        break;
                    case "get_rendering_stats":
                        response = HandleGetRenderingStats();
                        break;
                    case "list_available_stats":
                        response = HandleListAvailableStats(parameters);
                        break;
                    case "sample_recorder":
                        response = await HandleSampleRecorder(parameters, cancellationToken);
                        break;
                    case "get_frame_range":
                        response = HandleGetFrameRange();
                        break;
                    case "read_frame":
                        response = HandleReadFrame(parameters);
                        break;
                    case "list_draw_events":
                        response = await HandleListDrawEvents(parameters);
                        break;
                    case "get_draw_event":
                        response = HandleGetDrawEvent(parameters);
                        break;
                    case "disable_frame_debugger":
                        response = HandleDisableFrameDebugger();
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: get_stats, get_memory, start_recording, stop_recording, get_rendering_stats, list_available_stats, sample_recorder, get_frame_range, read_frame, list_draw_events, get_draw_event, disable_frame_debugger");
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
            return response.ToToolResult(sw.Elapsed.TotalMilliseconds);
        }

        #region Action Handlers

        private ToolResponse HandleGetStats()
        {
            var data = new JObject();

            // Memory stats from Profiler API
            long totalAllocated = Profiler.GetTotalAllocatedMemoryLong();
            long totalReserved = Profiler.GetTotalReservedMemoryLong();

            data["memory"] = new JObject
            {
                ["total_allocated_mb"] = Math.Round(totalAllocated / (1024.0 * 1024.0), 2),
                ["total_reserved_mb"] = Math.Round(totalReserved / (1024.0 * 1024.0), 2)
            };

            // Try to get UnityStats via reflection (Editor-only class)
            var unityStatsType = Type.GetType("UnityEditor.UnityStats, UnityEditor");
            if (unityStatsType != null)
            {
                var statsData = new JObject();

                TryGetStaticProperty(unityStatsType, "frameTime", statsData, "frame_time_ms");
                TryGetStaticProperty(unityStatsType, "renderTime", statsData, "render_time_ms");
                TryGetStaticProperty(unityStatsType, "triangles", statsData, "triangles");
                TryGetStaticProperty(unityStatsType, "vertices", statsData, "vertices");
                TryGetStaticProperty(unityStatsType, "batches", statsData, "batches");
                TryGetStaticProperty(unityStatsType, "drawCalls", statsData, "draw_calls");
                TryGetStaticProperty(unityStatsType, "setPassCalls", statsData, "set_pass_calls");
                TryGetStaticProperty(unityStatsType, "shadowCasters", statsData, "shadow_casters");
                TryGetStaticProperty(unityStatsType, "screenRes", statsData, "screen_resolution");
                TryGetStaticProperty(unityStatsType, "screenBytes", statsData, "screen_bytes");

                // Calculate FPS from frame time
                if (statsData["frame_time_ms"] != null)
                {
                    var frameTimeMs = statsData["frame_time_ms"].Value<float>();
                    if (frameTimeMs > 0)
                    {
                        statsData["fps"] = Math.Round(1000.0 / frameTimeMs, 1);
                    }
                }

                data["stats"] = statsData;
            }
            else
            {
                data["stats_note"] = "UnityStats not available via reflection in this Unity version.";
            }

            data["profiler_enabled"] = Profiler.enabled;

            return ToolResponse.OkWithData(data, "Performance stats retrieved successfully.");
        }

        private ToolResponse HandleGetMemory()
        {
            long totalAllocated = Profiler.GetTotalAllocatedMemoryLong();
            long totalReserved = Profiler.GetTotalReservedMemoryLong();
            long monoHeap = Profiler.GetMonoHeapSizeLong();
            long monoUsed = Profiler.GetMonoUsedSizeLong();
            long gfxMemory = Profiler.GetAllocatedMemoryForGraphicsDriver();

            var data = new JObject
            {
                ["total_allocated_mb"] = Math.Round(totalAllocated / (1024.0 * 1024.0), 2),
                ["total_reserved_mb"] = Math.Round(totalReserved / (1024.0 * 1024.0), 2),
                ["mono_heap_mb"] = Math.Round(monoHeap / (1024.0 * 1024.0), 2),
                ["mono_used_mb"] = Math.Round(monoUsed / (1024.0 * 1024.0), 2),
                ["gfx_memory_mb"] = Math.Round(gfxMemory / (1024.0 * 1024.0), 2),
                ["total_allocated_bytes"] = totalAllocated,
                ["total_reserved_bytes"] = totalReserved,
                ["mono_heap_bytes"] = monoHeap,
                ["mono_used_bytes"] = monoUsed,
                ["gfx_memory_bytes"] = gfxMemory,
                ["mono_usage_percent"] = monoHeap > 0
                    ? Math.Round((double)monoUsed / monoHeap * 100, 1)
                    : 0
            };

            // Try to get temp allocator stats
            long tempAllocator = Profiler.GetTempAllocatorSize();
            data["temp_allocator_mb"] = Math.Round(tempAllocator / (1024.0 * 1024.0), 2);

            return ToolResponse.OkWithData(data, "Memory usage details retrieved successfully.");
        }

        private ToolResponse HandleStartRecording(JObject parameters)
        {
            if (Profiler.enabled)
            {
                return ToolResponse.Fail("Profiler recording is already active.");
            }

            var logFile = ToolHelpers.GetOptionalString(parameters, "log_file");

            if (!string.IsNullOrEmpty(logFile))
            {
                // Ensure directory exists
                var directory = System.IO.Path.GetDirectoryName(logFile);
                if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
                {
                    System.IO.Directory.CreateDirectory(directory);
                }

                Profiler.logFile = logFile;
                Profiler.enableBinaryLog = true;
            }

            Profiler.enabled = true;

            // Also enable ProfilerDriver.enabled — this is a SEPARATE Editor-side switch
            // that controls whether the Profiler Window's frame buffer is populated.
            // Without this, ProfilerDriver.firstFrameIndex/lastFrameIndex stay -1 and
            // read_frame / get_frame_range have no frames to read. Setting Profiler.enabled=true
            // alone only makes ProfilerRecorder / runtime sampling work.
            bool driverEnabled = false;
            string driverNote = "";
            try
            {
                UnityEditorInternal.ProfilerDriver.enabled = true;
                UnityEditorInternal.ProfilerDriver.profileEditor = true; // capture Editor-time frames too
                driverEnabled = UnityEditorInternal.ProfilerDriver.enabled;
            }
            catch (Exception e)
            {
                driverNote = $"ProfilerDriver.enabled setter failed: {e.GetType().Name}: {e.Message}. Frame buffer (read_frame/get_frame_range) may be empty.";
            }

            var data = new JObject
            {
                ["recording"] = true,
                ["log_file"] = logFile ?? "(none - in-memory only)",
                ["profiler_driver_enabled"] = driverEnabled,
                ["profile_editor"] = driverEnabled // profileEditor tracks same state
            };
            if (!string.IsNullOrEmpty(driverNote))
            {
                data["driver_note"] = driverNote;
            }

            return ToolResponse.OkWithData(data,
                driverEnabled
                    ? "Profiler recording started (runtime sampling + Editor frame buffer)."
                    : "Profiler recording started (runtime sampling only — frame buffer disabled, read_frame unavailable).");
        }

        private ToolResponse HandleStopRecording()
        {
            if (!Profiler.enabled)
            {
                return ToolResponse.Fail("Profiler recording is not active.");
            }

            var logFile = Profiler.logFile;

            Profiler.enabled = false;
            Profiler.enableBinaryLog = false;
            Profiler.logFile = "";

            // Symmetric with start_recording: also turn off ProfilerDriver.
            try
            {
                UnityEditorInternal.ProfilerDriver.enabled = false;
                UnityEditorInternal.ProfilerDriver.profileEditor = false;
            }
            catch { /* best-effort — matches start_recording tolerance */ }

            var data = new JObject
            {
                ["recording"] = false,
                ["log_file"] = string.IsNullOrEmpty(logFile) ? "(none)" : logFile
            };

            return ToolResponse.OkWithData(data, "Profiler recording stopped.");
        }

        private ToolResponse HandleGetRenderingStats()
        {
            var data = new JObject();

            // Try to get UnityStats via reflection
            var unityStatsType = Type.GetType("UnityEditor.UnityStats, UnityEditor");
            if (unityStatsType != null)
            {
                TryGetStaticProperty(unityStatsType, "batches", data, "batches");
                TryGetStaticProperty(unityStatsType, "drawCalls", data, "draw_calls");
                TryGetStaticProperty(unityStatsType, "triangles", data, "triangles");
                TryGetStaticProperty(unityStatsType, "vertices", data, "vertices");
                TryGetStaticProperty(unityStatsType, "setPassCalls", data, "set_pass_calls");
                TryGetStaticProperty(unityStatsType, "shadowCasters", data, "shadow_casters");
                TryGetStaticProperty(unityStatsType, "renderTime", data, "render_time_ms");
                TryGetStaticProperty(unityStatsType, "screenRes", data, "screen_resolution");
                TryGetStaticProperty(unityStatsType, "screenBytes", data, "screen_bytes");
                TryGetStaticProperty(unityStatsType, "dynamicBatchedDrawCalls", data, "dynamic_batched_draw_calls");
                TryGetStaticProperty(unityStatsType, "staticBatchedDrawCalls", data, "static_batched_draw_calls");
                TryGetStaticProperty(unityStatsType, "instancedBatchedDrawCalls", data, "instanced_batched_draw_calls");
                TryGetStaticProperty(unityStatsType, "dynamicBatches", data, "dynamic_batches");
                TryGetStaticProperty(unityStatsType, "staticBatches", data, "static_batches");
                TryGetStaticProperty(unityStatsType, "instancedBatches", data, "instanced_batches");
            }
            else
            {
                return ToolResponse.Fail("UnityStats not available via reflection in this Unity version.");
            }

            // Add render pipeline info
            var currentRP = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            if (currentRP != null)
            {
                data["render_pipeline"] = currentRP.GetType().Name;
            }
            else
            {
                data["render_pipeline"] = "Built-in";
            }

            return ToolResponse.OkWithData(data, "Rendering stats retrieved successfully.");
        }

        /// <summary>
        /// List all ProfilerRecorder-available stats across all categories.
        /// Uses ProfilerRecorderHandle.GetAvailable() to enumerate stat descriptions.
        /// Optional 'category' parameter filters by ProfilerCategory name (case-insensitive).
        /// </summary>
        private ToolResponse HandleListAvailableStats(JObject parameters)
        {
            var categoryFilter = ToolHelpers.GetOptionalString(parameters, "category");
            // Bug C (v1.11+): per-category limit to prevent oversized output.
            // Some Unity versions expose 500+ stats per category → full dump = 800k+ chars.
            var limit = ToolHelpers.GetOptionalInt(parameters, "limit", 100);
            if (limit <= 0) limit = 100;

            var handles = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);

            var byCategory = new Dictionary<string, List<JObject>>();
            var truncatedCategories = new Dictionary<string, int>(); // category -> total count before limit

            foreach (var handle in handles)
            {
                if (!handle.Valid) continue;

                var desc = ProfilerRecorderHandle.GetDescription(handle);
                var catName = desc.Category.Name ?? "(unknown)";

                if (!string.IsNullOrEmpty(categoryFilter) &&
                    !string.Equals(catName, categoryFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!byCategory.TryGetValue(catName, out var list))
                {
                    list = new List<JObject>();
                    byCategory[catName] = list;
                }

                // Track full count even beyond limit for truncation notice
                if (list.Count >= limit)
                {
                    if (!truncatedCategories.ContainsKey(catName))
                        truncatedCategories[catName] = list.Count;
                    truncatedCategories[catName]++;
                    continue;
                }

                list.Add(new JObject
                {
                    ["name"] = desc.Name,
                    ["data_type"] = desc.DataType.ToString(),
                    ["unit"] = desc.UnitType.ToString(),
                    ["flags"] = desc.Flags.ToString()
                });
            }

            var catsArr = new JArray();
            int totalFiltered = 0;
            foreach (var kvp in byCategory)
            {
                totalFiltered += kvp.Value.Count;
                var catObj = new JObject
                {
                    ["category"] = kvp.Key,
                    ["stat_count"] = kvp.Value.Count,
                    ["stats"] = new JArray(kvp.Value)
                };
                if (truncatedCategories.TryGetValue(kvp.Key, out var totalInCat))
                {
                    catObj["truncated"] = true;
                    catObj["total_stats_in_category"] = totalInCat;
                    catObj["truncation_hint"] = $"Category has {totalInCat} stats, only first {limit} shown. Pass 'limit' parameter with a higher value to see more, or use 'category' filter to narrow.";
                }
                catsArr.Add(catObj);
            }

            var data = new JObject
            {
                ["total_stats"] = handles.Count,
                ["filtered_stats"] = totalFiltered,
                ["category_filter"] = string.IsNullOrEmpty(categoryFilter) ? "(all)" : categoryFilter,
                ["categories"] = catsArr
            };

            return ToolResponse.OkWithData(data,
                $"Enumerated {handles.Count} ProfilerRecorder stats across {byCategory.Count} categor{(byCategory.Count == 1 ? "y" : "ies")}.");
        }

        /// <summary>
        /// Sample a ProfilerRecorder stat over N frames via EditorApplication.update hook.
        /// Returns per-frame values + min/max/mean/median/p95 aggregates.
        /// Requires Play Mode or a repainting Editor for meaningful non-zero data.
        /// </summary>
        private async Task<ToolResponse> HandleSampleRecorder(JObject parameters, CancellationToken cancellationToken)
        {
            var categoryName = ToolHelpers.GetOptionalString(parameters, "category");
            var statName = ToolHelpers.GetOptionalString(parameters, "stat_name");
            if (string.IsNullOrEmpty(statName))
            {
                return ToolResponse.Fail("stat_name is required for sample_recorder. Call list_available_stats first to discover valid names.");
            }

            int frameCount = 60;
            if (parameters["frame_count"] != null && parameters["frame_count"].Type == JTokenType.Integer)
            {
                frameCount = parameters["frame_count"].Value<int>();
                if (frameCount < 1) frameCount = 1;
                if (frameCount > 600) frameCount = 600;
            }

            int capacity = frameCount;
            if (parameters["capacity"] != null && parameters["capacity"].Type == JTokenType.Integer)
            {
                capacity = parameters["capacity"].Value<int>();
                if (capacity < frameCount) capacity = frameCount;
                if (capacity > 6000) capacity = 6000;
            }

            // Resolve category + description via GetAvailable() lookup.
            // ProfilerRecorder itself does not expose Handle publicly (Unity 2022.3), so we get desc up-front here.
            ProfilerCategory category = ProfilerCategory.Scripts;
            bool categoryResolved = false;
            string unitTypeStr = "Unknown";
            string dataTypeStr = "Unknown";

            if (!string.IsNullOrEmpty(categoryName))
            {
                // ProfilerCategory has static readonly fields per built-in category — reflect to resolve.
                var catType = typeof(ProfilerCategory);
                var field = catType.GetField(categoryName, BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
                if (field != null && field.FieldType == catType)
                {
                    category = (ProfilerCategory)field.GetValue(null);
                    categoryResolved = true;
                }
            }

            // Also scan GetAvailable() to grab the description (needed for unit/data type reporting)
            // and to fill in category when not provided.
            {
                var handles = new List<ProfilerRecorderHandle>();
                ProfilerRecorderHandle.GetAvailable(handles);
                foreach (var h in handles)
                {
                    if (!h.Valid) continue;
                    var d = ProfilerRecorderHandle.GetDescription(h);
                    if (!string.Equals(d.Name, statName, StringComparison.Ordinal)) continue;
                    // Match by category if caller specified it, otherwise take first match.
                    if (categoryResolved && !string.Equals(d.Category.Name, category.Name, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    if (!categoryResolved)
                    {
                        category = d.Category;
                        categoryResolved = true;
                    }
                    unitTypeStr = d.UnitType.ToString();
                    dataTypeStr = d.DataType.ToString();
                    break;
                }
            }

            if (!categoryResolved)
            {
                return ToolResponse.Fail(
                    $"Could not resolve ProfilerCategory for stat '{statName}'. Provide 'category' parameter explicitly, or call list_available_stats to see valid category+stat pairs.");
            }

            // Create recorder.
            ProfilerRecorder recorder;
            try
            {
                recorder = ProfilerRecorder.StartNew(category, statName, capacity);
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Failed to start ProfilerRecorder for '{statName}': {ex.Message}");
            }

            if (!recorder.Valid)
            {
                recorder.Dispose();
                return ToolResponse.Fail(
                    $"ProfilerRecorder for '{statName}' in category '{category.Name}' is not valid — stat may not exist or be unavailable in this Unity version. Call list_available_stats to verify.");
            }

            // Sample N frames via EditorApplication.update.
            // Timeout guard: if Editor is not repainting (e.g. no focus + no Play Mode), update won't fire.
            // Cap wall-clock time to (frame_count * 100ms) + 5s buffer to avoid infinite hang.
            var tcs = new TaskCompletionSource<bool>();
            int framesSeen = 0;
            EditorApplication.CallbackFunction updateCb = null;
            updateCb = () =>
            {
                framesSeen++;
                if (framesSeen >= frameCount || cancellationToken.IsCancellationRequested)
                {
                    EditorApplication.update -= updateCb;
                    tcs.TrySetResult(true);
                }
            };
            EditorApplication.update += updateCb;

            int timeoutMs = frameCount * 100 + 5000;
            bool timedOut = false;
            try
            {
                using (cancellationToken.Register(() => tcs.TrySetCanceled()))
                {
                    var timeoutTask = Task.Delay(timeoutMs, cancellationToken);
                    var completed = await Task.WhenAny(tcs.Task, timeoutTask);
                    if (completed == timeoutTask && !tcs.Task.IsCompleted)
                    {
                        EditorApplication.update -= updateCb;
                        timedOut = true;
                    }
                }
            }
            catch (TaskCanceledException)
            {
                EditorApplication.update -= updateCb;
                recorder.Dispose();
                return ToolResponse.Fail("sample_recorder was cancelled.");
            }

            // Read samples.
            int sampleCount = Math.Min(recorder.Count, framesSeen);
            var samplesList = new List<ProfilerRecorderSample>(sampleCount);
            for (int i = 0; i < sampleCount; i++)
            {
                samplesList.Add(recorder.GetSample(i));
            }

            recorder.Dispose();

            // Extract values as double for stats.
            var values = new double[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                values[i] = samplesList[i].Value;
            }

            double mean = 0, min = 0, max = 0, median = 0, p95 = 0;
            var perFrame = new JArray();
            if (sampleCount > 0)
            {
                min = values[0];
                max = values[0];
                double sum = 0;
                for (int i = 0; i < sampleCount; i++)
                {
                    if (values[i] < min) min = values[i];
                    if (values[i] > max) max = values[i];
                    sum += values[i];
                    perFrame.Add(new JObject
                    {
                        ["frame"] = i,
                        ["value"] = values[i]
                    });
                }
                mean = sum / sampleCount;

                var sorted = (double[])values.Clone();
                Array.Sort(sorted);
                median = sorted[SortedIndex(sorted.Length, 0.5)];
                p95 = sorted[SortedIndex(sorted.Length, 0.95)];
            }

            var data = new JObject
            {
                ["stat_name"] = statName,
                ["category"] = category.Name,
                ["data_type"] = dataTypeStr,
                ["unit_type"] = unitTypeStr,
                ["frames_requested"] = frameCount,
                ["frames_sampled"] = sampleCount,
                ["capacity"] = capacity,
                ["aggregates"] = new JObject
                {
                    ["min"] = min,
                    ["max"] = max,
                    ["mean"] = mean,
                    ["median"] = median,
                    ["p95"] = p95
                },
                ["per_frame_samples"] = perFrame,
                ["play_mode"] = EditorApplication.isPlaying,
                ["timed_out"] = timedOut,
                ["hint"] = timedOut
                    ? $"Sampling timed out after {timeoutMs}ms — Editor.update fired only {framesSeen}/{frameCount} frames. Cause: Editor not repainting (no focus + not in Play Mode). Enter Play Mode via manage_editor:play_mode, or focus a SceneView/GameView to force repaints."
                    : (EditorApplication.isPlaying
                        ? "Sampled in Play Mode."
                        : "Sampled outside Play Mode — values may be zero or reflect Editor-idle costs only. Call manage_editor:play_mode to enter Play Mode first for meaningful runtime data.")
            };

            return ToolResponse.OkWithData(data,
                $"Sampled '{statName}' ({category.Name}) over {sampleCount} frames — mean={mean:F2}, min={min:F2}, max={max:F2}, p95={p95:F2} {unitTypeStr}.");
        }

        private static int SortedIndex(int length, double percentile)
        {
            if (length == 0) return 0;
            int idx = (int)Math.Ceiling(length * percentile) - 1;
            if (idx < 0) idx = 0;
            if (idx >= length) idx = length - 1;
            return idx;
        }

        /// <summary>
        /// Report the current Profiler ring buffer's [firstFrameIndex, lastFrameIndex] window.
        /// These are the absolute frame indices for which frame data is currently retained and
        /// can be passed to <c>read_frame</c>. Requires Profiler to be enabled (Profiler.enabled=true)
        /// and frames to have been captured (Play Mode + running, or an active Editor recording).
        /// </summary>
        private ToolResponse HandleGetFrameRange()
        {
            try
            {
                int firstIdx = UnityEditorInternal.ProfilerDriver.firstFrameIndex;
                int lastIdx = UnityEditorInternal.ProfilerDriver.lastFrameIndex;
                bool profilerEnabled = Profiler.enabled;
                bool driverEnabled = false;
                try { driverEnabled = UnityEditorInternal.ProfilerDriver.enabled; } catch { /* ignore */ }
                bool isPlaying = EditorApplication.isPlaying;
                int frameCount = (firstIdx < 0 || lastIdx < 0 || lastIdx < firstIdx) ? 0 : (lastIdx - firstIdx + 1);

                string hint;
                if (frameCount == 0)
                {
                    if (!driverEnabled)
                    {
                        hint = "Frame buffer empty because ProfilerDriver.enabled=false. Call manage_profiler:start_recording — that turns on BOTH Profiler.enabled (for runtime sampling) AND ProfilerDriver.enabled (for the Editor frame buffer read_frame needs).";
                    }
                    else if (!profilerEnabled)
                    {
                        hint = "ProfilerDriver enabled but Profiler.enabled=false. Call manage_profiler:start_recording to enable both.";
                    }
                    else
                    {
                        hint = "Both switches on, waiting for frames. Enter Play Mode (manage_editor:play_mode state=play) and let it run a few seconds. If Play Mode is already on, the Profiler Window may need to be open at least once in this Editor session for frame data to flow.";
                    }
                }
                else if (!isPlaying)
                {
                    hint = "Frame buffer contains Editor-recorded frames (no Play Mode). Values reflect Editor overhead, not runtime behavior.";
                }
                else
                {
                    hint = $"{frameCount} frame(s) available. Pass a value in [{firstIdx}, {lastIdx}] to read_frame (negative = relative to last, e.g. -1 = latest).";
                }

                var data = new JObject
                {
                    ["first_frame_index"] = firstIdx,
                    ["last_frame_index"] = lastIdx,
                    ["frame_count"] = frameCount,
                    ["profiler_enabled"] = profilerEnabled,
                    ["profiler_driver_enabled"] = driverEnabled,
                    ["is_playing"] = isPlaying,
                    ["hint"] = hint
                };
                return ToolResponse.OkWithData(data, $"Profiler frame range: [{firstIdx}, {lastIdx}] ({frameCount} frames retained).");
            }
            catch (Exception e)
            {
                return ToolResponse.Fail($"Failed to read ProfilerDriver frame range: {e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>
        /// Read a captured frame's per-thread marker tree via <see cref="UnityEditor.Profiling.HierarchyFrameDataView"/>.
        /// Returns the top-N root-level markers sorted by total-time desc, with self-time and call count.
        /// Uses ProfilerDriver.GetHierarchyFrameDataView which is stable public API since Unity 2019.3.
        /// </summary>
        /// <remarks>
        /// Marker enumeration walks HierarchyFrameDataView children of the root (item id 0), which
        /// represents the whole-frame root sample. We enumerate GetItemChildren, sort by total-time
        /// descending, and return up to <c>max_markers</c> entries. The view is disposed after read.
        /// </remarks>
        private ToolResponse HandleReadFrame(JObject parameters)
        {
            int firstIdx = UnityEditorInternal.ProfilerDriver.firstFrameIndex;
            int lastIdx = UnityEditorInternal.ProfilerDriver.lastFrameIndex;
            if (firstIdx < 0 || lastIdx < 0 || lastIdx < firstIdx)
            {
                bool driverOn = false;
                try { driverOn = UnityEditorInternal.ProfilerDriver.enabled; } catch { /* ignore */ }
                string tail = driverOn
                    ? " ProfilerDriver.enabled=true but no frames buffered yet — enter Play Mode and let it run a few seconds, or open the Profiler Window once."
                    : " ProfilerDriver.enabled=false — call manage_profiler:start_recording first (enables BOTH Profiler.enabled AND ProfilerDriver.enabled).";
                return ToolResponse.Fail("No profiler frames available." + tail + " Call get_frame_range for full diagnostic state.");
            }

            // Resolve frame_index: absolute, or negative = relative-to-last (-1 = last).
            int frameIdxParam = ToolHelpers.GetOptionalInt(parameters, "frame_index", -1);
            int frameIndex = frameIdxParam < 0
                ? Math.Max(firstIdx, lastIdx + 1 + frameIdxParam) // -1 -> lastIdx, -2 -> lastIdx-1, ...
                : frameIdxParam;
            if (frameIndex < firstIdx || frameIndex > lastIdx)
            {
                return ToolResponse.Fail(
                    $"frame_index {frameIndex} out of range [{firstIdx}, {lastIdx}]. Use get_frame_range to see current window.");
            }

            int threadIndex = ToolHelpers.GetOptionalInt(parameters, "thread_index", 0);
            int maxMarkers = Math.Max(1, Math.Min(200, ToolHelpers.GetOptionalInt(parameters, "max_markers", 30)));
            // v1.8.4: depth = 递归层数 (1 = 只根级, 与 v1.8.3 及以前完全一致; max 5 防爆炸)
            int depth = Math.Max(1, Math.Min(5, ToolHelpers.GetOptionalInt(parameters, "depth", 1)));
            // v1.8.4: 只展开 total_ms >= min_ms 的节点子孙, 大幅降低响应体量
            double minMs = 0.0;
            var minMsToken = parameters?["min_ms"];
            if (minMsToken != null && minMsToken.Type != JTokenType.Null)
            {
                try { minMs = Math.Max(0.0, minMsToken.Value<double>()); } catch { /* ignore */ }
            }

            UnityEditor.Profiling.HierarchyFrameDataView view = null;
            try
            {
                view = UnityEditorInternal.ProfilerDriver.GetHierarchyFrameDataView(
                    frameIndex,
                    threadIndex,
                    UnityEditor.Profiling.HierarchyFrameDataView.ViewModes.Default,
                    (int)UnityEditor.Profiling.HierarchyFrameDataView.columnTotalTime,
                    sortAscending: false);
                if (view == null || !view.valid)
                {
                    return ToolResponse.Fail(
                        $"HierarchyFrameDataView invalid for frame={frameIndex}, thread={threadIndex}. Thread may not exist in that frame — try thread_index=0 (Main Thread).");
                }

                var rootId = view.GetRootItemID();
                var childIds = new List<int>();
                view.GetItemChildren(rootId, childIds);

                // Read column metadata
                int colTotalTime = (int)UnityEditor.Profiling.HierarchyFrameDataView.columnTotalTime;
                int colSelfTime = (int)UnityEditor.Profiling.HierarchyFrameDataView.columnSelfTime;
                int colGcAlloc = (int)UnityEditor.Profiling.HierarchyFrameDataView.columnGcMemory;

                // v1.8.4: 递归构建 marker 树 (depth 层, 每层 top-max_markers, min_ms 过滤)
                // 统计计数由 helper 内部累计, 用于 hint / message.
                var stats = new MarkerBuildStats();
                var markers = BuildMarkerChildren(
                    view, rootId,
                    colTotalTime, colSelfTime, colGcAlloc,
                    depthRemaining: depth, maxPerLevel: maxMarkers, minMs: minMs, stats: stats);

                var frameFps = view.frameFps;
                var frameTimeMs = view.frameTimeMs;
                var threadName = view.threadName ?? $"thread_{threadIndex}";
                var threadGroup = view.threadGroupName ?? "";

                string hintText;
                if (depth == 1)
                {
                    hintText = childIds.Count > markers.Count
                        ? $"Truncated to top {markers.Count} of {childIds.Count} root markers by total-time desc. Raise max_markers to see more."
                        : "All root markers returned.";
                }
                else
                {
                    hintText = $"Recursive read with depth={depth}, max_markers={maxMarkers} per level, min_ms={minMs}. " +
                               $"Emitted {stats.EmittedCount} markers total across {stats.MaxDepthSeen} depth levels. " +
                               (stats.TruncatedByCap > 0 ? $"{stats.TruncatedByCap} nodes had children beyond top-N (see 'children_truncated' hints per marker). " : "") +
                               (stats.FilteredByMinMs > 0 ? $"{stats.FilteredByMinMs} nodes filtered by min_ms. " : "");
                }

                var data = new JObject
                {
                    ["frame_index"] = frameIndex,
                    ["thread_index"] = threadIndex,
                    ["thread_name"] = threadName,
                    ["thread_group"] = threadGroup,
                    ["frame_fps"] = frameFps,
                    ["frame_time_ms"] = frameTimeMs,
                    ["depth"] = depth,
                    ["min_ms"] = minMs,
                    ["total_root_markers"] = childIds.Count,
                    ["returned_markers"] = markers.Count,
                    ["emitted_markers_total"] = stats.EmittedCount,
                    ["markers"] = markers,
                    ["hint"] = hintText
                };
                return ToolResponse.OkWithData(data,
                    depth == 1
                        ? $"Frame {frameIndex} ({threadName}): {frameTimeMs:F2} ms @ {frameFps:F1} FPS, {markers.Count}/{childIds.Count} root markers."
                        : $"Frame {frameIndex} ({threadName}): {frameTimeMs:F2} ms @ {frameFps:F1} FPS, {stats.EmittedCount} markers over depth={depth}.");
            }
            catch (Exception e)
            {
                return ToolResponse.Fail($"Failed to read frame {frameIndex} (thread {threadIndex}): {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                view?.Dispose();
            }
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Try to read a static property from a type via reflection and add it to a JObject.
        /// </summary>
        private static void TryGetStaticProperty(Type type, string propertyName, JObject target, string jsonKey)
        {
            try
            {
                var prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
                if (prop != null)
                {
                    var value = prop.GetValue(null);
                    if (value != null)
                    {
                        if (value is float f)
                            target[jsonKey] = Math.Round(f, 4);
                        else if (value is double d)
                            target[jsonKey] = Math.Round(d, 4);
                        else if (value is int i)
                            target[jsonKey] = i;
                        else if (value is long l)
                            target[jsonKey] = l;
                        else
                            target[jsonKey] = value.ToString();
                    }
                }
            }
            catch
            {
                // Silently ignore reflection failures
            }
        }

        /// <summary>
        /// v1.8.4: HierarchyFrameDataView 子节点递归构建 helper.
        /// Emits top-<paramref name="maxPerLevel"/> children of <paramref name="parentId"/>
        /// sorted by total-time desc, filtered by min_ms, recurses up to <paramref name="depthRemaining"/> levels.
        /// </summary>
        /// <remarks>
        /// Design: 每层独立 top-N (不做全局 top-N), 因为用户视角是"这个 EditorLoop 内谁最贵",
        /// 而非"全帧最贵的 N 个 marker" (后者在 depth=1 时已实现). min_ms 过滤在这层判定,
        /// 过小节点连自己都不 emit (子孙也不递归), 大幅降低响应体积.
        /// </remarks>
        private static JArray BuildMarkerChildren(
            UnityEditor.Profiling.HierarchyFrameDataView view,
            int parentId,
            int colTotalTime, int colSelfTime, int colGcAlloc,
            int depthRemaining, int maxPerLevel, double minMs,
            MarkerBuildStats stats,
            int currentDepth = 1)
        {
            var result = new JArray();
            if (depthRemaining <= 0) return result;

            var childIds = new List<int>();
            view.GetItemChildren(parentId, childIds);
            if (childIds.Count == 0) return result;

            // childIds 已按 view 的 sortColumn (totalTime desc) 排序; 取前 maxPerLevel.
            int take = Math.Min(childIds.Count, maxPerLevel);
            if (childIds.Count > take) stats.TruncatedByCap++;

            for (int i = 0; i < take; i++)
            {
                int id = childIds[i];
                float totalMs = view.GetItemColumnDataAsFloat(id, colTotalTime);
                // min_ms filter: skip emission entirely (also skips recursion into descendants).
                if (totalMs < minMs)
                {
                    stats.FilteredByMinMs++;
                    continue;
                }

                var name = view.GetItemName(id) ?? "<unknown>";
                float selfMs = view.GetItemColumnDataAsFloat(id, colSelfTime);
                long calls = view.GetItemMergedSamplesCount(id);
                string gcStr = view.GetItemColumnData(id, colGcAlloc);

                var marker = new JObject
                {
                    ["name"] = name,
                    ["total_ms"] = totalMs,
                    ["self_ms"] = selfMs,
                    ["calls"] = calls,
                    ["gc_alloc"] = gcStr,
                    ["depth"] = currentDepth
                };

                // Recurse into children if we have depth budget left.
                if (depthRemaining > 1)
                {
                    var children = BuildMarkerChildren(
                        view, id,
                        colTotalTime, colSelfTime, colGcAlloc,
                        depthRemaining - 1, maxPerLevel, minMs, stats, currentDepth + 1);
                    if (children.Count > 0) marker["children"] = children;
                }

                result.Add(marker);
                stats.EmittedCount++;
                if (currentDepth > stats.MaxDepthSeen) stats.MaxDepthSeen = currentDepth;
            }
            return result;
        }

        /// <summary>
        /// v1.8.4: Marker tree build stats — used to enrich the read_frame response hint.
        /// </summary>
        private sealed class MarkerBuildStats
        {
            public int EmittedCount;       // 总共 emit 的 marker 数
            public int TruncatedByCap;     // 有多少节点的子节点数 > maxPerLevel (被截断)
            public int FilteredByMinMs;    // 有多少子节点因 total_ms < min_ms 被过滤
            public int MaxDepthSeen;       // 实际 emit 到的最深层数
        }

        #endregion

        #region FrameDebugger (G03) — reflection into UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility (Unity 2022.3.x)

        // Cached reflection handles (resolved lazily on first FrameDebugger action)
        private static Type _fdUtilType;
        private static Type _fdEventType;
        private static Type _fdEventDataType;
        private static Type _fdEventTypeEnum;
        private static PropertyInfo _fdCountProp;
        private static PropertyInfo _fdLocallySupportedProp;
        private static MethodInfo _fdSetEnabledMethod;
        private static MethodInfo _fdGetFrameEventsMethod;
        private static MethodInfo _fdGetFrameEventDataMethod;
        private static MethodInfo _fdGetFrameEventInfoNameMethod;
        private static MethodInfo _fdGetFrameEventObjectMethod;
        private static bool _fdReflectionResolved;
        private static string _fdReflectionError;

        private static bool ResolveFrameDebuggerReflection()
        {
            if (_fdReflectionResolved) return _fdReflectionError == null;
            _fdReflectionResolved = true;

            try
            {
                var asm = typeof(UnityEditor.EditorWindow).Assembly;
                _fdUtilType = asm.GetType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility");
                _fdEventType = asm.GetType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEvent");
                _fdEventDataType = asm.GetType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData");
                _fdEventTypeEnum = asm.GetType("UnityEditorInternal.FrameDebuggerInternal.FrameEventType");

                if (_fdUtilType == null) { _fdReflectionError = "FrameDebuggerUtility type not found"; return false; }
                if (_fdEventType == null) { _fdReflectionError = "FrameDebuggerEvent type not found"; return false; }
                if (_fdEventDataType == null) { _fdReflectionError = "FrameDebuggerEventData type not found"; return false; }

                var pubStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                _fdCountProp = _fdUtilType.GetProperty("count", pubStatic);
                _fdLocallySupportedProp = _fdUtilType.GetProperty("locallySupported", pubStatic);
                _fdSetEnabledMethod = _fdUtilType.GetMethod("SetEnabled", pubStatic);
                _fdGetFrameEventsMethod = _fdUtilType.GetMethod("GetFrameEvents", pubStatic);
                _fdGetFrameEventDataMethod = _fdUtilType.GetMethod("GetFrameEventData", pubStatic);
                _fdGetFrameEventInfoNameMethod = _fdUtilType.GetMethod("GetFrameEventInfoName", pubStatic);
                _fdGetFrameEventObjectMethod = _fdUtilType.GetMethod("GetFrameEventObject", pubStatic);

                if (_fdCountProp == null) { _fdReflectionError = "FrameDebuggerUtility.count property not found"; return false; }
                if (_fdGetFrameEventsMethod == null) { _fdReflectionError = "FrameDebuggerUtility.GetFrameEvents method not found"; return false; }
                if (_fdGetFrameEventDataMethod == null) { _fdReflectionError = "FrameDebuggerUtility.GetFrameEventData method not found"; return false; }

                _fdReflectionError = null;
                return true;
            }
            catch (Exception ex)
            {
                _fdReflectionError = $"Reflection resolution threw {ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        private static bool FrameDebuggerSupported(out string reason)
        {
            reason = null;
            if (!ResolveFrameDebuggerReflection())
            {
                reason = $"FrameDebugger API unavailable (Unity {Application.unityVersion} may differ from the tested 2022.3.x). Reflection error: {_fdReflectionError}";
                return false;
            }
            if (_fdLocallySupportedProp != null)
            {
                try
                {
                    var supported = (bool)_fdLocallySupportedProp.GetValue(null);
                    if (!supported)
                    {
                        reason = "FrameDebugger.locallySupported=false — the current graphics API/platform does not support frame debugging locally.";
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    reason = $"Failed to read FrameDebuggerUtility.locallySupported: {ex.GetType().Name}: {ex.Message}";
                    return false;
                }
            }
            return true;
        }

        private static int GetFrameDebuggerCount()
        {
            return (int)_fdCountProp.GetValue(null);
        }

        private static bool TrySetFrameDebuggerEnabled(bool enabled, out string error)
        {
            error = null;
            if (_fdSetEnabledMethod == null)
            {
                error = "SetEnabled method not resolved via reflection.";
                return false;
            }
            try
            {
                // Signature: static void SetEnabled(bool enable, int index)
                //   `index` selects target player (0 = current editor / local). Passing 0 is safe for editor use.
                var parms = _fdSetEnabledMethod.GetParameters();
                object[] args;
                if (parms.Length == 2) args = new object[] { enabled, 0 };
                else if (parms.Length == 1) args = new object[] { enabled };
                else { error = $"Unexpected SetEnabled parameter count: {parms.Length}"; return false; }
                _fdSetEnabledMethod.Invoke(null, args);
                return true;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Enumerate GPU frame events captured by the Unity FrameDebugger.
        /// If no events are captured yet and enable_if_needed=true, auto-enables the debugger (requires Play Mode).
        /// </summary>
        private async Task<ToolResponse> HandleListDrawEvents(JObject parameters)
        {
            if (!FrameDebuggerSupported(out var reason)) return ToolResponse.Fail(reason);

            bool enableIfNeeded = ToolHelpers.GetOptionalBool(parameters, "enable_if_needed", true);
            int maxEvents = Math.Max(1, Math.Min(2000, ToolHelpers.GetOptionalInt(parameters, "max_events", 200)));

            int count = GetFrameDebuggerCount();
            bool didEnable = false;
            string enableNote = null;
            if (count == 0 && enableIfNeeded)
            {
                if (!Application.isPlaying)
                {
                    return ToolResponse.Fail(
                        "FrameDebugger has 0 events and Play Mode is not active. FrameDebugger typically requires Play Mode to capture events. " +
                        "Enter Play Mode (manage_editor action=play_mode state=play) then retry, or set enable_if_needed=false to inspect events already captured (if any).");
                }
                if (!TrySetFrameDebuggerEnabled(true, out var err))
                {
                    return ToolResponse.Fail($"Failed to enable FrameDebugger: {err}");
                }
                didEnable = true;
                // The frame debugger captures the NEXT frame after being enabled. Force a repaint so events populate,
                // then yield to the Editor for one tick (via delayCall) instead of blocking the main thread with a sleep.
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
                var tcs = new TaskCompletionSource<int>();
                EditorApplication.delayCall += () => tcs.TrySetResult(GetFrameDebuggerCount());
                count = await tcs.Task;
                enableNote = didEnable ? "FrameDebugger auto-enabled — GameView is now in debug mode until disable_frame_debugger is called. Events may not populate until the next Editor repaint." : null;
            }

            var events = new JArray();
            if (count > 0)
            {
                int limit = Math.Min(count, maxEvents);
                Array frameEvents = null;
                try
                {
                    frameEvents = (Array)_fdGetFrameEventsMethod.Invoke(null, null);
                }
                catch (Exception ex)
                {
                    return ToolResponse.Fail($"GetFrameEvents() threw: {ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}");
                }

                var typeField = _fdEventType.GetField("m_Type", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var objField = _fdEventType.GetField("m_Obj", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                int arrLen = frameEvents != null ? frameEvents.Length : 0;
                int scanCount = Math.Min(arrLen, limit);
                for (int i = 0; i < scanCount; i++)
                {
                    object ev = frameEvents.GetValue(i);
                    string typeName = "?";
                    int typeVal = -1;
                    if (typeField != null)
                    {
                        var v = typeField.GetValue(ev);
                        if (v != null)
                        {
                            typeName = v.ToString();
                            try { typeVal = Convert.ToInt32(v); } catch { }
                        }
                    }
                    UnityEngine.Object obj = null;
                    if (objField != null)
                    {
                        obj = objField.GetValue(ev) as UnityEngine.Object;
                    }
                    events.Add(new JObject
                    {
                        ["index"] = i,
                        ["type"] = typeName,
                        ["typeValue"] = typeVal,
                        ["objectName"] = obj != null ? obj.name : null,
                        ["objectType"] = obj != null ? obj.GetType().Name : null
                    });
                }
            }

            var data = new JObject
            {
                ["event_count"] = count,
                ["returned"] = events.Count,
                ["truncated"] = count > events.Count,
                ["auto_enabled"] = didEnable,
                ["events"] = events
            };
            if (enableNote != null) data["enable_note"] = enableNote;
            if (count == 0)
            {
                data["hint"] = "No frame events captured. FrameDebugger needs Play Mode + a rendering frame after SetEnabled(true). " +
                              "Try: manage_editor action=play_mode state=play, wait a few seconds, then retry list_draw_events. " +
                              "Remember to call disable_frame_debugger when done — GameView stays in debug mode otherwise.";
            }

            var msg = count == 0
                ? "0 frame events captured (see hint)."
                : $"{count} frame event(s) captured, returned {events.Count}.";
            return ToolResponse.OkWithData(data, msg);
        }

        /// <summary>
        /// Return detailed data for a single FrameDebugger event by index — shader/pass/keywords, draw stats, pipeline state, render target.
        /// </summary>
        private ToolResponse HandleGetDrawEvent(JObject parameters)
        {
            if (!FrameDebuggerSupported(out var reason)) return ToolResponse.Fail(reason);

            if (!parameters.TryGetValue("event_index", out var idxTok))
                return ToolResponse.Fail("Missing required parameter: event_index");
            int eventIndex = idxTok.Value<int>();

            int count = GetFrameDebuggerCount();
            if (count == 0)
                return ToolResponse.Fail("FrameDebugger has 0 events. Call list_draw_events with enable_if_needed=true (Play Mode required) first.");

            if (eventIndex < 0) eventIndex = count + eventIndex;
            if (eventIndex < 0 || eventIndex >= count)
                return ToolResponse.Fail($"event_index {idxTok.Value<int>()} out of range [0, {count - 1}].");

            object eventData;
            try
            {
                // Some builds signature: bool GetFrameEventData(int index, [out] FrameDebuggerEventData data)
                //                        or: FrameDebuggerEventData GetFrameEventData(int index)
                var parms = _fdGetFrameEventDataMethod.GetParameters();
                if (parms.Length == 1 && _fdGetFrameEventDataMethod.ReturnType == typeof(bool))
                {
                    // Rare — but signature was pure returning FrameDebuggerEventData in probe.
                    eventData = null;
                }

                if (_fdGetFrameEventDataMethod.ReturnType == _fdEventDataType)
                {
                    eventData = _fdGetFrameEventDataMethod.Invoke(null, new object[] { eventIndex });
                }
                else if (parms.Length == 2 && parms[1].ParameterType.IsByRef)
                {
                    // out parameter style
                    var argsArr = new object[] { eventIndex, null };
                    _fdGetFrameEventDataMethod.Invoke(null, argsArr);
                    eventData = argsArr[1];
                }
                else
                {
                    eventData = _fdGetFrameEventDataMethod.Invoke(null, new object[] { eventIndex });
                }
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"GetFrameEventData({eventIndex}) threw: {ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}");
            }

            if (eventData == null)
                return ToolResponse.Fail($"GetFrameEventData({eventIndex}) returned null.");

            var data = new JObject { ["event_index"] = eventIndex };
            var instFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            // Helper local functions to read a field by name safely
            Func<string, object> readField = (name) =>
            {
                var f = _fdEventDataType.GetField(name, instFlags);
                return f != null ? f.GetValue(eventData) : null;
            };
            Action<string, string> addPrim = (jsonKey, fieldName) =>
            {
                var v = readField(fieldName);
                if (v == null) return;
                if (v is UnityEngine.Object uo) data[jsonKey] = uo != null ? uo.name : null;
                else if (v is System.Array arr) data[jsonKey] = new JArray(arr.Cast<object>().Select(o => o?.ToString() ?? ""));
                else data[jsonKey] = JToken.FromObject(v);
            };

            // Draw stats
            addPrim("vertexCount", "m_VertexCount");
            addPrim("indexCount", "m_IndexCount");
            addPrim("instanceCount", "m_InstanceCount");
            addPrim("drawCallCount", "m_DrawCallCount");
            // Shader
            addPrim("originalShaderName", "m_OriginalShaderName");
            addPrim("realShaderName", "m_RealShaderName");
            addPrim("passName", "m_PassName");
            addPrim("passLightMode", "m_PassLightMode");
            addPrim("shaderInstanceId", "m_ShaderInstanceID");
            addPrim("subShaderIndex", "m_SubShaderIndex");
            addPrim("shaderPassIndex", "m_ShaderPassIndex");
            addPrim("shaderKeywords", "shaderKeywords");
            // Mesh
            addPrim("meshName", "m_Mesh");
            addPrim("meshSubset", "m_MeshSubset");
            addPrim("meshInstanceId", "m_MeshInstanceID");
            // Batch break
            addPrim("batchBreakCause", "m_BatchBreakCause");
            // Render target
            addPrim("rtName", "m_RenderTargetName");
            addPrim("rtWidth", "m_RenderTargetWidth");
            addPrim("rtHeight", "m_RenderTargetHeight");
            addPrim("rtFormat", "m_RenderTargetFormat");
            addPrim("rtCount", "m_RenderTargetCount");
            addPrim("rtIsBackBuffer", "m_RenderTargetIsBackBuffer");
            // Pipeline state — dump the nested structs to JSON via JToken.FromObject where possible
            var pipeline = new JObject();
            foreach (var name in new[] { "m_BlendState", "m_RasterState", "m_DepthState", "m_StencilState" })
            {
                var f = _fdEventDataType.GetField(name, instFlags);
                if (f == null) continue;
                var val = f.GetValue(eventData);
                if (val == null) continue;
                var section = new JObject();
                foreach (var sf in val.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    var sv = sf.GetValue(val);
                    try { section[sf.Name] = sv == null ? JValue.CreateNull() : JToken.FromObject(sv); }
                    catch { section[sf.Name] = sv?.ToString() ?? ""; }
                }
                pipeline[name.TrimStart('m', '_')] = section;
            }
            data["pipelineState"] = pipeline;
            addPrim("stencilRef", "m_StencilRef");
            // Compute
            addPrim("computeShader", "m_ComputeShader");
            addPrim("computeKernelName", "m_ComputeKernelName");

            // Include the friendly event name via GetFrameEventInfoName if available
            if (_fdGetFrameEventInfoNameMethod != null)
            {
                try
                {
                    var infoName = _fdGetFrameEventInfoNameMethod.Invoke(null, new object[] { eventIndex });
                    if (infoName != null) data["eventInfoName"] = infoName.ToString();
                }
                catch { }
            }

            return ToolResponse.OkWithData(data, $"Event {eventIndex}: {data["eventInfoName"] ?? data["realShaderName"] ?? "(no shader)"}");
        }

        /// <summary>
        /// Disable the FrameDebugger — GameView exits debug mode.
        /// </summary>
        private ToolResponse HandleDisableFrameDebugger()
        {
            if (!FrameDebuggerSupported(out var reason)) return ToolResponse.Fail(reason);
            if (!TrySetFrameDebuggerEnabled(false, out var err))
                return ToolResponse.Fail($"Failed to disable FrameDebugger: {err}");
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            return ToolResponse.OkWithData(new JObject
            {
                ["disabled"] = true,
                ["remaining_events"] = GetFrameDebuggerCount()
            }, "FrameDebugger disabled. GameView returns to normal on next repaint.");
        }

        #endregion
    }
}
