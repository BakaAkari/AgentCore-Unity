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

namespace AgentCore.Editor.Tools.Native.Specialized
{
    /// <summary>
    /// Manage Unity Terrain: create terrains, sculpt heightmaps with Perlin noise,
    /// smooth/flatten, add texture layers, paint textures, and place trees.
    /// Directly calls Unity Terrain / TerrainData API.
    /// </summary>
    [AgentTool("manage_terrain",
        Description = "Unity Terrain system — create landscapes and sculpt heightmaps procedurally. " +
                      "Actions: create (terrain with size/resolution), sculpt_perlin (apply Perlin noise heightmap with scale/amplitude/octaves), " +
                      "smooth (blur heightmap), flatten (set uniform height), " +
                      "add_texture_layer (add terrain texture with tiling), paint_texture (apply texture at world position with brush), " +
                      "place_trees (scatter tree prefabs with density/randomization). " +
                      "USE FOR: creating terrain landscapes, procedural heightmap generation, terrain texturing, " +
                      "tree/vegetation placement, terrain size/resolution setup. " +
                      "NOT FOR: terrain detail/grass painting (too fine-grained for tool), " +
                      "Terrain Tools package advanced features, SpeedTree configuration, custom terrain shaders. " +
                      "ACTIVATE WHEN: user mentions 'terrain', 'heightmap', 'landscape', 'terrain texture', 'place trees', 'sculpt terrain'.",
        Category = "Specialized",
        Visibility = ToolVisibility.OnDemand,
        RequiresMainThread = true)]
    public class ManageTerrainTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""create"", ""get_info"", ""get_height"", ""set_height"", ""generate_perlin"", ""smooth"", ""flatten"", ""add_layer"", ""paint_texture"", ""add_tree""],
                    ""description"": ""Action to perform on terrain""
                },
                ""target"": { ""type"": ""string"", ""description"": ""Target Terrain GameObject name or path"" },
                ""name"": { ""type"": ""string"", ""description"": ""Name for the new terrain GameObject"" },
                ""width"": { ""type"": ""number"", ""description"": ""Terrain width in world units (default: 1000)"" },
                ""height"": { ""type"": ""number"", ""description"": ""Terrain height in world units (default: 600)"" },
                ""length"": { ""type"": ""number"", ""description"": ""Terrain length in world units (default: 1000)"" },
                ""heightmapResolution"": { ""type"": ""integer"", ""description"": ""Heightmap resolution, must be 2^n+1 (default: 513)"" },
                ""assetPath"": { ""type"": ""string"", ""description"": ""Asset path to save TerrainData (e.g. Assets/Terrains/MyTerrain.asset)"" },
                ""x"": { ""type"": ""number"", ""description"": ""World X coordinate (for get_height)"" },
                ""z"": { ""type"": ""number"", ""description"": ""World Z coordinate (for get_height)"" },
                ""normalizedX"": { ""type"": ""number"", ""description"": ""Normalized X coordinate (0-1)"" },
                ""normalizedZ"": { ""type"": ""number"", ""description"": ""Normalized Z coordinate (0-1)"" },
                ""scale"": { ""type"": ""number"", ""description"": ""Perlin noise scale (default: 20)"" },
                ""amplitude"": { ""type"": ""number"", ""description"": ""Perlin noise amplitude (default: 0.3)"" },
                ""octaves"": { ""type"": ""integer"", ""description"": ""Perlin noise octaves (default: 4)"" },
                ""seed"": { ""type"": ""integer"", ""description"": ""Random seed for noise generation"" },
                ""iterations"": { ""type"": ""integer"", ""description"": ""Smoothing iterations (default: 3)"" },
                ""strength"": { ""type"": ""number"", ""description"": ""Smoothing strength 0-1 (default: 0.5)"" },
                ""texturePath"": { ""type"": ""string"", ""description"": ""Texture asset path for terrain layer"" },
                ""normalMapPath"": { ""type"": ""string"", ""description"": ""Normal map asset path (optional)"" },
                ""tileSize"": { ""type"": ""number"", ""description"": ""Texture tile size (default: 10)"" },
                ""layerIndex"": { ""type"": ""integer"", ""description"": ""Terrain layer index for painting"" },
                ""radius"": { ""type"": ""number"", ""description"": ""Brush radius 0-1 for painting"" },
                ""opacity"": { ""type"": ""number"", ""description"": ""Brush opacity 0-1 for painting"" },
                ""prefabPath"": { ""type"": ""string"", ""description"": ""Tree prefab asset path"" },
                ""count"": { ""type"": ""integer"", ""description"": ""Number of trees to place"" },
                ""minScale"": { ""type"": ""number"", ""description"": ""Minimum tree scale (default: 0.8)"" },
                ""maxScale"": { ""type"": ""number"", ""description"": ""Maximum tree scale (default: 1.2)"" }
            },
            ""required"": [""action""]
        }");

        /// <summary>
        /// Tool metadata for registration and LLM discovery.
        /// </summary>
        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_terrain",
            description: "Manage Unity Terrain: create, sculpt heightmaps (perlin/smooth/flatten), add texture layers, paint textures, and place trees",
            category: "Specialized",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        /// <summary>
        /// Execute a terrain management action.
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
                    case "create":
                        response = HandleCreate(parameters);
                        break;
                    case "get_info":
                        response = HandleGetInfo(parameters);
                        break;
                    case "get_height":
                        response = HandleGetHeight(parameters);
                        break;
                    case "set_height":
                        response = HandleSetHeight(parameters);
                        break;
                    case "generate_perlin":
                        response = HandleGeneratePerlin(parameters);
                        break;
                    case "smooth":
                        response = HandleSmooth(parameters);
                        break;
                    case "flatten":
                        response = HandleFlatten(parameters);
                        break;
                    case "add_layer":
                        response = HandleAddLayer(parameters);
                        break;
                    case "paint_texture":
                        response = HandlePaintTexture(parameters);
                        break;
                    case "add_tree":
                        response = HandleAddTree(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail($"Unknown action: {action}. Valid actions: create, get_info, get_height, set_height, generate_perlin, smooth, flatten, add_layer, paint_texture, add_tree");
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
        /// Create a new Terrain with TerrainData saved as an asset.
        /// </summary>
        private ToolResponse HandleCreate(JObject parameters)
        {
            var name = ToolHelpers.GetOptionalString(parameters, "name", "New Terrain");
            var width = ToolHelpers.GetOptionalFloat(parameters, "width", 1000f);
            var height = ToolHelpers.GetOptionalFloat(parameters, "height", 600f);
            var length = ToolHelpers.GetOptionalFloat(parameters, "length", 1000f);
            var resolution = ToolHelpers.GetOptionalInt(parameters, "heightmapResolution", 513);
            var assetPath = ToolHelpers.GetOptionalString(parameters, "assetPath", $"Assets/Terrains/{name}.asset");

            // Validate resolution is 2^n + 1
            if (!IsValidHeightmapResolution(resolution))
            {
                return ToolResponse.Fail($"heightmapResolution must be 2^n+1 (e.g. 33, 65, 129, 257, 513, 1025). Got: {resolution}");
            }

            // Ensure directory exists
            var normalizedPath = ToolHelpers.NormalizeAssetPath(assetPath);
            ToolHelpers.EnsureDirectoryExists(normalizedPath);

            // Create TerrainData
            var terrainData = new TerrainData();
            terrainData.heightmapResolution = resolution;
            terrainData.size = new Vector3(width, height, length);

            // Save TerrainData as asset
            AssetDatabase.CreateAsset(terrainData, normalizedPath);
            AssetDatabase.SaveAssets();

            // Create Terrain GameObject
            var terrainGo = Terrain.CreateTerrainGameObject(terrainData);
            terrainGo.name = name;
            ToolHelpers.RegisterCreatedObject(terrainGo, "Create Terrain");

            return ToolResponse.OkWithData(new
            {
                gameObject = name,
                terrainDataPath = normalizedPath,
                size = new { width, height, length },
                heightmapResolution = resolution
            }, $"Created terrain '{name}' with data saved to '{normalizedPath}'");
        }

        /// <summary>
        /// Get information about an existing terrain.
        /// </summary>
        private ToolResponse HandleGetInfo(JObject parameters)
        {
            var terrain = FindTerrain(parameters);
            if (terrain == null)
                return ToolResponse.Fail("Terrain not found. Provide 'target' with the Terrain GameObject name.");

            var data = terrain.terrainData;
            if (data == null)
                return ToolResponse.Fail("Terrain has no TerrainData assigned.");

            return ToolResponse.OkWithData(new
            {
                name = terrain.gameObject.name,
                position = ToolHelpers.Vector3ToJson(terrain.transform.position),
                size = new { width = data.size.x, height = data.size.y, length = data.size.z },
                heightmapResolution = data.heightmapResolution,
                alphamapResolution = data.alphamapResolution,
                terrainLayerCount = data.terrainLayers != null ? data.terrainLayers.Length : 0,
                treeInstanceCount = data.treeInstanceCount,
                treePrototypeCount = data.treePrototypes != null ? data.treePrototypes.Length : 0,
                detailResolution = data.detailResolution,
                assetPath = AssetDatabase.GetAssetPath(data)
            }, $"Terrain info for '{terrain.gameObject.name}'");
        }

        /// <summary>
        /// Get the terrain height at a world position.
        /// </summary>
        private ToolResponse HandleGetHeight(JObject parameters)
        {
            var terrain = FindTerrain(parameters);
            if (terrain == null)
                return ToolResponse.Fail("Terrain not found. Provide 'target' with the Terrain GameObject name.");

            var x = ToolHelpers.GetOptionalFloat(parameters, "x", 0f);
            var z = ToolHelpers.GetOptionalFloat(parameters, "z", 0f);

            var worldPos = new Vector3(x, 0, z);
            var worldHeight = terrain.SampleHeight(worldPos);

            return ToolResponse.OkWithData(new
            {
                worldX = x,
                worldZ = z,
                worldHeight,
                terrainRelativeHeight = worldHeight - terrain.transform.position.y
            }, $"Height at ({x}, {z}) = {worldHeight}");
        }

        /// <summary>
        /// Set the height at a normalized coordinate.
        /// </summary>
        private ToolResponse HandleSetHeight(JObject parameters)
        {
            var terrain = FindTerrain(parameters);
            if (terrain == null)
                return ToolResponse.Fail("Terrain not found. Provide 'target' with the Terrain GameObject name.");

            var normalizedX = ToolHelpers.GetOptionalFloat(parameters, "normalizedX", 0.5f);
            var normalizedZ = ToolHelpers.GetOptionalFloat(parameters, "normalizedZ", 0.5f);
            var height = ToolHelpers.GetOptionalFloat(parameters, "height", 0f);

            normalizedX = Mathf.Clamp01(normalizedX);
            normalizedZ = Mathf.Clamp01(normalizedZ);
            height = Mathf.Clamp01(height);

            var data = terrain.terrainData;
            ToolHelpers.RecordUndo(data, "Set Terrain Height");

            var res = data.heightmapResolution;
            var xIndex = Mathf.RoundToInt(normalizedZ * (res - 1));
            var yIndex = Mathf.RoundToInt(normalizedX * (res - 1));

            var heights = new float[1, 1];
            heights[0, 0] = height;
            data.SetHeights(xIndex, yIndex, heights);

            terrain.Flush();

            return ToolResponse.Ok($"Set height to {height} at normalized ({normalizedX}, {normalizedZ})");
        }

        /// <summary>
        /// Generate terrain heightmap using Perlin noise.
        /// </summary>
        private ToolResponse HandleGeneratePerlin(JObject parameters)
        {
            var terrain = FindTerrain(parameters);
            if (terrain == null)
                return ToolResponse.Fail("Terrain not found. Provide 'target' with the Terrain GameObject name.");

            var scale = ToolHelpers.GetOptionalFloat(parameters, "scale", 20f);
            var amplitude = ToolHelpers.GetOptionalFloat(parameters, "amplitude", 0.3f);
            var octaves = ToolHelpers.GetOptionalInt(parameters, "octaves", 4);
            var seed = ToolHelpers.GetOptionalInt(parameters, "seed", 0);

            if (scale <= 0f) scale = 1f;
            octaves = Mathf.Clamp(octaves, 1, 8);

            var data = terrain.terrainData;
            ToolHelpers.RecordUndo(data, "Generate Perlin Terrain");

            var res = data.heightmapResolution;
            var heights = new float[res, res];

            var rng = new System.Random(seed);
            var offsetX = (float)rng.NextDouble() * 10000f;
            var offsetZ = (float)rng.NextDouble() * 10000f;

            for (int z = 0; z < res; z++)
            {
                for (int x = 0; x < res; x++)
                {
                    float nx = (float)x / res;
                    float nz = (float)z / res;

                    float value = 0f;
                    float freq = 1f;
                    float amp = 1f;
                    float maxAmp = 0f;

                    for (int o = 0; o < octaves; o++)
                    {
                        value += Mathf.PerlinNoise(
                            offsetX + nx * scale * freq,
                            offsetZ + nz * scale * freq
                        ) * amp;

                        maxAmp += amp;
                        freq *= 2f;
                        amp *= 0.5f;
                    }

                    heights[z, x] = (value / maxAmp) * amplitude;
                }
            }

            data.SetHeights(0, 0, heights);
            terrain.Flush();

            return ToolResponse.OkWithData(new
            {
                resolution = res,
                scale,
                amplitude,
                octaves,
                seed
            }, $"Generated Perlin noise terrain (scale={scale}, amplitude={amplitude}, octaves={octaves})");
        }

        /// <summary>
        /// Smooth the terrain heightmap using averaging filter.
        /// </summary>
        private ToolResponse HandleSmooth(JObject parameters)
        {
            var terrain = FindTerrain(parameters);
            if (terrain == null)
                return ToolResponse.Fail("Terrain not found. Provide 'target' with the Terrain GameObject name.");

            var iterations = ToolHelpers.GetOptionalInt(parameters, "iterations", 3);
            var strength = ToolHelpers.GetOptionalFloat(parameters, "strength", 0.5f);

            iterations = Mathf.Clamp(iterations, 1, 20);
            strength = Mathf.Clamp01(strength);

            var data = terrain.terrainData;
            ToolHelpers.RecordUndo(data, "Smooth Terrain");

            var res = data.heightmapResolution;
            var heights = data.GetHeights(0, 0, res, res);

            for (int iter = 0; iter < iterations; iter++)
            {
                var smoothed = new float[res, res];
                for (int z = 0; z < res; z++)
                {
                    for (int x = 0; x < res; x++)
                    {
                        float sum = 0f;
                        int count = 0;

                        for (int dz = -1; dz <= 1; dz++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nz = z + dz;
                                int nx = x + dx;
                                if (nz >= 0 && nz < res && nx >= 0 && nx < res)
                                {
                                    sum += heights[nz, nx];
                                    count++;
                                }
                            }
                        }

                        float avg = sum / count;
                        smoothed[z, x] = Mathf.Lerp(heights[z, x], avg, strength);
                    }
                }
                heights = smoothed;
            }

            data.SetHeights(0, 0, heights);
            terrain.Flush();

            return ToolResponse.Ok($"Smoothed terrain with {iterations} iterations, strength={strength}");
        }

        /// <summary>
        /// Flatten the terrain to a uniform height.
        /// </summary>
        private ToolResponse HandleFlatten(JObject parameters)
        {
            var terrain = FindTerrain(parameters);
            if (terrain == null)
                return ToolResponse.Fail("Terrain not found. Provide 'target' with the Terrain GameObject name.");

            var height = ToolHelpers.GetOptionalFloat(parameters, "height", 0f);
            height = Mathf.Clamp01(height);

            var data = terrain.terrainData;
            ToolHelpers.RecordUndo(data, "Flatten Terrain");

            var res = data.heightmapResolution;
            var heights = new float[res, res];

            for (int z = 0; z < res; z++)
            {
                for (int x = 0; x < res; x++)
                {
                    heights[z, x] = height;
                }
            }

            data.SetHeights(0, 0, heights);
            terrain.Flush();

            return ToolResponse.Ok($"Flattened terrain to height={height}");
        }

        /// <summary>
        /// Add a terrain texture layer.
        /// </summary>
        private ToolResponse HandleAddLayer(JObject parameters)
        {
            var terrain = FindTerrain(parameters);
            if (terrain == null)
                return ToolResponse.Fail("Terrain not found. Provide 'target' with the Terrain GameObject name.");

            var texturePath = ToolHelpers.GetRequiredString(parameters, "texturePath");
            var tileSize = ToolHelpers.GetOptionalFloat(parameters, "tileSize", 10f);
            var normalMapPath = ToolHelpers.GetOptionalString(parameters, "normalMapPath", null);

            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            if (texture == null)
                return ToolResponse.Fail($"Texture not found at path: {texturePath}");

            Texture2D normalMap = null;
            if (!string.IsNullOrEmpty(normalMapPath))
            {
                normalMap = AssetDatabase.LoadAssetAtPath<Texture2D>(normalMapPath);
                if (normalMap == null)
                    return ToolResponse.Fail($"Normal map not found at path: {normalMapPath}");
            }

            var data = terrain.terrainData;
            ToolHelpers.RecordUndo(data, "Add Terrain Layer");

            var layer = new TerrainLayer
            {
                diffuseTexture = texture,
                tileSize = new Vector2(tileSize, tileSize)
            };

            if (normalMap != null)
                layer.normalMapTexture = normalMap;

            // Save the TerrainLayer as an asset
            var layerPath = texturePath.Replace(".png", "_layer.asset")
                                       .Replace(".jpg", "_layer.asset")
                                       .Replace(".tga", "_layer.asset");
            if (!layerPath.EndsWith(".asset"))
                layerPath = layerPath + "_layer.asset";

            layerPath = ToolHelpers.NormalizeAssetPath(layerPath);
            ToolHelpers.EnsureDirectoryExists(layerPath);
            AssetDatabase.CreateAsset(layer, layerPath);

            var existingLayers = data.terrainLayers != null ? data.terrainLayers.ToList() : new List<TerrainLayer>();
            existingLayers.Add(layer);
            data.terrainLayers = existingLayers.ToArray();

            AssetDatabase.SaveAssets();

            return ToolResponse.OkWithData(new
            {
                layerIndex = existingLayers.Count - 1,
                texturePath,
                tileSize,
                layerAssetPath = layerPath,
                totalLayers = existingLayers.Count
            }, $"Added terrain layer (index={existingLayers.Count - 1}) with texture '{texturePath}'");
        }

        /// <summary>
        /// Paint a texture at a normalized position on the terrain.
        /// </summary>
        private ToolResponse HandlePaintTexture(JObject parameters)
        {
            var terrain = FindTerrain(parameters);
            if (terrain == null)
                return ToolResponse.Fail("Terrain not found. Provide 'target' with the Terrain GameObject name.");

            var layerIndex = ToolHelpers.GetOptionalInt(parameters, "layerIndex", 0);
            var normalizedX = ToolHelpers.GetOptionalFloat(parameters, "normalizedX", 0.5f);
            var normalizedZ = ToolHelpers.GetOptionalFloat(parameters, "normalizedZ", 0.5f);
            var radius = ToolHelpers.GetOptionalFloat(parameters, "radius", 0.1f);
            var opacity = ToolHelpers.GetOptionalFloat(parameters, "opacity", 1f);

            normalizedX = Mathf.Clamp01(normalizedX);
            normalizedZ = Mathf.Clamp01(normalizedZ);
            radius = Mathf.Clamp01(radius);
            opacity = Mathf.Clamp01(opacity);

            var data = terrain.terrainData;
            var layerCount = data.terrainLayers != null ? data.terrainLayers.Length : 0;

            if (layerCount == 0)
                return ToolResponse.Fail("Terrain has no texture layers. Use 'add_layer' first.");

            if (layerIndex < 0 || layerIndex >= layerCount)
                return ToolResponse.Fail($"layerIndex {layerIndex} out of range. Terrain has {layerCount} layers (0-{layerCount - 1}).");

            ToolHelpers.RecordUndo(data, "Paint Terrain Texture");

            var alphamapRes = data.alphamapResolution;
            var alphamaps = data.GetAlphamaps(0, 0, alphamapRes, alphamapRes);

            int centerX = Mathf.RoundToInt(normalizedX * (alphamapRes - 1));
            int centerZ = Mathf.RoundToInt(normalizedZ * (alphamapRes - 1));
            int brushRadius = Mathf.RoundToInt(radius * alphamapRes * 0.5f);

            for (int z = -brushRadius; z <= brushRadius; z++)
            {
                for (int x = -brushRadius; x <= brushRadius; x++)
                {
                    int px = centerX + x;
                    int pz = centerZ + z;

                    if (px < 0 || px >= alphamapRes || pz < 0 || pz >= alphamapRes)
                        continue;

                    float dist = Mathf.Sqrt(x * x + z * z) / brushRadius;
                    if (dist > 1f) continue;

                    float falloff = 1f - dist;
                    float blend = falloff * opacity;

                    // Blend: increase target layer, decrease others proportionally
                    float currentVal = alphamaps[pz, px, layerIndex];
                    float newVal = Mathf.Lerp(currentVal, 1f, blend);
                    float remaining = 1f - newVal;
                    float otherSum = 0f;

                    for (int l = 0; l < layerCount; l++)
                    {
                        if (l != layerIndex)
                            otherSum += alphamaps[pz, px, l];
                    }

                    alphamaps[pz, px, layerIndex] = newVal;

                    if (otherSum > 0f)
                    {
                        for (int l = 0; l < layerCount; l++)
                        {
                            if (l != layerIndex)
                                alphamaps[pz, px, l] = alphamaps[pz, px, l] / otherSum * remaining;
                        }
                    }
                }
            }

            data.SetAlphamaps(0, 0, alphamaps);
            terrain.Flush();

            return ToolResponse.Ok($"Painted layer {layerIndex} at ({normalizedX}, {normalizedZ}) with radius={radius}, opacity={opacity}");
        }

        /// <summary>
        /// Add tree prototypes and randomly place tree instances.
        /// </summary>
        private ToolResponse HandleAddTree(JObject parameters)
        {
            var terrain = FindTerrain(parameters);
            if (terrain == null)
                return ToolResponse.Fail("Terrain not found. Provide 'target' with the Terrain GameObject name.");

            var prefabPath = ToolHelpers.GetRequiredString(parameters, "prefabPath");
            var count = ToolHelpers.GetOptionalInt(parameters, "count", 100);
            var minScale = ToolHelpers.GetOptionalFloat(parameters, "minScale", 0.8f);
            var maxScale = ToolHelpers.GetOptionalFloat(parameters, "maxScale", 1.2f);

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
                return ToolResponse.Fail($"Tree prefab not found at path: {prefabPath}");

            count = Mathf.Clamp(count, 1, 10000);

            var data = terrain.terrainData;
            ToolHelpers.RecordUndo(data, "Add Trees");

            // Add tree prototype if not already present
            var prototypes = data.treePrototypes != null ? data.treePrototypes.ToList() : new List<TreePrototype>();
            int protoIndex = prototypes.FindIndex(p => p.prefab == prefab);

            if (protoIndex < 0)
            {
                prototypes.Add(new TreePrototype { prefab = prefab });
                data.treePrototypes = prototypes.ToArray();
                protoIndex = prototypes.Count - 1;
            }

            // Place tree instances
            var existingTrees = data.treeInstances.ToList();
            var rng = new System.Random();

            for (int i = 0; i < count; i++)
            {
                float posX = (float)rng.NextDouble();
                float posZ = (float)rng.NextDouble();
                float scale = Mathf.Lerp(minScale, maxScale, (float)rng.NextDouble());

                // Sample height at this position for proper Y placement
                existingTrees.Add(new TreeInstance
                {
                    position = new Vector3(posX, 0f, posZ),
                    prototypeIndex = protoIndex,
                    widthScale = scale,
                    heightScale = scale,
                    color = Color.white,
                    lightmapColor = Color.white,
                    rotation = (float)rng.NextDouble() * Mathf.PI * 2f
                });
            }

            data.treeInstances = existingTrees.ToArray();
            terrain.Flush();

            return ToolResponse.OkWithData(new
            {
                prefabPath,
                prototypeIndex = protoIndex,
                addedCount = count,
                totalTreeInstances = existingTrees.Count,
                scaleRange = new { min = minScale, max = maxScale }
            }, $"Added {count} trees using prefab '{prefabPath}'");
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Find a Terrain component from the 'target' parameter.
        /// </summary>
        private Terrain FindTerrain(JObject parameters)
        {
            var target = ToolHelpers.GetOptionalString(parameters, "target", null);

            if (!string.IsNullOrEmpty(target))
            {
                var go = ToolHelpers.FindGameObject(target);
                if (go != null)
                    return go.GetComponent<Terrain>();
            }

            // Fallback: find any terrain in scene
            return UnityEngine.Object.FindObjectOfType<Terrain>();
        }

        /// <summary>
        /// Validate that a heightmap resolution is 2^n + 1.
        /// </summary>
        private bool IsValidHeightmapResolution(int resolution)
        {
            if (resolution < 33) return false;
            int n = resolution - 1;
            return (n & (n - 1)) == 0;
        }

        #endregion
    }
}
