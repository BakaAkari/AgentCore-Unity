using System;
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
    /// Manage generic AssetImporter settings, labels, bundles, dependencies, and reimport operations.
    /// </summary>
    [AgentTool("manage_asset_import",
        Description = "Manage generic AssetImporter settings, labels, bundles, dependencies, and reimport operations",
        Category = "Utility",
        RequiresMainThread = true)]
    public class ManageAssetImportTool : IAgentTool
    {
        private const string ValidActions = "get_importer, reimport, reimport_batch, set_labels, get_labels, set_bundle, get_dependencies, get_import_log, find_by_importer";

        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": { ""type"": ""string"", ""enum"": [""get_importer"", ""reimport"", ""reimport_batch"", ""set_labels"", ""get_labels"", ""set_bundle"", ""get_dependencies"", ""get_import_log"", ""find_by_importer""] },
                ""asset_path"": { ""type"": ""string"" },
                ""force"": { ""type"": ""boolean"" },
                ""paths"": { ""type"": ""array"" },
                ""items"": { ""type"": ""array"" },
                ""labels"": { ""description"": ""Comma-separated string or array of labels"" },
                ""assetBundleName"": { ""type"": ""string"" },
                ""assetBundleVariant"": { ""type"": ""string"" },
                ""recursive"": { ""type"": ""boolean"" },
                ""importer_type"": { ""type"": ""string"" },
                ""folder"": { ""type"": ""string"" },
                ""max_results"": { ""type"": ""integer"" }
            },
            ""required"": [""action""]
        }");

        /// <summary>
        /// Tool metadata for automatic registration.
        /// </summary>
        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_asset_import",
            description: "Manage generic AssetImporter settings, labels, bundles, dependencies, and reimport operations",
            category: "Utility",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        /// <summary>
        /// Executes a generic AssetImporter management action.
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
                    case "get_importer": response = HandleGetImporter(parameters); break;
                    case "reimport": response = HandleReimport(parameters); break;
                    case "reimport_batch": response = HandleReimportBatch(parameters); break;
                    case "set_labels": response = HandleSetLabels(parameters); break;
                    case "get_labels": response = HandleGetLabels(parameters); break;
                    case "set_bundle": response = HandleSetBundle(parameters); break;
                    case "get_dependencies": response = HandleGetDependencies(parameters); break;
                    case "get_import_log": response = HandleGetImportLog(parameters); break;
                    case "find_by_importer": response = HandleFindByImporter(parameters); break;
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

        private static ToolResponse HandleGetImporter(JObject parameters)
        {
            var importer = GetImporter(ToolHelpers.GetRequiredString(parameters, "asset_path"), out var path, out var error);
            return importer == null ? ToolResponse.Fail(error) : ToolResponse.OkWithData(SerializeImporter(importer, path), $"Importer info for '{path}'.");
        }

        private static ToolResponse HandleReimport(JObject parameters)
        {
            var path = NormalizeExistingPath(ToolHelpers.GetRequiredString(parameters, "asset_path"));
            var force = ToolHelpers.GetOptionalBool(parameters, "force", false);
            AssetDatabase.ImportAsset(path, force ? ImportAssetOptions.ForceUpdate : ImportAssetOptions.Default);
            return ToolResponse.OkWithData(new JObject { ["asset_path"] = path, ["force"] = force }, $"Reimported '{path}'.");
        }

        private static ToolResponse HandleReimportBatch(JObject parameters)
        {
            var paths = ToolHelpers.GetOptionalArray(parameters, "paths") ?? ToolHelpers.GetOptionalArray(parameters, "items");
            if (paths == null) return ToolResponse.Fail("Required parameter 'paths' or 'items' is missing or not an array.");
            var force = ToolHelpers.GetOptionalBool(parameters, "force", false);
            var succeeded = new JArray();
            var failed = new JArray();
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var token in paths)
                {
                    var rawPath = token is JObject obj ? obj["asset_path"]?.ToString() ?? obj["path"]?.ToString() : token.ToString();
                    try
                    {
                        var path = NormalizeExistingPath(rawPath);
                        AssetDatabase.ImportAsset(path, force ? ImportAssetOptions.ForceUpdate : ImportAssetOptions.Default);
                        succeeded.Add(new JObject { ["asset_path"] = path });
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new JObject { ["asset_path"] = rawPath ?? string.Empty, ["error"] = ex.Message });
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
            }
            return ToolResponse.OkWithData(new JObject { ["succeeded"] = succeeded, ["failed"] = failed }, $"Reimported {succeeded.Count} assets; {failed.Count} failed.");
        }

        private static ToolResponse HandleSetLabels(JObject parameters)
        {
            var asset = GetMainAsset(ToolHelpers.GetRequiredString(parameters, "asset_path"), out var path);
            var labels = ParseLabels(parameters["labels"]);
            AssetDatabase.SetLabels(asset, labels);
            return ToolResponse.OkWithData(new JObject { ["asset_path"] = path, ["labels"] = new JArray(labels) }, $"Set {labels.Length} labels on '{path}'.");
        }

        private static ToolResponse HandleGetLabels(JObject parameters)
        {
            var asset = GetMainAsset(ToolHelpers.GetRequiredString(parameters, "asset_path"), out var path);
            var labels = AssetDatabase.GetLabels(asset);
            return ToolResponse.OkWithData(new JObject { ["asset_path"] = path, ["labels"] = new JArray(labels) }, $"Labels for '{path}'.");
        }

        private static ToolResponse HandleSetBundle(JObject parameters)
        {
            var importer = GetImporter(ToolHelpers.GetRequiredString(parameters, "asset_path"), out var path, out var error);
            if (importer == null) return ToolResponse.Fail(error);
            importer.assetBundleName = ToolHelpers.GetOptionalString(parameters, "assetBundleName", importer.assetBundleName) ?? string.Empty;
            importer.assetBundleVariant = ToolHelpers.GetOptionalString(parameters, "assetBundleVariant", importer.assetBundleVariant) ?? string.Empty;
            importer.SaveAndReimport();
            return ToolResponse.OkWithData(SerializeImporter(importer, path), $"Updated AssetBundle settings for '{path}'.");
        }

        private static ToolResponse HandleGetDependencies(JObject parameters)
        {
            var path = NormalizeExistingPath(ToolHelpers.GetRequiredString(parameters, "asset_path"));
            var recursive = ToolHelpers.GetOptionalBool(parameters, "recursive", true);
            var dependencies = AssetDatabase.GetDependencies(path, recursive).Where(p => p != path).Select(p => new JObject
            {
                ["path"] = p,
                ["type"] = AssetDatabase.GetMainAssetTypeAtPath(p)?.Name ?? "Unknown",
                ["importer_type"] = AssetImporter.GetAtPath(p)?.GetType().Name ?? "None"
            });
            var array = new JArray(dependencies);
            return ToolResponse.OkWithData(new JObject { ["asset_path"] = path, ["recursive"] = recursive, ["dependencies"] = array, ["count"] = array.Count }, $"Found {array.Count} dependencies for '{path}'.");
        }

        private static ToolResponse HandleGetImportLog(JObject parameters)
        {
            var importer = GetImporter(ToolHelpers.GetRequiredString(parameters, "asset_path"), out var path, out var error);
            if (importer == null) return ToolResponse.Fail(error);
            var fullPath = Path.GetFullPath(path);
            var fileInfo = File.Exists(fullPath) ? new FileInfo(fullPath) : null;
            return ToolResponse.OkWithData(new JObject
            {
                ["asset_path"] = path,
                ["guid"] = AssetDatabase.AssetPathToGUID(path),
                ["importer_type"] = importer.GetType().Name,
                ["asset_time_stamp"] = importer.assetTimeStamp,
                ["user_data"] = importer.userData,
                ["file_last_write_time_utc"] = fileInfo != null ? fileInfo.LastWriteTimeUtc.ToString("o") : string.Empty,
                ["note"] = "Unity does not expose a per-asset import log through AssetImporter; returning importer diagnostics instead."
            }, $"Importer diagnostics for '{path}'.");
        }

        private static ToolResponse HandleFindByImporter(JObject parameters)
        {
            var importerType = ToolHelpers.GetRequiredString(parameters, "importer_type");
            var folder = ToolHelpers.NormalizeAssetPath(ToolHelpers.GetOptionalString(parameters, "folder", "Assets"));
            var maxResults = ToolHelpers.GetOptionalInt(parameters, "max_results", 50);
            var results = new JArray();
            var scanned = 0;
            foreach (var guid in AssetDatabase.FindAssets(string.Empty, new[] { folder }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var importer = AssetImporter.GetAtPath(path);
                if (importer == null) continue;
                scanned++;
                if (!ImporterTypeMatches(importer, importerType)) continue;
                results.Add(new JObject { ["path"] = path, ["guid"] = guid, ["importer_type"] = importer.GetType().Name, ["asset_type"] = AssetDatabase.GetMainAssetTypeAtPath(path)?.Name ?? "Unknown" });
                if (results.Count >= maxResults) break;
            }
            return ToolResponse.OkWithData(new JObject { ["results"] = results, ["scanned"] = scanned, ["returned"] = results.Count }, $"Found {results.Count} assets imported by {importerType}.");
        }

        private static AssetImporter GetImporter(string assetPath, out string normalizedPath, out string error)
        {
            normalizedPath = string.IsNullOrWhiteSpace(assetPath) ? assetPath : ToolHelpers.NormalizeAssetPath(assetPath);
            error = null;
            if (string.IsNullOrWhiteSpace(normalizedPath)) { error = "Required parameter 'asset_path' is missing or empty."; return null; }
            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(normalizedPath))) { error = $"Asset not found: {normalizedPath}"; return null; }
            var importer = AssetImporter.GetAtPath(normalizedPath);
            if (importer == null) error = $"No AssetImporter found for: {normalizedPath}";
            return importer;
        }

        private static UnityEngine.Object GetMainAsset(string assetPath, out string normalizedPath)
        {
            normalizedPath = NormalizeExistingPath(assetPath);
            var asset = AssetDatabase.LoadMainAssetAtPath(normalizedPath);
            if (asset == null) throw new ArgumentException($"Failed to load asset: {normalizedPath}");
            return asset;
        }

        private static string NormalizeExistingPath(string assetPath)
        {
            var path = ToolHelpers.NormalizeAssetPath(assetPath);
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Required parameter 'asset_path' is missing or empty.");
            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path))) throw new ArgumentException($"Asset not found: {path}");
            return path;
        }

        private static JObject SerializeImporter(AssetImporter importer, string path)
        {
            return new JObject
            {
                ["asset_path"] = path,
                ["guid"] = AssetDatabase.AssetPathToGUID(path),
                ["importer_type"] = importer.GetType().Name,
                ["assetBundleName"] = importer.assetBundleName,
                ["assetBundleVariant"] = importer.assetBundleVariant,
                ["userData"] = importer.userData,
                ["assetTimeStamp"] = importer.assetTimeStamp,
                ["labels"] = new JArray(AssetDatabase.GetLabels(AssetDatabase.LoadMainAssetAtPath(path)))
            };
        }

        private static string[] ParseLabels(JToken token)
        {
            if (token == null) return Array.Empty<string>();
            if (token is JArray arr) return arr.Select(t => t.ToString().Trim()).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToArray();
            return token.ToString().Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToArray();
        }

        private static bool ImporterTypeMatches(AssetImporter importer, string importerType)
        {
            var requested = (importerType ?? string.Empty).Replace(" ", string.Empty).Replace("_", string.Empty);
            var actual = importer.GetType().Name.Replace(" ", string.Empty).Replace("_", string.Empty);
            return string.Equals(actual, requested, StringComparison.OrdinalIgnoreCase) || actual.EndsWith(requested, StringComparison.OrdinalIgnoreCase);
        }
    }
}
