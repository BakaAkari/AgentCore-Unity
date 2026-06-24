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

namespace AgentCore.Editor.Tools.Native.Utility
{
    /// <summary>
    /// Inspect and configure ModelImporter settings for 3D model assets.
    /// </summary>
    [AgentTool("manage_model_import",
        Description = "Inspect and configure ModelImporter settings for 3D model assets",
        Category = "Utility",
        Visibility = ToolVisibility.OnDemand,
        RequiresMainThread = true)]
    public class ManageModelImportTool : IAgentTool
    {
        private const string ValidActions = "get_settings, set_settings, set_settings_batch, find_assets, get_mesh_info, get_materials_info, get_animations_info, set_animation_clips, get_rig_info, set_rig";

        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": { ""type"": ""string"", ""enum"": [""get_settings"", ""set_settings"", ""set_settings_batch"", ""find_assets"", ""get_mesh_info"", ""get_materials_info"", ""get_animations_info"", ""set_animation_clips"", ""get_rig_info"", ""set_rig""] },
                ""asset_path"": { ""type"": ""string"" },
                ""meshCompression"": { ""type"": ""string"" },
                ""isReadable"": { ""type"": ""boolean"" },
                ""optimizeMesh"": { ""type"": ""boolean"" },
                ""importBlendShapes"": { ""type"": ""boolean"" },
                ""importVisibility"": { ""type"": ""boolean"" },
                ""importCameras"": { ""type"": ""boolean"" },
                ""importLights"": { ""type"": ""boolean"" },
                ""scaleFactor"": { ""type"": ""number"" },
                ""animationType"": { ""type"": ""string"" },
                ""materialImportMode"": { ""type"": ""string"" },
                ""avatarSetup"": { ""type"": ""string"" },
                ""clips"": { ""type"": ""array"" },
                ""search"": { ""type"": ""string"" },
                ""folder"": { ""type"": ""string"" },
                ""max_results"": { ""type"": ""integer"" },
                ""items"": { ""type"": ""array"" }
            },
            ""required"": [""action""]
        }");

        /// <summary>
        /// Tool metadata for automatic registration.
        /// </summary>
        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_model_import",
            description: "Inspect and configure ModelImporter settings for 3D model assets",
            category: "Utility",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        /// <summary>
        /// Executes a ModelImporter management action.
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
                    case "get_settings": response = HandleGetSettings(parameters); break;
                    case "set_settings": response = HandleSetSettings(parameters); break;
                    case "set_settings_batch": response = HandleSetSettingsBatch(parameters); break;
                    case "find_assets": response = HandleFindAssets(parameters); break;
                    case "get_mesh_info": response = HandleGetMeshInfo(parameters); break;
                    case "get_materials_info": response = HandleGetMaterialsInfo(parameters); break;
                    case "get_animations_info": response = HandleGetAnimationsInfo(parameters); break;
                    case "set_animation_clips": response = HandleSetAnimationClips(parameters); break;
                    case "get_rig_info": response = HandleGetRigInfo(parameters); break;
                    case "set_rig": response = HandleSetRig(parameters); break;
                    default: response = ToolResponse.Fail($"Unknown action: {action}. Valid actions: {ValidActions}"); break;
                }
            }
            catch (Exception ex)
            {
                response = ToolResponse.Fail($"Error: {ex.Message}");
            }

            sw.Stop();
            return Task.FromResult(response.ToToolResult(sw.Elapsed.TotalMilliseconds));
        }

        private static ToolResponse HandleGetSettings(JObject parameters)
        {
            var importer = GetModelImporter(ToolHelpers.GetRequiredString(parameters, "asset_path"), out var path, out var error);
            return importer == null ? ToolResponse.Fail(error) : ToolResponse.OkWithData(SerializeImporter(importer, path), $"Model import settings for '{path}'.");
        }

        private static ToolResponse HandleSetSettings(JObject parameters)
        {
            var importer = GetModelImporter(ToolHelpers.GetRequiredString(parameters, "asset_path"), out var path, out var error);
            if (importer == null) return ToolResponse.Fail(error);
            ApplyCommonSettings(importer, parameters);
            importer.SaveAndReimport();
            return ToolResponse.OkWithData(SerializeImporter(importer, path), $"Updated ModelImporter settings for '{path}'.");
        }

        private static ToolResponse HandleSetSettingsBatch(JObject parameters)
        {
            var items = ToolHelpers.GetOptionalArray(parameters, "items");
            if (items == null) return ToolResponse.Fail("Required parameter 'items' is missing or not an array.");

            var succeeded = new JArray();
            var failed = new JArray();
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var token in items.OfType<JObject>())
                {
                    var assetPath = token["asset_path"]?.ToString();
                    var importer = GetModelImporter(assetPath, out var path, out var error);
                    if (importer == null)
                    {
                        failed.Add(new JObject { ["asset_path"] = assetPath ?? string.Empty, ["error"] = error });
                        continue;
                    }
                    try
                    {
                        ApplyCommonSettings(importer, token);
                        importer.SaveAndReimport();
                        succeeded.Add(new JObject { ["asset_path"] = path });
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new JObject { ["asset_path"] = path, ["error"] = ex.Message });
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
            }
            return ToolResponse.OkWithData(new JObject { ["succeeded"] = succeeded, ["failed"] = failed }, $"Updated {succeeded.Count} model importers; {failed.Count} failed.");
        }

        private static ToolResponse HandleFindAssets(JObject parameters)
        {
            var search = ToolHelpers.GetOptionalString(parameters, "search", string.Empty);
            var folder = ToolHelpers.NormalizeAssetPath(ToolHelpers.GetOptionalString(parameters, "folder", "Assets"));
            var maxResults = ToolHelpers.GetOptionalInt(parameters, "max_results", 50);
            var query = string.IsNullOrWhiteSpace(search) ? "t:Model" : $"{search} t:Model";
            var guids = AssetDatabase.FindAssets(query, new[] { folder });
            var results = new JArray();
            foreach (var guid in guids.Take(maxResults))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var importer = AssetImporter.GetAtPath(path) as ModelImporter;
                if (importer == null) continue;
                results.Add(new JObject { ["path"] = path, ["guid"] = guid, ["meshCompression"] = importer.meshCompression.ToString(), ["animationType"] = importer.animationType.ToString() });
            }
            return ToolResponse.OkWithData(new JObject { ["results"] = results, ["total_found"] = guids.Length, ["returned"] = results.Count }, $"Found {guids.Length} model assets.");
        }

        private static ToolResponse HandleGetMeshInfo(JObject parameters)
        {
            var path = NormalizeRequiredPath(parameters);
            var meshes = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().ToArray();
            if (meshes.Length == 0) return ToolResponse.Fail($"No Mesh sub-assets found in: {path}");
            var array = new JArray(meshes.Select(m => new JObject
            {
                ["name"] = m.name,
                ["vertices"] = m.vertexCount,
                ["triangles"] = m.triangles.Length / 3,
                ["submeshes"] = m.subMeshCount,
                ["bounds"] = SerializeBounds(m.bounds)
            }));
            return ToolResponse.OkWithData(new JObject { ["asset_path"] = path, ["mesh_count"] = meshes.Length, ["meshes"] = array }, $"Mesh info for '{path}'.");
        }

        private static ToolResponse HandleGetMaterialsInfo(JObject parameters)
        {
            var importer = GetModelImporter(ToolHelpers.GetRequiredString(parameters, "asset_path"), out var path, out var error);
            if (importer == null) return ToolResponse.Fail(error);
            var materials = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Material>().Select(m => new JObject
            {
                ["name"] = m.name,
                ["shader"] = m.shader != null ? m.shader.name : string.Empty,
                ["instance_id"] = m.GetInstanceID()
            });
            return ToolResponse.OkWithData(new JObject
            {
                ["asset_path"] = path,
                ["materialImportMode"] = importer.materialImportMode.ToString(),
                ["materialLocation"] = importer.materialLocation.ToString(),
                ["materials"] = new JArray(materials)
            }, $"Material import info for '{path}'.");
        }

        private static ToolResponse HandleGetAnimationsInfo(JObject parameters)
        {
            var importer = GetModelImporter(ToolHelpers.GetRequiredString(parameters, "asset_path"), out var path, out var error);
            if (importer == null) return ToolResponse.Fail(error);
            var clips = new JArray(importer.clipAnimations.Select(c => SerializeClip(c)));
            var importedClips = new JArray(AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().Select(c => new JObject
            {
                ["name"] = c.name,
                ["length"] = c.length,
                ["frameRate"] = c.frameRate,
                ["legacy"] = c.legacy
            }));
            return ToolResponse.OkWithData(new JObject { ["asset_path"] = path, ["clipAnimations"] = clips, ["importedClips"] = importedClips }, $"Animation import info for '{path}'.");
        }

        private static ToolResponse HandleSetAnimationClips(JObject parameters)
        {
            var importer = GetModelImporter(ToolHelpers.GetRequiredString(parameters, "asset_path"), out var path, out var error);
            if (importer == null) return ToolResponse.Fail(error);
            var clipsToken = ToolHelpers.GetOptionalArray(parameters, "clips");
            if (clipsToken == null) return ToolResponse.Fail("Required parameter 'clips' is missing or not an array.");
            var clips = new List<ModelImporterClipAnimation>();
            foreach (var clipObj in clipsToken.OfType<JObject>())
            {
                var clip = new ModelImporterClipAnimation
                {
                    name = clipObj["name"]?.ToString() ?? "Clip",
                    firstFrame = clipObj["firstFrame"]?.Value<float>() ?? clipObj["first_frame"]?.Value<float>() ?? 0f,
                    lastFrame = clipObj["lastFrame"]?.Value<float>() ?? clipObj["last_frame"]?.Value<float>() ?? 0f,
                    loopTime = clipObj["loop"]?.Value<bool>() ?? clipObj["loopTime"]?.Value<bool>() ?? false
                };
                clips.Add(clip);
            }
            importer.clipAnimations = clips.ToArray();
            importer.SaveAndReimport();
            return ToolResponse.OkWithData(new JObject { ["asset_path"] = path, ["clip_count"] = clips.Count, ["clips"] = new JArray(clips.Select(SerializeClip)) }, $"Set {clips.Count} animation clips for '{path}'.");
        }

        private static ToolResponse HandleGetRigInfo(JObject parameters)
        {
            var importer = GetModelImporter(ToolHelpers.GetRequiredString(parameters, "asset_path"), out var path, out var error);
            if (importer == null) return ToolResponse.Fail(error);
            return ToolResponse.OkWithData(new JObject
            {
                ["asset_path"] = path,
                ["animationType"] = importer.animationType.ToString(),
                ["avatarSetup"] = importer.avatarSetup.ToString(),
                ["humanDescription"] = new JObject
                {
                    ["human_count"] = importer.humanDescription.human?.Length ?? 0,
                    ["skeleton_count"] = importer.humanDescription.skeleton?.Length ?? 0,
                    ["upperArmTwist"] = importer.humanDescription.upperArmTwist,
                    ["lowerArmTwist"] = importer.humanDescription.lowerArmTwist,
                    ["upperLegTwist"] = importer.humanDescription.upperLegTwist,
                    ["lowerLegTwist"] = importer.humanDescription.lowerLegTwist
                }
            }, $"Rig info for '{path}'.");
        }

        private static ToolResponse HandleSetRig(JObject parameters)
        {
            var importer = GetModelImporter(ToolHelpers.GetRequiredString(parameters, "asset_path"), out var path, out var error);
            if (importer == null) return ToolResponse.Fail(error);
            if (parameters["animationType"] != null) importer.animationType = ParseEnum<ModelImporterAnimationType>(ToolHelpers.GetRequiredString(parameters, "animationType"));
            if (parameters["avatarSetup"] != null) importer.avatarSetup = ParseEnum<ModelImporterAvatarSetup>(ToolHelpers.GetRequiredString(parameters, "avatarSetup"));
            importer.SaveAndReimport();
            return ToolResponse.OkWithData(SerializeImporter(importer, path), $"Updated rig settings for '{path}'.");
        }

        private static void ApplyCommonSettings(ModelImporter importer, JObject parameters)
        {
            if (parameters["meshCompression"] != null) importer.meshCompression = ParseEnum<ModelImporterMeshCompression>(ToolHelpers.GetRequiredString(parameters, "meshCompression"));
            if (parameters["isReadable"] != null) importer.isReadable = ToolHelpers.GetOptionalBool(parameters, "isReadable", importer.isReadable);
            if (parameters["importBlendShapes"] != null) importer.importBlendShapes = ToolHelpers.GetOptionalBool(parameters, "importBlendShapes", importer.importBlendShapes);
            if (parameters["importVisibility"] != null) importer.importVisibility = ToolHelpers.GetOptionalBool(parameters, "importVisibility", importer.importVisibility);
            if (parameters["importCameras"] != null) importer.importCameras = ToolHelpers.GetOptionalBool(parameters, "importCameras", importer.importCameras);
            if (parameters["importLights"] != null) importer.importLights = ToolHelpers.GetOptionalBool(parameters, "importLights", importer.importLights);
            if (parameters["scaleFactor"] != null) importer.globalScale = ToolHelpers.GetOptionalFloat(parameters, "scaleFactor", importer.globalScale);
            if (parameters["animationType"] != null) importer.animationType = ParseEnum<ModelImporterAnimationType>(ToolHelpers.GetRequiredString(parameters, "animationType"));
            if (parameters["materialImportMode"] != null) importer.materialImportMode = ParseEnum<ModelImporterMaterialImportMode>(ToolHelpers.GetRequiredString(parameters, "materialImportMode"));
            if (parameters["optimizeMesh"] != null)
            {
                var optimize = ToolHelpers.GetOptionalBool(parameters, "optimizeMesh", true);
                importer.optimizeMeshPolygons = optimize;
                importer.optimizeMeshVertices = optimize;
            }
        }

        private static ModelImporter GetModelImporter(string assetPath, out string normalizedPath, out string error)
        {
            normalizedPath = string.IsNullOrWhiteSpace(assetPath) ? assetPath : ToolHelpers.NormalizeAssetPath(assetPath);
            error = null;
            if (string.IsNullOrWhiteSpace(normalizedPath)) { error = "Required parameter 'asset_path' is missing or empty."; return null; }
            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(normalizedPath))) { error = $"Asset not found: {normalizedPath}"; return null; }
            var importer = AssetImporter.GetAtPath(normalizedPath) as ModelImporter;
            if (importer == null) error = $"Asset is not imported by ModelImporter: {normalizedPath}";
            return importer;
        }

        private static string NormalizeRequiredPath(JObject parameters)
        {
            var path = ToolHelpers.NormalizeAssetPath(ToolHelpers.GetRequiredString(parameters, "asset_path"));
            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path))) throw new ArgumentException($"Asset not found: {path}");
            return path;
        }

        private static JObject SerializeImporter(ModelImporter importer, string path)
        {
            return new JObject
            {
                ["asset_path"] = path,
                ["guid"] = AssetDatabase.AssetPathToGUID(path),
                ["meshCompression"] = importer.meshCompression.ToString(),
                ["isReadable"] = importer.isReadable,
                ["optimizeMeshPolygons"] = importer.optimizeMeshPolygons,
                ["optimizeMeshVertices"] = importer.optimizeMeshVertices,
                ["importBlendShapes"] = importer.importBlendShapes,
                ["importVisibility"] = importer.importVisibility,
                ["importCameras"] = importer.importCameras,
                ["importLights"] = importer.importLights,
                ["scaleFactor"] = importer.globalScale,
                ["animationType"] = importer.animationType.ToString(),
                ["materialImportMode"] = importer.materialImportMode.ToString(),
                ["avatarSetup"] = importer.avatarSetup.ToString(),
                ["importer_type"] = importer.GetType().Name
            };
        }

        private static JObject SerializeClip(ModelImporterClipAnimation clip)
        {
            return new JObject
            {
                ["name"] = clip.name,
                ["firstFrame"] = clip.firstFrame,
                ["lastFrame"] = clip.lastFrame,
                ["loop"] = clip.loopTime
            };
        }

        private static JObject SerializeBounds(Bounds bounds)
        {
            return new JObject
            {
                ["center"] = ToolHelpers.Vector3ToJson(bounds.center),
                ["size"] = ToolHelpers.Vector3ToJson(bounds.size)
            };
        }

        private static T ParseEnum<T>(string value) where T : struct, Enum
        {
            var normalized = (value ?? string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty).Replace("-", string.Empty);
            foreach (var name in Enum.GetNames(typeof(T)))
            {
                if (string.Equals(name, normalized, StringComparison.OrdinalIgnoreCase)) return (T)Enum.Parse(typeof(T), name);
            }
            if (Enum.TryParse<T>(value, true, out var result)) return result;
            throw new ArgumentException($"Invalid {typeof(T).Name}: {value}. Valid values: {string.Join(", ", Enum.GetNames(typeof(T)))}");
        }
    }
}
