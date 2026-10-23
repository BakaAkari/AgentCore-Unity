using System;
using AgentCore.Editor.L10n;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Components
{
    /// <summary>
    /// v1.17.0: 输入区 + 按钮的"添加到消息"弹出菜单——上传图片 / 截取 Game 视图 / 截取 Scene 视图。
    /// 复用 <see cref="ModelSelector"/> 的弹出面板样式（ModelSelector.uss 的 agentcore-model-popup__*）。
    /// 动作由 ChatWindow 注入，本类只负责展示与可用性判断。
    /// </summary>
    internal sealed class AttachmentMenuPopup : PopupWindowContent
    {
        private const float Width = 260f;

        private readonly Action _upload;
        private readonly Action _captureGame;
        private readonly Action _captureScene;
        private readonly bool _gameAvailable;
        private readonly bool _sceneAvailable;

        public AttachmentMenuPopup(Action upload, Action captureGame, bool gameAvailable, Action captureScene, bool sceneAvailable)
        {
            _upload = upload;
            _captureGame = captureGame;
            _gameAvailable = gameAvailable;
            _captureScene = captureScene;
            _sceneAvailable = sceneAvailable;
        }

        public override Vector2 GetWindowSize() => new Vector2(Width, 22f + 3 * 34f + 8f + 30f);

        public override void OnGUI(Rect rect) { }

        public override void OnOpen()
        {
            var root = editorWindow.rootVisualElement;
            var uss = ModelSelector.LoadStyleSheet();
            if (uss != null) root.styleSheets.Add(uss);
            root.AddToClassList("agentcore-model-popup");
            root.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode != KeyCode.Escape) return;
                editorWindow.Close();
                evt.StopPropagation();
            });

            var section = new Label(Loc.Tr("chat.attach.menuTitle", "Add to message").ToUpperInvariant());
            section.AddToClassList("agentcore-model-popup__section");
            root.Add(section);

            var list = new VisualElement();
            list.AddToClassList("agentcore-model-popup__profiles");
            root.Add(list);

            list.Add(Item("d_RawImage Icon", "🖼",
                Loc.Tr("chat.attach.upload", "Upload image…"),
                Loc.Tr("chat.attach.uploadHint", "PNG / JPG"), true, _upload));
            list.Add(Item("d_UnityEditor.GameView", "▶",
                Loc.Tr("chat.attach.captureGame", "Capture Game view"),
                _gameAvailable ? Loc.Tr("chat.attach.captureGameHint", "Main camera") : Loc.Tr("chat.attach.noCamera", "No camera"),
                _gameAvailable, _captureGame));
            list.Add(Item("d_UnityEditor.SceneView", "◇",
                Loc.Tr("chat.attach.captureScene", "Capture Scene view"),
                _sceneAvailable ? Loc.Tr("chat.attach.captureSceneHint", "Current viewpoint") : Loc.Tr("chat.attach.noSceneView", "No Scene view open"),
                _sceneAvailable, _captureScene));

            var hint = new Label(Loc.Tr("chat.attach.dragHint", "Tip: you can also drag images onto the input box"));
            hint.AddToClassList("agentcore-model-popup__hint");
            root.Add(hint);
        }

        private VisualElement Item(string icon, string glyph, string title, string meta, bool enabled, Action act)
        {
            var row = new VisualElement();
            row.AddToClassList("agentcore-model-popup__row");
            row.AddToClassList("agentcore-model-popup__row--tall");

            var iconEl = new VisualElement();
            iconEl.AddToClassList("agentcore-model-popup__row-icon");
            var tex = EditorGUIUtility.FindTexture(icon);
            if (tex != null) iconEl.style.backgroundImage = tex;
            else
            {
                iconEl.AddToClassList("is-glyph");
                iconEl.Add(new Label(glyph));
            }
            row.Add(iconEl);

            var label = new Label(title);
            label.AddToClassList("agentcore-model-popup__row-text");
            row.Add(label);

            var metaLabel = new Label(meta);
            metaLabel.AddToClassList("agentcore-model-popup__row-meta");
            row.Add(metaLabel);

            if (enabled && act != null)
            {
                row.AddManipulator(new Clickable(() =>
                {
                    editorWindow.Close();
                    // 关闭弹窗后再执行：文件对话框 / 截图不应在 PopupWindow 生命周期内发生
                    EditorApplication.delayCall += () => act();
                }));
            }
            else
            {
                row.SetEnabled(false);
            }
            return row;
        }
    }
}
