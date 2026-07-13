using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Context
{
    /// <summary>
    /// 全局鼠标位置追踪器。
    ///
    /// 背景：Unity Shortcut 回调触发时 <see cref="Event.current"/> 通常为 null，
    /// 无法直接读取当前鼠标位置。因此需要**持续追踪**鼠标位置，等快捷键触发时读缓存值。
    ///
    /// 实现策略：
    /// 1. 在 <see cref="InitializeOnLoadMethodAttribute"/> 时给所有 EditorWindow 的 rootVisualElement
    ///    注册 MouseMoveEvent + MouseEnterEvent，更新静态 lastMousePosition
    /// 2. 提供 <see cref="GetLastMousePositionInWindow"/> 返回相对于指定窗口的最新位置
    ///
    /// 已知限制：
    /// - **只能追踪 UI Toolkit 覆盖的区域**。IMGUI 区域鼠标移动**不会**触发 UI Toolkit 事件
    /// - 第一次快捷键触发前可能没有位置样本 → 返回 null,调用方 fallback
    /// </summary>
    public static class MouseTracker
    {
        /// <summary>最后一次记录的鼠标位置（相对于 root visual element 局部坐标）。</summary>
        private static Vector2 _lastLocalPosition;

        /// <summary>最后追踪到鼠标的窗口的弱引用；用于验证 GetLastMousePositionInWindow 是否命中。</summary>
        private static WeakReference<EditorWindow> _lastWindow;

        /// <summary>最后一次追踪的时间戳（用于判断样本是否过期）。</summary>
        private static double _lastUpdateEditorTime;

        /// <summary>样本失效时间（秒）；用户 3s 没动鼠标就认为位置不再可靠。</summary>
        private const double SampleExpirySeconds = 3.0;

        /// <summary>
        /// Editor 启动时给所有已存在窗口挂钩子。
        /// 后续新开窗口在 <see cref="EnsureHooked"/> 里按需挂。
        /// </summary>
        [InitializeOnLoadMethod]
        private static void InitializeOnLoad()
        {
            EditorApplication.update += OnEditorUpdate;
        }

        /// <summary>
        /// 定期扫描所有 EditorWindow，给未挂钩子的挂上。
        /// 使用 EditorApplication.update 每帧扫一次成本可接受（窗口列表极短，通常 &lt; 30）。
        /// </summary>
        private static void OnEditorUpdate()
        {
            try
            {
                var all = Resources.FindObjectsOfTypeAll<EditorWindow>();
                for (int i = 0; i < all.Length; i++)
                {
                    EnsureHooked(all[i]);
                }
            }
            catch
            {
                // Domain reload / 初始化竞态时可能抛错，忽略
            }
        }

        /// <summary>
        /// 记录已挂钩子的 rootVisualElement 弱引用，避免重复挂。
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<int> _hookedRoots
            = new System.Collections.Generic.HashSet<int>();

        /// <summary>
        /// 给一个 EditorWindow 的 root 挂 MouseMoveEvent。
        /// 用 root 的 GetHashCode 作为唯一标识（VisualElement 生命周期跟窗口一致，域重载后会重建）。
        /// </summary>
        private static void EnsureHooked(EditorWindow win)
        {
            if (win == null) return;
            var root = win.rootVisualElement;
            if (root == null) return;

            var rootId = root.GetHashCode();
            if (_hookedRoots.Contains(rootId)) return;
            _hookedRoots.Add(rootId);

            var winRef = new WeakReference<EditorWindow>(win);

            root.RegisterCallback<MouseMoveEvent>(evt => OnMouseMove(winRef, evt.localMousePosition));
            root.RegisterCallback<MouseEnterEvent>(evt => OnMouseMove(winRef, evt.localMousePosition));
            root.RegisterCallback<PointerMoveEvent>(evt => OnMouseMove(winRef, (Vector2)evt.localPosition));
        }

        private static void OnMouseMove(WeakReference<EditorWindow> winRef, Vector2 localPos)
        {
            _lastLocalPosition = localPos;
            _lastWindow = winRef;
            _lastUpdateEditorTime = EditorApplication.timeSinceStartup;
        }

        /// <summary>
        /// 读取最后追踪到的鼠标位置（相对于指定窗口的 rootVisualElement）。
        /// 只在 (a) 追踪目标 window 与传入 window 是同一个 且 (b) 样本未过期时返回。
        /// </summary>
        public static Vector2? GetLastMousePositionInWindow(EditorWindow win)
        {
            if (win == null || _lastWindow == null) return null;
            if (!_lastWindow.TryGetTarget(out var trackedWin)) return null;
            if (!ReferenceEquals(trackedWin, win)) return null;

            var age = EditorApplication.timeSinceStartup - _lastUpdateEditorTime;
            if (age > SampleExpirySeconds) return null;

            return _lastLocalPosition;
        }
    }
}
