using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Infrastructure;
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
    [AgentTool("manage_scene", Description = "Manage Unity scenes - list, create, open, save, and get hierarchy", Category = "Scene", RequiresMainThread = true)]
    public class ManageSceneTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""list"", ""get_hierarchy"", ""get_active"", ""create"", ""open"", ""save"", ""set_active""],
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
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: list, get_hierarchy, get_active, create, open, save, set_active");
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
                    Debug.Log($"[AgentCore] Created scene at '{scenePath}'");
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

                Debug.Log($"[AgentCore] Opened scene '{scenePath}' (additive={additive})");
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
                    Debug.Log($"[AgentCore] Saved scene to '{finalPath}'");
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
                        Debug.Log($"[AgentCore] Set active scene to '{scene.name}'");
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

        #endregion
    }
}
