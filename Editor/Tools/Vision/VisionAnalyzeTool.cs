using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Config;
using AgentCore.Editor.LLM;
using AgentCore.Editor.Tools.Infrastructure;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AgentCore.Editor.Tools.Vision
{
    /// <summary>
    /// 视觉感知工具(v1.15.0)— 让 agent「看见」Game/SceneView, 并获取视觉模型文字描述矫正执行。
    /// <para>action=capture 截图到 PNG; action=analyze 截图 + 送视觉模型取文字描述。source 支持 auto/game/scene/camera/game_and_scene,
    /// 结果附视角元数据(view=)与当前选中对象(selection=)旁路数据。</para>
    /// </summary>
    [AgentTool("vision_analyze",
        Description = "Visual perception — capture Unity Game View / SceneView / a named Camera to PNG, and optionally get the configured Vision Model's text description for self-correct. Actions: capture (screenshot to file only) / analyze (screenshot + vision description).",
        Category = "Meta",
        Visibility = ToolVisibility.OnDemand,
        RequiresMainThread = true)]
    public class VisionAnalyzeTool : IAgentTool
    {
        public ToolMetadata Metadata => new ToolMetadata(
            name: "vision_analyze",
            description: "Visual perception — capture Game View / SceneView / a named Camera to PNG, and optionally get the configured Vision Model's text description to self-correct execution. " +
                         "Actions: capture = screenshot to PNG file(s) only; analyze = screenshot + send to Vision Model → return text description(s). " +
                         "source: auto (default — PlayMode→game, Edit→scene) | game (real running render) | scene (user's current SceneView view) | camera:<name> | game_and_scene (both for comparison). " +
                         "Each result carries view metadata (pivot/rotation/camera position) and the current selection (via Selection, no visual guessing). " +
                         "USE FOR: verifying visual execution (UI layout, colors, geometry, scene arrangement, rendering), expected-vs-actual, debugging visual-only issues, seeing the user's current scene view. " +
                         "REGION ZOOM (two-stage): whole-view screenshots downscale fine detail, so far/small/occluded objects are often missed. To inspect such detail, first call analyze WITHOUT region (overview + locate the suspicious area), then call analyze WITH region=\"x,y,w,h\" (normalized viewport coords 0-1, bottom-left origin; e.g. \"0.3,0.4,0.2,0.2\") to crop & zoom that area — the returned PNG is a crop of the same frame (filename carries _crop_...) bringing raw local pixels to the model. " +
                         "ACTIVATE WHEN: user asks to 'see the view' / 'check the screen' / 'verify how it looks' / '看画面 / 看效果 / 看我执行的视觉结果'.",
            category: "Meta",
            parametersSchema: _parametersSchema,
            requiresMainThread: true);

        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": { ""type"": ""string"", ""enum"": [""capture"", ""analyze""], ""description"": ""capture = screenshot to file only; analyze = screenshot + vision model description"" },
                ""source"": { ""type"": ""string"", ""enum"": [""auto"", ""game"", ""scene"", ""camera"", ""game_and_scene""], ""description"": ""auto=default(PlayMode→game, Edit→scene); game=real running render; scene=user's current SceneView view; camera=name a Camera via camera_name; game_and_scene=both for comparison"" },
                ""camera_name"": { ""type"": ""string"", ""description"": ""(source=camera) Camera GameObject name/path to capture"" },
                ""width"": { ""type"": ""integer"", ""description"": ""Capture width in px (default 1024, clamped 64-4096)"" },
                ""height"": { ""type"": ""integer"", ""description"": ""Capture height in px (default 1024, clamped 64-4096)"" },
                ""prompt"": { ""type"": ""string"", ""description"": ""(analyze) Instruction to the vision model (overrides default scene-description prompt)"" },
                ""region"": { ""type"": ""string"", ""description"": ""(optional) Crop region to focus on, in normalized viewport coordinates 'x,y,w,h' (0-1, bottom-left origin). e.g. '0.3,0.4,0.2,0.2' zooms into the center-lower area. USE FOR two-stage vision: first call analyze without region to get an overview + suspicious area, then call analyze with region to zoom into that area for detail. Returns the cropped PNG."" },
                ""output_path"": { ""type"": ""string"", ""description"": ""(capture) Optional Assets path prefix for PNGs (default Library/AgentCore/Screenshots/<source>_<timestamp>.png — outside Assets so it's not tracked by VCS)"" }
            },
            ""required"": [""action""]
        }");

        public async Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
        {
            var action = ToolHelpers.GetRequiredString(parameters, "action") ?? "analyze";

            var source = ParseSource(ToolHelpers.GetOptionalString(parameters, "source"));
            var cameraName = ToolHelpers.GetOptionalString(parameters, "camera_name");
            var width = ToolHelpers.GetOptionalInt(parameters, "width", 1024);
            var height = ToolHelpers.GetOptionalInt(parameters, "height", 1024);
            var cropRect = ParseRegion(ToolHelpers.GetOptionalString(parameters, "region"));

            try
            {
                switch (action)
                {
                    case "capture":
                    {
                        var outputPath = ToolHelpers.GetOptionalString(parameters, "output_path");
                        var results = VisionCaptureService.Render(source, cameraName, width, height, outputPath, cropRect);
                        var sb = new StringBuilder();
                        foreach (var r in results)
                            sb.AppendLine($"Captured {r.SourceLabel} to '{r.AssetPath}' (full: {r.FullPath}) view={DescribeView(r)} selection={r.SelectionInfo}");
                        return ToolResult.Ok(sb.ToString().Trim());
                    }

                    case "analyze":
                    default:
                    {
                        // fail-closed: 视觉未启用/未配置 → 明确错误并引导配置
                        if (!VisionModelConfig.IsEnabled)
                            return ToolResult.Fail("Vision model is not enabled. Enable it in Project Settings > AgentCore > Model & Agent (Vision Model card) — set 'Enable Vision Model', provide endpoint/model/apiKey, then retry.");
                        if (!VisionModelConfig.IsConfigured)
                            return ToolResult.Fail("Vision model is not fully configured (endpoint/model missing). Configure endpoint/model/apiKey in Project Settings > AgentCore > Model & Agent (Vision Model card), then retry.");

                        var defaultPrompt = "Describe this Unity view in detail: scene layout, visible objects and their arrangement, colors, text on screen, and any obvious rendering issues (clipping, misalignment, missing objects).";
                        var prompt = ToolHelpers.GetOptionalString(parameters, "prompt") ?? defaultPrompt;

                        var results = VisionCaptureService.Render(source, cameraName, width, height, null, cropRect);
                        if (results.Count == 0)
                            return ToolResult.Fail("vision_analyze: no camera captured for source.");

                        var sb = new StringBuilder();
                        foreach (var r in results)
                        {
                            var dataUrl = ToDataUrl(r.FullPath);
                            var description = await VisionLLMClient.AnalyzeImageAsync(dataUrl, prompt, cancellationToken);
                            sb.AppendLine($"[{r.SourceLabel}] view={DescribeView(r)} selection={r.SelectionInfo}");
                            sb.AppendLine(description);
                            sb.AppendLine();
                        }
                        return ToolResult.Ok(sb.ToString().Trim());
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return ToolResult.Fail($"vision_analyze {action} failed: {ex.Message}");
            }
        }

        private static VisionSource ParseSource(string raw)
        {
            switch ((raw ?? "auto").Trim().ToLowerInvariant())
            {
                case "game": return VisionSource.Game;
                case "scene": return VisionSource.Scene;
                case "camera": return VisionSource.Camera;
                case "game_and_scene":
                case "both": return VisionSource.GameAndScene;
                default: return VisionSource.Auto;
            }
        }

        /// <summary>
        /// 解析归一化 viewport 裁剪矩形 "x,y,w,h"(0-1, 左下角原点)。非法/缺失返回 null(整图)。
        /// </summary>
        private static Rect? ParseRegion(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var parts = raw.Split(',');
            if (parts.Length != 4) return null;
            if (!float.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var x) ||
                !float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var y) ||
                !float.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var w) ||
                !float.TryParse(parts[3], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var h))
                return null;
            // 归一化 + 下限保护(不传尺寸/维度 <=0 时视为非法, 回落整图)
            if (w <= 0f || h <= 0f) return null;
            return new Rect(Mathf.Clamp01(x), Mathf.Clamp01(y), Mathf.Clamp01(w), Mathf.Clamp01(h));
        }

        private static string DescribeView(VisionCaptureResult r)
        {
            var parts = new List<string>();
            if (r.CameraPosition.HasValue)
                parts.Add($"camera=({r.CameraPosition.Value.x:F2},{r.CameraPosition.Value.y:F2},{r.CameraPosition.Value.z:F2})");
            if (r.Pivot.HasValue)
                parts.Add($"pivot=({r.Pivot.Value.x:F2},{r.Pivot.Value.y:F2},{r.Pivot.Value.z:F2})");
            if (r.Rotation.HasValue)
            {
                var e = r.Rotation.Value.eulerAngles;
                parts.Add($"rotation=({e.x:F1},{e.y:F1},{e.z:F1})");
            }
            return parts.Count > 0 ? string.Join(" ", parts) : "(no view metadata)";
        }

        private static string ToDataUrl(string fullPath)
        {
            if (!System.IO.File.Exists(fullPath))
                throw new InvalidOperationException($"Screenshot file not found: {fullPath}");
            var bytes = System.IO.File.ReadAllBytes(fullPath);
            if (bytes.Length == 0)
                throw new InvalidOperationException($"Screenshot is empty: {fullPath}");
            return "data:image/png;base64," + Convert.ToBase64String(bytes);
        }
    }
}
