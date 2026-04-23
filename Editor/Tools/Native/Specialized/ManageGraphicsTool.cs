using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        Description = "Manage rendering and graphics settings including cameras, render settings, and quality levels",
        Category = "specialized",
        RequiresMainThread = true)]
    public class ManageGraphicsTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""get_render_settings"", ""set_render_settings"", ""get_quality_settings"", ""set_quality_level"", ""manage_camera""],
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
                ""culling_mask"": { ""type"": ""string"", ""description"": ""Culling mask layer name or Everything/Nothing"" }
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
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: get_render_settings, set_render_settings, get_quality_settings, set_quality_level, manage_camera");
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

        #endregion
    }
}
