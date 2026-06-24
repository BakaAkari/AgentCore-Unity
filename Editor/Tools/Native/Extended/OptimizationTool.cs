using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
    /// Scene and asset optimization tool — analyze performance bottlenecks,
    /// batch-optimize textures/meshes/audio, manage static flags, LOD groups,
    /// find duplicate materials, and detect overdraw risks.
    /// </summary>
    [AgentTool("optimization",
        Description = "Analyze scene performance bottlenecks, batch-optimize textures/meshes/audio settings, manage static flags and LOD groups, find duplicate materials, and detect overdraw risks.",
        Category = "Extended",
        RequiresMainThread = true,
        RiskLevel = ToolRiskLevel.Medium,
        Capabilities = ToolCapability.ModifyAssets | ToolCapability.ModifyScene)]
    public class OptimizationTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""analyze_scene"", ""optimize_textures"", ""optimize_mesh_compression"", ""find_large_assets"", ""set_static_flags"", ""get_static_flags"", ""optimize_audio"", ""find_duplicate_materials"", ""analyze_overdraw"", ""set_lod_group""],
                    ""description"": ""Action to perform""
                },
                ""target"": { ""type"": ""string"", ""description"": ""Target GameObject name or path (for set_static_flags, get_static_flags, set_lod_group)"" },
                ""folder"": { ""type"": ""string"", ""description"": ""Asset folder to search (default: Assets)"" },
                ""maxSize"": { ""type"": ""integer"", ""description"": ""Max texture size for optimize_textures (e.g. 512, 1024, 2048)"" },
                ""compression"": { ""type"": ""string"", ""description"": ""Compression level. For textures: None/LowQuality/Normal/HighQuality. For meshes: Off/Low/Medium/High"" },
                ""minSizeKB"": { ""type"": ""integer"", ""description"": ""Minimum asset size in KB for find_large_assets (default: 1024)"" },
                ""flags"": { ""type"": ""string"", ""description"": ""Static flags: Everything/Nothing/BatchingStatic/OccludeeStatic/OccluderStatic/NavigationStatic/ReflectionProbeStatic"" },
                ""includeChildren"": { ""type"": ""boolean"", ""description"": ""Include children when setting static flags (default: false)"" },
                ""compressionFormat"": { ""type"": ""string"", ""description"": ""Audio compression format: PCM/Vorbis/ADPCM"" },
                ""loadType"": { ""type"": ""string"", ""description"": ""Audio load type: DecompressOnLoad/CompressedInMemory/Streaming"" },
                ""lodDistances"": { ""type"": ""string"", ""description"": ""Comma-separated LOD screen-relative heights (e.g. '0.6,0.3,0.1')"" },
                ""polyThreshold"": { ""type"": ""integer"", ""description"": ""Triangle count threshold for high-poly warning (default: 10000)"" },
                ""materialThreshold"": { ""type"": ""integer"", ""description"": ""Material slot threshold for excessive materials warning (default: 5)"" },
                ""limit"": { ""type"": ""integer"", ""description"": ""Max results to return (default: 50)"" }
            },
            ""required"": [""action""]
        }");

        /// <summary>
        /// Tool metadata for auto-discovery registration.
        /// </summary>
        public ToolMetadata Metadata => new ToolMetadata(
            name: "optimization",
            description: "Analyze scene performance bottlenecks, batch-optimize textures/meshes/audio settings, manage static flags and LOD groups, find duplicate materials, and detect overdraw risks.",
            category: "Extended",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        /// <summary>
        /// Execute the optimization action specified in parameters.
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
                    case "analyze_scene":
                        response = HandleAnalyzeScene(parameters);
                        break;
                    case "optimize_textures":
                        response = HandleOptimizeTextures(parameters);
                        break;
                    case "optimize_mesh_compression":
                        response = HandleOptimizeMeshCompression(parameters);
                        break;
                    case "find_large_assets":
                        response = HandleFindLargeAssets(parameters);
                        break;
                    case "set_static_flags":
                        response = HandleSetStaticFlags(parameters);
                        break;
                    case "get_static_flags":
                        response = HandleGetStaticFlags(parameters);
                        break;
                    case "optimize_audio":
                        response = HandleOptimizeAudio(parameters);
                        break;
                    case "find_duplicate_materials":
                        response = HandleFindDuplicateMaterials(parameters);
                        break;
                    case "analyze_overdraw":
                        response = HandleAnalyzeOverdraw(parameters);
                        break;
                    case "set_lod_group":
                        response = HandleSetLodGroup(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail($"Unknown action: {action}. Valid actions: analyze_scene, optimize_textures, optimize_mesh_compression, find_large_assets, set_static_flags, get_static_flags, optimize_audio, find_duplicate_materials, analyze_overdraw, set_lod_group");
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
        /// Analyze scene for performance bottlenecks (high-poly meshes, excessive materials, dynamic lights).
        /// </summary>
        private ToolResponse HandleAnalyzeScene(JObject parameters)
        {
            int polyThreshold = ToolHelpers.GetOptionalInt(parameters, "polyThreshold", 10000);
            int materialThreshold = ToolHelpers.GetOptionalInt(parameters, "materialThreshold", 5);
            int limit = ToolHelpers.GetOptionalInt(parameters, "limit", 50);

            var renderers = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
            var issues = new List<object>();
            int totalTris = 0;
            int totalMats = 0;

            foreach (var r in renderers)
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                {
                    int tris = mf.sharedMesh.triangles.Length / 3;
                    totalTris += tris;
                    if (tris > polyThreshold && issues.Count < limit)
                    {
                        issues.Add(new
                        {
                            type = "HighPoly",
                            severity = "high",
                            gameObject = r.name,
                            path = GetGameObjectPath(r.gameObject),
                            triangles = tris
                        });
                    }
                }

                var smr = r as SkinnedMeshRenderer;
                if (smr != null && smr.sharedMesh != null)
                {
                    int tris = smr.sharedMesh.triangles.Length / 3;
                    totalTris += tris;
                    if (tris > polyThreshold && issues.Count < limit)
                    {
                        issues.Add(new
                        {
                            type = "HighPoly",
                            severity = "high",
                            gameObject = r.name,
                            path = GetGameObjectPath(r.gameObject),
                            triangles = tris
                        });
                    }
                }

                int matCount = r.sharedMaterials.Length;
                totalMats += matCount;
                if (matCount > materialThreshold && issues.Count < limit)
                {
                    issues.Add(new
                    {
                        type = "ExcessiveMaterials",
                        severity = "medium",
                        gameObject = r.name,
                        path = GetGameObjectPath(r.gameObject),
                        materialCount = matCount
                    });
                }
            }

            // Check dynamic lights
            var lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
            int realtimeLights = 0;
            foreach (var light in lights)
            {
                if (light.lightmapBakeType != LightmapBakeType.Baked)
                {
                    realtimeLights++;
                    if (realtimeLights > 4 && issues.Count < limit)
                    {
                        issues.Add(new
                        {
                            type = "ExcessiveRealtimeLights",
                            severity = "medium",
                            gameObject = light.name,
                            path = GetGameObjectPath(light.gameObject),
                            lightType = light.type.ToString(),
                            bakeType = light.lightmapBakeType.ToString()
                        });
                    }
                }
            }

            // Sort by severity
            var sortedIssues = issues.OrderBy(i =>
            {
                var severity = ((dynamic)i).severity as string;
                return severity == "high" ? 0 : severity == "medium" ? 1 : 2;
            }).ToList();

            return ToolResponse.OkWithData(new
            {
                totalRenderers = renderers.Length,
                totalTriangles = totalTris,
                totalMaterialSlots = totalMats,
                totalLights = lights.Length,
                realtimeLights,
                issueCount = sortedIssues.Count,
                issues = sortedIssues
            }, $"Scene analysis complete: {sortedIssues.Count} issues found");
        }

        /// <summary>
        /// Batch optimize texture import settings (maxSize, compression).
        /// </summary>
        private ToolResponse HandleOptimizeTextures(JObject parameters)
        {
            string folder = ToolHelpers.GetOptionalString(parameters, "folder", "Assets");
            int maxSize = ToolHelpers.GetOptionalInt(parameters, "maxSize", 2048);
            string compressionStr = ToolHelpers.GetOptionalString(parameters, "compression", "Normal");

            TextureImporterCompression compression;
            switch (compressionStr.ToLowerInvariant())
            {
                case "none": compression = TextureImporterCompression.Uncompressed; break;
                case "lowquality": compression = TextureImporterCompression.CompressedLQ; break;
                case "normal": compression = TextureImporterCompression.Compressed; break;
                case "highquality": compression = TextureImporterCompression.CompressedHQ; break;
                default: return ToolResponse.Fail($"Invalid compression: {compressionStr}. Valid: None, LowQuality, Normal, HighQuality");
            }

            var guids = AssetDatabase.FindAssets("t:Texture2D", new[] { folder });
            var modified = new List<object>();

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var guid in guids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                    if (importer == null) continue;

                    bool changed = false;

                    if (importer.maxTextureSize > maxSize)
                    {
                        importer.maxTextureSize = maxSize;
                        changed = true;
                    }

                    if (importer.textureCompression != compression &&
                        importer.textureType == TextureImporterType.Default)
                    {
                        importer.textureCompression = compression;
                        changed = true;
                    }

                    if (changed)
                    {
                        importer.SaveAndReimport();
                        modified.Add(new { path, name = Path.GetFileName(path) });
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
            }

            return ToolResponse.OkWithData(new
            {
                totalScanned = guids.Length,
                modifiedCount = modified.Count,
                maxSize,
                compression = compressionStr,
                modified
            }, $"Optimized {modified.Count} textures out of {guids.Length} scanned");
        }

        /// <summary>
        /// Set mesh compression for 3D model importers.
        /// </summary>
        private ToolResponse HandleOptimizeMeshCompression(JObject parameters)
        {
            string folder = ToolHelpers.GetOptionalString(parameters, "folder", "Assets");
            string compressionStr = ToolHelpers.GetOptionalString(parameters, "compression", "Medium");

            ModelImporterMeshCompression comp;
            switch (compressionStr.ToLowerInvariant())
            {
                case "off": comp = ModelImporterMeshCompression.Off; break;
                case "low": comp = ModelImporterMeshCompression.Low; break;
                case "medium": comp = ModelImporterMeshCompression.Medium; break;
                case "high": comp = ModelImporterMeshCompression.High; break;
                default: return ToolResponse.Fail($"Invalid compression: {compressionStr}. Valid: Off, Low, Medium, High");
            }

            var guids = AssetDatabase.FindAssets("t:Model", new[] { folder });
            var modified = new List<object>();

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var guid in guids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var importer = AssetImporter.GetAtPath(path) as ModelImporter;
                    if (importer == null) continue;

                    if (importer.meshCompression != comp)
                    {
                        importer.meshCompression = comp;
                        importer.SaveAndReimport();
                        modified.Add(new { path, name = Path.GetFileName(path) });
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
            }

            return ToolResponse.OkWithData(new
            {
                totalScanned = guids.Length,
                modifiedCount = modified.Count,
                compression = comp.ToString(),
                modified
            }, $"Set mesh compression to {comp} on {modified.Count} models");
        }

        /// <summary>
        /// Find assets exceeding a specified file size threshold.
        /// </summary>
        private ToolResponse HandleFindLargeAssets(JObject parameters)
        {
            int minSizeKB = ToolHelpers.GetOptionalInt(parameters, "minSizeKB", 1024);
            string folder = ToolHelpers.GetOptionalString(parameters, "folder", "Assets");
            int limit = ToolHelpers.GetOptionalInt(parameters, "limit", 50);

            var guids = AssetDatabase.FindAssets("", new[] { folder });
            var largeAssets = new List<object>();

            foreach (var guid in guids)
            {
                if (largeAssets.Count >= limit) break;

                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.StartsWith("Assets/")) continue;

                var fullPath = Path.Combine(Directory.GetCurrentDirectory(), path);
                if (!File.Exists(fullPath)) continue;

                var sizeKB = (int)(new FileInfo(fullPath).Length / 1024);
                if (sizeKB >= minSizeKB)
                {
                    var asset = AssetDatabase.LoadMainAssetAtPath(path);
                    largeAssets.Add(new
                    {
                        path,
                        name = Path.GetFileName(path),
                        type = asset != null ? asset.GetType().Name : "Unknown",
                        sizeKB,
                        sizeMB = Math.Round(sizeKB / 1024.0, 2)
                    });
                }
            }

            // Sort by size descending
            var sorted = largeAssets.OrderByDescending(a => ((dynamic)a).sizeKB).ToList();

            return ToolResponse.OkWithData(new
            {
                threshold = $"{minSizeKB}KB",
                count = sorted.Count,
                assets = sorted
            }, $"Found {sorted.Count} assets larger than {minSizeKB}KB");
        }

        /// <summary>
        /// Set static editor flags on a GameObject (and optionally its children).
        /// </summary>
        private ToolResponse HandleSetStaticFlags(JObject parameters)
        {
            string target = ToolHelpers.GetRequiredString(parameters, "target");
            string flagsStr = ToolHelpers.GetOptionalString(parameters, "flags", "Everything");
            bool includeChildren = ToolHelpers.GetOptionalBool(parameters, "includeChildren", false);

            var go = ToolHelpers.FindGameObject(target);
            if (go == null)
                return ToolResponse.Fail($"GameObject not found: {target}");

            if (!Enum.TryParse<StaticEditorFlags>(flagsStr, true, out var staticFlags))
                return ToolResponse.Fail($"Invalid flags: {flagsStr}. Valid: Everything, Nothing, BatchingStatic, OccludeeStatic, OccluderStatic, NavigationStatic, ReflectionProbeStatic");

            var targets = new List<GameObject> { go };
            if (includeChildren)
                targets.AddRange(go.GetComponentsInChildren<Transform>(true).Select(t => t.gameObject));

            // Remove duplicates (root is also in GetComponentsInChildren)
            targets = targets.Distinct().ToList();

            foreach (var t in targets)
            {
                Undo.RecordObject(t, "Set Static Flags");
                GameObjectUtility.SetStaticEditorFlags(t, staticFlags);
            }

            return ToolResponse.OkWithData(new
            {
                gameObject = go.name,
                flags = staticFlags.ToString(),
                affectedCount = targets.Count
            }, $"Set static flags '{staticFlags}' on {targets.Count} GameObjects");
        }

        /// <summary>
        /// Get the current static editor flags of a GameObject.
        /// </summary>
        private ToolResponse HandleGetStaticFlags(JObject parameters)
        {
            string target = ToolHelpers.GetRequiredString(parameters, "target");

            var go = ToolHelpers.FindGameObject(target);
            if (go == null)
                return ToolResponse.Fail($"GameObject not found: {target}");

            var flags = GameObjectUtility.GetStaticEditorFlags(go);

            return ToolResponse.OkWithData(new
            {
                gameObject = go.name,
                path = GetGameObjectPath(go),
                isStatic = go.isStatic,
                flags = flags.ToString(),
                flagsList = flags.ToString().Split(',').Select(f => f.Trim()).Where(f => !string.IsNullOrEmpty(f)).ToArray()
            }, $"Static flags for '{go.name}': {flags}");
        }

        /// <summary>
        /// Batch set audio clip compression format and load type.
        /// </summary>
        private ToolResponse HandleOptimizeAudio(JObject parameters)
        {
            string folder = ToolHelpers.GetOptionalString(parameters, "folder", "Assets");
            string compressionFormatStr = ToolHelpers.GetOptionalString(parameters, "compressionFormat", "Vorbis");
            string loadTypeStr = ToolHelpers.GetOptionalString(parameters, "loadType", "CompressedInMemory");

            if (!Enum.TryParse<AudioCompressionFormat>(compressionFormatStr, true, out var compressionFormat))
                return ToolResponse.Fail($"Invalid compressionFormat: {compressionFormatStr}. Valid: PCM, Vorbis, ADPCM");

            if (!Enum.TryParse<AudioClipLoadType>(loadTypeStr, true, out var loadType))
                return ToolResponse.Fail($"Invalid loadType: {loadTypeStr}. Valid: DecompressOnLoad, CompressedInMemory, Streaming");

            var guids = AssetDatabase.FindAssets("t:AudioClip", new[] { folder });
            var modified = new List<object>();

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var guid in guids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var importer = AssetImporter.GetAtPath(path) as AudioImporter;
                    if (importer == null) continue;

                    var settings = importer.defaultSampleSettings;
                    if (settings.compressionFormat == compressionFormat && settings.loadType == loadType)
                        continue;

                    settings.compressionFormat = compressionFormat;
                    settings.loadType = loadType;
                    importer.defaultSampleSettings = settings;
                    importer.SaveAndReimport();
                    modified.Add(new { path, name = Path.GetFileName(path) });
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
            }

            return ToolResponse.OkWithData(new
            {
                totalScanned = guids.Length,
                modifiedCount = modified.Count,
                compressionFormat = compressionFormat.ToString(),
                loadType = loadType.ToString(),
                modified
            }, $"Optimized {modified.Count} audio clips");
        }

        /// <summary>
        /// Find materials with identical shader and approximate properties (potential duplicates).
        /// </summary>
        private ToolResponse HandleFindDuplicateMaterials(JObject parameters)
        {
            int limit = ToolHelpers.GetOptionalInt(parameters, "limit", 50);

            var guids = AssetDatabase.FindAssets("t:Material");
            var matInfos = new List<(string path, string key)>();

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.StartsWith("Assets/")) continue;

                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null || mat.shader == null) continue;

                // Build a fingerprint from shader + main color + render queue
                string colorStr = "none";
                if (mat.HasProperty("_Color"))
                    colorStr = mat.color.ToString();
                else if (mat.HasProperty("_BaseColor"))
                    colorStr = mat.GetColor("_BaseColor").ToString();

                var key = $"{mat.shader.name}|{colorStr}|{mat.renderQueue}";
                matInfos.Add((path, key));
            }

            var duplicates = matInfos
                .GroupBy(m => m.key)
                .Where(g => g.Count() > 1)
                .Take(limit)
                .Select(g => new
                {
                    shader = g.Key.Split('|')[0],
                    count = g.Count(),
                    paths = g.Select(m => m.path).ToArray()
                })
                .ToArray();

            return ToolResponse.OkWithData(new
            {
                totalMaterials = matInfos.Count,
                duplicateGroups = duplicates.Length,
                groups = duplicates,
                note = "Comparison is approximate (shader + main color + render queue). Manual review recommended."
            }, $"Found {duplicates.Length} groups of potentially duplicate materials");
        }

        /// <summary>
        /// Analyze transparent objects that may cause overdraw performance issues.
        /// </summary>
        private ToolResponse HandleAnalyzeOverdraw(JObject parameters)
        {
            int limit = ToolHelpers.GetOptionalInt(parameters, "limit", 50);

            var renderers = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
            var transparentObjects = new List<object>();

            foreach (var r in renderers)
            {
                if (transparentObjects.Count >= limit) break;

                foreach (var mat in r.sharedMaterials)
                {
                    if (mat != null && mat.renderQueue >= 2500) // Transparent queue starts at 2500
                    {
                        transparentObjects.Add(new
                        {
                            gameObject = r.name,
                            path = GetGameObjectPath(r.gameObject),
                            material = mat.name,
                            renderQueue = mat.renderQueue,
                            shader = mat.shader != null ? mat.shader.name : "null"
                        });
                        break; // Only report once per renderer
                    }
                }
            }

            string riskLevel = transparentObjects.Count > 20 ? "high" :
                               transparentObjects.Count > 10 ? "medium" : "low";

            return ToolResponse.OkWithData(new
            {
                transparentObjectCount = transparentObjects.Count,
                overdrawRisk = riskLevel,
                objects = transparentObjects
            }, $"Found {transparentObjects.Count} transparent objects (overdraw risk: {riskLevel})");
        }

        /// <summary>
        /// Add or configure a LOD Group on a GameObject.
        /// </summary>
        private ToolResponse HandleSetLodGroup(JObject parameters)
        {
            string target = ToolHelpers.GetRequiredString(parameters, "target");
            string lodDistancesStr = ToolHelpers.GetOptionalString(parameters, "lodDistances", "0.6,0.3,0.1");

            var go = ToolHelpers.FindGameObject(target);
            if (go == null)
                return ToolResponse.Fail($"GameObject not found: {target}");

            var distanceParts = lodDistancesStr.Split(',');
            var distances = new List<float>();
            foreach (var part in distanceParts)
            {
                if (!float.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float dist))
                    return ToolResponse.Fail($"Invalid LOD distance value: '{part.Trim()}'. Expected comma-separated floats (e.g. '0.6,0.3,0.1')");
                if (dist < 0f || dist > 1f)
                    return ToolResponse.Fail($"LOD distance {dist} out of range. Must be between 0 and 1.");
                distances.Add(dist);
            }

            var lodGroup = go.GetComponent<LODGroup>();
            if (lodGroup == null)
                lodGroup = Undo.AddComponent<LODGroup>(go);
            else
                Undo.RecordObject(lodGroup, "Set LOD Group");

            var renderers = go.GetComponentsInChildren<Renderer>();
            var lods = new LOD[distances.Count + 1];
            for (int i = 0; i < distances.Count; i++)
                lods[i] = new LOD(distances[i], i == 0 ? renderers : new Renderer[0]);
            lods[distances.Count] = new LOD(0, new Renderer[0]); // Culled LOD

            lodGroup.SetLODs(lods);
            lodGroup.RecalculateBounds();

            return ToolResponse.OkWithData(new
            {
                gameObject = go.name,
                lodLevels = lods.Length,
                distances = lodDistancesStr,
                renderersAssigned = renderers.Length
            }, $"Set LOD Group on '{go.name}' with {lods.Length} levels");
        }

        #endregion

        #region Helpers

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
