using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Core;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Tools.Vision;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AgentCore.Editor.Tools.Vision
{
    /// <summary>
    /// 图像发送工具（v1.16.0）— 让 agent 把一张截图/图「发送」到聊天窗口，以图片形式显示在 assistant 消息气泡里。
    /// <para>与 vision_analyze 职责分离：vision_analyze 是「截屏→送视觉模型分析→返回文字」；本工具是
    /// 「截屏/取图→把图展示在聊天窗口」（不送视觉模型、不返回图片描述，模型视角只是确认已发送）。</para>
    /// <para>用法场景：用户说「把当前画面截图发给我」「把这张图展示出来」「发一张当前场景的图」。用户上传的图
    /// （source=user_image）也可由模型转发展示。</para>
    /// </summary>
    [AgentTool("send_image",
        Description = "Send a screenshot / image into the chat as a picture displayed in the assistant bubble. Use it when the user asks you to show / send a picture of the current view (scene or game), or to display an image they uploaded. It captures the viewport (or reuses the user's attached image) and renders it as an image in the chat — it does NOT run a vision model to describe it.",
        Category = "Specialized",
        Visibility = ToolVisibility.OnDemand,
        RequiresMainThread = true)]
    public class SendImageTool : IAgentTool
    {
        public ToolMetadata Metadata => new ToolMetadata(
            name: "send_image",
            description: "Send a screenshot or image into the chat as a picture in the assistant message bubble. " +
                         "Actions: captures the requested source (scene / game / camera / user_image) to PNG and displays it in the chat. " +
                         "USE FOR: when the user asks you to 'send/show me the current view', 'take a screenshot of the scene/game', '把当前画面截图发给我 / 展示这张图'. " +
                         "DIFFERENT FROM vision_analyze: vision_analyze captures + runs a vision model to RETURN a text description (for you to inspect); send_image captures and DISPLAYS the picture in the chat (no vision model, no description). " +
                         "source: scene (user's current SceneView) | game (running Game view) | camera:<name> (a named Camera) | user_image (the picture the user attached in chat). " +
                         "caption: optional short text shown alongside the image. If you capture and are unsure the view is clear, mention what the picture shows in your reply text.",
            category: "Specialized",
            parametersSchema: _parametersSchema,
            requiresMainThread: true);

        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""source"": { ""type"": ""string"", ""description"": ""What to send: scene (user's current SceneView) | game (running Game view) | camera (requires camera_name) | user_image (the picture the user uploaded in chat). Default scene."" },
                ""camera_name"": { ""type"": ""string"", ""description"": ""(source=camera) Camera GameObject name/path to capture."" },
                ""width"": { ""type"": ""integer"", ""description"": ""Capture width in px (default 1024, clamped 64-4096)."" },
                ""height"": { ""type"": ""integer"", ""description"": ""Capture height in px (default 1024, clamped 64-4096)."" },
                ""caption"": { ""type"": ""string"", ""description"": ""Optional short caption shown in the chat alongside the image."" }
            },
            ""required"": [""source""]
        }");

        public async Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask; // 本工具为纯同步截屏操作，保持 async 签名以匹配 IAgentTool（消除 CS1998）
            var sourceRaw = (ToolHelpers.GetOptionalString(parameters, "source") ?? "scene").Trim();
            var cameraName = ToolHelpers.GetOptionalString(parameters, "camera_name");
            var width = ToolHelpers.GetOptionalInt(parameters, "width", 1024);
            var height = ToolHelpers.GetOptionalInt(parameters, "height", 1024);
            var caption = ToolHelpers.GetOptionalString(parameters, "caption");

            try
            {
                string dataUrl;

                // source=user_image: 转发用户上传到聊天里的那张图（不截屏）。
                if (sourceRaw.Equals("user_image", StringComparison.OrdinalIgnoreCase))
                {
                    if (!UserImageStore.TryGetCurrent(out dataUrl))
                        return ToolResult.Fail("send_image source=user_image: no user-attached image found. The user must upload an image in the chat first (attach button), then retry.");
                }
                else
                {
                    var source = ParseSource(sourceRaw);
                    var results = VisionCaptureService.Render(source, cameraName, width, height);
                    if (results == null || results.Count == 0)
                        return ToolResult.Fail("send_image: no camera captured for source '" + sourceRaw + "'.");
                    var fullPath = results[0].FullPath;
                    if (string.IsNullOrEmpty(fullPath) || !System.IO.File.Exists(fullPath))
                        return ToolResult.Fail("send_image: screenshot file not found.");
                    var bytes = System.IO.File.ReadAllBytes(fullPath);
                    if (bytes.Length == 0)
                        return ToolResult.Fail("send_image: screenshot is empty.");
                    dataUrl = "data:image/png;base64," + Convert.ToBase64String(bytes);
                }

                // 暂存待发图，供 UI 在本次工具调用完成时渲染到 assistant 气泡 + 写入 turn.ImageDataUrl。
                SendImageStore.Set(dataUrl);

                var sb = new StringBuilder();
                sb.Append("[image sent] " + (string.IsNullOrEmpty(caption) ? sourceRaw : caption));
                return ToolResult.Ok(sb.ToString());
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return ToolResult.Fail("send_image failed: " + ex.Message);
            }
        }

        private static VisionSource ParseSource(string raw)
        {
            switch ((raw ?? "scene").Trim().ToLowerInvariant())
            {
                case "game": return VisionSource.Game;
                case "camera": return VisionSource.Camera;
                case "user_image": return VisionSource.Auto; // user_image 已在上面单独处理，不会走到这
                default: return VisionSource.Scene;
            }
        }
    }
}
