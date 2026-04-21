using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;
using AgentCore.Editor.Tools.Infrastructure;

namespace AgentCore.Editor.Tools.Native.Extended
{
    /// <summary>
    /// Manage build settings, build targets, and trigger builds.
    /// Provides access to build configuration, scene management, and player settings.
    /// </summary>
    [AgentTool("manage_build",
        Description = "Manage build settings, build targets, and trigger builds",
        Category = "extended",
        RequiresMainThread = true)]
    public class ManageBuildTool : IAgentTool
    {
        #region Schema

        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""get_settings"", ""set_target"", ""get_scenes"", ""set_scenes"", ""build"", ""get_player_settings""],
                    ""description"": ""Action to perform""
                },
                ""target"": {
                    ""type"": ""string"",
                    ""enum"": [""windows"", ""mac"", ""linux"", ""android"", ""ios"", ""webgl"", ""switch"", ""ps4"", ""ps5"", ""xbox""],
                    ""description"": ""Build target platform (for set_target action)""
                },
                ""subtarget"": {
                    ""type"": ""string"",
                    ""description"": ""Build subtarget (for set_target action, optional)""
                },
                ""scenes"": {
                    ""type"": ""array"",
                    ""items"": { ""type"": ""string"" },
                    ""description"": ""Scene paths to include in build (for set_scenes action)""
                },
                ""enabled"": {
                    ""type"": ""array"",
                    ""items"": { ""type"": ""boolean"" },
                    ""description"": ""Whether each scene is enabled (for set_scenes action, optional)""
                },
                ""output_path"": {
                    ""type"": ""string"",
                    ""description"": ""Output path for the build (for build action)""
                },
                ""options"": {
                    ""type"": ""array"",
                    ""items"": {
                        ""type"": ""string"",
                        ""enum"": [""development"", ""auto_run"", ""show_folder"", ""clean""]
                    },
                    ""description"": ""Build options (for build action, optional)""
                },
                ""platform"": {
                    ""type"": ""string"",
                    ""description"": ""Platform for player settings query (for get_player_settings action, optional)""
                }
            },
            ""required"": [""action""]
        }");

        #endregion

        // Build target mapping
        private static readonly Dictionary<string, BuildTarget> TargetMap = new Dictionary<string, BuildTarget>(StringComparer.OrdinalIgnoreCase)
        {
            { "windows", BuildTarget.StandaloneWindows64 },
            { "mac", BuildTarget.StandaloneOSX },
            { "linux", BuildTarget.StandaloneLinux64 },
            { "android", BuildTarget.Android },
            { "ios", BuildTarget.iOS },
            { "webgl", BuildTarget.WebGL },
        };

        // Build target group mapping
        private static readonly Dictionary<string, BuildTargetGroup> TargetGroupMap = new Dictionary<string, BuildTargetGroup>(StringComparer.OrdinalIgnoreCase)
        {
            { "windows", BuildTargetGroup.Standalone },
            { "mac", BuildTargetGroup.Standalone },
            { "linux", BuildTargetGroup.Standalone },
            { "android", BuildTargetGroup.Android },
            { "ios", BuildTargetGroup.iOS },
            { "webgl", BuildTargetGroup.WebGL },
        };

        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_build",
            description: "Manage build settings, build targets, and trigger builds",
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
                    case "get_settings":
                        response = HandleGetSettings();
                        break;
                    case "set_target":
                        response = HandleSetTarget(parameters);
                        break;
                    case "get_scenes":
                        response = HandleGetScenes();
                        break;
                    case "set_scenes":
                        response = HandleSetScenes(parameters);
                        break;
                    case "build":
                        response = HandleBuild(parameters);
                        break;
                    case "get_player_settings":
                        response = HandleGetPlayerSettings(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: get_settings, set_target, get_scenes, set_scenes, build, get_player_settings");
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

        private ToolResponse HandleGetSettings()
        {
            var activeBuildTarget = EditorUserBuildSettings.activeBuildTarget;
            var buildTargetGroup = EditorUserBuildSettings.selectedBuildTargetGroup;

            var scenes = EditorBuildSettings.scenes;
            var sceneList = new JArray();
            foreach (var scene in scenes)
            {
                sceneList.Add(new JObject
                {
                    ["path"] = scene.path,
                    ["enabled"] = scene.enabled,
                    ["guid"] = scene.guid.ToString()
                });
            }

            var data = new JObject
            {
                ["active_build_target"] = activeBuildTarget.ToString(),
                ["build_target_group"] = buildTargetGroup.ToString(),
                ["scenes_in_build"] = sceneList,
                ["scene_count"] = scenes.Length,
                ["enabled_scene_count"] = scenes.Count(s => s.enabled),
                ["scripting_backend"] = PlayerSettings.GetScriptingBackend(buildTargetGroup).ToString(),
                ["api_compatibility_level"] = PlayerSettings.GetApiCompatibilityLevel(buildTargetGroup).ToString(),
                ["development_build"] = EditorUserBuildSettings.development,
                ["il2cpp_compiler_configuration"] = PlayerSettings.GetIl2CppCompilerConfiguration(buildTargetGroup).ToString(),
                ["company_name"] = PlayerSettings.companyName,
                ["product_name"] = PlayerSettings.productName,
                ["bundle_version"] = PlayerSettings.bundleVersion
            };

            return ToolResponse.OkWithData(data, "Build settings retrieved successfully.");
        }

        private ToolResponse HandleSetTarget(JObject parameters)
        {
            var targetStr = ToolHelpers.GetRequiredString(parameters, "target").ToLowerInvariant();

            if (!TargetMap.TryGetValue(targetStr, out var buildTarget))
            {
                // Try platform-specific targets via reflection for console platforms
                var validTargets = string.Join(", ", TargetMap.Keys);
                return ToolResponse.Fail(
                    $"Unknown or unsupported build target: '{targetStr}'. Supported targets: {validTargets}. Console platforms (switch, ps4, ps5, xbox) require platform-specific SDK modules.");
            }

            if (!TargetGroupMap.TryGetValue(targetStr, out var buildTargetGroup))
            {
                return ToolResponse.Fail($"No target group mapping for: '{targetStr}'.");
            }

            var currentTarget = EditorUserBuildSettings.activeBuildTarget;
            if (currentTarget == buildTarget)
            {
                return ToolResponse.Ok($"Build target is already set to {buildTarget}.");
            }

            bool success = EditorUserBuildSettings.SwitchActiveBuildTarget(buildTargetGroup, buildTarget);

            if (success)
            {
                var data = new JObject
                {
                    ["previous_target"] = currentTarget.ToString(),
                    ["new_target"] = buildTarget.ToString(),
                    ["target_group"] = buildTargetGroup.ToString()
                };
                return ToolResponse.OkWithData(data, $"Build target switched to {buildTarget}.");
            }
            else
            {
                return ToolResponse.Fail($"Failed to switch build target to {buildTarget}. Ensure the platform module is installed.");
            }
        }

        private ToolResponse HandleGetScenes()
        {
            var scenes = EditorBuildSettings.scenes;
            var sceneList = new JArray();

            for (int i = 0; i < scenes.Length; i++)
            {
                sceneList.Add(new JObject
                {
                    ["index"] = i,
                    ["path"] = scenes[i].path,
                    ["enabled"] = scenes[i].enabled,
                    ["guid"] = scenes[i].guid.ToString()
                });
            }

            var data = new JObject
            {
                ["scenes"] = sceneList,
                ["total_count"] = scenes.Length,
                ["enabled_count"] = scenes.Count(s => s.enabled)
            };

            return ToolResponse.OkWithData(data, "Build scenes retrieved successfully.");
        }

        private ToolResponse HandleSetScenes(JObject parameters)
        {
            var scenesArray = ToolHelpers.GetOptionalArray(parameters, "scenes");
            if (scenesArray == null || scenesArray.Count == 0)
            {
                return ToolResponse.Fail("Parameter 'scenes' is required and must be a non-empty array of scene paths.");
            }

            var enabledArray = ToolHelpers.GetOptionalArray(parameters, "enabled");

            var newScenes = new List<EditorBuildSettingsScene>();
            for (int i = 0; i < scenesArray.Count; i++)
            {
                var path = scenesArray[i].ToString();
                bool enabled = true;

                if (enabledArray != null && i < enabledArray.Count)
                {
                    enabled = enabledArray[i].Value<bool>();
                }

                newScenes.Add(new EditorBuildSettingsScene(path, enabled));
            }

            EditorBuildSettings.scenes = newScenes.ToArray();

            var data = new JObject
            {
                ["scene_count"] = newScenes.Count,
                ["enabled_count"] = newScenes.Count(s => s.enabled)
            };

            return ToolResponse.OkWithData(data, $"Build scenes updated. {newScenes.Count} scene(s) configured.");
        }

        private ToolResponse HandleBuild(JObject parameters)
        {
            var outputPath = ToolHelpers.GetRequiredString(parameters, "output_path");
            var optionsArray = ToolHelpers.GetOptionalArray(parameters, "options");

            // Parse build options
            var buildOptions = BuildOptions.None;
            if (optionsArray != null)
            {
                foreach (var opt in optionsArray)
                {
                    var optStr = opt.ToString().ToLowerInvariant();
                    switch (optStr)
                    {
                        case "development":
                            buildOptions |= BuildOptions.Development;
                            break;
                        case "auto_run":
                            buildOptions |= BuildOptions.AutoRunPlayer;
                            break;
                        case "show_folder":
                            buildOptions |= BuildOptions.ShowBuiltPlayer;
                            break;
                        case "clean":
                            buildOptions |= BuildOptions.CleanBuildCache;
                            break;
                    }
                }
            }

            // Get enabled scenes
            var enabledScenes = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray();

            if (enabledScenes.Length == 0)
            {
                return ToolResponse.Fail("No enabled scenes in build settings. Add scenes first using set_scenes action.");
            }

            // Ensure output directory exists
            var outputDir = System.IO.Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir) && !System.IO.Directory.Exists(outputDir))
            {
                System.IO.Directory.CreateDirectory(outputDir);
            }

            // Trigger build
            var report = BuildPipeline.BuildPlayer(enabledScenes, outputPath, EditorUserBuildSettings.activeBuildTarget, buildOptions);

            var data = new JObject
            {
                ["output_path"] = outputPath,
                ["target"] = EditorUserBuildSettings.activeBuildTarget.ToString(),
                ["options"] = buildOptions.ToString(),
                ["scene_count"] = enabledScenes.Length,
                ["result"] = report.summary.result.ToString(),
                ["total_errors"] = report.summary.totalErrors,
                ["total_warnings"] = report.summary.totalWarnings,
                ["total_time_seconds"] = Math.Round(report.summary.totalTime.TotalSeconds, 2),
                ["total_size_bytes"] = report.summary.totalSize
            };

            if (report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                return ToolResponse.OkWithData(data, "Build completed successfully.");
            }
            else
            {
                // Include error steps
                var errors = new JArray();
                foreach (var step in report.steps)
                {
                    foreach (var msg in step.messages)
                    {
                        if (msg.type == LogType.Error)
                        {
                            errors.Add(new JObject
                            {
                                ["step"] = step.name,
                                ["message"] = msg.content
                            });
                        }
                    }
                }
                data["errors"] = errors;
                return ToolResponse.OkWithData(data, $"Build failed with {report.summary.totalErrors} error(s).");
            }
        }

        private ToolResponse HandleGetPlayerSettings(JObject parameters)
        {
            var platform = ToolHelpers.GetOptionalString(parameters, "platform");
            var buildTargetGroup = EditorUserBuildSettings.selectedBuildTargetGroup;

            // If platform specified, try to resolve target group
            if (!string.IsNullOrEmpty(platform) && TargetGroupMap.TryGetValue(platform, out var specifiedGroup))
            {
                buildTargetGroup = specifiedGroup;
            }

            var data = new JObject
            {
                ["company_name"] = PlayerSettings.companyName,
                ["product_name"] = PlayerSettings.productName,
                ["bundle_version"] = PlayerSettings.bundleVersion,
                ["default_icon"] = PlayerSettings.GetIconsForTargetGroup(BuildTargetGroup.Unknown)?.Length > 0 ? "set" : "not set",
                ["color_space"] = PlayerSettings.colorSpace.ToString(),
                ["scripting_backend"] = PlayerSettings.GetScriptingBackend(buildTargetGroup).ToString(),
                ["api_compatibility_level"] = PlayerSettings.GetApiCompatibilityLevel(buildTargetGroup).ToString(),
                ["target_group"] = buildTargetGroup.ToString(),
                ["run_in_background"] = PlayerSettings.runInBackground,
                ["visible_in_background"] = PlayerSettings.visibleInBackground,
                ["allow_fullscreen_switch"] = PlayerSettings.allowFullscreenSwitch,
                ["default_is_fullscreen"] = PlayerSettings.defaultIsNativeResolution,
                ["default_screen_width"] = PlayerSettings.defaultScreenWidth,
                ["default_screen_height"] = PlayerSettings.defaultScreenHeight,
                ["fullscreen_mode"] = PlayerSettings.fullScreenMode.ToString(),
                ["use_player_log"] = PlayerSettings.usePlayerLog,
                ["resizable_window"] = PlayerSettings.resizableWindow
            };

            // Platform-specific settings
            if (buildTargetGroup == BuildTargetGroup.Android)
            {
                data["android"] = new JObject
                {
                    ["min_sdk_version"] = PlayerSettings.Android.minSdkVersion.ToString(),
                    ["target_sdk_version"] = PlayerSettings.Android.targetSdkVersion.ToString(),
                    ["package_name"] = PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.Android),
                    ["target_architectures"] = PlayerSettings.Android.targetArchitectures.ToString()
                };
            }
            else if (buildTargetGroup == BuildTargetGroup.iOS)
            {
                data["ios"] = new JObject
                {
                    ["target_sdk"] = PlayerSettings.iOS.sdkVersion.ToString(),
                    ["target_os_version"] = PlayerSettings.iOS.targetOSVersionString,
                    ["bundle_identifier"] = PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.iOS)
                };
            }

            return ToolResponse.OkWithData(data, "Player settings retrieved successfully.");
        }

        #endregion
    }
}
