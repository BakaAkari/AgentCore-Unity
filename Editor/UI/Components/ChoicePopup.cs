using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Components
{
    /// <summary>
    /// v1.17.0: 通用单选弹出面板（与 <see cref="ModelSelectorPopup"/> 同一套样式：ModelSelector.uss 的 agentcore-model-popup__*）。
    /// 标题 + 副标题、每项"名称 + 右侧说明"、当前项 ✓ 高亮；↑/↓ 移动（自动跳过禁用项）、Enter 选中、Esc 关闭。
    /// </summary>
    internal sealed class ChoicePopup : PopupWindowContent
    {
        internal sealed class Item
        {
            public string Label;
            public string Hint;
            public bool Selected;
            public bool Disabled;
        }

        private const float RowHeight = 24f;

        private readonly string _title;
        private readonly string _subtitle;
        private readonly IReadOnlyList<Item> _items;
        private readonly Action<int> _onPick;
        private readonly float _width;
        private readonly Action _onClosed;
        private readonly List<VisualElement> _rows = new();
        private int _highlight = -1;

        public ChoicePopup(string title, string subtitle, IReadOnlyList<Item> items, Action<int> onPick, float width, Action onClosed = null)
        {
            _title = title;
            _subtitle = subtitle;
            _items = items ?? Array.Empty<Item>();
            _onPick = onPick;
            _width = width;
            _onClosed = onClosed;
        }

        public override Vector2 GetWindowSize() => new Vector2(_width, 44f + 6f + _items.Count * RowHeight + 8f);

        public override void OnGUI(Rect rect) { }

        public override void OnClose() => _onClosed?.Invoke();

        public override void OnOpen()
        {
            var root = editorWindow.rootVisualElement;
            var uss = ModelSelector.LoadStyleSheet();
            if (uss != null) root.styleSheets.Add(uss);
            root.AddToClassList("agentcore-model-popup");
            root.focusable = true;
            root.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);

            var header = new VisualElement();
            header.AddToClassList("agentcore-model-popup__header");
            var titles = new VisualElement();
            titles.AddToClassList("agentcore-model-popup__titles");
            var title = new Label(_title);
            title.AddToClassList("agentcore-model-popup__title");
            var sub = new Label(_subtitle);
            sub.AddToClassList("agentcore-model-popup__subtitle");
            titles.Add(title);
            titles.Add(sub);
            header.Add(titles);
            root.Add(header);

            var list = new VisualElement();
            list.AddToClassList("agentcore-model-popup__profiles");
            list.style.marginTop = 6;
            root.Add(list);

            for (int i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                var row = new VisualElement();
                row.AddToClassList("agentcore-model-popup__row");
                if (item.Selected)
                {
                    row.AddToClassList("is-current");
                    if (!item.Disabled) _highlight = i;
                }
                if (item.Disabled)
                {
                    row.AddToClassList("is-disabled");
                }

                var check = new Label(item.Selected ? "✓" : string.Empty);
                check.AddToClassList("agentcore-model-popup__check");
                row.Add(check);

                var label = new Label(item.Label);
                label.AddToClassList("agentcore-model-popup__row-text");
                row.Add(label);

                if (!string.IsNullOrEmpty(item.Hint))
                {
                    var hint = new Label(item.Hint);
                    hint.AddToClassList("agentcore-model-popup__row-meta");
                    row.Add(hint);
                }

                if (!item.Disabled)
                {
                    int index = i;
                    row.AddManipulator(new Clickable(() => Pick(index)));
                }

                list.Add(row);
                _rows.Add(row);
            }

            root.schedule.Execute(() => root.Focus());
        }

        /// <summary>校验某索引对应的条目是否可选（未越界且未禁用）。</summary>
        internal static bool CanPick(IReadOnlyList<Item> items, int index)
        {
            return items != null
                && index >= 0
                && index < items.Count
                && items[index] != null
                && !items[index].Disabled;
        }

        /// <summary>
        /// 按键盘方向查找下一个非禁用条目索引；全部禁用或空列表时返回 -1。
        /// </summary>
        /// <param name="items">条目列表。</param>
        /// <param name="currentIndex">当前高亮索引（-1 表示尚未高亮）。</param>
        /// <param name="direction">移动方向：&gt;=0 向下，&lt;0 向上。</param>
        internal static int FindNextEnabledIndex(IReadOnlyList<Item> items, int currentIndex, int direction)
        {
            if (items == null || items.Count == 0) return -1;

            int count = items.Count;
            int step = direction >= 0 ? 1 : -1;
            int start;
            if (currentIndex < 0 || currentIndex >= count)
            {
                start = step > 0 ? 0 : count - 1;
            }
            else
            {
                start = (currentIndex + step + count) % count;
            }

            for (int tried = 0; tried < count; tried++)
            {
                int idx = (start + tried * step + count * 2) % count;
                if (CanPick(items, idx)) return idx;
            }

            return -1;
        }

        /// <summary>尝试选中指定索引；越界或禁用时拦截并返回 false（不关闭弹窗，不触发回调）。</summary>
        internal bool TryPick(int index)
        {
            if (!CanPick(_items, index)) return false;
            editorWindow?.Close();
            _onPick?.Invoke(index);
            return true;
        }

        private void Pick(int index) => TryPick(index);

        private void OnKeyDown(KeyDownEvent evt)
        {
            switch (evt.keyCode)
            {
                case KeyCode.Escape:
                    editorWindow.Close();
                    break;
                case KeyCode.DownArrow:
                    SetHighlight(FindNextEnabledIndex(_items, _highlight, +1));
                    break;
                case KeyCode.UpArrow:
                    SetHighlight(FindNextEnabledIndex(_items, _highlight, -1));
                    break;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    if (CanPick(_items, _highlight)) Pick(_highlight);
                    break;
                default:
                    return;
            }
            evt.StopPropagation();
            evt.PreventDefault();
        }

        private void SetHighlight(int index)
        {
            foreach (var r in _rows) r.RemoveFromClassList("is-highlighted");
            _highlight = CanPick(_items, index) ? index : -1;
            if (_highlight >= 0 && _highlight < _rows.Count) _rows[_highlight].AddToClassList("is-highlighted");
        }
    }
}
