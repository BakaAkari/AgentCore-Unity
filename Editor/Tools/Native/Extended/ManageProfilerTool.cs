using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using UnityEngine;
using UnityEngine.Profiling;
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
        Description = "Access Unity Profiler data for performance analysis and optimization",
        Category = "extended",
        RequiresMainThread = true,
        RiskLevel = ToolRiskLevel.Low,
        Capabilities = ToolCapability.ReadProject)]
    public class ManageProfilerTool : IAgentTool
    {
        #region Schema

        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""get_stats"", ""get_memory"", ""start_recording"", ""stop_recording"", ""get_rendering_stats""],
                    ""description"": ""Action to perform""
                },
                ""log_file"": {
                    ""type"": ""string"",
                    ""description"": ""File path to save profiler data (for start_recording action, optional)""
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

        public Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
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
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: get_stats, get_memory, start_recording, stop_recording, get_rendering_stats");
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

            var data = new JObject
            {
                ["recording"] = true,
                ["log_file"] = logFile ?? "(none - in-memory only)"
            };

            return ToolResponse.OkWithData(data, "Profiler recording started.");
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

        #endregion
    }
}
