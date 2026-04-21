using System;
using System.Collections.Generic;
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
    /// Manage Unity assets - search, create, delete, move, copy, and get info.
    /// Directly calls Unity AssetDatabase API as part of the native tool system.
    /// </summary>
    [AgentTool("manage_asset",
        Description = "Manage Unity assets - search, create, delete, move, copy, and get info",
        Category = "Asset",
        RequiresMainThread = true)]
    public class ManageAssetTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""search"", ""get_info"", ""create_folder"", ""delete"", ""move"", ""copy"", ""import"", ""get_dependencies""],
                    ""description"": ""Action to perform""
                },
                ""path"": {
                    ""type"": ""string"",
                    ""description"": ""Asset path (e.g., 'Assets/Scripts/MyScript.cs')""
                },
                ""searchPattern"": {
                    ""type"": ""string"",
                    ""description"": ""Search filter for search action (e.g., 't:Material', 'l:MyLabel', name)""
                },
                ""searchFolder"": {
                    ""type"": ""string"",
                    ""description"": ""Folder to search in (default: 'Assets')""
                },
                ""destinationPath"": {
                    ""type"": ""string"",
                    ""description"": ""Destination path for move/copy""
                },
                ""folderName"": {
                    ""type"": ""string"",
                    ""description"": ""Folder name for create_folder""
                },
                ""parentFolder"": {
                    ""type"": ""string"",
                    ""description"": ""Parent folder for create_folder (default: 'Assets')""
                },
                ""maxResults"": {
                    ""type"": ""integer"",
                    ""description"": ""Max search results (default: 50)""
                }
            },
            ""required"": [""action""]
        }");

        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_asset",
            description: "Manage Unity assets - search, create, delete, move, copy, and get info",
            category: "Asset",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        public Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ToolResponse response;

            try
            {
                var action = ToolHelpers.GetRequiredString(parameters, "action").ToLowerInvariant();

                switch (action)
                {
                    case "search":
                        response = HandleSearch(parameters);
                        break;
                    case "get_info":
                        response = HandleGetInfo(parameters);
                        break;
                    case "create_folder":
                        response = HandleCreateFolder(parameters);
                        break;
                    case "delete":
                        response = HandleDelete(parameters);
                        break;
                    case "move":
                        response = HandleMove(parameters);
                        break;
                    case "copy":
                        response = HandleCopy(parameters);
                        break;
                    case "import":
                        response = HandleImport(parameters);
                        break;
                    case "get_dependencies":
                        response = HandleGetDependencies(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: search, get_info, create_folder, delete, move, copy, import, get_dependencies");
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

        private ToolResponse HandleSearch(JObject parameters)
        {
            try
            {
                var searchPattern = ToolHelpers.GetOptionalString(parameters, "searchPattern", "");
                var searchFolder = ToolHelpers.GetOptionalString(parameters, "searchFolder", "Assets");
                var maxResults = ToolHelpers.GetOptionalInt(parameters, "maxResults", 50);

                searchFolder = ToolHelpers.NormalizeAssetPath(searchFolder);

                string[] guids = AssetDatabase.FindAssets(searchPattern, new[] { searchFolder });

                var results = new JArray();
                int count = 0;
                foreach (var guid in guids)
                {
                    if (count >= maxResults) break;

                    var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    var assetType = AssetDatabase.GetMainAssetTypeAtPath(assetPath);

                    results.Add(new JObject
                    {
                        ["path"] = assetPath,
                        ["guid"] = guid,
                        ["type"] = assetType?.Name ?? "Unknown"
                    });
                    count++;
                }

                return ToolResponse.OkWithData(new JObject
                {
                    ["results"] = results,
                    ["totalFound"] = guids.Length,
                    ["returned"] = count
                }, $"Found {guids.Length} assets, returned {count}.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Search failed: {ex.Message}");
            }
        }

        private ToolResponse HandleGetInfo(JObject parameters)
        {
            try
            {
                var path = ToolHelpers.GetRequiredString(parameters, "path");
                path = ToolHelpers.NormalizeAssetPath(path);

                var guid = AssetDatabase.AssetPathToGUID(path);
                if (string.IsNullOrEmpty(guid))
                    return ToolResponse.Fail($"Asset not found at path: {path}");

                var asset = AssetDatabase.LoadMainAssetAtPath(path);
                if (asset == null)
                    return ToolResponse.Fail($"Failed to load asset at path: {path}");

                var info = new JObject
                {
                    ["path"] = path,
                    ["guid"] = guid,
                    ["name"] = asset.name,
                    ["type"] = asset.GetType().Name,
                    ["fullType"] = asset.GetType().FullName,
                    ["instanceId"] = asset.GetInstanceID()
                };

                // File info
                var fullPath = Path.GetFullPath(path);
                if (File.Exists(fullPath))
                {
                    var fileInfo = new FileInfo(fullPath);
                    info["fileSize"] = fileInfo.Length;
                    info["lastModified"] = fileInfo.LastWriteTimeUtc.ToString("o");
                }

                // Check if it's a directory
                info["isFolder"] = AssetDatabase.IsValidFolder(path);

                // Labels
                var labels = AssetDatabase.GetLabels(asset);
                if (labels.Length > 0)
                {
                    info["labels"] = new JArray(labels);
                }

                // Sub-assets
                var subAssets = AssetDatabase.LoadAllAssetsAtPath(path);
                if (subAssets.Length > 1)
                {
                    var subArray = new JArray();
                    foreach (var sub in subAssets)
                    {
                        if (sub == null || sub == asset) continue;
                        subArray.Add(new JObject
                        {
                            ["name"] = sub.name,
                            ["type"] = sub.GetType().Name
                        });
                    }
                    if (subArray.Count > 0)
                        info["subAssets"] = subArray;
                }

                return ToolResponse.OkWithData(info, $"Asset info for '{path}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Get info failed: {ex.Message}");
            }
        }

        private ToolResponse HandleCreateFolder(JObject parameters)
        {
            try
            {
                var folderName = ToolHelpers.GetRequiredString(parameters, "folderName");
                var parentFolder = ToolHelpers.GetOptionalString(parameters, "parentFolder", "Assets");
                parentFolder = ToolHelpers.NormalizeAssetPath(parentFolder);

                var fullPath = $"{parentFolder}/{folderName}";

                if (AssetDatabase.IsValidFolder(fullPath))
                    return ToolResponse.OkWithData(new JObject { ["path"] = fullPath },
                        $"Folder already exists: {fullPath}");

                // Create intermediate folders if needed
                var parts = folderName.Split('/');
                var currentParent = parentFolder;
                foreach (var part in parts)
                {
                    var nextPath = $"{currentParent}/{part}";
                    if (!AssetDatabase.IsValidFolder(nextPath))
                    {
                        var guid = AssetDatabase.CreateFolder(currentParent, part);
                        if (string.IsNullOrEmpty(guid))
                            return ToolResponse.Fail($"Failed to create folder: {nextPath}");
                    }
                    currentParent = nextPath;
                }

                AssetDatabase.Refresh();

                return ToolResponse.OkWithData(new JObject { ["path"] = fullPath },
                    $"Created folder: {fullPath}");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Create folder failed: {ex.Message}");
            }
        }

        private ToolResponse HandleDelete(JObject parameters)
        {
            try
            {
                var path = ToolHelpers.GetRequiredString(parameters, "path");
                path = ToolHelpers.NormalizeAssetPath(path);

                if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)))
                    return ToolResponse.Fail($"Asset not found at path: {path}");

                bool success = AssetDatabase.DeleteAsset(path);
                if (!success)
                    return ToolResponse.Fail($"Failed to delete asset: {path}");

                AssetDatabase.Refresh();

                return ToolResponse.Ok($"Deleted asset: {path}");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Delete failed: {ex.Message}");
            }
        }

        private ToolResponse HandleMove(JObject parameters)
        {
            try
            {
                var path = ToolHelpers.GetRequiredString(parameters, "path");
                var destinationPath = ToolHelpers.GetRequiredString(parameters, "destinationPath");
                path = ToolHelpers.NormalizeAssetPath(path);
                destinationPath = ToolHelpers.NormalizeAssetPath(destinationPath);

                if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)))
                    return ToolResponse.Fail($"Source asset not found: {path}");

                var error = AssetDatabase.MoveAsset(path, destinationPath);
                if (!string.IsNullOrEmpty(error))
                    return ToolResponse.Fail($"Move failed: {error}");

                AssetDatabase.Refresh();

                return ToolResponse.OkWithData(new JObject
                {
                    ["sourcePath"] = path,
                    ["destinationPath"] = destinationPath
                }, $"Moved asset from '{path}' to '{destinationPath}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Move failed: {ex.Message}");
            }
        }

        private ToolResponse HandleCopy(JObject parameters)
        {
            try
            {
                var path = ToolHelpers.GetRequiredString(parameters, "path");
                var destinationPath = ToolHelpers.GetRequiredString(parameters, "destinationPath");
                path = ToolHelpers.NormalizeAssetPath(path);
                destinationPath = ToolHelpers.NormalizeAssetPath(destinationPath);

                if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)))
                    return ToolResponse.Fail($"Source asset not found: {path}");

                bool success = AssetDatabase.CopyAsset(path, destinationPath);
                if (!success)
                    return ToolResponse.Fail($"Failed to copy asset from '{path}' to '{destinationPath}'.");

                AssetDatabase.Refresh();

                return ToolResponse.OkWithData(new JObject
                {
                    ["sourcePath"] = path,
                    ["destinationPath"] = destinationPath
                }, $"Copied asset from '{path}' to '{destinationPath}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Copy failed: {ex.Message}");
            }
        }

        private ToolResponse HandleImport(JObject parameters)
        {
            try
            {
                var path = ToolHelpers.GetOptionalString(parameters, "path");

                if (!string.IsNullOrEmpty(path))
                {
                    path = ToolHelpers.NormalizeAssetPath(path);
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                    return ToolResponse.Ok($"Imported asset: {path}");
                }
                else
                {
                    AssetDatabase.Refresh();
                    return ToolResponse.Ok("Refreshed entire AssetDatabase.");
                }
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Import failed: {ex.Message}");
            }
        }

        private ToolResponse HandleGetDependencies(JObject parameters)
        {
            try
            {
                var path = ToolHelpers.GetRequiredString(parameters, "path");
                path = ToolHelpers.NormalizeAssetPath(path);

                if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)))
                    return ToolResponse.Fail($"Asset not found at path: {path}");

                // Direct dependencies
                var directDeps = AssetDatabase.GetDependencies(path, false);
                var allDeps = AssetDatabase.GetDependencies(path, true);

                var directArray = new JArray();
                foreach (var dep in directDeps)
                {
                    if (dep == path) continue;
                    directArray.Add(new JObject
                    {
                        ["path"] = dep,
                        ["type"] = AssetDatabase.GetMainAssetTypeAtPath(dep)?.Name ?? "Unknown"
                    });
                }

                var allArray = new JArray();
                foreach (var dep in allDeps)
                {
                    if (dep == path) continue;
                    allArray.Add(new JObject
                    {
                        ["path"] = dep,
                        ["type"] = AssetDatabase.GetMainAssetTypeAtPath(dep)?.Name ?? "Unknown"
                    });
                }

                return ToolResponse.OkWithData(new JObject
                {
                    ["path"] = path,
                    ["directDependencies"] = directArray,
                    ["allDependencies"] = allArray,
                    ["directCount"] = directArray.Count,
                    ["totalCount"] = allArray.Count
                }, $"Dependencies for '{path}': {directArray.Count} direct, {allArray.Count} total.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Get dependencies failed: {ex.Message}");
            }
        }

        #endregion
    }
}
