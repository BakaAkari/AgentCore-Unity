using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Tools.Safety;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Tools.Native.Scripting
{
    /// <summary>
    /// Manage Unity Prefabs — create from GameObjects, instantiate, get info, unpack, apply, and revert.
    /// Directly calls PrefabUtility APIs.
    /// </summary>
    [AgentTool("manage_prefab",
        Description = "Manage Unity Prefab assets and instances. " +
            "Actions: create (save scene GameObject as Prefab asset), instantiate (place Prefab in scene), " +
            "get_info (overrides, nested prefabs, variant info), unpack (break Prefab connection), " +
            "apply (push instance overrides back to asset), revert (discard instance overrides). " +
            "Use for: Prefab workflow — creating reusable assets, managing Prefab instances and their overrides. " +
            "NOT for: modifying GameObject properties directly (use manage_gameobject/manage_component on the instance), " +
            "modifying Prefab source scripts (use manage_script). " +
            "Note: 'create' requires a scene GameObject as source; 'apply' writes to the Prefab asset file (affects all instances).",
        Category = "Scripting",
        Visibility = ToolVisibility.OnDemand,
        RequiresMainThread = true,
        RiskLevel = ToolRiskLevel.Medium,
        Capabilities = ToolCapability.ModifyAssets | ToolCapability.ModifyScene,
        ReadOnlyActions = new[] { "get_info" },
        // v1.12+ ModifyRuntimeState: create (SaveAsPrefabAsset 落盘) 和 apply (写回 Prefab 资产文件) 硬禁止。
        // instantiate/unpack/revert 只作用于场景运行时实例 (内存),Play Mode 中放行 —— 退出后自然消失。
        PlaymodeHardBlockedActions = new[] { "create", "apply" })]
    public class ManagePrefabTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""create"", ""instantiate"", ""get_info"", ""unpack"", ""apply"", ""revert""],
                    ""description"": ""Action to perform""
                },
                ""source"": {
                    ""type"": ""string"",
                    ""description"": ""Source GameObject name or path in scene (for create action)""
                },
                ""path"": {
                    ""type"": ""string"",
                    ""description"": ""Prefab asset path (e.g., 'Assets/Prefabs/MyPrefab.prefab')""
                },
                ""variant"": {
                    ""type"": ""boolean"",
                    ""description"": ""Whether to create a Prefab Variant (default: false)""
                },
                ""name"": {
                    ""type"": ""string"",
                    ""description"": ""Name for the instantiated GameObject (optional)""
                },
                ""position"": {
                    ""type"": ""object"",
                    ""properties"": {
                        ""x"": { ""type"": ""number"" },
                        ""y"": { ""type"": ""number"" },
                        ""z"": { ""type"": ""number"" }
                    },
                    ""description"": ""World position for instantiate action""
                },
                ""rotation"": {
                    ""type"": ""object"",
                    ""properties"": {
                        ""x"": { ""type"": ""number"" },
                        ""y"": { ""type"": ""number"" },
                        ""z"": { ""type"": ""number"" }
                    },
                    ""description"": ""Euler rotation for instantiate action""
                },
                ""parent"": {
                    ""type"": ""string"",
                    ""description"": ""Parent GameObject name for instantiate action""
                },
                ""target"": {
                    ""type"": ""string"",
                    ""description"": ""Target GameObject name in scene (for unpack, apply, revert actions)""
                },
                ""mode"": {
                    ""type"": ""string"",
                    ""enum"": [""root"", ""completely""],
                    ""description"": ""Unpack mode (default: 'root')""
                }
            },
            ""required"": [""action""]
        }");

        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_prefab",
            description: "Manage Unity Prefabs — create, instantiate, get info, unpack, apply overrides, and revert",
            category: "Scripting",
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
                    case "instantiate":
                        response = HandleInstantiate(parameters);
                        break;
                    case "get_info":
                        response = HandleGetInfo(parameters);
                        break;
                    case "unpack":
                        response = HandleUnpack(parameters);
                        break;
                    case "apply":
                        response = HandleApply(parameters);
                        break;
                    case "revert":
                        response = HandleRevert(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: create, instantiate, get_info, unpack, apply, revert");
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
            var sourceName = ToolHelpers.GetRequiredString(parameters, "source");
            var path = ToolHelpers.GetRequiredString(parameters, "path");
            var variant = ToolHelpers.GetOptionalBool(parameters, "variant", false);

            path = ToolHelpers.NormalizeAssetPath(path);

            // Ensure .prefab extension
            if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                path += ".prefab";

            // Find source GameObject in scene
            var sourceGo = ToolHelpers.FindGameObject(sourceName);
            if (sourceGo == null)
                return ToolResponse.Fail($"Source GameObject not found: '{sourceName}'");

            // Ensure directory exists
            ToolHelpers.EnsureDirectoryExists(System.IO.Path.GetFullPath(path));

            GameObject prefab;
            bool success;

            if (variant)
            {
                // Check if source is already a prefab instance
                var sourcePrefab = PrefabUtility.GetCorrespondingObjectFromSource(sourceGo);
                if (sourcePrefab == null)
                {
                    // First save as regular prefab, then create variant
                    var basePrefab = PrefabUtility.SaveAsPrefabAsset(sourceGo, path, out success);
                    if (!success || basePrefab == null)
                        return ToolResponse.Fail($"Failed to create base prefab at: {path}");

                    prefab = basePrefab;
                }
                else
                {
                    prefab = PrefabUtility.SaveAsPrefabAsset(sourceGo, path, out success);
                    if (!success || prefab == null)
                        return ToolResponse.Fail($"Failed to create prefab variant at: {path}");
                }
            }
            else
            {
                prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(sourceGo, path,
                    InteractionMode.UserAction, out success);
                if (!success || prefab == null)
                    return ToolResponse.Fail($"Failed to create prefab at: {path}");
            }

            Undo.RegisterCreatedObjectUndo(prefab, "AgentCore: Create Prefab");

            return ToolResponse.OkWithData(new JObject
            {
                ["path"] = path,
                ["name"] = prefab.name,
                ["instanceId"] = prefab.GetInstanceID(),
                ["isVariant"] = variant
            }, $"Created prefab: {path}");
        }

        private ToolResponse HandleInstantiate(JObject parameters)
        {
            var path = ToolHelpers.GetRequiredString(parameters, "path");
            path = ToolHelpers.NormalizeAssetPath(path);

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
                return ToolResponse.Fail($"Prefab not found at: {path}");

            // Use PrefabUtility.InstantiatePrefab to maintain prefab connection
            var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            if (instance == null)
                return ToolResponse.Fail($"Failed to instantiate prefab: {path}");

            // Set name
            var name = ToolHelpers.GetOptionalString(parameters, "name");
            if (!string.IsNullOrEmpty(name))
                instance.name = name;

            // Set position
            var posToken = parameters["position"];
            if (posToken != null)
            {
                instance.transform.position = ToolHelpers.ParseVector3(posToken);
            }

            // Set rotation
            var rotToken = parameters["rotation"];
            if (rotToken != null)
            {
                var euler = ToolHelpers.ParseVector3(rotToken);
                instance.transform.rotation = Quaternion.Euler(euler);
            }

            // Set parent
            var parentName = ToolHelpers.GetOptionalString(parameters, "parent");
            if (!string.IsNullOrEmpty(parentName))
            {
                var parent = ToolHelpers.FindGameObject(parentName);
                if (parent != null)
                {
                    instance.transform.SetParent(parent.transform, true);
                }
                else
                {
                    // Don't fail, just warn
                    AgentCoreLog.Warning($"[AgentCore] Parent GameObject not found: '{parentName}'. Prefab instantiated at root.");
                }
            }

            Undo.RegisterCreatedObjectUndo(instance, "AgentCore: Instantiate Prefab");

            return ToolResponse.OkWithData(new JObject
            {
                ["name"] = instance.name,
                ["instanceId"] = instance.GetInstanceID(),
                ["prefabPath"] = path,
                ["position"] = ToolHelpers.Vector3ToJson(instance.transform.position),
                ["rotation"] = ToolHelpers.QuaternionToJson(instance.transform.rotation)
            }, $"Instantiated prefab '{path}' as '{instance.name}'");
        }

        private ToolResponse HandleGetInfo(JObject parameters)
        {
            var path = ToolHelpers.GetRequiredString(parameters, "path");
            path = ToolHelpers.NormalizeAssetPath(path);

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
                return ToolResponse.Fail($"Prefab not found at: {path}");

            // Load prefab contents for inspection
            var contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var info = new JObject
                {
                    ["path"] = path,
                    ["name"] = contents.name,
                    ["assetGuid"] = AssetDatabase.AssetPathToGUID(path)
                };

                // Check prefab type
                var prefabAssetType = PrefabUtility.GetPrefabAssetType(prefab);
                info["prefabType"] = prefabAssetType.ToString();

                // Components on root
                var components = new JArray();
                foreach (var comp in contents.GetComponents<Component>())
                {
                    if (comp == null) continue;
                    components.Add(new JObject
                    {
                        ["type"] = comp.GetType().Name,
                        ["fullType"] = comp.GetType().FullName
                    });
                }
                info["components"] = components;

                // Child hierarchy
                var children = new JArray();
                BuildHierarchy(contents.transform, children, 0, 3); // Max depth 3
                info["children"] = children;

                // Count total objects
                var allTransforms = contents.GetComponentsInChildren<Transform>(true);
                info["totalObjects"] = allTransforms.Length;

                return ToolResponse.OkWithData(info, $"Prefab info: {path}");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        private ToolResponse HandleUnpack(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");
            var mode = ToolHelpers.GetOptionalString(parameters, "mode", "root").ToLowerInvariant();

            var targetGo = ToolHelpers.FindGameObject(targetName);
            if (targetGo == null)
                return ToolResponse.Fail($"Target GameObject not found: '{targetName}'");

            // Check if it's a prefab instance
            if (!PrefabUtility.IsPartOfPrefabInstance(targetGo))
                return ToolResponse.Fail($"'{targetName}' is not a prefab instance.");

            Undo.RecordObject(targetGo, "AgentCore: Unpack Prefab");

            PrefabUnpackMode unpackMode;
            switch (mode)
            {
                case "completely":
                    unpackMode = PrefabUnpackMode.Completely;
                    break;
                case "root":
                default:
                    unpackMode = PrefabUnpackMode.OutermostRoot;
                    break;
            }

            PrefabUtility.UnpackPrefabInstance(targetGo, unpackMode, InteractionMode.UserAction);

            return ToolResponse.OkWithData(new JObject
            {
                ["target"] = targetName,
                ["mode"] = mode,
                ["instanceId"] = targetGo.GetInstanceID()
            }, $"Unpacked prefab instance '{targetName}' (mode: {mode})");
        }

        private ToolResponse HandleApply(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");
            var path = ToolHelpers.GetOptionalString(parameters, "path");

            var targetGo = ToolHelpers.FindGameObject(targetName);
            if (targetGo == null)
                return ToolResponse.Fail($"Target GameObject not found: '{targetName}'");

            // Check if it's a prefab instance
            if (!PrefabUtility.IsPartOfPrefabInstance(targetGo))
                return ToolResponse.Fail($"'{targetName}' is not a prefab instance.");

            // Get the prefab asset path if not provided
            if (string.IsNullOrEmpty(path))
            {
                var prefabSource = PrefabUtility.GetCorrespondingObjectFromSource(targetGo);
                if (prefabSource == null)
                    return ToolResponse.Fail($"Could not determine prefab source for '{targetName}'.");

                path = AssetDatabase.GetAssetPath(prefabSource);
            }
            else
            {
                path = ToolHelpers.NormalizeAssetPath(path);
            }

            if (string.IsNullOrEmpty(path))
                return ToolResponse.Fail("Could not determine prefab asset path.");

            // Apply all overrides
            PrefabUtility.ApplyPrefabInstance(targetGo, InteractionMode.UserAction);

            return ToolResponse.OkWithData(new JObject
            {
                ["target"] = targetName,
                ["prefabPath"] = path,
                ["instanceId"] = targetGo.GetInstanceID()
            }, $"Applied overrides from '{targetName}' to prefab: {path}");
        }

        private ToolResponse HandleRevert(JObject parameters)
        {
            var targetName = ToolHelpers.GetRequiredString(parameters, "target");

            var targetGo = ToolHelpers.FindGameObject(targetName);
            if (targetGo == null)
                return ToolResponse.Fail($"Target GameObject not found: '{targetName}'");

            // Check if it's a prefab instance
            if (!PrefabUtility.IsPartOfPrefabInstance(targetGo))
                return ToolResponse.Fail($"'{targetName}' is not a prefab instance.");

            Undo.RecordObject(targetGo, "AgentCore: Revert Prefab");

            PrefabUtility.RevertPrefabInstance(targetGo, InteractionMode.UserAction);

            return ToolResponse.OkWithData(new JObject
            {
                ["target"] = targetName,
                ["instanceId"] = targetGo.GetInstanceID()
            }, $"Reverted prefab instance '{targetName}' to prefab state");
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Recursively build a hierarchy representation of child transforms.
        /// </summary>
        private void BuildHierarchy(Transform parent, JArray children, int currentDepth, int maxDepth)
        {
            if (currentDepth >= maxDepth) return;

            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                var childObj = new JObject
                {
                    ["name"] = child.name,
                    ["activeSelf"] = child.gameObject.activeSelf
                };

                // List components (type names only)
                var comps = new JArray();
                foreach (var comp in child.GetComponents<Component>())
                {
                    if (comp == null) continue;
                    if (comp is Transform) continue; // Skip Transform, it's always there
                    comps.Add(comp.GetType().Name);
                }
                if (comps.Count > 0)
                    childObj["components"] = comps;

                // Recurse into children
                if (child.childCount > 0 && currentDepth + 1 < maxDepth)
                {
                    var subChildren = new JArray();
                    BuildHierarchy(child, subChildren, currentDepth + 1, maxDepth);
                    if (subChildren.Count > 0)
                        childObj["children"] = subChildren;
                }
                else if (child.childCount > 0)
                {
                    childObj["childCount"] = child.childCount;
                }

                children.Add(childObj);
            }
        }

        #endregion
    }
}
