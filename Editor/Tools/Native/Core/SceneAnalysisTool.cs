using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AgentCore.Editor.Tools.Native.Core
{
    /// <summary>
    /// Scene analysis tool — provides comprehensive scene understanding capabilities
    /// including summarization, health checks, component statistics, hotspot detection,
    /// hierarchy visualization, spatial queries, material overview, performance hints,
    /// project info detection, and dependency analysis.
    /// </summary>
    [AgentTool("scene_analysis",
        Description = "Analyze the current Unity scene: summarize, health_check, component_stats, find_hotspots, hierarchy_tree, spatial_query, materials_overview, performance_hints, project_info, dependency_analyze. Read-only analysis — does not modify the scene.",
        Category = "Core",
        RequiresMainThread = true)]
    public class SceneAnalysisTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""summarize"", ""health_check"", ""component_stats"", ""find_hotspots"", ""hierarchy_tree"", ""spatial_query"", ""materials_overview"", ""performance_hints"", ""project_info"", ""dependency_analyze""],
                    ""description"": ""Analysis action to perform""
                },
                ""topN"": {
                    ""type"": ""integer"",
                    ""description"": ""Number of top components to return (summarize/component_stats, default: 10)""
                },
                ""deepThreshold"": {
                    ""type"": ""integer"",
                    ""description"": ""Hierarchy depth threshold for hotspot detection (find_hotspots, default: 10)""
                },
                ""childThreshold"": {
                    ""type"": ""integer"",
                    ""description"": ""Child count threshold for hotspot detection (find_hotspots, default: 20)""
                },
                ""maxResults"": {
                    ""type"": ""integer"",
                    ""description"": ""Maximum results to return (find_hotspots/spatial_query, default: 20)""
                },
                ""maxDepth"": {
                    ""type"": ""integer"",
                    ""description"": ""Maximum hierarchy depth (hierarchy_tree, default: 5)""
                },
                ""includeInactive"": {
                    ""type"": ""boolean"",
                    ""description"": ""Include inactive objects (hierarchy_tree, default: false)""
                },
                ""maxItems"": {
                    ""type"": ""integer"",
                    ""description"": ""Maximum items per level (hierarchy_tree, default: 100)""
                },
                ""center"": {
                    ""type"": ""object"",
                    ""properties"": { ""x"": {""type"":""number""}, ""y"": {""type"":""number""}, ""z"": {""type"":""number""} },
                    ""description"": ""Center point for spatial query (spatial_query)""
                },
                ""nearObject"": {
                    ""type"": ""string"",
                    ""description"": ""Object name/path to use as center (spatial_query, alternative to center)""
                },
                ""radius"": {
                    ""type"": ""number"",
                    ""description"": ""Search radius (spatial_query, default: 10)""
                },
                ""componentFilter"": {
                    ""type"": ""string"",
                    ""description"": ""Filter by component type name (spatial_query)""
                },
                ""target"": {
                    ""type"": ""string"",
                    ""description"": ""Target object name/path (dependency_analyze)""
                }
            },
            ""required"": [""action""]
        }");

        // Cached UI types resolved via reflection (may be null if com.unity.ugui is not installed)
        private static readonly Type _eventSystemType = FindTypeByName("UnityEngine.EventSystems.EventSystem");
        private static readonly Type _uiGraphicType = FindTypeByName("UnityEngine.UI.Graphic");
        private static readonly Type _uiButtonType = FindTypeByName("UnityEngine.UI.Button");
        private static readonly Type _uiTextType = FindTypeByName("UnityEngine.UI.Text");
        private static readonly Type _uiImageType = FindTypeByName("UnityEngine.UI.Image");

        /// <summary>
        /// Tool metadata for registration and LLM discovery.
        /// </summary>
        public ToolMetadata Metadata => new ToolMetadata(
            name: "scene_analysis",
            description: "Analyze the current Unity scene: summarize, health_check, component_stats, find_hotspots, hierarchy_tree, spatial_query, materials_overview, performance_hints, project_info, dependency_analyze. Read-only analysis — does not modify the scene.",
            category: "Core",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        /// <summary>
        /// Execute the scene analysis tool with the specified action.
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
                    case "summarize":
                        response = HandleSummarize(parameters);
                        break;
                    case "health_check":
                        response = HandleHealthCheck(parameters);
                        break;
                    case "component_stats":
                        response = HandleComponentStats(parameters);
                        break;
                    case "find_hotspots":
                        response = HandleFindHotspots(parameters);
                        break;
                    case "hierarchy_tree":
                        response = HandleHierarchyTree(parameters);
                        break;
                    case "spatial_query":
                        response = HandleSpatialQuery(parameters);
                        break;
                    case "materials_overview":
                        response = HandleMaterialsOverview(parameters);
                        break;
                    case "performance_hints":
                        response = HandlePerformanceHints(parameters);
                        break;
                    case "project_info":
                        response = HandleProjectInfo(parameters);
                        break;
                    case "dependency_analyze":
                        response = HandleDependencyAnalyze(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: summarize, health_check, component_stats, find_hotspots, hierarchy_tree, spatial_query, materials_overview, performance_hints, project_info, dependency_analyze");
                        break;
                }
            }
            catch (ArgumentException ex)
            {
                response = ToolResponse.Fail(ex.Message);
            }
            catch (Exception ex)
            {
                response = ToolResponse.Fail($"Scene analysis error: {ex.Message}");
            }

            sw.Stop();
            return Task.FromResult(response.ToToolResult(sw.Elapsed.TotalMilliseconds));
        }

        #region Helper Types

        /// <summary>
        /// Snapshot of scene metrics collected in a single pass.
        /// </summary>
        private class SceneMetrics
        {
            public Scene Scene;
            public List<GameObject> AllObjects;
            public Dictionary<string, int> ComponentCounts = new Dictionary<string, int>();
            public int TotalObjects;
            public int ActiveObjects;
            public int InactiveObjects;
            public int RootObjects;
            public int MaxHierarchyDepth;
            public int Cameras;
            public int MainCameraCount;
            public int Lights;
            public int Canvases;
            public int EventSystems;
            public int AudioListeners;
            public int PrefabInstances;
            public int EmptyLeafCount;
            public bool HasUiGraphic;
            public bool HasUiToolkitDocument;
        }

        #endregion

        #region Scene Metrics Collection

        /// <summary>
        /// Collect comprehensive scene metrics in a single pass over all GameObjects.
        /// </summary>
        private static SceneMetrics CollectSceneMetrics(bool includeComponentStats = true)
        {
            var scene = SceneManager.GetActiveScene();
            var allObjects = GetAllSceneObjects(scene);
            var metrics = new SceneMetrics
            {
                Scene = scene,
                AllObjects = allObjects,
                TotalObjects = allObjects.Count,
                RootObjects = scene.rootCount
            };

            var componentBuffer = new List<Component>(8);
            var uiDocumentType = FindTypeByName("UnityEngine.UIElements.UIDocument");

            foreach (var go in allObjects)
            {
                if (go.activeInHierarchy)
                    metrics.ActiveObjects++;
                else
                    metrics.InactiveObjects++;

                var depth = GetHierarchyDepth(go);
                if (depth > metrics.MaxHierarchyDepth)
                    metrics.MaxHierarchyDepth = depth;

                if (PrefabUtility.IsPartOfPrefabInstance(go) && !PrefabUtility.IsPartOfPrefabAsset(go))
                    metrics.PrefabInstances++;

                componentBuffer.Clear();
                go.GetComponents(componentBuffer);

                // Empty leaf: only Transform, no children
                if (componentBuffer.Count == 1 && go.transform.childCount == 0)
                    metrics.EmptyLeafCount++;

                foreach (var component in componentBuffer)
                {
                    if (component == null) continue;

                    var typeName = component.GetType().Name;

                    if (includeComponentStats)
                    {
                        metrics.ComponentCounts[typeName] =
                            metrics.ComponentCounts.TryGetValue(typeName, out var count) ? count + 1 : 1;
                    }

                    if (component is Camera)
                    {
                        metrics.Cameras++;
                        if (go.CompareTag("MainCamera"))
                            metrics.MainCameraCount++;
                    }
                    else if (component is Light)
                    {
                        metrics.Lights++;
                    }
                    else if (component is Canvas)
                    {
                        metrics.Canvases++;
                    }
                    else if (_eventSystemType != null && _eventSystemType.IsInstanceOfType(component))
                    {
                        metrics.EventSystems++;
                    }
                    else if (component is AudioListener)
                    {
                        metrics.AudioListeners++;
                    }
                    else if (_uiGraphicType != null && _uiGraphicType.IsInstanceOfType(component))
                    {
                        metrics.HasUiGraphic = true;
                    }
                }

                if (uiDocumentType != null && go.GetComponent(uiDocumentType) != null)
                    metrics.HasUiToolkitDocument = true;
            }

            return metrics;
        }

        /// <summary>
        /// Get all GameObjects in the active scene.
        /// </summary>
        private static List<GameObject> GetAllSceneObjects(Scene scene)
        {
            var result = new List<GameObject>();
            if (!scene.IsValid() || !scene.isLoaded) return result;

            var rootObjects = scene.GetRootGameObjects();
            var stack = new Stack<Transform>();

            foreach (var root in rootObjects)
            {
                stack.Push(root.transform);
            }

            while (stack.Count > 0)
            {
                var t = stack.Pop();
                result.Add(t.gameObject);
                for (int i = t.childCount - 1; i >= 0; i--)
                {
                    stack.Push(t.GetChild(i));
                }
            }

            return result;
        }

        /// <summary>
        /// Get the hierarchy depth of a GameObject (0 = root).
        /// </summary>
        private static int GetHierarchyDepth(GameObject go)
        {
            int depth = 0;
            var t = go.transform.parent;
            while (t != null)
            {
                depth++;
                t = t.parent;
            }
            return depth;
        }

        /// <summary>
        /// Get the full hierarchy path of a GameObject.
        /// </summary>
        private static string GetHierarchyPath(GameObject go)
        {
            var parts = new List<string>();
            var t = go.transform;
            while (t != null)
            {
                parts.Add(t.name);
                t = t.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        /// <summary>
        /// Find a Type by its full name across all loaded assemblies.
        /// </summary>
        private static Type FindTypeByName(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetType(fullName, false);
                    if (type != null) return type;
                }
                catch
                {
                    // Ignore assembly load failures
                }
            }
            return null;
        }

        #endregion

        #region Action: summarize

        /// <summary>
        /// Generate a structured summary of the current scene.
        /// </summary>
        private ToolResponse HandleSummarize(JObject parameters)
        {
            int topN = ToolHelpers.GetOptionalInt(parameters, "topN", 10);
            var metrics = CollectSceneMetrics(includeComponentStats: true);

            // Build top components (excluding Transform)
            var topComponents = metrics.ComponentCounts
                .Where(kv => kv.Key != "Transform")
                .OrderByDescending(kv => kv.Value)
                .Take(topN)
                .Select(kv => new { component = kv.Key, count = kv.Value })
                .ToArray();

            var data = new
            {
                sceneName = metrics.Scene.name,
                scenePath = metrics.Scene.path,
                isDirty = metrics.Scene.isDirty,
                stats = new
                {
                    totalObjects = metrics.TotalObjects,
                    activeObjects = metrics.ActiveObjects,
                    inactiveObjects = metrics.InactiveObjects,
                    rootObjects = metrics.RootObjects,
                    maxHierarchyDepth = metrics.MaxHierarchyDepth,
                    cameras = metrics.Cameras,
                    lights = metrics.Lights,
                    canvases = metrics.Canvases,
                    prefabInstances = metrics.PrefabInstances
                },
                topComponents
            };

            return ToolResponse.OkWithData(data, $"Scene '{metrics.Scene.name}': {metrics.TotalObjects} objects, depth {metrics.MaxHierarchyDepth}");
        }

        #endregion

        #region Action: health_check

        /// <summary>
        /// Run a comprehensive scene health check.
        /// </summary>
        private ToolResponse HandleHealthCheck(JObject parameters)
        {
            var metrics = CollectSceneMetrics(includeComponentStats: false);
            var findings = new List<object>();

            // 1. Check for missing scripts
            foreach (var go in metrics.AllObjects)
            {
                var components = go.GetComponents<Component>();
                for (int i = 0; i < components.Length; i++)
                {
                    if (components[i] == null)
                    {
                        findings.Add(new
                        {
                            severity = "error",
                            category = "MissingScript",
                            message = $"Missing script on '{go.name}'",
                            objectPath = GetHierarchyPath(go)
                        });
                    }
                }
            }

            // 2. Check for missing references via SerializedProperty
            int missingRefCount = 0;
            const int maxMissingRefs = 50;
            foreach (var go in metrics.AllObjects)
            {
                if (missingRefCount >= maxMissingRefs) break;

                var components = go.GetComponents<Component>();
                foreach (var comp in components)
                {
                    if (comp == null) continue;
                    if (missingRefCount >= maxMissingRefs) break;

                    var so = new SerializedObject(comp);
                    var prop = so.GetIterator();
                    while (prop.NextVisible(true))
                    {
                        if (prop.propertyType == SerializedPropertyType.ObjectReference &&
                            prop.objectReferenceValue == null &&
                            prop.objectReferenceInstanceIDValue != 0)
                        {
                            findings.Add(new
                            {
                                severity = "error",
                                category = "MissingReference",
                                message = $"{comp.GetType().Name}.{prop.name} has a missing reference",
                                objectPath = GetHierarchyPath(go)
                            });
                            missingRefCount++;
                            if (missingRefCount >= maxMissingRefs) break;
                        }
                    }
                }
            }

            // 3. Check for duplicate names
            var nameGroups = metrics.AllObjects
                .GroupBy(go => go.name)
                .Where(g => g.Count() > 1)
                .OrderByDescending(g => g.Count())
                .Take(10);

            foreach (var group in nameGroups)
            {
                findings.Add(new
                {
                    severity = "warning",
                    category = "DuplicateName",
                    message = $"{group.Count()} objects share the name '{group.Key}'",
                    objectPath = GetHierarchyPath(group.First())
                });
            }

            // 4. Check for empty nodes (no components except Transform, no children)
            int emptyCount = 0;
            foreach (var go in metrics.AllObjects)
            {
                var comps = go.GetComponents<Component>();
                if (comps.Length == 1 && go.transform.childCount == 0)
                {
                    emptyCount++;
                }
            }
            if (emptyCount > 0)
            {
                findings.Add(new
                {
                    severity = "info",
                    category = "EmptyNode",
                    message = $"{emptyCount} empty leaf objects (no components, no children)",
                    objectPath = ""
                });
            }

            // 5. Check for deep hierarchy (>10)
            foreach (var go in metrics.AllObjects)
            {
                var depth = GetHierarchyDepth(go);
                if (depth > 10)
                {
                    findings.Add(new
                    {
                        severity = "warning",
                        category = "DeepHierarchy",
                        message = $"Hierarchy depth {depth} at '{go.name}'",
                        objectPath = GetHierarchyPath(go)
                    });
                }
            }

            // 6. Infrastructure checks
            if (metrics.MainCameraCount == 0)
            {
                findings.Add(new
                {
                    severity = "error",
                    category = "MissingMainCamera",
                    message = "No MainCamera-tagged camera found in the scene",
                    objectPath = ""
                });
            }

            if (metrics.Lights == 0)
            {
                findings.Add(new
                {
                    severity = "warning",
                    category = "MissingLight",
                    message = "No Light component found in the scene",
                    objectPath = ""
                });
            }

            if ((metrics.Canvases > 0 || metrics.HasUiGraphic) && metrics.EventSystems == 0)
            {
                findings.Add(new
                {
                    severity = "error",
                    category = "MissingEventSystem",
                    message = "UGUI objects exist but no EventSystem was found",
                    objectPath = ""
                });
            }

            if (metrics.Cameras > 0 && metrics.AudioListeners == 0)
            {
                findings.Add(new
                {
                    severity = "warning",
                    category = "MissingAudioListener",
                    message = "Scene has cameras but no AudioListener",
                    objectPath = ""
                });
            }

            // Deduplicate by category+path+message
            var seen = new HashSet<string>();
            var uniqueFindings = new List<object>();
            foreach (var f in findings)
            {
                var jo = JObject.FromObject(f);
                var key = $"{jo["category"]}|{jo["objectPath"]}|{jo["message"]}";
                if (seen.Add(key))
                    uniqueFindings.Add(f);
            }

            int errors = uniqueFindings.Count(f => JObject.FromObject(f)["severity"]?.ToString() == "error");
            int warnings = uniqueFindings.Count(f => JObject.FromObject(f)["severity"]?.ToString() == "warning");
            int infos = uniqueFindings.Count(f => JObject.FromObject(f)["severity"]?.ToString() == "info");

            var data = new
            {
                sceneName = metrics.Scene.name,
                summary = new
                {
                    totalFindings = uniqueFindings.Count,
                    errors,
                    warnings,
                    info = infos
                },
                findings = uniqueFindings
            };

            return ToolResponse.OkWithData(data, $"Health check: {errors} errors, {warnings} warnings, {infos} info");
        }

        #endregion

        #region Action: component_stats

        /// <summary>
        /// Get detailed component statistics for the current scene.
        /// </summary>
        private ToolResponse HandleComponentStats(JObject parameters)
        {
            int topN = ToolHelpers.GetOptionalInt(parameters, "topN", 15);
            var metrics = CollectSceneMetrics(includeComponentStats: true);
            int totalObjects = Math.Max(metrics.TotalObjects, 1);

            var topComponents = metrics.ComponentCounts
                .Where(kv => kv.Key != "Transform")
                .OrderByDescending(kv => kv.Value)
                .Take(topN)
                .Select(kv => new { component = kv.Key, count = kv.Value })
                .ToArray();

            var data = new
            {
                sceneName = metrics.Scene.name,
                stats = new
                {
                    totalObjects = metrics.TotalObjects,
                    activeObjects = metrics.ActiveObjects,
                    inactiveObjects = metrics.InactiveObjects,
                    rootObjects = metrics.RootObjects,
                    maxHierarchyDepth = metrics.MaxHierarchyDepth,
                    prefabInstances = metrics.PrefabInstances,
                    emptyLeafObjects = metrics.EmptyLeafCount,
                    disabledRatio = Math.Round(metrics.InactiveObjects / (double)totalObjects, 3)
                },
                keyFacilities = new
                {
                    cameras = metrics.Cameras,
                    mainCameras = metrics.MainCameraCount,
                    lights = metrics.Lights,
                    canvases = metrics.Canvases,
                    eventSystems = metrics.EventSystems,
                    audioListeners = metrics.AudioListeners,
                    hasUgui = metrics.Canvases > 0 || metrics.HasUiGraphic,
                    hasUiToolkit = metrics.HasUiToolkitDocument
                },
                topComponents
            };

            return ToolResponse.OkWithData(data, $"Component stats: {metrics.ComponentCounts.Count} unique types across {metrics.TotalObjects} objects");
        }

        #endregion

        #region Action: find_hotspots

        /// <summary>
        /// Find structural hotspots in the scene hierarchy.
        /// </summary>
        private ToolResponse HandleFindHotspots(JObject parameters)
        {
            int deepThreshold = ToolHelpers.GetOptionalInt(parameters, "deepThreshold", 10);
            int childThreshold = ToolHelpers.GetOptionalInt(parameters, "childThreshold", 20);
            int maxResults = ToolHelpers.GetOptionalInt(parameters, "maxResults", 20);

            var metrics = CollectSceneMetrics(includeComponentStats: false);
            var hotspots = new List<object>();

            // 1. Deep hierarchy
            foreach (var go in metrics.AllObjects)
            {
                var depth = GetHierarchyDepth(go);
                if (depth >= deepThreshold)
                {
                    hotspots.Add(new
                    {
                        type = "DeepHierarchy",
                        severity = depth >= deepThreshold + 3 ? "warning" : "info",
                        name = go.name,
                        path = GetHierarchyPath(go),
                        depth,
                        count = depth,
                        message = $"Hierarchy depth {depth} exceeds threshold {deepThreshold}"
                    });
                }
            }

            // 2. Large child sets
            foreach (var go in metrics.AllObjects)
            {
                if (go.transform.childCount >= childThreshold)
                {
                    hotspots.Add(new
                    {
                        type = "LargeChildSet",
                        severity = go.transform.childCount >= childThreshold * 2 ? "warning" : "info",
                        name = go.name,
                        path = GetHierarchyPath(go),
                        depth = GetHierarchyDepth(go),
                        count = go.transform.childCount,
                        message = $"{go.transform.childCount} direct children under one node"
                    });
                }
            }

            // 3. Duplicate name clusters
            foreach (var group in metrics.AllObjects.GroupBy(go => go.name).Where(g => g.Count() > 1))
            {
                hotspots.Add(new
                {
                    type = "DuplicateNameCluster",
                    severity = group.Count() >= 5 ? "warning" : "info",
                    name = group.Key,
                    path = GetHierarchyPath(group.First()),
                    depth = 0,
                    count = group.Count(),
                    message = $"{group.Count()} objects share the name '{group.Key}'"
                });
            }

            // 4. Empty leaf clusters
            var emptyLeafGroups = metrics.AllObjects
                .Where(go => go.transform.childCount == 0 && go.GetComponents<Component>().Length == 1)
                .GroupBy(go => go.transform.parent != null ? GetHierarchyPath(go.transform.parent.gameObject) : "<root>")
                .Where(g => g.Count() >= 3);

            foreach (var group in emptyLeafGroups)
            {
                hotspots.Add(new
                {
                    type = "EmptyLeafCluster",
                    severity = "info",
                    name = "",
                    path = group.Key,
                    depth = 0,
                    count = group.Count(),
                    message = $"{group.Count()} empty leaf objects grouped under '{group.Key}'"
                });
            }

            // Sort by severity then count, take maxResults
            var sortedHotspots = hotspots
                .OrderBy(h => GetSeverityRank(JObject.FromObject(h)["severity"]?.ToString()))
                .ThenByDescending(h => JObject.FromObject(h)["count"]?.Value<int>() ?? 0)
                .Take(maxResults)
                .ToList();

            var data = new
            {
                sceneName = metrics.Scene.name,
                thresholds = new { deepThreshold, childThreshold },
                hotspotCount = sortedHotspots.Count,
                hotspots = sortedHotspots
            };

            return ToolResponse.OkWithData(data, $"Found {sortedHotspots.Count} hotspots");
        }

        /// <summary>
        /// Get numeric rank for severity sorting (lower = more severe).
        /// </summary>
        private static int GetSeverityRank(string severity)
        {
            switch (severity)
            {
                case "error": return 0;
                case "warning": return 1;
                default: return 2;
            }
        }

        #endregion

        #region Action: hierarchy_tree

        /// <summary>
        /// Generate a text-based hierarchy tree of the scene.
        /// </summary>
        private ToolResponse HandleHierarchyTree(JObject parameters)
        {
            int maxDepth = ToolHelpers.GetOptionalInt(parameters, "maxDepth", 5);
            bool includeInactive = ToolHelpers.GetOptionalBool(parameters, "includeInactive", false);
            int maxItems = ToolHelpers.GetOptionalInt(parameters, "maxItems", 100);

            var scene = SceneManager.GetActiveScene();
            var rootObjects = scene.GetRootGameObjects()
                .Where(g => includeInactive || g.activeInHierarchy)
                .OrderBy(g => g.transform.GetSiblingIndex())
                .Take(maxItems)
                .ToArray();

            var sb = new StringBuilder();
            sb.AppendLine($"Scene: {scene.name}");
            sb.AppendLine(new string('─', 40));

            int totalShown = 0;
            var componentBuffer = new List<Component>(8);

            foreach (var root in rootObjects)
            {
                BuildHierarchyTree(sb, root.transform, 0, maxDepth, includeInactive, maxItems, ref totalShown, componentBuffer);
            }

            var allRoots = scene.GetRootGameObjects();
            if (allRoots.Length > maxItems)
            {
                sb.AppendLine($"... and {allRoots.Length - maxItems} more root objects");
            }

            var data = new
            {
                sceneName = scene.name,
                hierarchy = sb.ToString(),
                totalObjectsShown = totalShown
            };

            return ToolResponse.OkWithData(data, $"Hierarchy tree: {totalShown} objects shown");
        }

        /// <summary>
        /// Recursively build the hierarchy tree text.
        /// </summary>
        private static void BuildHierarchyTree(StringBuilder sb, Transform t, int depth, int maxDepth,
            bool includeInactive, int maxItems, ref int total, List<Component> componentBuffer)
        {
            if (depth > maxDepth) return;
            if (!includeInactive && !t.gameObject.activeInHierarchy) return;

            total++;
            string indent = new string(' ', depth * 2);
            string prefix = depth == 0 ? "► " : "├─";
            string activeMarker = t.gameObject.activeSelf ? "" : " [inactive]";
            string componentHint = GetComponentHint(t.gameObject, componentBuffer);

            sb.AppendLine($"{indent}{prefix} {t.name}{componentHint}{activeMarker}");

            int childrenShown = 0;
            foreach (Transform child in t)
            {
                if (childrenShown >= maxItems)
                {
                    sb.AppendLine($"{indent}  ... and {t.childCount - childrenShown} more children");
                    break;
                }
                BuildHierarchyTree(sb, child, depth + 1, maxDepth, includeInactive, maxItems, ref total, componentBuffer);
                childrenShown++;
            }
        }

        /// <summary>
        /// Get a short component hint string for hierarchy display.
        /// </summary>
        private static string GetComponentHint(GameObject go, List<Component> componentBuffer)
        {
            componentBuffer.Clear();
            go.GetComponents(componentBuffer);

            foreach (var component in componentBuffer)
            {
                if (component == null) continue;

                if (component is Camera) return " [Camera]";
                if (component is Light) return " [Light]";
                if (component is Canvas) return " [Canvas]";
                if (_uiButtonType != null && _uiButtonType.IsInstanceOfType(component)) return " [Button]";
                if (component is Animator) return " [Animator]";
                if (component is AudioSource) return " [AudioSource]";
                if (component is ParticleSystem) return " [ParticleSystem]";
                if (component is Collider || component is Collider2D) return " [Collider]";
                if (component is Rigidbody || component is Rigidbody2D) return " [Rigidbody]";
                if (component is SkinnedMeshRenderer) return " [SkinnedMesh]";
                if (component is MeshRenderer) return " [MeshRenderer]";
                if (component is SpriteRenderer) return " [SpriteRenderer]";
                if ((_uiTextType != null && _uiTextType.IsInstanceOfType(component)) ||
                    (_uiImageType != null && _uiImageType.IsInstanceOfType(component))) return " [UI]";
            }

            return "";
        }

        #endregion

        #region Action: spatial_query

        /// <summary>
        /// Find objects within a radius of a point or near another object.
        /// </summary>
        private ToolResponse HandleSpatialQuery(JObject parameters)
        {
            float radius = ToolHelpers.GetOptionalFloat(parameters, "radius", 10f);
            int maxResults = ToolHelpers.GetOptionalInt(parameters, "maxResults", 50);
            string nearObject = ToolHelpers.GetOptionalString(parameters, "nearObject");
            string componentFilter = ToolHelpers.GetOptionalString(parameters, "componentFilter");

            Vector3 center;

            if (!string.IsNullOrEmpty(nearObject))
            {
                var go = ToolHelpers.FindGameObject(nearObject);
                if (go == null)
                    return ToolResponse.Fail($"Object '{nearObject}' not found");
                center = go.transform.position;
            }
            else
            {
                center = ToolHelpers.ParseVector3(parameters?["center"], Vector3.zero);
            }

            var scene = SceneManager.GetActiveScene();
            var allObjects = GetAllSceneObjects(scene);
            float radiusSq = radius * radius;

            // Resolve component filter type
            Type filterType = null;
            if (!string.IsNullOrEmpty(componentFilter))
            {
                filterType = ToolHelpers.ResolveComponentType(componentFilter);
            }

            var found = new List<(float dist, object info)>();
            foreach (var go in allObjects)
            {
                if (filterType != null && go.GetComponent(filterType) == null) continue;

                var pos = go.transform.position;
                float distSq = (pos - center).sqrMagnitude;
                if (distSq <= radiusSq)
                {
                    float dist = Mathf.Sqrt(distSq);
                    found.Add((dist, new
                    {
                        name = go.name,
                        path = GetHierarchyPath(go),
                        distance = Math.Round(dist, 3),
                        position = new
                        {
                            x = Math.Round(pos.x, 3),
                            y = Math.Round(pos.y, 3),
                            z = Math.Round(pos.z, 3)
                        }
                    }));
                }
            }

            var results = found
                .OrderBy(f => f.dist)
                .Take(maxResults)
                .Select(f => f.info)
                .ToList();

            var data = new
            {
                center = new { x = Math.Round(center.x, 3), y = Math.Round(center.y, 3), z = Math.Round(center.z, 3) },
                radius,
                componentFilter = componentFilter ?? "none",
                totalFound = found.Count,
                resultsShown = results.Count,
                results
            };

            return ToolResponse.OkWithData(data, $"Spatial query: {found.Count} objects within radius {radius}");
        }

        #endregion

        #region Action: materials_overview

        /// <summary>
        /// Get an overview of all materials and shaders used in the scene.
        /// </summary>
        private ToolResponse HandleMaterialsOverview(JObject parameters)
        {
            var renderers = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
            var materialMap = new Dictionary<int, MaterialEntry>();

            foreach (var renderer in renderers)
            {
                foreach (var mat in renderer.sharedMaterials)
                {
                    if (mat == null) continue;
                    int key = mat.GetInstanceID();
                    if (!materialMap.ContainsKey(key))
                    {
                        materialMap[key] = new MaterialEntry
                        {
                            Name = mat.name,
                            Shader = mat.shader != null ? mat.shader.name : "null",
                            RenderQueue = mat.renderQueue,
                            Path = AssetDatabase.GetAssetPath(mat),
                            Users = new List<string>()
                        };
                    }
                    materialMap[key].Users.Add(renderer.gameObject.name);
                }
            }

            // Group by shader
            var shaderGroups = materialMap.Values
                .GroupBy(m => m.Shader)
                .Select(g => new
                {
                    shader = g.Key,
                    materialCount = g.Count(),
                    materials = g.Select(m => new
                    {
                        name = m.Name,
                        path = m.Path,
                        renderQueue = m.RenderQueue,
                        userCount = m.Users.Count,
                        users = m.Users.Take(5).ToList()
                    }).ToList()
                })
                .OrderByDescending(g => g.materialCount)
                .ToList();

            var data = new
            {
                totalMaterials = materialMap.Count,
                totalShaders = shaderGroups.Count,
                shaders = shaderGroups
            };

            return ToolResponse.OkWithData(data, $"Materials: {materialMap.Count} materials, {shaderGroups.Count} shaders");
        }

        /// <summary>
        /// Internal material tracking entry.
        /// </summary>
        private class MaterialEntry
        {
            public string Name;
            public string Shader;
            public string Path;
            public int RenderQueue;
            public List<string> Users;
        }

        #endregion

        #region Action: performance_hints

        /// <summary>
        /// Analyze the scene for performance issues and provide prioritized suggestions.
        /// </summary>
        private ToolResponse HandlePerformanceHints(JObject parameters)
        {
            var hints = new List<object>();

            // 1. Shadow-casting lights
            var lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
            var shadowLights = lights.Where(l => l.shadows != LightShadows.None).ToArray();
            if (shadowLights.Length > 4)
            {
                hints.Add(new
                {
                    priority = 1,
                    category = "Lighting",
                    issue = $"{shadowLights.Length} shadow-casting lights",
                    suggestion = "Reduce to ≤4 or use baked lighting"
                });
            }

            // 2. Non-static renderers
            var renderers = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
            int nonStaticCount = renderers.Count(r => !r.gameObject.isStatic);
            if (nonStaticCount > 100)
            {
                hints.Add(new
                {
                    priority = 2,
                    category = "Batching",
                    issue = $"{nonStaticCount} non-static renderers",
                    suggestion = "Mark static objects to enable static batching"
                });
            }

            // 3. High-poly meshes without LOD
            var meshFilters = UnityEngine.Object.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None);
            var highPoly = meshFilters
                .Where(mf => mf.sharedMesh != null && mf.sharedMesh.triangles.Length / 3 > 10000
                    && mf.GetComponent<LODGroup>() == null)
                .ToArray();
            if (highPoly.Length > 0)
            {
                hints.Add(new
                {
                    priority = 2,
                    category = "Geometry",
                    issue = $"{highPoly.Length} high-poly meshes (>10k tris) without LOD",
                    suggestion = "Add LOD groups for distant objects"
                });
            }

            // 4. Duplicate material references
            var mats = renderers.SelectMany(r => r.sharedMaterials).Where(m => m != null).ToArray();
            var uniqueMatCount = mats.Select(m => m.GetInstanceID()).Distinct().Count();
            var duplicateCount = mats.Length - uniqueMatCount;
            if (duplicateCount > 10)
            {
                hints.Add(new
                {
                    priority = 3,
                    category = "Materials",
                    issue = $"{duplicateCount} duplicate material references across renderers",
                    suggestion = "Consolidate materials to reduce draw calls"
                });
            }

            // 5. Too many particle systems
            var particles = UnityEngine.Object.FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None);
            if (particles.Length > 20)
            {
                hints.Add(new
                {
                    priority = 3,
                    category = "Particles",
                    issue = $"{particles.Length} particle systems",
                    suggestion = "Consider reducing or pooling particle systems"
                });
            }

            // 6. Transparent objects (potential overdraw)
            int transparentCount = 0;
            foreach (var renderer in renderers)
            {
                foreach (var mat in renderer.sharedMaterials)
                {
                    if (mat != null && mat.renderQueue >= 3000)
                    {
                        transparentCount++;
                        break;
                    }
                }
            }
            if (transparentCount > 20)
            {
                hints.Add(new
                {
                    priority = 2,
                    category = "Overdraw",
                    issue = $"{transparentCount} renderers with transparent materials",
                    suggestion = "Reduce transparent objects to minimize overdraw"
                });
            }

            // 7. Realtime lights without baking
            int realtimeLightCount = lights.Count(l => l.lightmapBakeType == LightmapBakeType.Realtime);
            if (realtimeLightCount > 3)
            {
                hints.Add(new
                {
                    priority = 2,
                    category = "Lighting",
                    issue = $"{realtimeLightCount} realtime lights (not baked)",
                    suggestion = "Consider baking static lights for better performance"
                });
            }

            if (hints.Count == 0)
            {
                hints.Add(new
                {
                    priority = 0,
                    category = "OK",
                    issue = "No obvious performance issues detected",
                    suggestion = "Scene looks good from a structural perspective"
                });
            }

            // Sort by priority
            var sortedHints = hints
                .OrderBy(h => JObject.FromObject(h)["priority"]?.Value<int>() ?? 99)
                .ToList();

            var data = new
            {
                hintCount = sortedHints.Count,
                hints = sortedHints
            };

            return ToolResponse.OkWithData(data, $"Performance hints: {sortedHints.Count} suggestions");
        }

        #endregion

        #region Action: project_info

        /// <summary>
        /// Detect project configuration: render pipeline, UI route, input system, packages.
        /// </summary>
        private ToolResponse HandleProjectInfo(JObject parameters)
        {
            var metrics = CollectSceneMetrics(includeComponentStats: true);
            var packageIds = ReadInstalledPackageIds();

            // Detect render pipeline
            string renderPipeline = "Built-in";
            if (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null)
            {
                var rpType = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline.GetType().Name;
                if (rpType.Contains("Universal") || rpType.Contains("URP"))
                    renderPipeline = "URP";
                else if (rpType.Contains("HD") || rpType.Contains("HDRP"))
                    renderPipeline = "HDRP";
                else
                    renderPipeline = rpType;
            }

            // Detect UI route
            var hasUiToolkitAssets = AssetDatabase.FindAssets("t:VisualTreeAsset", new[] { "Assets" }).Length > 0
                || AssetDatabase.FindAssets("t:PanelSettings", new[] { "Assets" }).Length > 0;
            var usesUgui = metrics.Canvases > 0 || metrics.HasUiGraphic;
            var usesUiToolkit = metrics.HasUiToolkitDocument || hasUiToolkitAssets;

            string uiRoute;
            if (usesUgui && usesUiToolkit) uiRoute = "Both";
            else if (usesUiToolkit) uiRoute = "UIToolkit";
            else if (usesUgui) uiRoute = "UGUI";
            else uiRoute = "Unknown";

            // Detect input system
            string inputHandling = DetectInputHandling(packageIds);

            // Detect major packages
            bool cinemachineDetected = packageIds.Contains("com.unity.cinemachine")
                || FindTypeByName("Cinemachine.CinemachineBrain") != null
                || FindTypeByName("Unity.Cinemachine.CinemachineBrain") != null;
            bool timelineDetected = packageIds.Contains("com.unity.timeline");
            bool navMeshDetected = packageIds.Contains("com.unity.ai.navigation");
            bool xrDetected = packageIds.Contains("com.unity.xr.interaction.toolkit")
                || packageIds.Contains("com.unity.xr.management");
            bool proBuilderDetected = packageIds.Contains("com.unity.probuilder");
            bool inputSystemDetected = packageIds.Contains("com.unity.inputsystem");
            bool addressablesDetected = packageIds.Contains("com.unity.addressables");
            bool textMeshProDetected = packageIds.Contains("com.unity.textmeshpro");

            var data = new
            {
                unityVersion = Application.unityVersion,
                renderPipeline,
                ui = new
                {
                    route = uiRoute,
                    uguiDetected = usesUgui,
                    uiToolkitDetected = usesUiToolkit
                },
                input = new
                {
                    mode = inputHandling,
                    inputSystemInstalled = inputSystemDetected
                },
                packages = new
                {
                    cinemachine = cinemachineDetected,
                    timeline = timelineDetected,
                    navMesh = navMeshDetected,
                    xr = xrDetected,
                    proBuilder = proBuilderDetected,
                    inputSystem = inputSystemDetected,
                    addressables = addressablesDetected,
                    textMeshPro = textMeshProDetected
                },
                projectFolders = new
                {
                    scripts = Directory.Exists("Assets/Scripts"),
                    scenes = Directory.Exists("Assets/Scenes"),
                    prefabs = Directory.Exists("Assets/Prefabs"),
                    materials = Directory.Exists("Assets/Materials"),
                    tests = Directory.Exists("Assets/Tests")
                }
            };

            return ToolResponse.OkWithData(data, $"Project: Unity {Application.unityVersion}, {renderPipeline}, UI={uiRoute}");
        }

        /// <summary>
        /// Read installed package IDs from the manifest.json.
        /// </summary>
        private static HashSet<string> ReadInstalledPackageIds()
        {
            var packageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var manifestPath = Path.Combine("Packages", "manifest.json");
            if (!File.Exists(manifestPath)) return packageIds;

            try
            {
                var manifest = JObject.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
                if (manifest["dependencies"] is JObject dependencies)
                {
                    foreach (var dep in dependencies.Properties())
                        packageIds.Add(dep.Name);
                }
            }
            catch
            {
                // Ignore malformed manifest
            }

            return packageIds;
        }

        /// <summary>
        /// Detect the active input handling mode.
        /// </summary>
        private static string DetectInputHandling(HashSet<string> packageIds)
        {
            var property = typeof(PlayerSettings).GetProperty("activeInputHandler",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                ?? typeof(PlayerSettings).GetProperty("activeInputHandling",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

            if (property != null)
            {
                try
                {
                    var value = property.GetValue(null);
                    if (value != null) return value.ToString();
                }
                catch
                {
                    // Fall through to package-based detection
                }
            }

            return packageIds.Contains("com.unity.inputsystem")
                ? "InputSystemPackageInstalled"
                : "LegacyInputManager";
        }

        #endregion

        #region Action: dependency_analyze

        /// <summary>
        /// Analyze dependencies of a target object in the scene.
        /// </summary>
        private ToolResponse HandleDependencyAnalyze(JObject parameters)
        {
            string targetName = ToolHelpers.GetRequiredString(parameters, "target");
            var targetGo = ToolHelpers.FindGameObject(targetName);
            if (targetGo == null)
                return ToolResponse.Fail($"Target object '{targetName}' not found");

            var scene = SceneManager.GetActiveScene();
            var allObjects = GetAllSceneObjects(scene);
            var targetPath = GetHierarchyPath(targetGo);

            // Collect target + all descendants
            var targetPaths = new HashSet<string>();
            var stack = new Stack<Transform>();
            stack.Push(targetGo.transform);
            while (stack.Count > 0)
            {
                var t = stack.Pop();
                targetPaths.Add(GetHierarchyPath(t.gameObject));
                foreach (Transform child in t) stack.Push(child);
            }

            // Find who references the target (inbound references)
            var referencedBy = new List<object>();
            // Find what the target references (outbound references)
            var referencesTo = new List<object>();

            foreach (var go in allObjects)
            {
                var goPath = GetHierarchyPath(go);
                bool isTarget = targetPaths.Contains(goPath);

                var components = go.GetComponents<Component>();
                foreach (var comp in components)
                {
                    if (comp == null) continue;

                    var so = new SerializedObject(comp);
                    var prop = so.GetIterator();
                    while (prop.NextVisible(true))
                    {
                        if (prop.propertyType != SerializedPropertyType.ObjectReference) continue;
                        if (prop.objectReferenceValue == null) continue;

                        var refGo = prop.objectReferenceValue as GameObject;
                        if (refGo == null)
                        {
                            var refComp = prop.objectReferenceValue as Component;
                            if (refComp != null) refGo = refComp.gameObject;
                        }
                        if (refGo == null) continue;

                        var refPath = GetHierarchyPath(refGo);

                        // Outbound: target references something else
                        if (isTarget && !targetPaths.Contains(refPath))
                        {
                            referencesTo.Add(new
                            {
                                fromObject = goPath,
                                fromComponent = comp.GetType().Name,
                                property = prop.name,
                                toObject = refPath,
                                toType = prop.objectReferenceValue.GetType().Name
                            });
                        }

                        // Inbound: something else references the target
                        if (!isTarget && targetPaths.Contains(refPath))
                        {
                            referencedBy.Add(new
                            {
                                fromObject = goPath,
                                fromComponent = comp.GetType().Name,
                                property = prop.name,
                                toObject = refPath,
                                toType = prop.objectReferenceValue.GetType().Name
                            });
                        }
                    }
                }
            }

            var data = new
            {
                sceneName = scene.name,
                target = targetPath,
                targetChildCount = targetPaths.Count - 1,
                inboundReferences = new
                {
                    count = referencedBy.Count,
                    references = referencedBy
                },
                outboundReferences = new
                {
                    count = referencesTo.Count,
                    references = referencesTo
                },
                safeToDelete = referencedBy.Count == 0
                    ? "Yes — no external references found"
                    : $"No — {referencedBy.Count} external reference(s) would break"
            };

            return ToolResponse.OkWithData(data,
                $"Dependency analysis for '{targetName}': {referencedBy.Count} inbound, {referencesTo.Count} outbound references");
        }

        #endregion
    }
}
