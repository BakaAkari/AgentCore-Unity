using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Tools.Safety;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AgentCore.Editor.Tools.Native.Core
{
    /// <summary>
    /// Create, modify, delete, and inspect GameObjects in the scene.
    /// Directly calls Unity Editor API as part of the native tool system.
    /// </summary>
    [AgentTool("manage_gameobject",
        Description = "Create, modify, delete, duplicate, reparent, and inspect GameObjects in the currently open scene. " +
            "Supports single and batch operations (create_batch, modify_batch, delete_batch, set_active_batch, arrange_grid). " +
            "Applicable: operating on objects in the active scene hierarchy. " +
            "NOT for: Prefab asset editing (use manage_prefab), adding/removing components (use manage_component), reading scene structure (use manage_scene get_hierarchy). " +
            "Returns: JSON with name, hierarchy path, instanceId, transform, and active state. " +
            "Note: 'target' accepts a name or hierarchy path (e.g. '/Canvas/Panel/Button'). Duplicate names return the first match — use find_gameobjects to disambiguate.",
        Category = "GameObject", RequiresMainThread = true,
        RiskLevel = ToolRiskLevel.Medium, Capabilities = ToolCapability.ModifyScene)]
    public class ManageGameObjectTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""create"", ""delete"", ""get_info"", ""modify"", ""set_transform"", ""set_parent"", ""duplicate"", ""create_batch"", ""modify_batch"", ""delete_batch"", ""set_active_batch"", ""arrange_grid""],
                    ""description"": ""Action to perform""
                },
                ""target"": {
                    ""type"": ""string"",
                    ""description"": ""Target GameObject name or path""
                },
                ""primitiveType"": {
                    ""type"": ""string"",
                    ""enum"": [""Cube"", ""Sphere"", ""Cylinder"", ""Plane"", ""Capsule"", ""Empty""],
                    ""description"": ""Primitive type for create action""
                },
                ""name"": {
                    ""type"": ""string"",
                    ""description"": ""Name for the new/modified GameObject""
                },
                ""tag"": {
                    ""type"": ""string"",
                    ""description"": ""Tag to set""
                },
                ""layer"": {
                    ""type"": ""string"",
                    ""description"": ""Layer name to set""
                },
                ""isActive"": {
                    ""type"": ""boolean"",
                    ""description"": ""Active state""
                },
                ""isStatic"": {
                    ""type"": ""boolean"",
                    ""description"": ""Static flag""
                },
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
                ""scale"": {
                    ""type"": ""object"",
                    ""properties"": { ""x"": {""type"":""number""}, ""y"": {""type"":""number""}, ""z"": {""type"":""number""} },
                    ""description"": ""Local scale""
                },
                ""parent"": {
                    ""type"": ""string"",
                    ""description"": ""Parent GameObject name or path""
                },
                ""primitive_type"": {
                    ""type"": ""string"",
                    ""enum"": [""empty"", ""cube"", ""sphere"", ""capsule"", ""cylinder"", ""plane"", ""quad""],
                    ""description"": ""Primitive type for batch create action""
                },
                ""active"": {
                    ""type"": ""boolean"",
                    ""description"": ""Active state for batch actions""
                },
                ""names"": {
                    ""type"": ""string"",
                    ""description"": ""Comma-separated GameObject names or paths for batch actions""
                },
                ""items"": {
                    ""type"": ""array"",
                    ""description"": ""Batch operation items""
                },
                ""columns"": {
                    ""type"": ""integer"",
                    ""description"": ""Column count for arrange_grid""
                },
                ""spacing"": {
                    ""type"": ""object"",
                    ""properties"": { ""x"": {""type"":""number""}, ""y"": {""type"":""number""}, ""z"": {""type"":""number""} },
                    ""description"": ""Grid spacing for arrange_grid""
                },
                ""start_position"": {
                    ""type"": ""object"",
                    ""properties"": { ""x"": {""type"":""number""}, ""y"": {""type"":""number""}, ""z"": {""type"":""number""} },
                    ""description"": ""Start position for arrange_grid""
                },
                ""includeComponents"": {
                    ""type"": ""boolean"",
                    ""description"": ""Include component details in get_info""
                },
                ""includeChildren"": {
                    ""type"": ""boolean"",
                    ""description"": ""Include children in get_info""
                }
            },
            ""required"": [""action""]
        }");

        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_gameobject",
            description: "Create, modify, delete, and inspect GameObjects in the scene",
            category: "GameObject",
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
                    case "delete":
                        response = HandleDelete(parameters);
                        break;
                    case "get_info":
                        response = HandleGetInfo(parameters);
                        break;
                    case "modify":
                        response = HandleModify(parameters);
                        break;
                    case "set_transform":
                        response = HandleSetTransform(parameters);
                        break;
                    case "set_parent":
                        response = HandleSetParent(parameters);
                        break;
                    case "duplicate":
                        response = HandleDuplicate(parameters);
                        break;
                    case "create_batch":
                        response = HandleCreateBatch(parameters);
                        break;
                    case "modify_batch":
                        response = HandleModifyBatch(parameters);
                        break;
                    case "delete_batch":
                        response = HandleDeleteBatch(parameters);
                        break;
                    case "set_active_batch":
                        response = HandleSetActiveBatch(parameters);
                        break;
                    case "arrange_grid":
                        response = HandleArrangeGrid(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: create, delete, get_info, modify, set_transform, set_parent, duplicate, create_batch, modify_batch, delete_batch, set_active_batch, arrange_grid");
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
            try
            {
                var primitiveTypeStr = ToolHelpers.GetOptionalString(parameters, "primitiveType", "Empty");
                var name = ToolHelpers.GetOptionalString(parameters, "name");
                var parentPath = ToolHelpers.GetOptionalString(parameters, "parent");

                GameObject go;

                if (primitiveTypeStr.Equals("Empty", StringComparison.OrdinalIgnoreCase))
                {
                    go = new GameObject(name ?? "GameObject");
                }
                else if (Enum.TryParse<PrimitiveType>(primitiveTypeStr, true, out var primitiveType))
                {
                    go = GameObject.CreatePrimitive(primitiveType);
                    if (!string.IsNullOrEmpty(name))
                        go.name = name;
                }
                else
                {
                    return ToolResponse.Fail(
                        $"Invalid primitiveType: '{primitiveTypeStr}'. Valid types: Cube, Sphere, Cylinder, Plane, Capsule, Empty");
                }

                // Register for undo
                ToolHelpers.RegisterCreatedObject(go, "Create GameObject");

                // Set parent if specified
                if (!string.IsNullOrEmpty(parentPath))
                {
                    var parent = ToolHelpers.FindGameObject(parentPath);
                    if (parent != null)
                    {
                        go.transform.SetParent(parent.transform, true);
                    }
                    else
                    {
                        Debug.LogWarning($"[AgentCore] Parent '{parentPath}' not found, creating at root.");
                    }
                }

                // Apply transform if provided
                ApplyTransform(go, parameters);

                // Apply tag, layer, static, active
                ApplyProperties(go, parameters);

                EditorUtility.SetDirty(go);
                MarkSceneDirty(go);

                Debug.Log($"[AgentCore] Created GameObject '{go.name}' (type={primitiveTypeStr})");
                return ToolResponse.OkWithData(
                    ToolHelpers.SerializeGameObject(go, includeComponents: true, includeChildren: false),
                    $"GameObject '{go.name}' created successfully.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error creating GameObject: {ex.Message}");
            }
        }

        private ToolResponse HandleDelete(JObject parameters)
        {
            try
            {
                var target = ToolHelpers.GetRequiredString(parameters, "target");
                var go = ToolHelpers.FindGameObject(target);

                if (go == null)
                    return ToolResponse.Fail($"GameObject '{target}' not found.");

                var name = go.name;
                var instanceId = go.GetInstanceID();

                Undo.DestroyObjectImmediate(go);

                Debug.Log($"[AgentCore] Deleted GameObject '{name}'");
                return ToolResponse.OkWithData(new JObject
                {
                    ["deletedName"] = name,
                    ["deletedInstanceId"] = instanceId
                }, $"GameObject '{name}' deleted.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error deleting GameObject: {ex.Message}");
            }
        }

        private ToolResponse HandleGetInfo(JObject parameters)
        {
            try
            {
                var target = ToolHelpers.GetRequiredString(parameters, "target");
                var go = ToolHelpers.FindGameObject(target);

                if (go == null)
                    return ToolResponse.Fail($"GameObject '{target}' not found.");

                bool includeComponents = ToolHelpers.GetOptionalBool(parameters, "includeComponents", true);
                bool includeChildren = ToolHelpers.GetOptionalBool(parameters, "includeChildren", false);

                var data = ToolHelpers.SerializeGameObject(go, includeComponents, includeChildren);

                // Add hierarchy path
                data["path"] = GetGameObjectPath(go);

                return ToolResponse.OkWithData(data, $"Info for GameObject '{go.name}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error getting GameObject info: {ex.Message}");
            }
        }

        private ToolResponse HandleModify(JObject parameters)
        {
            try
            {
                var target = ToolHelpers.GetRequiredString(parameters, "target");
                var go = ToolHelpers.FindGameObject(target);

                if (go == null)
                    return ToolResponse.Fail($"GameObject '{target}' not found.");

                ToolHelpers.RecordUndo(go, "Modify GameObject");

                // Rename
                var newName = ToolHelpers.GetOptionalString(parameters, "name");
                if (!string.IsNullOrEmpty(newName))
                    go.name = newName;

                // Apply properties (tag, layer, static, active)
                ApplyProperties(go, parameters);

                EditorUtility.SetDirty(go);
                MarkSceneDirty(go);

                Debug.Log($"[AgentCore] Modified GameObject '{go.name}'");
                return ToolResponse.OkWithData(
                    ToolHelpers.SerializeGameObject(go),
                    $"GameObject '{go.name}' modified.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error modifying GameObject: {ex.Message}");
            }
        }

        private ToolResponse HandleSetTransform(JObject parameters)
        {
            try
            {
                var target = ToolHelpers.GetRequiredString(parameters, "target");
                var go = ToolHelpers.FindGameObject(target);

                if (go == null)
                    return ToolResponse.Fail($"GameObject '{target}' not found.");

                ToolHelpers.RecordUndo(go.transform, "Set Transform");

                ApplyTransform(go, parameters);

                EditorUtility.SetDirty(go);
                MarkSceneDirty(go);

                Debug.Log($"[AgentCore] Set transform on '{go.name}'");
                return ToolResponse.OkWithData(new JObject
                {
                    ["name"] = go.name,
                    ["instanceId"] = go.GetInstanceID(),
                    ["transform"] = new JObject
                    {
                        ["position"] = ToolHelpers.Vector3ToJson(go.transform.position),
                        ["rotation"] = ToolHelpers.QuaternionToJson(go.transform.rotation),
                        ["scale"] = ToolHelpers.Vector3ToJson(go.transform.localScale)
                    }
                }, $"Transform set on '{go.name}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error setting transform: {ex.Message}");
            }
        }

        private ToolResponse HandleSetParent(JObject parameters)
        {
            try
            {
                var target = ToolHelpers.GetRequiredString(parameters, "target");
                var go = ToolHelpers.FindGameObject(target);

                if (go == null)
                    return ToolResponse.Fail($"GameObject '{target}' not found.");

                var parentPath = ToolHelpers.GetOptionalString(parameters, "parent");

                ToolHelpers.RecordUndo(go.transform, "Set Parent");

                if (string.IsNullOrEmpty(parentPath))
                {
                    // Unparent (move to root)
                    go.transform.SetParent(null, true);
                }
                else
                {
                    var parent = ToolHelpers.FindGameObject(parentPath);
                    if (parent == null)
                        return ToolResponse.Fail($"Parent GameObject '{parentPath}' not found.");

                    go.transform.SetParent(parent.transform, true);
                }

                EditorUtility.SetDirty(go);
                MarkSceneDirty(go);

                var parentName = go.transform.parent != null ? go.transform.parent.name : "(root)";
                Debug.Log($"[AgentCore] Set parent of '{go.name}' to '{parentName}'");
                return ToolResponse.OkWithData(new JObject
                {
                    ["name"] = go.name,
                    ["instanceId"] = go.GetInstanceID(),
                    ["parent"] = parentName,
                    ["path"] = GetGameObjectPath(go)
                }, $"Parent of '{go.name}' set to '{parentName}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error setting parent: {ex.Message}");
            }
        }

        private ToolResponse HandleDuplicate(JObject parameters)
        {
            try
            {
                var target = ToolHelpers.GetRequiredString(parameters, "target");
                var go = ToolHelpers.FindGameObject(target);

                if (go == null)
                    return ToolResponse.Fail($"GameObject '{target}' not found.");

                var duplicate = UnityEngine.Object.Instantiate(go, go.transform.parent);
                var newName = ToolHelpers.GetOptionalString(parameters, "name");
                duplicate.name = newName ?? go.name;

                ToolHelpers.RegisterCreatedObject(duplicate, "Duplicate GameObject");

                EditorUtility.SetDirty(duplicate);
                MarkSceneDirty(duplicate);

                Debug.Log($"[AgentCore] Duplicated '{go.name}' as '{duplicate.name}'");
                return ToolResponse.OkWithData(
                    ToolHelpers.SerializeGameObject(duplicate),
                    $"GameObject '{go.name}' duplicated as '{duplicate.name}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error duplicating GameObject: {ex.Message}");
            }
        }

        private ToolResponse HandleCreateBatch(JObject parameters)
        {
            try
            {
                var items = parameters["items"] as JArray;
                if (items == null || items.Count == 0)
                    return ToolResponse.Fail("'items' array is required for 'create_batch' action.");

                var successes = new JArray();
                var failures = new JArray();
                Undo.IncrementCurrentGroup();
                int undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("Create GameObjects Batch");

                foreach (var token in items)
                {
                    if (!(token is JObject item))
                    {
                        failures.Add(new JObject { ["error"] = "Item must be an object." });
                        continue;
                    }

                    var primitiveType = ToolHelpers.GetOptionalString(item, "primitive_type", ToolHelpers.GetOptionalString(item, "primitiveType", "empty"));
                    var name = ToolHelpers.GetOptionalString(item, "name");
                    var parentPath = ToolHelpers.GetOptionalString(item, "parent");
                    var go = CreateGameObjectForPrimitive(primitiveType, name, out var error);
                    if (go == null)
                    {
                        failures.Add(new JObject { ["name"] = name ?? "(unnamed)", ["error"] = error });
                        continue;
                    }

                    ToolHelpers.RegisterCreatedObject(go, "Create GameObject Batch");
                    if (!string.IsNullOrEmpty(parentPath))
                    {
                        var parent = ToolHelpers.FindGameObject(parentPath);
                        if (parent != null)
                        {
                            go.transform.SetParent(parent.transform, true);
                        }
                        else
                        {
                            failures.Add(new JObject { ["name"] = go.name, ["error"] = $"Parent GameObject '{parentPath}' not found; created at root." });
                        }
                    }

                    ApplyTransform(go, item);
                    ApplyProperties(go, item);
                    EditorUtility.SetDirty(go);
                    MarkSceneDirty(go);
                    successes.Add(new JObject { ["name"] = go.name, ["instanceId"] = go.GetInstanceID(), ["path"] = GetGameObjectPath(go) });
                }

                Undo.CollapseUndoOperations(undoGroup);
                return ToolResponse.OkWithData(new JObject
                {
                    ["succeeded"] = successes,
                    ["failed"] = failures,
                    ["successCount"] = successes.Count,
                    ["failureCount"] = failures.Count
                }, $"Created {successes.Count} GameObject(s), {failures.Count} failure(s).");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error creating GameObjects batch: {ex.Message}");
            }
        }

        private ToolResponse HandleModifyBatch(JObject parameters)
        {
            try
            {
                var items = parameters["items"] as JArray;
                if (items == null || items.Count == 0)
                    return ToolResponse.Fail("'items' array is required for 'modify_batch' action.");

                var successes = new JArray();
                var failures = new JArray();
                Undo.IncrementCurrentGroup();
                int undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("Modify GameObjects Batch");

                foreach (var token in items)
                {
                    if (!(token is JObject item))
                    {
                        failures.Add(new JObject { ["error"] = "Item must be an object." });
                        continue;
                    }

                    var name = ToolHelpers.GetOptionalString(item, "name");
                    if (string.IsNullOrEmpty(name))
                    {
                        failures.Add(new JObject { ["error"] = "Item 'name' is required." });
                        continue;
                    }
                    var go = ToolHelpers.FindGameObject(name);
                    if (go == null)
                    {
                        failures.Add(new JObject { ["name"] = name, ["error"] = $"GameObject '{name}' not found." });
                        continue;
                    }

                    ToolHelpers.RecordUndo(go, "Modify GameObject Batch");
                    ToolHelpers.RecordUndo(go.transform, "Modify GameObject Transform Batch");
                    ApplyTransform(go, item);
                    ApplyProperties(go, item);
                    ApplyParent(go, item);
                    EditorUtility.SetDirty(go);
                    MarkSceneDirty(go);
                    successes.Add(new JObject { ["name"] = go.name, ["instanceId"] = go.GetInstanceID(), ["path"] = GetGameObjectPath(go) });
                }

                Undo.CollapseUndoOperations(undoGroup);
                return ToolResponse.OkWithData(new JObject { ["succeeded"] = successes, ["failed"] = failures, ["successCount"] = successes.Count, ["failureCount"] = failures.Count }, $"Modified {successes.Count} GameObject(s), {failures.Count} failure(s).");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error modifying GameObjects batch: {ex.Message}");
            }
        }

        private ToolResponse HandleDeleteBatch(JObject parameters)
        {
            try
            {
                var names = GetNamesFromParameters(parameters);
                if (names.Count == 0)
                    return ToolResponse.Fail("'names' or 'items' is required for 'delete_batch' action.");

                var successes = new JArray();
                var failures = new JArray();
                Undo.IncrementCurrentGroup();
                int undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("Delete GameObjects Batch");

                foreach (var name in names)
                {
                    var go = ToolHelpers.FindGameObject(name);
                    if (go == null)
                    {
                        failures.Add(new JObject { ["name"] = name, ["error"] = $"GameObject '{name}' not found." });
                        continue;
                    }

                    int instanceId = go.GetInstanceID();
                    string deletedName = go.name;
                    Undo.DestroyObjectImmediate(go);
                    successes.Add(new JObject { ["name"] = deletedName, ["instanceId"] = instanceId });
                }

                Undo.CollapseUndoOperations(undoGroup);
                return ToolResponse.OkWithData(new JObject { ["succeeded"] = successes, ["failed"] = failures, ["successCount"] = successes.Count, ["failureCount"] = failures.Count }, $"Deleted {successes.Count} GameObject(s), {failures.Count} failure(s).");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error deleting GameObjects batch: {ex.Message}");
            }
        }

        private ToolResponse HandleSetActiveBatch(JObject parameters)
        {
            try
            {
                var names = GetNamesFromParameters(parameters);
                if (names.Count == 0)
                    return ToolResponse.Fail("'names' is required for 'set_active_batch' action.");

                bool active = ToolHelpers.GetOptionalBool(parameters, "active", true);
                var successes = new JArray();
                var failures = new JArray();
                Undo.IncrementCurrentGroup();
                int undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("Set Active GameObjects Batch");

                foreach (var name in names)
                {
                    var go = ToolHelpers.FindGameObject(name);
                    if (go == null)
                    {
                        failures.Add(new JObject { ["name"] = name, ["error"] = $"GameObject '{name}' not found." });
                        continue;
                    }

                    ToolHelpers.RecordUndo(go, "Set Active GameObject Batch");
                    go.SetActive(active);
                    EditorUtility.SetDirty(go);
                    MarkSceneDirty(go);
                    successes.Add(new JObject { ["name"] = go.name, ["instanceId"] = go.GetInstanceID(), ["active"] = go.activeSelf });
                }

                Undo.CollapseUndoOperations(undoGroup);
                return ToolResponse.OkWithData(new JObject { ["succeeded"] = successes, ["failed"] = failures, ["successCount"] = successes.Count, ["failureCount"] = failures.Count }, $"Set active={active} on {successes.Count} GameObject(s), {failures.Count} failure(s).");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error setting active batch: {ex.Message}");
            }
        }

        private ToolResponse HandleArrangeGrid(JObject parameters)
        {
            try
            {
                var names = GetNamesFromParameters(parameters);
                if (names.Count == 0)
                    return ToolResponse.Fail("'names' is required for 'arrange_grid' action.");

                int columns = Math.Max(1, ToolHelpers.GetOptionalInt(parameters, "columns", 1));
                var spacing = ToolHelpers.ParseVector3(parameters["spacing"], Vector3.one);
                var startPosition = ToolHelpers.ParseVector3(parameters["start_position"], Vector3.zero);
                var successes = new JArray();
                var failures = new JArray();
                Undo.IncrementCurrentGroup();
                int undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("Arrange GameObjects Grid");

                int placed = 0;
                foreach (var name in names)
                {
                    var go = ToolHelpers.FindGameObject(name);
                    if (go == null)
                    {
                        failures.Add(new JObject { ["name"] = name, ["error"] = $"GameObject '{name}' not found." });
                        continue;
                    }

                    ToolHelpers.RecordUndo(go.transform, "Arrange GameObject Grid");
                    int row = placed / columns;
                    int col = placed % columns;
                    var position = startPosition + new Vector3(col * spacing.x, 0f, row * spacing.z) + new Vector3(0f, row * spacing.y, 0f);
                    go.transform.position = position;
                    EditorUtility.SetDirty(go);
                    MarkSceneDirty(go);
                    successes.Add(new JObject { ["name"] = go.name, ["instanceId"] = go.GetInstanceID(), ["position"] = ToolHelpers.Vector3ToJson(position) });
                    placed++;
                }

                Undo.CollapseUndoOperations(undoGroup);
                return ToolResponse.OkWithData(new JObject { ["succeeded"] = successes, ["failed"] = failures, ["successCount"] = successes.Count, ["failureCount"] = failures.Count }, $"Arranged {successes.Count} GameObject(s), {failures.Count} failure(s).");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error arranging GameObjects grid: {ex.Message}");
            }
        }

        #endregion

        #region Helpers

        private void ApplyTransform(GameObject go, JObject parameters)
        {
            var posToken = parameters["position"];
            if (posToken != null)
            {
                go.transform.position = ToolHelpers.ParseVector3(posToken, go.transform.position);
            }

            var rotToken = parameters["rotation"];
            if (rotToken != null)
            {
                var euler = ToolHelpers.ParseVector3(rotToken, go.transform.eulerAngles);
                go.transform.eulerAngles = euler;
            }

            var scaleToken = parameters["scale"];
            if (scaleToken != null)
            {
                go.transform.localScale = ToolHelpers.ParseVector3(scaleToken, go.transform.localScale);
            }
        }

        private void ApplyProperties(GameObject go, JObject parameters)
        {
            // Tag
            var tag = ToolHelpers.GetOptionalString(parameters, "tag");
            if (!string.IsNullOrEmpty(tag))
            {
                try
                {
                    go.tag = tag;
                }
                catch (Exception)
                {
                    Debug.LogWarning($"[AgentCore] Tag '{tag}' is not defined. Add it in Project Settings first.");
                }
            }

            // Layer
            var layerName = ToolHelpers.GetOptionalString(parameters, "layer");
            if (!string.IsNullOrEmpty(layerName))
            {
                int layer = LayerMask.NameToLayer(layerName);
                if (layer >= 0)
                {
                    go.layer = layer;
                }
                else
                {
                    Debug.LogWarning($"[AgentCore] Layer '{layerName}' not found.");
                }
            }

            // Active
            var isActiveToken = parameters["isActive"];
            if (isActiveToken == null)
                isActiveToken = parameters["active"];
            if (isActiveToken != null)
            {
                go.SetActive(isActiveToken.Value<bool>());
            }

            // Static
            var isStaticToken = parameters["isStatic"];
            if (isStaticToken != null)
            {
                go.isStatic = isStaticToken.Value<bool>();
            }
        }

        private void ApplyParent(GameObject go, JObject parameters)
        {
            if (parameters["parent"] == null)
                return;

            var parentPath = ToolHelpers.GetOptionalString(parameters, "parent");
            if (string.IsNullOrEmpty(parentPath))
            {
                go.transform.SetParent(null, true);
                return;
            }

            var parent = ToolHelpers.FindGameObject(parentPath);
            if (parent != null)
                go.transform.SetParent(parent.transform, true);
        }

        private GameObject CreateGameObjectForPrimitive(string primitiveTypeStr, string name, out string error)
        {
            error = null;
            primitiveTypeStr = string.IsNullOrEmpty(primitiveTypeStr) ? "empty" : primitiveTypeStr;
            if (primitiveTypeStr.Equals("empty", StringComparison.OrdinalIgnoreCase))
                return new GameObject(name ?? "GameObject");
            if (primitiveTypeStr.Equals("quad", StringComparison.OrdinalIgnoreCase))
            {
                var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                if (!string.IsNullOrEmpty(name))
                    quad.name = name;
                return quad;
            }
            if (Enum.TryParse<PrimitiveType>(primitiveTypeStr, true, out var primitiveType))
            {
                var go = GameObject.CreatePrimitive(primitiveType);
                if (!string.IsNullOrEmpty(name))
                    go.name = name;
                return go;
            }

            error = $"Invalid primitive_type: '{primitiveTypeStr}'. Valid types: empty, cube, sphere, capsule, cylinder, plane, quad";
            return null;
        }

        private List<string> GetNamesFromParameters(JObject parameters)
        {
            var result = new List<string>();
            var names = ToolHelpers.GetOptionalString(parameters, "names");
            if (!string.IsNullOrEmpty(names))
                result.AddRange(names.Split(',').Select(n => n.Trim()).Where(n => !string.IsNullOrEmpty(n)));

            if (parameters["items"] is JArray items)
            {
                foreach (var item in items)
                {
                    if (item.Type == JTokenType.String)
                    {
                        var value = item.Value<string>();
                        if (!string.IsNullOrWhiteSpace(value))
                            result.Add(value.Trim());
                    }
                    else if (item is JObject obj)
                    {
                        var value = ToolHelpers.GetOptionalString(obj, "name");
                        if (string.IsNullOrEmpty(value))
                            value = ToolHelpers.GetOptionalString(obj, "target");
                        if (!string.IsNullOrWhiteSpace(value))
                            result.Add(value.Trim());
                    }
                }
            }

            return result.Distinct().ToList();
        }

        private string GetGameObjectPath(GameObject go)
        {
            string path = go.name;
            var current = go.transform.parent;
            while (current != null)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }
            return path;
        }

        private void MarkSceneDirty(GameObject go)
        {
            var prefabStage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
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
