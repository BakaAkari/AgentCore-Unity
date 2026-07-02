using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Tools.Safety;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AgentCore.Editor.Tools.Native.Meta
{
    /// <summary>
    /// Workflow automation tool — batch operations, scene processing, asset pipelines,
    /// and multi-step editor automation sequences.
    /// </summary>
    [AgentTool("workflow",
        Description = "High-level workflow automation for repetitive Unity Editor tasks that would otherwise require many individual tool calls. " +
            "Actions: batch_rename (regex/sequential rename), batch_tag/batch_layer (assign tags/layers by criteria), " +
            "multi_scene_process (apply operation across multiple scenes), asset_batch (bulk asset operations), " +
            "snapshot/restore (save and restore scene state), find_replace_hierarchy (search and replace in hierarchy names/components), " +
            "bulk_component (add/remove/modify component across many objects). " +
            "Use workflow instead of batch_execute when: the operation involves criteria-based selection (e.g. 'all objects with MeshRenderer') rather than explicit target lists. " +
            "NOT for: single object operations (use manage_gameobject), code editing (use manage_script).",
        Category = "Meta",
        RequiresMainThread = true,
        MayModifyScripts = false,
        RiskLevel = ToolRiskLevel.High,
        Capabilities = ToolCapability.ModifyScene | ToolCapability.ModifyAssets | ToolCapability.BatchExecute)]
    public class WorkflowTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [
                        ""batch_rename"",
                        ""batch_set_tag"",
                        ""batch_set_layer"",
                        ""batch_set_active"",
                        ""batch_set_static"",
                        ""find_replace_name"",
                        ""collect_by_component"",
                        ""collect_by_tag"",
                        ""collect_by_layer"",
                        ""snapshot_hierarchy"",
                        ""batch_add_component"",
                        ""batch_remove_component"",
                        ""batch_move_to_parent"",
                        ""count_objects"",
                        ""list_scenes""
                    ],
                    ""description"": ""Workflow action: batch_rename (rename multiple GOs with pattern), batch_set_tag/layer/active/static (bulk property changes), find_replace_name (regex/text replace in names), collect_by_component/tag/layer (find all matching GOs), snapshot_hierarchy (dump scene tree to JSON), batch_add/remove_component (bulk component ops), batch_move_to_parent (reparent multiple GOs), count_objects (statistics), list_scenes (all scenes in project)""
                },
                ""targets"": {
                    ""type"": ""array"",
                    ""items"": { ""type"": ""string"" },
                    ""description"": ""Array of GameObject names or paths to operate on""
                },
                ""pattern"": {
                    ""type"": ""string"",
                    ""description"": ""Name pattern for batch_rename: use {index} for numbering, {name} for original name, {parent} for parent name. E.g. 'Enemy_{index:00}' or '{name}_copy'""
                },
                ""find"": {
                    ""type"": ""string"",
                    ""description"": ""Text to find in names for find_replace_name""
                },
                ""replace"": {
                    ""type"": ""string"",
                    ""description"": ""Replacement text for find_replace_name""
                },
                ""tag"": {
                    ""type"": ""string"",
                    ""description"": ""Tag name for batch_set_tag or collect_by_tag""
                },
                ""layer"": {
                    ""type"": ""string"",
                    ""description"": ""Layer name or index for batch_set_layer or collect_by_layer""
                },
                ""active"": {
                    ""type"": ""boolean"",
                    ""description"": ""Active state for batch_set_active""
                },
                ""static_flags"": {
                    ""type"": ""string"",
                    ""description"": ""Static flags: all, none, batching, navigation, occluder, occludee, reflection, lightmap, off_mesh_link (default: all)""
                },
                ""component_type"": {
                    ""type"": ""string"",
                    ""description"": ""Component type name for batch_add/remove_component or collect_by_component (e.g. Rigidbody, BoxCollider, AudioSource)""
                },
                ""parent_name"": {
                    ""type"": ""string"",
                    ""description"": ""Parent GameObject name for batch_move_to_parent (empty = move to root)""
                },
                ""search_root"": {
                    ""type"": ""string"",
                    ""description"": ""Root GameObject name to limit search scope (empty = entire scene)""
                },
                ""include_inactive"": {
                    ""type"": ""boolean"",
                    ""description"": ""Include inactive GameObjects in search operations (default: true)""
                },
                ""max_depth"": {
                    ""type"": ""integer"",
                    ""description"": ""Maximum hierarchy depth for snapshot_hierarchy (default: 10)""
                },
                ""start_index"": {
                    ""type"": ""integer"",
                    ""description"": ""Starting index for batch_rename numbering (default: 0)""
                },
                ""use_regex"": {
                    ""type"": ""boolean"",
                    ""description"": ""Use regex for find_replace_name (default: false, plain text)""
                },
                ""dry_run"": {
                    ""type"": ""boolean"",
                    ""description"": ""Preview changes without applying them (default: false)""
                }
            },
            ""required"": [""action""]
        }");

        /// <summary>
        /// Tool metadata for auto-discovery registration.
        /// </summary>
        public ToolMetadata Metadata => new ToolMetadata(
            name: "workflow",
            description: "Workflow automation for Unity Editor: batch rename/tag/layer operations on GameObjects, multi-scene processing, asset batch operations, snapshot/restore scene state, find-and-replace in scene hierarchy, and bulk component operations. Use for repetitive editor tasks that would otherwise require many individual tool calls.",
            category: "Meta",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        /// <summary>
        /// Executes the requested workflow automation action.
        /// </summary>
        public Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ToolResponse response;

            try
            {
                var action = ToolHelpers.GetRequiredString(parameters, "action").ToLowerInvariant();

                switch (action)
                {
                    case "batch_rename":
                        response = HandleBatchRename(parameters);
                        break;
                    case "batch_set_tag":
                        response = HandleBatchSetTag(parameters);
                        break;
                    case "batch_set_layer":
                        response = HandleBatchSetLayer(parameters);
                        break;
                    case "batch_set_active":
                        response = HandleBatchSetActive(parameters);
                        break;
                    case "batch_set_static":
                        response = HandleBatchSetStatic(parameters);
                        break;
                    case "find_replace_name":
                        response = HandleFindReplaceName(parameters);
                        break;
                    case "collect_by_component":
                        response = HandleCollectByComponent(parameters);
                        break;
                    case "collect_by_tag":
                        response = HandleCollectByTag(parameters);
                        break;
                    case "collect_by_layer":
                        response = HandleCollectByLayer(parameters);
                        break;
                    case "snapshot_hierarchy":
                        response = HandleSnapshotHierarchy(parameters);
                        break;
                    case "batch_add_component":
                        response = HandleBatchAddComponent(parameters);
                        break;
                    case "batch_remove_component":
                        response = HandleBatchRemoveComponent(parameters);
                        break;
                    case "batch_move_to_parent":
                        response = HandleBatchMoveToParent(parameters);
                        break;
                    case "count_objects":
                        response = HandleCountObjects(parameters);
                        break;
                    case "list_scenes":
                        response = HandleListScenes();
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: batch_rename, batch_set_tag, batch_set_layer, batch_set_active, batch_set_static, find_replace_name, collect_by_component, collect_by_tag, collect_by_layer, snapshot_hierarchy, batch_add_component, batch_remove_component, batch_move_to_parent, count_objects, list_scenes");
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
        /// Renames multiple GameObjects using a pattern with {index}, {name}, {parent} placeholders.
        /// </summary>
        private ToolResponse HandleBatchRename(JObject parameters)
        {
            var targets = GetTargetGameObjects(parameters, out string error);
            if (error != null) return ToolResponse.Fail(error);
            if (targets.Count == 0) return ToolResponse.Fail("No GameObjects found matching the specified targets.");

            string pattern = ToolHelpers.GetRequiredString(parameters, "pattern");
            int startIndex = ToolHelpers.GetOptionalInt(parameters, "start_index", 0);
            bool dryRun = ToolHelpers.GetOptionalBool(parameters, "dry_run", false);

            var results = new JArray();
            int successCount = 0;

            for (int i = 0; i < targets.Count; i++)
            {
                var go = targets[i];
                string originalName = go.name;
                string parentName = go.transform.parent != null ? go.transform.parent.name : "Scene";
                int index = startIndex + i;

                // Apply pattern substitutions
                string newName = pattern
                    .Replace("{index}", index.ToString())
                    .Replace("{name}", originalName)
                    .Replace("{parent}", parentName);

                // Handle formatted index like {index:00}
                newName = System.Text.RegularExpressions.Regex.Replace(newName,
                    @"\{index:([^}]+)\}",
                    m => index.ToString(m.Groups[1].Value));

                var entry = new JObject
                {
                    ["original"] = originalName,
                    ["new"] = newName,
                    ["path"] = GetGameObjectPath(go)
                };

                if (!dryRun)
                {
                    ToolHelpers.RecordUndo(go, "Batch Rename");
                    go.name = newName;
                    EditorUtility.SetDirty(go);
                    successCount++;
                }

                results.Add(entry);
            }

            string summary = dryRun
                ? $"[DRY RUN] Would rename {targets.Count} GameObject(s) using pattern '{pattern}'."
                : $"Renamed {successCount} GameObject(s) using pattern '{pattern}'.";

            return ToolResponse.OkWithData(new
            {
                pattern,
                startIndex,
                count = targets.Count,
                dryRun,
                renames = results
            }, summary);
        }

        /// <summary>
        /// Sets the tag on multiple GameObjects.
        /// </summary>
        private ToolResponse HandleBatchSetTag(JObject parameters)
        {
            var targets = GetTargetGameObjects(parameters, out string error);
            if (error != null) return ToolResponse.Fail(error);
            if (targets.Count == 0) return ToolResponse.Fail("No GameObjects found matching the specified targets.");

            string tag = ToolHelpers.GetRequiredString(parameters, "tag");
            bool dryRun = ToolHelpers.GetOptionalBool(parameters, "dry_run", false);

            // Validate tag exists
            if (!IsValidTag(tag))
                return ToolResponse.Fail($"Tag '{tag}' does not exist. Create it first via Tags & Layers settings.");

            int successCount = 0;
            var failedNames = new List<string>();

            foreach (var go in targets)
            {
                if (!dryRun)
                {
                    try
                    {
                        ToolHelpers.RecordUndo(go, "Batch Set Tag");
                        go.tag = tag;
                        EditorUtility.SetDirty(go);
                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        failedNames.Add($"{go.name}: {ex.Message}");
                    }
                }
                else
                {
                    successCount++;
                }
            }

            string summary = dryRun
                ? $"[DRY RUN] Would set tag '{tag}' on {targets.Count} GameObject(s)."
                : $"Set tag '{tag}' on {successCount}/{targets.Count} GameObject(s).";

            return ToolResponse.OkWithData(new
            {
                tag,
                total = targets.Count,
                succeeded = successCount,
                failed = failedNames.Count,
                failedObjects = failedNames,
                dryRun
            }, summary);
        }

        /// <summary>
        /// Sets the layer on multiple GameObjects.
        /// </summary>
        private ToolResponse HandleBatchSetLayer(JObject parameters)
        {
            var targets = GetTargetGameObjects(parameters, out string error);
            if (error != null) return ToolResponse.Fail(error);
            if (targets.Count == 0) return ToolResponse.Fail("No GameObjects found matching the specified targets.");

            string layerStr = ToolHelpers.GetRequiredString(parameters, "layer");
            bool dryRun = ToolHelpers.GetOptionalBool(parameters, "dry_run", false);

            int layerIndex = ResolveLayer(layerStr);
            if (layerIndex < 0)
                return ToolResponse.Fail($"Layer '{layerStr}' not found. Use a valid layer name or index (0-31).");

            int successCount = 0;
            foreach (var go in targets)
            {
                if (!dryRun)
                {
                    ToolHelpers.RecordUndo(go, "Batch Set Layer");
                    go.layer = layerIndex;
                    EditorUtility.SetDirty(go);
                    successCount++;
                }
                else
                {
                    successCount++;
                }
            }

            string layerName = LayerMask.LayerToName(layerIndex);
            string summary = dryRun
                ? $"[DRY RUN] Would set layer '{layerName}' ({layerIndex}) on {targets.Count} GameObject(s)."
                : $"Set layer '{layerName}' ({layerIndex}) on {successCount}/{targets.Count} GameObject(s).";

            return ToolResponse.OkWithData(new
            {
                layer = layerName,
                layerIndex,
                total = targets.Count,
                succeeded = successCount,
                dryRun
            }, summary);
        }

        /// <summary>
        /// Sets the active state on multiple GameObjects.
        /// </summary>
        private ToolResponse HandleBatchSetActive(JObject parameters)
        {
            var targets = GetTargetGameObjects(parameters, out string error);
            if (error != null) return ToolResponse.Fail(error);
            if (targets.Count == 0) return ToolResponse.Fail("No GameObjects found matching the specified targets.");

            bool active = ToolHelpers.GetOptionalBool(parameters, "active", true);
            bool dryRun = ToolHelpers.GetOptionalBool(parameters, "dry_run", false);

            int successCount = 0;
            foreach (var go in targets)
            {
                if (!dryRun)
                {
                    ToolHelpers.RecordUndo(go, "Batch Set Active");
                    go.SetActive(active);
                    EditorUtility.SetDirty(go);
                    successCount++;
                }
                else
                {
                    successCount++;
                }
            }

            string summary = dryRun
                ? $"[DRY RUN] Would set active={active} on {targets.Count} GameObject(s)."
                : $"Set active={active} on {successCount}/{targets.Count} GameObject(s).";

            return ToolResponse.OkWithData(new
            {
                active,
                total = targets.Count,
                succeeded = successCount,
                dryRun
            }, summary);
        }

        /// <summary>
        /// Sets static flags on multiple GameObjects.
        /// </summary>
        private ToolResponse HandleBatchSetStatic(JObject parameters)
        {
            var targets = GetTargetGameObjects(parameters, out string error);
            if (error != null) return ToolResponse.Fail(error);
            if (targets.Count == 0) return ToolResponse.Fail("No GameObjects found matching the specified targets.");

            string flagsStr = ToolHelpers.GetOptionalString(parameters, "static_flags", "all").ToLowerInvariant();
            bool dryRun = ToolHelpers.GetOptionalBool(parameters, "dry_run", false);

            StaticEditorFlags flags = ResolveStaticFlags(flagsStr);

            int successCount = 0;
            foreach (var go in targets)
            {
                if (!dryRun)
                {
                    ToolHelpers.RecordUndo(go, "Batch Set Static");
                    GameObjectUtility.SetStaticEditorFlags(go, flags);
                    EditorUtility.SetDirty(go);
                    successCount++;
                }
                else
                {
                    successCount++;
                }
            }

            string summary = dryRun
                ? $"[DRY RUN] Would set static flags '{flagsStr}' on {targets.Count} GameObject(s)."
                : $"Set static flags '{flagsStr}' on {successCount}/{targets.Count} GameObject(s).";

            return ToolResponse.OkWithData(new
            {
                staticFlags = flagsStr,
                flagsValue = (int)flags,
                total = targets.Count,
                succeeded = successCount,
                dryRun
            }, summary);
        }

        /// <summary>
        /// Find and replace text in GameObject names across the scene hierarchy.
        /// </summary>
        private ToolResponse HandleFindReplaceName(JObject parameters)
        {
            string find = ToolHelpers.GetRequiredString(parameters, "find");
            string replace = ToolHelpers.GetOptionalString(parameters, "replace", "");
            bool useRegex = ToolHelpers.GetOptionalBool(parameters, "use_regex", false);
            bool dryRun = ToolHelpers.GetOptionalBool(parameters, "dry_run", false);
            bool includeInactive = ToolHelpers.GetOptionalBool(parameters, "include_inactive", true);
            string searchRoot = ToolHelpers.GetOptionalString(parameters, "search_root");

            var allObjects = GetAllGameObjects(searchRoot, includeInactive);
            var matches = new JArray();
            int changedCount = 0;

            foreach (var go in allObjects)
            {
                string originalName = go.name;
                string newName;

                if (useRegex)
                {
                    try
                    {
                        newName = System.Text.RegularExpressions.Regex.Replace(originalName, find, replace);
                    }
                    catch (Exception ex)
                    {
                        return ToolResponse.Fail($"Invalid regex pattern '{find}': {ex.Message}");
                    }
                }
                else
                {
                    newName = originalName.Replace(find, replace);
                }

                if (newName != originalName)
                {
                    matches.Add(new JObject
                    {
                        ["original"] = originalName,
                        ["new"] = newName,
                        ["path"] = GetGameObjectPath(go)
                    });

                    if (!dryRun)
                    {
                        ToolHelpers.RecordUndo(go, "Find Replace Name");
                        go.name = newName;
                        EditorUtility.SetDirty(go);
                        changedCount++;
                    }
                }
            }

            string summary = dryRun
                ? $"[DRY RUN] Would rename {matches.Count} GameObject(s) (find: '{find}', replace: '{replace}')."
                : $"Renamed {changedCount} GameObject(s) (find: '{find}', replace: '{replace}').";

            return ToolResponse.OkWithData(new
            {
                find,
                replace,
                useRegex,
                dryRun,
                matchCount = matches.Count,
                matches
            }, summary);
        }

        /// <summary>
        /// Collects all GameObjects that have a specific component type.
        /// </summary>
        private ToolResponse HandleCollectByComponent(JObject parameters)
        {
            string componentType = ToolHelpers.GetRequiredString(parameters, "component_type");
            bool includeInactive = ToolHelpers.GetOptionalBool(parameters, "include_inactive", true);
            string searchRoot = ToolHelpers.GetOptionalString(parameters, "search_root");

            var type = ToolHelpers.ResolveComponentType(componentType);
            if (type == null)
                return ToolResponse.Fail($"Component type '{componentType}' not found. Use the full type name (e.g. UnityEngine.Rigidbody) or short name (e.g. Rigidbody).");

            var allObjects = GetAllGameObjects(searchRoot, includeInactive);
            var found = new JArray();

            foreach (var go in allObjects)
            {
                var comp = go.GetComponent(type);
                if (comp != null)
                {
                    found.Add(new JObject
                    {
                        ["name"] = go.name,
                        ["path"] = GetGameObjectPath(go),
                        ["active"] = go.activeInHierarchy,
                        ["layer"] = LayerMask.LayerToName(go.layer),
                        ["tag"] = go.tag
                    });
                }
            }

            return ToolResponse.OkWithData(new
            {
                componentType,
                count = found.Count,
                objects = found
            }, $"Found {found.Count} GameObject(s) with component '{componentType}'.");
        }

        /// <summary>
        /// Collects all GameObjects with a specific tag.
        /// </summary>
        private ToolResponse HandleCollectByTag(JObject parameters)
        {
            string tag = ToolHelpers.GetRequiredString(parameters, "tag");
            bool includeInactive = ToolHelpers.GetOptionalBool(parameters, "include_inactive", true);
            string searchRoot = ToolHelpers.GetOptionalString(parameters, "search_root");

            if (!IsValidTag(tag))
                return ToolResponse.Fail($"Tag '{tag}' does not exist.");

            var allObjects = GetAllGameObjects(searchRoot, includeInactive);
            var found = new JArray();

            foreach (var go in allObjects)
            {
                if (go.CompareTag(tag))
                {
                    found.Add(new JObject
                    {
                        ["name"] = go.name,
                        ["path"] = GetGameObjectPath(go),
                        ["active"] = go.activeInHierarchy,
                        ["layer"] = LayerMask.LayerToName(go.layer)
                    });
                }
            }

            return ToolResponse.OkWithData(new
            {
                tag,
                count = found.Count,
                objects = found
            }, $"Found {found.Count} GameObject(s) with tag '{tag}'.");
        }

        /// <summary>
        /// Collects all GameObjects on a specific layer.
        /// </summary>
        private ToolResponse HandleCollectByLayer(JObject parameters)
        {
            string layerStr = ToolHelpers.GetRequiredString(parameters, "layer");
            bool includeInactive = ToolHelpers.GetOptionalBool(parameters, "include_inactive", true);
            string searchRoot = ToolHelpers.GetOptionalString(parameters, "search_root");

            int layerIndex = ResolveLayer(layerStr);
            if (layerIndex < 0)
                return ToolResponse.Fail($"Layer '{layerStr}' not found. Use a valid layer name or index (0-31).");

            var allObjects = GetAllGameObjects(searchRoot, includeInactive);
            var found = new JArray();

            foreach (var go in allObjects)
            {
                if (go.layer == layerIndex)
                {
                    found.Add(new JObject
                    {
                        ["name"] = go.name,
                        ["path"] = GetGameObjectPath(go),
                        ["active"] = go.activeInHierarchy,
                        ["tag"] = go.tag
                    });
                }
            }

            string layerName = LayerMask.LayerToName(layerIndex);
            return ToolResponse.OkWithData(new
            {
                layer = layerName,
                layerIndex,
                count = found.Count,
                objects = found
            }, $"Found {found.Count} GameObject(s) on layer '{layerName}' ({layerIndex}).");
        }

        /// <summary>
        /// Takes a snapshot of the scene hierarchy as a JSON tree.
        /// </summary>
        private ToolResponse HandleSnapshotHierarchy(JObject parameters)
        {
            int maxDepth = ToolHelpers.GetOptionalInt(parameters, "max_depth", 10);
            maxDepth = Mathf.Clamp(maxDepth, 1, 20);
            string searchRoot = ToolHelpers.GetOptionalString(parameters, "search_root");
            bool includeInactive = ToolHelpers.GetOptionalBool(parameters, "include_inactive", true);

            var scene = SceneManager.GetActiveScene();
            var rootObjects = scene.GetRootGameObjects();

            var tree = new JArray();
            int totalCount = 0;

            if (!string.IsNullOrEmpty(searchRoot))
            {
                var rootGo = ToolHelpers.FindGameObject(searchRoot);
                if (rootGo == null)
                    return ToolResponse.Fail($"Root GameObject '{searchRoot}' not found.");

                tree.Add(BuildHierarchyNode(rootGo, 0, maxDepth, includeInactive, ref totalCount));
            }
            else
            {
                foreach (var root in rootObjects)
                {
                    if (!includeInactive && !root.activeInHierarchy) continue;
                    tree.Add(BuildHierarchyNode(root, 0, maxDepth, includeInactive, ref totalCount));
                }
            }

            return ToolResponse.OkWithData(new
            {
                sceneName = scene.name,
                scenePath = scene.path,
                rootCount = rootObjects.Length,
                totalObjects = totalCount,
                maxDepth,
                hierarchy = tree
            }, $"Scene '{scene.name}': {totalCount} GameObject(s) in hierarchy snapshot.");
        }

        /// <summary>
        /// Adds a component to multiple GameObjects.
        /// </summary>
        private ToolResponse HandleBatchAddComponent(JObject parameters)
        {
            var targets = GetTargetGameObjects(parameters, out string error);
            if (error != null) return ToolResponse.Fail(error);
            if (targets.Count == 0) return ToolResponse.Fail("No GameObjects found matching the specified targets.");

            string componentType = ToolHelpers.GetRequiredString(parameters, "component_type");
            bool dryRun = ToolHelpers.GetOptionalBool(parameters, "dry_run", false);

            var type = ToolHelpers.ResolveComponentType(componentType);
            if (type == null)
                return ToolResponse.Fail($"Component type '{componentType}' not found.");

            int added = 0, skipped = 0;
            var skippedNames = new List<string>();

            foreach (var go in targets)
            {
                if (go.GetComponent(type) != null)
                {
                    skipped++;
                    skippedNames.Add(go.name);
                    continue;
                }

                if (!dryRun)
                {
                    ToolHelpers.RecordUndo(go, $"Add {componentType}");
                    go.AddComponent(type);
                    EditorUtility.SetDirty(go);
                    added++;
                }
                else
                {
                    added++;
                }
            }

            string summary = dryRun
                ? $"[DRY RUN] Would add '{componentType}' to {added} GameObject(s) ({skipped} already have it)."
                : $"Added '{componentType}' to {added} GameObject(s) ({skipped} already had it, skipped).";

            return ToolResponse.OkWithData(new
            {
                componentType,
                added,
                skipped,
                skippedObjects = skippedNames,
                dryRun
            }, summary);
        }

        /// <summary>
        /// Removes a component from multiple GameObjects.
        /// </summary>
        private ToolResponse HandleBatchRemoveComponent(JObject parameters)
        {
            var targets = GetTargetGameObjects(parameters, out string error);
            if (error != null) return ToolResponse.Fail(error);
            if (targets.Count == 0) return ToolResponse.Fail("No GameObjects found matching the specified targets.");

            string componentType = ToolHelpers.GetRequiredString(parameters, "component_type");
            bool dryRun = ToolHelpers.GetOptionalBool(parameters, "dry_run", false);

            var type = ToolHelpers.ResolveComponentType(componentType);
            if (type == null)
                return ToolResponse.Fail($"Component type '{componentType}' not found.");

            int removed = 0, notFound = 0;
            var notFoundNames = new List<string>();

            foreach (var go in targets)
            {
                var comp = go.GetComponent(type);
                if (comp == null)
                {
                    notFound++;
                    notFoundNames.Add(go.name);
                    continue;
                }

                if (!dryRun)
                {
                    ToolHelpers.RecordUndo(comp, $"Remove {componentType}");
                    UnityEngine.Object.DestroyImmediate(comp);
                    EditorUtility.SetDirty(go);
                    removed++;
                }
                else
                {
                    removed++;
                }
            }

            string summary = dryRun
                ? $"[DRY RUN] Would remove '{componentType}' from {removed} GameObject(s) ({notFound} don't have it)."
                : $"Removed '{componentType}' from {removed} GameObject(s) ({notFound} didn't have it).";

            return ToolResponse.OkWithData(new
            {
                componentType,
                removed,
                notFound,
                notFoundObjects = notFoundNames,
                dryRun
            }, summary);
        }

        /// <summary>
        /// Moves multiple GameObjects to a new parent.
        /// </summary>
        private ToolResponse HandleBatchMoveToParent(JObject parameters)
        {
            var targets = GetTargetGameObjects(parameters, out string error);
            if (error != null) return ToolResponse.Fail(error);
            if (targets.Count == 0) return ToolResponse.Fail("No GameObjects found matching the specified targets.");

            string parentName = ToolHelpers.GetOptionalString(parameters, "parent_name");
            bool dryRun = ToolHelpers.GetOptionalBool(parameters, "dry_run", false);

            Transform newParent = null;
            if (!string.IsNullOrEmpty(parentName))
            {
                var parentGo = ToolHelpers.FindGameObject(parentName);
                if (parentGo == null)
                    return ToolResponse.Fail($"Parent GameObject '{parentName}' not found.");
                newParent = parentGo.transform;
            }

            int moved = 0;
            var results = new JArray();

            foreach (var go in targets)
            {
                // Prevent moving a parent into its own child
                if (newParent != null && newParent.IsChildOf(go.transform))
                {
                    results.Add(new JObject
                    {
                        ["name"] = go.name,
                        ["status"] = "skipped",
                        ["reason"] = "Cannot move a parent into its own child"
                    });
                    continue;
                }

                string oldPath = GetGameObjectPath(go);

                if (!dryRun)
                {
                    ToolHelpers.RecordUndo(go.transform, "Batch Move To Parent");
                    go.transform.SetParent(newParent, true);
                    EditorUtility.SetDirty(go);
                    moved++;
                }
                else
                {
                    moved++;
                }

                results.Add(new JObject
                {
                    ["name"] = go.name,
                    ["oldPath"] = oldPath,
                    ["newParent"] = parentName ?? "(root)",
                    ["status"] = dryRun ? "would_move" : "moved"
                });
            }

            string summary = dryRun
                ? $"[DRY RUN] Would move {moved} GameObject(s) to parent '{parentName ?? "(root)"}'."
                : $"Moved {moved} GameObject(s) to parent '{parentName ?? "(root)"}'.";

            return ToolResponse.OkWithData(new
            {
                newParent = parentName ?? "(root)",
                total = targets.Count,
                moved,
                dryRun,
                results
            }, summary);
        }

        /// <summary>
        /// Returns statistics about GameObjects in the scene.
        /// </summary>
        private ToolResponse HandleCountObjects(JObject parameters)
        {
            bool includeInactive = ToolHelpers.GetOptionalBool(parameters, "include_inactive", true);
            string searchRoot = ToolHelpers.GetOptionalString(parameters, "search_root");

            var allObjects = GetAllGameObjects(searchRoot, includeInactive);

            // Count by tag
            var byTag = allObjects
                .GroupBy(go => go.tag)
                .OrderByDescending(g => g.Count())
                .Take(20)
                .ToDictionary(g => g.Key, g => g.Count());

            // Count by layer
            var byLayer = allObjects
                .GroupBy(go => LayerMask.LayerToName(go.layer))
                .OrderByDescending(g => g.Count())
                .Take(20)
                .ToDictionary(g => g.Key, g => g.Count());

            // Count active vs inactive
            int activeCount = allObjects.Count(go => go.activeInHierarchy);
            int inactiveCount = allObjects.Count - activeCount;

            // Count with/without children
            int withChildren = allObjects.Count(go => go.transform.childCount > 0);
            int withoutChildren = allObjects.Count - withChildren;

            // Count by component presence (top components)
            var componentCounts = new Dictionary<string, int>();
            foreach (var go in allObjects)
            {
                foreach (var comp in go.GetComponents<Component>())
                {
                    if (comp == null) continue;
                    string typeName = comp.GetType().Name;
                    if (!componentCounts.ContainsKey(typeName))
                        componentCounts[typeName] = 0;
                    componentCounts[typeName]++;
                }
            }
            var topComponents = componentCounts
                .OrderByDescending(kv => kv.Value)
                .Take(15)
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            var scene = SceneManager.GetActiveScene();

            return ToolResponse.OkWithData(new
            {
                sceneName = scene.name,
                searchRoot = searchRoot ?? "(entire scene)",
                total = allObjects.Count,
                active = activeCount,
                inactive = inactiveCount,
                withChildren,
                withoutChildren,
                byTag,
                byLayer,
                topComponents
            }, $"Scene '{scene.name}': {allObjects.Count} total GameObjects ({activeCount} active, {inactiveCount} inactive).");
        }

        /// <summary>
        /// Lists all scenes in the project (build settings + all .unity files).
        /// </summary>
        private ToolResponse HandleListScenes()
        {
            // Scenes in build settings
            var buildScenes = new JArray();
            for (int i = 0; i < EditorBuildSettings.scenes.Length; i++)
            {
                var scene = EditorBuildSettings.scenes[i];
                buildScenes.Add(new JObject
                {
                    ["index"] = i,
                    ["path"] = scene.path,
                    ["name"] = Path.GetFileNameWithoutExtension(scene.path),
                    ["enabled"] = scene.enabled
                });
            }

            // All .unity files in project
            var allSceneGuids = AssetDatabase.FindAssets("t:Scene");
            var allScenes = new JArray();
            foreach (var guid in allSceneGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                allScenes.Add(new JObject
                {
                    ["path"] = path,
                    ["name"] = Path.GetFileNameWithoutExtension(path),
                    ["inBuildSettings"] = buildScenes.Any(s => s["path"]?.ToString() == path)
                });
            }

            // Currently open scenes
            var openScenes = new JArray();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                openScenes.Add(new JObject
                {
                    ["name"] = scene.name,
                    ["path"] = scene.path,
                    ["isLoaded"] = scene.isLoaded,
                    ["isDirty"] = scene.isDirty,
                    ["rootCount"] = scene.rootCount
                });
            }

            return ToolResponse.OkWithData(new
            {
                buildSettingsCount = buildScenes.Count,
                totalInProject = allScenes.Count,
                openCount = openScenes.Count,
                buildSettingsScenes = buildScenes,
                openScenes,
                allProjectScenes = allScenes
            }, $"Project has {allScenes.Count} scene(s) total, {buildScenes.Count} in build settings, {openScenes.Count} currently open.");
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Resolves the targets parameter to a list of GameObjects.
        /// </summary>
        private static List<GameObject> GetTargetGameObjects(JObject parameters, out string error)
        {
            error = null;
            var targetsToken = ToolHelpers.GetOptionalArray(parameters, "targets");
            if (targetsToken == null || targetsToken.Count == 0)
            {
                error = "targets array is required and must not be empty. Provide an array of GameObject names or paths.";
                return null;
            }

            var result = new List<GameObject>();
            var notFound = new List<string>();

            foreach (var token in targetsToken)
            {
                string name = token.Value<string>();
                if (string.IsNullOrEmpty(name)) continue;

                var go = ToolHelpers.FindGameObject(name);
                if (go != null)
                    result.Add(go);
                else
                    notFound.Add(name);
            }

            if (result.Count == 0 && notFound.Count > 0)
            {
                error = $"None of the specified GameObjects were found: {string.Join(", ", notFound.Take(5))}";
                return null;
            }

            return result;
        }

        /// <summary>
        /// Gets all GameObjects in the scene, optionally filtered by root and active state.
        /// </summary>
        private static List<GameObject> GetAllGameObjects(string searchRoot, bool includeInactive)
        {
            var result = new List<GameObject>();

            if (!string.IsNullOrEmpty(searchRoot))
            {
                var rootGo = ToolHelpers.FindGameObject(searchRoot);
                if (rootGo != null)
                    CollectAllChildren(rootGo.transform, result, includeInactive);
                return result;
            }

            var scene = SceneManager.GetActiveScene();
            foreach (var root in scene.GetRootGameObjects())
            {
                if (!includeInactive && !root.activeInHierarchy) continue;
                result.Add(root);
                CollectAllChildren(root.transform, result, includeInactive);
            }

            return result;
        }

        private static void CollectAllChildren(Transform parent, List<GameObject> result, bool includeInactive)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (!includeInactive && !child.gameObject.activeInHierarchy) continue;
                result.Add(child.gameObject);
                CollectAllChildren(child, result, includeInactive);
            }
        }

        /// <summary>
        /// Builds a hierarchy node for snapshot_hierarchy.
        /// </summary>
        private static JObject BuildHierarchyNode(GameObject go, int depth, int maxDepth, bool includeInactive, ref int totalCount)
        {
            totalCount++;
            var node = new JObject
            {
                ["name"] = go.name,
                ["active"] = go.activeSelf,
                ["activeInHierarchy"] = go.activeInHierarchy,
                ["tag"] = go.tag,
                ["layer"] = LayerMask.LayerToName(go.layer),
                ["childCount"] = go.transform.childCount,
                ["components"] = new JArray(go.GetComponents<Component>()
                    .Where(c => c != null)
                    .Select(c => c.GetType().Name)
                    .ToArray())
            };

            if (depth < maxDepth && go.transform.childCount > 0)
            {
                var children = new JArray();
                for (int i = 0; i < go.transform.childCount; i++)
                {
                    var child = go.transform.GetChild(i).gameObject;
                    if (!includeInactive && !child.activeInHierarchy) continue;
                    children.Add(BuildHierarchyNode(child, depth + 1, maxDepth, includeInactive, ref totalCount));
                }
                node["children"] = children;
            }
            else if (go.transform.childCount > 0)
            {
                node["childrenTruncated"] = true;
            }

            return node;
        }

        /// <summary>
        /// Returns the full path of a GameObject in the hierarchy.
        /// </summary>
        private static string GetGameObjectPath(GameObject go)
        {
            var path = go.name;
            var parent = go.transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }

        /// <summary>
        /// Resolves a layer name or index string to a layer index.
        /// </summary>
        private static int ResolveLayer(string layerStr)
        {
            if (int.TryParse(layerStr, out int idx) && idx >= 0 && idx <= 31)
                return idx;

            int byName = LayerMask.NameToLayer(layerStr);
            return byName; // returns -1 if not found
        }

        /// <summary>
        /// Checks if a tag exists in the project.
        /// </summary>
        private static bool IsValidTag(string tag)
        {
            if (tag == "Untagged") return true;
            return UnityEditorInternal.InternalEditorUtility.tags.Contains(tag);
        }

        /// <summary>
        /// Resolves a static flags string to StaticEditorFlags.
        /// </summary>
#pragma warning disable CS0618 // NavigationStatic and OffMeshLinkGeneration are obsolete but still functional for legacy NavMesh workflows
        private static StaticEditorFlags ResolveStaticFlags(string flagsStr)
        {
            switch (flagsStr)
            {
                case "none":
                    return 0;
                case "batching":
                    return StaticEditorFlags.BatchingStatic;
                case "navigation":
                    return StaticEditorFlags.NavigationStatic;
                case "occluder":
                    return StaticEditorFlags.OccluderStatic;
                case "occludee":
                    return StaticEditorFlags.OccludeeStatic;
                case "reflection":
                    return StaticEditorFlags.ReflectionProbeStatic;
                case "lightmap":
                    return StaticEditorFlags.ContributeGI;
                case "off_mesh_link":
                    return StaticEditorFlags.OffMeshLinkGeneration;
                case "all":
                default:
                    return StaticEditorFlags.BatchingStatic
                         | StaticEditorFlags.NavigationStatic
                         | StaticEditorFlags.OccluderStatic
                         | StaticEditorFlags.OccludeeStatic
                         | StaticEditorFlags.ReflectionProbeStatic
                         | StaticEditorFlags.ContributeGI
                         | StaticEditorFlags.OffMeshLinkGeneration;
            }
        }
#pragma warning restore CS0618

        #endregion
    }
}
