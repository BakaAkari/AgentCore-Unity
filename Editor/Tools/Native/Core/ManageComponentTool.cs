using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AgentCore.Editor.Tools.Native.Core
{
    /// <summary>
    /// Add, remove, modify, and inspect components on GameObjects.
    /// Directly calls Unity Editor API as part of the native tool system.
    /// Uses SerializedObject/SerializedProperty for robust property modification.
    /// </summary>
    [AgentTool("manage_component", Description = "Add, remove, modify, and inspect components on GameObjects", Category = "Component", RequiresMainThread = true)]
    public class ManageComponentTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""add"", ""remove"", ""get"", ""list"", ""modify"", ""set_enabled""],
                    ""description"": ""Action to perform""
                },
                ""target"": {
                    ""type"": ""string"",
                    ""description"": ""Target GameObject name or path""
                },
                ""componentType"": {
                    ""type"": ""string"",
                    ""description"": ""Component type name (e.g., 'Rigidbody', 'BoxCollider')""
                },
                ""properties"": {
                    ""type"": ""object"",
                    ""description"": ""Properties to modify (key-value pairs)""
                },
                ""enabled"": {
                    ""type"": ""boolean"",
                    ""description"": ""Enable/disable state for set_enabled""
                }
            },
            ""required"": [""action"", ""target""]
        }");

        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_component",
            description: "Add, remove, modify, and inspect components on GameObjects",
            category: "Component",
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
                var target = ToolHelpers.GetRequiredString(parameters, "target");
                var go = ToolHelpers.FindGameObject(target);

                if (go == null)
                {
                    response = ToolResponse.Fail($"GameObject '{target}' not found.");
                }
                else
                {
                    switch (action)
                    {
                        case "add":
                            response = HandleAdd(go, parameters);
                            break;
                        case "remove":
                            response = HandleRemove(go, parameters);
                            break;
                        case "get":
                            response = HandleGet(go, parameters);
                            break;
                        case "list":
                            response = HandleList(go);
                            break;
                        case "modify":
                            response = HandleModify(go, parameters);
                            break;
                        case "set_enabled":
                            response = HandleSetEnabled(go, parameters);
                            break;
                        default:
                            response = ToolResponse.Fail(
                                $"Unknown action: '{action}'. Valid actions: add, remove, get, list, modify, set_enabled");
                            break;
                    }
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

        private ToolResponse HandleAdd(GameObject go, JObject parameters)
        {
            try
            {
                var componentTypeName = ToolHelpers.GetRequiredString(parameters, "componentType");
                var type = ToolHelpers.ResolveComponentType(componentTypeName);

                if (type == null)
                    return ToolResponse.Fail($"Component type '{componentTypeName}' not found. Use a fully-qualified name if needed.");

                if (!typeof(Component).IsAssignableFrom(type))
                    return ToolResponse.Fail($"Type '{componentTypeName}' is not a Component.");

                // Check if component already exists (for components that don't allow multiples)
                var existing = go.GetComponent(type);
                if (existing != null && !IsMultipleAllowed(type))
                {
                    return ToolResponse.Fail($"Component '{componentTypeName}' already exists on '{go.name}' and does not allow multiples.");
                }

                var component = Undo.AddComponent(go, type);
                if (component == null)
                    return ToolResponse.Fail($"Failed to add component '{componentTypeName}' to '{go.name}'.");

                // Set properties if provided
                var properties = ToolHelpers.GetOptionalObject(parameters, "properties");
                if (properties != null && properties.HasValues)
                {
                    SetPropertiesViaSerializedObject(component, properties);
                }

                EditorUtility.SetDirty(go);
                MarkSceneDirty(go);

                Debug.Log($"[AgentCore] Added component '{componentTypeName}' to '{go.name}'");
                return ToolResponse.OkWithData(new JObject
                {
                    ["gameObject"] = go.name,
                    ["instanceId"] = go.GetInstanceID(),
                    ["componentType"] = type.FullName,
                    ["componentInstanceId"] = component.GetInstanceID()
                }, $"Component '{componentTypeName}' added to '{go.name}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error adding component: {ex.Message}");
            }
        }

        private ToolResponse HandleRemove(GameObject go, JObject parameters)
        {
            try
            {
                var componentTypeName = ToolHelpers.GetRequiredString(parameters, "componentType");
                var type = ToolHelpers.ResolveComponentType(componentTypeName);

                if (type == null)
                    return ToolResponse.Fail($"Component type '{componentTypeName}' not found.");

                var component = go.GetComponent(type);
                if (component == null)
                    return ToolResponse.Fail($"Component '{componentTypeName}' not found on '{go.name}'.");

                // Prevent removing Transform
                if (component is Transform)
                    return ToolResponse.Fail("Cannot remove the Transform component.");

                Undo.DestroyObjectImmediate(component);

                EditorUtility.SetDirty(go);
                MarkSceneDirty(go);

                Debug.Log($"[AgentCore] Removed component '{componentTypeName}' from '{go.name}'");
                return ToolResponse.OkWithData(new JObject
                {
                    ["gameObject"] = go.name,
                    ["instanceId"] = go.GetInstanceID(),
                    ["removedType"] = componentTypeName
                }, $"Component '{componentTypeName}' removed from '{go.name}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error removing component: {ex.Message}");
            }
        }

        private ToolResponse HandleGet(GameObject go, JObject parameters)
        {
            try
            {
                var componentTypeName = ToolHelpers.GetRequiredString(parameters, "componentType");
                var type = ToolHelpers.ResolveComponentType(componentTypeName);

                if (type == null)
                    return ToolResponse.Fail($"Component type '{componentTypeName}' not found.");

                var component = go.GetComponent(type);
                if (component == null)
                    return ToolResponse.Fail($"Component '{componentTypeName}' not found on '{go.name}'.");

                // Serialize component with all serialized properties
                var data = SerializeComponentDetailed(component);
                data["gameObject"] = go.name;
                data["gameObjectInstanceId"] = go.GetInstanceID();

                return ToolResponse.OkWithData(data, $"Component '{componentTypeName}' on '{go.name}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error getting component: {ex.Message}");
            }
        }

        private ToolResponse HandleList(GameObject go)
        {
            try
            {
                var components = go.GetComponents<Component>();
                var compArray = new JArray();

                foreach (var comp in components)
                {
                    if (comp == null)
                    {
                        compArray.Add(new JObject
                        {
                            ["type"] = "(Missing Script)",
                            ["isMissing"] = true
                        });
                        continue;
                    }

                    var compInfo = ToolHelpers.SerializeComponent(comp);

                    // Add enabled state for Behaviour components
                    if (comp is Behaviour behaviour)
                    {
                        compInfo["enabled"] = behaviour.enabled;
                    }
                    else if (comp is Renderer renderer)
                    {
                        compInfo["enabled"] = renderer.enabled;
                    }
                    else if (comp is Collider collider)
                    {
                        compInfo["enabled"] = collider.enabled;
                    }

                    compArray.Add(compInfo);
                }

                return ToolResponse.OkWithData(new JObject
                {
                    ["gameObject"] = go.name,
                    ["instanceId"] = go.GetInstanceID(),
                    ["componentCount"] = compArray.Count,
                    ["components"] = compArray
                }, $"Found {compArray.Count} components on '{go.name}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error listing components: {ex.Message}");
            }
        }

        private ToolResponse HandleModify(GameObject go, JObject parameters)
        {
            try
            {
                var componentTypeName = ToolHelpers.GetRequiredString(parameters, "componentType");
                var type = ToolHelpers.ResolveComponentType(componentTypeName);

                if (type == null)
                    return ToolResponse.Fail($"Component type '{componentTypeName}' not found.");

                var component = go.GetComponent(type);
                if (component == null)
                    return ToolResponse.Fail($"Component '{componentTypeName}' not found on '{go.name}'.");

                var properties = ToolHelpers.GetOptionalObject(parameters, "properties");
                if (properties == null || !properties.HasValues)
                    return ToolResponse.Fail("'properties' parameter is required for 'modify' action.");

                ToolHelpers.RecordUndo(component, $"Modify {componentTypeName}");

                var errors = SetPropertiesViaSerializedObject(component, properties);

                EditorUtility.SetDirty(component);
                MarkSceneDirty(go);

                if (errors.Count > 0)
                {
                    Debug.LogWarning($"[AgentCore] Some properties failed on '{componentTypeName}': {string.Join(", ", errors)}");
                    return ToolResponse.OkWithData(new JObject
                    {
                        ["gameObject"] = go.name,
                        ["componentType"] = componentTypeName,
                        ["errors"] = new JArray(errors.ToArray()),
                        ["partialSuccess"] = true
                    }, $"Modified '{componentTypeName}' on '{go.name}' with {errors.Count} error(s).");
                }

                Debug.Log($"[AgentCore] Modified component '{componentTypeName}' on '{go.name}'");
                return ToolResponse.OkWithData(new JObject
                {
                    ["gameObject"] = go.name,
                    ["instanceId"] = go.GetInstanceID(),
                    ["componentType"] = componentTypeName
                }, $"Component '{componentTypeName}' on '{go.name}' modified successfully.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error modifying component: {ex.Message}");
            }
        }

        private ToolResponse HandleSetEnabled(GameObject go, JObject parameters)
        {
            try
            {
                var componentTypeName = ToolHelpers.GetRequiredString(parameters, "componentType");
                var type = ToolHelpers.ResolveComponentType(componentTypeName);

                if (type == null)
                    return ToolResponse.Fail($"Component type '{componentTypeName}' not found.");

                var component = go.GetComponent(type);
                if (component == null)
                    return ToolResponse.Fail($"Component '{componentTypeName}' not found on '{go.name}'.");

                var enabledToken = parameters["enabled"];
                if (enabledToken == null)
                    return ToolResponse.Fail("'enabled' parameter is required for 'set_enabled' action.");

                bool enabled = enabledToken.Value<bool>();

                ToolHelpers.RecordUndo(component, $"Set Enabled on {componentTypeName}");

                bool success = false;
                if (component is Behaviour behaviour)
                {
                    behaviour.enabled = enabled;
                    success = true;
                }
                else if (component is Renderer renderer)
                {
                    renderer.enabled = enabled;
                    success = true;
                }
                else if (component is Collider collider)
                {
                    collider.enabled = enabled;
                    success = true;
                }
                else
                {
                    return ToolResponse.Fail($"Component '{componentTypeName}' does not support enable/disable.");
                }

                if (success)
                {
                    EditorUtility.SetDirty(component);
                    MarkSceneDirty(go);

                    Debug.Log($"[AgentCore] Set '{componentTypeName}' enabled={enabled} on '{go.name}'");
                    return ToolResponse.OkWithData(new JObject
                    {
                        ["gameObject"] = go.name,
                        ["componentType"] = componentTypeName,
                        ["enabled"] = enabled
                    }, $"Component '{componentTypeName}' on '{go.name}' {(enabled ? "enabled" : "disabled")}.");
                }

                return ToolResponse.Fail($"Failed to set enabled state on '{componentTypeName}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error setting enabled state: {ex.Message}");
            }
        }

        #endregion

        #region Property Modification via SerializedObject

        /// <summary>
        /// Sets properties on a component using SerializedObject/SerializedProperty.
        /// This approach supports any serialized property on any component.
        /// Returns a list of error messages for properties that failed to set.
        /// </summary>
        private List<string> SetPropertiesViaSerializedObject(Component component, JObject properties)
        {
            var errors = new List<string>();
            var serializedObject = new SerializedObject(component);

            foreach (var prop in properties.Properties())
            {
                try
                {
                    var serializedProp = serializedObject.FindProperty(prop.Name);
                    if (serializedProp != null)
                    {
                        if (SetSerializedPropertyValue(serializedProp, prop.Value))
                        {
                            continue; // Success
                        }
                        else
                        {
                            errors.Add($"Failed to set '{prop.Name}': unsupported property type '{serializedProp.propertyType}'.");
                        }
                    }
                    else
                    {
                        // Fallback: try reflection-based property setting
                        if (!TrySetPropertyViaReflection(component, prop.Name, prop.Value))
                        {
                            errors.Add($"Property '{prop.Name}' not found on '{component.GetType().Name}'.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Error setting '{prop.Name}': {ex.Message}");
                }
            }

            serializedObject.ApplyModifiedProperties();
            return errors;
        }

        /// <summary>
        /// Sets a SerializedProperty value from a JToken.
        /// Returns true if the value was set successfully.
        /// </summary>
        private bool SetSerializedPropertyValue(SerializedProperty prop, JToken value)
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
                    prop.stringValue = value.Value<string>();
                    return true;

                case SerializedPropertyType.Color:
                    var color = ToolHelpers.ParseColor(value, Color.white);
                    prop.colorValue = color;
                    return true;

                case SerializedPropertyType.Vector2:
                    if (value is JObject v2Obj)
                    {
                        prop.vector2Value = new Vector2(
                            v2Obj["x"]?.Value<float>() ?? 0f,
                            v2Obj["y"]?.Value<float>() ?? 0f
                        );
                        return true;
                    }
                    return false;

                case SerializedPropertyType.Vector3:
                    var v3 = ToolHelpers.ParseVector3(value);
                    prop.vector3Value = v3;
                    return true;

                case SerializedPropertyType.Vector4:
                    if (value is JObject v4Obj)
                    {
                        prop.vector4Value = new Vector4(
                            v4Obj["x"]?.Value<float>() ?? 0f,
                            v4Obj["y"]?.Value<float>() ?? 0f,
                            v4Obj["z"]?.Value<float>() ?? 0f,
                            v4Obj["w"]?.Value<float>() ?? 0f
                        );
                        return true;
                    }
                    return false;

                case SerializedPropertyType.Enum:
                    if (value.Type == JTokenType.String)
                    {
                        var enumNames = prop.enumNames;
                        var enumStr = value.Value<string>();
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
                    else if (value.Type == JTokenType.Integer)
                    {
                        prop.enumValueIndex = value.Value<int>();
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
                            rectObj["height"]?.Value<float>() ?? 0f
                        );
                        return true;
                    }
                    return false;

                case SerializedPropertyType.Bounds:
                    if (value is JObject boundsObj)
                    {
                        var center = ToolHelpers.ParseVector3(boundsObj["center"]);
                        var size = ToolHelpers.ParseVector3(boundsObj["size"]);
                        prop.boundsValue = new Bounds(center, size);
                        return true;
                    }
                    return false;

                case SerializedPropertyType.LayerMask:
                    if (value.Type == JTokenType.Integer)
                    {
                        prop.intValue = value.Value<int>();
                        return true;
                    }
                    else if (value.Type == JTokenType.String)
                    {
                        int layer = LayerMask.NameToLayer(value.Value<string>());
                        if (layer >= 0)
                        {
                            prop.intValue = 1 << layer;
                            return true;
                        }
                    }
                    return false;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Fallback: try to set a property via reflection when SerializedProperty is not available.
        /// </summary>
        private bool TrySetPropertyViaReflection(Component component, string propertyName, JToken value)
        {
            var type = component.GetType();

            // Try property first
            var propInfo = type.GetProperty(propertyName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (propInfo != null && propInfo.CanWrite)
            {
                try
                {
                    var convertedValue = ConvertJTokenToType(value, propInfo.PropertyType);
                    if (convertedValue != null || !propInfo.PropertyType.IsValueType)
                    {
                        propInfo.SetValue(component, convertedValue);
                        return true;
                    }
                }
                catch
                {
                    // Fall through to field
                }
            }

            // Try field
            var fieldInfo = type.GetField(propertyName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (fieldInfo != null)
            {
                try
                {
                    var convertedValue = ConvertJTokenToType(value, fieldInfo.FieldType);
                    if (convertedValue != null || !fieldInfo.FieldType.IsValueType)
                    {
                        fieldInfo.SetValue(component, convertedValue);
                        return true;
                    }
                }
                catch
                {
                    // Failed
                }
            }

            return false;
        }

        /// <summary>
        /// Converts a JToken to the specified target type.
        /// </summary>
        private object ConvertJTokenToType(JToken token, Type targetType)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;

            if (targetType == typeof(int))
                return token.Value<int>();
            if (targetType == typeof(float))
                return token.Value<float>();
            if (targetType == typeof(double))
                return token.Value<double>();
            if (targetType == typeof(bool))
                return token.Value<bool>();
            if (targetType == typeof(string))
                return token.Value<string>();
            if (targetType == typeof(Vector3))
                return ToolHelpers.ParseVector3(token);
            if (targetType == typeof(Vector2))
            {
                if (token is JObject obj)
                    return new Vector2(
                        obj["x"]?.Value<float>() ?? 0f,
                        obj["y"]?.Value<float>() ?? 0f);
            }
            if (targetType == typeof(Color))
                return ToolHelpers.ParseColor(token);
            if (targetType.IsEnum)
            {
                var str = token.Value<string>();
                if (!string.IsNullOrEmpty(str) && Enum.TryParse(targetType, str, true, out var enumVal))
                    return enumVal;
            }

            // Generic fallback
            try
            {
                return token.ToObject(targetType);
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Serializes a component with all its serialized properties for detailed inspection.
        /// </summary>
        private JObject SerializeComponentDetailed(Component component)
        {
            var result = ToolHelpers.SerializeComponent(component);

            // Add all serialized properties
            var serializedObject = new SerializedObject(component);
            var propsObj = new JObject();
            var iterator = serializedObject.GetIterator();

            if (iterator.NextVisible(true))
            {
                do
                {
                    // Skip internal Unity properties
                    if (iterator.name == "m_Script" || iterator.name == "m_ObjectHideFlags")
                        continue;

                    var propValue = GetSerializedPropertyValue(iterator);
                    if (propValue != null)
                    {
                        propsObj[iterator.name] = propValue;
                    }
                } while (iterator.NextVisible(false));
            }

            result["serializedProperties"] = propsObj;
            return result;
        }

        /// <summary>
        /// Gets the value of a SerializedProperty as a JToken.
        /// </summary>
        private JToken GetSerializedPropertyValue(SerializedProperty prop)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    return prop.intValue;
                case SerializedPropertyType.Boolean:
                    return prop.boolValue;
                case SerializedPropertyType.Float:
                    return Math.Round(prop.floatValue, 4);
                case SerializedPropertyType.String:
                    return prop.stringValue;
                case SerializedPropertyType.Color:
                    return $"#{ColorUtility.ToHtmlStringRGBA(prop.colorValue)}";
                case SerializedPropertyType.Vector2:
                    return new JObject
                    {
                        ["x"] = Math.Round(prop.vector2Value.x, 4),
                        ["y"] = Math.Round(prop.vector2Value.y, 4)
                    };
                case SerializedPropertyType.Vector3:
                    return ToolHelpers.Vector3ToJson(prop.vector3Value);
                case SerializedPropertyType.Vector4:
                    return new JObject
                    {
                        ["x"] = Math.Round(prop.vector4Value.x, 4),
                        ["y"] = Math.Round(prop.vector4Value.y, 4),
                        ["z"] = Math.Round(prop.vector4Value.z, 4),
                        ["w"] = Math.Round(prop.vector4Value.w, 4)
                    };
                case SerializedPropertyType.Enum:
                    return prop.enumNames.Length > prop.enumValueIndex && prop.enumValueIndex >= 0
                        ? prop.enumNames[prop.enumValueIndex]
                        : prop.enumValueIndex.ToString();
                case SerializedPropertyType.Rect:
                    var rect = prop.rectValue;
                    return new JObject
                    {
                        ["x"] = Math.Round(rect.x, 4),
                        ["y"] = Math.Round(rect.y, 4),
                        ["width"] = Math.Round(rect.width, 4),
                        ["height"] = Math.Round(rect.height, 4)
                    };
                case SerializedPropertyType.Bounds:
                    var bounds = prop.boundsValue;
                    return new JObject
                    {
                        ["center"] = ToolHelpers.Vector3ToJson(bounds.center),
                        ["size"] = ToolHelpers.Vector3ToJson(bounds.size)
                    };
                case SerializedPropertyType.LayerMask:
                    return prop.intValue;
                case SerializedPropertyType.ObjectReference:
                    if (prop.objectReferenceValue != null)
                    {
                        return new JObject
                        {
                            ["name"] = prop.objectReferenceValue.name,
                            ["type"] = prop.objectReferenceValue.GetType().Name,
                            ["instanceId"] = prop.objectReferenceValue.GetInstanceID()
                        };
                    }
                    return JValue.CreateNull();
                default:
                    return $"({prop.propertyType})";
            }
        }

        private bool IsMultipleAllowed(Type componentType)
        {
            // Check for DisallowMultipleComponent attribute
            return !componentType.GetCustomAttributes(typeof(DisallowMultipleComponent), true).Any();
        }

        private void MarkSceneDirty(GameObject go)
        {
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage != null)
            {
                EditorSceneManager.MarkSceneDirty(prefabStage.scene);
            }
            else
            {
                EditorSceneManager.MarkSceneDirty(go.scene);
            }
        }

        #endregion
    }
}
