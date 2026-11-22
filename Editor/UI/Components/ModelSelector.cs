using System;
using System.Collections.Generic;
using AgentCore.Editor.Config;
using AgentCore.Editor.L10n;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Components
{
    /// <summary>
    /// v1.17.0: chat 面板输入区的模型切换下拉。胶囊按钮显示当前 active profile 的模型名，点击弹出
    /// <see cref="ModelSelectorPopup"/>（UI Toolkit 自绘，深色主题，带搜索/分区/当前项高亮）：
    /// <list type="bullet">
    /// <item>当前 profile 的可用模型（<see cref="ProviderModelCatalog"/>，首次挂载时后台拉取）——选中即记录为本用户对该 profile 的
    /// 模型（<see cref="AgentCoreProviderProfiles.SetUserModel"/>，保存在本地 UserSettings）；</item>
    /// <item>刷新模型列表；</item>
    /// <item>切换到其他 profile；打开设置页管理 profile。</item>
    /// </list>
    /// <para>
    /// 显示的模型为 <see cref="AgentCoreProviderProfiles.GetEffectiveModelName"/>。模型/profile 状态与设置页同一份 <see cref="AgentCoreProviderProfiles"/>，靠
    /// <see cref="AgentCoreProviderProfiles.OnProfilesChanged"/> 与 <see cref="ProviderModelCatalog.OnCatalogChanged"/> 同步显示。
    /// Agent 运行中禁止切换（工具循环中途换模型会使 thinking 回放与缓存失效），由 <see cref="IsBusy"/> 判定。
    /// </para>
    /// </summary>
    public sealed class ModelSelector : VisualElement
    {
        internal const string StyleSheetPath = "Packages/com.agentcore.unity/Editor/UI/Components/ModelSelector.uss";

        private readonly VisualElement _dot;
        private readonly Label _label;

        /// <summary>Agent 是否正在运行（运行中只展示列表、不允许切换）。由 ChatWindow 注入。</summary>
        public Func<bool> IsBusy { get; set; }

        public ModelSelector()
        {
            AddToClassList("agentcore-model-selector");
            var uss = LoadStyleSheet();
            if (uss != null) styleSheets.Add(uss);

            _dot = new VisualElement();
            _dot.AddToClassList("agentcore-model-selector__dot");
            _label = new Label();
            _label.AddToClassList("agentcore-model-selector__label");
            var arrow = new VisualElement();
            arrow.AddToClassList("unity-base-popup-field__arrow");
            arrow.AddToClassList("agentcore-model-selector__arrow");
            Add(_dot);
            Add(_label);
            Add(arrow);

            this.AddManipulator(new Clickable(ShowPopup));
            RegisterCallback<AttachToPanelEvent>(OnAttach);
            RegisterCallback<DetachFromPanelEvent>(OnDetach);
        }

        internal static StyleSheet LoadStyleSheet() => AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);

        private void OnAttach(AttachToPanelEvent evt)
        {
            AgentCoreProviderProfiles.OnProfilesChanged += OnProfilesChanged;
            ProviderModelCatalog.OnCatalogChanged += OnCatalogChanged;
            LanguageManager.LanguageChanged += OnLanguageChanged;
            Refresh();
            EnsureCatalog();
        }

        private void OnDetach(DetachFromPanelEvent evt)
        {
            AgentCoreProviderProfiles.OnProfilesChanged -= OnProfilesChanged;
            ProviderModelCatalog.OnCatalogChanged -= OnCatalogChanged;
            LanguageManager.LanguageChanged -= OnLanguageChanged;
        }

        private void OnProfilesChanged()
        {
            Refresh();
            EnsureCatalog();
        }

        private void OnCatalogChanged(string profileId) => Refresh();

        private void OnLanguageChanged(string language) => Refresh();

        /// <summary>当前 profile 本会话尚未拉取过模型列表时后台拉取一次。</summary>
        private static void EnsureCatalog()
        {
            var active = AgentCoreProviderProfiles.instance.GetActive();
            if (active == null || ProviderModelCatalog.HasFetched(active.id) || ProviderModelCatalog.IsFetching(active.id))
                return;
            _ = ProviderModelCatalog.FetchAsync(active.id);
        }

        /// <summary>刷新胶囊文字、状态点与 tooltip。</summary>
        public void Refresh()
        {
            var store = AgentCoreProviderProfiles.instance;
            var active = store.GetActive();
            var effectiveModel = store.GetEffectiveModelName(active);
            _dot.EnableInClassList("is-ready", active != null && !string.IsNullOrWhiteSpace(effectiveModel));
            _dot.EnableInClassList("is-loading", active != null && ProviderModelCatalog.IsFetching(active.id));

            if (active == null)
            {
                _label.text = Loc.Tr("chat.model.none", "No model");
                tooltip = Loc.Tr("chat.model.noneTooltip", "No provider profile configured. Click to manage profiles.");
                return;
            }

            var model = string.IsNullOrWhiteSpace(effectiveModel) ? Loc.Tr("chat.model.unset", "(no model)") : effectiveModel;
            _label.text = ShortName(model);
            tooltip = string.Format(
                Loc.Tr("chat.model.tooltip", "Model: {0}\nProfile: {1}\nClick to switch model or profile"),
                model, ProfileName(active));
            tooltip += "\n" + Loc.Tr("chat.model.localTooltip", "Saved locally in UserSettings (personal only)");
        }

        private void ShowPopup()
        {
            if (AgentCoreProviderProfiles.instance.GetActive() == null)
            {
                AgentCoreSettingsProvider.OpenPreferences(AgentCoreSettingsProvider.ModelAgentSectionId);
                return;
            }
            AddToClassList("is-open");
            UnityEditor.PopupWindow.Show(worldBound,
                new ModelSelectorPopup(IsBusy?.Invoke() ?? false, worldBound.width, () => RemoveFromClassList("is-open")));
        }

        /// <summary>胶囊里只显示 <c>vendor/</c> 之后的部分（OpenRouter 等聚合端点的模型名很长）；完整名在 tooltip。</summary>
        internal static string ShortName(string model)
        {
            if (string.IsNullOrEmpty(model)) return model;
            int slash = model.LastIndexOf('/');
            return slash >= 0 && slash < model.Length - 1 ? model.Substring(slash + 1) : model;
        }

        internal static string ProfileName(ProviderProfile p)
            => string.IsNullOrWhiteSpace(p.displayName) ? Loc.Tr("chat.model.unnamedProfile", "(unnamed profile)") : p.displayName;
    }

    /// <summary>
    /// <see cref="ModelSelector"/> 的弹出面板：顶部 profile 标题 + 刷新、搜索框（模型多时）、模型列表、其他 profile、管理入口。
    /// 支持 ↑/↓ 移动高亮、Enter 选中、Esc 关闭。
    /// </summary>
    internal sealed class ModelSelectorPopup : PopupWindowContent
    {
        private const float MinWidth = 300f;
        private const float RowHeight = 24f;
        private const int SearchThreshold = 8;
        private const int MaxVisibleModelRows = 12;

        private readonly bool _busy;
        private readonly float _anchorWidth;
        private readonly Action _onClosed;

        private TextField _search;
        private Label _searchPlaceholder;
        private ScrollView _modelList;
        private Button _refreshButton;
        private readonly List<VisualElement> _navRows = new();
        private readonly List<Action> _navActions = new();
        private int _highlight = -1;

        public ModelSelectorPopup(bool busy, float anchorWidth, Action onClosed)
        {
            _busy = busy;
            _anchorWidth = anchorWidth;
            _onClosed = onClosed;
        }

        private static ProviderProfile Active => AgentCoreProviderProfiles.instance.GetActive();

        private static string EffectiveModel(ProviderProfile p) => AgentCoreProviderProfiles.instance.GetEffectiveModelName(p);

        private static List<string> ModelsOf(ProviderProfile active)
        {
            var models = ProviderModelCatalog.Get(active.id);
            // 当前模型保证出现在列表里（目录未拉取 / 端点未返回时也能看到）
            var effective = EffectiveModel(active);
            if (!string.IsNullOrWhiteSpace(effective) && !models.Contains(effective))
                models.Insert(0, effective);
            return models;
        }

        public override Vector2 GetWindowSize()
        {
            var active = Active;
            int models = active == null ? 0 : ModelsOf(active).Count;
            int profiles = CountProfiles();
            float h = 44f;                                                        // 标题
            if (_busy) h += 30f;                                                  // 运行中提示
            if (models > SearchThreshold) h += 34f;                               // 搜索框
            h += 22f + Mathf.Clamp(Math.Max(models, 1), 1, MaxVisibleModelRows) * RowHeight + 8f;
            if (profiles > 1) h += 22f + profiles * RowHeight + 8f;
            h += 36f;                                                             // 底部管理入口
            return new Vector2(Mathf.Max(MinWidth, _anchorWidth), h);
        }

        public override void OnGUI(Rect rect) { }

        public override void OnOpen()
        {
            var root = editorWindow.rootVisualElement;
            var uss = ModelSelector.LoadStyleSheet();
            if (uss != null) root.styleSheets.Add(uss);
            root.AddToClassList("agentcore-model-popup");
            root.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);

            var active = Active;
            root.Add(BuildHeader(active));

            if (_busy)
            {
                var banner = new Label(Loc.Tr("chat.model.busy", "Can't switch while the agent is running"));
                banner.AddToClassList("agentcore-model-popup__banner");
                root.Add(banner);
            }

            if (active != null && ModelsOf(active).Count > SearchThreshold)
                root.Add(BuildSearch());

            root.Add(SectionLabel(Loc.Tr("chat.model.models", "Models")));
            _modelList = new ScrollView(ScrollViewMode.Vertical);
            _modelList.AddToClassList("agentcore-model-popup__list");
            root.Add(_modelList);

            if (CountProfiles() > 1)
            {
                root.Add(SectionLabel(Loc.Tr("chat.model.switchProfile", "Switch profile")));
                var profileList = new VisualElement();
                profileList.AddToClassList("agentcore-model-popup__profiles");
                root.Add(profileList);
                BuildProfiles(profileList, active);
            }

            root.Add(BuildFooter());

            RebuildModels();
            ProviderModelCatalog.OnCatalogChanged += OnCatalogChanged;

            if (_search != null)
                _search.schedule.Execute(() => _search.Focus());
            else
                root.focusable = true;
        }

        public override void OnClose()
        {
            ProviderModelCatalog.OnCatalogChanged -= OnCatalogChanged;
            _onClosed?.Invoke();
        }

        private void OnCatalogChanged(string profileId)
        {
            var active = Active;
            if (active == null || active.id != profileId) return;
            RebuildModels();
        }

        // ── 构建 ─────────────────────────────────────────────────────────

        private VisualElement BuildHeader(ProviderProfile active)
        {
            var header = new VisualElement();
            header.AddToClassList("agentcore-model-popup__header");

            var titles = new VisualElement();
            titles.AddToClassList("agentcore-model-popup__titles");
            var name = new Label(active != null ? ModelSelector.ProfileName(active) : string.Empty);
            name.AddToClassList("agentcore-model-popup__title");
            var host = new Label(HostOf(active?.endpoint));
            host.AddToClassList("agentcore-model-popup__subtitle");
            titles.Add(name);
            titles.Add(host);
            header.Add(titles);

            _refreshButton = new Button(() =>
            {
                var a = Active;
                if (a != null) _ = ProviderModelCatalog.FetchAsync(a.id);
            });
            _refreshButton.AddToClassList("agentcore-model-popup__icon-button");
            _refreshButton.tooltip = Loc.Tr("chat.model.refresh", "Refresh model list");
            SetIcon(_refreshButton, "d_Refresh", "↻");
            header.Add(_refreshButton);
            return header;
        }

        private VisualElement BuildSearch()
        {
            var wrap = new VisualElement();
            wrap.AddToClassList("agentcore-model-popup__search");

            var icon = new VisualElement();
            icon.AddToClassList("agentcore-model-popup__search-icon");
            SetIcon(icon, "d_Search Icon", null);
            wrap.Add(icon);

            _search = new TextField();
            _search.AddToClassList("agentcore-model-popup__search-field");
            _search.RegisterValueChangedCallback(_ => RebuildModels());
            wrap.Add(_search);

            // Unity 2021.3 的 TextField 没有 placeholder，用叠加 Label 模拟
            _searchPlaceholder = new Label(Loc.Tr("chat.model.search", "Filter models…"));
            _searchPlaceholder.AddToClassList("agentcore-model-popup__search-placeholder");
            _searchPlaceholder.pickingMode = PickingMode.Ignore;
            wrap.Add(_searchPlaceholder);
            return wrap;
        }

        private void BuildProfiles(VisualElement container, ProviderProfile active)
        {
            foreach (var p in AgentCoreProviderProfiles.instance.Profiles)
            {
                if (p == null) continue;
                bool current = active != null && p.id == active.id;
                var row = Row(current);

                var dot = new VisualElement();
                dot.AddToClassList("agentcore-model-popup__profile-dot");
                row.Add(dot);

                var label = new Label(ModelSelector.ProfileName(p));
                label.AddToClassList("agentcore-model-popup__row-text");
                row.Add(label);

                var profileModel = EffectiveModel(p);
                if (!string.IsNullOrWhiteSpace(profileModel))
                {
                    var model = new Label(ModelSelector.ShortName(profileModel));
                    model.AddToClassList("agentcore-model-popup__row-meta");
                    row.Add(model);
                }

                row.tooltip = string.IsNullOrWhiteSpace(p.endpoint) ? profileModel : profileModel + "\n" + p.endpoint;
                var id = p.id;
                AddNavigable(container, row, current || _busy ? null : () =>
                {
                    AgentCoreProviderProfiles.instance.SetActive(id);
                    editorWindow.Close();
                });
            }
        }

        private VisualElement BuildFooter()
        {
            var footer = new VisualElement();
            footer.AddToClassList("agentcore-model-popup__footer");
            var row = Row(false);
            var icon = new VisualElement();
            icon.AddToClassList("agentcore-model-popup__row-icon");
            SetIcon(icon, "d_Settings", "⚙");
            row.Add(icon);
            var label = new Label(Loc.Tr("providerProfiles.manage", "Manage Profiles..."));
            label.AddToClassList("agentcore-model-popup__row-text");
            row.Add(label);
            AddNavigable(footer, row, () =>
            {
                editorWindow.Close();
                AgentCoreSettingsProvider.OpenPreferences(AgentCoreSettingsProvider.ModelAgentSectionId);
            });
            return footer;
        }

        /// <summary>重建模型列表（打开时、搜索变化、目录拉取完成）。导航行顺序：模型 → profile → 管理，模型段需插在最前。</summary>
        private void RebuildModels()
        {
            var active = Active;
            // 去掉旧的模型行（导航表里模型段在最前）
            for (int i = _navRows.Count - 1; i >= 0; i--)
            {
                if (_navRows[i].parent != _modelList.contentContainer) continue;
                _navRows.RemoveAt(i);
                _navActions.RemoveAt(i);
            }
            _modelList.Clear();
            _highlight = -1;

            bool fetching = active != null && ProviderModelCatalog.IsFetching(active.id);
            _refreshButton?.SetEnabled(active != null && !fetching);
            if (active == null) return;

            var filter = _search?.value?.Trim() ?? string.Empty;
            if (_searchPlaceholder != null)
                _searchPlaceholder.style.display = filter.Length == 0 ? DisplayStyle.Flex : DisplayStyle.None;

            var models = ModelsOf(active);
            var effectiveModel = EffectiveModel(active);
            var newRows = new List<VisualElement>();
            var newActions = new List<Action>();
            VisualElement selectedRow = null;
            foreach (var m in models)
            {
                if (!Matches(m, filter)) continue;
                bool current = m == effectiveModel;
                var row = Row(current);

                var check = new Label(current ? "✓" : string.Empty);
                check.AddToClassList("agentcore-model-popup__check");
                row.Add(check);

                int slash = m.LastIndexOf('/');
                if (slash > 0 && slash < m.Length - 1)
                {
                    var vendor = new Label(m.Substring(0, slash + 1));
                    vendor.AddToClassList("agentcore-model-popup__vendor");
                    row.Add(vendor);
                }
                var label = new Label(slash > 0 && slash < m.Length - 1 ? m.Substring(slash + 1) : m);
                label.AddToClassList("agentcore-model-popup__row-text");
                row.Add(label);
                row.tooltip = m;

                var model = m;
                var id = active.id;
                Action act = _busy ? null : () =>
                {
                    SelectModel(id, model);
                    editorWindow.Close();
                };
                _modelList.Add(row);
                if (act != null)
                {
                    row.AddManipulator(new Clickable(act));
                    newRows.Add(row);
                    newActions.Add(act);
                }
                else
                {
                    row.SetEnabled(false);
                }
                if (current) selectedRow = row;
            }

            if (fetching)
                _modelList.Add(Notice(Loc.Tr("chat.model.loading", "Loading model list…")));
            else if (newRows.Count == 0 && models.Count == 0 && ProviderModelCatalog.HasFetched(active.id))
                _modelList.Add(Notice(Loc.Tr("chat.model.empty", "No models returned by the endpoint")));
            else if (models.Count > 0 && _modelList.childCount == 0)
                _modelList.Add(Notice(Loc.Tr("chat.model.noMatch", "No matching models")));

            _navRows.InsertRange(0, newRows);
            _navActions.InsertRange(0, newActions);

            if (filter.Length > 0 && newRows.Count > 0)
                SetHighlight(0);
            else if (selectedRow != null)
                _modelList.schedule.Execute(() => _modelList.ScrollTo(selectedRow));
        }

        // ── 交互 ─────────────────────────────────────────────────────────

        private void OnKeyDown(KeyDownEvent evt)
        {
            switch (evt.keyCode)
            {
                case KeyCode.Escape:
                    editorWindow.Close();
                    break;
                case KeyCode.DownArrow:
                    SetHighlight(_navRows.Count == 0 ? -1 : (_highlight + 1) % _navRows.Count);
                    break;
                case KeyCode.UpArrow:
                    SetHighlight(_navRows.Count == 0 ? -1 : (_highlight <= 0 ? _navRows.Count : _highlight) - 1);
                    break;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    if (_highlight >= 0 && _highlight < _navActions.Count) _navActions[_highlight]();
                    break;
                default:
                    return;
            }
            evt.StopPropagation();
            evt.PreventDefault();
        }

        private void SetHighlight(int index)
        {
            if (_highlight >= 0 && _highlight < _navRows.Count)
                _navRows[_highlight].RemoveFromClassList("is-highlighted");
            _highlight = index;
            if (index < 0 || index >= _navRows.Count) return;
            var row = _navRows[index];
            row.AddToClassList("is-highlighted");
            if (row.parent == _modelList.contentContainer) _modelList.ScrollTo(row);
        }

        private void AddNavigable(VisualElement container, VisualElement row, Action act)
        {
            container.Add(row);
            if (act == null)
            {
                if (_busy) row.SetEnabled(false);
                return;
            }
            row.AddManipulator(new Clickable(act));
            _navRows.Add(row);
            _navActions.Add(act);
        }

        /// <summary>
        /// 选中模型：保存到本地 UserSettings，不改 ProjectSettings。
        /// </summary>
        private static void SelectModel(string profileId, string model)
        {
            var store = AgentCoreProviderProfiles.instance;
            var p = store.FindById(profileId);
            if (p == null || store.GetEffectiveModelName(p) == model) return;
            store.SetUserModel(profileId, model);
        }

        // ── 小工具 ───────────────────────────────────────────────────────

        private static VisualElement Row(bool current)
        {
            var row = new VisualElement();
            row.AddToClassList("agentcore-model-popup__row");
            if (current) row.AddToClassList("is-current");
            return row;
        }

        private static Label SectionLabel(string text)
        {
            var l = new Label(text.ToUpperInvariant());
            l.AddToClassList("agentcore-model-popup__section");
            return l;
        }

        private static Label Notice(string text)
        {
            var l = new Label(text);
            l.AddToClassList("agentcore-model-popup__notice");
            return l;
        }

        private static int CountProfiles()
        {
            int n = 0;
            var profiles = AgentCoreProviderProfiles.instance.Profiles;
            if (profiles != null)
                foreach (var p in profiles) if (p != null) n++;
            return n;
        }

        /// <summary>空格分隔的多个关键词都需命中（不区分大小写）。</summary>
        private static bool Matches(string model, string filter)
        {
            if (filter.Length == 0) return true;
            foreach (var token in filter.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (model.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0) return false;
            return true;
        }

        private static string HostOf(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) return string.Empty;
            return Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) ? uri.Host : endpoint.Trim();
        }

        /// <summary>优先用编辑器内置图标；取不到时退回文字字形。</summary>
        private static void SetIcon(VisualElement target, string iconName, string fallbackGlyph)
        {
            var tex = EditorGUIUtility.FindTexture(iconName);
            if (tex != null)
            {
                target.style.backgroundImage = tex;
                if (target is Button b) b.text = string.Empty;
                return;
            }
            target.AddToClassList("is-glyph");
            if (target is TextElement te) te.text = fallbackGlyph ?? string.Empty;
            else if (!string.IsNullOrEmpty(fallbackGlyph)) target.Add(new Label(fallbackGlyph));
        }
    }
}
