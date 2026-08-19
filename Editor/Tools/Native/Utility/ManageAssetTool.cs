using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Tools.Safety;
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
        Description = "Unity AssetDatabase operations — search, create, delete, move, copy, and inspect assets. " +
                      "Actions: search (find assets by name/type/label using AssetDatabase.FindAssets with filter syntax: 't:Type', 'l:Label', name), " +
                      "get_info (type, path, GUID, labels, bundle, main asset type, sub-assets), " +
                      "create (create asset from type — Material, RenderTexture, AnimatorController, etc), " +
                      "delete (move asset to OS recycle bin — recoverable via OS trash, NOT via Ctrl+Z), move (change asset path — NOT Ctrl+Z reversible, response includes reverseHint), copy (duplicate asset — Ctrl+Z reversible), " +
                      "rename (change asset filename), " +
                      "get_dependencies (what THIS asset depends on — outgoing edges, direct + transitive), " +
                      "find_references (what depends on THIS asset — incoming edges, reverse scan; use 'filter' to restrict scan set, 'recursive' for indirect refs). " +
                      "USE FOR: finding assets by type/name, creating new Material/RenderTexture/Shader assets, " +
                      "organizing assets (move/copy/rename), getting asset metadata (GUID, type, sub-assets), " +
                      "impact analysis before delete/rename (find_references answers 'who breaks if I remove this'), " +
                      "orphan detection (find_references returning 0 hits = safe to delete). " +
                      "NOT FOR: file content read/write (use manage_file), import settings (use manage_asset_import/manage_texture_import/manage_model_import), " +
                      "C# script creation (use manage_script), prefab operations (use manage_prefab), " +
                      "references inside a specific scene (use scene_analysis find_references_in_scene). " +
                      "Returns: asset paths, GUIDs, type info. Search filter syntax: 't:Texture2D' (by type), 'l:MyLabel' (by label), 'Player' (by name).",
        Category = "Asset",
        RequiresMainThread = true,
        RiskLevel = ToolRiskLevel.Medium,
        Capabilities = ToolCapability.ModifyAssets | ToolCapability.DeleteProjectFiles,
        ReadOnlyActions = new[] { "get_dependencies", "get_info", "search", "find_references" },
        // v1.12+ ModifyRuntimeState: 所有 AssetDatabase 落盘/删除/移动操作在 Play Mode 中硬禁止。
        // 这些操作直接写磁盘,与运行时内存修改语义冲突。Agent 运行时需临时对象应直接 Instantiate。
        PlaymodeHardBlockedActions = new[] { "create_folder", "delete", "move", "copy", "import" })]
    public class ManageAssetTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""search"", ""get_info"", ""create_folder"", ""delete"", ""move"", ""copy"", ""import"", ""get_dependencies"", ""find_references""],
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
                },
                ""filter"": {
                    ""type"": ""string"",
                    ""description"": ""Type filter for find_references (default: scans all assets). Uses AssetDatabase.FindAssets syntax, e.g. 't:Prefab', 't:Scene', 't:Material', 't:Prefab t:Scene'. Restricting the filter drastically speeds up scans in large projects.""
                },
                ""recursive"": {
                    ""type"": ""boolean"",
                    ""description"": ""For find_references: when true, includes indirect references (A refs B refs target = A appears). Default false (direct only). Warning: recursive scans large projects can take seconds.""
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
                    case "find_references":
                        response = HandleFindReferences(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: search, get_info, create_folder, delete, move, copy, import, get_dependencies, find_references");
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

                // Sub-assets. Skip for scene assets (.unity): LoadAllAssetsAtPath on a scene returns the
                // scene's objects, and Unity refuses to read scene objects on a threaded path - it spams
                // "Do not use ReadObjectThreaded on scene objects!". Scene files have no meaningful sub-assets
                // to enumerate here, so bail out before touching them.
                if (asset is SceneAsset)
                {
                    info["subAssets"] = null;
                }
                else
                {
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

                // Create intermediate folders if needed. AssetDatabase.CreateFolder is not
                // tracked by Unity's Undo system natively, so we register each newly created
                // folder as an Undo entry so Ctrl+Z removes them.
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

                        // Register the folder asset object with Undo. If Unity refuses to
                        // load the asset immediately (edge case) we still succeed silently.
                        var folderAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(nextPath);
                        if (folderAsset != null)
                            Undo.RegisterCreatedObjectUndo(folderAsset, $"Create Folder {nextPath}");
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

                // Use MoveAssetToTrash instead of DeleteAsset. MoveAssetToTrash routes the
                // file to the OS recycle bin so the user can manually restore it, unlike
                // DeleteAsset which permanently removes the file. Unity's Undo system does
                // not track AssetDatabase deletions, so the recycle bin is the recoverability
                // guarantee we can offer.
                bool success = AssetDatabase.MoveAssetToTrash(path);
                if (!success)
                    return ToolResponse.Fail($"Failed to delete asset (MoveAssetToTrash returned false): {path}");

                AssetDatabase.Refresh();

                return ToolResponse.Ok($"Moved asset to OS trash (recoverable from recycle bin): {path}");
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

                // AssetDatabase.MoveAsset is not covered by Unity's Undo system. We stash
                // the original path in the response so an agent can reverse the operation
                // by calling manage_asset move with sourcePath/destinationPath swapped.
                var error = AssetDatabase.MoveAsset(path, destinationPath);
                if (!string.IsNullOrEmpty(error))
                    return ToolResponse.Fail($"Move failed: {error}");

                AssetDatabase.Refresh();

                return ToolResponse.OkWithData(new JObject
                {
                    ["sourcePath"] = path,
                    ["destinationPath"] = destinationPath,
                    ["reverseHint"] = $"Move '{destinationPath}' back to '{path}' to undo (Ctrl+Z will NOT work on AssetDatabase.MoveAsset)"
                }, $"Moved asset from '{path}' to '{destinationPath}'. (Not Ctrl+Z reversible — see reverseHint.)");
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

                // Register the newly copied asset with Undo so Ctrl+Z removes the copy.
                var copiedAsset = AssetDatabase.LoadMainAssetAtPath(destinationPath);
                if (copiedAsset != null)
                    Undo.RegisterCreatedObjectUndo(copiedAsset, $"Copy Asset to {destinationPath}");

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

        /// <summary>
        /// Reverse dependency lookup: find every asset that depends on the given target asset.
        /// Implemented as a full-project scan using AssetDatabase.GetDependencies against every
        /// scan-candidate asset. Use the 'filter' parameter to shrink the scan set (e.g. only
        /// scenes or prefabs) — this dramatically speeds up the operation on large projects.
        /// </summary>
        private ToolResponse HandleFindReferences(JObject parameters)
        {
            try
            {
                var path = ToolHelpers.GetRequiredString(parameters, "path");
                path = ToolHelpers.NormalizeAssetPath(path);

                var targetGuid = AssetDatabase.AssetPathToGUID(path);
                if (string.IsNullOrEmpty(targetGuid))
                    return ToolResponse.Fail($"Asset not found at path: {path}");

                var filter = ToolHelpers.GetOptionalString(parameters, "filter") ?? "";
                bool recursive = ToolHelpers.GetOptionalBool(parameters, "recursive", false);
                int maxResults = ToolHelpers.GetOptionalInt(parameters, "maxResults", 500);

                // Build the scan candidate set. Empty filter → every asset under Assets/.
                string[] guids = string.IsNullOrEmpty(filter)
                    ? AssetDatabase.FindAssets("", new[] { "Assets" })
                    : AssetDatabase.FindAssets(filter, new[] { "Assets" });

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var directRefs = new JArray();
                var indirectRefs = new JArray();
                int scanned = 0;
                bool truncated = false;

                foreach (var g in guids)
                {
                    scanned++;
                    var candidatePath = AssetDatabase.GUIDToAssetPath(g);
                    if (string.IsNullOrEmpty(candidatePath)) continue;
                    if (candidatePath == path) continue;                 // self
                    if (AssetDatabase.IsValidFolder(candidatePath)) continue; // folders have no deps

                    // Direct deps (recursive:false) — one hop only
                    var direct = AssetDatabase.GetDependencies(candidatePath, false);
                    bool isDirect = false;
                    for (int i = 0; i < direct.Length; i++)
                    {
                        if (direct[i] == path) { isDirect = true; break; }
                    }

                    if (isDirect)
                    {
                        if (directRefs.Count < maxResults)
                        {
                            directRefs.Add(new JObject
                            {
                                ["path"] = candidatePath,
                                ["type"] = AssetDatabase.GetMainAssetTypeAtPath(candidatePath)?.Name ?? "Unknown"
                            });
                        }
                        else
                        {
                            truncated = true;
                        }
                        continue; // direct implies transitive — no need to check recursive set
                    }

                    if (recursive)
                    {
                        var all = AssetDatabase.GetDependencies(candidatePath, true);
                        bool isIndirect = false;
                        for (int i = 0; i < all.Length; i++)
                        {
                            if (all[i] == path) { isIndirect = true; break; }
                        }
                        if (isIndirect)
                        {
                            if (indirectRefs.Count < maxResults)
                            {
                                indirectRefs.Add(new JObject
                                {
                                    ["path"] = candidatePath,
                                    ["type"] = AssetDatabase.GetMainAssetTypeAtPath(candidatePath)?.Name ?? "Unknown"
                                });
                            }
                            else
                            {
                                truncated = true;
                            }
                        }
                    }
                }
                sw.Stop();

                var msg = recursive
                    ? $"References to '{path}': {directRefs.Count} direct, {indirectRefs.Count} indirect (scanned {scanned} assets in {sw.ElapsedMilliseconds} ms)."
                    : $"References to '{path}': {directRefs.Count} direct (scanned {scanned} assets in {sw.ElapsedMilliseconds} ms).";

                var data = new JObject
                {
                    ["path"] = path,
                    ["guid"] = targetGuid,
                    ["filter"] = filter,
                    ["recursive"] = recursive,
                    ["scanned_asset_count"] = scanned,
                    ["scan_elapsed_ms"] = sw.ElapsedMilliseconds,
                    ["direct_reference_count"] = directRefs.Count,
                    ["direct_references"] = directRefs,
                    ["truncated"] = truncated,
                    ["max_results"] = maxResults
                };
                if (recursive)
                {
                    data["indirect_reference_count"] = indirectRefs.Count;
                    data["indirect_references"] = indirectRefs;
                }
                if (directRefs.Count == 0 && (!recursive || indirectRefs.Count == 0))
                {
                    data["hint"] = "No references found. This asset appears to be an orphan — safe to delete unless referenced from code (strings, Resources.Load) or scenes not currently in the scan set. Widen 'filter' or set recursive=true to broaden search.";
                }
                else if (truncated)
                {
                    data["hint"] = $"Result truncated at maxResults={maxResults}. Increase maxResults or narrow 'filter' to see all references.";
                }

                return ToolResponse.OkWithData(data, msg);
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Find references failed: {ex.Message}");
            }
        }

        #endregion
    }
}
