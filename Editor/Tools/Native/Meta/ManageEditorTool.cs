using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Tools.Safety;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace AgentCore.Editor.Tools.Native.Meta
{
    /// <summary>
    /// Manage Unity Editor state, windows, play mode, and project settings.
    /// Use 'get_info' action to check editor status, connection, and project info (version, platform, scene, etc.).
    /// </summary>
    [AgentTool("manage_editor",
        Description = "Control Unity Editor state and access project-level settings. " +
            "Actions: get_info (editor version, platform, play state, active scene, render pipeline — use to verify environment), " +
            "play_mode (enter/exit/pause play mode), refresh (force asset reimport/recompile), " +
            "get_selection/set_selection (current editor selection), focus_window (bring editor windows to front), " +
            "get_project_settings/set_project_setting (PlayerSettings, Physics, Quality, etc.). " +
            "Use get_info as a first step to confirm editor connectivity and project state. " +
            "Use refresh after script changes to trigger recompilation. " +
            "NOT for: scene object operations (use manage_gameobject), build pipeline (use manage_build).",
        Category = "meta",
        RequiresMainThread = true,
        RiskLevel = ToolRiskLevel.Medium,
        Capabilities = ToolCapability.ModifyProjectSettings)]
    public class ManageEditorTool : IAgentTool
    {
        #region Schema

        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""get_info"", ""play_mode"", ""focus_window"", ""get_selection"", ""set_selection"", ""refresh"", ""get_project_settings"", ""set_project_setting""],
                    ""description"": ""Action to perform. Use 'get_info' to check editor status and connection (returns Unity version, platform, play state, active scene, render pipeline). Use 'play_mode' with 'state' param to control play/pause/stop/step. Use 'refresh' to reimport assets.""
                },
                ""state"": {
                    ""type"": ""string"",
                    ""enum"": [""play"", ""pause"", ""stop"", ""step""],
                    ""description"": ""Play mode state (for play_mode action)""
                },
                ""window"": {
                    ""type"": ""string"",
                    ""enum"": [""scene"", ""game"", ""inspector"", ""hierarchy"", ""project"", ""console""],
                    ""description"": ""Window to focus (for focus_window action)""
                },
                ""target"": {
                    ""type"": ""string"",
                    ""description"": ""Target GameObject name or asset path (for set_selection action)""
                },
                ""import_mode"": {
                    ""type"": ""string"",
                    ""enum"": [""default"", ""force""],
                    ""description"": ""Import mode for refresh action (default: default)""
                },
                ""category"": {
                    ""type"": ""string"",
                    ""enum"": [""player"", ""quality"", ""physics"", ""time"", ""audio"", ""all""],
                    ""description"": ""Settings category (for get_project_settings / set_project_setting)""
                },
                ""property"": {
                    ""type"": ""string"",
                    ""description"": ""Property name to set (for set_project_setting)""
                },
                ""value"": {
                    ""type"": ""string"",
                    ""description"": ""Value to set (for set_project_setting)""
                }
            },
            ""required"": [""action""]
        }");

        #endregion

        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_editor",
            description: "Manage Unity Editor state and project settings. Use 'get_info' to check editor status/connection (returns Unity version, platform, play state, active scene, render pipeline, etc.). Other actions: play_mode, focus_window, get_selection, set_selection, refresh, get_project_settings, set_project_setting",
            category: "meta",
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
                    case "get_info":
                        response = HandleGetInfo();
                        break;
                    case "play_mode":
                        response = HandlePlayMode(parameters);
                        break;
                    case "focus_window":
                        response = HandleFocusWindow(parameters);
                        break;
                    case "get_selection":
                        response = HandleGetSelection();
                        break;
                    case "set_selection":
                        response = HandleSetSelection(parameters);
                        break;
                    case "refresh":
                        response = HandleRefresh(parameters);
                        break;
                    case "get_project_settings":
                        response = HandleGetProjectSettings(parameters);
                        break;
                    case "set_project_setting":
                        response = HandleSetProjectSetting(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: get_info, play_mode, focus_window, get_selection, set_selection, refresh, get_project_settings, set_project_setting");
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

        private ToolResponse HandleGetInfo()
        {
            var activeScene = SceneManager.GetActiveScene();

            // Detect render pipeline
            string renderPipeline = "Built-in";
            var currentRP = GraphicsSettings.currentRenderPipeline;
            if (currentRP != null)
            {
                var rpName = currentRP.GetType().Name;
                if (rpName.Contains("Universal") || rpName.Contains("URP"))
                    renderPipeline = "URP";
                else if (rpName.Contains("HD") || rpName.Contains("HDRP"))
                    renderPipeline = "HDRP";
                else
                    renderPipeline = rpName;
            }

            var data = new JObject
            {
                ["unity_version"] = Application.unityVersion,
                ["project_path"] = Application.dataPath.Replace("/Assets", ""),
                ["project_name"] = Application.productName,
                ["platform"] = EditorUserBuildSettings.activeBuildTarget.ToString(),
                ["is_playing"] = EditorApplication.isPlaying,
                ["is_paused"] = EditorApplication.isPaused,
                ["is_compiling"] = EditorApplication.isCompiling,
                ["active_scene"] = new JObject
                {
                    ["name"] = activeScene.name,
                    ["path"] = activeScene.path,
                    ["is_dirty"] = activeScene.isDirty,
                    ["root_count"] = activeScene.rootCount
                },
                ["render_pipeline"] = renderPipeline,
                ["scripting_backend"] = PlayerSettings.GetScriptingBackend(EditorUserBuildSettings.selectedBuildTargetGroup).ToString(),
                ["color_space"] = PlayerSettings.colorSpace.ToString(),
                ["loaded_scene_count"] = SceneManager.sceneCount
            };

            return ToolResponse.OkWithData(data, "Editor info retrieved successfully.");
        }

        private ToolResponse HandlePlayMode(JObject parameters)
        {
            var state = ToolHelpers.GetRequiredString(parameters, "state").ToLowerInvariant();

            switch (state)
            {
                case "play":
                    if (EditorApplication.isPlaying)
                        return ToolResponse.Ok("Editor is already in play mode.");
                    EditorApplication.isPlaying = true;
                    return ToolResponse.Ok("Entering play mode.");

                case "pause":
                    if (!EditorApplication.isPlaying)
                        return ToolResponse.Fail("Cannot pause: Editor is not in play mode.");
                    EditorApplication.isPaused = !EditorApplication.isPaused;
                    return ToolResponse.Ok(EditorApplication.isPaused
                        ? "Editor paused."
                        : "Editor unpaused.");

                case "stop":
                    if (!EditorApplication.isPlaying)
                        return ToolResponse.Ok("Editor is already stopped.");
                    EditorApplication.isPlaying = false;
                    return ToolResponse.Ok("Stopping play mode.");

                case "step":
                    if (!EditorApplication.isPlaying)
                        return ToolResponse.Fail("Cannot step: Editor is not in play mode.");
                    EditorApplication.Step();
                    return ToolResponse.Ok("Stepped one frame.");

                default:
                    return ToolResponse.Fail(
                        $"Unknown state: '{state}'. Valid states: play, pause, stop, step");
            }
        }

        private ToolResponse HandleFocusWindow(JObject parameters)
        {
            var window = ToolHelpers.GetRequiredString(parameters, "window").ToLowerInvariant();

            Type windowType = null;
            string windowName = window;

            switch (window)
            {
                case "scene":
                    windowType = typeof(SceneView);
                    windowName = "Scene View";
                    break;
                case "game":
                    windowType = Type.GetType("UnityEditor.GameView,UnityEditor");
                    windowName = "Game View";
                    break;
                case "inspector":
                    windowType = Type.GetType("UnityEditor.InspectorWindow,UnityEditor");
                    windowName = "Inspector";
                    break;
                case "hierarchy":
                    windowType = Type.GetType("UnityEditor.SceneHierarchyWindow,UnityEditor");
                    windowName = "Hierarchy";
                    break;
                case "project":
                    windowType = Type.GetType("UnityEditor.ProjectBrowser,UnityEditor");
                    windowName = "Project";
                    break;
                case "console":
                    windowType = Type.GetType("UnityEditor.ConsoleWindow,UnityEditor");
                    windowName = "Console";
                    break;
                default:
                    return ToolResponse.Fail(
                        $"Unknown window: '{window}'. Valid windows: scene, game, inspector, hierarchy, project, console");
            }

            if (windowType == null)
            {
                return ToolResponse.Fail($"Could not resolve window type for '{window}'.");
            }

            var editorWindow = EditorWindow.GetWindow(windowType);
            if (editorWindow != null)
            {
                editorWindow.Focus();
                return ToolResponse.Ok($"Focused {windowName} window.");
            }

            return ToolResponse.Fail($"Could not open or focus {windowName} window.");
        }

        private ToolResponse HandleGetSelection()
        {
            var data = new JObject();

            // Active GameObject
            var activeGo = Selection.activeGameObject;
            if (activeGo != null)
            {
                data["active_gameobject"] = ToolHelpers.SerializeGameObject(activeGo, includeComponents: true);
            }
            else
            {
                data["active_gameobject"] = null;
            }

            // Active Object (could be an asset)
            var activeObj = Selection.activeObject;
            if (activeObj != null && activeObj != activeGo)
            {
                data["active_object"] = new JObject
                {
                    ["name"] = activeObj.name,
                    ["type"] = activeObj.GetType().Name,
                    ["instance_id"] = activeObj.GetInstanceID()
                };

                var assetPath = AssetDatabase.GetAssetPath(activeObj);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    data["active_object"]["asset_path"] = assetPath;
                }
            }

            // All selected objects
            var selectedObjects = Selection.objects;
            var selectedArray = new JArray();
            foreach (var obj in selectedObjects)
            {
                if (obj == null) continue;
                var item = new JObject
                {
                    ["name"] = obj.name,
                    ["type"] = obj.GetType().Name,
                    ["instance_id"] = obj.GetInstanceID()
                };
                var path = AssetDatabase.GetAssetPath(obj);
                if (!string.IsNullOrEmpty(path))
                    item["asset_path"] = path;
                selectedArray.Add(item);
            }
            data["selected_objects"] = selectedArray;
            data["selection_count"] = selectedObjects.Length;

            return ToolResponse.OkWithData(data,
                selectedObjects.Length > 0
                    ? $"Current selection: {selectedObjects.Length} object(s)."
                    : "No objects selected.");
        }

        private ToolResponse HandleSetSelection(JObject parameters)
        {
            var target = ToolHelpers.GetRequiredString(parameters, "target");

            // Try to find as GameObject in scene
            var go = ToolHelpers.FindGameObject(target);
            if (go != null)
            {
                Undo.RecordObject(Selection.activeGameObject != null ? (UnityEngine.Object)Selection.activeGameObject : go,
                    "AgentCore: Set Selection");
                Selection.activeGameObject = go;
                EditorGUIUtility.PingObject(go);
                return ToolResponse.OkWithData(new JObject
                {
                    ["name"] = go.name,
                    ["instance_id"] = go.GetInstanceID(),
                    ["type"] = "GameObject"
                }, $"Selected GameObject: '{go.name}'");
            }

            // Try to find as asset
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(target);
            if (asset != null)
            {
                Selection.activeObject = asset;
                EditorGUIUtility.PingObject(asset);
                return ToolResponse.OkWithData(new JObject
                {
                    ["name"] = asset.name,
                    ["instance_id"] = asset.GetInstanceID(),
                    ["type"] = asset.GetType().Name,
                    ["asset_path"] = target
                }, $"Selected asset: '{asset.name}'");
            }

            return ToolResponse.Fail($"Could not find GameObject or asset with target: '{target}'");
        }

        private ToolResponse HandleRefresh(JObject parameters)
        {
            var importMode = ToolHelpers.GetOptionalString(parameters, "import_mode", "default").ToLowerInvariant();

            if (importMode == "force")
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            }
            else
            {
                AssetDatabase.Refresh();
            }

            return ToolResponse.Ok($"Asset database refreshed (mode: {importMode}).");
        }

        private ToolResponse HandleGetProjectSettings(JObject parameters)
        {
            var category = ToolHelpers.GetOptionalString(parameters, "category", "all").ToLowerInvariant();
            var data = new JObject();

            if (category == "all" || category == "player")
            {
                data["player"] = new JObject
                {
                    ["company_name"] = PlayerSettings.companyName,
                    ["product_name"] = PlayerSettings.productName,
                    ["version"] = PlayerSettings.bundleVersion,
                    ["default_is_fullscreen"] = PlayerSettings.fullScreenMode.ToString(),
                    ["run_in_background"] = PlayerSettings.runInBackground,
                    ["scripting_backend"] = PlayerSettings.GetScriptingBackend(
                        EditorUserBuildSettings.selectedBuildTargetGroup).ToString(),
                    ["api_compatibility"] = PlayerSettings.GetApiCompatibilityLevel(
                        EditorUserBuildSettings.selectedBuildTargetGroup).ToString(),
                    ["color_space"] = PlayerSettings.colorSpace.ToString()
                };
            }

            if (category == "all" || category == "quality")
            {
                data["quality"] = new JObject
                {
                    ["current_level"] = QualitySettings.names[QualitySettings.GetQualityLevel()],
                    ["quality_level_index"] = QualitySettings.GetQualityLevel(),
                    ["available_levels"] = new JArray(QualitySettings.names),
                    ["vsync_count"] = QualitySettings.vSyncCount,
                    ["anti_aliasing"] = QualitySettings.antiAliasing,
                    ["shadow_resolution"] = QualitySettings.shadowResolution.ToString(),
                    ["texture_quality"] = QualitySettings.globalTextureMipmapLimit
                };
            }

            if (category == "all" || category == "physics")
            {
                data["physics"] = new JObject
                {
                    ["gravity"] = ToolHelpers.Vector3ToJson(Physics.gravity),
                    ["default_solver_iterations"] = Physics.defaultSolverIterations,
                    ["default_solver_velocity_iterations"] = Physics.defaultSolverVelocityIterations,
                    ["bounce_threshold"] = Physics.bounceThreshold,
                    ["sleep_threshold"] = Physics.sleepThreshold,
                    ["auto_simulation"] = Physics.simulationMode.ToString()
                };
            }

            if (category == "all" || category == "time")
            {
                data["time"] = new JObject
                {
                    ["fixed_timestep"] = Time.fixedDeltaTime,
                    ["maximum_timestep"] = Time.maximumDeltaTime,
                    ["time_scale"] = Time.timeScale,
                    ["maximum_particle_timestep"] = Time.maximumParticleDeltaTime
                };
            }

            if (category == "all" || category == "audio")
            {
                data["audio"] = new JObject
                {
                    ["global_volume"] = AudioListener.volume,
                    ["spatializer_plugin"] = AudioSettings.GetSpatializerPluginName(),
                    ["sample_rate"] = AudioSettings.outputSampleRate,
                    ["dsp_buffer_size"] = AudioSettings.GetConfiguration().dspBufferSize,
                    ["speaker_mode"] = AudioSettings.GetConfiguration().speakerMode.ToString()
                };
            }

            return ToolResponse.OkWithData(data, $"Project settings retrieved (category: {category}).");
        }

        private ToolResponse HandleSetProjectSetting(JObject parameters)
        {
            var category = ToolHelpers.GetRequiredString(parameters, "category").ToLowerInvariant();
            var property = ToolHelpers.GetRequiredString(parameters, "property").ToLowerInvariant();
            var value = ToolHelpers.GetRequiredString(parameters, "value");

            switch (category)
            {
                case "player":
                    return SetPlayerSetting(property, value);
                case "time":
                    return SetTimeSetting(property, value);
                default:
                    return ToolResponse.Fail(
                        $"Setting category '{category}' is not directly supported for modification. " +
                        "Supported categories: player, time. For other settings, use Edit/Project Settings... menu.");
            }
        }

        #endregion

        #region Setting Helpers

        private ToolResponse SetPlayerSetting(string property, string value)
        {
            switch (property)
            {
                case "company_name":
                    var oldCompany = PlayerSettings.companyName;
                    PlayerSettings.companyName = value;
                    return ToolResponse.OkWithData(new JObject
                    {
                        ["property"] = "company_name",
                        ["old_value"] = oldCompany,
                        ["new_value"] = value
                    }, $"Set company name to '{value}'.");

                case "product_name":
                    var oldProduct = PlayerSettings.productName;
                    PlayerSettings.productName = value;
                    return ToolResponse.OkWithData(new JObject
                    {
                        ["property"] = "product_name",
                        ["old_value"] = oldProduct,
                        ["new_value"] = value
                    }, $"Set product name to '{value}'.");

                case "version":
                    var oldVersion = PlayerSettings.bundleVersion;
                    PlayerSettings.bundleVersion = value;
                    return ToolResponse.OkWithData(new JObject
                    {
                        ["property"] = "version",
                        ["old_value"] = oldVersion,
                        ["new_value"] = value
                    }, $"Set bundle version to '{value}'.");

                default:
                    return ToolResponse.Fail(
                        $"Unknown player property: '{property}'. Supported: company_name, product_name, version");
            }
        }

        private ToolResponse SetTimeSetting(string property, string value)
        {
            if (!float.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float floatValue))
            {
                return ToolResponse.Fail($"Invalid float value: '{value}'");
            }

            switch (property)
            {
                case "fixed_timestep":
                    var oldFixed = Time.fixedDeltaTime;
                    Time.fixedDeltaTime = floatValue;
                    return ToolResponse.OkWithData(new JObject
                    {
                        ["property"] = "fixed_timestep",
                        ["old_value"] = oldFixed,
                        ["new_value"] = floatValue
                    }, $"Set fixed timestep to {floatValue}.");

                case "maximum_timestep":
                    var oldMax = Time.maximumDeltaTime;
                    Time.maximumDeltaTime = floatValue;
                    return ToolResponse.OkWithData(new JObject
                    {
                        ["property"] = "maximum_timestep",
                        ["old_value"] = oldMax,
                        ["new_value"] = floatValue
                    }, $"Set maximum timestep to {floatValue}.");

                case "time_scale":
                    var oldScale = Time.timeScale;
                    Time.timeScale = floatValue;
                    return ToolResponse.OkWithData(new JObject
                    {
                        ["property"] = "time_scale",
                        ["old_value"] = oldScale,
                        ["new_value"] = floatValue
                    }, $"Set time scale to {floatValue}.");

                default:
                    return ToolResponse.Fail(
                        $"Unknown time property: '{property}'. Supported: fixed_timestep, maximum_timestep, time_scale");
            }
        }

        #endregion
    }
}
