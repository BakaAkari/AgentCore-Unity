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
    /// Inspect and manage shaders - list available shaders, get shader info, find shader properties.
    /// Directly calls Unity ShaderUtil API as part of the native tool system.
    /// </summary>
    [AgentTool("manage_shader",
        Description = "Inspect and manage shaders - list available shaders, get shader info, find shader properties",
        Category = "Shader",
        Visibility = ToolVisibility.OnDemand,
        RequiresMainThread = true)]
    public class ManageShaderTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""list"", ""get_info"", ""find"", ""list_keywords"", ""get_properties"", ""get_keywords"", ""find_shaders"", ""get_shader_info""],
                    ""description"": ""Action to perform""
                },
                ""shaderName"": {
                    ""type"": ""string"",
                    ""description"": ""Shader name (e.g., 'Standard', 'Universal Render Pipeline/Lit')""
                },
                ""shader_name"": {
                    ""type"": ""string"",
                    ""description"": ""Alias for shaderName (snake_case)""
                },
                ""filter"": {
                    ""type"": ""string"",
                    ""description"": ""Filter for list action (partial name match)""
                },
                ""search"": {
                    ""type"": ""string"",
                    ""description"": ""Search term for find_shaders action (partial name match)""
                },
                ""type"": {
                    ""type"": ""string"",
                    ""enum"": [""all"", ""surface"", ""unlit"", ""custom""],
                    ""description"": ""Shader type filter for find_shaders action (default: 'all')""
                },
                ""maxResults"": {
                    ""type"": ""integer"",
                    ""description"": ""Max results for list (default: 50)""
                }
            },
            ""required"": [""action""]
        }");

        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_shader",
            description: "Inspect and manage shaders - list available shaders, get shader info, find shader properties",
            category: "Shader",
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
                    case "list":
                        response = HandleList(parameters);
                        break;
                    case "get_info":
                        response = HandleGetInfo(parameters);
                        break;
                    case "find":
                        response = HandleFind(parameters);
                        break;
                    case "list_keywords":
                    case "get_keywords":
                        response = HandleListKeywords(parameters);
                        break;
                    case "get_properties":
                        response = HandleGetProperties(parameters);
                        break;
                    case "find_shaders":
                        response = HandleFindShaders(parameters);
                        break;
                    case "get_shader_info":
                        response = HandleGetShaderInfo(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: list, get_info, find, list_keywords, get_properties, get_keywords, find_shaders, get_shader_info");
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

        #region Helpers

        /// <summary>
        /// Resolves shader name from parameters, supporting both 'shaderName' and 'shader_name' keys.
        /// </summary>
        private static string GetShaderName(JObject parameters)
        {
            var name = ToolHelpers.GetOptionalString(parameters, "shaderName");
            if (string.IsNullOrEmpty(name))
                name = ToolHelpers.GetOptionalString(parameters, "shader_name");
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("Required parameter 'shader_name' (or 'shaderName') is missing.");
            return name;
        }

        #endregion

        #region Action Handlers

        private ToolResponse HandleList(JObject parameters)
        {
            try
            {
                var filter = ToolHelpers.GetOptionalString(parameters, "filter", "");
                var maxResults = ToolHelpers.GetOptionalInt(parameters, "maxResults", 50);

                // Find all shader assets in the project
                var shaderGuids = AssetDatabase.FindAssets("t:Shader");
                var shaders = new List<JObject>();

                foreach (var guid in shaderGuids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                    if (shader == null) continue;

                    if (!string.IsNullOrEmpty(filter) &&
                        shader.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    shaders.Add(new JObject
                    {
                        ["name"] = shader.name,
                        ["path"] = path,
                        ["propertyCount"] = ShaderUtil.GetPropertyCount(shader),
                        ["isSupported"] = shader.isSupported
                    });

                    if (shaders.Count >= maxResults) break;
                }

                // Also include built-in shaders that match the filter
                var builtInShaderNames = new[]
                {
                    "Standard", "Standard (Specular setup)",
                    "Universal Render Pipeline/Lit", "Universal Render Pipeline/Unlit",
                    "Universal Render Pipeline/Simple Lit", "Universal Render Pipeline/Baked Lit",
                    "Universal Render Pipeline/Particles/Lit", "Universal Render Pipeline/Particles/Unlit",
                    "Universal Render Pipeline/Terrain/Lit",
                    "Unlit/Color", "Unlit/Texture", "Unlit/Transparent",
                    "Sprites/Default", "UI/Default",
                    "Skybox/6 Sided", "Skybox/Procedural",
                    "Particles/Standard Surface", "Particles/Standard Unlit",
                    "Hidden/InternalErrorShader"
                };

                foreach (var name in builtInShaderNames)
                {
                    if (shaders.Count >= maxResults) break;
                    if (!string.IsNullOrEmpty(filter) &&
                        name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    // Skip if already in list
                    if (shaders.Any(s => s["name"]?.ToString() == name))
                        continue;

                    var builtIn = Shader.Find(name);
                    if (builtIn != null)
                    {
                        shaders.Add(new JObject
                        {
                            ["name"] = builtIn.name,
                            ["path"] = "(built-in)",
                            ["propertyCount"] = ShaderUtil.GetPropertyCount(builtIn),
                            ["isSupported"] = builtIn.isSupported
                        });
                    }
                }

                return ToolResponse.OkWithData(new JObject
                {
                    ["shaders"] = new JArray(shaders),
                    ["count"] = shaders.Count
                }, $"Found {shaders.Count} shaders.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"List shaders failed: {ex.Message}");
            }
        }

        private ToolResponse HandleGetInfo(JObject parameters)
        {
            try
            {
                var shaderName = GetShaderName(parameters);
                var shader = Shader.Find(shaderName);
                if (shader == null)
                    return ToolResponse.Fail($"Shader not found: '{shaderName}'");

                var info = new JObject
                {
                    ["name"] = shader.name,
                    ["isSupported"] = shader.isSupported,
                    ["renderQueue"] = shader.renderQueue,
                    ["passCount"] = shader.passCount
                };

                // Properties
                var properties = new JArray();
                int propCount = ShaderUtil.GetPropertyCount(shader);
                for (int i = 0; i < propCount; i++)
                {
                    var propName = ShaderUtil.GetPropertyName(shader, i);
                    var propType = ShaderUtil.GetPropertyType(shader, i);
                    var propDesc = ShaderUtil.GetPropertyDescription(shader, i);

                    var prop = new JObject
                    {
                        ["name"] = propName,
                        ["type"] = propType.ToString(),
                        ["description"] = propDesc
                    };

                    // Add range info for Range type
                    if (propType == ShaderUtil.ShaderPropertyType.Range)
                    {
                        prop["rangeMin"] = ShaderUtil.GetRangeLimits(shader, i, 1);
                        prop["rangeMax"] = ShaderUtil.GetRangeLimits(shader, i, 2);
                        prop["rangeDefault"] = ShaderUtil.GetRangeLimits(shader, i, 0);
                    }

                    // Check if hidden
                    prop["isHidden"] = ShaderUtil.IsShaderPropertyHidden(shader, i);

                    properties.Add(prop);
                }
                info["properties"] = properties;
                info["propertyCount"] = propCount;

                return ToolResponse.OkWithData(info, $"Shader info for '{shaderName}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Get info failed: {ex.Message}");
            }
        }

        private ToolResponse HandleFind(JObject parameters)
        {
            try
            {
                var shaderName = GetShaderName(parameters);
                var shader = Shader.Find(shaderName);

                if (shader == null)
                {
                    // Try partial match in project shaders
                    var guids = AssetDatabase.FindAssets("t:Shader");
                    var matches = new JArray();

                    foreach (var guid in guids)
                    {
                        var path = AssetDatabase.GUIDToAssetPath(guid);
                        var s = AssetDatabase.LoadAssetAtPath<Shader>(path);
                        if (s != null && s.name.IndexOf(shaderName, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            matches.Add(new JObject
                            {
                                ["name"] = s.name,
                                ["path"] = path,
                                ["isSupported"] = s.isSupported
                            });
                        }
                    }

                    if (matches.Count == 0)
                        return ToolResponse.Fail($"No shader found matching: '{shaderName}'");

                    return ToolResponse.OkWithData(new JObject
                    {
                        ["exactMatch"] = false,
                        ["matches"] = matches,
                        ["count"] = matches.Count
                    }, $"No exact match for '{shaderName}', found {matches.Count} partial matches.");
                }

                // Find asset path if it exists
                var assetPath = AssetDatabase.GetAssetPath(shader);

                return ToolResponse.OkWithData(new JObject
                {
                    ["exactMatch"] = true,
                    ["name"] = shader.name,
                    ["path"] = string.IsNullOrEmpty(assetPath) ? "(built-in)" : assetPath,
                    ["isSupported"] = shader.isSupported,
                    ["propertyCount"] = ShaderUtil.GetPropertyCount(shader)
                }, $"Found shader: '{shader.name}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Find shader failed: {ex.Message}");
            }
        }

        private ToolResponse HandleListKeywords(JObject parameters)
        {
            try
            {
                var shaderName = GetShaderName(parameters);
                var shader = Shader.Find(shaderName);
                if (shader == null)
                    return ToolResponse.Fail($"Shader not found: '{shaderName}'");

                // Get global keywords
                var globalKeywords = shader.keywordSpace.keywords;
                var keywordArray = new JArray();

                foreach (var keyword in globalKeywords)
                {
                    keywordArray.Add(new JObject
                    {
                        ["name"] = keyword.name,
                        ["type"] = keyword.type.ToString()
                    });
                }

                return ToolResponse.OkWithData(new JObject
                {
                    ["shaderName"] = shader.name,
                    ["keywords"] = keywordArray,
                    ["keywordCount"] = keywordArray.Count
                }, $"Found {keywordArray.Count} keywords for shader '{shaderName}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"List keywords failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets all properties of a shader by name.
        /// Returns property list with name, type, and description.
        /// </summary>
        private ToolResponse HandleGetProperties(JObject parameters)
        {
            try
            {
                var shaderName = GetShaderName(parameters);
                var shader = Shader.Find(shaderName);
                if (shader == null)
                    return ToolResponse.Fail($"Shader not found: '{shaderName}'");

                var properties = new JArray();
                int propCount = ShaderUtil.GetPropertyCount(shader);
                for (int i = 0; i < propCount; i++)
                {
                    var propName = ShaderUtil.GetPropertyName(shader, i);
                    var propType = ShaderUtil.GetPropertyType(shader, i);
                    var propDesc = ShaderUtil.GetPropertyDescription(shader, i);

                    var prop = new JObject
                    {
                        ["name"] = propName,
                        ["type"] = propType.ToString(),
                        ["description"] = propDesc,
                        ["isHidden"] = ShaderUtil.IsShaderPropertyHidden(shader, i)
                    };

                    // Add range info for Range type
                    if (propType == ShaderUtil.ShaderPropertyType.Range)
                    {
                        prop["rangeMin"] = ShaderUtil.GetRangeLimits(shader, i, 1);
                        prop["rangeMax"] = ShaderUtil.GetRangeLimits(shader, i, 2);
                        prop["rangeDefault"] = ShaderUtil.GetRangeLimits(shader, i, 0);
                    }

                    // Add texture dimension for TexEnv type
                    if (propType == ShaderUtil.ShaderPropertyType.TexEnv)
                    {
                        prop["textureDimension"] = ShaderUtil.GetTexDim(shader, i).ToString();
                    }

                    properties.Add(prop);
                }

                return ToolResponse.OkWithData(new JObject
                {
                    ["shaderName"] = shader.name,
                    ["propertyCount"] = propCount,
                    ["properties"] = properties
                }, $"Found {propCount} properties for shader '{shaderName}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Get properties failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Searches for shaders in the project with optional type filtering.
        /// Supports filtering by shader type: all, surface, unlit, custom.
        /// </summary>
        private ToolResponse HandleFindShaders(JObject parameters)
        {
            try
            {
                var search = ToolHelpers.GetOptionalString(parameters, "search", "");
                var typeFilter = ToolHelpers.GetOptionalString(parameters, "type", "all").ToLowerInvariant();
                var maxResults = ToolHelpers.GetOptionalInt(parameters, "maxResults", 50);

                var shaderGuids = AssetDatabase.FindAssets("t:Shader");
                var results = new JArray();

                foreach (var guid in shaderGuids)
                {
                    if (results.Count >= maxResults) break;

                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                    if (shader == null) continue;

                    // Apply name filter
                    if (!string.IsNullOrEmpty(search) &&
                        shader.name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    // Apply type filter
                    if (!MatchesShaderType(shader, typeFilter))
                        continue;

                    results.Add(new JObject
                    {
                        ["name"] = shader.name,
                        ["path"] = path,
                        ["propertyCount"] = ShaderUtil.GetPropertyCount(shader),
                        ["isSupported"] = shader.isSupported,
                        ["renderQueue"] = shader.renderQueue,
                        ["passCount"] = shader.passCount
                    });
                }

                return ToolResponse.OkWithData(new JObject
                {
                    ["shaders"] = results,
                    ["count"] = results.Count,
                    ["typeFilter"] = typeFilter,
                    ["searchTerm"] = search
                }, $"Found {results.Count} shaders matching criteria.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Find shaders failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets detailed shader information including pass count, render queue, LOD, and subshader count.
        /// </summary>
        private ToolResponse HandleGetShaderInfo(JObject parameters)
        {
            try
            {
                var shaderName = GetShaderName(parameters);
                var shader = Shader.Find(shaderName);
                if (shader == null)
                    return ToolResponse.Fail($"Shader not found: '{shaderName}'");

                var assetPath = AssetDatabase.GetAssetPath(shader);

                var info = new JObject
                {
                    ["name"] = shader.name,
                    ["isSupported"] = shader.isSupported,
                    ["renderQueue"] = shader.renderQueue,
                    ["passCount"] = shader.passCount,
                    ["isBuiltIn"] = string.IsNullOrEmpty(assetPath) || assetPath.StartsWith("Resources/"),
                    ["assetPath"] = string.IsNullOrEmpty(assetPath) ? "(built-in)" : assetPath,
                    ["propertyCount"] = ShaderUtil.GetPropertyCount(shader)
                };

                info["subshaderCount"] = TryGetSubshaderCount(shader) ?? -1;

                // Get shader LOD
                try
                {
                    info["maximumLOD"] = shader.maximumLOD;
                }
                catch
                {
                    info["maximumLOD"] = -1;
                }

                // Keywords
                var keywords = shader.keywordSpace.keywords;
                var keywordList = new JArray();
                foreach (var kw in keywords)
                {
                    keywordList.Add(new JObject
                    {
                        ["name"] = kw.name,
                        ["type"] = kw.type.ToString()
                    });
                }
                info["keywords"] = keywordList;
                info["keywordCount"] = keywordList.Count;

                // Pass names
                var passes = new JArray();
                for (int i = 0; i < shader.passCount; i++)
                {
                    passes.Add(shader.FindPassTagValue(i, new ShaderTagId("LightMode")).name ?? $"Pass {i}");
                }
                info["passLightModes"] = passes;

                return ToolResponse.OkWithData(info, $"Detailed shader info for '{shaderName}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Get shader info failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks if a shader matches the specified type filter.
        /// </summary>
        private static bool MatchesShaderType(Shader shader, string typeFilter)
        {
            if (typeFilter == "all") return true;

            var name = shader.name.ToLowerInvariant();

            switch (typeFilter)
            {
                case "surface":
                    return name.Contains("standard") || name.Contains("/lit") ||
                           name.Contains("surface") || name.Contains("pbr");
                case "unlit":
                    return name.Contains("unlit") || name.Contains("transparent") ||
                           name.Contains("sprite") || name.Contains("ui/");
                case "custom":
                    // Custom shaders are those not in standard Unity paths
                    return !name.StartsWith("hidden/") &&
                           !name.StartsWith("universal render pipeline/") &&
                           !name.StartsWith("particles/") &&
                           !name.StartsWith("skybox/") &&
                           !name.StartsWith("ui/") &&
                           !name.StartsWith("sprites/") &&
                           !name.StartsWith("unlit/") &&
                           name != "standard" &&
                           name != "standard (specular setup)";
                default:
                    return true;
            }
        }

        /// <summary>
        /// Gets the subshader count through reflection because ShaderUtil method names differ between Unity versions.
        /// </summary>
        private static int? TryGetSubshaderCount(Shader shader)
        {
            var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var methodNames = new[] { "GetShaderSubshaderCount", "GetSubshaderCount" };

            foreach (var methodName in methodNames)
            {
                var method = typeof(ShaderUtil).GetMethod(methodName, flags, null, new[] { typeof(Shader) }, null);
                if (method == null) continue;

                try
                {
                    var result = method.Invoke(null, new object[] { shader });
                    if (result != null) return Convert.ToInt32(result);
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        #endregion
    }
}
