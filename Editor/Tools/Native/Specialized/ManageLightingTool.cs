using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Tools.Native.Specialized
{
    /// <summary>
    /// Manage lights, lightmapping, and lighting settings in the scene.
    /// Directly calls Unity Light / Lightmapping API.
    /// </summary>
    [AgentTool("manage_lighting",
        Description = "Manage lights, lightmapping, and lighting settings in the scene",
        Category = "specialized",
        Visibility = ToolVisibility.OnDemand,
        RequiresMainThread = true)]
    public class ManageLightingTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""create"", ""modify"", ""get_info"", ""list"", ""bake"", ""get_lightmap_settings""],
                    ""description"": ""Action to perform""
                },
                ""type"": {
                    ""type"": ""string"",
                    ""enum"": [""directional"", ""point"", ""spot"", ""area""],
                    ""description"": ""Light type (for create action or list filter)""
                },
                ""name"": { ""type"": ""string"", ""description"": ""Light GameObject name"" },
                ""target"": { ""type"": ""string"", ""description"": ""Target light GameObject name"" },
                ""position"": {
                    ""type"": ""object"",
                    ""properties"": { ""x"": {""type"":""number""}, ""y"": {""type"":""number""}, ""z"": {""type"":""number""} },
                    ""description"": ""World position""
                },
                ""rotation"": {
                    ""type"": ""object"",
                    ""properties"": { ""x"": {""type"":""number""}, ""y"": {""type"":""number""}, ""z"": {""type"":""number""} },
                    ""description"": ""Euler rotation""
                },
                ""color"": {
                    ""type"": ""object"",
                    ""properties"": { ""r"": {""type"":""number""}, ""g"": {""type"":""number""}, ""b"": {""type"":""number""}, ""a"": {""type"":""number""} },
                    ""description"": ""Light color""
                },
                ""intensity"": { ""type"": ""number"", ""description"": ""Light intensity"" },
                ""range"": { ""type"": ""number"", ""description"": ""Light range (point/spot)"" },
                ""spot_angle"": { ""type"": ""number"", ""description"": ""Spot light angle"" },
                ""shadows"": {
                    ""type"": ""string"",
                    ""enum"": [""none"", ""hard"", ""soft""],
                    ""description"": ""Shadow type""
                },
                ""shadow_strength"": { ""type"": ""number"", ""description"": ""Shadow strength (0-1)"" },
                ""cookie"": { ""type"": ""string"", ""description"": ""Cookie texture asset path"" },
                ""enabled"": { ""type"": ""boolean"", ""description"": ""Enable or disable the light"" },
                ""mode"": {
                    ""type"": ""string"",
                    ""enum"": [""clear"", ""bake""],
                    ""description"": ""Bake mode (default: bake)""
                }
            },
            ""required"": [""action""]
        }");

        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_lighting",
            description: "Manage lights, lightmapping, and lighting settings in the scene",
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
                    case "create":
                        response = HandleCreate(parameters);
                        break;
                    case "modify":
                        response = HandleModify(parameters);
                        break;
                    case "get_info":
                        response = HandleGetInfo(parameters);
                        break;
                    case "list":
                        response = HandleList(parameters);
                        break;
                    case "bake":
                        response = HandleBake(parameters);
                        break;
                    case "get_lightmap_settings":
                        response = HandleGetLightmapSettings();
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: create, modify, get_info, list, bake, get_lightmap_settings");
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

        private ToolResponse HandleCreate(JObject parameters)
        {
            var typeStr = ToolHelpers.GetRequiredString(parameters, "type").ToLowerInvariant();
            LightType lightType;
            switch (typeStr)
            {
                case "directional": lightType = LightType.Directional; break;
                case "point": lightType = LightType.Point; break;
                case "spot": lightType = LightType.Spot; break;
                case "area": lightType = LightType.Area; break;
                default:
                    return ToolResponse.Fail($"Invalid light type: '{typeStr}'. Valid: directional, point, spot, area");
            }

            var name = ToolHelpers.GetOptionalString(parameters, "name", $"{typeStr.Substring(0, 1).ToUpper()}{typeStr.Substring(1)} Light");
            var go = new GameObject(name);
            ToolHelpers.RegisterCreatedObject(go, "Create Light");

            var light = go.AddComponent<Light>();
            light.type = lightType;

            // Position
            var posToken = parameters["position"];
            if (posToken != null)
            {
                go.transform.position = ToolHelpers.ParseVector3(posToken);
            }

            // Rotation
            var rotToken = parameters["rotation"];
            if (rotToken != null)
            {
                go.transform.eulerAngles = ToolHelpers.ParseVector3(rotToken);
            }

            // Color
            var colorToken = parameters["color"];
            if (colorToken != null)
            {
                light.color = ToolHelpers.ParseColor(colorToken, Color.white);
            }

            // Intensity
            light.intensity = ToolHelpers.GetOptionalFloat(parameters, "intensity", 1f);

            // Range (for point/spot)
            if (parameters["range"] != null)
            {
                light.range = ToolHelpers.GetOptionalFloat(parameters, "range", 10f);
            }

            // Spot angle
            if (parameters["spot_angle"] != null && lightType == LightType.Spot)
            {
                light.spotAngle = ToolHelpers.GetOptionalFloat(parameters, "spot_angle", 30f);
            }

            // Shadows
            var shadowsStr = ToolHelpers.GetOptionalString(parameters, "shadows");
            if (!string.IsNullOrEmpty(shadowsStr))
            {
                light.shadows = ParseShadowType(shadowsStr);
            }

            var data = SerializeLightInfo(go, light);
            return ToolResponse.OkWithData(data, $"Created {typeStr} light '{name}'.");
        }

        private ToolResponse HandleModify(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");
            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return ToolResponse.Fail($"GameObject '{targetName}' not found.");

            var light = go.GetComponent<Light>();
            if (light == null)
                return ToolResponse.Fail($"GameObject '{targetName}' does not have a Light component.");

            ToolHelpers.RecordUndo(light, "Modify Light");
            ToolHelpers.RecordUndo(go, "Modify Light");

            bool modified = false;

            var colorToken = parameters["color"];
            if (colorToken != null)
            {
                light.color = ToolHelpers.ParseColor(colorToken, light.color);
                modified = true;
            }

            if (parameters["intensity"] != null)
            {
                light.intensity = ToolHelpers.GetOptionalFloat(parameters, "intensity", light.intensity);
                modified = true;
            }

            if (parameters["range"] != null)
            {
                light.range = ToolHelpers.GetOptionalFloat(parameters, "range", light.range);
                modified = true;
            }

            if (parameters["spot_angle"] != null)
            {
                light.spotAngle = ToolHelpers.GetOptionalFloat(parameters, "spot_angle", light.spotAngle);
                modified = true;
            }

            var shadowsStr = ToolHelpers.GetOptionalString(parameters, "shadows");
            if (!string.IsNullOrEmpty(shadowsStr))
            {
                light.shadows = ParseShadowType(shadowsStr);
                modified = true;
            }

            if (parameters["shadow_strength"] != null)
            {
                light.shadowStrength = ToolHelpers.GetOptionalFloat(parameters, "shadow_strength", light.shadowStrength);
                modified = true;
            }

            var cookiePath = ToolHelpers.GetOptionalString(parameters, "cookie");
            if (!string.IsNullOrEmpty(cookiePath))
            {
                cookiePath = ToolHelpers.NormalizeAssetPath(cookiePath);
                var texture = AssetDatabase.LoadAssetAtPath<Texture>(cookiePath);
                if (texture == null)
                    return ToolResponse.Fail($"Cookie texture not found at: {cookiePath}");
                light.cookie = texture;
                modified = true;
            }

            if (parameters["enabled"] != null)
            {
                light.enabled = ToolHelpers.GetOptionalBool(parameters, "enabled", light.enabled);
                modified = true;
            }

            if (modified)
            {
                EditorUtility.SetDirty(light);
                EditorUtility.SetDirty(go);
            }

            var data = SerializeLightInfo(go, light);
            data["modified"] = modified;
            return ToolResponse.OkWithData(data, modified ? $"Light '{targetName}' modified." : $"Light '{targetName}' info retrieved.");
        }

        private ToolResponse HandleGetInfo(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");
            var go = ToolHelpers.FindGameObject(targetName);
            if (go == null)
                return ToolResponse.Fail($"GameObject '{targetName}' not found.");

            var light = go.GetComponent<Light>();
            if (light == null)
                return ToolResponse.Fail($"GameObject '{targetName}' does not have a Light component.");

            var data = SerializeLightInfo(go, light);
            return ToolResponse.OkWithData(data, $"Light info for '{targetName}'.");
        }

        private ToolResponse HandleList(JObject parameters)
        {
            var filterType = ToolHelpers.GetOptionalString(parameters, "type");
            LightType? filterLightType = null;

            if (!string.IsNullOrEmpty(filterType))
            {
                switch (filterType.ToLowerInvariant())
                {
                    case "directional": filterLightType = LightType.Directional; break;
                    case "point": filterLightType = LightType.Point; break;
                    case "spot": filterLightType = LightType.Spot; break;
                    case "area": filterLightType = LightType.Area; break;
                    default:
                        return ToolResponse.Fail($"Invalid light type filter: '{filterType}'. Valid: directional, point, spot, area");
                }
            }

            var allLights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
            var lightsArray = new JArray();

            foreach (var light in allLights)
            {
                if (filterLightType.HasValue && light.type != filterLightType.Value)
                    continue;

                lightsArray.Add(new JObject
                {
                    ["name"] = light.gameObject.name,
                    ["type"] = light.type.ToString(),
                    ["intensity"] = light.intensity,
                    ["color"] = $"#{ColorUtility.ToHtmlStringRGBA(light.color)}",
                    ["enabled"] = light.enabled,
                    ["shadows"] = light.shadows.ToString(),
                    ["instanceId"] = light.gameObject.GetInstanceID()
                });
            }

            var data = new JObject
            {
                ["count"] = lightsArray.Count,
                ["lights"] = lightsArray
            };

            return ToolResponse.OkWithData(data, $"Found {lightsArray.Count} light(s) in scene.");
        }

        private ToolResponse HandleBake(JObject parameters)
        {
            var mode = ToolHelpers.GetOptionalString(parameters, "mode", "bake").ToLowerInvariant();

            switch (mode)
            {
                case "clear":
                    Lightmapping.Clear();
                    Lightmapping.ClearDiskCache();
                    return ToolResponse.Ok("Lightmap data cleared.");

                case "bake":
                    if (Lightmapping.isRunning)
                        return ToolResponse.Fail("A lightmap bake is already in progress.");

                    Lightmapping.BakeAsync();
                    return ToolResponse.Ok("Lightmap bake started asynchronously. Use get_lightmap_settings to check progress.");

                default:
                    return ToolResponse.Fail($"Invalid bake mode: '{mode}'. Valid: clear, bake");
            }
        }

        private ToolResponse HandleGetLightmapSettings()
        {
            var data = new JObject
            {
                ["isRunning"] = Lightmapping.isRunning,
                ["lightingDataAsset"] = Lightmapping.lightingDataAsset != null ? Lightmapping.lightingDataAsset.name : null,
                ["lightmapCount"] = LightmapSettings.lightmaps.Length,
                ["lightmapsMode"] = LightmapSettings.lightmapsMode.ToString(),
                ["bakedGI"] = Lightmapping.bakedGI,
                ["realtimeGI"] = Lightmapping.realtimeGI
            };

            // Lightmap textures info
            if (LightmapSettings.lightmaps.Length > 0)
            {
                var lightmaps = new JArray();
                foreach (var lm in LightmapSettings.lightmaps)
                {
                    var lmInfo = new JObject();
                    if (lm.lightmapColor != null)
                        lmInfo["colorMap"] = lm.lightmapColor.name;
                    if (lm.lightmapDir != null)
                        lmInfo["dirMap"] = lm.lightmapDir.name;
                    if (lm.shadowMask != null)
                        lmInfo["shadowMask"] = lm.shadowMask.name;
                    lightmaps.Add(lmInfo);
                }
                data["lightmaps"] = lightmaps;
            }

            return ToolResponse.OkWithData(data, "Lightmap settings retrieved.");
        }

        #endregion

        #region Helpers

        private static LightShadows ParseShadowType(string shadowsStr)
        {
            switch (shadowsStr.ToLowerInvariant())
            {
                case "none": return LightShadows.None;
                case "hard": return LightShadows.Hard;
                case "soft": return LightShadows.Soft;
                default: return LightShadows.None;
            }
        }

        private static JObject SerializeLightInfo(GameObject go, Light light)
        {
            var data = new JObject
            {
                ["name"] = go.name,
                ["instanceId"] = go.GetInstanceID(),
                ["type"] = light.type.ToString(),
                ["color"] = new JObject
                {
                    ["r"] = Math.Round(light.color.r, 4),
                    ["g"] = Math.Round(light.color.g, 4),
                    ["b"] = Math.Round(light.color.b, 4),
                    ["a"] = Math.Round(light.color.a, 4)
                },
                ["intensity"] = light.intensity,
                ["range"] = light.range,
                ["spotAngle"] = light.spotAngle,
                ["innerSpotAngle"] = light.innerSpotAngle,
                ["shadows"] = light.shadows.ToString(),
                ["shadowStrength"] = light.shadowStrength,
                ["shadowBias"] = light.shadowBias,
                ["shadowNormalBias"] = light.shadowNormalBias,
                ["enabled"] = light.enabled,
                ["lightmapBakeType"] = light.lightmapBakeType.ToString(),
                ["bounceIntensity"] = light.bounceIntensity,
                ["position"] = ToolHelpers.Vector3ToJson(go.transform.position),
                ["rotation"] = ToolHelpers.QuaternionToJson(go.transform.rotation)
            };

            if (light.cookie != null)
                data["cookie"] = light.cookie.name;

            return data;
        }

        #endregion
    }
}
