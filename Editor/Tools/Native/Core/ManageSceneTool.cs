using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Tools.Safety;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AgentCore.Editor.Tools.Native.Core
{
    /// <summary>
    /// Manage Unity scenes - list, create, open, save, and get hierarchy.
    /// Directly calls Unity Editor API as part of the native tool system.
    /// </summary>
    [AgentTool("manage_scene",
        Description = "Manage Unity scenes: list all scenes in project, open/close/create/save scenes, get the full hierarchy tree, manage build settings scenes, and merge scenes. " +
            "Use get_hierarchy to understand scene structure before operating on objects. Use list to find scene file paths. " +
            "Applicable: scene-level operations (open, save, create, hierarchy inspection). " +
            "NOT for: modifying individual GameObjects (use manage_gameobject), searching for objects (use find_gameobjects), scene quality analysis (use scene_analysis). " +
            "Returns: scene list with paths and load states, or hierarchy tree with depth/components. " +
            "Note: opening a new scene discards unsaved changes in the current scene unless saved first.",
        Category = "Scene", RequiresMainThread = true,
        RiskLevel = ToolRiskLevel.Medium, Capabilities = ToolCapability.ModifyScene | ToolCapability.WriteProjectFiles,
        ReadOnlyActions = new[] { "get_active", "get_hierarchy", "list", "get_build_scenes", "list_open_scenes" })]
    public class ManageSceneTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""list"", ""get_hierarchy"", ""get_active"", ""create"", ""open"", ""save"", ""set_active"", ""new_scene"", ""open_scene"", ""save_scene_as"", ""list_open_scenes"", ""set_active_scene"", ""merge_scenes"", ""get_build_scenes"", ""add_to_build""],
                    ""description"": ""Action to perform""
                },
                ""scenePath"": {
                    ""type"": ""string"",
                    ""description"": ""Scene file path (for create/open)""
                },
                ""sceneName"": {
                    ""type"": ""string"",
                    ""description"": ""Scene name (for set_active)""
                },
                ""name"": {
                    ""type"": ""string"",
                    ""description"": ""Scene name for new_scene or set_active_scene""
                },
                ""path"": {
                    ""type"": ""string"",
                    ""description"": ""Scene asset path for open_scene, save_scene_as, and add_to_build""
                },
                ""mode"": {
                    ""type"": ""string"",
                    ""enum"": [""single"", ""additive""],
                    ""description"": ""Scene open/create mode""
                },
                ""save_current"": {
                    ""type"": ""boolean"",
                    ""description"": ""Save current open scenes before creating a new scene""
                },
                ""source"": {
                    ""type"": ""string"",
                    ""description"": ""Source loaded scene name or path for merge_scenes""
                },
                ""destination"": {
                    ""type"": ""string"",
                    ""description"": ""Destination loaded scene name or path for merge_scenes""
                },
                ""enabled"": {
                    ""type"": ""boolean"",
                    ""description"": ""Build Settings enabled flag for add_to_build""
                },
                ""additive"": {
                    ""type"": ""boolean"",
                    ""description"": ""Open scene additively (default: false)""
                },
                ""maxDepth"": {
                    ""type"": ""integer"",
                    ""description"": ""Max hierarchy depth for get_hierarchy (default: 3)""
                }
            },
            ""required"": [""action""]
        }");

        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_scene",
            description: "Manage Unity scenes - list, create, open, save, and get hierarchy",
            category: "Scene",
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
                    case "list":
                        response = HandleList();
                        break;
                    case "get_hierarchy":
                        response = HandleGetHierarchy(parameters);
                        break;
                    case "get_active":
                        response = HandleGetActive();
                        break;
                    case "create":
                        response = HandleCreate(parameters);
                        break;
                    case "open":
                        response = HandleOpen(parameters);
                        break;
                    case "save":
                        response = HandleSave(parameters);
                        break;
                    case "set_active":
                        response = HandleSetActive(parameters);
                        break;
                    case "new_scene":
                        response = HandleNewScene(parameters);
                        break;
                    case "open_scene":
                        response = HandleOpenScene(parameters);
                        break;
                    case "save_scene_as":
                        response = HandleSaveSceneAs(parameters);
                        break;
                    case "list_open_scenes":
                        response = HandleListOpenScenes();
                        break;
                    case "set_active_scene":
                        response = HandleSetActiveScene(parameters);
                        break;
                    case "merge_scenes":
                        response = HandleMergeScenes(parameters);
                        break;
                    case "get_build_scenes":
                        response = HandleGetBuildScenes();
                        break;
                    case "add_to_build":
                        response = HandleAddToBuild(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: list, get_hierarchy, get_active, create, open, save, set_active, new_scene, open_scene, save_scene_as, list_open_scenes, set_active_scene, merge_scenes, get_build_scenes, add_to_build");
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

        private ToolResponse HandleList()
        {
            try
            {
                var scenes = EditorBuildSettings.scenes;
                var sceneList = new JArray();

                for (int i = 0; i < scenes.Length; i++)
                {
                    var scene = scenes[i];
                    sceneList.Add(new JObject
                    {
                        ["buildIndex"] = i,
                        ["path"] = scene.path,
                        ["name"] = Path.GetFileNameWithoutExtension(scene.path),
                        ["enabled"] = scene.enabled
                    });
                }

                // Also include currently loaded scenes not in build settings
                var loadedScenes = new JArray();
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    loadedScenes.Add(new JObject
                    {
                        ["name"] = scene.name,
                        ["path"] = scene.path,
                        ["isLoaded"] = scene.isLoaded,
                        ["isDirty"] = scene.isDirty,
                        ["buildIndex"] = scene.buildIndex
                    });
                }

                return ToolResponse.OkWithData(new JObject
                {
                    ["buildScenes"] = sceneList,
                    ["loadedScenes"] = loadedScenes,
                    ["buildSceneCount"] = scenes.Length,
                    ["loadedSceneCount"] = SceneManager.sceneCount
                }, $"Found {scenes.Length} scenes in Build Settings, {SceneManager.sceneCount} loaded.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error listing scenes: {ex.Message}");
            }
        }

        private ToolResponse HandleGetHierarchy(JObject parameters)
        {
            try
            {
                int maxDepth = ToolHelpers.GetOptionalInt(parameters, "maxDepth", 3);
                var activeScene = SceneManager.GetActiveScene();

                if (!activeScene.IsValid())
                    return ToolResponse.Fail("No valid active scene.");

                var rootObjects = activeScene.GetRootGameObjects();
                var hierarchy = new JArray();

                foreach (var root in rootObjects)
                {
                    hierarchy.Add(BuildHierarchyNode(root, 0, maxDepth));
                }

                return ToolResponse.OkWithData(new JObject
                {
                    ["sceneName"] = activeScene.name,
                    ["scenePath"] = activeScene.path,
                    ["rootCount"] = rootObjects.Length,
                    ["maxDepth"] = maxDepth,
                    ["hierarchy"] = hierarchy
                }, $"Scene '{activeScene.name}' hierarchy with {rootObjects.Length} root objects.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error getting hierarchy: {ex.Message}");
            }
        }

        private ToolResponse HandleGetActive()
        {
            try
            {
                var scene = SceneManager.GetActiveScene();
                if (!scene.IsValid())
                    return ToolResponse.Fail("No valid active scene.");

                var rootObjects = scene.GetRootGameObjects();
                int totalObjects = rootObjects.Sum(CountDescendants);

                return ToolResponse.OkWithData(new JObject
                {
                    ["name"] = scene.name,
                    ["path"] = scene.path,
                    ["buildIndex"] = scene.buildIndex,
                    ["isDirty"] = scene.isDirty,
                    ["isLoaded"] = scene.isLoaded,
                    ["rootCount"] = scene.rootCount,
                    ["totalGameObjects"] = totalObjects
                }, $"Active scene: '{scene.name}' ({totalObjects} GameObjects).");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error getting active scene info: {ex.Message}");
            }
        }

        private ToolResponse HandleCreate(JObject parameters)
        {
            try
            {
                var scenePath = ToolHelpers.GetRequiredString(parameters, "scenePath");
                scenePath = ToolHelpers.NormalizeAssetPath(scenePath);

                if (!scenePath.EndsWith(".unity"))
                    scenePath += ".unity";

                // Check if scene already exists
                if (File.Exists(scenePath))
                    return ToolResponse.Fail($"Scene already exists at '{scenePath}'.");

                // Ensure directory exists
                ToolHelpers.EnsureDirectoryExists(scenePath);

                // Create new scene
                var newScene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                bool saved = EditorSceneManager.SaveScene(newScene, scenePath);

                if (saved)
                {
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                    AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] Created scene at '{scenePath}'");
                    return ToolResponse.OkWithData(new JObject
                    {
                        ["path"] = scenePath,
                        ["name"] = Path.GetFileNameWithoutExtension(scenePath)
                    }, $"Scene created at '{scenePath}'.");
                }

                return ToolResponse.Fail($"Failed to save new scene to '{scenePath}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error creating scene: {ex.Message}");
            }
        }

        private ToolResponse HandleOpen(JObject parameters)
        {
            try
            {
                var scenePath = ToolHelpers.GetRequiredString(parameters, "scenePath");
                scenePath = ToolHelpers.NormalizeAssetPath(scenePath);
                bool additive = ToolHelpers.GetOptionalBool(parameters, "additive", false);

                if (!File.Exists(scenePath))
                    return ToolResponse.Fail($"Scene file not found at '{scenePath}'.");

                // Check for unsaved changes
                if (!additive && EditorSceneManager.GetActiveScene().isDirty)
                {
                    return ToolResponse.Fail(
                        "Current scene has unsaved changes. Save first or use additive mode.");
                }

                var mode = additive ? OpenSceneMode.Additive : OpenSceneMode.Single;
                var scene = EditorSceneManager.OpenScene(scenePath, mode);

                AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] Opened scene '{scenePath}' (additive={additive})");
                return ToolResponse.OkWithData(new JObject
                {
                    ["path"] = scene.path,
                    ["name"] = scene.name,
                    ["additive"] = additive
                }, $"Scene '{scene.name}' opened successfully.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error opening scene: {ex.Message}");
            }
        }

        private ToolResponse HandleSave(JObject parameters)
        {
            try
            {
                var scenePath = ToolHelpers.GetOptionalString(parameters, "scenePath");
                var activeScene = EditorSceneManager.GetActiveScene();

                if (!activeScene.IsValid())
                    return ToolResponse.Fail("No valid active scene to save.");

                bool saved;
                string finalPath;

                if (!string.IsNullOrEmpty(scenePath))
                {
                    // Save As
                    scenePath = ToolHelpers.NormalizeAssetPath(scenePath);
                    if (!scenePath.EndsWith(".unity"))
                        scenePath += ".unity";
                    ToolHelpers.EnsureDirectoryExists(scenePath);
                    saved = EditorSceneManager.SaveScene(activeScene, scenePath);
                    finalPath = scenePath;
                }
                else
                {
                    // Save current
                    if (string.IsNullOrEmpty(activeScene.path))
                        return ToolResponse.Fail("Scene is untitled. Provide 'scenePath' to save.");

                    saved = EditorSceneManager.SaveScene(activeScene);
                    finalPath = activeScene.path;
                }

                if (saved)
                {
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                    AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] Saved scene to '{finalPath}'");
                    return ToolResponse.OkWithData(new JObject
                    {
                        ["path"] = finalPath,
                        ["name"] = activeScene.name
                    }, $"Scene '{activeScene.name}' saved to '{finalPath}'.");
                }

                return ToolResponse.Fail($"Failed to save scene '{activeScene.name}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error saving scene: {ex.Message}");
            }
        }

        private ToolResponse HandleSetActive(JObject parameters)
        {
            try
            {
                var sceneName = ToolHelpers.GetRequiredString(parameters, "sceneName");

                // Find the scene among loaded scenes
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (scene.name == sceneName || scene.path.EndsWith($"/{sceneName}.unity"))
                    {
                        if (!scene.isLoaded)
                            return ToolResponse.Fail($"Scene '{sceneName}' is not loaded.");

                        SceneManager.SetActiveScene(scene);
                        AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] Set active scene to '{scene.name}'");
                        return ToolResponse.OkWithData(new JObject
                        {
                            ["name"] = scene.name,
                            ["path"] = scene.path
                        }, $"Active scene set to '{scene.name}'.");
                    }
                }

                return ToolResponse.Fail($"Scene '{sceneName}' not found among loaded scenes.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error setting active scene: {ex.Message}");
            }
        }

        private ToolResponse HandleNewScene(JObject parameters)
        {
            try
            {
                bool saveCurrent = ToolHelpers.GetOptionalBool(parameters, "save_current", true);
                if (saveCurrent && !EditorSceneManager.SaveOpenScenes())
                    return ToolResponse.Fail("Failed to save current open scenes before creating a new scene.");

                var mode = ParseNewSceneMode(ToolHelpers.GetOptionalString(parameters, "mode", "single"));
                var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, mode);
                var name = ToolHelpers.GetOptionalString(parameters, "name");
                if (!string.IsNullOrEmpty(name))
                {
                    var savedPath = NormalizeScenePath(name.Contains("/") || name.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) ? name : $"Assets/{name}.unity");
                    ToolHelpers.EnsureDirectoryExists(savedPath);
                    if (!EditorSceneManager.SaveScene(scene, savedPath))
                        return ToolResponse.Fail($"Failed to save new scene to '{savedPath}'.");
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                }

                return ToolResponse.OkWithData(SerializeScene(scene), $"New scene '{scene.name}' created.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error creating new scene: {ex.Message}");
            }
        }

        private ToolResponse HandleOpenScene(JObject parameters)
        {
            try
            {
                var path = NormalizeScenePath(ToolHelpers.GetRequiredString(parameters, "path"));
                if (!File.Exists(path))
                    return ToolResponse.Fail($"Scene file not found at '{path}'.");

                var mode = ParseOpenSceneMode(ToolHelpers.GetOptionalString(parameters, "mode", "single"));
                if (mode == OpenSceneMode.Single && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                    return ToolResponse.Fail("Current scene has unsaved changes. Save first or use additive mode.");

                var scene = EditorSceneManager.OpenScene(path, mode);
                return ToolResponse.OkWithData(SerializeScene(scene), $"Scene '{scene.name}' opened successfully.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error opening scene: {ex.Message}");
            }
        }

        private ToolResponse HandleSaveSceneAs(JObject parameters)
        {
            try
            {
                var path = NormalizeScenePath(ToolHelpers.GetRequiredString(parameters, "path"));
                var activeScene = EditorSceneManager.GetActiveScene();
                if (!activeScene.IsValid())
                    return ToolResponse.Fail("No valid active scene to save.");

                ToolHelpers.EnsureDirectoryExists(path);
                if (!EditorSceneManager.SaveScene(activeScene, path))
                    return ToolResponse.Fail($"Failed to save scene '{activeScene.name}' to '{path}'.");

                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                return ToolResponse.OkWithData(SerializeScene(activeScene), $"Scene '{activeScene.name}' saved to '{path}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error saving scene as: {ex.Message}");
            }
        }

        private ToolResponse HandleListOpenScenes()
        {
            try
            {
                var scenes = GetLoadedScenesArray();
                return ToolResponse.OkWithData(new JObject
                {
                    ["loadedSceneCount"] = SceneManager.sceneCount,
                    ["activeScene"] = SceneManager.GetActiveScene().name,
                    ["scenes"] = scenes
                }, $"Found {SceneManager.sceneCount} open scene(s).");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error listing open scenes: {ex.Message}");
            }
        }

        private ToolResponse HandleSetActiveScene(JObject parameters)
        {
            try
            {
                var sceneIdentifier = ToolHelpers.GetOptionalString(parameters, "name");
                if (string.IsNullOrEmpty(sceneIdentifier))
                    sceneIdentifier = ToolHelpers.GetOptionalString(parameters, "path");
                if (string.IsNullOrEmpty(sceneIdentifier))
                    return ToolResponse.Fail("'name' or 'path' is required for 'set_active_scene' action.");

                var scene = FindLoadedScene(sceneIdentifier);
                if (!scene.IsValid())
                    return ToolResponse.Fail($"Scene '{sceneIdentifier}' not found among loaded scenes.");
                if (!scene.isLoaded)
                    return ToolResponse.Fail($"Scene '{sceneIdentifier}' is not loaded.");

                SceneManager.SetActiveScene(scene);
                return ToolResponse.OkWithData(SerializeScene(scene), $"Active scene set to '{scene.name}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error setting active scene: {ex.Message}");
            }
        }

        private ToolResponse HandleMergeScenes(JObject parameters)
        {
            try
            {
                var sourceName = ToolHelpers.GetRequiredString(parameters, "source");
                var destinationName = ToolHelpers.GetRequiredString(parameters, "destination");
                var source = FindLoadedScene(sourceName);
                var destination = FindLoadedScene(destinationName);
                if (!source.IsValid())
                    return ToolResponse.Fail($"Source scene '{sourceName}' not found among loaded scenes.");
                if (!destination.IsValid())
                    return ToolResponse.Fail($"Destination scene '{destinationName}' not found among loaded scenes.");

                EditorSceneManager.MergeScenes(source, destination);
                EditorSceneManager.MarkSceneDirty(destination);
                return ToolResponse.OkWithData(SerializeScene(destination), $"Scene '{sourceName}' merged into '{destination.name}'.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error merging scenes: {ex.Message}");
            }
        }

        private ToolResponse HandleGetBuildScenes()
        {
            try
            {
                var sceneList = GetBuildScenesArray();
                return ToolResponse.OkWithData(new JObject
                {
                    ["buildSceneCount"] = sceneList.Count,
                    ["scenes"] = sceneList
                }, $"Found {sceneList.Count} scenes in Build Settings.");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error getting build scenes: {ex.Message}");
            }
        }

        private ToolResponse HandleAddToBuild(JObject parameters)
        {
            try
            {
                var path = NormalizeScenePath(ToolHelpers.GetRequiredString(parameters, "path"));
                bool enabled = ToolHelpers.GetOptionalBool(parameters, "enabled", true);
                if (!File.Exists(path))
                    return ToolResponse.Fail($"Scene file not found at '{path}'.");

                var scenes = EditorBuildSettings.scenes.ToList();
                var existing = scenes.FirstOrDefault(s => string.Equals(s.path, path, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.enabled = enabled;
                }
                else
                {
                    scenes.Add(new EditorBuildSettingsScene(path, enabled));
                }

                EditorBuildSettings.scenes = scenes.ToArray();
                AssetDatabase.SaveAssets();
                return ToolResponse.OkWithData(new JObject
                {
                    ["path"] = path,
                    ["enabled"] = enabled,
                    ["buildSceneCount"] = scenes.Count
                }, $"Scene '{path}' added to Build Settings (enabled={enabled}).");
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Error adding scene to Build Settings: {ex.Message}");
            }
        }

        #endregion

        #region Helpers

        private JObject BuildHierarchyNode(GameObject go, int currentDepth, int maxDepth)
        {
            var node = new JObject
            {
                ["name"] = go.name,
                ["instanceId"] = go.GetInstanceID(),
                ["activeSelf"] = go.activeSelf,
                ["childCount"] = go.transform.childCount
            };

            // Add component type names
            var components = go.GetComponents<Component>();
            var compNames = new JArray();
            foreach (var comp in components)
            {
                if (comp != null)
                    compNames.Add(comp.GetType().Name);
            }
            node["components"] = compNames;

            // Recurse into children if within depth limit
            if (currentDepth < maxDepth && go.transform.childCount > 0)
            {
                var children = new JArray();
                for (int i = 0; i < go.transform.childCount; i++)
                {
                    var child = go.transform.GetChild(i).gameObject;
                    children.Add(BuildHierarchyNode(child, currentDepth + 1, maxDepth));
                }
                node["children"] = children;
            }
            else if (go.transform.childCount > 0)
            {
                node["childrenTruncated"] = true;
            }

            return node;
        }

        private int CountDescendants(GameObject go)
        {
            int count = 1; // Count self
            for (int i = 0; i < go.transform.childCount; i++)
            {
                count += CountDescendants(go.transform.GetChild(i).gameObject);
            }
            return count;
        }

        private JArray GetBuildScenesArray()
        {
            var scenes = EditorBuildSettings.scenes;
            var sceneList = new JArray();
            for (int i = 0; i < scenes.Length; i++)
            {
                var scene = scenes[i];
                sceneList.Add(new JObject
                {
                    ["buildIndex"] = i,
                    ["path"] = scene.path,
                    ["name"] = Path.GetFileNameWithoutExtension(scene.path),
                    ["enabled"] = scene.enabled
                });
            }
            return sceneList;
        }

        private JArray GetLoadedScenesArray()
        {
            var loadedScenes = new JArray();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                loadedScenes.Add(SerializeScene(SceneManager.GetSceneAt(i)));
            }
            return loadedScenes;
        }

        private JObject SerializeScene(Scene scene)
        {
            return new JObject
            {
                ["name"] = scene.name,
                ["path"] = scene.path,
                ["isLoaded"] = scene.isLoaded,
                ["isDirty"] = scene.isDirty,
                ["buildIndex"] = scene.buildIndex,
                ["rootCount"] = scene.IsValid() ? scene.rootCount : 0
            };
        }

        private Scene FindLoadedScene(string nameOrPath)
        {
            var normalized = nameOrPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) || nameOrPath.Contains("/")
                ? NormalizeScenePath(nameOrPath)
                : nameOrPath;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (string.Equals(scene.name, nameOrPath, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(scene.path, normalized, StringComparison.OrdinalIgnoreCase) ||
                    scene.path.EndsWith($"/{nameOrPath}.unity", StringComparison.OrdinalIgnoreCase))
                    return scene;
            }

            return default;
        }

        private string NormalizeScenePath(string path)
        {
            path = ToolHelpers.NormalizeAssetPath(path);
            if (!path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                path += ".unity";
            return path;
        }

        private NewSceneMode ParseNewSceneMode(string mode)
        {
            return string.Equals(mode, "additive", StringComparison.OrdinalIgnoreCase)
                ? NewSceneMode.Additive
                : NewSceneMode.Single;
        }

        private OpenSceneMode ParseOpenSceneMode(string mode)
        {
            return string.Equals(mode, "additive", StringComparison.OrdinalIgnoreCase)
                ? OpenSceneMode.Additive
                : OpenSceneMode.Single;
        }

        #endregion
    }
}
