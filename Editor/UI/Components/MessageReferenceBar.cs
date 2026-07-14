using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Components
{
    /// <summary>
    /// 消息底部资源引用栏。
    ///
    /// 在消息气泡底部渲染一排 chip 按钮，展示当前消息里提到的：
    ///   - 项目资源（Assets/Packages 路径 + 可选行号）→ 点击打开
    ///   - GameObject 引用（Hierarchy 路径 / 名称）→ 点击 Ping + 选中
    ///
    /// 由 <see cref="MessageBubble"/> 在 SetupStaticMode/FinalizeContent 时构建并添加。
    /// </summary>
    public class MessageReferenceBar : VisualElement
    {
        private static readonly Color ChipBg = new Color(0.20f, 0.28f, 0.42f);
        private static readonly Color ChipBgHover = new Color(0.26f, 0.36f, 0.55f);
        private static readonly Color ChipText = new Color(0.85f, 0.90f, 0.98f);
        private static readonly Color ChipBorder = new Color(0.35f, 0.45f, 0.65f);

        public MessageReferenceBar()
        {
            AddToClassList("message-reference-bar");
            style.flexDirection = FlexDirection.Row;
            style.flexWrap = Wrap.Wrap;
            style.marginTop = 4;
            style.marginBottom = 2;
            style.paddingTop = 4;
            style.borderTopWidth = 1;
            style.borderTopColor = new Color(0.30f, 0.30f, 0.30f);
        }

        /// <summary>
        /// 从消息内容重新构建 chip 列表。会清除现有 chip。
        /// </summary>
        public void Rebuild(string content)
        {
            Clear();

            var refs = MessageReferenceExtractor.Extract(content);
            if (refs == null || refs.Count == 0)
            {
                style.display = DisplayStyle.None;
                return;
            }

            style.display = DisplayStyle.Flex;

            var prefix = new Label("引用:");
            prefix.style.color = new Color(0.55f, 0.55f, 0.55f);
            prefix.style.fontSize = 10;
            prefix.style.marginRight = 6;
            prefix.style.alignSelf = Align.Center;
            Add(prefix);

            foreach (var r in refs)
            {
                Add(BuildChip(r));
            }
        }

        private VisualElement BuildChip(MessageReference r)
        {
            var chip = new Button(() => OnChipClicked(r))
            {
                text = FormatChipLabel(r)
            };
            chip.AddToClassList("message-reference-bar__chip");
            chip.tooltip = BuildTooltip(r);

            // 尺寸：足够点击 + 显示文本，避免被 Unity Button 默认 min-height 挤压成一条
            chip.style.minHeight = 22;
            chip.style.height = 22;
            chip.style.flexShrink = 1;
            chip.style.flexGrow = 0;
            chip.style.maxWidth = new Length(100, LengthUnit.Percent);

            // 内边距 + margin
            chip.style.marginRight = 6;
            chip.style.marginBottom = 4;
            chip.style.marginTop = 2;
            chip.style.marginLeft = 0;
            chip.style.paddingLeft = 10;
            chip.style.paddingRight = 10;
            chip.style.paddingTop = 0;
            chip.style.paddingBottom = 0;

            // 文本
            chip.style.fontSize = 11;
            chip.style.color = ChipText;
            chip.style.unityTextAlign = TextAnchor.MiddleCenter;
            chip.style.whiteSpace = WhiteSpace.Normal;
            chip.style.overflow = Overflow.Hidden;
            chip.style.textOverflow = TextOverflow.Ellipsis;

            // 背景 + 圆角
            chip.style.backgroundColor = ChipBg;
            chip.style.borderTopLeftRadius = 11;
            chip.style.borderTopRightRadius = 11;
            chip.style.borderBottomLeftRadius = 11;
            chip.style.borderBottomRightRadius = 11;
            chip.style.borderTopWidth = 1;
            chip.style.borderBottomWidth = 1;
            chip.style.borderLeftWidth = 1;
            chip.style.borderRightWidth = 1;
            chip.style.borderTopColor = ChipBorder;
            chip.style.borderBottomColor = ChipBorder;
            chip.style.borderLeftColor = ChipBorder;
            chip.style.borderRightColor = ChipBorder;

            chip.RegisterCallback<MouseEnterEvent>(_ => chip.style.backgroundColor = ChipBgHover);
            chip.RegisterCallback<MouseLeaveEvent>(_ => chip.style.backgroundColor = ChipBg);

            return chip;
        }

        private static string FormatChipLabel(MessageReference r)
        {
            // 用 ASCII 前缀避免 Unity 字体不支持 emoji（会渲染为方块 + 报错）
            switch (r.Kind)
            {
                case MessageReference.RefKind.Asset: return "[File] " + r.DisplayLabel;
                case MessageReference.RefKind.GameObject: return "[GO] " + r.DisplayLabel;
                default: return r.DisplayLabel;
            }
        }

        private static string BuildTooltip(MessageReference r)
        {
            switch (r.Kind)
            {
                case MessageReference.RefKind.Asset:
                    return r.Line > 0 ? $"打开 {r.Target}:{r.Line}" : $"打开 {r.Target}";
                case MessageReference.RefKind.GameObject:
                    return $"在 Hierarchy 中定位并高亮: {r.Target}";
                default:
                    return r.Target;
            }
        }

        private static void OnChipClicked(MessageReference r)
        {
            switch (r.Kind)
            {
                case MessageReference.RefKind.Asset: OpenAsset(r); break;
                case MessageReference.RefKind.GameObject: PingGameObject(r); break;
            }
        }

        private static void OpenAsset(MessageReference r)
        {
            var obj = AssetDatabase.LoadAssetAtPath<Object>(r.Target);
            if (obj == null)
            {
                Debug.LogWarning($"[AgentCore] 未找到资源: {r.Target}");
                return;
            }

            if (r.Line > 0)
                AssetDatabase.OpenAsset(obj, r.Line);
            else
                AssetDatabase.OpenAsset(obj);
        }

        private static void PingGameObject(MessageReference r)
        {
            // 先尝试完整路径匹配（Hierarchy path with slashes）
            GameObject go = null;
            if (r.Target.Contains("/"))
            {
                go = GameObject.Find(r.Target);
            }

            // fallback: 按名字在场景中查找
            if (go == null)
            {
                var lastSegment = r.Target.Contains("/")
                    ? r.Target.Substring(r.Target.LastIndexOf('/') + 1)
                    : r.Target;
                go = GameObject.Find(lastSegment);
            }

            if (go == null)
            {
                Debug.LogWarning($"[AgentCore] 未找到 GameObject: {r.Target}");
                return;
            }

            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
        }
    }
}
