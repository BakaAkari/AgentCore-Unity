using System.Collections.Generic;
using AgentCore.Editor.Config;
using AgentCore.Editor.L10n;
using UnityEditor;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Toolbar
{
    /// <summary>
    /// UI Toolkit 工具栏下拉控件, 用于切换当前 active Provider Profile (v1.13.0).
    /// </summary>
    /// <remarks>
    /// <para>
    /// 选项组成: 各 profile → 底部 "Manage Profiles..."。选中普通 profile 调
    /// <see cref="AgentCoreProviderProfiles.SetActive"/>; 选中 "Manage" 不切换,
    /// 而是打开 Project Settings 的 AgentCore 页, 并把下拉值 pop 回当前 active。
    /// </para>
    /// <para>
    /// 生命周期跟随 panel: AttachToPanelEvent 里订阅 <see cref="AgentCoreProviderProfiles.OnProfilesChanged"/>
    /// 与 <see cref="LanguageManager.LanguageChanged"/> (刷新 Manage 文案), DetachFromPanelEvent 里退订,
    /// 避免野悬挂。<c>_suppressCallback</c> 保护程序化 SetValue 不触发切换。
    /// 模板参照 <see cref="AgentCore.Editor.L10n.UI.LanguageSelector"/>。
    /// </para>
    /// </remarks>
    public sealed class ProviderProfileSelector : VisualElement
    {
        // Sentinel id for the "Manage Profiles..." item (never a valid guid).
        private const string ManageId = "__agentcore_manage__";

        private const string SettingsPath = "Project/AgentCore";

        private readonly PopupField<string> _popup;
        private readonly List<string> _labels = new List<string>();
        private readonly List<string> _ids = new List<string>();
        private bool _suppressCallback;

        public ProviderProfileSelector()
        {
            AddToClassList("agentcore-provider-profile-selector");
            style.flexDirection = FlexDirection.Row;
            style.alignItems = Align.Center;
            style.flexGrow = 0;
            style.flexShrink = 0;

            BuildOptions();

            var currentIndex = IndexOfActive();
            _popup = new PopupField<string>(new List<string>(_labels), currentIndex)
            {
                tooltip = Loc.Tr("providerProfiles.tooltip", "Switch active LLM provider profile"),
            };
            _popup.AddToClassList("agentcore-provider-profile-selector__popup");

            // PopupField 默认预留 label 空位, 手动隐藏。
            var labelEl = _popup.Q<Label>(className: "unity-base-field__label");
            if (labelEl != null)
                labelEl.style.display = DisplayStyle.None;

            // 固定宽度 (profile 名可能较长)。
            _popup.style.width = 160;
            _popup.style.minWidth = 160;
            _popup.style.maxWidth = 160;
            _popup.style.flexGrow = 0;
            _popup.style.flexShrink = 0;
            _popup.RegisterValueChangedCallback(OnPopupChanged);

            Add(_popup);

            RegisterCallback<AttachToPanelEvent>(OnAttach);
            RegisterCallback<DetachFromPanelEvent>(OnDetach);
        }

        private void OnAttach(AttachToPanelEvent evt)
        {
            AgentCoreProviderProfiles.OnProfilesChanged += OnProfilesChangedExternally;
            LanguageManager.LanguageChanged += OnLanguageChangedExternally;
            Rebuild();
        }

        private void OnDetach(DetachFromPanelEvent evt)
        {
            AgentCoreProviderProfiles.OnProfilesChanged -= OnProfilesChangedExternally;
            LanguageManager.LanguageChanged -= OnLanguageChangedExternally;
        }

        private void OnProfilesChangedExternally() => Rebuild();

        private void OnLanguageChangedExternally(string newLanguage) => Rebuild();

        private void OnPopupChanged(ChangeEvent<string> evt)
        {
            if (_suppressCallback)
                return;

            var index = _labels.IndexOf(evt.newValue);
            if (index < 0 || index >= _ids.Count)
                return;

            var id = _ids[index];

            if (id == ManageId)
            {
                // 不实际切换 profile: 打开设置页, 并把下拉值 pop 回当前 active。
                SettingsService.OpenProjectSettings(SettingsPath);
                SyncSelection();
                return;
            }

            var store = AgentCoreProviderProfiles.instance;
            if ((store.ActiveProfileId ?? string.Empty) == id)
                return;

            // SetActive 触发 OnProfilesChanged → Rebuild → SyncSelection。
            store.SetActive(id);
        }

        /// <summary>重建选项列表与选中项 (profile 增删/切换 active 或语言变化时)。</summary>
        private void Rebuild()
        {
            BuildOptions();
            _popup.choices = new List<string>(_labels);
            SyncSelection();
        }

        /// <summary>重算 <see cref="_labels"/> / <see cref="_ids"/> 平行列表。标签保证唯一以适配 PopupField。</summary>
        private void BuildOptions()
        {
            _labels.Clear();
            _ids.Clear();
            var seen = new HashSet<string>();

            var store = AgentCoreProviderProfiles.instance;
            var profiles = store.Profiles;
            if (profiles != null)
            {
                foreach (var p in profiles)
                {
                    if (p == null)
                        continue;
                    var name = string.IsNullOrWhiteSpace(p.displayName)
                        ? $"(unnamed {ShortId(p.id)})"
                        : p.displayName;
                    AddOption(p.id, name, seen);
                }
            }

            AddOption(ManageId, Loc.Tr("providerProfiles.manage", "Manage Profiles..."), seen);
        }

        private void AddOption(string id, string label, HashSet<string> seen)
        {
            var unique = label;
            int n = 2;
            while (!seen.Add(unique))
                unique = $"{label} ({n++})";

            _labels.Add(unique);
            _ids.Add(id);
        }

        private int IndexOfActive()
        {
            var active = AgentCoreProviderProfiles.instance.ActiveProfileId ?? string.Empty;
            var index = _ids.IndexOf(active);
            return index < 0 ? 0 : index; // fall back to first profile if active id not found
        }

        private void SyncSelection()
        {
            var index = IndexOfActive();
            if (index < 0 || index >= _labels.Count)
                return;

            var target = _labels[index];
            if (_popup.value == target)
                return;

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

        private static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id))
                return "?";
            return id.Length <= 8 ? id : id.Substring(0, 8);
        }
    }
}
