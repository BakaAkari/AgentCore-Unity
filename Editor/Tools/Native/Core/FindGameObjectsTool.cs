using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Tools.Safety;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AgentCore.Editor.Tools.Native.Core
{
    /// <summary>
    /// Search for GameObjects in the scene by name, tag, component, or layer.
    /// Directly calls Unity Editor API as part of the native tool system.
    /// </summary>
    [AgentTool("find_gameobjects",
        Description = "Search for GameObjects in the active scene by name (partial match), tag, layer, or attached component type. " +
            "Use this BEFORE manage_gameobject when the target name might be ambiguous or you need to discover objects matching criteria. " +
            "Returns: JSON array of {name, path, instanceId, tag, layer, isActive, components[]}. " +
            "Supports pagination (page_size, cursor) for large result sets. " +
            "NOT for: searching asset files (use manage_asset action:search), finding code symbols (use search_code).",
        Category = "GameObject", RequiresMainThread = true,
        RiskLevel = ToolRiskLevel.ReadOnly, Capabilities = ToolCapability.ReadProject)]
    public class FindGameObjectsTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""searchTerm"": {
                    ""type"": ""string"",
                    ""description"": ""Search by name (supports partial match)""
                },
                ""tag"": {
                    ""type"": ""string"",
                    ""description"": ""Filter by tag""
                },
                ""layer"": {
                    ""type"": ""string"",
                    ""description"": ""Filter by layer name""
                },
                ""componentType"": {
                    ""type"": ""string"",
                    ""description"": ""Filter by component type name""
                },
                ""activeOnly"": {
                    ""type"": ""boolean"",
                    ""description"": ""Only return active GameObjects (default: true)""
                },
                ""maxResults"": {
                    ""type"": ""integer"",
                    ""description"": ""Maximum results to return (default: 50)""
                }
            }
        }");

        public ToolMetadata Metadata => new ToolMetadata(
            name: "find_gameobjects",
            description: "Search for GameObjects in the scene by name, tag, component, or layer",
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
                response = HandleSearch(parameters);
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

        private ToolResponse HandleSearch(JObject parameters)
        {
            var searchTerm = ToolHelpers.GetOptionalString(parameters, "searchTerm");
            var tag = ToolHelpers.GetOptionalString(parameters, "tag");
            var layerName = ToolHelpers.GetOptionalString(parameters, "layer");
            var componentType = ToolHelpers.GetOptionalString(parameters, "componentType");
            bool activeOnly = ToolHelpers.GetOptionalBool(parameters, "activeOnly", true);
            int maxResults = ToolHelpers.GetOptionalInt(parameters, "maxResults", 50);

            // Validate: at least one search criterion must be provided
            if (string.IsNullOrEmpty(searchTerm) && string.IsNullOrEmpty(tag) &&
                string.IsNullOrEmpty(layerName) && string.IsNullOrEmpty(componentType))
            {
                return ToolResponse.Fail(
                    "At least one search criterion is required: searchTerm, tag, layer, or componentType.");
            }

            // Clamp maxResults
            maxResults = Mathf.Clamp(maxResults, 1, 500);

            try
            {
                // Get all GameObjects in the scene
                var findMode = activeOnly
                    ? FindObjectsInactive.Exclude
                    : FindObjectsInactive.Include;
                var allObjects = UnityEngine.Object.FindObjectsByType<GameObject>(findMode, FindObjectsSortMode.None);

                // Apply filters
                IEnumerable<GameObject> filtered = allObjects;

                // Filter by name (partial match, case-insensitive)
                if (!string.IsNullOrEmpty(searchTerm))
                {
                    filtered = filtered.Where(go =>
                        go.name.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase) >= 0);
                }

                // Filter by tag
                if (!string.IsNullOrEmpty(tag))
                {
                    filtered = filtered.Where(go =>
                    {
                        try { return go.CompareTag(tag); }
                        catch { return false; } // Tag might not exist
                    });
                }

                // Filter by layer
                if (!string.IsNullOrEmpty(layerName))
                {
                    int layer = LayerMask.NameToLayer(layerName);
                    if (layer < 0)
                        return ToolResponse.Fail($"Layer '{layerName}' not found.");
                    filtered = filtered.Where(go => go.layer == layer);
                }

                // Filter by component type
                if (!string.IsNullOrEmpty(componentType))
                {
                    var type = ToolHelpers.ResolveComponentType(componentType);
                    if (type == null)
                        return ToolResponse.Fail($"Component type '{componentType}' not found.");
                    filtered = filtered.Where(go => go.GetComponent(type) != null);
                }

                // Materialize all matches first, then take the subset
                var allMatches = filtered.ToList();
                int totalMatches = allMatches.Count;
                var results = allMatches.Take(maxResults).ToList();

                // Build result array
                var resultArray = new JArray();
                foreach (var go in results)
                {
                    var item = new JObject
                    {
                        ["name"] = go.name,
                        ["instanceId"] = go.GetInstanceID(),
                        ["activeSelf"] = go.activeSelf,
                        ["activeInHierarchy"] = go.activeInHierarchy,
                        ["tag"] = go.tag,
                        ["layer"] = LayerMask.LayerToName(go.layer),
                        ["path"] = GetGameObjectPath(go)
                    };

                    // Include component type names for context
                    var components = go.GetComponents<Component>();
                    var compNames = new JArray();
                    foreach (var comp in components)
                    {
                        if (comp != null)
                            compNames.Add(comp.GetType().Name);
                    }
                    item["components"] = compNames;

                    resultArray.Add(item);
                }

                // Build search criteria description for the message
                var criteria = new List<string>();
                if (!string.IsNullOrEmpty(searchTerm)) criteria.Add($"name='{searchTerm}'");
                if (!string.IsNullOrEmpty(tag)) criteria.Add($"tag='{tag}'");
                if (!string.IsNullOrEmpty(layerName)) criteria.Add($"layer='{layerName}'");
                if (!string.IsNullOrEmpty(componentType)) criteria.Add($"component='{componentType}'");
                var criteriaStr = string.Join(", ", criteria);

                return ToolResponse.OkWithData(new JObject
                {
                    ["results"] = resultArray,
                    ["count"] = results.Count,
                    ["totalMatches"] = totalMatches,
                    ["maxResults"] = maxResults,
                    ["hasMore"] = totalMatches > maxResults,
                    ["activeOnly"] = activeOnly
                }, $"Found {results.Count} GameObjects matching [{criteriaStr}]" +
                   (totalMatches > maxResults ? $" (showing {maxResults} of {totalMatches})" : "") + ".");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error searching GameObjects: {ex.Message}");
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
    }
}
