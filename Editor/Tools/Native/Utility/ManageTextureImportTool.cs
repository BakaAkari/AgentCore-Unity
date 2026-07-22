using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    /// Inspect and configure TextureImporter settings for texture assets.
    /// </summary>
    [AgentTool("manage_texture_import",
        Description = "TextureImporter settings for image assets (PNG, JPG, PSD, TGA, EXR, etc). " +
                      "Actions: get_settings (full import config), set_settings (modify import parameters), " +
                      "set_settings_batch (apply to multiple textures), find_assets (discover texture files), " +
                      "get_info (actual size, format, memory, import warnings), set_type (Default/Sprite/NormalMap/Lightmap/etc), " +
                      "set_platform_settings (per-platform format/maxSize/compression), get_platform_settings, " +
                      "set_sprite_settings (sprite mode, pixels per unit, pivot, packing tag), find_by_size (find textures by dimension). " +
                      "USE FOR: configuring texture compression per platform (Android/iOS/Standalone), setting up sprites for 2D, " +
                      "converting texture types (Normal Map, Lightmap), adjusting max size for optimization, checking texture memory usage. " +
                      "NOT FOR: creating textures (import the file instead), material texture assignment (use manage_material), " +
                      "texture content editing (use external tools). " +
                      "ACTIVATE WHEN: user mentions 'texture import', 'texture compression', 'sprite settings', 'max texture size', " +
                      "'normal map', 'platform override', 'texture memory'.",
        Category = "Utility",
        Visibility = ToolVisibility.OnDemand,
        RequiresMainThread = true)]
    public class ManageTextureImportTool : IAgentTool
    {
        private const string ValidActions = "get_settings, set_settings, set_settings_batch, find_assets, get_info, set_type, set_platform_settings, get_platform_settings, set_sprite_settings, find_by_size";

        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": { ""type"": ""string"", ""enum"": [""get_settings"", ""set_settings"", ""set_settings_batch"", ""find_assets"", ""get_info"", ""set_type"", ""set_platform_settings"", ""get_platform_settings"", ""set_sprite_settings"", ""find_by_size""] },
                ""asset_path"": { ""type"": ""string"" },
                ""texture_type"": { ""type"": ""string"" },
                ""max_size"": { ""type"": ""integer"" },
                ""filter_mode"": { ""type"": ""string"" },
                ""compression"": { ""type"": ""string"" },
                ""mipmap_enabled"": { ""type"": ""boolean"" },
                ""is_readable"": { ""type"": ""boolean"" },
                ""sRGB_texture"": { ""type"": ""boolean"" },
                ""alpha_source"": { ""type"": ""string"" },
                ""wrap_mode"": { ""type"": ""string"" },
                ""platform"": { ""type"": ""string"" },
                ""format"": { ""type"": ""string"" },
                ""compression_quality"": { ""type"": ""integer"" },
                ""overridden"": { ""type"": ""boolean"" },
                ""pixels_per_unit"": { ""type"": ""number"" },
                ""sprite_mode"": { ""type"": ""string"" },
                ""mesh_type"": { ""type"": ""string"" },
                ""extrude_edges"": { ""type"": ""integer"" },
                ""pivot"": { ""description"": ""Sprite pivot as {x,y} or [x,y]"" },
                ""search"": { ""type"": ""string"" },
                ""folder"": { ""type"": ""string"" },
                ""max_results"": { ""type"": ""integer"" },
                ""min_size"": { ""type"": ""integer"" },
                ""items"": { ""type"": ""array"" }
            },
            ""required"": [""action""]
        }");

        /// <summary>
        /// Tool metadata for automatic registration.
        /// </summary>
        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_texture_import",
            description: "Inspect and configure TextureImporter settings for texture assets",
            category: "Utility",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        /// <summary>
        /// Executes a TextureImporter management action.
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
                    case "get_info": response = HandleGetInfo(parameters); break;
                    case "set_type": response = HandleSetType(parameters); break;
                    case "set_platform_settings": response = HandleSetPlatformSettings(parameters); break;
                    case "get_platform_settings": response = HandleGetPlatformSettings(parameters); break;
                    case "set_sprite_settings": response = HandleSetSpriteSettings(parameters); break;
                    case "find_by_size": response = HandleFindBySize(parameters); break;
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
            var importer = GetTextureImporter(ToolHelpers.GetRequiredString(parameters, "asset_path"), out var path, out var error);
            return importer == null ? ToolResponse.Fail(error) : ToolResponse.OkWithData(SerializeImporter(importer, path), $"Texture import settings for '{path}'.");
        }

        private static ToolResponse HandleSetSettings(JObject parameters)
        {
            var importer = GetTextureImporter(ToolHelpers.GetRequiredString(parameters, "asset_path"), out var path, out var error);
            if (importer == null) return ToolResponse.Fail(error);

            // Record Undo on the importer BEFORE mutating its properties so Ctrl+Z restores
            // prior settings. The subsequent reimport itself is not undoable, but the
            // importer property values are.
            Undo.RecordObject(importer, $"Set TextureImporter Settings on {path}");
            ApplyCommonSettings(importer, parameters);
            importer.SaveAndReimport();
            return ToolResponse.OkWithData(SerializeImporter(importer, path), $"Updated TextureImporter settings for '{path}'.");
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
                    var importer = GetTextureImporter(assetPath, out var path, out var error);
                    if (importer == null)
                    {
                        failed.Add(new JObject { ["asset_path"] = assetPath ?? string.Empty, ["error"] = error });
                        continue;
                    }

                    try
                    {
                        Undo.RecordObject(importer, $"Set TextureImporter Settings on {path} (batch)");
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

            return ToolResponse.OkWithData(new JObject { ["succeeded"] = succeeded, ["failed"] = failed }, $"Updated {succeeded.Count} texture importers; {failed.Count} failed.");
        }

        private static ToolResponse HandleFindAssets(JObject parameters)
        {
            var search = ToolHelpers.GetOptionalString(parameters, "search", string.Empty);
            var folder = ToolHelpers.NormalizeAssetPath(ToolHelpers.GetOptionalString(parameters, "folder", "Assets"));
            var maxResults = ToolHelpers.GetOptionalInt(parameters, "max_results", 50);
            var query = string.IsNullOrWhiteSpace(search) ? "t:Texture" : $"{search} t:Texture";
            var results = new JArray();
            var guids = AssetDatabase.FindAssets(query, new[] { folder });
            foreach (var guid in guids.Take(maxResults))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null) continue;
                results.Add(new JObject { ["path"] = path, ["guid"] = guid, ["texture_type"] = importer.textureType.ToString(), ["max_size"] = importer.maxTextureSize });
            }

            return ToolResponse.OkWithData(new JObject { ["results"] = results, ["total_found"] = guids.Length, ["returned"] = results.Count }, $"Found {guids.Length} texture assets.");
        }

        private static ToolResponse HandleGetInfo(JObject parameters)
        {
            var importer = GetTextureImporter(ToolHelpers.GetRequiredString(parameters, "asset_path"), out var path, out var error);
            if (importer == null) return ToolResponse.Fail(error);

            var texture = AssetDatabase.LoadAssetAtPath<Texture>(path);
            var info = SerializeImporter(importer, path);
            if (texture != null)
            {
                info["width"] = texture.width;
                info["height"] = texture.height;
                info["estimated_memory_bytes"] = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(texture);
                if (texture is Texture2D t2d) info["format"] = t2d.format.ToString();
            }

            return ToolResponse.OkWithData(info, $"Texture import info for '{path}'.");
        }

        private static ToolResponse HandleSetType(JObject parameters)
        {
            var importer = GetTextureImporter(ToolHelpers.GetRequiredString(parameters, "asset_path"), out var path, out var error);
            if (importer == null) return ToolResponse.Fail(error);
            Undo.RecordObject(importer, $"Set TextureImporter Type on {path}");
            importer.textureType = ParseEnum<TextureImporterType>(ToolHelpers.GetRequiredString(parameters, "texture_type"));
            importer.SaveAndReimport();
            return ToolResponse.OkWithData(new JObject { ["asset_path"] = path, ["texture_type"] = importer.textureType.ToString() }, $"Set texture type for '{path}'.");
        }

        private static ToolResponse HandleSetPlatformSettings(JObject parameters)
        {
            var importer = GetTextureImporter(ToolHelpers.GetRequiredString(parameters, "asset_path"), out var path, out var error);
            if (importer == null) return ToolResponse.Fail(error);

            var platform = ToolHelpers.GetRequiredString(parameters, "platform");
            Undo.RecordObject(importer, $"Set TextureImporter {platform} Platform Settings on {path}");
            var settings = importer.GetPlatformTextureSettings(platform);
            settings.name = platform;
            if (parameters["overridden"] != null) settings.overridden = ToolHelpers.GetOptionalBool(parameters, "overridden", settings.overridden);
            if (parameters["max_size"] != null) settings.maxTextureSize = ToolHelpers.GetOptionalInt(parameters, "max_size", settings.maxTextureSize);
            if (parameters["compression_quality"] != null) settings.compressionQuality = ToolHelpers.GetOptionalInt(parameters, "compression_quality", settings.compressionQuality);
            if (parameters["format"] != null) settings.format = ParseEnum<TextureImporterFormat>(ToolHelpers.GetRequiredString(parameters, "format"));
            importer.SetPlatformTextureSettings(settings);
            importer.SaveAndReimport();

            return ToolResponse.OkWithData(SerializePlatformSettings(importer.GetPlatformTextureSettings(platform)), $"Updated {platform} texture platform settings for '{path}'.");
        }

        private static ToolResponse HandleGetPlatformSettings(JObject parameters)
        {
            var importer = GetTextureImporter(ToolHelpers.GetRequiredString(parameters, "asset_path"), out _, out var error);
            if (importer == null) return ToolResponse.Fail(error);
            var platform = ToolHelpers.GetRequiredString(parameters, "platform");
            return ToolResponse.OkWithData(SerializePlatformSettings(importer.GetPlatformTextureSettings(platform)), $"Texture platform settings for '{platform}'.");
        }

        private static ToolResponse HandleSetSpriteSettings(JObject parameters)
        {
            var importer = GetTextureImporter(ToolHelpers.GetRequiredString(parameters, "asset_path"), out var path, out var error);
            if (importer == null) return ToolResponse.Fail(error);

            Undo.RecordObject(importer, $"Set Sprite Import Settings on {path}");
            importer.textureType = TextureImporterType.Sprite;
            var textureSettings = ReadTextureSettings(importer);
            if (parameters["pixels_per_unit"] != null) importer.spritePixelsPerUnit = ToolHelpers.GetOptionalFloat(parameters, "pixels_per_unit", importer.spritePixelsPerUnit);
            if (parameters["sprite_mode"] != null) importer.spriteImportMode = ParseEnum<SpriteImportMode>(ToolHelpers.GetRequiredString(parameters, "sprite_mode"));
            if (parameters["mesh_type"] != null) SetTextureSettingsEnum(textureSettings, "spriteMeshType", ToolHelpers.GetRequiredString(parameters, "mesh_type"));
            if (parameters["extrude_edges"] != null) SetTextureSettingsValue(textureSettings, "spriteExtrude", Math.Max(0, ToolHelpers.GetOptionalInt(parameters, "extrude_edges", GetTextureSettingsInt(textureSettings, "spriteExtrude", 0))));
            if (parameters["pivot"] != null) importer.spritePivot = ParseVector2(parameters["pivot"], importer.spritePivot);
            importer.SetTextureSettings(textureSettings);
            importer.SaveAndReimport();

            return ToolResponse.OkWithData(SerializeImporter(importer, path), $"Updated sprite import settings for '{path}'.");
        }

        private static ToolResponse HandleFindBySize(JObject parameters)
        {
            var minSize = ToolHelpers.GetOptionalInt(parameters, "min_size", 0);
            var maxSize = ToolHelpers.GetOptionalInt(parameters, "max_size", int.MaxValue);
            var folder = ToolHelpers.NormalizeAssetPath(ToolHelpers.GetOptionalString(parameters, "folder", "Assets"));
            var maxResults = ToolHelpers.GetOptionalInt(parameters, "max_results", 50);
            var results = new JArray();
            var scanned = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Texture", new[] { folder }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var texture = AssetDatabase.LoadAssetAtPath<Texture>(path);
                if (texture == null) continue;
                scanned++;
                var longest = Math.Max(texture.width, texture.height);
                if (longest < minSize || longest > maxSize) continue;
                results.Add(new JObject { ["path"] = path, ["width"] = texture.width, ["height"] = texture.height, ["longest_side"] = longest });
                if (results.Count >= maxResults) break;
            }

            return ToolResponse.OkWithData(new JObject { ["results"] = results, ["scanned"] = scanned, ["returned"] = results.Count }, $"Found {results.Count} textures in size range.");
        }

        private static void ApplyCommonSettings(TextureImporter importer, JObject parameters)
        {
            if (parameters["texture_type"] != null) importer.textureType = ParseEnum<TextureImporterType>(ToolHelpers.GetRequiredString(parameters, "texture_type"));
            if (parameters["max_size"] != null) importer.maxTextureSize = ToolHelpers.GetOptionalInt(parameters, "max_size", importer.maxTextureSize);
            if (parameters["filter_mode"] != null) importer.filterMode = ParseEnum<FilterMode>(ToolHelpers.GetRequiredString(parameters, "filter_mode"));
            if (parameters["compression"] != null) importer.textureCompression = ParseTextureCompression(ToolHelpers.GetRequiredString(parameters, "compression"));
            if (parameters["mipmap_enabled"] != null) importer.mipmapEnabled = ToolHelpers.GetOptionalBool(parameters, "mipmap_enabled", importer.mipmapEnabled);
            if (parameters["is_readable"] != null) importer.isReadable = ToolHelpers.GetOptionalBool(parameters, "is_readable", importer.isReadable);
            if (parameters["sRGB_texture"] != null) importer.sRGBTexture = ToolHelpers.GetOptionalBool(parameters, "sRGB_texture", importer.sRGBTexture);
            if (parameters["alpha_source"] != null) importer.alphaSource = ParseEnum<TextureImporterAlphaSource>(ToolHelpers.GetRequiredString(parameters, "alpha_source"));
            if (parameters["wrap_mode"] != null) importer.wrapMode = ParseEnum<TextureWrapMode>(ToolHelpers.GetRequiredString(parameters, "wrap_mode"));
        }

        private static TextureImporter GetTextureImporter(string assetPath, out string normalizedPath, out string error)
        {
            normalizedPath = string.IsNullOrWhiteSpace(assetPath) ? assetPath : ToolHelpers.NormalizeAssetPath(assetPath);
            error = null;
            if (string.IsNullOrWhiteSpace(normalizedPath)) { error = "Required parameter 'asset_path' is missing or empty."; return null; }
            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(normalizedPath))) { error = $"Asset not found: {normalizedPath}"; return null; }
            var importer = AssetImporter.GetAtPath(normalizedPath) as TextureImporter;
            if (importer == null) error = $"Asset is not imported by TextureImporter: {normalizedPath}";
            return importer;
        }

        private static JObject SerializeImporter(TextureImporter importer, string path)
        {
            var textureSettings = ReadTextureSettings(importer);
            return new JObject
            {
                ["asset_path"] = path,
                ["guid"] = AssetDatabase.AssetPathToGUID(path),
                ["texture_type"] = importer.textureType.ToString(),
                ["max_size"] = importer.maxTextureSize,
                ["filter_mode"] = importer.filterMode.ToString(),
                ["compression"] = importer.textureCompression.ToString(),
                ["mipmap_enabled"] = importer.mipmapEnabled,
                ["is_readable"] = importer.isReadable,
                ["sRGB_texture"] = importer.sRGBTexture,
                ["alpha_source"] = importer.alphaSource.ToString(),
                ["wrap_mode"] = importer.wrapMode.ToString(),
                ["sprite_mode"] = importer.spriteImportMode.ToString(),
                ["sprite_pixels_per_unit"] = importer.spritePixelsPerUnit,
                ["sprite_mesh_type"] = GetTextureSettingsString(textureSettings, "spriteMeshType", null),
                ["sprite_extrude"] = GetTextureSettingsInt(textureSettings, "spriteExtrude", -1),
                ["importer_type"] = importer.GetType().Name
            };
        }

        private static TextureImporterSettings ReadTextureSettings(TextureImporter importer)
        {
            var settings = new TextureImporterSettings();
            importer.ReadTextureSettings(settings);
            return settings;
        }

        private static void SetTextureSettingsEnum(TextureImporterSettings settings, string memberName, string value)
        {
            var memberType = GetTextureSettingsMemberType(memberName);
            if (memberType == null || !memberType.IsEnum) return;
            SetTextureSettingsValue(settings, memberName, Enum.Parse(memberType, value, true));
        }

        private static void SetTextureSettingsValue(TextureImporterSettings settings, string memberName, object value)
        {
            var property = typeof(TextureImporterSettings).GetProperty(memberName);
            if (property != null && property.CanWrite)
            {
                property.SetValue(settings, ConvertTextureSettingsValue(value, property.PropertyType), null);
                return;
            }

            var field = typeof(TextureImporterSettings).GetField(memberName);
            if (field != null)
            {
                field.SetValue(settings, ConvertTextureSettingsValue(value, field.FieldType));
            }
        }

        private static string GetTextureSettingsString(TextureImporterSettings settings, string memberName, string defaultValue)
        {
            var value = GetTextureSettingsValue(settings, memberName);
            return value != null ? value.ToString() : defaultValue;
        }

        private static int GetTextureSettingsInt(TextureImporterSettings settings, string memberName, int defaultValue)
        {
            var value = GetTextureSettingsValue(settings, memberName);
            return value != null ? Convert.ToInt32(value) : defaultValue;
        }

        private static object GetTextureSettingsValue(TextureImporterSettings settings, string memberName)
        {
            var property = typeof(TextureImporterSettings).GetProperty(memberName);
            if (property != null && property.CanRead) return property.GetValue(settings, null);
            var field = typeof(TextureImporterSettings).GetField(memberName);
            return field != null ? field.GetValue(settings) : null;
        }

        private static Type GetTextureSettingsMemberType(string memberName)
        {
            var property = typeof(TextureImporterSettings).GetProperty(memberName);
            if (property != null) return property.PropertyType;
            var field = typeof(TextureImporterSettings).GetField(memberName);
            return field != null ? field.FieldType : null;
        }

        private static object ConvertTextureSettingsValue(object value, Type targetType)
        {
            if (value == null || targetType.IsInstanceOfType(value)) return value;
            if (targetType.IsEnum) return Enum.Parse(targetType, value.ToString(), true);
            return Convert.ChangeType(value, targetType);
        }

        private static JObject SerializePlatformSettings(TextureImporterPlatformSettings settings)
        {
            return new JObject
            {
                ["platform"] = settings.name,
                ["overridden"] = settings.overridden,
                ["max_size"] = settings.maxTextureSize,
                ["format"] = settings.format.ToString(),
                ["compression_quality"] = settings.compressionQuality
            };
        }

        private static TextureImporterCompression ParseTextureCompression(string value)
        {
            switch ((value ?? string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty).ToLowerInvariant())
            {
                case "none":
                case "uncompressed": return TextureImporterCompression.Uncompressed;
                case "low":
                case "lowquality":
                case "compressedlq": return TextureImporterCompression.CompressedLQ;
                case "normal":
                case "compressed": return TextureImporterCompression.Compressed;
                case "high":
                case "highquality":
                case "compressedhq": return TextureImporterCompression.CompressedHQ;
                default: return ParseEnum<TextureImporterCompression>(value);
            }
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

        private static Vector2 ParseVector2(JToken token, Vector2 defaultValue)
        {
            if (token is JObject obj) return new Vector2(obj["x"]?.Value<float>() ?? defaultValue.x, obj["y"]?.Value<float>() ?? defaultValue.y);
            if (token is JArray arr && arr.Count >= 2) return new Vector2(arr[0].Value<float>(), arr[1].Value<float>());
            return defaultValue;
        }
    }
}
