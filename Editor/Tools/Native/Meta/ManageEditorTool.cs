using System;
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
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace AgentCore.Editor.Tools.Native.Meta
{
    /// <summary>
    /// Manage Unity Editor state, windows, play mode, and project settings.
    /// Use 'get_info' action to check editor status, connection, and project info (version, platform, scene, etc.).
    /// </summary>
    [AgentTool("manage_editor",
        Description = "Control Unity Editor state and access project-level settings. " +
            "Actions: get_info (editor version, platform, play state, active scene, render pipeline — use to verify environment), " +
            "play_mode (enter/exit/pause play mode), refresh (force asset reimport/recompile), " +
            "get_selection (returns rich structured selection: instance_ids, asset_guids, active_context, hierarchy paths — use to know what user selected), " +
            "set_selection (write selection; supports mode=replace|add|remove, and identifiers include hierarchy paths, instance IDs, asset paths, or 'guid:<assetGuid>' prefix), " +
            "set_selection_by_query (select by scene component type OR project asset filter — use for 'select all X' workflows), " +
            "focus_window (bring editor windows to front), " +
            "get_project_settings/set_project_setting (PlayerSettings, Physics, Quality, etc.). " +
            "Use get_info as a first step to confirm editor connectivity and project state. " +
            "Use refresh after script changes to trigger recompilation. " +
            "NOT for: scene object operations (use manage_gameobject), build pipeline (use manage_build).",
        Category = "Meta",
        RequiresMainThread = true,
        RiskLevel = ToolRiskLevel.Medium,
        Capabilities = ToolCapability.ModifyProjectSettings,
        ReadOnlyActions = new[] { "get_info", "get_selection", "get_project_settings" },
        // v1.13+ 白名单反转补漏: play_mode 只是编辑器状态机切换 (play/pause/stop/step),
        // 不触达 ProjectSettings 磁盘文件、不触发 Domain Reload、不落盘 —— 与本工具声明的
        // ModifyProjectSettings 硬禁止能力位语义完全无关，之前因白名单检查排在能力位检查之后
        // 被误伤整体封死 (真实案例: play_mode:stop 是退出 Play Mode 的唯一工具路径都被拦，
        // 只能绕行 execute_code 调 EditorApplication.isPlaying=false)。set_project_setting/
        // refresh/set_selection 等真正写 ProjectSettings 或触发 reimport 的 action 不在此列，
        // 仍受硬禁止能力位拦截。
        PlaymodeRuntimeSafeActions = new[] { "play_mode" })]
    public class ManageEditorTool : IAgentTool
    {
        #region Schema

        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [""get_info"", ""play_mode"", ""focus_window"", ""get_selection"", ""set_selection"", ""set_selection_by_query"", ""refresh"", ""get_project_settings"", ""set_project_setting""],
                    ""description"": ""Action to perform. Use 'get_info' to check editor status and connection (returns Unity version, platform, play state, active scene, render pipeline). Use 'play_mode' with 'state' param to control play/pause/stop/step. Use 'refresh' to reimport assets. Use 'get_selection' to inspect current selection (rich fields: instance_ids, asset_guids, active_context, game_objects). Use 'set_selection' to write; use 'set_selection_by_query' for 'select all X with component/label/type' workflows.""
                },
                ""state"": {
                    ""type"": ""string"",
                    ""enum"": [""play"", ""pause"", ""stop"", ""step""],
                    ""description"": ""Play mode state (for play_mode action)""
                },
                ""window"": {
                    ""type"": ""string"",
                    ""enum"": [""scene"", ""game"", ""inspector"", ""hierarchy"", ""project"", ""console""],
                    ""description"": ""Window to focus (for focus_window action)""
                },
                ""target"": {
                    ""description"": ""Target(s) for set_selection. Each target may be: a GameObject name (selects ALL objects with that name), a hierarchy path like 'Player/Camera' (selects one exact object), an integer InstanceID, an asset path (e.g. 'Assets/Prefabs/Enemy.prefab'), or 'guid:<assetGuid>' (32-char hex — useful when path is unstable). Pass a single string or an array of strings for multi-select."",
                    ""oneOf"": [
                        { ""type"": ""string"" },
                        { ""type"": ""array"", ""items"": { ""type"": ""string"" } }
                    ]
                },
                ""mode"": {
                    ""type"": ""string"",
                    ""enum"": [""replace"", ""add"", ""remove""],
                    ""description"": ""Selection write mode for set_selection (default: replace). 'add' merges into current Selection.objects; 'remove' subtracts resolved objects from current selection.""
                },
                ""active_context"": {
                    ""type"": ""string"",
                    ""description"": ""Optional context object for set_selection (asset path or hierarchy path). Sets Selection.SetActiveObjectWithContext so Project window can scope to a folder or scene root.""
                },
                ""query"": {
                    ""type"": ""object"",
                    ""description"": ""Query object for set_selection_by_query. Must contain scope='scene' (with component_type) OR scope='project' (with asset_filter)."",
                    ""properties"": {
                        ""scope"": {
                            ""type"": ""string"",
                            ""enum"": [""scene"", ""project""],
                            ""description"": ""'scene' iterates loaded scenes; 'project' uses AssetDatabase.FindAssets syntax.""
                        },
                        ""component_type"": {
                            ""type"": ""string"",
                            ""description"": ""(scene) Full component type name (e.g. 'UnityEngine.Rigidbody' or 'Rigidbody'). Selects all GameObjects that have this component.""
                        },
                        ""asset_filter"": {
                            ""type"": ""string"",
                            ""description"": ""(project) AssetDatabase.FindAssets filter, e.g. 't:Prefab l:MyLabel' or 't:Material'.""
                        },
                        ""include_inactive"": {
                            ""type"": ""boolean"",
                            ""description"": ""(scene, default true) Include inactive GameObjects.""
                        },
                        ""search_folders"": {
                            ""type"": ""array"",
                            ""items"": { ""type"": ""string"" },
                            ""description"": ""(project, optional) Limit search to these folders, e.g. ['Assets/Prefabs'].""
                        },
                        ""max_results"": {
                            ""type"": ""integer"",
                            ""description"": ""Safety cap on selection size (default 500). Set higher if you really need to select thousands.""
                        }
                    }
                },
                ""import_mode"": {
                    ""type"": ""string"",
                    ""enum"": [""default"", ""force""],
                    ""description"": ""Import mode for refresh action (default: default)""
                },
                ""category"": {
                    ""type"": ""string"",
                    ""enum"": [""player"", ""quality"", ""physics"", ""time"", ""audio"", ""all""],
                    ""description"": ""Settings category (for get_project_settings / set_project_setting)""
                },
                ""property"": {
                    ""type"": ""string"",
                    ""description"": ""Property name to set (for set_project_setting)""
                },
                ""value"": {
                    ""type"": ""string"",
                    ""description"": ""Value to set (for set_project_setting)""
                }
            },
            ""required"": [""action""]
        }");

        #endregion

        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_editor",
            description: "Manage Unity Editor state and project settings. Use 'get_info' to check editor status/connection (returns Unity version, platform, play state, active scene, render pipeline, etc.). Other actions: play_mode, focus_window, get_selection, set_selection, refresh, get_project_settings, set_project_setting",
            category: "meta",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        public Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            ToolResponse response;

            try
            {
                var action = ToolHelpers.GetRequiredString(parameters, "action").ToLowerInvariant();

                switch (action)
                {
                    case "get_info":
                        response = HandleGetInfo();
                        break;
                    case "play_mode":
                        response = HandlePlayMode(parameters);
                        break;
                    case "focus_window":
                        response = HandleFocusWindow(parameters);
                        break;
                    case "get_selection":
                        response = HandleGetSelection();
                        break;
                    case "set_selection":
                        response = HandleSetSelection(parameters);
                        break;
                    case "set_selection_by_query":
                        response = HandleSetSelectionByQuery(parameters);
                        break;
                    case "refresh":
                        response = HandleRefresh(parameters);
                        break;
                    case "get_project_settings":
                        response = HandleGetProjectSettings(parameters);
                        break;
                    case "set_project_setting":
                        response = HandleSetProjectSetting(parameters);
                        break;
                    default:
                        response = ToolResponse.Fail(
                            $"Unknown action: '{action}'. Valid actions: get_info, play_mode, focus_window, get_selection, set_selection, set_selection_by_query, refresh, get_project_settings, set_project_setting");
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

        private ToolResponse HandleGetInfo()
        {
            var activeScene = SceneManager.GetActiveScene();

            // Detect render pipeline
            string renderPipeline = "Built-in";
            var currentRP = GraphicsSettings.currentRenderPipeline;
            if (currentRP != null)
            {
                var rpName = currentRP.GetType().Name;
                if (rpName.Contains("Universal") || rpName.Contains("URP"))
                    renderPipeline = "URP";
                else if (rpName.Contains("HD") || rpName.Contains("HDRP"))
                    renderPipeline = "HDRP";
                else
                    renderPipeline = rpName;
            }

            var data = new JObject
            {
                ["unity_version"] = Application.unityVersion,
                ["project_path"] = Application.dataPath.Replace("/Assets", ""),
                ["project_name"] = Application.productName,
                ["platform"] = EditorUserBuildSettings.activeBuildTarget.ToString(),
                ["is_playing"] = EditorApplication.isPlaying,
                ["is_paused"] = EditorApplication.isPaused,
                ["is_compiling"] = EditorApplication.isCompiling,
                ["active_scene"] = new JObject
                {
                    ["name"] = activeScene.name,
                    ["path"] = activeScene.path,
                    ["is_dirty"] = activeScene.isDirty,
                    ["root_count"] = activeScene.rootCount
                },
                ["render_pipeline"] = renderPipeline,
                ["scripting_backend"] = PlayerSettings.GetScriptingBackend(EditorUserBuildSettings.selectedBuildTargetGroup).ToString(),
                ["color_space"] = PlayerSettings.colorSpace.ToString(),
                ["loaded_scene_count"] = SceneManager.sceneCount
            };

            return ToolResponse.OkWithData(data, "Editor info retrieved successfully.");
        }

        private ToolResponse HandlePlayMode(JObject parameters)
        {
            var state = ToolHelpers.GetRequiredString(parameters, "state").ToLowerInvariant();

            switch (state)
            {
                case "play":
                    if (EditorApplication.isPlaying)
                        return ToolResponse.Ok("Editor is already in play mode.");
                    EditorApplication.isPlaying = true;
                    return ToolResponse.Ok("Entering play mode.");

                case "pause":
                    if (!EditorApplication.isPlaying)
                        return ToolResponse.Fail("Cannot pause: Editor is not in play mode.");
                    EditorApplication.isPaused = !EditorApplication.isPaused;
                    return ToolResponse.Ok(EditorApplication.isPaused
                        ? "Editor paused."
                        : "Editor unpaused.");

                case "stop":
                    if (!EditorApplication.isPlaying)
                        return ToolResponse.Ok("Editor is already stopped.");
                    EditorApplication.isPlaying = false;
                    return ToolResponse.Ok("Stopping play mode.");

                case "step":
                    if (!EditorApplication.isPlaying)
                        return ToolResponse.Fail("Cannot step: Editor is not in play mode.");
                    EditorApplication.Step();
                    return ToolResponse.Ok("Stepped one frame.");

                default:
                    return ToolResponse.Fail(
                        $"Unknown state: '{state}'. Valid states: play, pause, stop, step");
            }
        }

        private ToolResponse HandleFocusWindow(JObject parameters)
        {
            var window = ToolHelpers.GetRequiredString(parameters, "window").ToLowerInvariant();

            Type windowType = null;
            string windowName = window;

            switch (window)
            {
                case "scene":
                    windowType = typeof(SceneView);
                    windowName = "Scene View";
                    break;
                case "game":
                    windowType = Type.GetType("UnityEditor.GameView,UnityEditor");
                    windowName = "Game View";
                    break;
                case "inspector":
                    windowType = Type.GetType("UnityEditor.InspectorWindow,UnityEditor");
                    windowName = "Inspector";
                    break;
                case "hierarchy":
                    windowType = Type.GetType("UnityEditor.SceneHierarchyWindow,UnityEditor");
                    windowName = "Hierarchy";
                    break;
                case "project":
                    windowType = Type.GetType("UnityEditor.ProjectBrowser,UnityEditor");
                    windowName = "Project";
                    break;
                case "console":
                    windowType = Type.GetType("UnityEditor.ConsoleWindow,UnityEditor");
                    windowName = "Console";
                    break;
                default:
                    return ToolResponse.Fail(
                        $"Unknown window: '{window}'. Valid windows: scene, game, inspector, hierarchy, project, console");
            }

            if (windowType == null)
            {
                return ToolResponse.Fail($"Could not resolve window type for '{window}'.");
            }

            var editorWindow = EditorWindow.GetWindow(windowType);
            if (editorWindow != null)
            {
                editorWindow.Focus();
                return ToolResponse.Ok($"Focused {windowName} window.");
            }

            return ToolResponse.Fail($"Could not open or focus {windowName} window.");
        }

        // ─── G06 Selection deep dive (v1.9.3) ────────────────────────────────
        // Structured selection info used by both get_selection and set_selection results.
        // 保证 gameObject 走 hierarchy_path, asset 走 asset_path + asset_guid.
        //
        // Deprecation timeline (per plans/v1.10.0-handoff.md §6.6):
        //   v1.9.3 → v1.10.x: 新旧字段并存 (get_selection 保留 active_gameobject / active_object / selected_objects / selection_count)
        //   v1.11.0: CHANGELOG mark old fields as Deprecated
        //   v1.12.0 or next major: remove old fields
        private static JObject SerializeSelectedObject(UnityEngine.Object obj)
        {
            if (obj == null) return null;
            var item = new JObject
            {
                ["name"] = obj.name,
                ["type"] = obj.GetType().Name,
                ["instance_id"] = obj.GetInstanceID()
            };

            if (obj is GameObject go)
            {
                item["is_gameobject"] = true;
                item["hierarchy_path"] = BuildHierarchyPath(go);
                if (go.scene.IsValid())
                {
                    item["scene_name"] = go.scene.name;
                    item["scene_path"] = go.scene.path;
                }
            }

            var assetPath = AssetDatabase.GetAssetPath(obj);
            if (!string.IsNullOrEmpty(assetPath))
            {
                item["asset_path"] = assetPath;
                var guid = AssetDatabase.AssetPathToGUID(assetPath);
                if (!string.IsNullOrEmpty(guid))
                    item["asset_guid"] = guid;
            }
            return item;
        }

        /// <summary>Build "/Root/Child/Leaf" style hierarchy path for a scene GameObject.</summary>
        private static string BuildHierarchyPath(GameObject go)
        {
            if (go == null) return null;
            var stack = new System.Collections.Generic.Stack<string>();
            var current = go.transform;
            while (current != null)
            {
                stack.Push(current.name);
                current = current.parent;
            }
            return string.Join("/", stack);
        }

        private ToolResponse HandleGetSelection()
        {
            var data = new JObject();

            // === Legacy fields (deprecation target: v1.12.0) ===
            var activeGo = Selection.activeGameObject;
            data["active_gameobject"] = activeGo != null
                ? ToolHelpers.SerializeGameObject(activeGo, includeComponents: true)
                : null;

            var activeObj = Selection.activeObject;
            if (activeObj != null && activeObj != activeGo)
            {
                data["active_object"] = SerializeSelectedObject(activeObj);
            }
            else
            {
                data["active_object"] = null;
            }

            var selectedObjects = Selection.objects ?? System.Array.Empty<UnityEngine.Object>();
            var selectedArray = new JArray();
            foreach (var obj in selectedObjects)
            {
                if (obj == null) continue;
                selectedArray.Add(SerializeSelectedObject(obj));
            }
            data["selected_objects"] = selectedArray;
            data["selection_count"] = selectedObjects.Length;

            // === New enriched fields (v1.9.3, G06) ===
            // active
            data["active"] = SerializeSelectedObject(activeObj);
            data["active_hierarchy_path"] = activeGo != null ? BuildHierarchyPath(activeGo) : null;

            // active_context (Project window folder / scene root)
            var activeContext = Selection.activeContext;
            data["active_context"] = SerializeSelectedObject(activeContext);

            // Structured lists
            var instanceIds = new JArray();
            var assetGuids = new JArray();
            var goArray = new JArray();
            var seenGuids = new System.Collections.Generic.HashSet<string>();

            foreach (var obj in selectedObjects)
            {
                if (obj == null) continue;
                instanceIds.Add(obj.GetInstanceID());
                if (obj is GameObject go)
                {
                    goArray.Add(SerializeSelectedObject(go));
                }
                var assetPath = AssetDatabase.GetAssetPath(obj);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    var guid = AssetDatabase.AssetPathToGUID(assetPath);
                    if (!string.IsNullOrEmpty(guid) && seenGuids.Add(guid))
                        assetGuids.Add(guid);
                }
            }

            data["instance_ids"] = instanceIds;
            data["asset_guids"] = assetGuids;
            data["game_objects"] = goArray;
            data["select_count"] = Selection.count;

            return ToolResponse.OkWithData(data,
                selectedObjects.Length > 0
                    ? $"Current selection: {selectedObjects.Length} object(s)."
                    : "No objects selected.");
        }

        /// <summary>构造单个 GameObject 的选择信息 JObject（供 set_selection 结果复用）。</summary>
        private static JObject GameObjectInfo(GameObject go)
        {
            return SerializeSelectedObject(go);
        }

        private ToolResponse HandleSetSelection(JObject parameters)
        {
            // v1.7.16：支持单选与多选。target 可为单个字符串，或字符串数组（多选）。
            // v1.9.3 (G06): 新增 mode (replace/add/remove) + active_context + 'guid:<assetGuid>' 前缀支持。
            var token = parameters["target"];
            if (token == null || token.Type == JTokenType.Null)
            {
                return ToolResponse.Fail("Missing required parameter: 'target' (string or array of strings).");
            }

            var targets = NormalizeTargetTokens(token);
            if (targets.Count == 0)
            {
                return ToolResponse.Fail("Parameter 'target' contained no valid entries.");
            }

            var mode = ToolHelpers.GetOptionalString(parameters, "mode", "replace").ToLowerInvariant();
            if (mode != "replace" && mode != "add" && mode != "remove")
            {
                return ToolResponse.Fail($"Invalid 'mode': '{mode}'. Valid: replace, add, remove.");
            }

            var resolved = new System.Collections.Generic.List<UnityEngine.Object>();
            var resolvedInfo = new JArray();
            var notFound = new System.Collections.Generic.List<string>();

            foreach (var target in targets)
            {
                if (TryResolveTarget(target, resolved, resolvedInfo)) continue;
                notFound.Add(target);
            }

            if (resolved.Count == 0)
            {
                return ToolResponse.Fail(
                    $"Could not find any GameObject or asset for target(s): {string.Join(", ", notFound)}");
            }

            // Compute new Selection.objects based on mode
            var currentSelection = Selection.objects ?? System.Array.Empty<UnityEngine.Object>();
            UnityEngine.Object[] finalObjects;
            switch (mode)
            {
                case "add":
                {
                    var set = new System.Collections.Generic.List<UnityEngine.Object>(currentSelection);
                    var seenIds = new System.Collections.Generic.HashSet<int>();
                    foreach (var o in currentSelection) if (o != null) seenIds.Add(o.GetInstanceID());
                    foreach (var o in resolved)
                    {
                        if (o != null && seenIds.Add(o.GetInstanceID()))
                            set.Add(o);
                    }
                    finalObjects = set.ToArray();
                    break;
                }
                case "remove":
                {
                    var removeIds = new System.Collections.Generic.HashSet<int>();
                    foreach (var o in resolved) if (o != null) removeIds.Add(o.GetInstanceID());
                    var kept = new System.Collections.Generic.List<UnityEngine.Object>();
                    foreach (var o in currentSelection)
                    {
                        if (o != null && !removeIds.Contains(o.GetInstanceID()))
                            kept.Add(o);
                    }
                    finalObjects = kept.ToArray();
                    break;
                }
                default: // replace
                    finalObjects = resolved.ToArray();
                    break;
            }

            // Optional active_context
            UnityEngine.Object contextObj = null;
            var contextToken = parameters["active_context"];
            if (contextToken != null && contextToken.Type != JTokenType.Null)
            {
                var contextStr = contextToken.ToString().Trim();
                if (!string.IsNullOrEmpty(contextStr))
                {
                    var scratch = new System.Collections.Generic.List<UnityEngine.Object>();
                    if (TryResolveTarget(contextStr, scratch, null) && scratch.Count > 0)
                    {
                        contextObj = scratch[0];
                    }
                    else
                    {
                        return ToolResponse.Fail(
                            $"active_context '{contextStr}' could not be resolved to a GameObject or asset.");
                    }
                }
            }

            // Apply selection
            if (finalObjects.Length == 0)
            {
                Selection.objects = System.Array.Empty<UnityEngine.Object>();
            }
            else if (contextObj != null)
            {
                Selection.objects = finalObjects;
                Selection.SetActiveObjectWithContext(finalObjects[0], contextObj);
            }
            else if (finalObjects.Length == 1)
            {
                Selection.activeObject = finalObjects[0];
            }
            else
            {
                Selection.objects = finalObjects;
            }

            if (finalObjects.Length > 0)
            {
                EditorGUIUtility.PingObject(finalObjects[0]);
            }

            var data = new JObject
            {
                ["mode"] = mode,
                ["selected_count"] = resolved.Count,       // legacy
                ["selected"] = resolvedInfo,               // legacy
                ["resolved_count"] = resolved.Count,
                ["resolved"] = resolvedInfo,
                ["selection_count_after"] = finalObjects.Length
            };
            if (notFound.Count > 0)
            {
                data["not_found"] = new JArray(notFound);
            }
            if (contextObj != null)
            {
                data["active_context_resolved"] = SerializeSelectedObject(contextObj);
            }

            string summary;
            if (mode == "add")
            {
                summary = $"Added {resolved.Count} object(s) to selection (total: {finalObjects.Length}).";
            }
            else if (mode == "remove")
            {
                summary = $"Removed {resolved.Count} object(s) from selection (remaining: {finalObjects.Length}).";
            }
            else if (finalObjects.Length == 1)
            {
                summary = $"Selected: '{((resolvedInfo[0] as JObject)?["name"])}'";
            }
            else
            {
                summary = $"Selected {finalObjects.Length} object(s).";
            }
            if (notFound.Count > 0)
            {
                summary += $" ({notFound.Count} not found: {string.Join(", ", notFound)})";
            }

            return ToolResponse.OkWithData(data, summary);
        }

        /// <summary>Normalize the 'target' JSON token into a flat list of trimmed target strings.</summary>
        private static System.Collections.Generic.List<string> NormalizeTargetTokens(JToken token)
        {
            var targets = new System.Collections.Generic.List<string>();
            if (token.Type == JTokenType.Array)
            {
                foreach (var item in (JArray)token)
                {
                    var s = item?.ToString();
                    if (!string.IsNullOrWhiteSpace(s)) targets.Add(s.Trim());
                }
                return targets;
            }

            var raw = token.ToString().Trim();
            // 兼容 LLM 把数组序列化成字符串传入的情况，例如 target = "[\"a\", \"b\"]"。
            if (raw.StartsWith("[") && raw.EndsWith("]"))
            {
                JArray arr = null;
                try { arr = JArray.Parse(raw); } catch { arr = null; }
                if (arr != null)
                {
                    foreach (var item in arr)
                    {
                        var e = item?.ToString();
                        if (!string.IsNullOrWhiteSpace(e)) targets.Add(e.Trim());
                    }
                    if (targets.Count > 0) return targets;
                }
            }

            // 未识别为 JSON 数组：再兼容逗号分隔的多目标写法 "a, b"。
            if (raw.IndexOf(',') >= 0)
            {
                foreach (var part in raw.Split(','))
                {
                    var e = part.Trim();
                    if (!string.IsNullOrWhiteSpace(e)) targets.Add(e);
                }
            }
            else if (!string.IsNullOrWhiteSpace(raw))
            {
                targets.Add(raw);
            }
            return targets;
        }

        /// <summary>
        /// Resolve a single target string to zero or more UnityEngine.Objects and append them
        /// to `resolved` + `resolvedInfo` (if non-null). Returns true if at least one object was found.
        /// Supported forms:
        ///   - "guid:32-char-hex" → asset by GUID (v1.9.3)
        ///   - "Path/With/Slashes" → scene GameObject by hierarchy path
        ///   - integer → InstanceID (may resolve to asset or GameObject)
        ///   - "Name" → all scene GameObjects with that name
        ///   - "Assets/..." or similar asset path fallback
        /// </summary>
        private static bool TryResolveTarget(
            string target,
            System.Collections.Generic.List<UnityEngine.Object> resolved,
            JArray resolvedInfo)
        {
            if (string.IsNullOrEmpty(target)) return false;

            // ─── "guid:xxxxxxxx" prefix (v1.9.3) — resolve asset by GUID ───
            if (target.StartsWith("guid:", StringComparison.OrdinalIgnoreCase))
            {
                var guid = target.Substring(5).Trim();
                if (string.IsNullOrEmpty(guid)) return false;
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(assetPath)) return false;
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                if (asset == null) return false;
                resolved.Add(asset);
                resolvedInfo?.Add(SerializeSelectedObject(asset));
                return true;
            }

            // ─── Hierarchy path ───
            if (target.IndexOf('/') >= 0)
            {
                // Ambiguity: it could be a scene hierarchy path OR an asset path.
                // Prefer scene lookup first (matches v1.7.16 semantics), fall back to asset.
                var go = ToolHelpers.FindGameObject(target);
                if (go != null)
                {
                    resolved.Add(go);
                    resolvedInfo?.Add(SerializeSelectedObject(go));
                    return true;
                }

                var pathAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(target);
                if (pathAsset != null)
                {
                    resolved.Add(pathAsset);
                    resolvedInfo?.Add(SerializeSelectedObject(pathAsset));
                    return true;
                }
                return false;
            }

            // ─── Pure integer → InstanceID ───
            if (int.TryParse(target, out var instanceId))
            {
                var obj = EditorUtility.InstanceIDToObject(instanceId);
                if (obj != null)
                {
                    resolved.Add(obj);
                    resolvedInfo?.Add(SerializeSelectedObject(obj));
                    return true;
                }
                // Fallthrough to name lookup (rare: an object literally named e.g. "123")
                var byName = ToolHelpers.FindGameObjectsByName(target);
                foreach (var g in byName)
                {
                    resolved.Add(g);
                    resolvedInfo?.Add(SerializeSelectedObject(g));
                }
                return byName.Count > 0;
            }

            // ─── Plain name → all scene GameObjects with that name ───
            var byNameList = ToolHelpers.FindGameObjectsByName(target);
            if (byNameList.Count > 0)
            {
                foreach (var g in byNameList)
                {
                    resolved.Add(g);
                    resolvedInfo?.Add(SerializeSelectedObject(g));
                }
                return true;
            }

            // ─── Asset path fallback ───
            var asset2 = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(target);
            if (asset2 != null)
            {
                resolved.Add(asset2);
                resolvedInfo?.Add(SerializeSelectedObject(asset2));
                return true;
            }

            return false;
        }

        /// <summary>
        /// v1.9.3 (G06): New action `set_selection_by_query`.
        /// Selects scene GameObjects by component type OR project assets by AssetDatabase filter.
        /// </summary>
        private ToolResponse HandleSetSelectionByQuery(JObject parameters)
        {
            var queryToken = parameters["query"];
            if (queryToken == null || queryToken.Type != JTokenType.Object)
            {
                return ToolResponse.Fail("Missing required parameter: 'query' (object with 'scope' and one of component_type / asset_filter).");
            }
            var query = (JObject)queryToken;
            var scope = ToolHelpers.GetRequiredString(query, "scope").ToLowerInvariant();
            var maxResults = ToolHelpers.GetOptionalInt(query, "max_results", 500);
            if (maxResults <= 0) maxResults = 500;

            var resolved = new System.Collections.Generic.List<UnityEngine.Object>();
            // Bug B (v1.11+): total_matches 记录 filter 匹配全量, 而 resolved.Count 受 max_results 上限限制。
            // truncated=true 时 total_matches - resolved.Count 就是被截掉的候选数, 让 agent 知道规模。
            int totalMatches = 0;
            string queryDescription;

            switch (scope)
            {
                case "scene":
                {
                    var componentTypeName = ToolHelpers.GetOptionalString(query, "component_type");
                    if (string.IsNullOrEmpty(componentTypeName))
                    {
                        return ToolResponse.Fail("scope='scene' requires 'component_type' (e.g. 'UnityEngine.Rigidbody' or short name 'Rigidbody').");
                    }
                    var includeInactive = ToolHelpers.GetOptionalBool(query, "include_inactive", true);

                    var componentType = ResolveComponentType(componentTypeName);
                    if (componentType == null)
                    {
                        return ToolResponse.Fail(
                            $"Could not resolve component type '{componentTypeName}'. Try a fully-qualified name like 'UnityEngine.Rigidbody'.");
                    }
                    if (!typeof(Component).IsAssignableFrom(componentType))
                    {
                        return ToolResponse.Fail(
                            $"Type '{componentType.FullName}' does not derive from UnityEngine.Component; cannot query scene by it.");
                    }

                    // FindObjectsOfType(bool) — includeInactive true means includeInactive+includeUninitialized
                    var components = UnityEngine.Object.FindObjectsOfType(componentType, includeInactive);
                    totalMatches = components.Length;
                    foreach (var comp in components)
                    {
                        if (comp is Component c && c.gameObject != null)
                        {
                            resolved.Add(c.gameObject);
                            if (resolved.Count >= maxResults) break;
                        }
                    }
                    queryDescription = $"scene component_type='{componentType.FullName}' include_inactive={includeInactive}";
                    break;
                }

                case "project":
                {
                    var assetFilter = ToolHelpers.GetOptionalString(query, "asset_filter");
                    if (string.IsNullOrEmpty(assetFilter))
                    {
                        return ToolResponse.Fail("scope='project' requires 'asset_filter' (AssetDatabase.FindAssets syntax, e.g. 't:Prefab l:MyLabel').");
                    }

                    string[] searchFolders = null;
                    var foldersToken = query["search_folders"];
                    if (foldersToken != null && foldersToken.Type == JTokenType.Array)
                    {
                        var list = new System.Collections.Generic.List<string>();
                        foreach (var f in (JArray)foldersToken)
                        {
                            var s = f?.ToString();
                            if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
                        }
                        if (list.Count > 0) searchFolders = list.ToArray();
                    }

                    string[] guids = searchFolders != null
                        ? AssetDatabase.FindAssets(assetFilter, searchFolders)
                        : AssetDatabase.FindAssets(assetFilter);
                    totalMatches = guids.Length;

                    // Bug D (v1.11+): AssetDatabase.FindAssets("t:Material") 会命中 .shadergraph 等
                    // 复合 asset 里的 Material sub-asset, 但 LoadAssetAtPath 加载的是主 asset (Shader),
                    // 造成语义泄漏 (agent 拿到 Shader 却以为是 Material).
                    // 策略: 从 assetFilter 里解析出 t:<Type> token, 加载主 asset 后按 GetType().Name 二次过滤。
                    // 命中 sub-asset 的复合 asset 会在此处被丢弃 — 保守但语义准确 (推荐"方案 A严格").
                    var typeFilters = ExtractTypeFilters(assetFilter);

                    foreach (var guid in guids)
                    {
                        var path = AssetDatabase.GUIDToAssetPath(guid);
                        if (string.IsNullOrEmpty(path)) continue;
                        var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                        if (asset == null) continue;

                        // Bug D: 类型二次过滤
                        if (typeFilters.Count > 0 && !MatchesAnyType(asset, typeFilters))
                            continue;

                        resolved.Add(asset);
                        if (resolved.Count >= maxResults) break;
                    }
                    queryDescription = $"project asset_filter='{assetFilter}'"
                        + (searchFolders != null ? $" folders=[{string.Join(",", searchFolders)}]" : "");
                    break;
                }

                default:
                    return ToolResponse.Fail($"Invalid query.scope: '{scope}'. Valid: scene, project.");
            }

            var resolvedInfo = new JArray();
            foreach (var o in resolved)
            {
                if (o != null) resolvedInfo.Add(SerializeSelectedObject(o));
            }

            // Write selection
            if (resolved.Count == 0)
            {
                Selection.objects = System.Array.Empty<UnityEngine.Object>();
            }
            else if (resolved.Count == 1)
            {
                Selection.activeObject = resolved[0];
            }
            else
            {
                Selection.objects = resolved.ToArray();
            }
            if (resolved.Count > 0)
            {
                EditorGUIUtility.PingObject(resolved[0]);
            }

            var data = new JObject
            {
                ["query"] = queryDescription,
                ["resolved_count"] = resolved.Count,
                ["resolved"] = resolvedInfo,
                ["max_results"] = maxResults,
                ["total_matches"] = totalMatches,
                ["truncated"] = resolved.Count >= maxResults
            };

            var summary = $"set_selection_by_query ({queryDescription}) selected {resolved.Count} object(s)"
                + (resolved.Count >= maxResults ? $" — truncated at max_results={maxResults}" : ".");
            return ToolResponse.OkWithData(data, summary);
        }

        /// <summary>
        /// Bug D (v1.11+): 从 AssetDatabase filter 字符串里提取所有 t:&lt;Type&gt; token。
        /// 用于对 <c>FindAssets(assetFilter)</c> 结果做类型二次过滤 — 因为 FindAssets 会命中
        /// sub-asset (如 .shadergraph 里的 Material sub-asset), 但 LoadAssetAtPath 加载主 asset,
        /// 造成语义泄漏 (返回 Shader 却说是 Material).
        /// 例: "t:Material l:MyLabel" → { "Material" }; "t:Prefab t:Mesh" → { "Prefab", "Mesh" }.
        /// </summary>
        private static System.Collections.Generic.List<string> ExtractTypeFilters(string assetFilter)
        {
            var result = new System.Collections.Generic.List<string>();
            if (string.IsNullOrWhiteSpace(assetFilter)) return result;
            var tokens = assetFilter.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var tok in tokens)
            {
                if (tok.StartsWith("t:", StringComparison.OrdinalIgnoreCase) && tok.Length > 2)
                {
                    var typeName = tok.Substring(2);
                    if (!string.IsNullOrWhiteSpace(typeName))
                        result.Add(typeName.Trim());
                }
            }
            return result;
        }

        /// <summary>
        /// Bug D (v1.11+): 检查 asset 是否命中任一 type filter (短名或全名匹配, 大小写不敏感)。
        /// 匹配语义: <c>asset.GetType().Name</c> == filter (短名), 或 <c>FullName</c> == filter (全名).
        /// 还检查基类链, 使得 <c>t:Texture</c> 能匹配 Texture2D / Texture3D 等派生类型 —
        /// 对齐 Unity Asset Search 的 <c>t:</c> 类型层级语义。
        /// </summary>
        private static bool MatchesAnyType(UnityEngine.Object asset, System.Collections.Generic.List<string> typeFilters)
        {
            if (asset == null || typeFilters == null || typeFilters.Count == 0) return false;
            var t = asset.GetType();
            while (t != null && t != typeof(object))
            {
                foreach (var filter in typeFilters)
                {
                    if (string.Equals(t.Name, filter, StringComparison.OrdinalIgnoreCase)) return true;
                    if (string.Equals(t.FullName, filter, StringComparison.OrdinalIgnoreCase)) return true;
                }
                t = t.BaseType;
            }
            return false;
        }

        /// <summary>Resolve a Component type by full name (e.g. 'UnityEngine.Rigidbody') or short name ('Rigidbody').</summary>
        private static Type ResolveComponentType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;

            // 1) Exact type name across all loaded assemblies (fully qualified wins first).
            var t = Type.GetType(typeName, throwOnError: false, ignoreCase: false);
            if (t != null && typeof(Component).IsAssignableFrom(t)) return t;

            // 2) Try common Unity assemblies with short name.
            foreach (var asmName in new[] { "UnityEngine", "UnityEngine.CoreModule", "UnityEngine.PhysicsModule",
                                            "UnityEngine.UI", "UnityEngine.AnimationModule" })
            {
                var candidate = Type.GetType($"UnityEngine.{typeName}, {asmName}", throwOnError: false);
                if (candidate != null && typeof(Component).IsAssignableFrom(candidate)) return candidate;
            }

            // 3) Slow fallback: enumerate all loaded assemblies.
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (System.Reflection.ReflectionTypeLoadException ex) { types = ex.Types; }
                foreach (var candidate in types)
                {
                    if (candidate == null) continue;
                    if (!typeof(Component).IsAssignableFrom(candidate)) continue;
                    if (candidate.FullName == typeName || candidate.Name == typeName)
                        return candidate;
                }
            }
            return null;
        }

        private ToolResponse HandleRefresh(JObject parameters)
        {
            var importMode = ToolHelpers.GetOptionalString(parameters, "import_mode", "default").ToLowerInvariant();

            if (importMode == "force")
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            }
            else
            {
                AssetDatabase.Refresh();
            }

            return ToolResponse.Ok($"Asset database refreshed (mode: {importMode}).");
        }

        private ToolResponse HandleGetProjectSettings(JObject parameters)
        {
            var category = ToolHelpers.GetOptionalString(parameters, "category", "all").ToLowerInvariant();
            var data = new JObject();

            if (category == "all" || category == "player")
            {
                data["player"] = new JObject
                {
                    ["company_name"] = PlayerSettings.companyName,
                    ["product_name"] = PlayerSettings.productName,
                    ["version"] = PlayerSettings.bundleVersion,
                    ["default_is_fullscreen"] = PlayerSettings.fullScreenMode.ToString(),
                    ["run_in_background"] = PlayerSettings.runInBackground,
                    ["scripting_backend"] = PlayerSettings.GetScriptingBackend(
                        EditorUserBuildSettings.selectedBuildTargetGroup).ToString(),
                    ["api_compatibility"] = PlayerSettings.GetApiCompatibilityLevel(
                        EditorUserBuildSettings.selectedBuildTargetGroup).ToString(),
                    ["color_space"] = PlayerSettings.colorSpace.ToString()
                };
            }

            if (category == "all" || category == "quality")
            {
                data["quality"] = new JObject
                {
                    ["current_level"] = QualitySettings.names[QualitySettings.GetQualityLevel()],
                    ["quality_level_index"] = QualitySettings.GetQualityLevel(),
                    ["available_levels"] = new JArray(QualitySettings.names),
                    ["vsync_count"] = QualitySettings.vSyncCount,
                    ["anti_aliasing"] = QualitySettings.antiAliasing,
                    ["shadow_resolution"] = QualitySettings.shadowResolution.ToString(),
                    ["texture_quality"] = QualitySettings.globalTextureMipmapLimit
                };
            }

            if (category == "all" || category == "physics")
            {
                data["physics"] = new JObject
                {
                    ["gravity"] = ToolHelpers.Vector3ToJson(Physics.gravity),
                    ["default_solver_iterations"] = Physics.defaultSolverIterations,
                    ["default_solver_velocity_iterations"] = Physics.defaultSolverVelocityIterations,
                    ["bounce_threshold"] = Physics.bounceThreshold,
                    ["sleep_threshold"] = Physics.sleepThreshold,
                    ["auto_simulation"] = Physics.simulationMode.ToString()
                };
            }

            if (category == "all" || category == "time")
            {
                data["time"] = new JObject
                {
                    ["fixed_timestep"] = Time.fixedDeltaTime,
                    ["maximum_timestep"] = Time.maximumDeltaTime,
                    ["time_scale"] = Time.timeScale,
                    ["maximum_particle_timestep"] = Time.maximumParticleDeltaTime
                };
            }

            if (category == "all" || category == "audio")
            {
                data["audio"] = new JObject
                {
                    ["global_volume"] = AudioListener.volume,
                    ["spatializer_plugin"] = AudioSettings.GetSpatializerPluginName(),
                    ["sample_rate"] = AudioSettings.outputSampleRate,
                    ["dsp_buffer_size"] = AudioSettings.GetConfiguration().dspBufferSize,
                    ["speaker_mode"] = AudioSettings.GetConfiguration().speakerMode.ToString()
                };
            }

            return ToolResponse.OkWithData(data, $"Project settings retrieved (category: {category}).");
        }

        private ToolResponse HandleSetProjectSetting(JObject parameters)
        {
            var category = ToolHelpers.GetRequiredString(parameters, "category").ToLowerInvariant();
            var property = ToolHelpers.GetRequiredString(parameters, "property").ToLowerInvariant();
            var value = ToolHelpers.GetRequiredString(parameters, "value");

            switch (category)
            {
                case "player":
                    return SetPlayerSetting(property, value);
                case "time":
                    return SetTimeSetting(property, value);
                default:
                    return ToolResponse.Fail(
                        $"Setting category '{category}' is not directly supported for modification. " +
                        "Supported categories: player, time. For other settings, use Edit/Project Settings... menu.");
            }
        }

        #endregion

        #region Setting Helpers

        private ToolResponse SetPlayerSetting(string property, string value)
        {
            switch (property)
            {
                case "company_name":
                    var oldCompany = PlayerSettings.companyName;
                    PlayerSettings.companyName = value;
                    return ToolResponse.OkWithData(new JObject
                    {
                        ["property"] = "company_name",
                        ["old_value"] = oldCompany,
                        ["new_value"] = value
                    }, $"Set company name to '{value}'.");

                case "product_name":
                    var oldProduct = PlayerSettings.productName;
                    PlayerSettings.productName = value;
                    return ToolResponse.OkWithData(new JObject
                    {
                        ["property"] = "product_name",
                        ["old_value"] = oldProduct,
                        ["new_value"] = value
                    }, $"Set product name to '{value}'.");

                case "version":
                    var oldVersion = PlayerSettings.bundleVersion;
                    PlayerSettings.bundleVersion = value;
                    return ToolResponse.OkWithData(new JObject
                    {
                        ["property"] = "version",
                        ["old_value"] = oldVersion,
                        ["new_value"] = value
                    }, $"Set bundle version to '{value}'.");

                default:
                    return ToolResponse.Fail(
                        $"Unknown player property: '{property}'. Supported: company_name, product_name, version");
            }
        }

        private ToolResponse SetTimeSetting(string property, string value)
        {
            if (!float.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float floatValue))
            {
                return ToolResponse.Fail($"Invalid float value: '{value}'");
            }

            switch (property)
            {
                case "fixed_timestep":
                    var oldFixed = Time.fixedDeltaTime;
                    Time.fixedDeltaTime = floatValue;
                    return ToolResponse.OkWithData(new JObject
                    {
                        ["property"] = "fixed_timestep",
                        ["old_value"] = oldFixed,
                        ["new_value"] = floatValue
                    }, $"Set fixed timestep to {floatValue}.");

                case "maximum_timestep":
                    var oldMax = Time.maximumDeltaTime;
                    Time.maximumDeltaTime = floatValue;
                    return ToolResponse.OkWithData(new JObject
                    {
                        ["property"] = "maximum_timestep",
                        ["old_value"] = oldMax,
                        ["new_value"] = floatValue
                    }, $"Set maximum timestep to {floatValue}.");

                case "time_scale":
                    var oldScale = Time.timeScale;
                    Time.timeScale = floatValue;
                    return ToolResponse.OkWithData(new JObject
                    {
                        ["property"] = "time_scale",
                        ["old_value"] = oldScale,
                        ["new_value"] = floatValue
                    }, $"Set time scale to {floatValue}.");

                default:
                    return ToolResponse.Fail(
                        $"Unknown time property: '{property}'. Supported: fixed_timestep, maximum_timestep, time_scale");
            }
        }

        #endregion
    }
}
