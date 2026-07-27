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

namespace AgentCore.Editor.Tools.Native.Specialized
{
    /// <summary>
    /// Creates, configures, inspects, aligns, and renders Unity Camera components.
    /// </summary>
    [AgentTool("manage_camera",
        Description = "Unity Camera component management — create, configure, inspect, align, and render, plus SceneView camera control (v1.9.7 G09). " +
                      "Actions: create (new Camera with configurable properties), get_info (FOV/near/far/clear/culling/depth), " +
                      "configure (modify any Camera property), look_at (point camera at world position), " +
                      "align_to_view (match Scene View camera), create_render_texture (create RT asset), " +
                      "render_to_texture (capture camera output to file), list_cameras (all cameras with priority), set_main_camera (tag as MainCamera), " +
                      "get_scene_view (read SceneView pivot/size/rotation/orthographic/in2DMode — the Editor's own viewport camera), " +
                      "set_scene_view (write SceneView pivot/size/rotation/orthographic/in2DMode — programmatically frame the SceneView on a location). " +
                      "USE FOR: camera setup, adjusting perspective/orthographic, rendering screenshots, aligning camera to current view, driving SceneView from a script. " +
                      "NOT FOR: Cinemachine virtual cameras (use manage_cinemachine), post-processing effects, camera animation (use Timeline). " +
                      "ACTIVATE WHEN: user mentions 'camera', 'FOV', 'render texture', 'screenshot', 'camera alignment', 'main camera', 'scene view', 'frame scene view'.",
        Category = "Specialized",
        Visibility = ToolVisibility.OnDemand,
        RequiresMainThread = true)]
    public class ManageCameraTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": { ""type"": ""string"", ""enum"": [""create"", ""get_info"", ""configure"", ""look_at"", ""align_to_view"", ""create_render_texture"", ""render_to_texture"", ""list_cameras"", ""set_main_camera"", ""get_scene_view"", ""set_scene_view""], ""description"": ""Camera action to perform"" },
                ""pivot"": { ""type"": ""object"", ""description"": ""(set_scene_view) Vector3 {x,y,z} — SceneView orbit pivot (world space)."", ""properties"": { ""x"": { ""type"": ""number"" }, ""y"": { ""type"": ""number"" }, ""z"": { ""type"": ""number"" } }, ""required"": [""x"", ""y"", ""z""] },
                ""size"": { ""type"": ""number"", ""description"": ""(set_scene_view) Scalar SceneView zoom (approx camera distance from pivot)."" },
                ""orthographic"": { ""type"": ""boolean"", ""description"": ""(set_scene_view / create / configure) Toggle orthographic projection. For set_scene_view: toggles SceneView orthographic viewport."" },
                ""in2DMode"": { ""type"": ""boolean"", ""description"": ""(set_scene_view) Toggle SceneView 2D mode."" },
                ""repaint"": { ""type"": ""boolean"", ""description"": ""(set_scene_view) Force SceneView.Repaint() after applying changes. Default true."" },
                ""name"": { ""type"": ""string"", ""description"": ""Camera GameObject name or hierarchy path (e.g. 'MainCamera' or 'Player/PlayerCamera'). For render_to_texture the tool also accepts this via 'target' as a fallback, and falls back to Camera.main when both are omitted."" },
                ""position"": { ""type"": ""object"", ""description"": ""Vector3 object {x,y,z}; used as transform position or look_at target position"", ""properties"": { ""x"": { ""type"": ""number"" }, ""y"": { ""type"": ""number"" }, ""z"": { ""type"": ""number"" } }, ""required"": [""x"", ""y"", ""z""] },
                ""rotation"": { ""type"": ""object"", ""description"": ""Rotation as Euler {x,y,z} (degrees), OR as Quaternion {x,y,z,w} (include w to force quaternion mode; set_scene_view supports both)"", ""properties"": { ""x"": { ""type"": ""number"" }, ""y"": { ""type"": ""number"" }, ""z"": { ""type"": ""number"" }, ""w"": { ""type"": ""number"" } }, ""required"": [""x"", ""y"", ""z""] },
                ""target"": { ""type"": ""string"", ""description"": ""For look_at: target GameObject name/path to point the camera at. For render_to_texture: fallback camera path when 'name' is not given (accepted for agent convenience)."" },
                ""fov"": { ""type"": ""number"" },
                ""near_clip"": { ""type"": ""number"" },
                ""far_clip"": { ""type"": ""number"" },
                ""depth"": { ""type"": ""number"" },
                ""tag_main_camera"": { ""type"": ""boolean"" },
                ""orthographic"": { ""type"": ""boolean"" },
                ""orthographic_size"": { ""type"": ""number"" },
                ""clear_flags"": { ""type"": ""string"", ""description"": ""Skybox, SolidColor, Depth, or Nothing"" },
                ""background_color"": { ""description"": ""Color object {r,g,b,a} or HTML string such as #223344FF"" },
                ""culling_mask"": { ""description"": ""Integer layer mask or comma-separated layer names"" },
                ""asset_path"": { ""type"": ""string"", ""description"": ""Assets path for RenderTexture asset"" },
                ""output_path"": { ""type"": ""string"", ""description"": ""Assets path for PNG output"" },
                ""width"": { ""type"": ""integer"" },
                ""height"": { ""type"": ""integer"" },
                ""format"": { ""type"": ""string"", ""description"": ""RenderTextureFormat name"" },
                ""include_inactive"": { ""type"": ""boolean"" }
            },
            ""required"": [""action""]
        }");


        /// <summary>
        /// Tool metadata for auto-discovery registration.
        /// </summary>
        public ToolMetadata Metadata => new ToolMetadata(
            name: "manage_camera",
            description: "Create, configure, inspect, align, and render Unity Cameras",
            category: "Specialized",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        /// <summary>
        /// Executes the requested camera management action.
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
                    case "create": response = HandleCreate(parameters); break;
                    case "get_info": response = HandleGetInfo(parameters); break;
                    case "configure": response = HandleConfigure(parameters); break;
                    case "look_at": response = HandleLookAt(parameters); break;
                    case "align_to_view": response = HandleAlignToView(parameters); break;
                    case "create_render_texture": response = HandleCreateRenderTexture(parameters); break;
                    case "render_to_texture": response = HandleRenderToTexture(parameters); break;
                    case "list_cameras": response = HandleListCameras(parameters); break;
                    case "set_main_camera": response = HandleSetMainCamera(parameters); break;
                    case "get_scene_view": response = HandleGetSceneView(parameters); break;
                    case "set_scene_view": response = HandleSetSceneView(parameters); break;
                    default:
                        response = ToolResponse.Fail($"Unknown action: {action}. Valid actions: create, get_info, configure, look_at, align_to_view, create_render_texture, render_to_texture, list_cameras, set_main_camera, get_scene_view, set_scene_view");
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

        private ToolResponse HandleCreate(JObject parameters)
        {
            var name = ToolHelpers.GetOptionalString(parameters, "name", "Camera");
            var go = new GameObject(name);
            ToolHelpers.RegisterCreatedObject(go, "Create Camera");

            go.transform.position = ToolHelpers.ParseVector3(parameters["position"], Vector3.zero);
            go.transform.eulerAngles = ToolHelpers.ParseVector3(parameters["rotation"], Vector3.zero);

            var camera = go.AddComponent<Camera>();
            camera.fieldOfView = ToolHelpers.GetOptionalFloat(parameters, "fov", camera.fieldOfView);
            camera.nearClipPlane = ToolHelpers.GetOptionalFloat(parameters, "near_clip", camera.nearClipPlane);
            camera.farClipPlane = ToolHelpers.GetOptionalFloat(parameters, "far_clip", camera.farClipPlane);
            camera.depth = ToolHelpers.GetOptionalFloat(parameters, "depth", camera.depth);

            if (ToolHelpers.GetOptionalBool(parameters, "tag_main_camera", false))
                SetSingleMainCamera(go);

            EditorUtility.SetDirty(go);
            return ToolResponse.OkWithData(SerializeCamera(camera), $"Created Camera '{go.name}'.");
        }

        private ToolResponse HandleGetInfo(JObject parameters)
        {
            var camera = ResolveCamera(ToolHelpers.GetRequiredString(parameters, "name"));
            if (camera == null) return ToolResponse.Fail("Camera not found on the specified GameObject.");
            return ToolResponse.OkWithData(SerializeCamera(camera), $"Camera info for '{camera.gameObject.name}'.");
        }

        private ToolResponse HandleConfigure(JObject parameters)
        {
            var camera = ResolveCamera(ToolHelpers.GetRequiredString(parameters, "name"));
            if (camera == null) return ToolResponse.Fail("Camera not found on the specified GameObject.");

            ToolHelpers.RecordUndo(camera, "Configure Camera");
            var changes = new List<string>();

            SetFloat(parameters, "fov", v => camera.fieldOfView = v, changes);
            SetFloat(parameters, "near_clip", v => camera.nearClipPlane = v, changes);
            SetFloat(parameters, "far_clip", v => camera.farClipPlane = v, changes);
            SetFloat(parameters, "depth", v => camera.depth = v, changes);
            SetFloat(parameters, "orthographic_size", v => camera.orthographicSize = v, changes);

            if (parameters["orthographic"] != null)
            {
                camera.orthographic = ToolHelpers.GetOptionalBool(parameters, "orthographic");
                changes.Add($"orthographic={camera.orthographic}");
            }

            var clearFlags = ToolHelpers.GetOptionalString(parameters, "clear_flags");
            if (!string.IsNullOrEmpty(clearFlags))
            {
                if (!Enum.TryParse(clearFlags, true, out CameraClearFlags parsed))
                    return ToolResponse.Fail($"Invalid clear_flags '{clearFlags}'. Valid values: Skybox, SolidColor, Depth, Nothing.");
                camera.clearFlags = parsed;
                changes.Add($"clear_flags={parsed}");
            }

            if (parameters["background_color"] != null)
            {
                camera.backgroundColor = ToolHelpers.ParseColor(parameters["background_color"], camera.backgroundColor);
                changes.Add($"background_color=#{ColorUtility.ToHtmlStringRGBA(camera.backgroundColor)}");
            }

            if (parameters["culling_mask"] != null)
            {
                camera.cullingMask = ParseCullingMask(parameters["culling_mask"]);
                changes.Add($"culling_mask={camera.cullingMask}");
            }

            EditorUtility.SetDirty(camera);
            return ToolResponse.OkWithData(SerializeCamera(camera), changes.Count == 0 ? $"No camera settings changed on '{camera.gameObject.name}'." : $"Configured '{camera.gameObject.name}': {string.Join(", ", changes)}");
        }

        private ToolResponse HandleLookAt(JObject parameters)
        {
            var camera = ResolveCamera(ToolHelpers.GetRequiredString(parameters, "name"));
            if (camera == null) return ToolResponse.Fail("Camera not found on the specified GameObject.");

            var targetName = ToolHelpers.GetOptionalString(parameters, "target");
            Vector3 targetPosition;
            if (!string.IsNullOrEmpty(targetName))
            {
                var target = ToolHelpers.FindGameObject(targetName);
                if (target == null) return ToolResponse.Fail($"Target GameObject not found: {targetName}");
                targetPosition = target.transform.position;
            }
            else if (parameters["position"] != null)
            {
                targetPosition = ToolHelpers.ParseVector3(parameters["position"]);
            }
            else
            {
                return ToolResponse.Fail("Provide either target GameObject name or position for look_at.");
            }

            ToolHelpers.RecordUndo(camera.transform, "Camera Look At");
            camera.transform.LookAt(targetPosition);
            EditorUtility.SetDirty(camera.transform);
            return ToolResponse.OkWithData(SerializeCamera(camera), $"Camera '{camera.gameObject.name}' now looks at {targetPosition}.");
        }

        // ─── G09 (v1.9.7): SceneView pivot/size/rotation/orthographic/in2DMode read+write ──
        private ToolResponse HandleGetSceneView(JObject parameters)
        {
            var view = SceneView.lastActiveSceneView;
            if (view == null)
                return ToolResponse.Fail("No active SceneView. Open a Scene View window in the Editor and try again.");
            var data = new JObject
            {
                ["pivot"] = ToolHelpers.Vector3ToJson(view.pivot),
                ["size"] = view.size,
                ["rotation"] = new JObject
                {
                    ["x"] = view.rotation.x,
                    ["y"] = view.rotation.y,
                    ["z"] = view.rotation.z,
                    ["w"] = view.rotation.w
                },
                ["rotation_euler"] = ToolHelpers.Vector3ToJson(view.rotation.eulerAngles),
                ["orthographic"] = view.orthographic,
                ["in_2d_mode"] = view.in2DMode,
                ["camera_position"] = view.camera != null
                    ? ToolHelpers.Vector3ToJson(view.camera.transform.position)
                    : null,
                ["camera_forward"] = view.camera != null
                    ? ToolHelpers.Vector3ToJson(view.camera.transform.forward)
                    : null,
                ["scene_view_title"] = view.titleContent != null ? view.titleContent.text : "SceneView"
            };
            return ToolResponse.OkWithData(data, $"SceneView: pivot={view.pivot}, size={view.size:F2}, orthographic={view.orthographic}, in2DMode={view.in2DMode}.");
        }

        private ToolResponse HandleSetSceneView(JObject parameters)
        {
            var view = SceneView.lastActiveSceneView;
            if (view == null)
                return ToolResponse.Fail("No active SceneView. Open a Scene View window in the Editor and try again.");

            bool anyChange = false;
            var applied = new JObject();

            var pivotToken = parameters["pivot"];
            if (pivotToken != null && pivotToken.Type != JTokenType.Null)
            {
                var newPivot = ToolHelpers.ParseVector3(pivotToken);
                view.pivot = newPivot;
                applied["pivot"] = ToolHelpers.Vector3ToJson(newPivot);
                anyChange = true;
            }

            if (parameters["size"] != null && parameters["size"].Type != JTokenType.Null)
            {
                var newSize = parameters["size"].Value<float>();
                if (newSize <= 0f)
                    return ToolResponse.Fail($"SceneView size must be > 0 (got {newSize}).");
                view.size = newSize;
                applied["size"] = newSize;
                anyChange = true;
            }

            var rotToken = parameters["rotation"];
            if (rotToken != null && rotToken.Type == JTokenType.Object)
            {
                var obj = (JObject)rotToken;
                // Accept full quaternion {x,y,z,w} OR Euler {x,y,z}.
                if (obj["w"] != null)
                {
                    float qx = obj["x"]?.Value<float>() ?? 0f;
                    float qy = obj["y"]?.Value<float>() ?? 0f;
                    float qz = obj["z"]?.Value<float>() ?? 0f;
                    float qw = obj["w"]?.Value<float>() ?? 1f;
                    var q = new Quaternion(qx, qy, qz, qw);
                    if (q == default) q = Quaternion.identity;
                    view.rotation = q;
                    applied["rotation_quaternion"] = new JObject { ["x"] = q.x, ["y"] = q.y, ["z"] = q.z, ["w"] = q.w };
                }
                else
                {
                    var euler = ToolHelpers.ParseVector3(rotToken);
                    view.rotation = Quaternion.Euler(euler);
                    applied["rotation_euler"] = ToolHelpers.Vector3ToJson(euler);
                }
                anyChange = true;
            }

            if (parameters["orthographic"] != null && parameters["orthographic"].Type != JTokenType.Null)
            {
                var ortho = parameters["orthographic"].Value<bool>();
                view.orthographic = ortho;
                applied["orthographic"] = ortho;
                anyChange = true;
            }

            if (parameters["in2DMode"] != null && parameters["in2DMode"].Type != JTokenType.Null)
            {
                var in2d = parameters["in2DMode"].Value<bool>();
                view.in2DMode = in2d;
                applied["in_2d_mode"] = in2d;
                anyChange = true;
            }

            if (!anyChange)
                return ToolResponse.Fail("No SceneView parameters provided. Set at least one of: pivot, size, rotation, orthographic, in2DMode.");

            var repaint = ToolHelpers.GetOptionalBool(parameters, "repaint", true);
            if (repaint) view.Repaint();

            var data = new JObject
            {
                ["applied"] = applied,
                ["repainted"] = repaint,
                ["pivot"] = ToolHelpers.Vector3ToJson(view.pivot),
                ["size"] = view.size,
                ["orthographic"] = view.orthographic,
                ["in_2d_mode"] = view.in2DMode
            };
            return ToolResponse.OkWithData(data, $"SceneView updated ({applied.Count} field(s) applied).");
        }

        private ToolResponse HandleAlignToView(JObject parameters)
        {
            var camera = ResolveCamera(ToolHelpers.GetRequiredString(parameters, "name"));
            if (camera == null) return ToolResponse.Fail("Camera not found on the specified GameObject.");
            var view = SceneView.lastActiveSceneView;
            if (view == null) return ToolResponse.Fail("No active SceneView found.");

            ToolHelpers.RecordUndo(camera.transform, "Align Camera To Scene View");
            camera.transform.position = view.camera.transform.position;
            camera.transform.rotation = view.camera.transform.rotation;
            EditorUtility.SetDirty(camera.transform);
            return ToolResponse.OkWithData(SerializeCamera(camera), $"Aligned Camera '{camera.gameObject.name}' to SceneView.");
        }

        private ToolResponse HandleCreateRenderTexture(JObject parameters)
        {
            var camera = ResolveCamera(ToolHelpers.GetRequiredString(parameters, "name"));
            if (camera == null) return ToolResponse.Fail("Camera not found on the specified GameObject.");

            var path = ToolHelpers.NormalizeAssetPath(ToolHelpers.GetRequiredString(parameters, "asset_path"));
            var width = ToolHelpers.GetOptionalInt(parameters, "width", 1024);
            var height = ToolHelpers.GetOptionalInt(parameters, "height", 1024);
            var depth = ToolHelpers.GetOptionalInt(parameters, "depth", 24);
            var formatName = ToolHelpers.GetOptionalString(parameters, "format", "ARGB32");
            if (!Enum.TryParse(formatName, true, out RenderTextureFormat format))
                format = RenderTextureFormat.ARGB32;

            ToolHelpers.EnsureDirectoryExists(path);
            var rt = new RenderTexture(width, height, depth, format) { name = Path.GetFileNameWithoutExtension(path) };
            AssetDatabase.CreateAsset(rt, path);

            ToolHelpers.RecordUndo(camera, "Bind RenderTexture To Camera");
            camera.targetTexture = rt;
            EditorUtility.SetDirty(camera);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            return ToolResponse.OkWithData(new { path, width, height, depth, format = format.ToString(), camera = camera.gameObject.name }, $"Created RenderTexture '{path}' and bound it to '{camera.gameObject.name}'.");
        }

        private ToolResponse HandleRenderToTexture(JObject parameters)
        {
            // Accept either 'name' (documented) or 'target' (agents often confuse the two).
            // If neither provided, fall back to Camera.main or a lone scene camera.
            var cameraId = ToolHelpers.GetOptionalString(parameters, "name");
            if (string.IsNullOrEmpty(cameraId))
            {
                cameraId = ToolHelpers.GetOptionalString(parameters, "target");
            }

            Camera camera;
            if (string.IsNullOrEmpty(cameraId))
            {
                camera = Camera.main;
                if (camera == null)
                {
                    var allCams = UnityEngine.Object.FindObjectsOfType<Camera>(true);
                    if (allCams.Length == 1) camera = allCams[0];
                }
                if (camera == null)
                {
                    return ToolResponse.Fail(
                        "No camera specified and Camera.main is null. Pass 'name' (or 'target') with the camera GameObject name or hierarchy path (e.g. 'Player/PlayerCamera'). Use list_cameras to enumerate available cameras.");
                }
            }
            else
            {
                camera = ResolveCamera(cameraId);
                if (camera == null)
                {
                    return ToolResponse.Fail(
                        $"Camera not found for '{cameraId}'. Tried GameObject.Find (exact hierarchy path) and by-name scan (including inactive). " +
                        "The GameObject must have a Camera component. Use list_cameras to see valid camera paths.");
                }
            }

            var outputPathRaw = ToolHelpers.GetOptionalString(parameters, "output_path");
            if (string.IsNullOrEmpty(outputPathRaw))
            {
                // Default: Assets/AgentCore/Screenshots/<CameraName>_<timestamp>.png
                var safeName = System.Text.RegularExpressions.Regex.Replace(camera.gameObject.name, @"[^A-Za-z0-9_]", "_");
                outputPathRaw = $"Assets/AgentCore/Screenshots/{safeName}_{System.DateTime.Now:yyyyMMdd_HHmmss}.png";
            }
            var outputPath = ToolHelpers.NormalizeAssetPath(outputPathRaw);
            var width = ToolHelpers.GetOptionalInt(parameters, "width", 1024);
            var height = ToolHelpers.GetOptionalInt(parameters, "height", 1024);
            ToolHelpers.EnsureDirectoryExists(outputPath);

            var oldTarget = camera.targetTexture;
            var oldActive = RenderTexture.active;
            var rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            try
            {
                camera.targetTexture = rt;
                RenderTexture.active = rt;
                camera.Render();

                var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                File.WriteAllBytes(outputPath, tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);
            }
            finally
            {
                camera.targetTexture = oldTarget;
                RenderTexture.active = oldActive;
                RenderTexture.ReleaseTemporary(rt);
            }

            AssetDatabase.Refresh();
            return ToolResponse.OkWithData(new { outputPath, width, height, camera = camera.gameObject.name }, $"Rendered Camera '{camera.gameObject.name}' to '{outputPath}'.");
        }

        private ToolResponse HandleListCameras(JObject parameters)
        {
            var includeInactive = ToolHelpers.GetOptionalBool(parameters, "include_inactive", false);
            var cameras = UnityEngine.Object.FindObjectsOfType<Camera>(includeInactive)
                .Select(SerializeCamera)
                .ToArray();
            return ToolResponse.OkWithData(new { count = cameras.Length, cameras }, $"Found {cameras.Length} Camera(s).");
        }

        private ToolResponse HandleSetMainCamera(JObject parameters)
        {
            var camera = ResolveCamera(ToolHelpers.GetRequiredString(parameters, "name"));
            if (camera == null) return ToolResponse.Fail("Camera not found on the specified GameObject.");
            SetSingleMainCamera(camera.gameObject);
            return ToolResponse.OkWithData(SerializeCamera(camera), $"Set '{camera.gameObject.name}' as MainCamera.");
        }

        private static Camera ResolveCamera(string name)
        {
            var go = ToolHelpers.FindGameObject(name);
            return go != null ? go.GetComponent<Camera>() : null;
        }

        private static void SetSingleMainCamera(GameObject target)
        {
            foreach (var camera in UnityEngine.Object.FindObjectsOfType<Camera>(true))
            {
                if (camera.CompareTag("MainCamera"))
                {
                    ToolHelpers.RecordUndo(camera.gameObject, "Clear MainCamera Tag");
                    camera.gameObject.tag = "Untagged";
                    EditorUtility.SetDirty(camera.gameObject);
                }
            }

            ToolHelpers.RecordUndo(target, "Set MainCamera Tag");
            target.tag = "MainCamera";
            EditorUtility.SetDirty(target);
        }

        private static void SetFloat(JObject parameters, string key, Action<float> setter, List<string> changes)
        {
            if (parameters[key] == null) return;
            var value = ToolHelpers.GetOptionalFloat(parameters, key);
            setter(value);
            changes.Add($"{key}={value}");
        }

        private static int ParseCullingMask(JToken token)
        {
            if (token.Type == JTokenType.Integer) return token.Value<int>();
            var text = token.ToString();
            if (int.TryParse(text, out var parsed)) return parsed;

            var mask = 0;
            foreach (var layerName in text.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)))
            {
                var layer = LayerMask.NameToLayer(layerName);
                if (layer < 0) throw new ArgumentException($"Layer not found: {layerName}");
                mask |= 1 << layer;
            }
            return mask;
        }

        private static object SerializeCamera(Camera camera)
        {
            var targetTexture = camera.targetTexture;
            return new
            {
                name = camera.gameObject.name,
                path = GetGameObjectPath(camera.gameObject),
                active = camera.gameObject.activeInHierarchy,
                tag = camera.gameObject.tag,
                position = ToolHelpers.Vector3ToJson(camera.transform.position),
                rotation = ToolHelpers.QuaternionToJson(camera.transform.rotation),
                clearFlags = camera.clearFlags.ToString(),
                backgroundColor = $"#{ColorUtility.ToHtmlStringRGBA(camera.backgroundColor)}",
                fov = camera.fieldOfView,
                nearClip = camera.nearClipPlane,
                farClip = camera.farClipPlane,
                projection = camera.orthographic ? "Orthographic" : "Perspective",
                orthographic = camera.orthographic,
                orthographicSize = camera.orthographicSize,
                depth = camera.depth,
                cullingMask = camera.cullingMask,
                targetTexture = targetTexture != null ? new { name = targetTexture.name, width = targetTexture.width, height = targetTexture.height, depth = targetTexture.depth, format = targetTexture.format.ToString(), path = AssetDatabase.GetAssetPath(targetTexture) } : null
            };
        }

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
    }
}
