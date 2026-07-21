using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Tools.Native.Extended
{
    /// <summary>
    /// Smart batch operations for GameObjects — align, distribute, snap,
    /// randomize transforms, replace objects, and select by criteria.
    /// </summary>
    [AgentTool("smart_operations",
        Description = "Spatial batch operations on GameObjects — precision layout and transform manipulation. " +
                      "Actions: align_objects (align position/rotation to reference axis), distribute_objects (even spacing along axis), " +
                      "snap_to_grid (snap positions to world grid), align_to_ground (raycast down to place on terrain/collider surface), " +
                      "randomize_transform (randomize position/rotation/scale within ranges — useful for natural-looking placement), " +
                      "replace_objects (swap GameObjects with a prefab while preserving transforms), " +
                      "select_by_criteria (select objects matching component/name/layer/tag criteria). " +
                      "USE FOR: level design workflows (arranging props, distributing objects evenly, snapping to grid), " +
                      "randomizing vegetation/decoration placement, replacing placeholder objects with final prefabs. " +
                      "NOT FOR: individual object positioning (use manage_gameobject set_transform), " +
                      "batch rename/tag/layer (use workflow tool). " +
                      "ACTIVATE WHEN: user mentions 'align', 'distribute evenly', 'snap to grid', 'place on ground', 'randomize positions', 'replace objects'.",
        Category = "Extended",
        RequiresMainThread = true,
        Visibility = ToolVisibility.OnDemand)]
    public class SmartOperationsTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""align_objects"", ""distribute_objects"", ""snap_to_grid"", ""align_to_ground"", ""randomize_transform"", ""replace_objects"", ""select_by_criteria""],
                    ""description"": ""Action to perform""
                },
                ""names"": { ""type"": ""string"", ""description"": ""Comma-separated GameObject names (for align_objects, distribute_objects, replace_objects)"" },
                ""name"": { ""type"": ""string"", ""description"": ""Target GameObject name (for snap_to_grid, align_to_ground, randomize_transform)"" },
                ""axis"": { ""type"": ""string"", ""description"": ""Axis to align/distribute along: x, y, or z"" },
                ""mode"": { ""type"": ""string"", ""description"": ""Alignment mode: min, max, center, average (default: center)"" },
                ""grid_size"": { ""type"": ""number"", ""description"": ""Grid size for snap_to_grid (default: 1.0)"" },
                ""offset"": { ""type"": ""number"", ""description"": ""Y offset after align_to_ground (default: 0)"" },
                ""layer_mask"": { ""type"": ""integer"", ""description"": ""Layer mask for align_to_ground raycast (optional)"" },
                ""position_range"": { ""type"": ""object"", ""description"": ""Random position offset range {x, y, z} for randomize_transform"", ""properties"": { ""x"": {""type"":""number""}, ""y"": {""type"":""number""}, ""z"": {""type"":""number""} } },
                ""rotation_range"": { ""type"": ""object"", ""description"": ""Random rotation range {x, y, z} in degrees for randomize_transform"", ""properties"": { ""x"": {""type"":""number""}, ""y"": {""type"":""number""}, ""z"": {""type"":""number""} } },
                ""scale_range"": { ""type"": ""object"", ""description"": ""Random uniform scale range {min, max} for randomize_transform"", ""properties"": { ""min"": {""type"":""number""}, ""max"": {""type"":""number""} } },
                ""prefab_path"": { ""type"": ""string"", ""description"": ""Prefab asset path for replace_objects (e.g. Assets/Prefabs/Tree.prefab)"" },
                ""component"": { ""type"": ""string"", ""description"": ""Component type name filter for select_by_criteria"" },
                ""tag"": { ""type"": ""string"", ""description"": ""Tag filter for select_by_criteria"" },
                ""layer"": { ""type"": ""string"", ""description"": ""Layer name filter for select_by_criteria"" },
                ""name_contains"": { ""type"": ""string"", ""description"": ""Name substring filter for select_by_criteria"" },
                ""static_only"": { ""type"": ""boolean"", ""description"": ""Only select static GameObjects (default: false)"" }
            },
            ""required"": [""action""]
        }");

        /// <summary>
        /// Tool metadata for auto-discovery registration.
        /// </summary>
        public ToolMetadata Metadata => new ToolMetadata(
            name: "smart_operations",
            description: "Smart batch operations for GameObjects: align, distribute, snap, randomize transforms, replace objects, and select by criteria",
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
                    case "align_objects":
                        response = HandleAlignObjects(parameters);
                        break;
                    case "distribute_objects":
                        response = HandleDistributeObjects(parameters);
                        break;
                    case "snap_to_grid":
                        response = HandleSnapToGrid(parameters);
                        break;
                    case "align_to_ground":
                        response = HandleAlignToGround(parameters);
                        break;
                    case "randomize_transform":
                        response = HandleRandomizeTransform(parameters);
                        break;
                    case "replace_objects":
                        response = HandleReplaceObjects(parameters);
                        break;
                    case "select_by_criteria":
                        response = HandleSelectByCriteria(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail($"Unknown action: {action}. Valid actions: align_objects, distribute_objects, snap_to_grid, align_to_ground, randomize_transform, replace_objects, select_by_criteria");
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
        /// Align multiple GameObjects along a specified axis using the given alignment mode.
        /// </summary>
        private ToolResponse HandleAlignObjects(JObject parameters)
        {
            string namesStr = ToolHelpers.GetRequiredString(parameters, "names");
            string axis = ToolHelpers.GetRequiredString(parameters, "axis").ToLowerInvariant();
            string mode = ToolHelpers.GetOptionalString(parameters, "mode", "center").ToLowerInvariant();

            if (axis != "x" && axis != "y" && axis != "z")
                return ToolResponse.Fail($"Invalid axis: {axis}. Must be x, y, or z.");

            if (mode != "min" && mode != "max" && mode != "center" && mode != "average")
                return ToolResponse.Fail($"Invalid mode: {mode}. Must be min, max, center, or average.");

            var gameObjects = ResolveGameObjects(namesStr);
            if (gameObjects.Count < 2)
                return ToolResponse.Fail($"Need at least 2 GameObjects to align. Found: {gameObjects.Count}");

            // Calculate target value based on mode
            float targetValue = CalculateAlignTarget(gameObjects, axis, mode);

            // Record undo for all objects
            var transforms = gameObjects.Select(go => go.transform).ToArray();
            Undo.RecordObjects(transforms, "Align Objects");

            int aligned = 0;
            foreach (var go in gameObjects)
            {
                var pos = go.transform.position;
                switch (axis)
                {
                    case "x": pos.x = targetValue; break;
                    case "y": pos.y = targetValue; break;
                    case "z": pos.z = targetValue; break;
                }
                go.transform.position = pos;
                aligned++;
            }

            return ToolResponse.OkWithData(new
            {
                alignedCount = aligned,
                axis,
                mode,
                targetValue = Math.Round(targetValue, 4),
                objects = gameObjects.Select(go => go.name).ToArray()
            }, $"Aligned {aligned} objects along {axis}-axis using '{mode}' mode (target: {targetValue:F4})");
        }

        /// <summary>
        /// Distribute multiple GameObjects evenly along a specified axis.
        /// </summary>
        private ToolResponse HandleDistributeObjects(JObject parameters)
        {
            string namesStr = ToolHelpers.GetRequiredString(parameters, "names");
            string axis = ToolHelpers.GetRequiredString(parameters, "axis").ToLowerInvariant();

            if (axis != "x" && axis != "y" && axis != "z")
                return ToolResponse.Fail($"Invalid axis: {axis}. Must be x, y, or z.");

            var gameObjects = ResolveGameObjects(namesStr);
            if (gameObjects.Count < 3)
                return ToolResponse.Fail($"Need at least 3 GameObjects to distribute. Found: {gameObjects.Count}");

            // Sort by current position along the axis
            gameObjects.Sort((a, b) => GetAxisValue(a.transform.position, axis).CompareTo(GetAxisValue(b.transform.position, axis)));

            float minVal = GetAxisValue(gameObjects[0].transform.position, axis);
            float maxVal = GetAxisValue(gameObjects[gameObjects.Count - 1].transform.position, axis);
            float step = (maxVal - minVal) / (gameObjects.Count - 1);

            // Record undo
            var transforms = gameObjects.Select(go => go.transform).ToArray();
            Undo.RecordObjects(transforms, "Distribute Objects");

            for (int i = 1; i < gameObjects.Count - 1; i++)
            {
                var pos = gameObjects[i].transform.position;
                float newVal = minVal + step * i;
                switch (axis)
                {
                    case "x": pos.x = newVal; break;
                    case "y": pos.y = newVal; break;
                    case "z": pos.z = newVal; break;
                }
                gameObjects[i].transform.position = pos;
            }

            return ToolResponse.OkWithData(new
            {
                distributedCount = gameObjects.Count,
                axis,
                range = new { min = Math.Round(minVal, 4), max = Math.Round(maxVal, 4) },
                step = Math.Round(step, 4),
                objects = gameObjects.Select(go => go.name).ToArray()
            }, $"Distributed {gameObjects.Count} objects evenly along {axis}-axis (step: {step:F4})");
        }

        /// <summary>
        /// Snap a GameObject's position to the nearest grid point.
        /// </summary>
        private ToolResponse HandleSnapToGrid(JObject parameters)
        {
            string name = ToolHelpers.GetRequiredString(parameters, "name");
            float gridSize = ToolHelpers.GetOptionalFloat(parameters, "grid_size", 1.0f);

            if (gridSize <= 0f)
                return ToolResponse.Fail("grid_size must be greater than 0.");

            var go = ToolHelpers.FindGameObject(name);
            if (go == null)
                return ToolResponse.Fail($"GameObject not found: {name}");

            Undo.RecordObject(go.transform, "Snap to Grid");

            var pos = go.transform.position;
            var oldPos = pos;
            pos.x = Mathf.Round(pos.x / gridSize) * gridSize;
            pos.y = Mathf.Round(pos.y / gridSize) * gridSize;
            pos.z = Mathf.Round(pos.z / gridSize) * gridSize;
            go.transform.position = pos;

            return ToolResponse.OkWithData(new
            {
                gameObject = go.name,
                gridSize,
                oldPosition = new { x = Math.Round(oldPos.x, 4), y = Math.Round(oldPos.y, 4), z = Math.Round(oldPos.z, 4) },
                newPosition = new { x = Math.Round(pos.x, 4), y = Math.Round(pos.y, 4), z = Math.Round(pos.z, 4) }
            }, $"Snapped '{go.name}' to grid (size: {gridSize})");
        }

        /// <summary>
        /// Align a GameObject to the ground by raycasting downward.
        /// </summary>
        private ToolResponse HandleAlignToGround(JObject parameters)
        {
            string name = ToolHelpers.GetRequiredString(parameters, "name");
            float offset = ToolHelpers.GetOptionalFloat(parameters, "offset", 0f);
            int layerMask = ToolHelpers.GetOptionalInt(parameters, "layer_mask", -1);

            var go = ToolHelpers.FindGameObject(name);
            if (go == null)
                return ToolResponse.Fail($"GameObject not found: {name}");

            // Raycast from above the object downward
            var origin = go.transform.position + Vector3.up * 1000f;
            RaycastHit hit;
            bool didHit;

            if (layerMask >= 0)
                didHit = Physics.Raycast(origin, Vector3.down, out hit, Mathf.Infinity, layerMask);
            else
                didHit = Physics.Raycast(origin, Vector3.down, out hit, Mathf.Infinity);

            if (!didHit)
                return ToolResponse.Fail($"No ground found below '{name}'. Ensure there are colliders in the scene.");

            Undo.RecordObject(go.transform, "Align to Ground");

            var oldPos = go.transform.position;
            var newPos = new Vector3(oldPos.x, hit.point.y + offset, oldPos.z);
            go.transform.position = newPos;

            return ToolResponse.OkWithData(new
            {
                gameObject = go.name,
                oldY = Math.Round(oldPos.y, 4),
                newY = Math.Round(newPos.y, 4),
                groundHit = hit.collider.gameObject.name,
                offset
            }, $"Aligned '{go.name}' to ground (y: {oldPos.y:F4} → {newPos.y:F4}, hit: '{hit.collider.gameObject.name}')");
        }

        /// <summary>
        /// Randomize a GameObject's transform within specified ranges.
        /// </summary>
        private ToolResponse HandleRandomizeTransform(JObject parameters)
        {
            string name = ToolHelpers.GetRequiredString(parameters, "name");
            var posRange = ToolHelpers.GetOptionalObject(parameters, "position_range");
            var rotRange = ToolHelpers.GetOptionalObject(parameters, "rotation_range");
            var scaleRange = ToolHelpers.GetOptionalObject(parameters, "scale_range");

            if (posRange == null && rotRange == null && scaleRange == null)
                return ToolResponse.Fail("At least one of position_range, rotation_range, or scale_range must be specified.");

            var go = ToolHelpers.FindGameObject(name);
            if (go == null)
                return ToolResponse.Fail($"GameObject not found: {name}");

            Undo.RecordObject(go.transform, "Randomize Transform");

            var changes = new List<string>();

            // Randomize position
            if (posRange != null)
            {
                float rx = posRange["x"]?.Value<float>() ?? 0f;
                float ry = posRange["y"]?.Value<float>() ?? 0f;
                float rz = posRange["z"]?.Value<float>() ?? 0f;

                var pos = go.transform.position;
                pos.x += UnityEngine.Random.Range(-rx, rx);
                pos.y += UnityEngine.Random.Range(-ry, ry);
                pos.z += UnityEngine.Random.Range(-rz, rz);
                go.transform.position = pos;
                changes.Add($"position offset ±({rx},{ry},{rz})");
            }

            // Randomize rotation
            if (rotRange != null)
            {
                float rx = rotRange["x"]?.Value<float>() ?? 0f;
                float ry = rotRange["y"]?.Value<float>() ?? 0f;
                float rz = rotRange["z"]?.Value<float>() ?? 0f;

                var euler = go.transform.eulerAngles;
                euler.x += UnityEngine.Random.Range(-rx, rx);
                euler.y += UnityEngine.Random.Range(-ry, ry);
                euler.z += UnityEngine.Random.Range(-rz, rz);
                go.transform.eulerAngles = euler;
                changes.Add($"rotation offset ±({rx},{ry},{rz})°");
            }

            // Randomize scale
            if (scaleRange != null)
            {
                float minScale = scaleRange["min"]?.Value<float>() ?? 1f;
                float maxScale = scaleRange["max"]?.Value<float>() ?? 1f;

                float s = UnityEngine.Random.Range(minScale, maxScale);
                go.transform.localScale = new Vector3(s, s, s);
                changes.Add($"uniform scale [{minScale},{maxScale}] → {s:F4}");
            }

            return ToolResponse.OkWithData(new
            {
                gameObject = go.name,
                position = new { x = Math.Round(go.transform.position.x, 4), y = Math.Round(go.transform.position.y, 4), z = Math.Round(go.transform.position.z, 4) },
                rotation = new { x = Math.Round(go.transform.eulerAngles.x, 4), y = Math.Round(go.transform.eulerAngles.y, 4), z = Math.Round(go.transform.eulerAngles.z, 4) },
                scale = new { x = Math.Round(go.transform.localScale.x, 4), y = Math.Round(go.transform.localScale.y, 4), z = Math.Round(go.transform.localScale.z, 4) },
                appliedChanges = changes.ToArray()
            }, $"Randomized transform of '{go.name}': {string.Join(", ", changes)}");
        }

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

            var gameObjects = ResolveGameObjects(namesStr);
            if (gameObjects.Count == 0)
                return ToolResponse.Fail("No GameObjects found matching the provided names.");

            Undo.SetCurrentGroupName("Replace Objects with Prefab");
            int undoGroup = Undo.GetCurrentGroup();

            int replaced = 0;
            var replacedNames = new List<string>();

            foreach (var go in gameObjects)
            {
                var pos = go.transform.position;
                var rot = go.transform.rotation;
                var scale = go.transform.localScale;
                var parent = go.transform.parent;
                int siblingIndex = go.transform.GetSiblingIndex();

                // Instantiate prefab
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                if (instance == null) continue;

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
                replacedNames.Add(go.name);
            }

            Undo.CollapseUndoOperations(undoGroup);

            return ToolResponse.OkWithData(new
            {
                replacedCount = replaced,
                prefabPath,
                replacedObjects = replacedNames.ToArray()
            }, $"Replaced {replaced} objects with prefab '{prefabPath}'");
        }

        /// <summary>
        /// Select GameObjects in the scene matching specified criteria.
        /// </summary>
        private ToolResponse HandleSelectByCriteria(JObject parameters)
        {
            string component = ToolHelpers.GetOptionalString(parameters, "component");
            string tag = ToolHelpers.GetOptionalString(parameters, "tag");
            string layer = ToolHelpers.GetOptionalString(parameters, "layer");
            string nameContains = ToolHelpers.GetOptionalString(parameters, "name_contains");
            bool staticOnly = ToolHelpers.GetOptionalBool(parameters, "static_only", false);

            if (string.IsNullOrEmpty(component) && string.IsNullOrEmpty(tag) &&
                string.IsNullOrEmpty(layer) && string.IsNullOrEmpty(nameContains) && !staticOnly)
            {
                return ToolResponse.Fail("At least one filter criterion must be specified: component, tag, layer, name_contains, or static_only.");
            }

            // Resolve layer index if specified
            int layerIndex = -1;
            if (!string.IsNullOrEmpty(layer))
            {
                layerIndex = LayerMask.NameToLayer(layer);
                if (layerIndex < 0)
                    return ToolResponse.Fail($"Layer not found: {layer}");
            }

            // Resolve component type if specified
            Type componentType = null;
            if (!string.IsNullOrEmpty(component))
            {
                componentType = ToolHelpers.ResolveComponentType(component);
                if (componentType == null)
                    return ToolResponse.Fail($"Component type not found: {component}");
            }

            var allObjects = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var matched = new List<GameObject>();

            foreach (var go in allObjects)
            {
                // Filter by component
                if (componentType != null && go.GetComponent(componentType) == null)
                    continue;

                // Filter by tag
                if (!string.IsNullOrEmpty(tag) && !go.CompareTag(tag))
                    continue;

                // Filter by layer
                if (layerIndex >= 0 && go.layer != layerIndex)
                    continue;

                // Filter by name
                if (!string.IsNullOrEmpty(nameContains) && !go.name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Filter by static
                if (staticOnly && !go.isStatic)
                    continue;

                matched.Add(go);
            }

            // Set Unity Editor selection
            Selection.objects = matched.ToArray();

            return ToolResponse.OkWithData(new
            {
                selectedCount = matched.Count,
                filters = new
                {
                    component,
                    tag,
                    layer,
                    name_contains = nameContains,
                    static_only = staticOnly
                },
                objects = matched.Take(100).Select(go => new { name = go.name, path = GetGameObjectPath(go) }).ToArray(),
                truncated = matched.Count > 100
            }, $"Selected {matched.Count} GameObjects matching criteria");
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Resolve a comma-separated list of names to GameObjects.
        /// </summary>
        private List<GameObject> ResolveGameObjects(string namesStr)
        {
            var names = namesStr.Split(',')
                .Select(n => n.Trim())
                .Where(n => !string.IsNullOrEmpty(n))
                .ToArray();

            var result = new List<GameObject>();
            foreach (var name in names)
            {
                var go = ToolHelpers.FindGameObject(name);
                if (go != null)
                    result.Add(go);
            }
            return result;
        }

        /// <summary>
        /// Get the value of a specific axis from a Vector3.
        /// </summary>
        private static float GetAxisValue(Vector3 v, string axis)
        {
            switch (axis)
            {
                case "x": return v.x;
                case "y": return v.y;
                case "z": return v.z;
                default: return 0f;
            }
        }

        /// <summary>
        /// Calculate the alignment target value based on mode.
        /// </summary>
        private float CalculateAlignTarget(List<GameObject> objects, string axis, string mode)
        {
            var values = objects.Select(go => GetAxisValue(go.transform.position, axis)).ToList();

            switch (mode)
            {
                case "min": return values.Min();
                case "max": return values.Max();
                case "center": return (values.Min() + values.Max()) / 2f;
                case "average": return values.Average();
                default: return values.Average();
            }
        }

        /// <summary>
        /// Get the full hierarchy path of a GameObject.
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
