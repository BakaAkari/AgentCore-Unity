using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Components
{
    /// <summary>
    /// v1.17.0: 通用单选弹出面板（与 <see cref="ModelSelectorPopup"/> 同一套样式：ModelSelector.uss 的 agentcore-model-popup__*）。
    /// 标题 + 副标题、每项"名称 + 右侧说明"、当前项 ✓ 高亮；↑/↓ 移动、Enter 选中、Esc 关闭。
    /// </summary>
    internal sealed class ChoicePopup : PopupWindowContent
    {
        internal sealed class Item
        {
            public string Label;
            public string Hint;
            public bool Selected;
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
            _items = items;
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
                    _highlight = i;
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

                int index = i;
                row.AddManipulator(new Clickable(() => Pick(index)));
                list.Add(row);
                _rows.Add(row);
            }

            root.schedule.Execute(() => root.Focus());
        }

        private void Pick(int index)
        {
            editorWindow.Close();
            _onPick?.Invoke(index);
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            switch (evt.keyCode)
            {
                case KeyCode.Escape:
                    editorWindow.Close();
                    break;
                case KeyCode.DownArrow:
                    SetHighlight(_rows.Count == 0 ? -1 : (_highlight + 1) % _rows.Count);
                    break;
                case KeyCode.UpArrow:
                    SetHighlight(_rows.Count == 0 ? -1 : (_highlight <= 0 ? _rows.Count : _highlight) - 1);
                    break;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    if (_highlight >= 0 && _highlight < _rows.Count) Pick(_highlight);
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
            _highlight = index;
            if (index >= 0 && index < _rows.Count) _rows[index].AddToClassList("is-highlighted");
        }
    }
}
