using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace AgentCore.Editor.Tools.Native.Utility
{
    /// <summary>
    /// Create, modify, and inspect materials.
    /// Directly calls Unity Material API as part of the native tool system.
    /// </summary>
    [AgentTool("manage_material",
        Description = "Create, modify, and inspect materials",
        Category = "Material",
        RequiresMainThread = true)]
    public class ManageMaterialTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""create"", ""get_info"", ""set_property"", ""set_shader"", ""list_properties"", ""assign""],
                    ""description"": ""Action to perform on materials""
                },
                ""material_path"": {
                    ""type"": ""string"",
                    ""description"": ""Material asset path (e.g., 'Assets/Materials/MyMaterial.mat'). Required for all actions.""
                },
                ""shader_name"": {
                    ""type"": ""string"",
                    ""description"": ""Shader name (e.g., 'Standard', 'Universal Render Pipeline/Lit')""
                },
                ""property_name"": {
                    ""type"": ""string"",
                    ""description"": ""Material property name (e.g., '_Color', '_MainTex')""
                },
                ""property_type"": {
                    ""type"": ""string"",
                    ""enum"": [""color"", ""float"", ""int"", ""vector"", ""texture"", ""keyword""],
                    ""description"": ""Property type""
                },
                ""value"": {
                    ""description"": ""Property value (format depends on property_type)""
                },
                ""target"": {
                    ""type"": ""string"",
                    ""description"": ""Target GameObject for assign action""
                },
                ""material_index"": {
                    ""type"": ""integer"",
                    ""description"": ""Material slot index for assign (default: 0)""
                }
            },
            ""required"": [""action""]
        }");

        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_material",
            description: "Create, modify, and inspect materials",
            category: "Material",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        public Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ToolResponse response;

            try
            {
                var action = ToolHelpers.GetRequiredString(parameters, "action").ToLowerInvariant();

                switch (action)
                {
                    case "create":
                        response = HandleCreate(parameters);
                        break;
                    case "get_info":
                        response = HandleGetInfo(parameters);
                        break;
                    case "set_property":
                        response = HandleSetProperty(parameters);
                        break;
                    case "set_shader":
                        response = HandleSetShader(parameters);
                        break;
                    case "list_properties":
                        response = HandleListProperties(parameters);
                        break;
                    case "assign":
                        response = HandleAssign(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: create, get_info, set_property, set_shader, list_properties, assign");
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

        #region Parameter Helpers

        /// <summary>
        /// 获取材质路径参数，支持多种参数名以兼容 LLM 可能使用的不同命名风格。
        /// 优先级：material_path > path > materialPath
        /// </summary>
        private static string GetMaterialPath(JObject parameters)
        {
            // 优先使用 schema 中定义的 snake_case 名称
            var value = parameters?["material_path"]?.ToString();
            // 兼容旧的短名称
            if (string.IsNullOrEmpty(value))
                value = parameters?["path"]?.ToString();
            // 兼容 LLM 可能使用的 camelCase 名称
            if (string.IsNullOrEmpty(value))
                value = parameters?["materialPath"]?.ToString();

            if (string.IsNullOrEmpty(value))
                throw new ArgumentException("Required parameter 'material_path' is missing or empty. Please provide the material asset path (e.g., 'Assets/Materials/MyMaterial.mat').");

            return value;
        }

        /// <summary>
        /// 获取可选字符串参数，支持 snake_case 和 camelCase 两种命名风格。
        /// </summary>
        private static string GetStringWithFallback(JObject parameters, string snakeName, string camelName, string defaultValue = null)
        {
            var value = parameters?[snakeName]?.ToString();
            if (string.IsNullOrEmpty(value))
                value = parameters?[camelName]?.ToString();
            return value ?? defaultValue;
        }

        /// <summary>
        /// 获取必需字符串参数，支持 snake_case 和 camelCase 两种命名风格。
        /// </summary>
        private static string GetRequiredStringWithFallback(JObject parameters, string snakeName, string camelName)
        {
            var value = parameters?[snakeName]?.ToString();
            if (string.IsNullOrEmpty(value))
                value = parameters?[camelName]?.ToString();
            if (string.IsNullOrEmpty(value))
                throw new ArgumentException($"Required parameter '{snakeName}' is missing or empty.");
            return value;
        }

        #endregion

        #region Action Handlers

        private ToolResponse HandleCreate(JObject parameters)
        {
            try
            {
                var path = GetMaterialPath(parameters);
                path = ToolHelpers.NormalizeAssetPath(path);

                if (!path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                    path += ".mat";

                var shaderName = GetStringWithFallback(parameters, "shader_name", "shaderName", "Standard");
                var shader = Shader.Find(shaderName);

                if (shader == null)
                {
                    // Try common URP shader as fallback
                    shader = Shader.Find("Universal Render Pipeline/Lit");
                    if (shader == null)
                        shader = Shader.Find("Standard");
                    if (shader == null)
                        return ToolResponse.Fail($"Shader not found: '{shaderName}' and no fallback available.");
                }

                var material = new Material(shader);

                // Ensure directory exists
                ToolHelpers.EnsureDirectoryExists(path);

                AssetDatabase.CreateAsset(material, path);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                return ToolResponse.OkWithData(new JObject
                {
                    ["path"] = path,
                    ["shader"] = shader.name,
                    ["instanceId"] = material.GetInstanceID()
                }, $"Created material at '{path}' with shader '{shader.name}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Create material failed: {ex.Message}");
            }
        }

        private ToolResponse HandleGetInfo(JObject parameters)
        {
            try
            {
                var path = GetMaterialPath(parameters);
                path = ToolHelpers.NormalizeAssetPath(path);

                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null)
                    return ToolResponse.Fail($"Material not found at path: {path}");

                var info = new JObject
                {
                    ["path"] = path,
                    ["name"] = material.name,
                    ["shader"] = material.shader != null ? material.shader.name : "None",
                    ["instanceId"] = material.GetInstanceID(),
                    ["renderQueue"] = material.renderQueue,
                    ["passCount"] = material.passCount
                };

                // Shader keywords
                var keywords = material.shaderKeywords;
                if (keywords.Length > 0)
                {
                    info["enabledKeywords"] = new JArray(keywords);
                }

                // List properties with values
                if (material.shader != null)
                {
                    var properties = new JArray();
                    int propCount = ShaderUtil.GetPropertyCount(material.shader);
                    for (int i = 0; i < propCount; i++)
                    {
                        var propName = ShaderUtil.GetPropertyName(material.shader, i);
                        var propType = ShaderUtil.GetPropertyType(material.shader, i);
                        var propDesc = ShaderUtil.GetPropertyDescription(material.shader, i);

                        var prop = new JObject
                        {
                            ["name"] = propName,
                            ["type"] = propType.ToString(),
                            ["description"] = propDesc
                        };

                        // Get current value
                        try
                        {
                            switch (propType)
                            {
                                case ShaderUtil.ShaderPropertyType.Color:
                                    var color = material.GetColor(propName);
                                    prop["value"] = $"#{ColorUtility.ToHtmlStringRGBA(color)}";
                                    break;
                                case ShaderUtil.ShaderPropertyType.Float:
                                case ShaderUtil.ShaderPropertyType.Range:
                                    prop["value"] = material.GetFloat(propName);
                                    break;
                                case ShaderUtil.ShaderPropertyType.TexEnv:
                                    var tex = material.GetTexture(propName);
                                    prop["value"] = tex != null ? tex.name : "None";
                                    break;
                                case ShaderUtil.ShaderPropertyType.Vector:
                                    var vec = material.GetVector(propName);
                                    prop["value"] = new JObject
                                    {
                                        ["x"] = vec.x, ["y"] = vec.y,
                                        ["z"] = vec.z, ["w"] = vec.w
                                    };
                                    break;
                                case ShaderUtil.ShaderPropertyType.Int:
                                    prop["value"] = material.GetInt(propName);
                                    break;
                            }
                        }
                        catch
                        {
                            prop["value"] = "Unable to read";
                        }

                        properties.Add(prop);
                    }
                    info["properties"] = properties;
                }

                return ToolResponse.OkWithData(info, $"Material info for '{path}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Get info failed: {ex.Message}");
            }
        }

        private ToolResponse HandleSetProperty(JObject parameters)
        {
            try
            {
                var path = GetMaterialPath(parameters);
                path = ToolHelpers.NormalizeAssetPath(path);

                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null)
                    return ToolResponse.Fail($"Material not found at path: {path}");

                var propertyName = GetRequiredStringWithFallback(parameters, "property_name", "propertyName");
                var propertyType = GetRequiredStringWithFallback(parameters, "property_type", "propertyType").ToLowerInvariant();
                var valueToken = parameters["value"];

                if (valueToken == null)
                    return ToolResponse.Fail("Parameter 'value' is required for set_property.");

                ToolHelpers.RecordUndo(material, "Set Material Property");

                switch (propertyType)
                {
                    case "color":
                        var color = ToolHelpers.ParseColor(valueToken, Color.white);
                        material.SetColor(propertyName, color);
                        break;

                    case "float":
                        var floatVal = valueToken.Value<float>();
                        material.SetFloat(propertyName, floatVal);
                        break;

                    case "int":
                        var intVal = valueToken.Value<int>();
                        material.SetInt(propertyName, intVal);
                        break;

                    case "vector":
                        var vec = new Vector4(
                            valueToken["x"]?.Value<float>() ?? 0f,
                            valueToken["y"]?.Value<float>() ?? 0f,
                            valueToken["z"]?.Value<float>() ?? 0f,
                            valueToken["w"]?.Value<float>() ?? 0f
                        );
                        material.SetVector(propertyName, vec);
                        break;

                    case "texture":
                        var texPath = valueToken.ToString();
                        texPath = ToolHelpers.NormalizeAssetPath(texPath);
                        var texture = AssetDatabase.LoadAssetAtPath<Texture>(texPath);
                        if (texture == null)
                            return ToolResponse.Fail($"Texture not found at path: {texPath}");
                        material.SetTexture(propertyName, texture);
                        break;

                    case "keyword":
                        var keywordValue = valueToken.Value<bool>();
                        if (keywordValue)
                            material.EnableKeyword(propertyName);
                        else
                            material.DisableKeyword(propertyName);
                        break;

                    default:
                        return ToolResponse.Fail(
                            $"Unknown property type: '{propertyType}'. Valid types: color, float, int, vector, texture, keyword");
                }

                EditorUtility.SetDirty(material);
                AssetDatabase.SaveAssets();

                return ToolResponse.Ok($"Set property '{propertyName}' on material '{path}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Set property failed: {ex.Message}");
            }
        }

        private ToolResponse HandleSetShader(JObject parameters)
        {
            try
            {
                var path = GetMaterialPath(parameters);
                path = ToolHelpers.NormalizeAssetPath(path);

                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null)
                    return ToolResponse.Fail($"Material not found at path: {path}");

                var shaderName = GetRequiredStringWithFallback(parameters, "shader_name", "shaderName");
                var shader = Shader.Find(shaderName);
                if (shader == null)
                    return ToolResponse.Fail($"Shader not found: '{shaderName}'");

                ToolHelpers.RecordUndo(material, "Set Material Shader");
                material.shader = shader;

                EditorUtility.SetDirty(material);
                AssetDatabase.SaveAssets();

                return ToolResponse.OkWithData(new JObject
                {
                    ["path"] = path,
                    ["shader"] = shader.name
                }, $"Set shader to '{shader.name}' on material '{path}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Set shader failed: {ex.Message}");
            }
        }

        private ToolResponse HandleListProperties(JObject parameters)
        {
            try
            {
                var path = GetMaterialPath(parameters);
                path = ToolHelpers.NormalizeAssetPath(path);

                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null)
                    return ToolResponse.Fail($"Material not found at path: {path}");

                if (material.shader == null)
                    return ToolResponse.Fail($"Material has no shader assigned.");

                var properties = new JArray();
                int propCount = ShaderUtil.GetPropertyCount(material.shader);
                for (int i = 0; i < propCount; i++)
                {
                    var propName = ShaderUtil.GetPropertyName(material.shader, i);
                    var propType = ShaderUtil.GetPropertyType(material.shader, i);
                    var propDesc = ShaderUtil.GetPropertyDescription(material.shader, i);

                    var prop = new JObject
                    {
                        ["name"] = propName,
                        ["type"] = propType.ToString(),
                        ["description"] = propDesc
                    };

                    // Add range info for Range type
                    if (propType == ShaderUtil.ShaderPropertyType.Range)
                    {
                        prop["rangeMin"] = ShaderUtil.GetRangeLimits(material.shader, i, 1);
                        prop["rangeMax"] = ShaderUtil.GetRangeLimits(material.shader, i, 2);
                    }

                    properties.Add(prop);
                }

                return ToolResponse.OkWithData(new JObject
                {
                    ["path"] = path,
                    ["shader"] = material.shader.name,
                    ["propertyCount"] = propCount,
                    ["properties"] = properties
                }, $"Listed {propCount} properties for material '{path}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"List properties failed: {ex.Message}");
            }
        }

        private ToolResponse HandleAssign(JObject parameters)
        {
            try
            {
                var path = GetMaterialPath(parameters);
                path = ToolHelpers.NormalizeAssetPath(path);

                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null)
                    return ToolResponse.Fail($"Material not found at path: {path}");

                var targetName = ToolHelpers.GetRequiredString(parameters, "target");
                var go = ToolHelpers.FindGameObject(targetName);
                if (go == null)
                    return ToolResponse.Fail($"GameObject not found: '{targetName}'");

                var renderer = go.GetComponent<Renderer>();
                if (renderer == null)
                    return ToolResponse.Fail($"GameObject '{targetName}' has no Renderer component.");

                // Support both snake_case and camelCase for material_index
                var materialIndex = parameters?["material_index"] != null
                    ? parameters["material_index"].Value<int>()
                    : ToolHelpers.GetOptionalInt(parameters, "materialIndex", 0);

                ToolHelpers.RecordUndo(renderer, "Assign Material");

                var materials = renderer.sharedMaterials;
                if (materialIndex < 0 || materialIndex >= materials.Length)
                {
                    // If index is out of range, expand the array
                    if (materialIndex == materials.Length)
                    {
                        var newMats = new Material[materials.Length + 1];
                        materials.CopyTo(newMats, 0);
                        materials = newMats;
                    }
                    else
                    {
                        return ToolResponse.Fail(
                            $"Material index {materialIndex} is out of range. Renderer has {materials.Length} material slot(s).");
                    }
                }

                materials[materialIndex] = material;
                renderer.sharedMaterials = materials;

                EditorUtility.SetDirty(renderer);

                return ToolResponse.OkWithData(new JObject
                {
                    ["materialPath"] = path,
                    ["target"] = targetName,
                    ["materialIndex"] = materialIndex
                }, $"Assigned material '{path}' to '{targetName}' at slot {materialIndex}.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Assign material failed: {ex.Message}");
            }
        }

        #endregion
    }
}
