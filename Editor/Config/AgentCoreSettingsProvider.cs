using System;
using System.Collections.Generic;
using System.Linq;
using AgentCore.Editor.Config.Settings;
using AgentCore.Editor.Config.Settings.Pages;
using AgentCore.Editor.Extensions;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.Config
{
    /// <summary>
    /// Provides the AgentCore Project Settings hub with top-tab navigation.
    /// </summary>
    public class AgentCoreSettingsProvider : SettingsProvider
    {
        private AgentCoreSettingsContext _settingsContext;
        private IAgentCoreSettingsPage[] _pages;

        private AgentCoreSettingsProvider(string path, SettingsScope scope)
            : base(path, scope)
        {
            _pages = BuildPageList();
        }

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
        /// Creates the AgentCore Project Settings provider.
        /// </summary>
        /// <returns>The configured settings provider instance.</returns>
        [SettingsProvider]
        public static SettingsProvider CreateSettingsProvider()
        {
            return new AgentCoreSettingsProvider("Project/AgentCore", SettingsScope.Project)
            {
                label = "AgentCore",
                keywords = new[] { "agent", "ai", "llm", "chat", "agentcore", "mem0", "lightrag", "tools", "extensions" }
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

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("AgentCore Settings", EditorStyles.boldLabel);
            _settingsContext.Ui.DrawHelpText("Configure AgentCore through modular settings pages.");
            EditorGUILayout.Space(8);

            DrawSettingsShell();
        }

        private void InitializeSettingsShell()
        {
            _settingsContext = AgentCoreSettingsContext.Create();

            var selectedId = _settingsContext.State.SelectedSectionId;
            var firstPage = _pages.FirstOrDefault(p => p.Id == selectedId) ?? _pages[0];
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
                EditorGUILayout.HelpBox($"Failed to draw settings page '{selected.Id}': {ex.Message}", MessageType.Error);
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
            if (GUILayout.Button("OK", GUILayout.Width(80)))
            {
                _confirmed = true;
                Close();
            }

            if (GUILayout.Button("Cancel", GUILayout.Width(80)))
            {
                _confirmed = false;
                Close();
            }

            EditorGUILayout.EndHorizontal();
        }
    }
}
