using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Tools.Native.Extended
{
    /// <summary>
    /// Project cleaner tool — find unused assets, duplicates, missing references/scripts,
    /// empty folders, large files, asset usage, and dependency trees.
    /// </summary>
    [AgentTool("cleaner",
        Description = "Find unused assets, duplicate files, missing references/scripts, empty folders, large assets, asset usage info, and dependency trees. Helps clean up and maintain Unity projects.",
        Category = "Extended",
        RequiresMainThread = true)]
    public class CleanerTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""find_unused_assets"", ""find_duplicates"", ""find_missing_references"", ""find_missing_scripts"", ""fix_missing_scripts"", ""find_empty_folders"", ""delete_empty_folders"", ""find_large_assets"", ""get_asset_usage"", ""get_dependency_tree""],
                    ""description"": ""Action to perform""
                },
                ""folder"": { ""type"": ""string"", ""description"": ""Folder to search in (default: Assets)"" },
                ""extensions"": { ""type"": ""string"", ""description"": ""Comma-separated file extensions to filter (e.g. '.mat,.prefab,.png')"" },
                ""minSizeKB"": { ""type"": ""integer"", ""description"": ""Minimum file size in KB for find_large_assets (default: 1024)"" },
                ""assetPath"": { ""type"": ""string"", ""description"": ""Asset path for get_asset_usage and get_dependency_tree"" },
                ""depth"": { ""type"": ""integer"", ""description"": ""Dependency tree depth (default: 3). Use 0 for direct dependencies only."" },
                ""limit"": { ""type"": ""integer"", ""description"": ""Max results to return (default: 50)"" }
            },
            ""required"": [""action""]
        }");

        /// <summary>
        /// Tool metadata for auto-discovery registration.
        /// </summary>
        public ToolMetadata Metadata => new ToolMetadata(
            name: "cleaner",
            description: "Find unused assets, duplicate files, missing references/scripts, empty folders, large assets, asset usage info, and dependency trees. Helps clean up and maintain Unity projects.",
            category: "Extended",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        /// <summary>
        /// Execute the cleaner action specified in parameters.
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
                    case "find_unused_assets":
                        response = HandleFindUnusedAssets(parameters);
                        break;
                    case "find_duplicates":
                        response = HandleFindDuplicates(parameters);
                        break;
                    case "find_missing_references":
                        response = HandleFindMissingReferences(parameters);
                        break;
                    case "find_missing_scripts":
                        response = HandleFindMissingScripts(parameters);
                        break;
                    case "fix_missing_scripts":
                        response = HandleFixMissingScripts(parameters);
                        break;
                    case "find_empty_folders":
                        response = HandleFindEmptyFolders(parameters);
                        break;
                    case "delete_empty_folders":
                        response = HandleDeleteEmptyFolders(parameters);
                        break;
                    case "find_large_assets":
                        response = HandleFindLargeAssets(parameters);
                        break;
                    case "get_asset_usage":
                        response = HandleGetAssetUsage(parameters);
                        break;
                    case "get_dependency_tree":
                        response = HandleGetDependencyTree(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail($"Unknown action: {action}. Valid actions: find_unused_assets, find_duplicates, find_missing_references, find_missing_scripts, fix_missing_scripts, find_empty_folders, delete_empty_folders, find_large_assets, get_asset_usage, get_dependency_tree");
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
        /// Find assets not referenced by any scene or other asset in the project.
        /// </summary>
        private ToolResponse HandleFindUnusedAssets(JObject parameters)
        {
            string folder = ToolHelpers.GetOptionalString(parameters, "folder", "Assets");
            string extensionsStr = ToolHelpers.GetOptionalString(parameters, "extensions");
            int limit = ToolHelpers.GetOptionalInt(parameters, "limit", 50);

            // Parse extensions filter
            HashSet<string> extensionFilter = null;
            if (!string.IsNullOrEmpty(extensionsStr))
            {
                extensionFilter = new HashSet<string>(
                    extensionsStr.Split(',').Select(e => e.Trim().ToLowerInvariant()),
                    StringComparer.OrdinalIgnoreCase
                );
            }

            // Collect all scene paths in build settings
            var scenePaths = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray();

            // Collect all dependencies from scenes
            var referencedAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var scenePath in scenePaths)
            {
                if (string.IsNullOrEmpty(scenePath)) continue;
                var deps = AssetDatabase.GetDependencies(scenePath, true);
                foreach (var dep in deps)
                    referencedAssets.Add(dep);
            }

            // Also check Resources folders — assets there are always potentially used
            var resourceGuids = AssetDatabase.FindAssets("", new[] { folder });
            var potentiallyUnused = new List<object>();

            foreach (var guid in resourceGuids)
            {
                if (potentiallyUnused.Count >= limit) break;

                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/")) continue;
                if (AssetDatabase.IsValidFolder(path)) continue;

                // Skip assets in Resources folders (always potentially used)
                if (path.Contains("/Resources/")) continue;

                // Apply extension filter
                if (extensionFilter != null)
                {
                    var ext = Path.GetExtension(path).ToLowerInvariant();
                    if (!extensionFilter.Contains(ext)) continue;
                }

                // Skip scene files and scripts
                if (path.EndsWith(".unity") || path.EndsWith(".cs")) continue;

                if (!referencedAssets.Contains(path))
                {
                    var asset = AssetDatabase.LoadMainAssetAtPath(path);
                    var fullPath = Path.Combine(Directory.GetCurrentDirectory(), path);
                    long sizeBytes = File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0;

                    potentiallyUnused.Add(new
                    {
                        path,
                        name = asset != null ? asset.name : Path.GetFileNameWithoutExtension(path),
                        type = asset != null ? asset.GetType().Name : "Unknown",
                        sizeKB = (int)(sizeBytes / 1024)
                    });
                }
            }

            return ToolResponse.OkWithData(new
            {
                scenesChecked = scenePaths.Length,
                potentiallyUnusedCount = potentiallyUnused.Count,
                note = "Assets may still be used via Resources.Load, Addressables, or runtime loading. Manual review recommended.",
                assets = potentiallyUnused
            }, $"Found {potentiallyUnused.Count} potentially unused assets");
        }

        /// <summary>
        /// Find duplicate files by content hash (MD5).
        /// </summary>
        private ToolResponse HandleFindDuplicates(JObject parameters)
        {
            string folder = ToolHelpers.GetOptionalString(parameters, "folder", "Assets");
            string extensionsStr = ToolHelpers.GetOptionalString(parameters, "extensions");
            int limit = ToolHelpers.GetOptionalInt(parameters, "limit", 50);

            HashSet<string> extensionFilter = null;
            if (!string.IsNullOrEmpty(extensionsStr))
            {
                extensionFilter = new HashSet<string>(
                    extensionsStr.Split(',').Select(e => e.Trim().ToLowerInvariant()),
                    StringComparer.OrdinalIgnoreCase
                );
            }

            var fullFolderPath = Path.Combine(Directory.GetCurrentDirectory(), folder);
            if (!Directory.Exists(fullFolderPath))
                return ToolResponse.Fail($"Folder not found: {folder}");

            // Group files by size first (fast pre-filter)
            var sizeGroups = new Dictionary<long, List<string>>();
            var files = Directory.GetFiles(fullFolderPath, "*.*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".meta"));

            foreach (var file in files)
            {
                if (extensionFilter != null)
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (!extensionFilter.Contains(ext)) continue;
                }

                var fi = new FileInfo(file);
                if (!fi.Exists || fi.Length == 0) continue;

                if (!sizeGroups.ContainsKey(fi.Length))
                    sizeGroups[fi.Length] = new List<string>();
                sizeGroups[fi.Length].Add(file);
            }

            // Hash files with same size to find true duplicates
            var duplicateGroups = new List<object>();
            long totalWastedBytes = 0;

            using (var md5 = MD5.Create())
            {
                foreach (var group in sizeGroups.Values.Where(g => g.Count > 1))
                {
                    if (duplicateGroups.Count >= limit) break;

                    var hashGroups = new Dictionary<string, List<string>>();
                    foreach (var filePath in group)
                    {
                        try
                        {
                            using (var stream = File.OpenRead(filePath))
                            {
                                var hash = BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", "");
                                if (!hashGroups.ContainsKey(hash))
                                    hashGroups[hash] = new List<string>();
                                hashGroups[hash].Add(filePath);
                            }
                        }
                        catch { /* Skip files that can't be read */ }
                    }

                    foreach (var hashGroup in hashGroups.Values.Where(g => g.Count > 1))
                    {
                        var fi = new FileInfo(hashGroup[0]);
                        long wasted = fi.Length * (hashGroup.Count - 1);
                        totalWastedBytes += wasted;

                        // Convert to relative paths
                        var projectRoot = Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar;
                        var relativePaths = hashGroup.Select(p =>
                            p.Replace("\\", "/").Replace(projectRoot.Replace("\\", "/"), "")
                        ).ToArray();

                        duplicateGroups.Add(new
                        {
                            count = hashGroup.Count,
                            sizeKB = (int)(fi.Length / 1024),
                            wastedKB = (int)(wasted / 1024),
                            files = relativePaths
                        });
                    }
                }
            }

            return ToolResponse.OkWithData(new
            {
                duplicateGroupCount = duplicateGroups.Count,
                totalWastedKB = (int)(totalWastedBytes / 1024),
                totalWastedMB = Math.Round(totalWastedBytes / (1024.0 * 1024.0), 2),
                groups = duplicateGroups
            }, $"Found {duplicateGroups.Count} groups of duplicate files ({Math.Round(totalWastedBytes / (1024.0 * 1024.0), 2)} MB wasted)");
        }

        /// <summary>
        /// Find components with missing asset references by inspecting SerializedProperty.
        /// </summary>
        private ToolResponse HandleFindMissingReferences(JObject parameters)
        {
            int limit = ToolHelpers.GetOptionalInt(parameters, "limit", 50);

            var allObjects = Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(go => !EditorUtility.IsPersistent(go) && go.hideFlags == HideFlags.None)
                .ToArray();

            var issues = new List<object>();

            foreach (var go in allObjects)
            {
                if (issues.Count >= limit) break;

                var components = go.GetComponents<Component>();
                foreach (var component in components.Where(c => c != null))
                {
                    if (issues.Count >= limit) break;

                    var so = new SerializedObject(component);
                    var prop = so.GetIterator();
                    while (prop.NextVisible(true))
                    {
                        if (prop.propertyType == SerializedPropertyType.ObjectReference)
                        {
                            if (prop.objectReferenceValue == null && prop.objectReferenceInstanceIDValue != 0)
                            {
                                issues.Add(new
                                {
                                    type = "MissingReference",
                                    gameObject = go.name,
                                    path = GetGameObjectPath(go),
                                    component = component.GetType().Name,
                                    property = prop.propertyPath
                                });
                                break; // One issue per component is enough
                            }
                        }
                    }
                }
            }

            return ToolResponse.OkWithData(new
            {
                scannedObjects = allObjects.Length,
                issueCount = issues.Count,
                issues
            }, $"Found {issues.Count} missing references in {allObjects.Length} GameObjects");
        }

        /// <summary>
        /// Find GameObjects with missing (null) script components.
        /// </summary>
        private ToolResponse HandleFindMissingScripts(JObject parameters)
        {
            int limit = ToolHelpers.GetOptionalInt(parameters, "limit", 50);

            var allObjects = Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(go => !EditorUtility.IsPersistent(go) && go.hideFlags == HideFlags.None)
                .ToArray();

            var issues = new List<object>();

            foreach (var go in allObjects)
            {
                if (issues.Count >= limit) break;

                int missingCount = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
                if (missingCount > 0)
                {
                    issues.Add(new
                    {
                        gameObject = go.name,
                        path = GetGameObjectPath(go),
                        missingScriptCount = missingCount
                    });
                }
            }

            int totalMissing = issues.Sum(i => ((dynamic)i).missingScriptCount);

            return ToolResponse.OkWithData(new
            {
                scannedObjects = allObjects.Length,
                affectedObjects = issues.Count,
                totalMissingScripts = totalMissing,
                objects = issues
            }, $"Found {totalMissing} missing scripts on {issues.Count} GameObjects");
        }

        /// <summary>
        /// Automatically remove all missing script components using GameObjectUtility.
        /// </summary>
        private ToolResponse HandleFixMissingScripts(JObject parameters)
        {
            var allObjects = Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(go => !EditorUtility.IsPersistent(go) && go.hideFlags == HideFlags.None)
                .ToArray();

            int totalRemoved = 0;
            var fixedObjects = new List<object>();

            foreach (var go in allObjects)
            {
                int count = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
                if (count > 0)
                {
                    Undo.RegisterCompleteObjectUndo(go, "Fix Missing Scripts");
                    GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
                    totalRemoved += count;
                    fixedObjects.Add(new
                    {
                        gameObject = go.name,
                        path = GetGameObjectPath(go),
                        removedCount = count
                    });
                }
            }

            return ToolResponse.OkWithData(new
            {
                scannedObjects = allObjects.Length,
                fixedObjects = fixedObjects.Count,
                totalRemovedComponents = totalRemoved,
                objects = fixedObjects
            }, $"Removed {totalRemoved} missing script components from {fixedObjects.Count} GameObjects");
        }

        /// <summary>
        /// Find empty folders in the project (folders with no non-.meta files).
        /// </summary>
        private ToolResponse HandleFindEmptyFolders(JObject parameters)
        {
            string folder = ToolHelpers.GetOptionalString(parameters, "folder", "Assets");

            var fullPath = Path.Combine(Directory.GetCurrentDirectory(), folder);
            if (!Directory.Exists(fullPath))
                return ToolResponse.Fail($"Folder not found: {folder}");

            var emptyFolders = new List<string>();
            FindEmptyFoldersRecursive(fullPath, emptyFolders);

            // Convert to relative paths
            var projectRoot = Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar;
            var relativePaths = emptyFolders
                .Select(p => p.Replace("\\", "/").Replace(projectRoot.Replace("\\", "/"), ""))
                .ToList();

            return ToolResponse.OkWithData(new
            {
                count = relativePaths.Count,
                folders = relativePaths
            }, $"Found {relativePaths.Count} empty folders");
        }

        /// <summary>
        /// Delete all empty folders found in the specified directory.
        /// </summary>
        private ToolResponse HandleDeleteEmptyFolders(JObject parameters)
        {
            string folder = ToolHelpers.GetOptionalString(parameters, "folder", "Assets");

            var fullPath = Path.Combine(Directory.GetCurrentDirectory(), folder);
            if (!Directory.Exists(fullPath))
                return ToolResponse.Fail($"Folder not found: {folder}");

            var emptyFolders = new List<string>();
            FindEmptyFoldersRecursive(fullPath, emptyFolders);

            // Convert to relative paths and delete deepest first
            var projectRoot = Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar;
            var relativePaths = emptyFolders
                .Select(p => p.Replace("\\", "/").Replace(projectRoot.Replace("\\", "/"), ""))
                .OrderByDescending(p => p.Length)
                .ToList();

            int deleted = 0;
            foreach (var relPath in relativePaths)
            {
                if (AssetDatabase.DeleteAsset(relPath))
                    deleted++;
            }

            AssetDatabase.Refresh();

            return ToolResponse.OkWithData(new
            {
                found = relativePaths.Count,
                deleted
            }, $"Deleted {deleted} of {relativePaths.Count} empty folders");
        }

        /// <summary>
        /// Find the largest files in the project by file size.
        /// </summary>
        private ToolResponse HandleFindLargeAssets(JObject parameters)
        {
            int minSizeKB = ToolHelpers.GetOptionalInt(parameters, "minSizeKB", 1024);
            string folder = ToolHelpers.GetOptionalString(parameters, "folder", "Assets");
            int limit = ToolHelpers.GetOptionalInt(parameters, "limit", 50);

            var fullFolderPath = Path.Combine(Directory.GetCurrentDirectory(), folder);
            if (!Directory.Exists(fullFolderPath))
                return ToolResponse.Fail($"Folder not found: {folder}");

            var projectRoot = Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar;
            var largeFiles = Directory.GetFiles(fullFolderPath, "*.*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".meta"))
                .Select(f => new FileInfo(f))
                .Where(fi => fi.Exists && fi.Length >= minSizeKB * 1024L)
                .OrderByDescending(fi => fi.Length)
                .Take(limit)
                .Select(fi =>
                {
                    var relativePath = fi.FullName.Replace("\\", "/").Replace(projectRoot.Replace("\\", "/"), "");
                    var asset = AssetDatabase.LoadMainAssetAtPath(relativePath);
                    return new
                    {
                        path = relativePath,
                        name = fi.Name,
                        type = asset != null ? asset.GetType().Name : "Unknown",
                        sizeKB = (int)(fi.Length / 1024),
                        sizeMB = Math.Round(fi.Length / (1024.0 * 1024.0), 2)
                    };
                })
                .ToArray();

            return ToolResponse.OkWithData(new
            {
                threshold = $"{minSizeKB}KB",
                count = largeFiles.Length,
                assets = largeFiles
            }, $"Found {largeFiles.Length} assets larger than {minSizeKB}KB");
        }

        /// <summary>
        /// Find which scenes and prefabs reference a specific asset.
        /// </summary>
        private ToolResponse HandleGetAssetUsage(JObject parameters)
        {
            string assetPath = ToolHelpers.GetRequiredString(parameters, "assetPath");
            int limit = ToolHelpers.GetOptionalInt(parameters, "limit", 50);

            if (!File.Exists(assetPath) && !AssetDatabase.LoadMainAssetAtPath(assetPath))
                return ToolResponse.Fail($"Asset not found: {assetPath}");

            var usedBy = new List<object>();

            // Check all assets for dependencies on the target
            var allGuids = AssetDatabase.FindAssets("t:Object", new[] { "Assets" });
            foreach (var guid in allGuids)
            {
                if (usedBy.Count >= limit) break;

                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path == assetPath) continue;
                if (string.IsNullOrEmpty(path)) continue;

                // Only check direct dependencies (not recursive) for performance
                var deps = AssetDatabase.GetDependencies(path, false);
                if (deps.Contains(assetPath))
                {
                    var asset = AssetDatabase.LoadMainAssetAtPath(path);
                    usedBy.Add(new
                    {
                        path,
                        name = asset != null ? asset.name : Path.GetFileNameWithoutExtension(path),
                        type = asset != null ? asset.GetType().Name : "Unknown"
                    });
                }
            }

            var targetAsset = AssetDatabase.LoadMainAssetAtPath(assetPath);

            return ToolResponse.OkWithData(new
            {
                asset = new
                {
                    path = assetPath,
                    name = targetAsset != null ? targetAsset.name : Path.GetFileNameWithoutExtension(assetPath),
                    type = targetAsset != null ? targetAsset.GetType().Name : "Unknown"
                },
                usedByCount = usedBy.Count,
                usedBy
            }, $"Asset '{assetPath}' is referenced by {usedBy.Count} other assets");
        }

        /// <summary>
        /// Get the dependency tree of an asset (what it depends on).
        /// </summary>
        private ToolResponse HandleGetDependencyTree(JObject parameters)
        {
            string assetPath = ToolHelpers.GetRequiredString(parameters, "assetPath");
            int depth = ToolHelpers.GetOptionalInt(parameters, "depth", 3);

            if (!File.Exists(assetPath) && !AssetDatabase.LoadMainAssetAtPath(assetPath))
                return ToolResponse.Fail($"Asset not found: {assetPath}");

            // depth > 0 means recursive, depth == 0 means direct only
            bool recursive = depth > 0;
            var deps = AssetDatabase.GetDependencies(assetPath, recursive)
                .Where(d => d != assetPath)
                .Select(d =>
                {
                    var asset = AssetDatabase.LoadMainAssetAtPath(d);
                    return new
                    {
                        path = d,
                        name = asset != null ? asset.name : Path.GetFileNameWithoutExtension(d),
                        type = asset != null ? asset.GetType().Name : "Unknown"
                    };
                })
                .ToArray();

            // Group by type for summary
            var typeGroups = deps
                .GroupBy(d => d.type)
                .Select(g => new { type = g.Key, count = g.Count() })
                .OrderByDescending(g => g.count)
                .ToArray();

            return ToolResponse.OkWithData(new
            {
                assetPath,
                recursive,
                dependencyCount = deps.Length,
                typeSummary = typeGroups,
                dependencies = deps
            }, $"Asset '{assetPath}' has {deps.Length} dependencies");
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Recursively find empty folders (no non-.meta files and all subfolders are also empty).
        /// </summary>
        private static bool FindEmptyFoldersRecursive(string path, List<string> results)
        {
            var dirs = Directory.GetDirectories(path);
            var files = Directory.GetFiles(path).Where(f => !f.EndsWith(".meta")).ToArray();

            bool allSubEmpty = true;
            foreach (var dir in dirs)
            {
                if (!FindEmptyFoldersRecursive(dir, results))
                    allSubEmpty = false;
            }

            if (files.Length == 0 && (dirs.Length == 0 || allSubEmpty))
            {
                results.Add(path);
                return true;
            }

            return false;
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
