using System;
using System.IO;
using AgentCore.Editor.L10n;
using AgentCore.Editor.Tools.Vision;
using AgentCore.Editor.UI.Components;
using AgentCore.Editor.Utils;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI
{
    /// <summary>
    /// v1.17.0: 输入区图片附件——+ 按钮弹出"添加到消息"菜单（上传图片 / 截取 Game 视图 / 截取 Scene 视图），
    /// 拖拽图片到输入区也可附加。附加后<b>不立即发送</b>：在输入框上方显示预览条（悬停浮出大图），
    /// 用户补充文字后随下一条消息一起发送（<see cref="OnSendClicked"/> 消费 <c>_pendingAttachImageDataUrl</c>）。
    /// <para>图片不塞进主 LLM 上下文（base64 是噪音），主模型只收到"用户上传了图，可调 vision_analyze source=user_image"提示。</para>
    /// </summary>
    public partial class ChatWindow
    {
        /// <summary>附件图最长边（px），控制 data URL 体积。</summary>
        private const int AttachMaxSide = 1024;

        private AttachmentPreview _attachmentPreview;

        /// <summary>待发送图的纹理（预览用；清除/发送后销毁）。</summary>
        private Texture2D _pendingAttachTexture;

        /// <summary>待发送图的显示名（文件名或"Game 视图截图 hh:mm:ss"）。</summary>
        private string _pendingAttachName;

        // 待发送图跨 Domain Reload 保留：data URL 可达数 MB，放 SessionState（只活到编辑器关闭），
        // 不放 [SerializeField]，避免写进窗口布局文件。纹理本身不跨重载，CreateGUI 时按 data URL 重新解码。
        private const string PendingAttachDataUrlKey = "AgentCore.ChatWindow.PendingAttach.DataUrl";
        private const string PendingAttachNameKey = "AgentCore.ChatWindow.PendingAttach.Name";

        /// <summary>创建预览条（插在 input-area 正上方）并为输入区注册图片拖拽。</summary>
        private void InitializeAttachments(VisualElement chatArea, VisualElement inputArea)
        {
            _attachmentPreview = new AttachmentPreview();
            _attachmentPreview.RemoveClicked += ClearPendingAttachment;
            int index = chatArea.IndexOf(inputArea);
            if (index >= 0) chatArea.Insert(index, _attachmentPreview);
            else chatArea.Add(_attachmentPreview);

            inputArea.RegisterCallback<DragUpdatedEvent>(evt =>
            {
                if (TryGetDraggedImagePath() == null) return;
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                inputArea.AddToClassList("is-drag-over");
                evt.StopPropagation();
            }, TrickleDown.TrickleDown);
            inputArea.RegisterCallback<DragPerformEvent>(evt =>
            {
                inputArea.RemoveFromClassList("is-drag-over");
                var path = TryGetDraggedImagePath();
                if (path == null) return;
                DragAndDrop.AcceptDrag();
                evt.StopPropagation();
                AttachImageFile(path, Path.GetFileName(path));
            }, TrickleDown.TrickleDown);
            inputArea.RegisterCallback<DragLeaveEvent>(_ => inputArea.RemoveFromClassList("is-drag-over"));
            inputArea.RegisterCallback<DragExitedEvent>(_ => inputArea.RemoveFromClassList("is-drag-over"));

            RefreshAttachmentUI();
        }

        /// <summary>+ 按钮：弹出"添加到消息"菜单。</summary>
        private void OnAttachClicked()
        {
            if (_attachButton == null) return;
            var sceneView = SceneView.lastActiveSceneView;
            bool sceneAvailable = sceneView != null && sceneView.camera != null;
            bool gameAvailable = VisionCaptureService.ResolveSourceCameras(VisionSource.Game, null).Count > 0;
            UnityEditor.PopupWindow.Show(_attachButton.worldBound, new AttachmentMenuPopup(
                PickImageFile,
                () => CaptureViewToAttachment(VisionSource.Game), gameAvailable,
                () => CaptureViewToAttachment(VisionSource.Scene), sceneAvailable));
        }

        private void PickImageFile()
        {
            var path = EditorUtility.OpenFilePanel(Loc.Tr("chat.attach.title", "选择图片"), "", "png,jpg,jpeg");
            if (string.IsNullOrEmpty(path)) return; // 用户取消
            AttachImageFile(path, Path.GetFileName(path));
        }

        /// <summary>按视口比例渲染 Game / Scene 视图（最长边 <see cref="AttachMaxSide"/>）并附加。</summary>
        private void CaptureViewToAttachment(VisionSource source)
        {
            try
            {
                Vector2 size;
                if (source == VisionSource.Scene && SceneView.lastActiveSceneView != null)
                    size = SceneView.lastActiveSceneView.position.size * EditorGUIUtility.pixelsPerPoint;
                else
                    size = Handles.GetMainGameViewSize();
                if (size.x < 1f || size.y < 1f) size = new Vector2(16, 9);
                float scale = AttachMaxSide / Mathf.Max(size.x, size.y);
                int w = Mathf.Max(64, Mathf.RoundToInt(size.x * scale));
                int h = Mathf.Max(64, Mathf.RoundToInt(size.y * scale));

                var results = VisionCaptureService.Render(source, null, w, h);
                if (results.Count == 0) return;
                var label = source == VisionSource.Scene
                    ? Loc.Tr("chat.attach.sceneLabel", "Scene 视图截图")
                    : Loc.Tr("chat.attach.gameLabel", "Game 视图截图");
                AttachImageFile(results[0].FullPath, $"{label} {DateTime.Now:HH:mm:ss}");
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"[AgentCore] Capture {source} view for attachment failed: {ex.Message}");
            }
        }

        private void AttachImageFile(string path, string displayName)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length == 0)
                {
                    AgentCoreLog.Warning("[AgentCore] Attach image: selected file is empty.");
                    return;
                }
                AttachImageBytes(bytes, displayName);
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"[AgentCore] Attach image error: {ex.Message}");
            }
        }

        /// <summary>解码 → 超过 <see cref="AttachMaxSide"/> 时降采样 → PNG data URL，暂存为待发送图并显示预览。</summary>
        private void AttachImageBytes(byte[] bytes, string displayName)
        {
            var texture = new Texture2D(2, 2) { hideFlags = HideFlags.HideAndDontSave };
            if (!texture.LoadImage(bytes))
            {
                UnityEngine.Object.DestroyImmediate(texture);
                AgentCoreLog.Warning("[AgentCore] Attach image: not a valid image file.");
                return;
            }
            // 大图降采样（最近邻，API 均经验证存在：GetPixels32 / SetPixels32 / EncodeToPNG）。原图无需降采样时直接用。
            int w = texture.width, h = texture.height;
            if (w > AttachMaxSide || h > AttachMaxSide)
            {
                float scale = w >= h ? (float)AttachMaxSide / w : (float)AttachMaxSide / h;
                int nw = Math.Max(1, (int)(w * scale));
                int nh = Math.Max(1, (int)(h * scale));
                var srcPixels = texture.GetPixels32();
                var dstPixels = new Color32[nw * nh];
                for (int y = 0; y < nh; y++)
                {
                    int sy = Math.Min(h - 1, (int)(y / scale));
                    for (int x = 0; x < nw; x++)
                    {
                        int sx = Math.Min(w - 1, (int)(x / scale));
                        dstPixels[y * nw + x] = srcPixels[sy * w + sx];
                    }
                }
                var scaled = new Texture2D(nw, nh, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                scaled.SetPixels32(dstPixels);
                scaled.Apply();
                UnityEngine.Object.DestroyImmediate(texture);
                texture = scaled;
            }
            var png = texture.EncodeToPNG();
            if (png == null || png.Length == 0)
            {
                UnityEngine.Object.DestroyImmediate(texture);
                AgentCoreLog.Warning("[AgentCore] Attach image: encode failed.");
                return;
            }

            ReleasePendingAttachTexture();
            _pendingAttachTexture = texture;
            _pendingAttachImageDataUrl = "data:image/png;base64," + Convert.ToBase64String(png);
            _pendingAttachName = displayName;
            PersistPendingAttachment();
            RefreshAttachmentUI();
            _inputField?.Focus();
        }

        /// <summary>移除待发送图（预览条 ✕）。</summary>
        private void ClearPendingAttachment()
        {
            _pendingAttachImageDataUrl = null;
            _pendingAttachName = null;
            PersistPendingAttachment();
            ReleasePendingAttachTexture();
            RefreshAttachmentUI();
        }

        /// <summary>把当前待发送图写入 SessionState（无图时擦除），供 Domain Reload 后恢复。</summary>
        private void PersistPendingAttachment()
        {
            if (string.IsNullOrEmpty(_pendingAttachImageDataUrl))
            {
                SessionState.EraseString(PendingAttachDataUrlKey);
                SessionState.EraseString(PendingAttachNameKey);
                return;
            }
            SessionState.SetString(PendingAttachDataUrlKey, _pendingAttachImageDataUrl);
            SessionState.SetString(PendingAttachNameKey, _pendingAttachName ?? string.Empty);
        }

        /// <summary>
        /// CreateGUI 时恢复 Domain Reload 前的待发送图：重新解码 data URL 生成预览纹理。
        /// 已有待发送图（本次窗口生命周期内新附加的）时不覆盖。
        /// </summary>
        private void RestorePendingAttachment()
        {
            if (!string.IsNullOrEmpty(_pendingAttachImageDataUrl)) return;

            var dataUrl = SessionState.GetString(PendingAttachDataUrlKey, string.Empty);
            if (string.IsNullOrEmpty(dataUrl)) return;

            _pendingAttachImageDataUrl = dataUrl;
            _pendingAttachName = SessionState.GetString(PendingAttachNameKey, string.Empty);
            EnsurePendingAttachTexture();
            RefreshAttachmentUI();
        }

        /// <summary>
        /// 有待发送图但没有预览纹理时（Domain Reload / OnDisable 释放后），按 data URL 重新解码。
        /// 解码失败只影响预览，不影响发送（发送用的是 data URL）。
        /// </summary>
        private void EnsurePendingAttachTexture()
        {
            if (_pendingAttachTexture != null || string.IsNullOrEmpty(_pendingAttachImageDataUrl)) return;
            try
            {
                var comma = _pendingAttachImageDataUrl.IndexOf(',');
                if (comma < 0) return;
                var bytes = Convert.FromBase64String(_pendingAttachImageDataUrl.Substring(comma + 1));
                var texture = new Texture2D(2, 2) { hideFlags = HideFlags.HideAndDontSave };
                if (!texture.LoadImage(bytes))
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                    return;
                }
                _pendingAttachTexture = texture;
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] Restore pending attachment preview failed: {ex.Message}");
            }
        }

        private void ReleasePendingAttachTexture()
        {
            if (_pendingAttachTexture == null) return;
            UnityEngine.Object.DestroyImmediate(_pendingAttachTexture);
            _pendingAttachTexture = null;
        }

        /// <summary>同步预览条与 + 按钮状态（有图时按钮高亮）。</summary>
        private void RefreshAttachmentUI()
        {
            bool has = !string.IsNullOrEmpty(_pendingAttachImageDataUrl);
            if (_attachmentPreview != null)
            {
                if (has && _pendingAttachTexture != null) _attachmentPreview.Show(_pendingAttachTexture, _pendingAttachName);
                else _attachmentPreview.Hide();
            }
            if (_attachButton != null)
            {
                _attachButton.EnableInClassList("has-attachment", has);
                _attachButton.tooltip = Loc.Tr("chat.attach.tooltip", "添加图片：上传文件，或截取 Game / Scene 视图");
            }
        }

        /// <summary>拖拽内容里的第一张 png/jpg（系统文件或 Project 窗口资源）；没有返回 null。</summary>
        private static string TryGetDraggedImagePath()
        {
            if (DragAndDrop.paths != null)
                foreach (var p in DragAndDrop.paths)
                    if (IsImagePath(p)) return p;
            if (DragAndDrop.objectReferences != null)
                foreach (var o in DragAndDrop.objectReferences)
                {
                    if (!(o is Texture2D)) continue;
                    var p = AssetDatabase.GetAssetPath(o);
                    if (IsImagePath(p)) return p;
                }
            return null;
        }

        private static bool IsImagePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg";
        }
    }
}
