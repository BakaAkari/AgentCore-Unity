using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace AgentCore.Editor.Tools.Native.Specialized
{
    /// <summary>
    /// Manage rendering and graphics settings including cameras, render settings, and quality levels.
    /// Directly calls Unity RenderSettings / QualitySettings / Camera API.
    /// </summary>
    [AgentTool("manage_graphics",
        Description = "Global rendering and quality settings — RenderSettings, QualitySettings, and rendering configuration. " +
                      "Actions: get_render_settings (fog, ambient, skybox, halo, flare), set_render_settings (modify any render setting), " +
                      "get_quality_settings (all quality levels with detail), set_quality_level (switch active quality), " +
                      "manage_camera (deprecated — prefer manage_camera tool), " +
                      "volume_list (enumerate scene Volumes + their VolumeProfile), " +
                      "volume_get (dump one Volume's or profile's VolumeComponent tree with all parameter values + override state), " +
                      "volume_set (write one parameter on one VolumeComponent — Bloom.intensity, Vignette.color, ColorAdjustments.contrast, etc.). " +
                      "USE FOR: configuring fog (mode/color/density), ambient lighting (color/intensity/mode), skybox material, " +
                      "quality level switching, shadow distance/resolution, LOD bias, texture resolution, vsync, " +
                      "post-processing tuning in URP or HDRP (bloom, vignette, color grading, tonemapping, depth of field, ambient occlusion). " +
                      "NOT FOR: per-object rendering (use manage_component on Renderer), shader editing, " +
                      "render pipeline asset configuration (SRP settings are in pipeline assets), custom VolumeComponent creation (only reads/writes existing components). " +
                      "ACTIVATE WHEN: user mentions 'fog', 'ambient light', 'skybox', 'quality settings', 'render settings', 'shadow distance', 'vsync', " +
                      "'bloom', 'vignette', 'post-processing', 'color grading', 'tonemapping', 'volume', 'HDRP', 'URP', '后处理', '泛光', '暗角', '色调'.",
        Category = "specialized",
        Visibility = ToolVisibility.OnDemand,
        RequiresMainThread = true)]
    public class ManageGraphicsTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""get_render_settings"", ""set_render_settings"", ""get_quality_settings"", ""set_quality_level"", ""manage_camera"", ""volume_list"", ""volume_get"", ""volume_set""],
                    ""description"": ""Action to perform""
                },
                ""ambient_mode"": {
                    ""type"": ""string"",
                    ""enum"": [""skybox"", ""trilight"", ""flat"", ""custom""],
                    ""description"": ""Ambient lighting mode""
                },
                ""ambient_color"": {
                    ""type"": ""object"",
                    ""properties"": { ""r"": {""type"":""number""}, ""g"": {""type"":""number""}, ""b"": {""type"":""number""}, ""a"": {""type"":""number""} },
                    ""description"": ""Ambient light color""
                },
                ""fog_enabled"": { ""type"": ""boolean"", ""description"": ""Enable or disable fog"" },
                ""fog_color"": {
                    ""type"": ""object"",
                    ""properties"": { ""r"": {""type"":""number""}, ""g"": {""type"":""number""}, ""b"": {""type"":""number""}, ""a"": {""type"":""number""} },
                    ""description"": ""Fog color""
                },
                ""fog_mode"": {
                    ""type"": ""string"",
                    ""enum"": [""linear"", ""exponential"", ""exponential_squared""],
                    ""description"": ""Fog mode""
                },
                ""fog_density"": { ""type"": ""number"", ""description"": ""Fog density (for exponential modes)"" },
                ""fog_start"": { ""type"": ""number"", ""description"": ""Fog start distance (linear mode)"" },
                ""fog_end"": { ""type"": ""number"", ""description"": ""Fog end distance (linear mode)"" },
                ""skybox_material"": { ""type"": ""string"", ""description"": ""Skybox material asset path"" },
                ""level"": {
                    ""description"": ""Quality level index (int) or name (string)""
                },
                ""target"": { ""type"": ""string"", ""description"": ""Camera GameObject name (default: Main Camera)"" },
                ""clear_flags"": {
                    ""type"": ""string"",
                    ""enum"": [""skybox"", ""solid_color"", ""depth"", ""nothing""],
                    ""description"": ""Camera clear flags""
                },
                ""background_color"": {
                    ""type"": ""object"",
                    ""properties"": { ""r"": {""type"":""number""}, ""g"": {""type"":""number""}, ""b"": {""type"":""number""}, ""a"": {""type"":""number""} },
                    ""description"": ""Camera background color""
                },
                ""fov"": { ""type"": ""number"", ""description"": ""Camera field of view"" },
                ""near_clip"": { ""type"": ""number"", ""description"": ""Camera near clip plane"" },
                ""far_clip"": { ""type"": ""number"", ""description"": ""Camera far clip plane"" },
                ""orthographic"": { ""type"": ""boolean"", ""description"": ""Use orthographic projection"" },
                ""orthographic_size"": { ""type"": ""number"", ""description"": ""Orthographic camera size"" },
                ""depth"": { ""type"": ""number"", ""description"": ""Camera depth"" },
                ""culling_mask"": { ""type"": ""string"", ""description"": ""Culling mask layer name or Everything/Nothing"" },
                ""volume_name"": { ""type"": ""string"", ""description"": ""Volume GameObject name or hierarchy path (for volume_get / volume_set). If omitted, uses the first Global Volume in the active scene."" },
                ""profile_path"": { ""type"": ""string"", ""description"": ""Explicit VolumeProfile asset path (for volume_get / volume_set). When set, operates directly on the profile asset instead of resolving via a scene Volume. Preferred for reproducibility."" },
                ""component"": { ""type"": ""string"", ""description"": ""VolumeComponent type name (for volume_set). Matches by short type name (case-insensitive), e.g. 'Bloom', 'Vignette', 'ColorAdjustments', 'Tonemapping'. Case-insensitive."" },
                ""parameter"": { ""type"": ""string"", ""description"": ""Parameter field name on the VolumeComponent (for volume_set), e.g. 'intensity', 'threshold', 'color', 'active'. Special: 'active' toggles the whole component. Case-insensitive."" },
                ""value"": { ""description"": ""New parameter value (for volume_set). Type must match the parameter: number for FloatParameter, bool for BoolParameter, {r,g,b,a} object for ColorParameter, {x,y,z} for Vector3Parameter, string enum name for enum parameters."" },
                ""override_state"": { ""type"": ""boolean"", ""description"": ""Whether the parameter should be marked as overridden (for volume_set). Defaults to true — required for the value to actually affect the final blend result. Set false to disable an override without changing the value."" }
            },
            ""required"": [""action""]
        }");

        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_graphics",
            description: "Manage rendering and graphics settings including cameras, render settings, and quality levels",
            category: "specialized",
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
                    case "get_render_settings":
                        response = HandleGetRenderSettings();
                        break;
                    case "set_render_settings":
                        response = HandleSetRenderSettings(parameters);
                        break;
                    case "get_quality_settings":
                        response = HandleGetQualitySettings();
                        break;
                    case "set_quality_level":
                        response = HandleSetQualityLevel(parameters);
                        break;
                    case "manage_camera":
                        response = HandleManageCamera(parameters);
                        break;
                    case "volume_list":
                        response = HandleVolumeList();
                        break;
                    case "volume_get":
                        response = HandleVolumeGet(parameters);
                        break;
                    case "volume_set":
                        response = HandleVolumeSet(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: get_render_settings, set_render_settings, get_quality_settings, set_quality_level, manage_camera, volume_list, volume_get, volume_set");
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

        private ToolResponse HandleGetRenderSettings()
        {
            var data = new JObject
            {
                ["ambientMode"] = RenderSettings.ambientMode.ToString(),
                ["ambientSkyColor"] = ColorToJson(RenderSettings.ambientSkyColor),
                ["ambientEquatorColor"] = ColorToJson(RenderSettings.ambientEquatorColor),
                ["ambientGroundColor"] = ColorToJson(RenderSettings.ambientGroundColor),
                ["ambientLight"] = ColorToJson(RenderSettings.ambientLight),
                ["ambientIntensity"] = RenderSettings.ambientIntensity,
                ["fogEnabled"] = RenderSettings.fog,
                ["fogColor"] = ColorToJson(RenderSettings.fogColor),
                ["fogMode"] = RenderSettings.fogMode.ToString(),
                ["fogDensity"] = RenderSettings.fogDensity,
                ["fogStartDistance"] = RenderSettings.fogStartDistance,
                ["fogEndDistance"] = RenderSettings.fogEndDistance,
                ["skyboxMaterial"] = RenderSettings.skybox != null ? RenderSettings.skybox.name : null,
                ["reflectionBounces"] = RenderSettings.defaultReflectionResolution,
                ["reflectionIntensity"] = RenderSettings.reflectionIntensity,
                ["defaultReflectionMode"] = RenderSettings.defaultReflectionMode.ToString(),
                ["subtractiveShadowColor"] = ColorToJson(RenderSettings.subtractiveShadowColor)
            };

            if (RenderSettings.sun != null)
            {
                data["sunSource"] = RenderSettings.sun.gameObject.name;
            }

            return ToolResponse.OkWithData(data, "Render settings retrieved.");
        }

        private ToolResponse HandleSetRenderSettings(JObject parameters)
        {
            // Note: RenderSettings is a static class in Unity 2022.3, Undo not directly supported

            var ambientModeStr = ToolHelpers.GetOptionalString(parameters, "ambient_mode");
            if (!string.IsNullOrEmpty(ambientModeStr))
            {
                switch (ambientModeStr.ToLowerInvariant())
                {
                    case "skybox": RenderSettings.ambientMode = AmbientMode.Skybox; break;
                    case "trilight": RenderSettings.ambientMode = AmbientMode.Trilight; break;
                    case "flat": RenderSettings.ambientMode = AmbientMode.Flat; break;
                    case "custom": RenderSettings.ambientMode = AmbientMode.Custom; break;
                    default:
                        return ToolResponse.Fail($"Invalid ambient_mode: '{ambientModeStr}'. Valid: skybox, trilight, flat, custom");
                }
            }

            var ambientColorToken = parameters["ambient_color"];
            if (ambientColorToken != null)
            {
                RenderSettings.ambientLight = ToolHelpers.ParseColor(ambientColorToken, RenderSettings.ambientLight);
            }

            if (parameters["fog_enabled"] != null)
            {
                RenderSettings.fog = ToolHelpers.GetOptionalBool(parameters, "fog_enabled", RenderSettings.fog);
            }

            var fogColorToken = parameters["fog_color"];
            if (fogColorToken != null)
            {
                RenderSettings.fogColor = ToolHelpers.ParseColor(fogColorToken, RenderSettings.fogColor);
            }

            var fogModeStr = ToolHelpers.GetOptionalString(parameters, "fog_mode");
            if (!string.IsNullOrEmpty(fogModeStr))
            {
                switch (fogModeStr.ToLowerInvariant())
                {
                    case "linear": RenderSettings.fogMode = FogMode.Linear; break;
                    case "exponential": RenderSettings.fogMode = FogMode.Exponential; break;
                    case "exponential_squared": RenderSettings.fogMode = FogMode.ExponentialSquared; break;
                    default:
                        return ToolResponse.Fail($"Invalid fog_mode: '{fogModeStr}'. Valid: linear, exponential, exponential_squared");
                }
            }

            if (parameters["fog_density"] != null)
                RenderSettings.fogDensity = ToolHelpers.GetOptionalFloat(parameters, "fog_density", RenderSettings.fogDensity);

            if (parameters["fog_start"] != null)
                RenderSettings.fogStartDistance = ToolHelpers.GetOptionalFloat(parameters, "fog_start", RenderSettings.fogStartDistance);

            if (parameters["fog_end"] != null)
                RenderSettings.fogEndDistance = ToolHelpers.GetOptionalFloat(parameters, "fog_end", RenderSettings.fogEndDistance);

            var skyboxPath = ToolHelpers.GetOptionalString(parameters, "skybox_material");
            if (!string.IsNullOrEmpty(skyboxPath))
            {
                skyboxPath = ToolHelpers.NormalizeAssetPath(skyboxPath);
                var mat = AssetDatabase.LoadAssetAtPath<Material>(skyboxPath);
                if (mat == null)
                    return ToolResponse.Fail($"Skybox material not found at: {skyboxPath}");
                RenderSettings.skybox = mat;
            }

            // RenderSettings is static in Unity 2022.3; mark scene dirty instead
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());
            return ToolResponse.Ok("Render settings updated.");
        }

        private ToolResponse HandleGetQualitySettings()
        {
            var names = QualitySettings.names;
            var levels = new JArray();
            for (int i = 0; i < names.Length; i++)
            {
                levels.Add(new JObject
                {
                    ["index"] = i,
                    ["name"] = names[i],
                    ["isCurrent"] = (i == QualitySettings.GetQualityLevel())
                });
            }

            var data = new JObject
            {
                ["currentLevel"] = QualitySettings.GetQualityLevel(),
                ["currentLevelName"] = names[QualitySettings.GetQualityLevel()],
                ["levels"] = levels,
                ["pixelLightCount"] = QualitySettings.pixelLightCount,
                ["anisotropicFiltering"] = QualitySettings.anisotropicFiltering.ToString(),
                ["antiAliasing"] = QualitySettings.antiAliasing,
                ["shadowDistance"] = QualitySettings.shadowDistance,
                ["shadowResolution"] = QualitySettings.shadowResolution.ToString(),
                ["vSyncCount"] = QualitySettings.vSyncCount
            };

            return ToolResponse.OkWithData(data, "Quality settings retrieved.");
        }

        private ToolResponse HandleSetQualityLevel(JObject parameters)
        {
            var levelToken = parameters["level"];
            if (levelToken == null)
                return ToolResponse.Fail("Parameter 'level' is required for set_quality_level action.");

            var names = QualitySettings.names;

            if (levelToken.Type == JTokenType.Integer)
            {
                int index = levelToken.Value<int>();
                if (index < 0 || index >= names.Length)
                    return ToolResponse.Fail($"Quality level index {index} out of range (0-{names.Length - 1}).");
                QualitySettings.SetQualityLevel(index, true);
                return ToolResponse.Ok($"Quality level set to {index} ({names[index]}).");
            }
            else
            {
                string levelName = levelToken.ToString();
                for (int i = 0; i < names.Length; i++)
                {
                    if (string.Equals(names[i], levelName, StringComparison.OrdinalIgnoreCase))
                    {
                        QualitySettings.SetQualityLevel(i, true);
                        return ToolResponse.Ok($"Quality level set to {i} ({names[i]}).");
                    }
                }
                return ToolResponse.Fail($"Quality level '{levelName}' not found. Available: {string.Join(", ", names)}");
            }
        }

        private ToolResponse HandleManageCamera(JObject parameters)
        {
            var targetName = ToolHelpers.GetOptionalString(parameters, "target", "Main Camera");
            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return ToolResponse.Fail($"GameObject '{targetName}' not found.");

            var camera = go.GetComponent<Camera>();
            if (camera == null)
                return ToolResponse.Fail($"GameObject '{targetName}' does not have a Camera component.");

            ToolHelpers.RecordUndo(camera, "Manage Camera");

            bool modified = false;

            var clearFlagsStr = ToolHelpers.GetOptionalString(parameters, "clear_flags");
            if (!string.IsNullOrEmpty(clearFlagsStr))
            {
                switch (clearFlagsStr.ToLowerInvariant())
                {
                    case "skybox": camera.clearFlags = CameraClearFlags.Skybox; break;
                    case "solid_color": camera.clearFlags = CameraClearFlags.SolidColor; break;
                    case "depth": camera.clearFlags = CameraClearFlags.Depth; break;
                    case "nothing": camera.clearFlags = CameraClearFlags.Nothing; break;
                    default:
                        return ToolResponse.Fail($"Invalid clear_flags: '{clearFlagsStr}'. Valid: skybox, solid_color, depth, nothing");
                }
                modified = true;
            }

            var bgColorToken = parameters["background_color"];
            if (bgColorToken != null)
            {
                camera.backgroundColor = ToolHelpers.ParseColor(bgColorToken, camera.backgroundColor);
                modified = true;
            }

            if (parameters["fov"] != null)
            {
                camera.fieldOfView = ToolHelpers.GetOptionalFloat(parameters, "fov", camera.fieldOfView);
                modified = true;
            }

            if (parameters["near_clip"] != null)
            {
                camera.nearClipPlane = ToolHelpers.GetOptionalFloat(parameters, "near_clip", camera.nearClipPlane);
                modified = true;
            }

            if (parameters["far_clip"] != null)
            {
                camera.farClipPlane = ToolHelpers.GetOptionalFloat(parameters, "far_clip", camera.farClipPlane);
                modified = true;
            }

            if (parameters["orthographic"] != null)
            {
                camera.orthographic = ToolHelpers.GetOptionalBool(parameters, "orthographic", camera.orthographic);
                modified = true;
            }

            if (parameters["orthographic_size"] != null)
            {
                camera.orthographicSize = ToolHelpers.GetOptionalFloat(parameters, "orthographic_size", camera.orthographicSize);
                modified = true;
            }

            if (parameters["depth"] != null)
            {
                camera.depth = ToolHelpers.GetOptionalFloat(parameters, "depth", camera.depth);
                modified = true;
            }

            var cullingMaskStr = ToolHelpers.GetOptionalString(parameters, "culling_mask");
            if (!string.IsNullOrEmpty(cullingMaskStr))
            {
                switch (cullingMaskStr.ToLowerInvariant())
                {
                    case "everything":
                        camera.cullingMask = -1;
                        break;
                    case "nothing":
                        camera.cullingMask = 0;
                        break;
                    default:
                        int layer = LayerMask.NameToLayer(cullingMaskStr);
                        if (layer == -1)
                            return ToolResponse.Fail($"Layer '{cullingMaskStr}' not found.");
                        camera.cullingMask = 1 << layer;
                        break;
                }
                modified = true;
            }

            if (modified)
            {
                EditorUtility.SetDirty(camera);
            }

            // Return current camera info
            var info = new JObject
            {
                ["name"] = go.name,
                ["clearFlags"] = camera.clearFlags.ToString(),
                ["backgroundColor"] = ColorToJson(camera.backgroundColor),
                ["fieldOfView"] = camera.fieldOfView,
                ["nearClipPlane"] = camera.nearClipPlane,
                ["farClipPlane"] = camera.farClipPlane,
                ["orthographic"] = camera.orthographic,
                ["orthographicSize"] = camera.orthographicSize,
                ["depth"] = camera.depth,
                ["cullingMask"] = camera.cullingMask,
                ["modified"] = modified
            };

            return ToolResponse.OkWithData(info, modified ? "Camera settings updated." : "Camera info retrieved.");
        }

        // ============================================================
        // Volume actions — SRP post-processing (URP / HDRP / any pipeline
        // built on the Core RP Library's VolumeSystem). Works via the
        // UnityEngine.Rendering.Volume / VolumeProfile / VolumeComponent
        // stack — no direct dependency on URP or HDRP assemblies.
        //
        // Compiled only when com.unity.render-pipelines.core is present
        // (see AgentCore.Editor.asmdef versionDefines). Built-in pipeline
        // projects get a clean "SRP not detected" fallback instead of a
        // compile error.
        // ============================================================

#if AGENTCORE_HAS_SRP_CORE

        /// <summary>
        /// Enumerate every <see cref="Volume"/> in every loaded scene, plus their bound VolumeProfile.
        /// </summary>
        private ToolResponse HandleVolumeList()
        {
            var volumes = UnityEngine.Object.FindObjectsOfType<Volume>(includeInactive: true);
            var arr = new JArray();
            foreach (var v in volumes)
            {
                if (v == null) continue;
                var go = v.gameObject;
                var profile = v.sharedProfile;
                var profilePath = profile != null ? AssetDatabase.GetAssetPath(profile) : "";
                int componentCount = profile != null && profile.components != null ? profile.components.Count : 0;

                arr.Add(new JObject
                {
                    ["gameobject"] = go != null ? go.name : "<null>",
                    ["scene"] = go != null ? go.scene.name : "",
                    ["hierarchy_path"] = go != null ? GetHierarchyPath(go.transform) : "",
                    ["enabled"] = v.enabled,
                    ["is_global"] = v.isGlobal,
                    ["priority"] = v.priority,
                    ["weight"] = v.weight,
                    ["blend_distance"] = v.blendDistance,
                    ["profile_name"] = profile != null ? profile.name : "(none)",
                    ["profile_path"] = profilePath,
                    ["component_count"] = componentCount
                });
            }
            var data = new JObject
            {
                ["volume_count"] = arr.Count,
                ["volumes"] = arr,
                ["hint"] = arr.Count == 0
                    ? "No Volumes found in loaded scenes. Post-processing in URP/HDRP requires at least one Volume with a VolumeProfile. Use manage_gameobject to create one, or check that the active render pipeline is URP or HDRP."
                    : "Pass 'volume_name' or 'profile_path' to volume_get/volume_set to inspect or modify a specific Volume."
            };
            return ToolResponse.OkWithData(data, $"Found {arr.Count} Volume(s) in loaded scenes.");
        }

        /// <summary>
        /// Read every VolumeComponent + parameter from a resolved profile (via Volume GO name or profile asset path).
        /// </summary>
        private ToolResponse HandleVolumeGet(JObject parameters)
        {
            var profile = ResolveVolumeProfile(parameters, out string source, out string error);
            if (profile == null)
            {
                return ToolResponse.Fail(error);
            }

            var components = new JArray();
            if (profile.components != null)
            {
                foreach (var comp in profile.components)
                {
                    if (comp == null) continue;
                    components.Add(SerializeVolumeComponent(comp));
                }
            }

            var data = new JObject
            {
                ["profile_name"] = profile.name,
                ["profile_path"] = AssetDatabase.GetAssetPath(profile),
                ["source"] = source,
                ["component_count"] = components.Count,
                ["components"] = components,
                ["hint"] = "To modify: volume_set component=<TypeName> parameter=<paramName> value=<newValue> [override_state=true]"
            };
            return ToolResponse.OkWithData(data, $"Profile '{profile.name}' — {components.Count} VolumeComponent(s).");
        }

        /// <summary>
        /// Write one parameter on one VolumeComponent. Parameter selection is by field name,
        /// resolved via reflection on the concrete VolumeComponent subclass. Value coercion
        /// handles the common VolumeParameter&lt;T&gt; subclasses (float, int, bool, Color,
        /// Vector3, enum).
        /// </summary>
        private ToolResponse HandleVolumeSet(JObject parameters)
        {
            var profile = ResolveVolumeProfile(parameters, out string source, out string error);
            if (profile == null)
            {
                return ToolResponse.Fail(error);
            }

            var componentName = ToolHelpers.GetOptionalString(parameters, "component");
            if (string.IsNullOrEmpty(componentName))
            {
                return ToolResponse.Fail("Parameter 'component' is required for volume_set (e.g. 'Bloom', 'Vignette', 'ColorAdjustments').");
            }
            var paramName = ToolHelpers.GetOptionalString(parameters, "parameter");
            if (string.IsNullOrEmpty(paramName))
            {
                return ToolResponse.Fail("Parameter 'parameter' is required for volume_set (e.g. 'intensity', 'threshold', 'color'). Use 'active' to toggle the whole component.");
            }
            if (!parameters.TryGetValue("value", out var valueToken) || valueToken == null || valueToken.Type == JTokenType.Null)
            {
                return ToolResponse.Fail("Parameter 'value' is required for volume_set.");
            }
            bool overrideState = ToolHelpers.GetOptionalBool(parameters, "override_state", true);

            // Locate component
            VolumeComponent target = null;
            if (profile.components != null)
            {
                foreach (var c in profile.components)
                {
                    if (c == null) continue;
                    var t = c.GetType();
                    if (string.Equals(t.Name, componentName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(t.FullName, componentName, StringComparison.OrdinalIgnoreCase))
                    {
                        target = c;
                        break;
                    }
                }
            }
            if (target == null)
            {
                var available = new List<string>();
                if (profile.components != null)
                {
                    foreach (var c in profile.components) if (c != null) available.Add(c.GetType().Name);
                }
                return ToolResponse.Fail(
                    $"VolumeComponent '{componentName}' not found on profile '{profile.name}'. Available: {(available.Count == 0 ? "(none)" : string.Join(", ", available))}.");
            }

            // Special case: parameter 'active' toggles the whole component
            if (string.Equals(paramName, "active", StringComparison.OrdinalIgnoreCase))
            {
                if (valueToken.Type != JTokenType.Boolean)
                {
                    return ToolResponse.Fail("Parameter 'active' requires a boolean value.");
                }
                target.active = valueToken.Value<bool>();
                EditorUtility.SetDirty(profile);
                return ToolResponse.OkWithData(SerializeVolumeComponent(target),
                    $"{target.GetType().Name}.active = {target.active} on profile '{profile.name}'.");
            }

            // Locate parameter field on the component (VolumeParameter<T>-typed field)
            var fields = target.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
            FieldInfo paramField = null;
            foreach (var f in fields)
            {
                if (!string.Equals(f.Name, paramName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!typeof(VolumeParameter).IsAssignableFrom(f.FieldType)) continue;
                paramField = f;
                break;
            }
            if (paramField == null)
            {
                var available = new List<string>();
                foreach (var f in fields)
                {
                    if (typeof(VolumeParameter).IsAssignableFrom(f.FieldType)) available.Add(f.Name);
                }
                return ToolResponse.Fail(
                    $"Parameter '{paramName}' not found on {target.GetType().Name}. Available: {(available.Count == 0 ? "(none)" : string.Join(", ", available))}.");
            }

            var vp = paramField.GetValue(target) as VolumeParameter;
            if (vp == null)
            {
                return ToolResponse.Fail($"Field '{paramField.Name}' on {target.GetType().Name} is null.");
            }

            // Set overrideState (public writable property on VolumeParameter since 2019.3+)
            try { vp.overrideState = overrideState; }
            catch (Exception e) { return ToolResponse.Fail($"Failed to set overrideState: {e.Message}"); }

            // Coerce and assign value via reflection on the concrete VolumeParameter<T>.value property.
            var vpType = vp.GetType();
            var valueProp = vpType.GetProperty("value", BindingFlags.Public | BindingFlags.Instance);
            if (valueProp == null || !valueProp.CanWrite)
            {
                return ToolResponse.Fail($"VolumeParameter subclass {vpType.Name} does not expose a writable 'value' property.");
            }

            object coerced;
            try
            {
                coerced = CoerceJTokenToType(valueToken, valueProp.PropertyType, out string coerceError);
                if (coerceError != null)
                {
                    return ToolResponse.Fail(coerceError);
                }
            }
            catch (Exception e)
            {
                return ToolResponse.Fail($"Value coercion failed: {e.GetType().Name}: {e.Message}");
            }

            try
            {
                valueProp.SetValue(vp, coerced);
            }
            catch (Exception e)
            {
                return ToolResponse.Fail($"Failed to set {vpType.Name}.value: {e.GetType().Name}: {e.Message}");
            }

            EditorUtility.SetDirty(profile);

            var data = SerializeVolumeComponent(target);
            data["modified_parameter"] = paramField.Name;
            return ToolResponse.OkWithData(data,
                $"{target.GetType().Name}.{paramField.Name} = {FormatValueForMessage(coerced)} (overrideState={overrideState}) on profile '{profile.name}'.");
        }

#else // AGENTCORE_HAS_SRP_CORE not defined — built-in pipeline fallback

        private ToolResponse HandleVolumeList() => VolumeUnavailable("volume_list");
        private ToolResponse HandleVolumeGet(JObject parameters) => VolumeUnavailable("volume_get");
        private ToolResponse HandleVolumeSet(JObject parameters) => VolumeUnavailable("volume_set");

        private static ToolResponse VolumeUnavailable(string actionName)
        {
            return ToolResponse.Fail(
                $"Action '{actionName}' requires the Scriptable Render Pipeline (URP or HDRP). " +
                "This project is using the built-in render pipeline (com.unity.render-pipelines.core is not installed). " +
                "To enable Volume post-processing tooling, install URP or HDRP via Window > Package Manager, " +
                "then recompile. AgentCore auto-detects SRP via asmdef versionDefines.");
        }

#endif

        #endregion

        #region Helpers

        private static JObject ColorToJson(Color c)
        {
            return new JObject
            {
                ["r"] = Math.Round(c.r, 4),
                ["g"] = Math.Round(c.g, 4),
                ["b"] = Math.Round(c.b, 4),
                ["a"] = Math.Round(c.a, 4)
            };
        }

        // ---------- Volume helpers (SRP-only) ----------

#if AGENTCORE_HAS_SRP_CORE

        /// <summary>
        /// Build a Transform hierarchy path like "Root/Child/GrandChild".
        /// </summary>
        private static string GetHierarchyPath(Transform t)
        {
            if (t == null) return "";
            var stack = new Stack<string>();
            while (t != null)
            {
                stack.Push(t.name);
                t = t.parent;
            }
            return string.Join("/", stack);
        }

        /// <summary>
        /// Resolve a VolumeProfile from parameters (profile_path preferred, then volume_name, then first Global Volume).
        /// Returns null and sets <paramref name="error"/> when no profile can be resolved.
        /// </summary>
        private static VolumeProfile ResolveVolumeProfile(JObject parameters, out string source, out string error)
        {
            source = "";
            error = "";

            var profilePath = ToolHelpers.GetOptionalString(parameters, "profile_path");
            if (!string.IsNullOrEmpty(profilePath))
            {
                var loaded = AssetDatabase.LoadAssetAtPath<VolumeProfile>(profilePath);
                if (loaded == null)
                {
                    error = $"VolumeProfile asset not found at '{profilePath}'. Check the path (must start with 'Assets/').";
                    return null;
                }
                source = $"profile_path={profilePath}";
                return loaded;
            }

            var volumeName = ToolHelpers.GetOptionalString(parameters, "volume_name");
            var volumes = UnityEngine.Object.FindObjectsOfType<Volume>(includeInactive: true);
            if (volumes == null || volumes.Length == 0)
            {
                error = "No Volumes found in loaded scenes and no profile_path was given. Provide 'profile_path' or create a Volume in the scene first.";
                return null;
            }

            Volume picked = null;
            if (!string.IsNullOrEmpty(volumeName))
            {
                foreach (var v in volumes)
                {
                    if (v == null || v.gameObject == null) continue;
                    if (string.Equals(v.gameObject.name, volumeName, StringComparison.OrdinalIgnoreCase) ||
                        GetHierarchyPath(v.transform).EndsWith("/" + volumeName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(GetHierarchyPath(v.transform), volumeName, StringComparison.OrdinalIgnoreCase))
                    {
                        picked = v;
                        break;
                    }
                }
                if (picked == null)
                {
                    var names = string.Join(", ", volumes.Where(x => x != null && x.gameObject != null).Select(x => x.gameObject.name));
                    error = $"Volume '{volumeName}' not found. Available: {names}.";
                    return null;
                }
                source = $"volume_name={volumeName}";
            }
            else
            {
                // Prefer first enabled Global Volume; fall back to first Volume overall.
                foreach (var v in volumes)
                {
                    if (v != null && v.enabled && v.isGlobal)
                    {
                        picked = v;
                        break;
                    }
                }
                if (picked == null) picked = volumes[0];
                source = $"auto-picked Volume '{picked.gameObject.name}' (no volume_name/profile_path supplied)";
            }

            if (picked.sharedProfile == null)
            {
                error = $"Volume '{picked.gameObject.name}' has no VolumeProfile assigned. Assign one in the Inspector or pass 'profile_path' explicitly.";
                return null;
            }
            return picked.sharedProfile;
        }

        /// <summary>
        /// Serialize a VolumeComponent to JSON, walking every VolumeParameter field.
        /// </summary>
        private static JObject SerializeVolumeComponent(VolumeComponent comp)
        {
            var type = comp.GetType();
            var obj = new JObject
            {
                ["type"] = type.Name,
                ["full_type"] = type.FullName ?? type.Name,
                ["active"] = comp.active
            };
            var paramsObj = new JObject();
            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!typeof(VolumeParameter).IsAssignableFrom(f.FieldType)) continue;
                var vp = f.GetValue(comp) as VolumeParameter;
                if (vp == null) continue;
                var vpType = vp.GetType();
                var valueProp = vpType.GetProperty("value", BindingFlags.Public | BindingFlags.Instance);
                object val = null;
                try { val = valueProp?.GetValue(vp); } catch { /* best-effort */ }
                paramsObj[f.Name] = new JObject
                {
                    ["type"] = vpType.Name,
                    ["override_state"] = vp.overrideState,
                    ["value"] = ValueToJToken(val)
                };
            }
            obj["parameters"] = paramsObj;
            return obj;
        }

        /// <summary>
        /// Convert a runtime value to a JToken suitable for serialization.
        /// Handles primitives, Unity math structs, Color, and enums.
        /// </summary>
        private static JToken ValueToJToken(object v)
        {
            if (v == null) return JValue.CreateNull();
            switch (v)
            {
                case bool b: return b;
                case int i: return i;
                case long l: return l;
                case float f: return f;
                case double d: return d;
                case string s: return s;
                case Color c: return ColorToJson(c);
                case Vector2 v2: return new JObject { ["x"] = v2.x, ["y"] = v2.y };
                case Vector3 v3: return new JObject { ["x"] = v3.x, ["y"] = v3.y, ["z"] = v3.z };
                case Vector4 v4: return new JObject { ["x"] = v4.x, ["y"] = v4.y, ["z"] = v4.z, ["w"] = v4.w };
                case Enum e: return e.ToString();
                case UnityEngine.Object o: return o != null ? $"{o.name} ({o.GetType().Name})" : "(null)";
            }
            // Fallback: ToString
            return v.ToString();
        }

        /// <summary>
        /// Coerce a JToken to the expected .NET type of a VolumeParameter&lt;T&gt;.value property.
        /// Sets <paramref name="error"/> to a message when coercion is impossible; returns null in that case.
        /// </summary>
        private static object CoerceJTokenToType(JToken token, Type targetType, out string error)
        {
            error = null;

            if (targetType == typeof(float)) return token.Type == JTokenType.Float || token.Type == JTokenType.Integer ? (float)token.ToObject<double>() : Fail<float>(token, targetType, out error);
            if (targetType == typeof(double)) return token.Type == JTokenType.Float || token.Type == JTokenType.Integer ? token.ToObject<double>() : Fail<double>(token, targetType, out error);
            if (targetType == typeof(int)) return token.Type == JTokenType.Integer || token.Type == JTokenType.Float ? token.ToObject<int>() : Fail<int>(token, targetType, out error);
            if (targetType == typeof(bool)) return token.Type == JTokenType.Boolean ? token.ToObject<bool>() : Fail<bool>(token, targetType, out error);
            if (targetType == typeof(string)) return token.ToObject<string>();

            if (targetType == typeof(Color))
            {
                if (token.Type != JTokenType.Object)
                {
                    error = $"Expected Color object with r/g/b[/a] fields, got {token.Type}.";
                    return null;
                }
                var o = (JObject)token;
                return new Color(
                    o.Value<float?>("r") ?? 0f,
                    o.Value<float?>("g") ?? 0f,
                    o.Value<float?>("b") ?? 0f,
                    o.Value<float?>("a") ?? 1f);
            }

            if (targetType == typeof(Vector2))
            {
                if (token.Type != JTokenType.Object) { error = $"Expected Vector2 object {{x,y}}, got {token.Type}."; return null; }
                var o = (JObject)token;
                return new Vector2(o.Value<float?>("x") ?? 0f, o.Value<float?>("y") ?? 0f);
            }
            if (targetType == typeof(Vector3))
            {
                if (token.Type != JTokenType.Object) { error = $"Expected Vector3 object {{x,y,z}}, got {token.Type}."; return null; }
                var o = (JObject)token;
                return new Vector3(o.Value<float?>("x") ?? 0f, o.Value<float?>("y") ?? 0f, o.Value<float?>("z") ?? 0f);
            }
            if (targetType == typeof(Vector4))
            {
                if (token.Type != JTokenType.Object) { error = $"Expected Vector4 object {{x,y,z,w}}, got {token.Type}."; return null; }
                var o = (JObject)token;
                return new Vector4(o.Value<float?>("x") ?? 0f, o.Value<float?>("y") ?? 0f, o.Value<float?>("z") ?? 0f, o.Value<float?>("w") ?? 0f);
            }

            if (targetType.IsEnum)
            {
                var s = token.ToObject<string>();
                if (string.IsNullOrEmpty(s)) { error = $"Expected enum {targetType.Name} name (string), got null."; return null; }
                try { return Enum.Parse(targetType, s, ignoreCase: true); }
                catch (Exception e)
                {
                    error = $"Cannot parse '{s}' as {targetType.Name}: {e.Message}. Valid names: {string.Join(", ", Enum.GetNames(targetType))}.";
                    return null;
                }
            }

            // Fallback: try Newtonsoft direct conversion
            try { return token.ToObject(targetType); }
            catch (Exception e)
            {
                error = $"No coercion rule for type {targetType.Name}: {e.Message}";
                return null;
            }
        }

        private static T Fail<T>(JToken token, Type targetType, out string error)
        {
            error = $"Cannot coerce JToken ({token.Type}: {token}) to {targetType.Name}.";
            return default;
        }

        private static string FormatValueForMessage(object v)
        {
            if (v == null) return "null";
            if (v is Color c) return $"({c.r:F3}, {c.g:F3}, {c.b:F3}, {c.a:F3})";
            if (v is Vector2 v2) return $"({v2.x:F3}, {v2.y:F3})";
            if (v is Vector3 v3) return $"({v3.x:F3}, {v3.y:F3}, {v3.z:F3})";
            if (v is Vector4 v4) return $"({v4.x:F3}, {v4.y:F3}, {v4.z:F3}, {v4.w:F3})";
            return v.ToString();
        }

#endif // AGENTCORE_HAS_SRP_CORE

        #endregion
    }
}
