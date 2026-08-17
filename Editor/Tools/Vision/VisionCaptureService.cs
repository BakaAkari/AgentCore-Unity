using System;
using System.Collections.Generic;
using System.IO;
using AgentCore.Editor.Tools.Infrastructure;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Tools.Vision
{
    /// <summary>
    /// 截图来源(v1.15.0)。<see cref="Auto"/> 按运行时状态默认:PlayMode→Game, Edit→Scene。
    /// </summary>
    public enum VisionSource
    {
        /// <summary>自动: Play Mode → Game, Edit → Scene。</summary>
        Auto,
        /// <summary>Play/运行相机(Camera.main)渲染——真实运行效果。</summary>
        Game,
        /// <summary>SceneView 相机渲染——用户当前场景视角(干净几何, 无 grid/gizmo overlay)。</summary>
        Scene,
        /// <summary>指定名称的 Camera 渲染。</summary>
        Camera,
        /// <summary>Game 与 Scene 都渲染(对比用)。</summary>
        GameAndScene
    }

    /// <summary>一次截图的结果 + 视角元数据(供 agent 与用户描述/选中对象三方对证)。</summary>
    public sealed class VisionCaptureResult
    {
        /// <summary>实际使用的源。</summary>
        public VisionSource Source;
        /// <summary>有效源字符串(game/scene/camera:name)。</summary>
        public string SourceLabel;
        /// <summary>PNG 文件(Assets 相对路径)。</summary>
        public string AssetPath;
        /// <summary>PNG 文件(完整路径)。</summary>
        public string FullPath;
        /// <summary>SceneView 视角:焦点(仅 scene 源; 否则 null)。</summary>
        public Vector3? Pivot;
        /// <summary>SceneView 视角:旋转(仅 scene 源)。</summary>
        public Quaternion? Rotation;
        /// <summary>SceneView 视角(仅 scene 源)或相机位置(Game/Camera 源)。</summary>
        public Vector3? CameraPosition;
        /// <summary>当前选中对象信息(旁路数据, 不依赖视觉): instanceId/name/path/position。复合 JObject 字符串。</summary>
        public string SelectionInfo;
    }

    /// <summary>
    /// 截图管线(v1.15.0)— 渲染 Game / SceneView / 指定相机到 PNG, 并提取视角元数据。
    /// <para>
    /// 复用 <c>ManageCameraTool</c> 的相机渲染模式(camera.targetTexture → RenderTexture → ReadPixels → PNG)。
    /// </para>
    /// <para>
    /// 已验证(2026-08, Unity 2022.3.50f1 探针): <c>SceneView.lastActiveSceneView.camera.Render()</c> 在
    /// PlayMode 与 Edit 下都能出图, 且 = 用户当前视角(position/rotation 与 view.pivot/rotation/cameraDistance 推导
    /// 差 &lt;0.01)。唯一边界: SceneView 相机 transform 只在 SceneView 渲染后同步——若探测太早可能未对齐,
    /// 故 Scene 源渲染前<b>显式对齐</b>相机到 view 参数(不依赖 Unity 自动同步), 渲染后恢复。
    /// </para>
    /// <para>Scene 源输出的是<b>干净的几何渲染</b>(无 grid/gizmo/选中线框)。要用户选了什么, 请读本类的
    /// <see cref="VisionCaptureResult.SelectionInfo"/>(Selection 旁路), 不要靠视觉猜。</para>
    /// </summary>
    public static class VisionCaptureService
    {
        /// <summary>默认输出目录(相对项目根; 存 Unity Library 下以避开 VCS, 不被 Assets 污染/不触发 AssetDatabase)。</summary>
        public const string DefaultOutputDir = "Library/AgentCore/Screenshots";
        private const int MaxDimension = 4096;

        /// <summary>Library 下截图目录的绝对路径({ProjectRoot}/Library/AgentCore/Screenshots, dataPath=.../Assets)。</summary>
        private static string DefaultOutputDirAbsolute
            => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "AgentCore", "Screenshots"));

        /// <summary>
        /// 解析 source 到要渲染的相机列表。返回 (viewportKey, camera) 列表。
        /// </summary>
        public static List<(VisionSource source, Camera cam)> ResolveSourceCameras(VisionSource source, string cameraName)
        {
            var result = new List<(VisionSource, Camera)>();
            switch (source)
            {
                case VisionSource.Auto:
                    return ResolveSourceCameras(EditorApplication.isPlaying ? VisionSource.Game : VisionSource.Scene, cameraName);
                case VisionSource.Game:
                    var g = Camera.main != null ? Camera.main : FirstCamera();
                    if (g != null) result.Add((VisionSource.Game, g));
                    break;
                case VisionSource.Scene:
                    var view = SceneView.lastActiveSceneView;
                    if (view != null && view.camera != null) result.Add((VisionSource.Scene, view.camera));
                    break;
                case VisionSource.Camera:
                    Camera c = null;
                    if (!string.IsNullOrEmpty(cameraName))
                    {
                        var go = ToolHelpers.FindGameObject(cameraName);
                        if (go != null) c = go.GetComponent<Camera>();
                    }
                    if (c == null) c = Camera.main != null ? Camera.main : FirstCamera();
                    if (c != null) result.Add((VisionSource.Camera, c));
                    break;
                case VisionSource.GameAndScene:
                    var view2 = SceneView.lastActiveSceneView;
                    if (Camera.main != null) result.Add((VisionSource.Game, Camera.main));
                    if (view2 != null && view2.camera != null) result.Add((VisionSource.Scene, view2.camera));
                    break;
            }
            return result;
        }

        private static Camera FirstCamera()
        {
            var cams = UnityEngine.Object.FindObjectsOfType<Camera>(true);
            return cams != null && cams.Length > 0 ? cams[0] : null;
        }

        /// <summary>
        /// 按 source 渲染一张或多张 PNG, 返回结果列表(含视角元数据)。
        /// <paramref name="cropRect"/> 为 null 时渲染整图; 非 null 时只把该归一化矩形区域(0-1, 左下角原点)
        /// 裁剪存为独立 PNG(用于\"先整图粗看 → 定位可疑区域 → crop 放大细看\"的两阶段视觉)。
        /// </summary>
        public static List<VisionCaptureResult> Render(
            VisionSource source, string cameraName, int width, int height, string outputAssetPath = null,
            Rect? cropRect = null)
        {
            var sources = ResolveSourceCameras(source, cameraName);
            if (sources.Count == 0)
                throw new InvalidOperationException(DescribeNoSource(source));

            var results = new List<VisionCaptureResult>();
            foreach (var (src, cam) in sources)
            {
                results.Add(RenderOne(src, cam, width, height, outputAssetPath, cropRect));
            }
            return results;
        }

        private static VisionCaptureResult RenderOne(
            VisionSource source, Camera cam, int width, int height, string outputAssetPath, Rect? cropRect = null)
        {
            width = Mathf.Clamp(width <= 0 ? 1024 : width, 64, MaxDimension);
            height = Mathf.Clamp(height <= 0 ? 1024 : height, 64, MaxDimension);

            string label = LabelOf(source, cam);
            var safeName = System.Text.RegularExpressions.Regex.Replace(label, @"[^A-Za-z0-9_]", "_");

            // 默认: 存 Library/AgentCore/Screenshots(绝对路径, 避免 VCS/Assets 污染, 不触发 AssetDatabase)
            // 显式 outputAssetPath: 走 NormemizeAssignedPath(Assets 语义, 允许用户指定 Assets 目录用于手工查看)
            bool isAssetPath;
            string writePath;      // 写文件用的路径(绝对, 不依赖 cwd)
            string displayPath;    // 返回给 agent 的展示路径(相对项目根或 Assets)
            if (string.IsNullOrWhiteSpace(outputAssetPath))
            {
                writePath = Path.Combine(DefaultOutputDirAbsolute, $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                displayPath = ToProjectRootRelative(writePath);
                isAssetPath = false;
            }
            else
            {
                var normalized = ToolHelpers.NormalizeAssetPath(outputAssetPath);
                if (!normalized.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    normalized += ".png";
                // NormalizeAssetPath 返回 "Assets/..." 相对项目根; 转绝对用于写
                writePath = Path.GetFullPath(normalized);
                displayPath = normalized;
                isAssetPath = normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
            }
            // EnsureDirectoryExists 期望文件路径, 内部取父目录创建(递归)。传 writePath 而非 GetDirectoryName(writePath),
            // 否则它只会创建到上一级(Library/AgentCore), 漏建 Screenshots 子目录 → File.WriteAllBytes 报路径不存在。
            ToolHelpers.EnsureDirectoryExists(writePath);

            // Scene 源: 渲染前显式对齐相机到用户当前视角(解决"相机未同步"边界), 渲染后恢复原姿态。
            var sceneView = (source == VisionSource.Scene) ? SceneView.lastActiveSceneView : null;
            var oldPos = cam.transform.position;
            var oldRot = cam.transform.rotation;
            bool repositioned = false;
            if (sceneView != null && sceneView.camera == cam)
            {
                Vector3 fwd = sceneView.rotation * Vector3.forward;
                Vector3 p = sceneView.pivot - fwd * sceneView.cameraDistance;
                // 仅当没对齐才重设(避免给已同步的相机做无谓改动)
                if (Vector3.Distance(cam.transform.position, p) > 0.01f
                    || Quaternion.Angle(cam.transform.rotation, sceneView.rotation) > 0.5f)
                {
                    cam.transform.SetPositionAndRotation(p, sceneView.rotation);
                    repositioned = true;
                }
            }

            // 相机 → RenderTexture → PNG
            var rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            var oldTarget = cam.targetTexture;
            var oldActive = RenderTexture.active;
            try
            {
                cam.targetTexture = rt;
                RenderTexture.active = rt;
                cam.Render();
                var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
                try
                {
                    tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    tex.Apply();

                    if (cropRect.HasValue)
                    {
                        // 归一化 viewport 坐标(0-1, 左下角原点) → 像素矩形(Unity 纹理左下角原点, 像素单位)。
                        // ReadPixels 的 Rect 也是左下角原点, 故无需翻转 y。
                        var r = cropRect.Value;
                        int _cropX = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(r.x) * width), 0, width - 1);
                        int _cropY = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(r.y) * height), 0, height - 1);
                        int _cropW = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(r.width) * width), 1, width - _cropX);
                        int _cropH = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(r.height) * height), 1, height - _cropY);

                        var cropTex = new Texture2D(_cropW, _cropH, TextureFormat.RGBA32, false);
                        try
                        {
                            cropTex.ReadPixels(new Rect(_cropX, _cropY, _cropW, _cropH), 0, 0);
                            cropTex.Apply();

                            // crop 用独立文件名(避免覆盖整图), 带区域坐标便于辨识。
                            string cropWrite = InsertBeforeExt(writePath, $"_crop_{_cropX}_{_cropY}_{_cropW}_{_cropH}");
                            string cropDisplay = InsertBeforeExt(displayPath, $"_crop_{_cropX}_{_cropY}_{_cropW}_{_cropH}");
                            File.WriteAllBytes(cropWrite, cropTex.EncodeToPNG());
                            if (isAssetPath)
                                AssetDatabase.ImportAsset(cropDisplay, ImportAssetOptions.ForceSynchronousImport);

                            writePath = cropWrite;
                            displayPath = cropDisplay;
                        }
                        finally
                        {
                            UnityEngine.Object.DestroyImmediate(cropTex);
                        }
                    }
                    else
                    {
                        File.WriteAllBytes(writePath, tex.EncodeToPNG());
                        if (isAssetPath)
                            AssetDatabase.ImportAsset(displayPath, ImportAssetOptions.ForceSynchronousImport);
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(tex);
                }
            }
            finally
            {
                if (repositioned)
                {
                    cam.transform.SetPositionAndRotation(oldPos, oldRot);
                }
                cam.targetTexture = oldTarget;
                RenderTexture.active = oldActive;
                RenderTexture.ReleaseTemporary(rt);
            }

            return new VisionCaptureResult
            {
                Source = source,
                SourceLabel = label,
                AssetPath = displayPath,
                FullPath = writePath,
                Pivot = sceneView != null && sceneView.camera == cam ? (Vector3?)sceneView.pivot : null,
                Rotation = sceneView != null && sceneView.camera == cam ? (Quaternion?)sceneView.rotation : null,
                CameraPosition = cam.transform.position,
                SelectionInfo = BuildSelectionInfo()
            };
        }

        /// <summary>把绝对路径转成相对项目根的展示路径(如 Library/AgentCore/Screenshots/x.png 或 Assets/x.png)。</summary>
        private static string ToProjectRootRelative(string absolutePath)
        {
            var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var p = Path.GetFullPath(absolutePath);
            if (p.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                var rel = p.Substring(root.Length).TrimStart('/', '\\');
                return rel.Replace('\\', '/');
            }
            return p.Replace('\\', '/');
        }

        /// <summary>
        /// 当前选中对象旁路数据(不依赖视觉): instanceId / name / 层级路径 / 世界位置。
        /// </summary>
        private static string BuildSelectionInfo()
        {
            var sel = Selection.activeGameObject;
            if (sel == null) return "(no selection)";
            string path = sel.transform != null
                ? TransformPath(sel.transform)
                : sel.name;
            var pos = sel.transform != null ? sel.transform.position : Vector3.zero;
            return $"{{instanceId:{sel.GetInstanceID()}, name:\"{sel.name}\", path:\"{path}\", position:({pos.x:F2},{pos.y:F2},{pos.z:F2})}}";
        }

        private static string TransformPath(Transform t)
        {
            var parts = new List<string>();
            var cur = t;
            while (cur != null) { parts.Add(cur.name); cur = cur.parent; }
            parts.Reverse();
            return string.Join("/", parts);
        }

        /// <summary>在文件名的扩展名之前插入 <paramref name="suffix"/>（如 x.png + "_crop_.." → x_crop_...png）。</summary>
        private static string InsertBeforeExt(string path, string suffix)
        {
            if (string.IsNullOrEmpty(path)) return path;
            var ext = Path.GetExtension(path);
            var noExt = path.Substring(0, path.Length - ext.Length);
            return noExt + suffix + ext;
        }

        private static string LabelOf(VisionSource source, Camera cam)
        {
            switch (source)
            {
                case VisionSource.Game: return "GameView";
                case VisionSource.Scene: return "SceneView";
                case VisionSource.Camera: return "Camera_" + cam.gameObject.name;
                default: return "Capture";
            }
        }

        private static string DescribeNoSource(VisionSource source)
        {
            switch (source)
            {
                case VisionSource.Game: return "No Game camera found. Ensure a Camera exists (or is tagged MainCamera).";
                case VisionSource.Scene: return "No active SceneView or its camera is unavailable. Open a Scene View window.";
                case VisionSource.Camera: return "Specified Camera not found and no default Camera available.";
                case VisionSource.GameAndScene: return "No Game camera or SceneView available to capture.";
                default: return "No camera available for the selected source.";
            }
        }
    }
}
