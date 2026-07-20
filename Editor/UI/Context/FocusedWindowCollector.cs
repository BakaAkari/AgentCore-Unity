using System;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.UI.Context
{
    /// <summary>
    /// 通用"焦点窗口"采集器。
    /// 目标：让 Ctrl+Shift+X 作为**全局查询入口**——用户看到任何窗口里不认识的东西，
    /// 都能把窗口上下文 + 光标附近元素采集出来交给 LLM。
    ///
    /// 采集层次（从高到低）：
    /// 1. 已知专项窗口（Settings / Preferences / Package Manager）→ 反射拿内部 selection
    /// 2. UI Toolkit Pick（focusedWindow.rootVisualElement.panel.Pick(mousePos)）→ 光标下 element
    /// 3. 通用元数据（window title + 类型全名 + Selection 备注）
    ///
    /// **重要**：本 collector 只在**已知窗口未被上游 Router 消费**时使用，
    /// 不重复处理 Console/Project/Hierarchy/SceneView（那些走专用 collector）。
    /// </summary>
    public static class FocusedWindowCollector
    {
        /// <summary>
        /// 采集当前 focused window 的上下文。
        /// 保证不返回 null；至少返回窗口元数据。
        /// </summary>
        public static ContextIngestResult Collect()
        {
            var focused = EditorWindow.focusedWindow;
            if (focused == null)
                return ContextIngestResult.Empty();

            var winType = focused.GetType();
            var winTypeName = winType.Name;
            var winFullName = winType.FullName ?? winTypeName;
            var winTitle = focused.titleContent?.text ?? winTypeName;

            var sb = new StringBuilder(512);
            sb.Append("Focused window: ").Append(winTitle)
              .Append(" (type: ").Append(winFullName).Append(")\n");

            // 层次 1：已知专项窗口
            string specific = TryCollectKnownWindow(focused, winTypeName);
            if (!string.IsNullOrEmpty(specific))
            {
                sb.Append("\n").Append(specific);
            }

            // 层次 2：UI Toolkit 光标下元素
            var pickInfo = TryPickUnderCursor(focused);
            if (!string.IsNullOrEmpty(pickInfo))
            {
                sb.Append("\nElement under cursor:\n").Append(pickInfo);
            }

            // 层次 3：Selection 备注（因为不确定用户是否在意 Selection，标为"仅供参考"）
            if (Selection.objects != null && Selection.objects.Length > 0)
            {
                sb.Append("\nGlobal Selection (may or may not be relevant): ");
                for (int i = 0; i < Selection.objects.Length && i < 5; i++)
                {
                    if (i > 0) sb.Append(", ");
                    var obj = Selection.objects[i];
                    sb.Append(obj != null ? obj.name : "<null>");
                }
                if (Selection.objects.Length > 5) sb.Append($", ... (+{Selection.objects.Length - 5} more)");
                sb.Append('\n');
            }

            var label = $"Window: {winTitle}";
            return ContextIngestResult.Ok(label, sb.ToString());
        }

        // ==================== 层次 1：已知专项窗口 ====================

        private static string TryCollectKnownWindow(EditorWindow win, string typeName)
        {
            // Project Settings / Preferences（共用 SettingsWindow 类型）
            if (typeName == "SettingsWindow" || typeName == "SettingsService" ||
                (win.GetType().FullName?.Contains("SettingsWindow") ?? false))
            {
                return CollectSettingsWindow(win);
            }

            // Package Manager
            if (typeName == "PackageManagerWindow" ||
                (win.GetType().FullName?.Contains("PackageManager") ?? false))
            {
                return CollectPackageManagerWindow(win);
            }

            // Animation Window
            if (typeName == "AnimationWindow")
            {
                return CollectAnimationWindow(win);
            }

            // 其他未知窗口 → 返回 null 交给 Pick + 通用元数据处理
            return null;
        }

        /// <summary>
        /// 反射 SettingsWindow.m_CurrentProvider 或 currentProvider，采集当前 setting category。
        /// SettingsWindow 内部结构：
        ///   - m_Providers: 所有 SettingsProvider 列表
        ///   - m_CurrentProvider: 当前选中的 provider
        ///   - SettingsProvider.settingsPath: 类似 "Project/Player"
        ///   - SettingsProvider.label: 面板标题
        /// </summary>
        private static string CollectSettingsWindow(EditorWindow win)
        {
            try
            {
                var winType = win.GetType();

                // 尝试字段名（Unity 版本差异）
                var providerField = winType.GetField("m_CurrentProvider",
                                        BindingFlags.NonPublic | BindingFlags.Instance)
                                    ?? winType.GetField("currentProvider",
                                        BindingFlags.NonPublic | BindingFlags.Instance);

                if (providerField == null)
                    return "Settings window detected but reflection field 'm_CurrentProvider' not found.";

                var provider = providerField.GetValue(win);
                if (provider == null)
                    return "Settings window has no active provider.";

                var providerType = provider.GetType();

                // 常见属性：settingsPath（路径）、label（标题）、scope（Project/User）
                var path = providerType.GetProperty("settingsPath")?.GetValue(provider) as string
                           ?? providerType.GetField("settingsPath")?.GetValue(provider) as string;
                var label = providerType.GetProperty("label")?.GetValue(provider) as string
                            ?? providerType.GetField("label")?.GetValue(provider) as string;
                var scope = providerType.GetProperty("scope")?.GetValue(provider)?.ToString()
                            ?? providerType.GetField("scope")?.GetValue(provider)?.ToString();

                var sb = new StringBuilder();
                sb.Append("Settings category:\n");
                if (!string.IsNullOrEmpty(path)) sb.Append("  path: ").Append(path).Append('\n');
                if (!string.IsNullOrEmpty(label)) sb.Append("  label: ").Append(label).Append('\n');
                if (!string.IsNullOrEmpty(scope)) sb.Append("  scope: ").Append(scope).Append('\n');
                sb.Append("  providerType: ").Append(providerType.FullName).Append('\n');

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"Settings window reflection failed: {ex.Message}";
            }
        }

        /// <summary>
        /// 反射 Package Manager 内部选中的包信息。
        /// PackageManagerWindow 结构比较私有，字段名各 Unity 版本差异较大，尽力而为。
        /// </summary>
        private static string CollectPackageManagerWindow(EditorWindow win)
        {
            try
            {
                var winType = win.GetType();
                var sb = new StringBuilder();
                sb.Append("Package Manager detected.\n");

                // 尝试常见字段 m_PackageList / m_SelectedPackageInfo
                var selectedField = winType.GetField("m_SelectedPackage",
                                        BindingFlags.NonPublic | BindingFlags.Instance)
                                    ?? winType.GetField("m_SelectedPackageInfo",
                                        BindingFlags.NonPublic | BindingFlags.Instance);

                if (selectedField != null)
                {
                    var selected = selectedField.GetValue(win);
                    if (selected != null)
                    {
                        var selType = selected.GetType();
                        var name = selType.GetProperty("name")?.GetValue(selected) as string
                                   ?? selType.GetField("name")?.GetValue(selected) as string;
                        var version = selType.GetProperty("version")?.GetValue(selected)?.ToString();
                        var displayName = selType.GetProperty("displayName")?.GetValue(selected) as string;
                        var desc = selType.GetProperty("description")?.GetValue(selected) as string;

                        if (!string.IsNullOrEmpty(name)) sb.Append("  name: ").Append(name).Append('\n');
                        if (!string.IsNullOrEmpty(displayName)) sb.Append("  displayName: ").Append(displayName).Append('\n');
                        if (!string.IsNullOrEmpty(version)) sb.Append("  version: ").Append(version).Append('\n');
                        if (!string.IsNullOrEmpty(desc))
                        {
                            sb.Append("  description: ")
                              .Append(ContextIngestFormatter.TruncateValue(desc, 400))
                              .Append('\n');
                        }
                        return sb.ToString();
                    }
                }

                sb.Append("  (no selected package detected via reflection)\n");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"Package Manager reflection failed: {ex.Message}";
            }
        }

        /// <summary>
        /// 反射 AnimationWindow 拿当前选中的 clip / GameObject。
        /// </summary>
        private static string CollectAnimationWindow(EditorWindow win)
        {
            try
            {
                var winType = win.GetType();
                var sb = new StringBuilder();
                sb.Append("Animation Window detected.\n");

                // 尝试 selectedClip / m_ActiveGameObject
                var clipProp = winType.GetProperty("activeAnimationClip",
                                   BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                if (clipProp != null)
                {
                    var clip = clipProp.GetValue(win) as AnimationClip;
                    if (clip != null)
                    {
                        sb.Append("  activeClip: ").Append(clip.name)
                          .Append(" (length=").Append(clip.length).Append("s)")
                          .Append(" frameRate=").Append(clip.frameRate).Append('\n');
                    }
                }

                var goProp = winType.GetProperty("activeGameObject",
                                 BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                if (goProp != null)
                {
                    var go = goProp.GetValue(win) as GameObject;
                    if (go != null) sb.Append("  activeGameObject: ").Append(go.name).Append('\n');
                }

                if (sb.Length <= "Animation Window detected.\n".Length)
                    sb.Append("  (no active clip/GameObject detected via reflection)\n");

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"Animation Window reflection failed: {ex.Message}";
            }
        }

        // ==================== 层次 2：UI Toolkit Pick ====================

        /// <summary>
        /// 尝试从 focusedWindow 的 rootVisualElement 用 Pick 找到光标下 element。
        /// 只对 UI Toolkit 窗口有效；IMGUI 窗口返回 null。
        /// </summary>
        private static string TryPickUnderCursor(EditorWindow win)
        {
            try
            {
                var root = win.rootVisualElement;
                if (root == null || root.panel == null) return null;

                // Unity Shortcut 触发时 Event.current 通常为 null；
                // 需要从静态跟踪的最近鼠标位置读取。
                var mousePos = MouseTracker.GetLastMousePositionInWindow(win);
                if (!mousePos.HasValue) return null;

                var picked = root.panel.Pick(mousePos.Value);
                if (picked == null) return null;

                var sb = new StringBuilder();
                int depth = 0;
                var cur = picked;
                while (cur != null && depth < 5)
                {
                    if (depth > 0) sb.Append(" > ");
                    var name = string.IsNullOrEmpty(cur.name) ? "<anon>" : cur.name;
                    sb.Append(cur.GetType().Name).Append('(').Append(name).Append(')');

                    // 尝试提取文本内容（Label / TextField / Button 等）
                    var text = ExtractElementText(cur);
                    if (!string.IsNullOrEmpty(text) && depth == 0)
                    {
                        sb.Append(" text=\"").Append(
                            ContextIngestFormatter.TruncateValue(text, 100)).Append('"');
                    }

                    cur = cur.parent;
                    depth++;
                }

                return "  " + sb.ToString() + "\n";
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore] UI Toolkit Pick failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 尽力提取 UI Toolkit element 的显示文本。
        /// </summary>
        private static string ExtractElementText(VisualElement el)
        {
            // TextElement (Label, Button, etc.)
            var textProp = el.GetType().GetProperty("text",
                BindingFlags.Public | BindingFlags.Instance);
            if (textProp != null && textProp.PropertyType == typeof(string))
            {
                var t = textProp.GetValue(el) as string;
                if (!string.IsNullOrEmpty(t)) return t;
            }

            // tooltip
            if (!string.IsNullOrEmpty(el.tooltip)) return "(tooltip) " + el.tooltip;

            return null;
        }
    }
}
