using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.UI.Context
{
    /// <summary>
    /// 根据当前 Unity Editor 焦点窗口 + Selection 状态，路由到最合适的 Collector。
    ///
    /// 设计原则："Ctrl+Shift+X 作为全局查询入口"——用户对任何 Unity 界面元素按快捷键，
    ///   都应该采到**该元素相关**的上下文，而不是"上次在 Hierarchy 选中的 GameObject"。
    ///
    /// 优先级链（v1.6.4 更新，比原初版更严格）：
    ///   1. Console 焦点 → ConsoleCollector（最近 error/warning，无关 Selection）
    ///   2. ProjectBrowser 焦点 + assetGUIDs 非空 → AssetCollector
    ///   3. Hierarchy / SceneView / Inspector 焦点 → SelectionCollector（如有 Selection.gameObjects）
    ///   4. **其他任何窗口焦点**（Settings / Preferences / PackageManager / 自定义窗口等）
    ///      → FocusedWindowCollector（专项反射 + UI Toolkit Pick + 元数据）
    ///   5. Selection.gameObjects 非空（无窗口匹配）→ SelectionCollector
    ///   6. Selection.assetGUIDs 非空（无窗口匹配）→ AssetCollector
    ///   7. 最后回退 → SceneCollector
    ///
    /// 关键改动：**焦点窗口是"陌生窗口"时，绝不 fallback 到 Global Selection**，
    ///   而是走 FocusedWindowCollector 采集窗口本身的语义 + 光标附近元素。
    ///   这避免了"我在 Project Settings 按快捷键却注入 Hierarchy 里遗留的 Cube"这种错答。
    /// </summary>
    public static class ContextIngestRouter
    {
        public static ContextIngestResult Route()
        {
            var focused = EditorWindow.focusedWindow;
            var focusedTypeName = focused?.GetType().Name ?? string.Empty;

            // 1. Console 焦点 → Console
            if (IsConsoleWindow(focusedTypeName))
                return ConsoleContextCollector.Collect(preferErrorsOnly: false);

            // 2. Project 焦点 + asset 选中 → Asset
            if (IsProjectBrowser(focusedTypeName))
            {
                var assetResult = AssetContextCollector.Collect();
                if (!assetResult.IsEmpty) return assetResult;
                // Project 焦点但无 asset 选中 → 走 Selection（可能刚点开 Hierarchy 又切回 Project）
            }

            // 3. Hierarchy / SceneView / Inspector 焦点 + 有 GameObject 选中 → Selection
            //    Inspector 显示对象 = Selection.activeObject，这里三种窗口共享同一处理
            if ((IsHierarchyWindow(focusedTypeName) || IsSceneView(focusedTypeName) || IsInspectorWindow(focusedTypeName))
                && Selection.gameObjects != null && Selection.gameObjects.Length > 0)
            {
                return SelectionContextCollector.Collect();
            }

            // 4. 其他任何 EditorWindow 焦点 → FocusedWindowCollector（专项 + Pick + 元数据）
            //    这是关键分支：Settings / Preferences / PackageManager / 自定义窗口都走这里
            //    避免 fallback 到 Global Selection 产生"错的默认"
            if (focused != null && !IsKnownGlobalSelectionWindow(focusedTypeName))
            {
                return FocusedWindowCollector.Collect();
            }

            // 5. 无窗口焦点 或 焦点是已知全局 Selection 窗口但无 Selection：
            //    尝试全局 Selection 作为最后一层兜底
            if (Selection.gameObjects != null && Selection.gameObjects.Length > 0)
                return SelectionContextCollector.Collect();

            // 6. 无 GO 选中但有 Asset 选中
            var assetGuidResult = AssetContextCollector.Collect();
            if (!assetGuidResult.IsEmpty) return assetGuidResult;

            // 7. 最后回退：Scene 摘要
            return SceneContextCollector.Collect();
        }

        // ---------- 焦点识别 ----------

        private static bool IsConsoleWindow(string name)
            => name == "ConsoleWindow";

        private static bool IsProjectBrowser(string name)
            => name == "ProjectBrowser";

        private static bool IsSceneView(string name)
            => name == "SceneView";

        private static bool IsHierarchyWindow(string name)
            => name == "SceneHierarchyWindow";

        private static bool IsInspectorWindow(string name)
            => name == "InspectorWindow";

        /// <summary>
        /// 已知会写入 Global Selection 的窗口。
        /// 这些窗口没有 Selection 时才回退到全局 Selection 兜底（分支 5）。
        /// </summary>
        private static bool IsKnownGlobalSelectionWindow(string name)
            => IsHierarchyWindow(name)
               || IsSceneView(name)
               || IsInspectorWindow(name)
               || IsProjectBrowser(name);
    }
}
