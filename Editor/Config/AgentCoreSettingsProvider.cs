using System;
using System.Collections.Generic;
using System.Linq;
using AgentCore.Editor.Config.Settings;
using AgentCore.Editor.Config.Settings.Pages;
using AgentCore.Editor.Extensions;
using AgentCore.Editor.L10n;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.Config
{
    /// <summary>
    /// Provides the AgentCore Preferences hub with top-tab navigation.
    /// </summary>
    public class AgentCoreSettingsProvider : SettingsProvider
    {
        /// <summary>Preferences path of the AgentCore settings hub.</summary>
        public const string SettingsPath = "Preferences/AgentCore";

        /// <summary>Section id of the Dashboard page.</summary>
        public const string DashboardSectionId = "dashboard";

        /// <summary>Section id of the Model &amp; Agent page.</summary>
        public const string ModelAgentSectionId = "model-agent";

        /// <summary>Section id of the Context &amp; Memory page.</summary>
        public const string ContextMemorySectionId = "context-memory";

        /// <summary>Section id of the Tools &amp; Extensions page.</summary>
        public const string ToolsExtensionsSectionId = "tools-extensions";

        /// <summary>Section id of the Permissions page.</summary>
        public const string PermissionsSectionId = "permissions";

        /// <summary>Section id of the Workspace page.</summary>
        public const string WorkspaceSectionId = "workspace";

        // Pending deep-link target. Static so it survives a provider instance being recreated
        // when Unity focuses an already-open Preferences window.
        private static string _pendingSectionId;

        private readonly bool _persistState;
        private readonly string _selectedSectionPrefKey;
        private readonly string _foldoutsSessionKey;
        private readonly string _statusesSessionKey;
        private AgentCoreSettingsContext _settingsContext;
        private IAgentCoreSettingsPage[] _pages;

        private AgentCoreSettingsProvider(string path, SettingsScope scope)
            : base(path, scope)
        {
            _persistState = true;
            _selectedSectionPrefKey = AgentCoreSettingsState.DefaultSelectedSectionPrefKey;
            _foldoutsSessionKey = AgentCoreSettingsState.DefaultFoldoutsSessionKey;
            _statusesSessionKey = AgentCoreSettingsState.DefaultStatusesSessionKey;
            _pages = BuildPageList();
        }

        /// <summary>
        /// Test-only constructor that injects the page list so lifecycle transitions can be observed
        /// without reading or mutating real user EditorPrefs/SessionState.
        /// </summary>
        /// <param name="path">Settings path.</param>
        /// <param name="scope">Settings scope.</param>
        /// <param name="pages">Pages to use; when null the built-in list is used.</param>
        internal AgentCoreSettingsProvider(string path, SettingsScope scope, IAgentCoreSettingsPage[] pages)
            : this(path, scope, pages, persistState: false, null, null, null)
        {
        }

        /// <summary>
        /// Test-only constructor allowing opt-in persistence with isolated storage keys.
        /// </summary>
        internal AgentCoreSettingsProvider(
            string path,
            SettingsScope scope,
            IAgentCoreSettingsPage[] pages,
            bool persistState,
            string selectedSectionPrefKey = null,
            string foldoutsSessionKey = null,
            string statusesSessionKey = null)
            : base(path, scope)
        {
            _persistState = persistState;
            _selectedSectionPrefKey = selectedSectionPrefKey ?? AgentCoreSettingsState.DefaultSelectedSectionPrefKey;
            _foldoutsSessionKey = foldoutsSessionKey ?? AgentCoreSettingsState.DefaultFoldoutsSessionKey;
            _statusesSessionKey = statusesSessionKey ?? AgentCoreSettingsState.DefaultStatusesSessionKey;
            _pages = pages ?? BuildPageList();
        }

        /// <summary>
        /// Opens the AgentCore Preferences window, optionally selecting a specific section.
        /// </summary>
        /// <param name="sectionId">
        /// Target section id (one of the <c>*SectionId</c> constants), or null to keep the current section.
        /// Unknown ids fall back to the current/first page.
        /// </param>
        public static void OpenPreferences(string sectionId = null)
        {
            if (!string.IsNullOrWhiteSpace(sectionId))
            {
                _pendingSectionId = sectionId;
            }

            SettingsService.OpenUserPreferences(SettingsPath);
        }

        /// <summary>
        /// Sets the pending section id. Intended for EditMode tests.
        /// </summary>
        /// <param name="sectionId">Section id to queue.</param>
        internal static void SetPendingSectionForTests(string sectionId)
        {
            _pendingSectionId = sectionId;
        }

        /// <summary>
        /// Returns and clears the pending section id (one-shot consumption).
        /// </summary>
        /// <returns>The queued section id, or null when none is pending.</returns>
        internal static string ConsumePendingSectionId()
        {
            var pending = _pendingSectionId;
            _pendingSectionId = null;
            return pending;
        }

        /// <summary>
        /// Resolves a requested section id to an existing page id, falling back to the current
        /// selection or the first page when the request is null or unknown.
        /// </summary>
        /// <param name="sectionId">Requested section id.</param>
        /// <returns>A valid page id.</returns>
        internal string ResolveTargetSectionId(string sectionId)
        {
            if (!string.IsNullOrWhiteSpace(sectionId) && _pages.Any(p => p.Id == sectionId))
            {
                return sectionId;
            }

            var currentId = _settingsContext?.State?.SelectedSectionId;
            if (!string.IsNullOrWhiteSpace(currentId) && _pages.Any(p => p.Id == currentId))
            {
                return currentId;
            }

            return _pages.Length > 0 ? _pages[0].Id : null;
        }

        /// <summary>
        /// Applies a pending deep-link section to an already-initialized shell. Used when Unity
        /// focuses or repaints an open Preferences window without re-invoking <see cref="OnActivate"/>.
        /// </summary>
        internal void ApplyPendingSectionIfAny()
        {
            var pending = ConsumePendingSectionId();
            if (string.IsNullOrWhiteSpace(pending))
            {
                return;
            }

            if (_pages.Any(p => p.Id == pending))
            {
                SelectPage(pending);
            }
        }

        /// <summary>Gets the currently selected section id. Intended for tests.</summary>
        internal string SelectedSectionId => _settingsContext?.State?.SelectedSectionId;

        /// <summary>Gets the active settings state. Intended for tests.</summary>
        internal AgentCoreSettingsState State => _settingsContext?.State;

        /// <summary>
        /// Builds the ordered page list by merging built-in pages with dynamically discovered
        /// optional component pages from <see cref="AgentCoreExtensionRegistry.Pages"/>.
        /// </summary>
        private static IAgentCoreSettingsPage[] BuildPageList()
        {
            var builtIn = new IAgentCoreSettingsPage[]
            {
                new DashboardSettingsPage(),
                new ModelAgentSettingsPage(),
                new ContextMemorySettingsPage(),
                new ToolsExtensionsSettingsPage(),
                new PermissionsSettingsPage(),
                new WorkspaceSettingsPage(),
            };

            // Merge with dynamically discovered pages from optional component assemblies.
            var dynamic = AgentCoreExtensionRegistry.Pages;
            if (dynamic == null || dynamic.Count == 0)
                return builtIn;

            var merged = new List<IAgentCoreSettingsPage>(builtIn);
            foreach (var page in dynamic)
            {
                // Avoid duplicates by Id.
                if (merged.All(p => p.Id != page.Id))
                    merged.Add(page);
            }

            return merged.OrderBy(p => p.Order).ThenBy(p => p.Id, StringComparer.Ordinal).ToArray();
        }

        /// <summary>
        /// Creates the AgentCore Preferences provider.
        /// </summary>
        /// <returns>The configured settings provider instance.</returns>
        [SettingsProvider]
        public static SettingsProvider CreateSettingsProvider()
        {
            return new AgentCoreSettingsProvider(SettingsPath, SettingsScope.User)
            {
                label = "AgentCore",
                keywords = new[] { "agent", "ai", "llm", "chat", "agentcore", "mem0", "lightrag", "tools", "extensions", "permissions", "rules" }
            };
        }

        /// <inheritdoc />
        public override void OnActivate(string searchContext, VisualElement rootElement)
        {
            InitializeSettingsShell();
        }

        /// <inheritdoc />
        public override void OnGUI(string searchContext)
        {
            EnsureSettingsShell();
            ApplyPendingSectionIfAny();

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField(Loc.Tr("settings.provider.title", "AgentCore Settings"), EditorStyles.boldLabel);
            _settingsContext.Ui.DrawHelpText(Loc.Tr(
                "settings.provider.subtitle",
                "Configure AgentCore through modular settings pages."));
            EditorGUILayout.Space(8);

            DrawSettingsShell();
        }

        private void InitializeSettingsShell()
        {
            var state = _persistState
                ? AgentCoreSettingsState.CreatePersistent(_selectedSectionPrefKey, _foldoutsSessionKey, _statusesSessionKey)
                : new AgentCoreSettingsState();
            _settingsContext = new AgentCoreSettingsContext(
                AgentCoreSettings.instance,
                state,
                new AgentCoreSettingsUi());

            var targetId = ResolveTargetSectionId(ConsumePendingSectionId());
            var firstPage = _pages.FirstOrDefault(p => p.Id == targetId) ?? _pages[0];
            _settingsContext.State.SelectedSectionId = firstPage.Id;
            firstPage.OnActivate(_settingsContext);
        }

        private void EnsureSettingsShell()
        {
            if (_settingsContext == null || _settingsContext.Settings == null)
            {
                InitializeSettingsShell();
            }
        }

        private void DrawSettingsShell()
        {
            DrawPageTabs();
            EditorGUILayout.Space(8);
            DrawSelectedPage();
        }

        private void DrawPageTabs()
        {
            EditorGUILayout.BeginHorizontal();

            foreach (var page in _pages)
            {
                var isSelected = _settingsContext.State.SelectedSectionId == page.Id;
                var previousColor = GUI.backgroundColor;

                if (isSelected)
                {
                    GUI.backgroundColor = new Color(0.35f, 0.55f, 0.9f, 1f);
                }

                if (GUILayout.Button(page.Title, EditorStyles.miniButton, GUILayout.Height(26), GUILayout.MinWidth(100)))
                {
                    SelectPage(page.Id);
                }

                GUI.backgroundColor = previousColor;
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawSelectedPage()
        {
            var selected = _pages.FirstOrDefault(p => p.Id == _settingsContext.State.SelectedSectionId) ?? _pages[0];

            _settingsContext.Ui.DrawSectionTitle(selected.Title, selected.Description);

            try
            {
                selected.Draw(_settingsContext);
            }
            catch (Exception ex)
            {
                EditorGUILayout.HelpBox(
                    Loc.Tr("settings.provider.pageError", "Failed to draw settings page '{0}': {1}", selected.Id, ex.Message),
                    MessageType.Error);
            }
        }

        private void SelectPage(string pageId)
        {
            if (_settingsContext == null || _settingsContext.State.SelectedSectionId == pageId)
                return;

            var previousPage = _pages.FirstOrDefault(p => p.Id == _settingsContext.State.SelectedSectionId);
            previousPage?.OnDeactivate(_settingsContext);

            _settingsContext.State.SelectedSectionId = pageId;

            var nextPage = _pages.FirstOrDefault(p => p.Id == pageId);
            nextPage?.OnActivate(_settingsContext);
        }
    }

    /// <summary>
    /// Simple modal input dialog for secure setting values.
    /// </summary>
    public class EditorInputDialog : EditorWindow
    {
        private string _value = string.Empty;
        private string _message = string.Empty;
        private bool _confirmed;

        /// <summary>CJK 字体缓存（IMGUI 模式下需要手动设置 GUI.skin.font）</summary>
        private static Font _cachedFont;

        /// <summary>
        /// Shows a blocking modal input dialog.
        /// </summary>
        /// <param name="title">Dialog title.</param>
        /// <param name="message">Prompt message.</param>
        /// <param name="defaultValue">Default text value.</param>
        /// <returns>The entered value, or null when cancelled.</returns>
        public static string Show(string title, string message, string defaultValue = "")
        {
            var window = CreateInstance<EditorInputDialog>();
            window.titleContent = new GUIContent(title);
            window._message = message;
            window._value = defaultValue ?? string.Empty;
            window.position = new Rect(Screen.width / 2f, Screen.height / 2f, 420, 120);
            window.ShowModalUtility();
            return window._confirmed ? window._value : null;
        }

        /// <summary>
        /// 获取或创建 CJK 字体（与 ChatWindow 使用相同的跨平台回退链）。
        /// IMGUI 模式下独立 EditorWindow 不继承 GUI.skin.font，
        /// 需要显式设置，否则中文 bold 会出现笔画粗细不均的合成问题。
        /// </summary>
        private static Font GetCJKFont()
        {
            if (_cachedFont != null)
                return _cachedFont;

#if UNITY_EDITOR_WIN
            string[] fontCandidates = { "Microsoft YaHei", "SimHei", "Arial" };
#elif UNITY_EDITOR_OSX
            string[] fontCandidates = { "PingFang SC", "Hiragino Sans GB", "Arial" };
#else
            string[] fontCandidates = { "Noto Sans CJK SC", "WenQuanYi Micro Hei", "Arial" };
#endif
            foreach (var fontName in fontCandidates)
            {
                _cachedFont = Font.CreateDynamicFontFromOSFont(fontName, 14);
                if (_cachedFont != null) break;
            }
            return _cachedFont;
        }

        private void OnGUI()
        {
            // 设置 CJK 字体，确保中文渲染正常
            var font = GetCJKFont();
            if (font != null && GUI.skin.font != font)
            {
                GUI.skin.font = font;
            }

            EditorGUILayout.LabelField(_message, EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space(8);
            GUI.SetNextControlName("input");
            _value = EditorGUILayout.TextField(_value);
            EditorGUI.FocusTextInControl("input");

            EditorGUILayout.Space(8);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Loc.Tr("common.ok", "OK"), GUILayout.Width(80)))
            {
                _confirmed = true;
                Close();
            }

            if (GUILayout.Button(Loc.Tr("common.cancel", "Cancel"), GUILayout.Width(80)))
            {
                _confirmed = false;
                Close();
            }

            EditorGUILayout.EndHorizontal();
        }
    }
}
