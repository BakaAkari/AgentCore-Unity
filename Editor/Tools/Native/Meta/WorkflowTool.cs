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
    /// Workflow automation tool — batch rename / find-replace name / snapshot hierarchy.
    /// v1.7.24: 从 15 action 瘦身到 3 保留 action。删除的 12 个 batch/collect action
    /// (batch_set_tag/layer/active/static, collect_by_component/tag/layer,
    ///  batch_add/remove_component, batch_move_to_parent, count_objects, list_scenes)
    /// 均可用 execute_code:run 一段 3-8 行 C# 完整覆盖，无独特语义价值。
    /// 保留的三个 action 有 execute_code:run 无法优雅表达的独特价值:
    ///   - batch_rename: {index}/{name}/{parent}/{index:00} 占位符 pattern 语义
    ///   - find_replace_name: 场景全量遍历 + 正则/文本 find-replace + dry_run 预览
    ///   - snapshot_hierarchy: 递归深度限制 + 结构化 JSON 树输出 (agent 一次消化整场景结构)
    /// </summary>
    [AgentTool("workflow",
        Description = "Focused workflow automation for three high-value repetitive tasks: batch_rename (pattern-based rename with {index}/{name}/{parent}/{index:00} placeholders), find_replace_name (regex or plain-text search-and-replace over scene hierarchy names with dry_run preview), snapshot_hierarchy (structured JSON tree dump with depth limit). " +
            "For all other bulk operations (set tag/layer/active/static on N objects, collect by component/tag/layer, add/remove component in bulk, reparent many, count, list scenes) use execute_code:run — 3-8 lines of C# with LINQ + FindObjectsByType covers them without adding tool-specific actions here. " +
            "NOT for: single object operations (use manage_gameobject), code editing (use manage_script), arbitrary custom logic (use execute_code:run).",
        Category = "Meta",
        RequiresMainThread = true,
        MayModifyScripts = false,
        RiskLevel = ToolRiskLevel.High,
        Capabilities = ToolCapability.ModifyScene | ToolCapability.BatchExecute,
        ReadOnlyActions = new[] { "snapshot_hierarchy" })]
    public class WorkflowTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [
                        ""batch_rename"",
                        ""find_replace_name"",
                        ""snapshot_hierarchy""
                    ],
                    ""description"": ""Workflow action: batch_rename (rename many GameObjects with a {index}/{name}/{parent} pattern), find_replace_name (regex or plain-text search-and-replace over names across the scene), snapshot_hierarchy (dump scene tree to structured JSON with depth limit).""
                },
                ""targets"": {
                    ""type"": ""array"",
                    ""items"": { ""type"": ""string"" },
                    ""description"": ""Array of GameObject names or paths (used by batch_rename).""
                },
                ""pattern"": {
                    ""type"": ""string"",
                    ""description"": ""Name pattern for batch_rename: use {index} for numbering, {name} for original name, {parent} for parent name, {index:00} for zero-padded numbering. E.g. 'Enemy_{index:00}' or '{name}_copy'.""
                },
                ""find"": {
                    ""type"": ""string"",
                    ""description"": ""Text or regex to find in names for find_replace_name.""
                },
                ""replace"": {
                    ""type"": ""string"",
                    ""description"": ""Replacement text for find_replace_name.""
                },
                ""search_root"": {
                    ""type"": ""string"",
                    ""description"": ""Root GameObject name to limit search scope for find_replace_name and snapshot_hierarchy (empty = entire scene).""
                },
                ""include_inactive"": {
                    ""type"": ""boolean"",
                    ""description"": ""Include inactive GameObjects in find_replace_name and snapshot_hierarchy traversal (default: true).""
                },
                ""max_depth"": {
                    ""type"": ""integer"",
                    ""description"": ""Maximum hierarchy depth for snapshot_hierarchy (default: 10, clamped to [1,20]).""
                },
                ""start_index"": {
                    ""type"": ""integer"",
                    ""description"": ""Starting index for batch_rename numbering (default: 0).""
                },
                ""use_regex"": {
                    ""type"": ""boolean"",
                    ""description"": ""Use regex for find_replace_name (default: false, plain text).""
                },
                ""dry_run"": {
                    ""type"": ""boolean"",
                    ""description"": ""Preview changes without applying them, for batch_rename and find_replace_name (default: false).""
                }
            },
            ""required"": [""action""]
        }");

        /// <summary>
        /// Tool metadata for auto-discovery registration.
        /// </summary>
        public ToolMetadata Metadata => new ToolMetadata(
            name: "workflow",
            description: "Focused workflow automation: batch_rename (pattern), find_replace_name (regex/text), snapshot_hierarchy (JSON tree). For other bulk operations use execute_code:run.",
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
                    case "find_replace_name":
                        response = HandleFindReplaceName(parameters);
                        break;
                    case "snapshot_hierarchy":
                        response = HandleSnapshotHierarchy(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: batch_rename, find_replace_name, snapshot_hierarchy. " +
                            $"Other bulk operations (batch_set_tag/layer/active/static, collect_by_component/tag/layer, batch_add/remove_component, batch_move_to_parent, count_objects, list_scenes) were removed in v1.7.24 — use execute_code:run with 3-8 lines of C# instead.");
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

        #endregion

        #region Helpers

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
        #endregion
    }
}
