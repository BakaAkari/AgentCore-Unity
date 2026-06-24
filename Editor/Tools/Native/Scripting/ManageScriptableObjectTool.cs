using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Tools.Safety;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Tools.Native.Scripting
{
    /// <summary>
    /// Manage ScriptableObject assets: create, inspect, modify, search, export/import JSON.
    /// Uses SerializedObject/SerializedProperty for robust property access.
    /// </summary>
    [AgentTool("manage_scriptable_object",
        Description = "Manage ScriptableObject assets — create, get/set properties, find, duplicate, delete, export/import JSON. " +
                      "Uses SerializedObject for reliable property access.",
        Category = "Scripting",
        RequiresMainThread = true,
        RiskLevel = ToolRiskLevel.Medium,
        Capabilities = ToolCapability.ModifyAssets | ToolCapability.DeleteProjectFiles)]
    public class ManageScriptableObjectTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""create"", ""get"", ""set"", ""list_types"", ""duplicate"", ""delete"", ""find"", ""export_json"", ""import_json"", ""set_batch""],
                    ""description"": ""Action to perform on ScriptableObject assets""
                },
                ""type_name"": { ""type"": ""string"", ""description"": ""ScriptableObject type name (simple or fully qualified)"" },
                ""asset_path"": { ""type"": ""string"", ""description"": ""Asset path (e.g. Assets/Data/MyConfig.asset)"" },
                ""property_name"": { ""type"": ""string"", ""description"": ""Property name to get/set (SerializedProperty path)"" },
                ""value"": { ""description"": ""Value to set (type auto-detected from property)"" },
                ""source_path"": { ""type"": ""string"", ""description"": ""Source asset path for duplicate"" },
                ""dest_path"": { ""type"": ""string"", ""description"": ""Destination asset path for duplicate"" },
                ""search_folder"": { ""type"": ""string"", ""description"": ""Folder to search in (default: Assets)"" },
                ""name_filter"": { ""type"": ""string"", ""description"": ""Name filter for find action"" },
                ""filter"": { ""type"": ""string"", ""description"": ""Type name filter for list_types"" },
                ""json"": { ""type"": ""string"", ""description"": ""JSON string for import_json"" },
                ""properties"": { ""type"": ""object"", ""description"": ""Key-value pairs for set_batch (propertyName: value)"" },
                ""limit"": { ""type"": ""integer"", ""description"": ""Max results for list/find (default: 50)"" }
            },
            ""required"": [""action""]
        }");

        /// <summary>
        /// Tool metadata for registration and LLM discovery.
        /// </summary>
        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_scriptable_object",
            description: "Manage ScriptableObject assets — create, get/set properties, find, duplicate, delete, export/import JSON. " +
                         "Uses SerializedObject for reliable property access.",
            category: "Scripting",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        /// <summary>
        /// Execute the requested ScriptableObject action.
        /// </summary>
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
                    case "get":
                        response = HandleGet(parameters);
                        break;
                    case "set":
                        response = HandleSet(parameters);
                        break;
                    case "list_types":
                        response = HandleListTypes(parameters);
                        break;
                    case "duplicate":
                        response = HandleDuplicate(parameters);
                        break;
                    case "delete":
                        response = HandleDelete(parameters);
                        break;
                    case "find":
                        response = HandleFind(parameters);
                        break;
                    case "export_json":
                        response = HandleExportJson(parameters);
                        break;
                    case "import_json":
                        response = HandleImportJson(parameters);
                        break;
                    case "set_batch":
                        response = HandleSetBatch(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: create, get, set, list_types, duplicate, delete, find, export_json, import_json, set_batch");
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

        /// <summary>
        /// Create a new ScriptableObject asset.
        /// </summary>
        private ToolResponse HandleCreate(JObject parameters)
        {
            var typeName = ToolHelpers.GetRequiredString(parameters, "type_name");
            var assetPath = ToolHelpers.GetRequiredString(parameters, "asset_path");

            var type = FindScriptableObjectType(typeName);
            if (type == null)
                return ToolResponse.Fail($"ScriptableObject type not found: '{typeName}'. Use list_types to see available types.");

            var instance = ScriptableObject.CreateInstance(type);
            if (instance == null)
                return ToolResponse.Fail($"Failed to create instance of: {typeName}");

            // Ensure directory exists
            var dir = Path.GetDirectoryName(assetPath);
            if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
            {
                CreateFolderRecursive(dir);
            }

            AssetDatabase.CreateAsset(instance, assetPath);
            AssetDatabase.SaveAssets();

            return ToolResponse.OkWithData(new
            {
                typeName = type.FullName,
                path = assetPath
            }, $"Created ScriptableObject '{type.Name}' at {assetPath}");
        }

        /// <summary>
        /// Get all serialized properties of a ScriptableObject.
        /// </summary>
        private ToolResponse HandleGet(JObject parameters)
        {
            var assetPath = ToolHelpers.GetRequiredString(parameters, "asset_path");

            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);
            if (asset == null)
                return ToolResponse.Fail($"ScriptableObject not found at: {assetPath}");

            var so = new SerializedObject(asset);
            var properties = new List<object>();
            var iterator = so.GetIterator();
            bool enterChildren = true;

            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                // Skip m_Script field
                if (iterator.name == "m_Script") continue;

                properties.Add(new
                {
                    name = iterator.name,
                    displayName = iterator.displayName,
                    type = iterator.propertyType.ToString(),
                    value = GetSerializedPropertyValue(iterator),
                    path = iterator.propertyPath,
                    depth = iterator.depth
                });
            }

            return ToolResponse.OkWithData(new
            {
                path = assetPath,
                typeName = asset.GetType().FullName,
                propertyCount = properties.Count,
                properties
            }, $"Retrieved {properties.Count} properties from {assetPath}");
        }

        /// <summary>
        /// Set a single serialized property on a ScriptableObject.
        /// </summary>
        private ToolResponse HandleSet(JObject parameters)
        {
            var assetPath = ToolHelpers.GetRequiredString(parameters, "asset_path");
            var propertyName = ToolHelpers.GetRequiredString(parameters, "property_name");
            var valueToken = parameters["value"];

            if (valueToken == null)
                return ToolResponse.Fail("Required parameter 'value' is missing.");

            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);
            if (asset == null)
                return ToolResponse.Fail($"ScriptableObject not found at: {assetPath}");

            var so = new SerializedObject(asset);
            var prop = so.FindProperty(propertyName);
            if (prop == null)
                return ToolResponse.Fail($"Property '{propertyName}' not found on {asset.GetType().Name}. Use 'get' action to list available properties.");

            Undo.RecordObject(asset, $"Set {propertyName}");

            if (!SetSerializedPropertyValue(prop, valueToken))
                return ToolResponse.Fail($"Failed to set property '{propertyName}' — unsupported type: {prop.propertyType}");

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();

            return ToolResponse.OkWithData(new
            {
                path = assetPath,
                property = propertyName,
                newValue = GetSerializedPropertyValue(so.FindProperty(propertyName))
            }, $"Set '{propertyName}' on {assetPath}");
        }

        /// <summary>
        /// List all available ScriptableObject types in loaded assemblies.
        /// </summary>
        private ToolResponse HandleListTypes(JObject parameters)
        {
            var filter = ToolHelpers.GetOptionalString(parameters, "filter");
            var limit = ToolHelpers.GetOptionalInt(parameters, "limit", 50);

            var types = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a =>
                {
                    try { return a.GetTypes(); }
                    catch { return Array.Empty<Type>(); }
                })
                .Where(t => t.IsSubclassOf(typeof(ScriptableObject)) && !t.IsAbstract && !t.IsGenericType)
                .Where(t => string.IsNullOrEmpty(filter) ||
                            t.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            (t.FullName != null && t.FullName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(t => t.Name)
                .Take(limit)
                .Select(t => new { name = t.Name, fullName = t.FullName, assembly = t.Assembly.GetName().Name })
                .ToArray();

            return ToolResponse.OkWithData(new
            {
                count = types.Length,
                types
            }, $"Found {types.Length} ScriptableObject types" + (string.IsNullOrEmpty(filter) ? "" : $" matching '{filter}'"));
        }

        /// <summary>
        /// Duplicate a ScriptableObject asset.
        /// </summary>
        private ToolResponse HandleDuplicate(JObject parameters)
        {
            var sourcePath = ToolHelpers.GetRequiredString(parameters, "source_path");
            var destPath = ToolHelpers.GetOptionalString(parameters, "dest_path");

            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(sourcePath);
            if (asset == null)
                return ToolResponse.Fail($"ScriptableObject not found at: {sourcePath}");

            if (string.IsNullOrEmpty(destPath))
                destPath = AssetDatabase.GenerateUniqueAssetPath(sourcePath);

            // Ensure destination directory exists
            var dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
            {
                CreateFolderRecursive(dir);
            }

            if (!AssetDatabase.CopyAsset(sourcePath, destPath))
                return ToolResponse.Fail($"Failed to copy asset from '{sourcePath}' to '{destPath}'");

            AssetDatabase.Refresh();

            return ToolResponse.OkWithData(new
            {
                original = sourcePath,
                copy = destPath
            }, $"Duplicated to {destPath}");
        }

        /// <summary>
        /// Delete a ScriptableObject asset.
        /// </summary>
        private ToolResponse HandleDelete(JObject parameters)
        {
            var assetPath = ToolHelpers.GetRequiredString(parameters, "asset_path");

            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);
            if (asset == null)
                return ToolResponse.Fail($"ScriptableObject not found at: {assetPath}");

            if (!AssetDatabase.DeleteAsset(assetPath))
                return ToolResponse.Fail($"Failed to delete asset at: {assetPath}");

            return ToolResponse.Ok($"Deleted ScriptableObject at {assetPath}");
        }

        /// <summary>
        /// Find ScriptableObject assets by type, folder, and name filter.
        /// </summary>
        private ToolResponse HandleFind(JObject parameters)
        {
            var typeName = ToolHelpers.GetOptionalString(parameters, "type_name", "ScriptableObject");
            var searchFolder = ToolHelpers.GetOptionalString(parameters, "search_folder", "Assets");
            var nameFilter = ToolHelpers.GetOptionalString(parameters, "name_filter");
            var limit = ToolHelpers.GetOptionalInt(parameters, "limit", 50);

            var searchQuery = $"t:{typeName}";
            if (!string.IsNullOrEmpty(nameFilter))
                searchQuery += $" {nameFilter}";

            var guids = AssetDatabase.FindAssets(searchQuery, new[] { searchFolder });
            var results = guids
                .Take(limit)
                .Select(g =>
                {
                    var path = AssetDatabase.GUIDToAssetPath(g);
                    var obj = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
                    return new
                    {
                        path,
                        name = Path.GetFileNameWithoutExtension(path),
                        typeName = obj != null ? obj.GetType().Name : "Unknown",
                        guid = g
                    };
                })
                .ToArray();

            return ToolResponse.OkWithData(new
            {
                count = results.Length,
                totalFound = guids.Length,
                assets = results
            }, $"Found {results.Length} ScriptableObject assets" + (guids.Length > limit ? $" (showing {limit} of {guids.Length})" : ""));
        }

        /// <summary>
        /// Export a ScriptableObject to JSON using EditorJsonUtility.
        /// </summary>
        private ToolResponse HandleExportJson(JObject parameters)
        {
            var assetPath = ToolHelpers.GetRequiredString(parameters, "asset_path");

            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);
            if (asset == null)
                return ToolResponse.Fail($"ScriptableObject not found at: {assetPath}");

            var json = EditorJsonUtility.ToJson(asset, true);

            return ToolResponse.OkWithData(new
            {
                path = assetPath,
                typeName = asset.GetType().FullName,
                json
            }, $"Exported {assetPath} to JSON");
        }

        /// <summary>
        /// Import JSON data into an existing ScriptableObject.
        /// </summary>
        private ToolResponse HandleImportJson(JObject parameters)
        {
            var assetPath = ToolHelpers.GetRequiredString(parameters, "asset_path");
            var json = ToolHelpers.GetRequiredString(parameters, "json");

            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);
            if (asset == null)
                return ToolResponse.Fail($"ScriptableObject not found at: {assetPath}");

            Undo.RecordObject(asset, "Import JSON to ScriptableObject");
            EditorJsonUtility.FromJsonOverwrite(json, asset);
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();

            return ToolResponse.OkWithData(new
            {
                path = assetPath,
                typeName = asset.GetType().FullName
            }, $"Imported JSON into {assetPath}");
        }

        /// <summary>
        /// Batch set multiple properties on a ScriptableObject.
        /// </summary>
        private ToolResponse HandleSetBatch(JObject parameters)
        {
            var assetPath = ToolHelpers.GetRequiredString(parameters, "asset_path");
            var propsObj = ToolHelpers.GetOptionalObject(parameters, "properties");

            if (propsObj == null || !propsObj.HasValues)
                return ToolResponse.Fail("Required parameter 'properties' is missing or empty. Provide a JSON object of {propertyName: value} pairs.");

            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);
            if (asset == null)
                return ToolResponse.Fail($"ScriptableObject not found at: {assetPath}");

            var so = new SerializedObject(asset);
            Undo.RecordObject(asset, "Set ScriptableObject Batch");

            int setCount = 0;
            var errors = new List<string>();

            foreach (var kvp in propsObj)
            {
                var prop = so.FindProperty(kvp.Key);
                if (prop == null)
                {
                    errors.Add($"Property '{kvp.Key}' not found");
                    continue;
                }

                if (SetSerializedPropertyValue(prop, kvp.Value))
                    setCount++;
                else
                    errors.Add($"Failed to set '{kvp.Key}' (unsupported type: {prop.propertyType})");
            }

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();

            var data = new JObject
            {
                ["path"] = assetPath,
                ["propertiesSet"] = setCount,
                ["totalRequested"] = propsObj.Count
            };
            if (errors.Count > 0)
                data["errors"] = JArray.FromObject(errors);

            return ToolResponse.OkWithData(data, $"Set {setCount}/{propsObj.Count} properties on {assetPath}");
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Find a ScriptableObject type by name across all loaded assemblies.
        /// </summary>
        private static Type FindScriptableObjectType(string name)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a =>
                {
                    try { return a.GetTypes(); }
                    catch { return Array.Empty<Type>(); }
                })
                .FirstOrDefault(t =>
                    t.IsSubclassOf(typeof(ScriptableObject)) &&
                    !t.IsAbstract &&
                    (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(t.FullName, name, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>
        /// Recursively create folders in the AssetDatabase.
        /// </summary>
        private static void CreateFolderRecursive(string folderPath)
        {
            if (AssetDatabase.IsValidFolder(folderPath)) return;

            var parent = Path.GetDirectoryName(folderPath);
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
                CreateFolderRecursive(parent);

            var folderName = Path.GetFileName(folderPath);
            if (!string.IsNullOrEmpty(parent) && !string.IsNullOrEmpty(folderName))
                AssetDatabase.CreateFolder(parent, folderName);
        }

        /// <summary>
        /// Read the value of a SerializedProperty as a boxed object for JSON output.
        /// </summary>
        private static object GetSerializedPropertyValue(SerializedProperty prop)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    return prop.intValue;
                case SerializedPropertyType.Boolean:
                    return prop.boolValue;
                case SerializedPropertyType.Float:
                    return Math.Round(prop.floatValue, 6);
                case SerializedPropertyType.String:
                    return prop.stringValue;
                case SerializedPropertyType.Color:
                    var c = prop.colorValue;
                    return new { r = Math.Round(c.r, 4), g = Math.Round(c.g, 4), b = Math.Round(c.b, 4), a = Math.Round(c.a, 4) };
                case SerializedPropertyType.ObjectReference:
                    var obj = prop.objectReferenceValue;
                    return obj != null ? new { name = obj.name, type = obj.GetType().Name, instanceId = obj.GetInstanceID() } : (object)null;
                case SerializedPropertyType.Enum:
                    return prop.enumDisplayNames != null && prop.enumValueIndex >= 0 && prop.enumValueIndex < prop.enumDisplayNames.Length
                        ? prop.enumDisplayNames[prop.enumValueIndex]
                        : prop.enumValueIndex.ToString();
                case SerializedPropertyType.Vector2:
                    var v2 = prop.vector2Value;
                    return new { x = Math.Round(v2.x, 4), y = Math.Round(v2.y, 4) };
                case SerializedPropertyType.Vector3:
                    var v3 = prop.vector3Value;
                    return new { x = Math.Round(v3.x, 4), y = Math.Round(v3.y, 4), z = Math.Round(v3.z, 4) };
                case SerializedPropertyType.Vector4:
                    var v4 = prop.vector4Value;
                    return new { x = Math.Round(v4.x, 4), y = Math.Round(v4.y, 4), z = Math.Round(v4.z, 4), w = Math.Round(v4.w, 4) };
                case SerializedPropertyType.Rect:
                    var r = prop.rectValue;
                    return new { x = r.x, y = r.y, width = r.width, height = r.height };
                case SerializedPropertyType.Bounds:
                    var b = prop.boundsValue;
                    return new { center = new { x = b.center.x, y = b.center.y, z = b.center.z }, size = new { x = b.size.x, y = b.size.y, z = b.size.z } };
                case SerializedPropertyType.AnimationCurve:
                    return $"AnimationCurve ({prop.animationCurveValue?.length ?? 0} keys)";
                case SerializedPropertyType.LayerMask:
                    return prop.intValue;
                default:
                    return $"({prop.propertyType})";
            }
        }

        /// <summary>
        /// Set the value of a SerializedProperty from a JToken.
        /// </summary>
        private static bool SetSerializedPropertyValue(SerializedProperty prop, JToken value)
        {
            try
            {
                switch (prop.propertyType)
                {
                    case SerializedPropertyType.Integer:
                        prop.intValue = value.Value<int>();
                        return true;
                    case SerializedPropertyType.Boolean:
                        prop.boolValue = value.Value<bool>();
                        return true;
                    case SerializedPropertyType.Float:
                        prop.floatValue = value.Value<float>();
                        return true;
                    case SerializedPropertyType.String:
                        prop.stringValue = value.ToString();
                        return true;
                    case SerializedPropertyType.Color:
                        if (value is JObject colorObj)
                        {
                            prop.colorValue = new Color(
                                colorObj["r"]?.Value<float>() ?? 0f,
                                colorObj["g"]?.Value<float>() ?? 0f,
                                colorObj["b"]?.Value<float>() ?? 0f,
                                colorObj["a"]?.Value<float>() ?? 1f);
                            return true;
                        }
                        return false;
                    case SerializedPropertyType.Enum:
                        if (value.Type == JTokenType.Integer)
                        {
                            prop.enumValueIndex = value.Value<int>();
                            return true;
                        }
                        if (value.Type == JTokenType.String)
                        {
                            var enumStr = value.ToString();
                            var names = prop.enumDisplayNames;
                            for (int i = 0; i < names.Length; i++)
                            {
                                if (string.Equals(names[i], enumStr, StringComparison.OrdinalIgnoreCase))
                                {
                                    prop.enumValueIndex = i;
                                    return true;
                                }
                            }
                            // Try enum names (internal)
                            var enumNames = prop.enumNames;
                            for (int i = 0; i < enumNames.Length; i++)
                            {
                                if (string.Equals(enumNames[i], enumStr, StringComparison.OrdinalIgnoreCase))
                                {
                                    prop.enumValueIndex = i;
                                    return true;
                                }
                            }
                            return false;
                        }
                        return false;
                    case SerializedPropertyType.Vector2:
                        if (value is JObject v2Obj)
                        {
                            prop.vector2Value = new Vector2(
                                v2Obj["x"]?.Value<float>() ?? 0f,
                                v2Obj["y"]?.Value<float>() ?? 0f);
                            return true;
                        }
                        return false;
                    case SerializedPropertyType.Vector3:
                        if (value is JObject v3Obj)
                        {
                            prop.vector3Value = new Vector3(
                                v3Obj["x"]?.Value<float>() ?? 0f,
                                v3Obj["y"]?.Value<float>() ?? 0f,
                                v3Obj["z"]?.Value<float>() ?? 0f);
                            return true;
                        }
                        return false;
                    case SerializedPropertyType.Vector4:
                        if (value is JObject v4Obj)
                        {
                            prop.vector4Value = new Vector4(
                                v4Obj["x"]?.Value<float>() ?? 0f,
                                v4Obj["y"]?.Value<float>() ?? 0f,
                                v4Obj["z"]?.Value<float>() ?? 0f,
                                v4Obj["w"]?.Value<float>() ?? 0f);
                            return true;
                        }
                        return false;
                    case SerializedPropertyType.Rect:
                        if (value is JObject rectObj)
                        {
                            prop.rectValue = new Rect(
                                rectObj["x"]?.Value<float>() ?? 0f,
                                rectObj["y"]?.Value<float>() ?? 0f,
                                rectObj["width"]?.Value<float>() ?? 0f,
                                rectObj["height"]?.Value<float>() ?? 0f);
                            return true;
                        }
                        return false;
                    case SerializedPropertyType.Bounds:
                        if (value is JObject boundsObj)
                        {
                            var center = boundsObj["center"] as JObject;
                            var size = boundsObj["size"] as JObject;
                            prop.boundsValue = new Bounds(
                                new Vector3(center?["x"]?.Value<float>() ?? 0f, center?["y"]?.Value<float>() ?? 0f, center?["z"]?.Value<float>() ?? 0f),
                                new Vector3(size?["x"]?.Value<float>() ?? 0f, size?["y"]?.Value<float>() ?? 0f, size?["z"]?.Value<float>() ?? 0f));
                            return true;
                        }
                        return false;
                    case SerializedPropertyType.LayerMask:
                        prop.intValue = value.Value<int>();
                        return true;
                    case SerializedPropertyType.ObjectReference:
                        if (value.Type == JTokenType.String)
                        {
                            var refPath = value.ToString();
                            var refObj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(refPath);
                            if (refObj != null)
                            {
                                prop.objectReferenceValue = refObj;
                                return true;
                            }
                            return false;
                        }
                        if (value.Type == JTokenType.Null)
                        {
                            prop.objectReferenceValue = null;
                            return true;
                        }
                        return false;
                    default:
                        return false;
                }
            }
            catch
            {
                return false;
            }
        }

        #endregion
    }
}
