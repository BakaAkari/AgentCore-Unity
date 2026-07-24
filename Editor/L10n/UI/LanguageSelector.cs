using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace AgentCore.Editor.L10n.UI
{
    /// <summary>
    /// UI Toolkit 语言选择下拉控件 (PopupField&lt;string&gt; 包装).
    /// </summary>
    /// <remarks>
    /// <para>
    /// 展示 <see cref="LanguageManager.SupportedLanguages"/> 里的语言, 选中后调用
    /// <see cref="LanguageManager.SetLanguage"/> 触发热切换. 自动订阅
    /// <see cref="LanguageManager.LanguageChanged"/>, 语言在其他地方切换时同步下拉框选中项.
    /// </para>
    /// <para>
    /// 使用: <c>new LanguageSelector()</c> 直接添加到任意 VisualElement. 组件生命周期跟随 panel,
    /// AttachToPanelEvent/DetachFromPanelEvent 里挂/摘事件订阅, 避免野悬挂.
    /// </para>
    /// </remarks>
    public sealed class LanguageSelector : VisualElement
    {
        private readonly PopupField<string> _popup;
        private readonly List<string> _codes;
        private readonly List<string> _displayNames;
        private bool _suppressCallback;

        public LanguageSelector()
        {
            AddToClassList("agentcore-language-selector");
            style.flexDirection = FlexDirection.Row;
            style.alignItems = Align.Center;

            _codes = new List<string>();
            _displayNames = new List<string>();
            foreach (var opt in LanguageManager.SupportedLanguages)
            {
                _codes.Add(opt.Code);
                _displayNames.Add(opt.DisplayName);
            }

            var currentIndex = _codes.IndexOf(LanguageManager.CurrentLanguage);
            if (currentIndex < 0) currentIndex = 0;

            _popup = new PopupField<string>(_displayNames, currentIndex)
            {
                tooltip = Loc.Tr("language.tooltip", "Switch UI language"),
            };
            _popup.AddToClassList("agentcore-language-selector__popup");
            _popup.style.minWidth = 96;
            _popup.RegisterValueChangedCallback(OnPopupChanged);

            Add(_popup);

            RegisterCallback<AttachToPanelEvent>(OnAttach);
            RegisterCallback<DetachFromPanelEvent>(OnDetach);
        }

        private void OnAttach(AttachToPanelEvent evt)
        {
            LanguageManager.LanguageChanged += OnLanguageChangedExternally;
            SyncSelection();
        }

        private void OnDetach(DetachFromPanelEvent evt)
        {
            LanguageManager.LanguageChanged -= OnLanguageChangedExternally;
        }

        private void OnPopupChanged(ChangeEvent<string> evt)
        {
            if (_suppressCallback) return;

            var index = _displayNames.IndexOf(evt.newValue);
            if (index < 0 || index >= _codes.Count) return;

            var code = _codes[index];
            if (code == LanguageManager.CurrentLanguage) return;

            LanguageManager.SetLanguage(code);
        }

        private void OnLanguageChangedExternally(string newLanguage)
        {
            SyncSelection();
        }

        private void SyncSelection()
        {
            var index = _codes.IndexOf(LanguageManager.CurrentLanguage);
            if (index < 0 || index >= _displayNames.Count) return;

            var target = _displayNames[index];
            if (_popup.value == target) return;

            _suppressCallback = true;
            try
            {
                _popup.SetValueWithoutNotify(target);
            }
            finally
            {
                _suppressCallback = false;
            }
        }
    }
}
