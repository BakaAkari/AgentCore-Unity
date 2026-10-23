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
using AgentCore.Editor.Tools.Safety;

namespace AgentCore.Editor.Tools.Native.Extended
{
    /// <summary>
    /// Manage build settings, build targets, and trigger builds.
    /// Provides access to build configuration, scene management, and player settings.
    /// </summary>
    [AgentTool("manage_build",
        Description = "Unity build pipeline control. " +
                      "Actions: get_settings (current build target, scenes, defines), set_target (switch platform), " +
                      "build (trigger actual build to output path), add_scene/remove_scene (manage build scene list), " +
                      "get_defines/set_defines (scripting define symbols). " +
                      "USE FOR: switching build platform, triggering builds, managing which scenes are in the build, " +
                      "reading/writing scripting define symbols (e.g. adding feature flags). " +
                      "NOT FOR: player settings like resolution/icon (use manage_editor get_project_settings), " +
                      "asset bundle building, addressable builds. " +
                      "REQUIRES CONFIRMATION: build action triggers actual compilation and may take minutes. " +
                      "ACTIVATE WHEN: user mentions 'build', 'platform switch', 'build settings', 'scripting defines', 'build scenes'.",
        Category = "Extended",
        RequiresMainThread = true,
        RiskLevel = ToolRiskLevel.High,
        Capabilities = ToolCapability.BuildPlayer | ToolCapability.ModifyProjectSettings,
        ReadOnlyActions = new[] { "get_player_settings", "get_scenes", "get_settings" },
        RequiresConfirmation = true,
        Visibility = ToolVisibility.OnDemand)]
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

            // 以切换后的实际 activeBuildTarget 为准，返回值为 true 但平台没变也视为失败
            var actualTarget = EditorUserBuildSettings.activeBuildTarget;
            if (success && actualTarget != buildTarget)
            {
                return ToolResponse.Fail(
                    $"SwitchActiveBuildTarget reported success but the active build target is still {actualTarget} (requested {buildTarget}). " +
                    "The switch may still be pending or was rejected; check the Unity Console.");
            }

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

            // 写入行为不变（缺失的场景也照常写入）；只是如实回报哪些条目指向不存在的场景
            var appliedCount = EditorBuildSettings.scenes.Length;
            var items = new List<ToolItemResult>();
            foreach (var scene in newScenes)
            {
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(scene.path) != null)
                    items.Add(ToolItemResult.Succeeded(scene.path));
                else
                    items.Add(ToolItemResult.Failed(scene.path,
                        "scene asset not found at this path (the entry was still written to build settings and will break builds until fixed)"));
            }

            var data = new JObject
            {
                ["scene_count"] = appliedCount,
                ["enabled_count"] = EditorBuildSettings.scenes.Count(s => s.enabled)
            };

            if (appliedCount != newScenes.Count)
            {
                return ToolResponse.Fail(
                    $"Build scene list was not applied as requested: requested {newScenes.Count} scene(s), build settings now contain {appliedCount}.");
            }

            if (items.Any(i => !i.Ok))
            {
                return ToolResponse.Partial(
                    $"Build scenes updated ({newScenes.Count} entries written), but some paths do not point to an existing scene",
                    items, data);
            }

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

            var summary = report.summary;
            var data = new JObject
            {
                ["output_path"] = outputPath,
                ["target"] = EditorUserBuildSettings.activeBuildTarget.ToString(),
                ["options"] = buildOptions.ToString(),
                ["scene_count"] = enabledScenes.Length,
                ["result"] = summary.result.ToString(),
                ["total_errors"] = summary.totalErrors,
                ["total_warnings"] = summary.totalWarnings,
                ["total_time_seconds"] = Math.Round(summary.totalTime.TotalSeconds, 2),
                ["total_size_bytes"] = summary.totalSize
            };

            // 成功与否只看 BuildReport.summary.result：仅 Succeeded 视为成功，
            // Failed / Cancelled / Unknown 一律报失败，绝不把失败的构建报成成功
            if (summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                return ToolResponse.OkWithData(data,
                    $"Build succeeded: {summary.totalErrors} error(s), {summary.totalWarnings} warning(s), " +
                    $"{Math.Round(summary.totalTime.TotalSeconds, 1)}s, output '{outputPath}'.");
            }

            // 失败结果只有 Error 文本会交给模型，因此关键信息（结果、计数、错误消息）全部写进 Error
            var errors = CollectBuildErrors(report, MaxReportedBuildErrors, out int errorMessageCount);
            string reason;
            switch (summary.result)
            {
                case UnityEditor.Build.Reporting.BuildResult.Cancelled:
                    reason = "Build was cancelled";
                    break;
                case UnityEditor.Build.Reporting.BuildResult.Failed:
                    reason = "Build failed";
                    break;
                default:
                    reason = $"Build result is '{summary.result}' — Unity did not confirm success, so the build must not be treated as successful";
                    break;
            }

            var sb = new System.Text.StringBuilder();
            sb.Append(reason)
              .Append($" (target {EditorUserBuildSettings.activeBuildTarget}, output '{outputPath}', ")
              .Append($"{summary.totalErrors} error(s), {summary.totalWarnings} warning(s), ")
              .Append($"{Math.Round(summary.totalTime.TotalSeconds, 1)}s).");
            if (errors.Count > 0)
            {
                sb.Append(" Errors:");
                foreach (var e in errors)
                    sb.Append("\n- [").Append(e["step"]).Append("] ").Append(e["message"]);
                if (errorMessageCount > errors.Count)
                    sb.Append($"\n... and {errorMessageCount - errors.Count} more error message(s) in the build report / Console.");
            }
            else if (summary.result == UnityEditor.Build.Reporting.BuildResult.Failed)
            {
                sb.Append(" No error messages were recorded in the build report; check the Unity Console for details.");
            }

            return ToolResponse.Fail(sb.ToString());
        }

        /// <summary>失败构建最多回报的错误消息条数（避免撑爆上下文）</summary>
        private const int MaxReportedBuildErrors = 20;

        /// <summary>
        /// 从 BuildReport 各步骤中收集错误消息（Error / Exception / Assert），最多 maxCount 条。
        /// </summary>
        private static List<JObject> CollectBuildErrors(UnityEditor.Build.Reporting.BuildReport report, int maxCount, out int totalCount)
        {
            var errors = new List<JObject>();
            totalCount = 0;
            foreach (var step in report.steps)
            {
                foreach (var msg in step.messages)
                {
                    if (msg.type != LogType.Error && msg.type != LogType.Exception && msg.type != LogType.Assert)
                        continue;
                    totalCount++;
                    if (errors.Count < maxCount)
                    {
                        errors.Add(new JObject
                        {
                            ["step"] = step.name,
                            ["message"] = msg.content
                        });
                    }
                }
            }
            return errors;
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
