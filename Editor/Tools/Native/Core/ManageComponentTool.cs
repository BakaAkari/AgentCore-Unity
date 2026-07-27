using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Tools.Safety;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEditor.SceneManagement;
using UnityEngine;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Tools.Native.Core
{
    /// <summary>
    /// Add, remove, modify, and inspect components on GameObjects.
    /// Directly calls Unity Editor API as part of the native tool system.
    /// Uses SerializedObject/SerializedProperty for robust property modification.
    /// </summary>
    [AgentTool("manage_component",
        Description = "Add, remove, modify, enable/disable, copy, and inspect components on GameObjects. " +
            "Uses SerializedProperty for robust property modification — handles all serializable fields including nested objects, arrays, and references. " +
            "Supports batch operations (add_batch, remove_batch, set_property_batch, get_components_batch). " +
            "Applicable: any component manipulation on scene objects. " +
            "NOT for: creating the GameObject itself (use manage_gameobject), modifying script source code (use manage_script). " +
            "Returns: JSON with component type, enabled state, and all serialized properties with current values. " +
            "Note: componentType uses the class name (e.g. 'Rigidbody', 'BoxCollider', 'AudioSource'), not the full namespace.",
        Category = "Component", RequiresMainThread = true,
        RiskLevel = ToolRiskLevel.Medium, Capabilities = ToolCapability.ModifyScene,
        ReadOnlyActions = new[] { "get", "list", "get_components_batch" })]
    public class ManageComponentTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""add"", ""remove"", ""get"", ""list"", ""modify"", ""set_enabled"", ""add_batch"", ""remove_batch"", ""set_property_batch"", ""get_components_batch"", ""copy_component""],
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
                ""component_type"": {
                    ""type"": ""string"",
                    ""description"": ""Component type name alias for batch actions""
                },
                ""targets"": {
                    ""type"": ""string"",
                    ""description"": ""Comma-separated GameObject names or paths for batch actions""
                },
                ""items"": {
                    ""type"": ""array"",
                    ""description"": ""Batch operation items""
                },
                ""property"": {
                    ""type"": ""string"",
                    ""description"": ""Property name for set_property_batch items""
                },
                ""value"": {
                    ""description"": ""Property value for set_property_batch items""
                },
                ""include_properties"": {
                    ""type"": ""boolean"",
                    ""description"": ""Include serialized properties in get_components_batch""
                },
                ""source"": {
                    ""type"": ""string"",
                    ""description"": ""Source GameObject name or path for copy_component""
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
            ""required"": [""action""]
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

                switch (action)
                {
                    case "add_batch":
                        response = HandleAddBatch(parameters);
                        break;
                    case "remove_batch":
                        response = HandleRemoveBatch(parameters);
                        break;
                    case "set_property_batch":
                        response = HandleSetPropertyBatch(parameters);
                        break;
                    case "get_components_batch":
                        response = HandleGetComponentsBatch(parameters);
                        break;
                    case "copy_component":
                        response = HandleCopyComponent(parameters);
                        break;
                    case "add":
                    case "remove":
                    case "get":
                    case "list":
                    case "modify":
                    case "set_enabled":
                        var target = ToolHelpers.GetRequiredString(parameters, "target");
                        var go = ToolHelpers.FindGameObject(target);
                        if (go == null)
                        {
                            response = ToolResponse.Fail($"GameObject '{target}' not found.");
                        }
                        else if (action == "add")
                            response = HandleAdd(go, parameters);
                        else if (action == "remove")
                            response = HandleRemove(go, parameters);
                        else if (action == "get")
                            response = HandleGet(go, parameters);
                        else if (action == "list")
                            response = HandleList(go);
                        else if (action == "modify")
                            response = HandleModify(go, parameters);
                        else
                            response = HandleSetEnabled(go, parameters);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: add, remove, get, list, modify, set_enabled, add_batch, remove_batch, set_property_batch, get_components_batch, copy_component");
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

                AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] Added component '{componentTypeName}' to '{go.name}'");
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

                AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] Removed component '{componentTypeName}' from '{go.name}'");
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
                    AgentCoreLog.Warning($"[AgentCore] Some properties failed on '{componentTypeName}': {string.Join(", ", errors)}");
                    return ToolResponse.OkWithData(new JObject
                    {
                        ["gameObject"] = go.name,
                        ["componentType"] = componentTypeName,
                        ["errors"] = new JArray(errors.ToArray()),
                        ["partialSuccess"] = true
                    }, $"Modified '{componentTypeName}' on '{go.name}' with {errors.Count} error(s).");
                }

                AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] Modified component '{componentTypeName}' on '{go.name}'");
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

                    AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] Set '{componentTypeName}' enabled={enabled} on '{go.name}'");
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

        private ToolResponse HandleAddBatch(JObject parameters)
        {
            try
            {
                var targets = GetTargetNames(parameters);
                if (targets.Count == 0)
                    return ToolResponse.Fail("'targets' is required for 'add_batch' action.");

                var componentTypeName = GetComponentTypeName(parameters);
                var type = ToolHelpers.ResolveComponentType(componentTypeName);
                if (type == null)
                    return ToolResponse.Fail($"Component type '{componentTypeName}' not found. Use a fully-qualified name if needed.");
                if (!typeof(Component).IsAssignableFrom(type))
                    return ToolResponse.Fail($"Type '{componentTypeName}' is not a Component.");

                var successes = new JArray();
                var failures = new JArray();
                Undo.IncrementCurrentGroup();
                int undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("Add Components Batch");

                foreach (var target in targets)
                {
                    var go = ToolHelpers.FindGameObject(target);
                    if (go == null)
                    {
                        failures.Add(new JObject { ["target"] = target, ["error"] = $"GameObject '{target}' not found." });
                        continue;
                    }

                    var existing = go.GetComponent(type);
                    if (existing != null && !IsMultipleAllowed(type))
                    {
                        failures.Add(new JObject { ["target"] = target, ["error"] = $"Component '{componentTypeName}' already exists on '{go.name}' and does not allow multiples." });
                        continue;
                    }

                    var component = Undo.AddComponent(go, type);
                    if (component == null)
                    {
                        failures.Add(new JObject { ["target"] = target, ["error"] = $"Failed to add component '{componentTypeName}' to '{go.name}'." });
                        continue;
                    }

                    EditorUtility.SetDirty(go);
                    MarkSceneDirty(go);
                    successes.Add(new JObject { ["gameObject"] = go.name, ["instanceId"] = go.GetInstanceID(), ["componentType"] = type.FullName, ["componentInstanceId"] = component.GetInstanceID() });
                }

                Undo.CollapseUndoOperations(undoGroup);
                return ToolResponse.OkWithData(new JObject { ["succeeded"] = successes, ["failed"] = failures, ["successCount"] = successes.Count, ["failureCount"] = failures.Count }, $"Added component to {successes.Count} GameObject(s), {failures.Count} failure(s).");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error adding components batch: {ex.Message}");
            }
        }

        private ToolResponse HandleRemoveBatch(JObject parameters)
        {
            try
            {
                var targets = GetTargetNames(parameters);
                if (targets.Count == 0)
                    return ToolResponse.Fail("'targets' is required for 'remove_batch' action.");

                var componentTypeName = GetComponentTypeName(parameters);
                var type = ToolHelpers.ResolveComponentType(componentTypeName);
                if (type == null)
                    return ToolResponse.Fail($"Component type '{componentTypeName}' not found.");

                var successes = new JArray();
                var failures = new JArray();
                Undo.IncrementCurrentGroup();
                int undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("Remove Components Batch");

                foreach (var target in targets)
                {
                    var go = ToolHelpers.FindGameObject(target);
                    if (go == null)
                    {
                        failures.Add(new JObject { ["target"] = target, ["error"] = $"GameObject '{target}' not found." });
                        continue;
                    }

                    var component = go.GetComponent(type);
                    if (component == null)
                    {
                        failures.Add(new JObject { ["target"] = target, ["error"] = $"Component '{componentTypeName}' not found on '{go.name}'." });
                        continue;
                    }
                    if (component is Transform)
                    {
                        failures.Add(new JObject { ["target"] = target, ["error"] = "Cannot remove the Transform component." });
                        continue;
                    }

                    Undo.DestroyObjectImmediate(component);
                    EditorUtility.SetDirty(go);
                    MarkSceneDirty(go);
                    successes.Add(new JObject { ["gameObject"] = go.name, ["instanceId"] = go.GetInstanceID(), ["removedType"] = componentTypeName });
                }

                Undo.CollapseUndoOperations(undoGroup);
                return ToolResponse.OkWithData(new JObject { ["succeeded"] = successes, ["failed"] = failures, ["successCount"] = successes.Count, ["failureCount"] = failures.Count }, $"Removed component from {successes.Count} GameObject(s), {failures.Count} failure(s).");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error removing components batch: {ex.Message}");
            }
        }

        private ToolResponse HandleSetPropertyBatch(JObject parameters)
        {
            try
            {
                var items = parameters["items"] as JArray;
                if (items == null || items.Count == 0)
                    return ToolResponse.Fail("'items' array is required for 'set_property_batch' action.");

                var successes = new JArray();
                var failures = new JArray();
                Undo.IncrementCurrentGroup();
                int undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("Set Component Properties Batch");

                foreach (var token in items)
                {
                    if (!(token is JObject item))
                    {
                        failures.Add(new JObject { ["error"] = "Item must be an object." });
                        continue;
                    }

                    var target = ToolHelpers.GetOptionalString(item, "target");
                    if (string.IsNullOrEmpty(target))
                    {
                        failures.Add(new JObject { ["error"] = "Item 'target' is required." });
                        continue;
                    }

                    var go = ToolHelpers.FindGameObject(target);
                    if (go == null)
                    {
                        failures.Add(new JObject { ["target"] = target, ["error"] = $"GameObject '{target}' not found." });
                        continue;
                    }

                    var componentTypeName = GetComponentTypeName(item);
                    var type = ToolHelpers.ResolveComponentType(componentTypeName);
                    if (type == null)
                    {
                        failures.Add(new JObject { ["target"] = target, ["error"] = $"Component type '{componentTypeName}' not found." });
                        continue;
                    }

                    var component = go.GetComponent(type);
                    if (component == null)
                    {
                        failures.Add(new JObject { ["target"] = target, ["error"] = $"Component '{componentTypeName}' not found on '{go.name}'." });
                        continue;
                    }

                    var property = ToolHelpers.GetRequiredString(item, "property");
                    if (item["value"] == null)
                    {
                        failures.Add(new JObject { ["target"] = target, ["property"] = property, ["error"] = "Item 'value' is required." });
                        continue;
                    }

                    ToolHelpers.RecordUndo(component, $"Set {componentTypeName}.{property}");
                    var errors = SetPropertiesViaSerializedObject(component, new JObject { [property] = item["value"] });
                    EditorUtility.SetDirty(component);
                    MarkSceneDirty(go);
                    if (errors.Count > 0)
                        failures.Add(new JObject { ["target"] = target, ["componentType"] = componentTypeName, ["property"] = property, ["errors"] = new JArray(errors.ToArray()) });
                    else
                        successes.Add(new JObject { ["target"] = go.name, ["componentType"] = componentTypeName, ["property"] = property });
                }

                Undo.CollapseUndoOperations(undoGroup);
                return ToolResponse.OkWithData(new JObject { ["succeeded"] = successes, ["failed"] = failures, ["successCount"] = successes.Count, ["failureCount"] = failures.Count }, $"Set {successes.Count} component propertie(s), {failures.Count} failure(s).");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error setting component properties batch: {ex.Message}");
            }
        }

        private ToolResponse HandleGetComponentsBatch(JObject parameters)
        {
            try
            {
                var targets = GetTargetNames(parameters);
                if (targets.Count == 0)
                    return ToolResponse.Fail("'targets' is required for 'get_components_batch' action.");

                bool includeProperties = ToolHelpers.GetOptionalBool(parameters, "include_properties", false);
                var successes = new JArray();
                var failures = new JArray();
                foreach (var target in targets)
                {
                    var go = ToolHelpers.FindGameObject(target);
                    if (go == null)
                    {
                        failures.Add(new JObject { ["target"] = target, ["error"] = $"GameObject '{target}' not found." });
                        continue;
                    }

                    var components = new JArray();
                    foreach (var component in go.GetComponents<Component>())
                    {
                        if (component == null)
                        {
                            components.Add(new JObject { ["type"] = "(Missing Script)", ["isMissing"] = true });
                            continue;
                        }
                        components.Add(includeProperties ? SerializeComponentDetailed(component) : ToolHelpers.SerializeComponent(component));
                    }

                    successes.Add(new JObject { ["gameObject"] = go.name, ["instanceId"] = go.GetInstanceID(), ["componentCount"] = components.Count, ["components"] = components });
                }

                return ToolResponse.OkWithData(new JObject { ["succeeded"] = successes, ["failed"] = failures, ["successCount"] = successes.Count, ["failureCount"] = failures.Count }, $"Read components from {successes.Count} GameObject(s), {failures.Count} failure(s).");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error getting components batch: {ex.Message}");
            }
        }

        private ToolResponse HandleCopyComponent(JObject parameters)
        {
            try
            {
                var sourceName = ToolHelpers.GetRequiredString(parameters, "source");
                var targetName = ToolHelpers.GetRequiredString(parameters, "target");
                var componentTypeName = GetComponentTypeName(parameters);
                var source = ToolHelpers.FindGameObject(sourceName);
                var target = ToolHelpers.FindGameObject(targetName);
                if (source == null)
                    return ToolResponse.Fail($"GameObject '{sourceName}' not found.");
                if (target == null)
                    return ToolResponse.Fail($"GameObject '{targetName}' not found.");

                var type = ToolHelpers.ResolveComponentType(componentTypeName);
                if (type == null)
                    return ToolResponse.Fail($"Component type '{componentTypeName}' not found.");
                var component = source.GetComponent(type);
                if (component == null)
                    return ToolResponse.Fail($"Component '{componentTypeName}' not found on '{source.name}'.");
                if (component is Transform)
                    return ToolResponse.Fail("Cannot copy the Transform component.");

                Undo.RegisterCompleteObjectUndo(target, "Copy Component");
                if (!ComponentUtility.CopyComponent(component) || !ComponentUtility.PasteComponentAsNew(target))
                    return ToolResponse.Fail($"Failed to copy component '{componentTypeName}' from '{source.name}' to '{target.name}'.");

                EditorUtility.SetDirty(target);
                MarkSceneDirty(target);
                return ToolResponse.OkWithData(new JObject { ["source"] = source.name, ["target"] = target.name, ["componentType"] = componentTypeName }, $"Component '{componentTypeName}' copied from '{source.name}' to '{target.name}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error copying component: {ex.Message}");
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
                            errors.Add($"Property '{prop.Name}' not found on '{component.GetType().Name}'. " +
                                       $"Tip: nested fields use dot notation (e.g. 'stats.attack'), array elements use " +
                                       $"'fieldName.Array.data[N]' — run 'get' first to see the exact propertyPath keys available.");
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
                    if (!ToolHelpers.TryCoerceInt(value, "value", out var _intV)) throw new ArgumentException($"value expected int, got {value.Type}");
                    prop.intValue = _intV;
                    return true;

                case SerializedPropertyType.Boolean:
                    if (!ToolHelpers.TryCoerceBool(value, "value", out var _boolV)) throw new ArgumentException($"value expected bool, got {value.Type}");
                    prop.boolValue = _boolV;
                    return true;

                case SerializedPropertyType.Float:
                    if (!ToolHelpers.TryCoerceFloat(value, "value", out var _floatV)) throw new ArgumentException($"value expected float, got {value.Type}");
                    prop.floatValue = _floatV;
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
                        if (!ToolHelpers.TryCoerceInt(value, "value", out var _enumV)) throw new ArgumentException($"value expected int (enum index), got {value.Type}");
                        prop.enumValueIndex = _enumV;
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

                case SerializedPropertyType.ObjectReference:
                    return SetObjectReferenceValue(prop, value);

                default:
                    return false;
            }
        }

        /// <summary>
        /// 为 ObjectReference 类型的 SerializedProperty 赋值。支持多种输入格式：
        /// <list type="bullet">
        /// <item>null / JSON null → 清空引用</item>
        /// <item>{ "instanceId": 12345 } → 按实例 ID 精确解析（与读取输出对称）</item>
        /// <item>{ "path": "Assets/.../X.prefab" } → 按资源路径加载 Asset</item>
        /// <item>{ "name": "Player", "type": "Transform" } / { "name": "Player" } → 按名字在场景查找</item>
        /// <item>纯字符串 "Player" → 按名字/层级路径在场景查找</item>
        /// </list>
        /// 关键：解析出的对象会按字段期望类型做 GameObject↔Component 转换，
        /// 例如字段要 Transform 但给了 GameObject 名字时自动取其 Transform 组件。
        /// </summary>
        private bool SetObjectReferenceValue(SerializedProperty prop, JToken value)
        {
            // null → 清空引用
            if (value == null || value.Type == JTokenType.Null)
            {
                prop.objectReferenceValue = null;
                return true;
            }

            // 字段期望的对象类型（用于 GameObject↔Component 转换与类型校验）
            var expectedType = ResolveExpectedObjectType(prop);

            UnityEngine.Object resolved = null;

            if (value is JObject obj)
            {
                // 1) instanceId 精确解析
                var idToken = obj["instanceId"];
                if (idToken != null && idToken.Type == JTokenType.Integer)
                {
                    resolved = EditorUtility.InstanceIDToObject(idToken.Value<int>());
                }

                // 2) 资源路径
                if (resolved == null)
                {
                    var pathToken = obj["path"];
                    if (pathToken != null && pathToken.Type == JTokenType.String)
                    {
                        var assetPath = pathToken.Value<string>();
                        var loadType = expectedType ?? typeof(UnityEngine.Object);
                        resolved = AssetDatabase.LoadAssetAtPath(assetPath, loadType);
                    }
                }

                // 3) 按名字在场景查找
                if (resolved == null)
                {
                    var nameToken = obj["name"];
                    if (nameToken != null && nameToken.Type == JTokenType.String)
                    {
                        resolved = ResolveSceneObjectByName(nameToken.Value<string>(), expectedType);
                    }
                }
            }
            else if (value.Type == JTokenType.String)
            {
                var str = value.Value<string>();
                // 先当资源路径试（含扩展名 / 以 Assets/ 开头），否则当场景名字
                if (str.StartsWith("Assets/") || str.Contains("."))
                {
                    var loadType = expectedType ?? typeof(UnityEngine.Object);
                    resolved = AssetDatabase.LoadAssetAtPath(str, loadType);
                }
                if (resolved == null)
                {
                    resolved = ResolveSceneObjectByName(str, expectedType);
                }
            }
            else if (value.Type == JTokenType.Integer)
            {
                resolved = EditorUtility.InstanceIDToObject(value.Value<int>());
            }

            if (resolved == null)
                return false; // 解析失败：调用方记录 unsupported/未找到

            // 按字段期望类型做 GameObject ↔ Component 转换
            var coerced = CoerceToExpectedType(resolved, expectedType);
            if (coerced == null)
                return false; // 类型不兼容

            prop.objectReferenceValue = coerced;
            return true;
        }

        /// <summary>反射解析 ObjectReference 字段的期望类型（如 Transform / Rigidbody / GameObject）。取不到返回 null。</summary>
        private static Type ResolveExpectedObjectType(SerializedProperty prop)
        {
            try
            {
                var targetType = prop.serializedObject?.targetObject?.GetType();
                if (targetType == null) return null;
                // 支持带路径的属性名（取最后一段）；多数组件字段是顶层
                var fieldName = prop.name;
                var fi = targetType.GetField(fieldName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return fi?.FieldType;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>在场景中按名字/层级路径查找对象，并按期望类型返回 GameObject 或其组件。</summary>
        private static UnityEngine.Object ResolveSceneObjectByName(string nameOrPath, Type expectedType)
        {
            var go = ToolHelpers.FindGameObject(nameOrPath);
            if (go == null) return null;
            return CoerceToExpectedType(go, expectedType);
        }

        /// <summary>
        /// 把解析到的对象转换成字段期望类型：
        /// 期望 Component 但拿到 GameObject → GetComponent；期望 GameObject 但拿到 Component → 取 gameObject。
        /// 期望类型未知或已兼容则原样返回。不兼容返回 null。
        /// </summary>
        private static UnityEngine.Object CoerceToExpectedType(UnityEngine.Object resolved, Type expectedType)
        {
            if (resolved == null) return null;
            if (expectedType == null) return resolved; // 未知期望：交由 Unity 赋值时自行校验

            // 已经是兼容类型
            if (expectedType.IsInstanceOfType(resolved)) return resolved;

            // 期望 Component（或其子类）但拿到 GameObject → 取组件
            if (typeof(Component).IsAssignableFrom(expectedType) && resolved is GameObject go)
            {
                var comp = go.GetComponent(expectedType);
                return comp;
            }

            // 期望 GameObject 但拿到 Component → 取其 gameObject
            if (expectedType == typeof(GameObject) && resolved is Component c)
            {
                return c.gameObject;
            }

            return null; // 不兼容
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

            // Add all serialized properties.
            // v1.7.22: recurse into nested serialized properties and use propertyPath as the JSON key,
            // so agents can see nested fields (e.g. "stats.attack", "clips.Array.data[0].name") and
            // pass them directly back to modify/set_property_batch — Unity's SerializedObject.FindProperty
            // already accepts dot-separated path notation.
            var serializedObject = new SerializedObject(component);
            var propsObj = new JObject();
            var iterator = serializedObject.GetIterator();

            if (iterator.NextVisible(true))
            {
                do
                {
                    // Skip internal Unity properties.
                    if (iterator.name == "m_Script" || iterator.name == "m_ObjectHideFlags")
                        continue;

                    // Skip the two synthetic top-level array wrappers (Array + size); the real elements
                    // are already surfaced under their own paths (Array.data[N]).
                    if (iterator.name == "Array" || iterator.name == "size")
                        continue;

                    var propValue = GetSerializedPropertyValue(iterator);
                    if (propValue != null)
                    {
                        // Use propertyPath (full dot-separated path) so nested fields become
                        // agent-addressable modify keys directly.
                        propsObj[iterator.propertyPath] = propValue;
                    }
                } while (iterator.NextVisible(true)); // enterChildren = true → recurse into structs/nested
            }

            result["serializedProperties"] = propsObj;
            result["_hint"] = "Property keys use SerializedObject path notation. Nested fields (e.g. 'stats.attack') and array elements (e.g. 'clips.Array.data[0].name') can be passed directly to modify or set_property_batch.";
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

        private string GetComponentTypeName(JObject parameters)
        {
            var componentTypeName = ToolHelpers.GetOptionalString(parameters, "componentType");
            if (string.IsNullOrEmpty(componentTypeName))
                componentTypeName = ToolHelpers.GetOptionalString(parameters, "component_type");
            if (string.IsNullOrEmpty(componentTypeName))
                throw new ArgumentException("'componentType' or 'component_type' parameter is required.");
            return componentTypeName;
        }

        private List<string> GetTargetNames(JObject parameters)
        {
            var targets = ToolHelpers.GetOptionalString(parameters, "targets");
            if (string.IsNullOrEmpty(targets))
                targets = ToolHelpers.GetOptionalString(parameters, "target");
            if (string.IsNullOrEmpty(targets))
                return new List<string>();
            return targets.Split(',').Select(t => t.Trim()).Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList();
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
