using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using AgentCore.Editor.Utils;

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
            // 关键：引用栏本身不得超出父气泡宽度。flexShrink=1 + maxWidth=100%
            // 让整条栏在窄气泡里收缩到容器内，chip 靠 flexWrap 换行而非溢出。
            style.flexShrink = 1;
            style.maxWidth = new Length(100, LengthUnit.Percent);
            style.overflow = Overflow.Hidden;
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
            prefix.style.flexShrink = 0;   // 短标签固定，不参与收缩
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

            // 尺寸：用 minHeight 保证可点击高度，但不锁死 height —— 让高度由内容
            // （文本行高 + 上下 border）自然撑开。此前固定 height:22 + overflow:hidden
            // 会把超出 22px 的文本上下裁掉，导致 [File]/[GO] 图标字形被截成一半。
            chip.style.minHeight = 22;
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
            // 垂直 padding 给文本行高留出空间，避免贴着上下 border 显得被切
            chip.style.paddingTop = 2;
            chip.style.paddingBottom = 2;

            // 文本
            chip.style.fontSize = 11;
            chip.style.color = ChipText;
            chip.style.unityTextAlign = TextAnchor.MiddleCenter;
            // NoWrap + Ellipsis：长文件路径在窄气泡里截断为省略号，
            // 而不是换行把 chip 撑高/撑出气泡（配合固定 height:22 与 overflow:hidden）。
            chip.style.whiteSpace = WhiteSpace.NoWrap;
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
                AgentCoreLog.Warning($"[AgentCore] 未找到资源: {r.Target}");
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
                AgentCoreLog.Warning($"[AgentCore] 未找到 GameObject: {r.Target}");
                return;
            }

            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
        }
    }
}
