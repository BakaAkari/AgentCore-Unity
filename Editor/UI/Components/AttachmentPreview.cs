using System;
using AgentCore.Editor.L10n;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Components
{
    /// <summary>
    /// v1.17.0: 输入框上方的待发送图片预览条——缩略图 + 名称/尺寸 + 移除按钮；
    /// 鼠标悬停缩略图时在其上方浮出大图预览。样式见 ChatWindow.uss 的 .agentcore-attachment-preview*。
    /// </summary>
    public sealed class AttachmentPreview : VisualElement
    {
        private readonly Image _thumb;
        private readonly Label _name;
        private readonly Label _meta;
        private readonly Button _remove;
        private readonly VisualElement _floating;
        private readonly Image _floatingImage;

        /// <summary>用户点了移除按钮。</summary>
        public event Action RemoveClicked;

        public AttachmentPreview()
        {
            AddToClassList("agentcore-attachment-preview");

            _thumb = new Image { scaleMode = ScaleMode.ScaleAndCrop };
            _thumb.AddToClassList("agentcore-attachment-preview__thumb");
            Add(_thumb);

            var texts = new VisualElement();
            texts.AddToClassList("agentcore-attachment-preview__texts");
            _name = new Label();
            _name.AddToClassList("agentcore-attachment-preview__name");
            _meta = new Label();
            _meta.AddToClassList("agentcore-attachment-preview__meta");
            texts.Add(_name);
            texts.Add(_meta);
            Add(texts);

            _remove = new Button(() => RemoveClicked?.Invoke()) { text = "✕" };
            _remove.AddToClassList("agentcore-attachment-preview__remove");
            Add(_remove);

            _floating = new VisualElement { pickingMode = PickingMode.Ignore };
            _floating.AddToClassList("agentcore-attachment-preview__floating");
            _floatingImage = new Image { scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            _floatingImage.AddToClassList("agentcore-attachment-preview__floating-image");
            _floating.Add(_floatingImage);
            Add(_floating);

            _thumb.RegisterCallback<MouseEnterEvent>(_ => _floating.style.display = DisplayStyle.Flex);
            _thumb.RegisterCallback<MouseLeaveEvent>(_ => _floating.style.display = DisplayStyle.None);

            Hide();
        }

        /// <summary>显示待发送图片。</summary>
        public void Show(Texture2D texture, string name)
        {
            _thumb.image = texture;
            _floatingImage.image = texture;
            _name.text = name;
            _name.tooltip = name;
            _meta.text = texture == null
                ? string.Empty
                : string.Format(Loc.Tr("chat.attach.pendingHint", "{0}×{1} · sent with your next message"), texture.width, texture.height);
            _remove.tooltip = Loc.Tr("chat.attach.remove", "Remove image");

            // 浮动大图按图片比例定高（宽固定 260）
            if (texture != null && texture.width > 0)
                _floatingImage.style.height = Mathf.Min(260f, 260f * texture.height / texture.width);

            _floating.style.display = DisplayStyle.None;
            style.display = DisplayStyle.Flex;
        }

        public void Hide()
        {
            _thumb.image = null;
            _floatingImage.image = null;
            _floating.style.display = DisplayStyle.None;
            style.display = DisplayStyle.None;
        }
    }
}
