using System;
using System.Collections.Generic;
using System.Diagnostics;
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

namespace AgentCore.Editor.Tools.Native.Utility
{
    /// <summary>
    /// Scene and project validation tool — checks for common issues like missing references,
    /// duplicate names, missing components, layer/tag problems, and performance concerns.
    /// Useful for quality assurance before builds.
    /// </summary>
    [AgentTool("validate",
        Description = "Validate scene and project quality: check for missing references, duplicate names, missing components, empty GameObjects, layer/tag issues, and performance concerns. Returns structured issue reports with severity levels.",
        Category = "Utility",
        RequiresMainThread = true,
        MayModifyScripts = false,
        RiskLevel = ToolRiskLevel.ReadOnly,
        Capabilities = ToolCapability.ReadProject)]
    public class ValidationTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [
                        ""check_missing_references"", ""check_duplicate_names"", ""check_empty_gameobjects"",
                        ""check_missing_components"", ""check_layer_tags"", ""check_performance"",
                        ""check_prefab_integrity"", ""check_audio"", ""validate_scene"", ""validate_project""
                    ],
                    ""description"": ""Validation action to perform""
                },
                ""scope"": {
                    ""type"": ""string"",
                    ""enum"": [""scene"", ""selection"", ""all_scenes""],
                    ""description"": ""Scope of validation (default: scene)""
                },
                ""severity_filter"": {
                    ""type"": ""string"",
                    ""enum"": [""error"", ""warning"", ""info"", ""all""],
                    ""description"": ""Minimum severity level to report (default: all)""
                },
                ""max_issues"": {
                    ""type"": ""integer"",
                    ""description"": ""Maximum number of issues to return (default: 100)""
                },
                ""include_inactive"": {
                    ""type"": ""boolean"",
                    ""description"": ""Include inactive GameObjects in validation (default: true)""
                }
            },
            ""required"": [""action""]
        }");

        /// <summary>
        /// Tool metadata for registration and LLM discovery.
        /// </summary>
        public ToolMetadata Metadata => new ToolMetadata(
            name: "validate",
            description: "Validate scene and project quality: check for missing references, duplicate names, missing components, empty GameObjects, layer/tag issues, and performance concerns. Returns structured issue reports with severity levels.",
            category: "Utility",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        /// <summary>
        /// Execute a validation action.
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
                    case "check_missing_references":
                        response = HandleCheckMissingReferences(parameters);
                        break;
                    case "check_duplicate_names":
                        response = HandleCheckDuplicateNames(parameters);
                        break;
                    case "check_empty_gameobjects":
                        response = HandleCheckEmptyGameObjects(parameters);
                        break;
                    case "check_missing_components":
                        response = HandleCheckMissingComponents(parameters);
                        break;
                    case "check_layer_tags":
                        response = HandleCheckLayerTags(parameters);
                        break;
                    case "check_performance":
                        response = HandleCheckPerformance(parameters);
                        break;
                    case "check_prefab_integrity":
                        response = HandleCheckPrefabIntegrity(parameters);
                        break;
                    case "check_audio":
                        response = HandleCheckAudio(parameters);
                        break;
                    case "validate_scene":
                        response = HandleValidateScene(parameters);
                        break;
                    case "validate_project":
                        response = HandleValidateProject(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: check_missing_references, check_duplicate_names, " +
                            "check_empty_gameobjects, check_missing_components, check_layer_tags, check_performance, " +
                            "check_prefab_integrity, check_audio, validate_scene, validate_project");
                        break;
                }
            }
            catch (Exception ex)
            {
                response = ToolResponse.Fail($"Error executing validate '{parameters?["action"]}': {ex.Message}");
            }

            sw.Stop();
            return Task.FromResult(response.ToToolResult(sw.Elapsed.TotalMilliseconds));
        }

        // ─────────────────────────────────────────────────────────────────────
        // Validation Handlers
        // ─────────────────────────────────────────────────────────────────────

        private ToolResponse HandleCheckMissingReferences(JObject parameters)
        {
            var includeInactive = ToolHelpers.GetOptionalBool(parameters, "include_inactive", true);
            var maxIssues = ToolHelpers.GetOptionalInt(parameters, "max_issues", 100);

            var issues = new List<ValidationIssue>();
            var allObjects = GetAllGameObjects(includeInactive);

            foreach (var go in allObjects)
            {
                var components = go.GetComponents<Component>();
                foreach (var comp in components)
                {
                    if (comp == null)
                    {
                        issues.Add(new ValidationIssue
                        {
                            severity = "error",
                            category = "missing_reference",
                            game_object = go.name,
                            path = GetPath(go),
                            message = "Missing (destroyed) component script reference",
                            fix_hint = "Remove the missing component via Inspector > right-click > Remove Component"
                        });
                        continue;
                    }

                    // Check serialized fields for null references
                    var so = new SerializedObject(comp);
                    var prop = so.GetIterator();
                    bool enterChildren = true;

                    while (prop.NextVisible(enterChildren))
                    {
                        enterChildren = prop.propertyType != SerializedPropertyType.String;

                        if (prop.propertyType == SerializedPropertyType.ObjectReference &&
                            prop.objectReferenceValue == null &&
                            prop.objectReferenceInstanceIDValue != 0)
                        {
                            issues.Add(new ValidationIssue
                            {
                                severity = "error",
                                category = "missing_reference",
                                game_object = go.name,
                                path = GetPath(go),
                                component = comp.GetType().Name,
                                field = prop.name,
                                message = $"Missing object reference on field '{prop.name}' in {comp.GetType().Name}",
                                fix_hint = "Reassign the missing reference in the Inspector"
                            });

                            if (issues.Count >= maxIssues) break;
                        }
                    }

                    if (issues.Count >= maxIssues) break;
                }

                if (issues.Count >= maxIssues) break;
            }

            return BuildIssueResponse("check_missing_references", issues, maxIssues, allObjects.Count);
        }

        private ToolResponse HandleCheckDuplicateNames(JObject parameters)
        {
            var includeInactive = ToolHelpers.GetOptionalBool(parameters, "include_inactive", true);
            var maxIssues = ToolHelpers.GetOptionalInt(parameters, "max_issues", 100);

            var allObjects = GetAllGameObjects(includeInactive);
            var nameGroups = allObjects
                .GroupBy(go => go.name)
                .Where(g => g.Count() > 1)
                .ToList();

            var issues = new List<ValidationIssue>();

            foreach (var group in nameGroups)
            {
                foreach (var go in group)
                {
                    issues.Add(new ValidationIssue
                    {
                        severity = "warning",
                        category = "duplicate_name",
                        game_object = go.name,
                        path = GetPath(go),
                        message = $"Duplicate name '{go.name}' ({group.Count()} objects share this name)",
                        fix_hint = "Rename objects to be unique, especially if referenced by name in code (GameObject.Find)"
                    });

                    if (issues.Count >= maxIssues) break;
                }

                if (issues.Count >= maxIssues) break;
            }

            return BuildIssueResponse("check_duplicate_names", issues, maxIssues, allObjects.Count);
        }

        private ToolResponse HandleCheckEmptyGameObjects(JObject parameters)
        {
            var includeInactive = ToolHelpers.GetOptionalBool(parameters, "include_inactive", true);
            var maxIssues = ToolHelpers.GetOptionalInt(parameters, "max_issues", 100);

            var allObjects = GetAllGameObjects(includeInactive);
            var issues = new List<ValidationIssue>();

            foreach (var go in allObjects)
            {
                var components = go.GetComponents<Component>();
                // Only Transform (or RectTransform) = truly empty
                var nonTransformComponents = components.Where(c => c != null && !(c is Transform)).ToList();

                if (nonTransformComponents.Count == 0 && go.transform.childCount == 0)
                {
                    issues.Add(new ValidationIssue
                    {
                        severity = "info",
                        category = "empty_gameobject",
                        game_object = go.name,
                        path = GetPath(go),
                        message = $"Empty GameObject '{go.name}' has no components and no children",
                        fix_hint = "Consider removing this empty GameObject or adding components to it"
                    });

                    if (issues.Count >= maxIssues) break;
                }
            }

            return BuildIssueResponse("check_empty_gameobjects", issues, maxIssues, allObjects.Count);
        }

        private ToolResponse HandleCheckMissingComponents(JObject parameters)
        {
            var includeInactive = ToolHelpers.GetOptionalBool(parameters, "include_inactive", true);
            var maxIssues = ToolHelpers.GetOptionalInt(parameters, "max_issues", 100);

            var allObjects = GetAllGameObjects(includeInactive);
            var issues = new List<ValidationIssue>();

            foreach (var go in allObjects)
            {
                var components = go.GetComponents<Component>();
                int missingCount = 0;

                foreach (var comp in components)
                {
                    if (comp == null)
                        missingCount++;
                }

                if (missingCount > 0)
                {
                    issues.Add(new ValidationIssue
                    {
                        severity = "error",
                        category = "missing_component",
                        game_object = go.name,
                        path = GetPath(go),
                        message = $"{missingCount} missing component script(s) on '{go.name}'",
                        fix_hint = "Select the GameObject and use Edit > Remove Missing Scripts, or right-click component > Remove Component"
                    });

                    if (issues.Count >= maxIssues) break;
                }
            }

            return BuildIssueResponse("check_missing_components", issues, maxIssues, allObjects.Count);
        }

        private ToolResponse HandleCheckLayerTags(JObject parameters)
        {
            var includeInactive = ToolHelpers.GetOptionalBool(parameters, "include_inactive", true);
            var maxIssues = ToolHelpers.GetOptionalInt(parameters, "max_issues", 100);

            var allObjects = GetAllGameObjects(includeInactive);
            var issues = new List<ValidationIssue>();

            // Get valid layers and tags
            var validLayers = new HashSet<int>();
            for (int i = 0; i < 32; i++)
            {
                if (!string.IsNullOrEmpty(LayerMask.LayerToName(i)))
                    validLayers.Add(i);
            }

            foreach (var go in allObjects)
            {
                // Check layer
                if (!validLayers.Contains(go.layer))
                {
                    issues.Add(new ValidationIssue
                    {
                        severity = "warning",
                        category = "invalid_layer",
                        game_object = go.name,
                        path = GetPath(go),
                        message = $"GameObject '{go.name}' uses undefined layer {go.layer}",
                        fix_hint = "Assign a valid layer in the Inspector or define the layer in Project Settings > Tags and Layers"
                    });
                }

                // Check tag
                try
                {
                    var tag = go.tag;
                    if (string.IsNullOrEmpty(tag) || tag == "Untagged")
                    {
                        // Not an error, just info for root objects
                        if (go.transform.parent == null)
                        {
                            issues.Add(new ValidationIssue
                            {
                                severity = "info",
                                category = "untagged",
                                game_object = go.name,
                                path = GetPath(go),
                                message = $"Root GameObject '{go.name}' is Untagged",
                                fix_hint = "Consider assigning a meaningful tag for easier identification"
                            });
                        }
                    }
                }
                catch (UnityException)
                {
                    issues.Add(new ValidationIssue
                    {
                        severity = "warning",
                        category = "invalid_tag",
                        game_object = go.name,
                        path = GetPath(go),
                        message = $"GameObject '{go.name}' uses an undefined tag",
                        fix_hint = "Assign a valid tag in the Inspector or define the tag in Project Settings > Tags and Layers"
                    });
                }

                if (issues.Count >= maxIssues) break;
            }

            return BuildIssueResponse("check_layer_tags", issues, maxIssues, allObjects.Count);
        }

        private ToolResponse HandleCheckPerformance(JObject parameters)
        {
            var includeInactive = ToolHelpers.GetOptionalBool(parameters, "include_inactive", false);
            var maxIssues = ToolHelpers.GetOptionalInt(parameters, "max_issues", 100);

            var allObjects = GetAllGameObjects(includeInactive);
            var issues = new List<ValidationIssue>();

            // Check for excessive polygon counts
            var meshFilters = allObjects
                .SelectMany(go => go.GetComponents<MeshFilter>())
                .Where(mf => mf != null && mf.sharedMesh != null)
                .ToList();

            int totalTriangles = 0;
            foreach (var mf in meshFilters)
            {
                var triCount = mf.sharedMesh.triangles.Length / 3;
                totalTriangles += triCount;

                if (triCount > 50000)
                {
                    issues.Add(new ValidationIssue
                    {
                        severity = "warning",
                        category = "performance",
                        game_object = mf.gameObject.name,
                        path = GetPath(mf.gameObject),
                        message = $"High polygon mesh: '{mf.sharedMesh.name}' has {triCount:N0} triangles",
                        fix_hint = "Consider using LOD groups or reducing polygon count for better performance"
                    });
                }
            }

            // Check for lights without baking
            var lights = allObjects
                .SelectMany(go => go.GetComponents<Light>())
                .Where(l => l != null)
                .ToList();

            int realtimeLights = lights.Count(l => l.lightmapBakeType == LightmapBakeType.Realtime);
            if (realtimeLights > 4)
            {
                issues.Add(new ValidationIssue
                {
                    severity = "warning",
                    category = "performance",
                    game_object = "Scene",
                    path = "Scene",
                    message = $"{realtimeLights} realtime lights in scene (recommended: ≤4 for mobile)",
                    fix_hint = "Consider baking static lights or using mixed lighting mode"
                });
            }

            // Check for cameras with high depth
            var cameras = allObjects
                .SelectMany(go => go.GetComponents<Camera>())
                .Where(c => c != null)
                .ToList();

            if (cameras.Count > 3)
            {
                issues.Add(new ValidationIssue
                {
                    severity = "info",
                    category = "performance",
                    game_object = "Scene",
                    path = "Scene",
                    message = $"{cameras.Count} cameras active in scene",
                    fix_hint = "Multiple cameras increase rendering cost. Disable cameras that are not needed."
                });
            }

            // Check for AudioSources without 3D settings
            var audioSources = allObjects
                .SelectMany(go => go.GetComponents<AudioSource>())
                .Where(a => a != null && a.clip != null)
                .ToList();

            foreach (var audio in audioSources)
            {
                if (audio.spatialBlend == 0f && audio.transform.parent != null)
                {
                    issues.Add(new ValidationIssue
                    {
                        severity = "info",
                        category = "performance",
                        game_object = audio.gameObject.name,
                        path = GetPath(audio.gameObject),
                        message = $"AudioSource '{audio.gameObject.name}' is 2D (spatialBlend=0) but is not at root level",
                        fix_hint = "Consider using 3D audio (spatialBlend=1) for world-space sounds"
                    });
                }

                if (issues.Count >= maxIssues) break;
            }

            // Summary stats
            var stats = new
            {
                total_gameobjects = allObjects.Count,
                total_triangles = totalTriangles,
                mesh_count = meshFilters.Count,
                light_count = lights.Count,
                realtime_light_count = realtimeLights,
                camera_count = cameras.Count,
                audio_source_count = audioSources.Count
            };

            return ToolResponse.OkWithData(new
            {
                action = "check_performance",
                stats,
                issue_count = issues.Count,
                issues = issues.Take(maxIssues).Select(i => i.ToDictionary()).ToList()
            }, $"Performance check: {issues.Count} issue(s) found. Total triangles: {totalTriangles:N0}");
        }

        private ToolResponse HandleCheckPrefabIntegrity(JObject parameters)
        {
            var maxIssues = ToolHelpers.GetOptionalInt(parameters, "max_issues", 100);
            var includeInactive = ToolHelpers.GetOptionalBool(parameters, "include_inactive", true);

            var allObjects = GetAllGameObjects(includeInactive);
            var issues = new List<ValidationIssue>();

            foreach (var go in allObjects)
            {
                var prefabStatus = PrefabUtility.GetPrefabInstanceStatus(go);
                var prefabType = PrefabUtility.GetPrefabAssetType(go);

                // Check for disconnected prefabs
                if (prefabStatus == PrefabInstanceStatus.Disconnected)
                {
                    issues.Add(new ValidationIssue
                    {
                        severity = "warning",
                        category = "prefab_integrity",
                        game_object = go.name,
                        path = GetPath(go),
                        message = $"Prefab instance '{go.name}' is disconnected from its source prefab",
                        fix_hint = "Reconnect via right-click > Prefab > Reconnect to Prefab Asset, or unpack the prefab"
                    });
                }

                // Check for missing prefab source
                if (prefabType == PrefabAssetType.MissingAsset)
                {
                    issues.Add(new ValidationIssue
                    {
                        severity = "error",
                        category = "prefab_integrity",
                        game_object = go.name,
                        path = GetPath(go),
                        message = $"Prefab instance '{go.name}' references a missing prefab asset",
                        fix_hint = "Restore the missing prefab asset or unpack the prefab instance"
                    });
                }

                if (issues.Count >= maxIssues) break;
            }

            return BuildIssueResponse("check_prefab_integrity", issues, maxIssues, allObjects.Count);
        }

        private ToolResponse HandleCheckAudio(JObject parameters)
        {
            var includeInactive = ToolHelpers.GetOptionalBool(parameters, "include_inactive", true);
            var maxIssues = ToolHelpers.GetOptionalInt(parameters, "max_issues", 100);

            var allObjects = GetAllGameObjects(includeInactive);
            var issues = new List<ValidationIssue>();

            foreach (var go in allObjects)
            {
                var audioSources = go.GetComponents<AudioSource>();
                foreach (var audio in audioSources)
                {
                    if (audio == null) continue;

                    // Missing clip
                    if (audio.clip == null && !audio.playOnAwake)
                    {
                        issues.Add(new ValidationIssue
                        {
                            severity = "warning",
                            category = "audio",
                            game_object = go.name,
                            path = GetPath(go),
                            message = $"AudioSource on '{go.name}' has no AudioClip assigned",
                            fix_hint = "Assign an AudioClip or remove the AudioSource if not needed"
                        });
                    }

                    // Volume is 0
                    if (audio.volume == 0f)
                    {
                        issues.Add(new ValidationIssue
                        {
                            severity = "info",
                            category = "audio",
                            game_object = go.name,
                            path = GetPath(go),
                            message = $"AudioSource on '{go.name}' has volume = 0",
                            fix_hint = "Check if this is intentional or set volume > 0"
                        });
                    }

                    // Play on awake with no clip
                    if (audio.playOnAwake && audio.clip == null)
                    {
                        issues.Add(new ValidationIssue
                        {
                            severity = "error",
                            category = "audio",
                            game_object = go.name,
                            path = GetPath(go),
                            message = $"AudioSource on '{go.name}' has Play On Awake enabled but no AudioClip",
                            fix_hint = "Assign an AudioClip or disable Play On Awake"
                        });
                    }

                    if (issues.Count >= maxIssues) break;
                }

                if (issues.Count >= maxIssues) break;
            }

            return BuildIssueResponse("check_audio", issues, maxIssues, allObjects.Count);
        }

        private ToolResponse HandleValidateScene(JObject parameters)
        {
            // Run all checks and aggregate results
            var maxIssues = ToolHelpers.GetOptionalInt(parameters, "max_issues", 200);
            var includeInactive = ToolHelpers.GetOptionalBool(parameters, "include_inactive", true);

            var subParams = new JObject
            {
                ["include_inactive"] = includeInactive,
                ["max_issues"] = maxIssues / 5  // Divide budget across checks
            };

            var allIssues = new List<object>();
            var summary = new Dictionary<string, int>();

            void RunCheck(string checkName, Func<JObject, ToolResponse> handler)
            {
                try
                {
                    var result = handler(subParams);
                    if (result.Success && result.Data != null)
                    {
                        var issues = result.Data["issues"] as JArray;
                        if (issues != null)
                        {
                            foreach (var issue in issues)
                                allIssues.Add(issue);
                        }
                        var count = result.Data["issue_count"]?.Value<int>() ?? 0;
                        summary[checkName] = count;
                    }
                }
                catch (Exception ex)
                {
                    summary[checkName + "_error"] = -1;
                    UnityEngine.Debug.LogWarning($"[ValidationTool] {checkName} failed: {ex.Message}");
                }
            }

            RunCheck("missing_references", HandleCheckMissingReferences);
            RunCheck("duplicate_names", HandleCheckDuplicateNames);
            RunCheck("empty_gameobjects", HandleCheckEmptyGameObjects);
            RunCheck("missing_components", HandleCheckMissingComponents);
            RunCheck("layer_tags", HandleCheckLayerTags);
            RunCheck("prefab_integrity", HandleCheckPrefabIntegrity);
            RunCheck("audio", HandleCheckAudio);

            var totalIssues = allIssues.Count;
            var errorCount = allIssues.Count(i => (i as JObject)?["severity"]?.ToString() == "error");
            var warningCount = allIssues.Count(i => (i as JObject)?["severity"]?.ToString() == "warning");
            var infoCount = allIssues.Count(i => (i as JObject)?["severity"]?.ToString() == "info");

            var sceneName = SceneManager.GetActiveScene().name;

            return ToolResponse.OkWithData(new
            {
                scene = sceneName,
                total_issues = totalIssues,
                errors = errorCount,
                warnings = warningCount,
                info = infoCount,
                summary,
                issues = allIssues.Take(maxIssues).ToList()
            }, $"Scene '{sceneName}' validation: {errorCount} error(s), {warningCount} warning(s), {infoCount} info(s)");
        }

        private ToolResponse HandleValidateProject(JObject parameters)
        {
            var maxIssues = ToolHelpers.GetOptionalInt(parameters, "max_issues", 50);

            var issues = new List<ValidationIssue>();

            // Check build settings
            var buildScenes = EditorBuildSettings.scenes;
            if (buildScenes.Length == 0)
            {
                issues.Add(new ValidationIssue
                {
                    severity = "warning",
                    category = "build_settings",
                    game_object = "Build Settings",
                    path = "Project",
                    message = "No scenes in Build Settings",
                    fix_hint = "Add scenes via File > Build Settings > Add Open Scenes"
                });
            }
            else
            {
                var disabledScenes = buildScenes.Count(s => !s.enabled);
                if (disabledScenes > 0)
                {
                    issues.Add(new ValidationIssue
                    {
                        severity = "info",
                        category = "build_settings",
                        game_object = "Build Settings",
                        path = "Project",
                        message = $"{disabledScenes} scene(s) in Build Settings are disabled",
                        fix_hint = "Enable scenes in Build Settings if they should be included in the build"
                    });
                }

                // Check for missing scene files
                foreach (var scene in buildScenes)
                {
                    if (!System.IO.File.Exists(scene.path))
                    {
                        issues.Add(new ValidationIssue
                        {
                            severity = "error",
                            category = "build_settings",
                            game_object = scene.path,
                            path = "Project",
                            message = $"Scene file missing: '{scene.path}'",
                            fix_hint = "Remove the missing scene from Build Settings or restore the scene file"
                        });
                    }
                }
            }

            // Check for missing script assets
            var scriptGuids = AssetDatabase.FindAssets("t:MonoScript");
            var brokenScripts = 0;
            foreach (var guid in scriptGuids.Take(200))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                if (script != null && script.GetClass() == null && !path.Contains("Editor") &&
                    !path.Contains("Plugins") && path.EndsWith(".cs"))
                {
                    brokenScripts++;
                }
            }

            if (brokenScripts > 0)
            {
                issues.Add(new ValidationIssue
                {
                    severity = "warning",
                    category = "scripts",
                    game_object = "Scripts",
                    path = "Project",
                    message = $"{brokenScripts} script(s) may have compilation errors or missing class definitions",
                    fix_hint = "Check the Console for compilation errors and fix them"
                });
            }

            // Check PlayerSettings
            var productName = PlayerSettings.productName;
            if (string.IsNullOrEmpty(productName) || productName == "New Unity Project")
            {
                issues.Add(new ValidationIssue
                {
                    severity = "info",
                    category = "player_settings",
                    game_object = "Player Settings",
                    path = "Project",
                    message = $"Product name is '{productName}' — consider setting a proper name",
                    fix_hint = "Set product name in Edit > Project Settings > Player"
                });
            }

            // Check for default company name
            var companyName = PlayerSettings.companyName;
            if (string.IsNullOrEmpty(companyName) || companyName == "DefaultCompany")
            {
                issues.Add(new ValidationIssue
                {
                    severity = "info",
                    category = "player_settings",
                    game_object = "Player Settings",
                    path = "Project",
                    message = $"Company name is '{companyName}' — consider setting a proper name",
                    fix_hint = "Set company name in Edit > Project Settings > Player"
                });
            }

            return BuildIssueResponse("validate_project", issues, maxIssues, -1);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────

        private static List<GameObject> GetAllGameObjects(bool includeInactive)
        {
            var scene = SceneManager.GetActiveScene();
            var rootObjects = scene.GetRootGameObjects();
            var result = new List<GameObject>();

            foreach (var root in rootObjects)
                CollectGameObjects(root, result, includeInactive);

            return result;
        }

        private static void CollectGameObjects(GameObject go, List<GameObject> result, bool includeInactive)
        {
            if (!includeInactive && !go.activeInHierarchy) return;
            result.Add(go);
            for (int i = 0; i < go.transform.childCount; i++)
                CollectGameObjects(go.transform.GetChild(i).gameObject, result, includeInactive);
        }

        private static string GetPath(GameObject go)
        {
            var path = go.name;
            var parent = go.transform.parent;
            while (parent != null)
            {
                path = parent.gameObject.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }

        private static ToolResponse BuildIssueResponse(string action, List<ValidationIssue> issues, int maxIssues, int objectCount)
        {
            var errorCount = issues.Count(i => i.severity == "error");
            var warningCount = issues.Count(i => i.severity == "warning");
            var infoCount = issues.Count(i => i.severity == "info");

            var data = new
            {
                action,
                objects_checked = objectCount,
                issue_count = issues.Count,
                errors = errorCount,
                warnings = warningCount,
                info = infoCount,
                truncated = issues.Count >= maxIssues,
                issues = issues.Take(maxIssues).Select(i => i.ToDictionary()).ToList()
            };

            var msg = issues.Count == 0
                ? $"{action}: No issues found"
                : $"{action}: {errorCount} error(s), {warningCount} warning(s), {infoCount} info(s)";

            return ToolResponse.OkWithData(data, msg);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Data Types
        // ─────────────────────────────────────────────────────────────────────

        private class ValidationIssue
        {
            public string severity;    // "error", "warning", "info"
            public string category;
            public string game_object;
            public string path;
            public string component;
            public string field;
            public string message;
            public string fix_hint;

            public Dictionary<string, object> ToDictionary()
            {
                var d = new Dictionary<string, object>
                {
                    ["severity"] = severity,
                    ["category"] = category,
                    ["game_object"] = game_object,
                    ["path"] = path,
                    ["message"] = message,
                    ["fix_hint"] = fix_hint
                };
                if (!string.IsNullOrEmpty(component)) d["component"] = component;
                if (!string.IsNullOrEmpty(field)) d["field"] = field;
                return d;
            }
        }
    }
}
