using System;
using System.Linq;
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
    /// Create, modify, delete, and inspect GameObjects in the scene.
    /// Directly calls Unity Editor API as part of the native tool system.
    /// </summary>
    [AgentTool("manage_gameobject", Description = "Create, modify, delete, and inspect GameObjects in the scene", Category = "GameObject", RequiresMainThread = true)]
    public class ManageGameObjectTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""create"", ""delete"", ""get_info"", ""modify"", ""set_transform"", ""set_parent"", ""duplicate""],
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
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: create, delete, get_info, modify, set_transform, set_parent, duplicate");
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
