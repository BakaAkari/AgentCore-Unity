using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Tools.Safety;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Tools.Native.Extended
{
    /// <summary>
    /// Smart operations tool — v1.7.25 大瘦身,只保留 replace_objects 一个高价值特化 action。
    /// v1.7.25 删除的 6 个 action(align_objects, distribute_objects, snap_to_grid, align_to_ground,
    /// randomize_transform, select_by_criteria) 全部可用 execute_code:run 一段 3-8 行 C# 覆盖:
    ///   align: LINQ Min/Max/Average + foreach 赋值
    ///   distribute: 计算等距间隔 + foreach 赋值
    ///   snap_to_grid: Mathf.Round(x/g)*g 三行
    ///   align_to_ground: Physics.Raycast Vector3.down 五行
    ///   randomize: UnityEngine.Random.Range 八行
    ///   select_by_criteria: 与 find_gameobjects 工具完全重复(searchTerm/tag/layer/componentType/activeOnly 四维过滤已覆盖 name_contains/tag/layer/component,static_only 用 execute_code .Where(g=>g.isStatic) 一行补)
    /// 保留的 replace_objects 有独特工程价值:PrefabUtility.InstantiatePrefab + 保 transform/parent/sibling index + Undo group,agent 用 execute_code 现拼 15+ 行且易漏 undo/sibling。
    /// </summary>
    [AgentTool("smart_operations",
        Description = "Replace scene GameObjects with a prefab while preserving position/rotation/scale/parent/sibling index and grouping the swap into a single Undo. " +
                      "This is the only action here — align/distribute/snap/randomize/select_by_criteria were removed in v1.7.25 because execute_code:run covers them in 3-8 lines of C# (LINQ Min/Max/Average + foreach / Mathf.Round / Physics.Raycast / UnityEngine.Random.Range). " +
                      "select_by_criteria was a duplicate of find_gameobjects (searchTerm+tag+layer+componentType+activeOnly). " +
                      "USE FOR: swapping placeholder GameObjects with final prefabs in bulk (level design finalization). " +
                      "NOT FOR: single object positioning (use manage_gameobject set_transform); scene queries (use find_gameobjects); arbitrary geometry (use execute_code:run).",
        Category = "Extended",
        RequiresMainThread = true,
        Visibility = ToolVisibility.OnDemand,
        RiskLevel = ToolRiskLevel.Medium,
        // replace_objects: 批量实例化 Prefab 并销毁原 GameObject (单个 Undo 组, 可撤销)
        Capabilities = ToolCapability.ReadProject | ToolCapability.ModifyScene,
        // 风险标注只用于确认流程，不改变 Play Mode 行为：本工具不受 Play Mode 写拦截（与标注前一致）
        PlaymodeRuntimeSafeActions = new[] { "*" })]
    public class SmartOperationsTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""replace_objects""],
                    ""description"": ""Only action available: replace_objects. Other spatial batch operations (align, distribute, snap, randomize, select) were removed in v1.7.25 — use execute_code:run instead.""
                },
                ""names"": {
                    ""type"": ""string"",
                    ""description"": ""Comma-separated GameObject names to replace (each will be swapped with an instance of prefab_path).""
                },
                ""prefab_path"": {
                    ""type"": ""string"",
                    ""description"": ""Prefab asset path to instantiate (e.g. 'Assets/Prefabs/Tree.prefab'). Original transform/parent/sibling index are preserved on each replacement.""
                }
            },
            ""required"": [""action"", ""names"", ""prefab_path""]
        }");

        /// <summary>
        /// Tool metadata for auto-discovery registration.
        /// </summary>
        public ToolMetadata Metadata => new ToolMetadata(
            name: "smart_operations",
            description: "Replace scene GameObjects with a prefab while preserving transform/parent/sibling index (single Undo group). Other spatial batch ops removed in v1.7.25 — use execute_code:run.",
            category: "Extended",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        /// <summary>
        /// Execute the smart operation action specified in parameters.
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
                    case "replace_objects":
                        response = HandleReplaceObjects(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: replace_objects. " +
                            $"Actions removed in v1.7.25 (use execute_code:run instead): align_objects (LINQ Min/Max/Average + foreach), distribute_objects (等距间隔 + foreach), snap_to_grid (Mathf.Round(x/g)*g), align_to_ground (Physics.Raycast Vector3.down), randomize_transform (UnityEngine.Random.Range), select_by_criteria (use find_gameobjects tool instead — supports searchTerm/tag/layer/componentType/activeOnly).");
                        break;
                }
            }
            catch (Exception ex)
            {
                response = ToolResponse.Fail($"Error: {ex.Message}");
            }

            sw.Stop();
            return Task.FromResult(response.ToToolResult(sw.Elapsed.TotalMilliseconds));
        }

        #region Action Handlers

        /// <summary>
        /// Replace multiple GameObjects with instances of a Prefab, preserving transforms.
        /// </summary>
        private ToolResponse HandleReplaceObjects(JObject parameters)
        {
            string namesStr = ToolHelpers.GetRequiredString(parameters, "names");
            string prefabPath = ToolHelpers.GetRequiredString(parameters, "prefab_path");

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
                return ToolResponse.Fail($"Prefab not found at path: {prefabPath}");

            var targets = ResolveGameObjects(namesStr);
            if (targets.All(t => t.GameObject == null))
                return ToolResponse.Fail($"No GameObjects found matching the provided names: {string.Join(", ", targets.Select(t => t.Name))}");

            Undo.SetCurrentGroupName("Replace Objects with Prefab");
            int undoGroup = Undo.GetCurrentGroup();

            int replaced = 0;
            var replacedNames = new List<string>();
            // 逐个名字记录结果：找不到 / 重复 / 实例化失败 / 异常都如实列出，不再整体报成功
            var items = new List<ToolItemResult>();
            var seen = new HashSet<GameObject>();

            foreach (var (requestedName, go) in targets)
            {
                if (go == null)
                {
                    items.Add(ToolItemResult.Failed(requestedName, "GameObject not found"));
                    continue;
                }
                if (!seen.Add(go))
                {
                    items.Add(ToolItemResult.Failed(requestedName, "resolves to the same GameObject as an earlier name; already replaced"));
                    continue;
                }

                // 必须在销毁原物体之前取名字：销毁后访问 go.name 会抛 MissingReferenceException
                var originalName = go.name;
                GameObject instance = null;
                try
                {
                    var pos = go.transform.position;
                    var rot = go.transform.rotation;
                    var scale = go.transform.localScale;
                    var parent = go.transform.parent;
                    int siblingIndex = go.transform.GetSiblingIndex();

                    // Instantiate prefab
                    instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                    if (instance == null)
                    {
                        items.Add(ToolItemResult.Failed(requestedName, "PrefabUtility.InstantiatePrefab returned null; original left unchanged"));
                        continue;
                    }

                    Undo.RegisterCreatedObjectUndo(instance, "Create Prefab Instance");

                    instance.transform.position = pos;
                    instance.transform.rotation = rot;
                    instance.transform.localScale = scale;
                    if (parent != null)
                        instance.transform.SetParent(parent);
                    instance.transform.SetSiblingIndex(siblingIndex);

                    // Destroy original
                    Undo.DestroyObjectImmediate(go);

                    replaced++;
                    replacedNames.Add(originalName);
                    items.Add(ToolItemResult.Succeeded(requestedName, new { instance = instance.name }));
                }
                catch (Exception ex)
                {
                    // 记录失败并继续处理其余对象；若实例已创建而原物体仍在，如实说明
                    var leftover = instance != null && go != null
                        ? $" A prefab instance '{instance.name}' was already created and the original still exists (undo with Ctrl+Z or delete manually)."
                        : string.Empty;
                    items.Add(ToolItemResult.Failed(requestedName, ex.Message + leftover));
                }
            }

            Undo.CollapseUndoOperations(undoGroup);

            var data = new
            {
                replacedCount = replaced,
                prefabPath,
                replacedObjects = replacedNames.ToArray()
            };
            return ToolResponse.FromItems($"Replaced {replaced} of {targets.Count} objects with prefab '{prefabPath}'", items, data);
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Resolve a comma-separated list of names to GameObjects.
        /// 保留每个请求的名字，找不到时 GameObject 为 null，便于逐项回报。
        /// </summary>
        private List<(string Name, GameObject GameObject)> ResolveGameObjects(string namesStr)
        {
            var names = namesStr.Split(',')
                .Select(n => n.Trim())
                .Where(n => !string.IsNullOrEmpty(n))
                .ToArray();

            var result = new List<(string Name, GameObject GameObject)>();
            foreach (var name in names)
                result.Add((name, ToolHelpers.FindGameObject(name)));
            return result;
        }
        #endregion
    }
}
