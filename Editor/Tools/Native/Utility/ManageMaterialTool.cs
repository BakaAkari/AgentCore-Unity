using System;
using System.Collections.Generic;
using System.Linq;
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
        Description = "Unity Material management — create, configure, inspect, and batch-modify materials and their shader properties. " +
                      "Actions: create (new material with shader), get_info (shader/render queue/properties/keywords), " +
                      "set_property (color/float/vector/int by property name), set_texture (assign texture to slot), " +
                      "set_shader (change material shader), list_properties (all available properties with types), " +
                      "assign (apply material to Renderer on a GameObject), copy_properties (clone properties between materials), " +
                      "set_keyword/get_keywords (shader feature keywords), find_by_shader (all materials using a shader), " +
                      "batch_set_properties (modify same property across multiple materials), list_materials (all materials in project), " +
                      "get_shader_info (shader details including Shader Graph identification, pass count, property list). " +
                      "USE FOR: creating materials, changing colors/textures/floats on materials, assigning materials to objects, " +
                      "shader keyword toggling, finding all materials using a specific shader for batch updates. " +
                      "NOT FOR: shader code editing (use manage_file for .shader files), Shader Graph visual editing, render pipeline asset configuration. " +
                      "ACTIVATE WHEN: user mentions 'material', 'shader property', 'texture slot', 'render queue', 'material color', '_MainTex', '_Color'.",
        Category = "Material",
        Visibility = ToolVisibility.OnDemand,
        RequiresMainThread = true)]
    public class ManageMaterialTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""create"", ""get_info"", ""set_property"", ""set_shader"", ""list_properties"", ""assign"", ""copy_properties"", ""set_texture"", ""set_keyword"", ""get_keywords"", ""find_by_shader"", ""batch_set_properties"", ""list_materials"", ""get_shader_info""],
                    ""description"": ""Action to perform on materials""
                },
                ""material_path"": {
                    ""type"": ""string"",
                    ""description"": ""Material asset path (e.g., 'Assets/Materials/MyMaterial.mat'). Required for most actions.""
                },
                ""shader_name"": {
                    ""type"": ""string"",
                    ""description"": ""Shader name (e.g., 'Standard', 'Universal Render Pipeline/Lit'). Used by find_by_shader and get_shader_info.""
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
                },
                ""source_path"": {
                    ""type"": ""string"",
                    ""description"": ""Source material path for copy_properties action""
                },
                ""target_path"": {
                    ""type"": ""string"",
                    ""description"": ""Target material path for copy_properties action""
                },
                ""texture_path"": {
                    ""type"": ""string"",
                    ""description"": ""Texture asset path for set_texture action""
                },
                ""keyword"": {
                    ""type"": ""string"",
                    ""description"": ""Shader keyword name for set_keyword/get_keywords actions""
                },
                ""enabled"": {
                    ""type"": ""boolean"",
                    ""description"": ""Whether to enable or disable a keyword (default: true)""
                },
                ""asset_path"": {
                    ""type"": ""string"",
                    ""description"": ""Alias for material_path""
                },
                ""properties"": {
                    ""type"": ""array"",
                    ""description"": ""Array of property objects for batch_set_properties. Each object: {name, type, value}"",
                    ""items"": {
                        ""type"": ""object"",
                        ""properties"": {
                            ""name"": { ""type"": ""string"" },
                            ""type"": { ""type"": ""string"", ""enum"": [""color"", ""float"", ""int"", ""vector"", ""texture"", ""keyword""] },
                            ""value"": {}
                        },
                        ""required"": [""name"", ""type"", ""value""]
                    }
                },
                ""folder"": {
                    ""type"": ""string"",
                    ""description"": ""Folder path filter for list_materials (e.g., 'Assets/Materials'). Default: entire project.""
                },
                ""shader_filter"": {
                    ""type"": ""string"",
                    ""description"": ""Shader name filter for list_materials (partial match supported)""
                },
                ""max_results"": {
                    ""type"": ""integer"",
                    ""description"": ""Maximum number of results to return for list_materials (default: 100)""
                },
                ""shader_path"": {
                    ""type"": ""string"",
                    ""description"": ""Shader asset path for get_shader_info (e.g., 'Assets/Shaders/MyShader.shadergraph')""
                }
            },
            ""required"": [""action""]
        }");

        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_material",
            description: "Create, modify, inspect, and batch-manage materials. Supports shader info retrieval including Shader Graph identification.",
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
                    case "copy_properties":
                        response = HandleCopyProperties(parameters);
                        break;
                    case "set_texture":
                        response = HandleSetTexture(parameters);
                        break;
                    case "set_keyword":
                        response = HandleSetKeyword(parameters);
                        break;
                    case "get_keywords":
                        response = HandleGetKeywords(parameters);
                        break;
                    case "find_by_shader":
                        response = HandleFindByShader(parameters);
                        break;
                    case "batch_set_properties":
                        response = HandleBatchSetProperties(parameters);
                        break;
                    case "list_materials":
                        response = HandleListMaterials(parameters);
                        break;
                    case "get_shader_info":
                        response = HandleGetShaderInfo(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: create, get_info, set_property, set_shader, list_properties, assign, copy_properties, set_texture, set_keyword, get_keywords, find_by_shader, batch_set_properties, list_materials, get_shader_info");
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
            // 兼容 asset_path 别名
            if (string.IsNullOrEmpty(value))
                value = parameters?["asset_path"]?.ToString();
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
                        if (!ToolHelpers.TryCoerceFloat(valueToken, "value", out var floatVal)) throw new ArgumentException($"value expected float, got {valueToken.Type}");
                        material.SetFloat(propertyName, floatVal);
                        break;

                    case "int":
                        if (!ToolHelpers.TryCoerceInt(valueToken, "value", out var intVal)) throw new ArgumentException($"value expected int, got {valueToken.Type}");
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
                        if (!ToolHelpers.TryCoerceBool(valueToken, "value", out var keywordValue)) throw new ArgumentException($"value expected bool, got {valueToken.Type}");
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

        /// <summary>
        /// Copies all properties from a source material to a target material.
        /// Uses Material.CopyPropertiesFromMaterial().
        /// </summary>
        private ToolResponse HandleCopyProperties(JObject parameters)
        {
            try
            {
                var sourcePath = GetRequiredStringWithFallback(parameters, "source_path", "sourcePath");
                sourcePath = ToolHelpers.NormalizeAssetPath(sourcePath);

                var targetPath = GetRequiredStringWithFallback(parameters, "target_path", "targetPath");
                targetPath = ToolHelpers.NormalizeAssetPath(targetPath);

                var sourceMaterial = AssetDatabase.LoadAssetAtPath<Material>(sourcePath);
                if (sourceMaterial == null)
                    return ToolResponse.Fail($"Source material not found at path: {sourcePath}");

                var targetMaterial = AssetDatabase.LoadAssetAtPath<Material>(targetPath);
                if (targetMaterial == null)
                    return ToolResponse.Fail($"Target material not found at path: {targetPath}");

                ToolHelpers.RecordUndo(targetMaterial, "Copy Material Properties");
                targetMaterial.CopyPropertiesFromMaterial(sourceMaterial);

                EditorUtility.SetDirty(targetMaterial);
                AssetDatabase.SaveAssets();

                return ToolResponse.OkWithData(new JObject
                {
                    ["sourcePath"] = sourcePath,
                    ["targetPath"] = targetPath,
                    ["sourceShader"] = sourceMaterial.shader != null ? sourceMaterial.shader.name : "None",
                    ["targetShader"] = targetMaterial.shader != null ? targetMaterial.shader.name : "None"
                }, $"Copied properties from '{sourcePath}' to '{targetPath}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Copy properties failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Sets a texture property on a material.
        /// Loads the texture from the specified asset path and assigns it.
        /// </summary>
        private ToolResponse HandleSetTexture(JObject parameters)
        {
            try
            {
                var materialPath = GetMaterialPath(parameters);
                materialPath = ToolHelpers.NormalizeAssetPath(materialPath);

                var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (material == null)
                    return ToolResponse.Fail($"Material not found at path: {materialPath}");

                var propertyName = GetRequiredStringWithFallback(parameters, "property_name", "propertyName");
                var texturePath = GetRequiredStringWithFallback(parameters, "texture_path", "texturePath");
                texturePath = ToolHelpers.NormalizeAssetPath(texturePath);

                var texture = AssetDatabase.LoadAssetAtPath<Texture>(texturePath);
                if (texture == null)
                    return ToolResponse.Fail($"Texture not found at path: {texturePath}");

                // Verify the property exists on the shader
                if (!material.HasProperty(propertyName))
                    return ToolResponse.Fail($"Material does not have property '{propertyName}'. Use 'list_properties' to see available properties.");

                ToolHelpers.RecordUndo(material, "Set Material Texture");
                material.SetTexture(propertyName, texture);

                EditorUtility.SetDirty(material);
                AssetDatabase.SaveAssets();

                return ToolResponse.OkWithData(new JObject
                {
                    ["materialPath"] = materialPath,
                    ["propertyName"] = propertyName,
                    ["texturePath"] = texturePath,
                    ["textureName"] = texture.name
                }, $"Set texture '{texture.name}' on property '{propertyName}' of material '{materialPath}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Set texture failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Enables or disables a shader keyword on a material.
        /// </summary>
        private ToolResponse HandleSetKeyword(JObject parameters)
        {
            try
            {
                var materialPath = GetMaterialPath(parameters);
                materialPath = ToolHelpers.NormalizeAssetPath(materialPath);

                var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (material == null)
                    return ToolResponse.Fail($"Material not found at path: {materialPath}");

                var keyword = ToolHelpers.GetRequiredString(parameters, "keyword");
                var enabled = ToolHelpers.GetOptionalBool(parameters, "enabled", true);

                ToolHelpers.RecordUndo(material, "Set Material Keyword");

                if (enabled)
                    material.EnableKeyword(keyword);
                else
                    material.DisableKeyword(keyword);

                EditorUtility.SetDirty(material);
                AssetDatabase.SaveAssets();

                return ToolResponse.OkWithData(new JObject
                {
                    ["materialPath"] = materialPath,
                    ["keyword"] = keyword,
                    ["enabled"] = enabled,
                    ["allKeywords"] = new JArray(material.shaderKeywords)
                }, $"{(enabled ? "Enabled" : "Disabled")} keyword '{keyword}' on material '{materialPath}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Set keyword failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets all currently enabled shader keywords on a material.
        /// </summary>
        private ToolResponse HandleGetKeywords(JObject parameters)
        {
            try
            {
                var materialPath = GetMaterialPath(parameters);
                materialPath = ToolHelpers.NormalizeAssetPath(materialPath);

                var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (material == null)
                    return ToolResponse.Fail($"Material not found at path: {materialPath}");

                var keywords = material.shaderKeywords;

                return ToolResponse.OkWithData(new JObject
                {
                    ["materialPath"] = materialPath,
                    ["shader"] = material.shader != null ? material.shader.name : "None",
                    ["keywords"] = new JArray(keywords),
                    ["keywordCount"] = keywords.Length
                }, $"Material '{materialPath}' has {keywords.Length} enabled keyword(s).");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Get keywords failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Finds all materials in the project that use a specified shader.
        /// </summary>
        private ToolResponse HandleFindByShader(JObject parameters)
        {
            try
            {
                var shaderName = GetRequiredStringWithFallback(parameters, "shader_name", "shaderName");
                var targetShader = Shader.Find(shaderName);

                // We'll match by name even if Shader.Find fails (for partial matches)
                var materialGuids = AssetDatabase.FindAssets("t:Material");
                var results = new JArray();

                foreach (var guid in materialGuids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                    if (material == null || material.shader == null) continue;

                    bool matches = false;
                    if (targetShader != null)
                    {
                        // Exact shader match
                        matches = material.shader == targetShader;
                    }
                    else
                    {
                        // Partial name match
                        matches = material.shader.name.IndexOf(shaderName, StringComparison.OrdinalIgnoreCase) >= 0;
                    }

                    if (matches)
                    {
                        results.Add(new JObject
                        {
                            ["path"] = path,
                            ["name"] = material.name,
                            ["shader"] = material.shader.name,
                            ["renderQueue"] = material.renderQueue
                        });
                    }
                }

                return ToolResponse.OkWithData(new JObject
                {
                    ["shaderName"] = shaderName,
                    ["exactMatch"] = targetShader != null,
                    ["materials"] = results,
                    ["count"] = results.Count
                }, $"Found {results.Count} material(s) using shader '{shaderName}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Find by shader failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Batch set multiple properties on a material in a single operation.
        /// Uses a "best effort" strategy: sets as many properties as possible and reports failures.
        /// </summary>
        private ToolResponse HandleBatchSetProperties(JObject parameters)
        {
            try
            {
                var materialPath = GetMaterialPath(parameters);
                materialPath = ToolHelpers.NormalizeAssetPath(materialPath);

                var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (material == null)
                    return ToolResponse.Fail($"Material not found at path: {materialPath}");

                var propertiesToken = parameters["properties"] as JArray;
                if (propertiesToken == null || propertiesToken.Count == 0)
                    return ToolResponse.Fail("Parameter 'properties' is required and must be a non-empty array. Each item: {name, type, value}");

                ToolHelpers.RecordUndo(material, "Batch Set Material Properties");

                var successes = new List<string>();
                var failures = new List<object>();

                foreach (var propToken in propertiesToken)
                {
                    var propObj = propToken as JObject;
                    if (propObj == null)
                    {
                        failures.Add(new { name = "(invalid)", error = "Property entry is not a valid object" });
                        continue;
                    }

                    var propName = propObj["name"]?.ToString();
                    var propType = propObj["type"]?.ToString()?.ToLowerInvariant();
                    var valueToken = propObj["value"];

                    if (string.IsNullOrEmpty(propName) || string.IsNullOrEmpty(propType))
                    {
                        failures.Add(new { name = propName ?? "(null)", error = "Missing 'name' or 'type'" });
                        continue;
                    }

                    try
                    {
                        switch (propType)
                        {
                            case "color":
                                var color = ToolHelpers.ParseColor(valueToken, Color.white);
                                material.SetColor(propName, color);
                                break;
                            case "float":
                                if (!ToolHelpers.TryCoerceFloat(valueToken, "value", out var _mfv)) throw new ArgumentException($"value expected float, got {valueToken.Type}"); material.SetFloat(propName, _mfv);
                                break;
                            case "int":
                                if (!ToolHelpers.TryCoerceInt(valueToken, "value", out var _miv)) throw new ArgumentException($"value expected int, got {valueToken.Type}"); material.SetInt(propName, _miv);
                                break;
                            case "vector":
                                var vec = new Vector4(
                                    valueToken["x"]?.Value<float>() ?? 0f,
                                    valueToken["y"]?.Value<float>() ?? 0f,
                                    valueToken["z"]?.Value<float>() ?? 0f,
                                    valueToken["w"]?.Value<float>() ?? 0f
                                );
                                material.SetVector(propName, vec);
                                break;
                            case "texture":
                                var texPath = ToolHelpers.NormalizeAssetPath(valueToken.ToString());
                                var texture = AssetDatabase.LoadAssetAtPath<Texture>(texPath);
                                if (texture == null)
                                {
                                    failures.Add(new { name = propName, error = $"Texture not found: {texPath}" });
                                    continue;
                                }
                                material.SetTexture(propName, texture);
                                break;
                            case "keyword":
                                if (!ToolHelpers.TryCoerceBool(valueToken, "value", out var keywordEnabled)) throw new ArgumentException($"value expected bool, got {valueToken.Type}");
                                if (keywordEnabled)
                                    material.EnableKeyword(propName);
                                else
                                    material.DisableKeyword(propName);
                                break;
                            default:
                                failures.Add(new { name = propName, error = $"Unknown type: {propType}" });
                                continue;
                        }
                        successes.Add(propName);
                    }
                    catch (Exception ex)
                    {
                        failures.Add(new { name = propName, error = ex.Message });
                    }
                }

                EditorUtility.SetDirty(material);
                AssetDatabase.SaveAssets();

                var resultData = new JObject
                {
                    ["materialPath"] = materialPath,
                    ["totalRequested"] = propertiesToken.Count,
                    ["succeeded"] = successes.Count,
                    ["failed"] = failures.Count,
                    ["successProperties"] = new JArray(successes.ToArray()),
                };

                if (failures.Count > 0)
                {
                    var failArray = new JArray();
                    foreach (var f in failures)
                        failArray.Add(JObject.FromObject(f));
                    resultData["failures"] = failArray;
                }

                string message = failures.Count == 0
                    ? $"Successfully set {successes.Count} properties on '{materialPath}'."
                    : $"Set {successes.Count}/{propertiesToken.Count} properties on '{materialPath}'. {failures.Count} failed.";

                return ToolResponse.OkWithData(resultData, message);
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Batch set properties failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Lists all materials in the project with optional folder and shader filters.
        /// </summary>
        private ToolResponse HandleListMaterials(JObject parameters)
        {
            try
            {
                var folder = ToolHelpers.GetOptionalString(parameters, "folder");
                var shaderFilter = ToolHelpers.GetOptionalString(parameters, "shader_filter");
                var maxResults = ToolHelpers.GetOptionalInt(parameters, "max_results", 100);

                // Find all material assets
                var materialGuids = AssetDatabase.FindAssets("t:Material",
                    string.IsNullOrEmpty(folder) ? null : new[] { folder });

                var results = new JArray();
                int totalFound = 0;

                foreach (var guid in materialGuids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);

                    // Apply folder filter (more precise than FindAssets search folder)
                    if (!string.IsNullOrEmpty(folder) &&
                        !path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                    if (material == null) continue;

                    // Apply shader filter
                    if (!string.IsNullOrEmpty(shaderFilter))
                    {
                        if (material.shader == null) continue;
                        if (material.shader.name.IndexOf(shaderFilter, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                    }

                    totalFound++;

                    if (results.Count < maxResults)
                    {
                        results.Add(new JObject
                        {
                            ["path"] = path,
                            ["name"] = material.name,
                            ["shader"] = material.shader != null ? material.shader.name : "None",
                            ["renderQueue"] = material.renderQueue
                        });
                    }
                }

                return ToolResponse.OkWithData(new JObject
                {
                    ["folder"] = folder ?? "(all)",
                    ["shaderFilter"] = shaderFilter ?? "(none)",
                    ["totalFound"] = totalFound,
                    ["returned"] = results.Count,
                    ["truncated"] = totalFound > maxResults,
                    ["materials"] = results
                }, $"Found {totalFound} material(s)" +
                   (!string.IsNullOrEmpty(folder) ? $" in '{folder}'" : "") +
                   (!string.IsNullOrEmpty(shaderFilter) ? $" with shader matching '{shaderFilter}'" : "") +
                   (totalFound > maxResults ? $" (showing first {maxResults})" : "") + ".");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"List materials failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets detailed information about a shader, including whether it's a Shader Graph asset,
        /// its properties, keywords, and variant count.
        /// </summary>
        private ToolResponse HandleGetShaderInfo(JObject parameters)
        {
            try
            {
                // Support both shader_name (by name lookup) and shader_path (by asset path)
                var shaderPath = ToolHelpers.GetOptionalString(parameters, "shader_path");
                var shaderName = GetStringWithFallback(parameters, "shader_name", "shaderName");

                Shader shader = null;
                string resolvedPath = null;
                bool isShaderGraph = false;

                if (!string.IsNullOrEmpty(shaderPath))
                {
                    // Load by asset path
                    shaderPath = ToolHelpers.NormalizeAssetPath(shaderPath);
                    shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
                    resolvedPath = shaderPath;

                    // Check if it's a Shader Graph by extension
                    isShaderGraph = shaderPath.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase)
                        || shaderPath.EndsWith(".shadersubgraph", StringComparison.OrdinalIgnoreCase);
                }
                else if (!string.IsNullOrEmpty(shaderName))
                {
                    // Find by name
                    shader = Shader.Find(shaderName);
                    if (shader != null)
                    {
                        resolvedPath = AssetDatabase.GetAssetPath(shader);
                        // Check if it's a Shader Graph by path or name prefix
                        isShaderGraph = (!string.IsNullOrEmpty(resolvedPath) &&
                            (resolvedPath.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase)
                             || resolvedPath.EndsWith(".shadersubgraph", StringComparison.OrdinalIgnoreCase)))
                            || shaderName.StartsWith("Shader Graphs/", StringComparison.OrdinalIgnoreCase);
                    }
                }
                else
                {
                    return ToolResponse.Fail("Either 'shader_name' or 'shader_path' is required for get_shader_info.");
                }

                if (shader == null)
                    return ToolResponse.Fail($"Shader not found. Name: '{shaderName ?? "(none)"}', Path: '{shaderPath ?? "(none)"}'");

                // Gather shader properties
                var properties = new JArray();
                int propCount = ShaderUtil.GetPropertyCount(shader);
                for (int i = 0; i < propCount; i++)
                {
                    var prop = new JObject
                    {
                        ["name"] = ShaderUtil.GetPropertyName(shader, i),
                        ["type"] = ShaderUtil.GetPropertyType(shader, i).ToString(),
                        ["description"] = ShaderUtil.GetPropertyDescription(shader, i)
                    };

                    var propType = ShaderUtil.GetPropertyType(shader, i);
                    if (propType == ShaderUtil.ShaderPropertyType.Range)
                    {
                        prop["rangeMin"] = ShaderUtil.GetRangeLimits(shader, i, 1);
                        prop["rangeMax"] = ShaderUtil.GetRangeLimits(shader, i, 2);
                    }

                    properties.Add(prop);
                }

                // Determine shader type/category
                string shaderType = "Standard";
                if (isShaderGraph)
                    shaderType = "Shader Graph";
                else if (shader.name.StartsWith("Universal Render Pipeline/", StringComparison.OrdinalIgnoreCase)
                    || shader.name.StartsWith("URP/", StringComparison.OrdinalIgnoreCase))
                    shaderType = "URP Built-in";
                else if (shader.name.StartsWith("HDRP/", StringComparison.OrdinalIgnoreCase)
                    || shader.name.StartsWith("High Definition/", StringComparison.OrdinalIgnoreCase))
                    shaderType = "HDRP Built-in";
                else if (shader.name.StartsWith("Hidden/", StringComparison.OrdinalIgnoreCase))
                    shaderType = "Hidden";
                else if (string.IsNullOrEmpty(resolvedPath) || resolvedPath.StartsWith("Packages/"))
                    shaderType = "Built-in/Package";

                var info = new JObject
                {
                    ["name"] = shader.name,
                    ["path"] = resolvedPath ?? "(built-in)",
                    ["shaderType"] = shaderType,
                    ["isShaderGraph"] = isShaderGraph,
                    ["propertyCount"] = propCount,
                    ["properties"] = properties,
                    ["passCount"] = shader.passCount,
                    ["isSupported"] = shader.isSupported,
                    ["renderQueue"] = shader.renderQueue
                };

                // Try to get keyword info from a temporary material
                try
                {
                    var tempMat = new Material(shader);
                    var globalKeywords = tempMat.shaderKeywords;
                    info["defaultKeywords"] = new JArray(globalKeywords);
                    UnityEngine.Object.DestroyImmediate(tempMat);
                }
                catch
                {
                    info["defaultKeywords"] = new JArray();
                }

                return ToolResponse.OkWithData(info,
                    $"Shader '{shader.name}' ({shaderType}): {propCount} properties, {shader.passCount} passes" +
                    (isShaderGraph ? " [Shader Graph]" : ""));
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Get shader info failed: {ex.Message}");
            }
        }

        #endregion
    }
}
