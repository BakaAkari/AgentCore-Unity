using System;
using UnityEditor;

namespace AgentCore.Editor.Core
{
    /// <summary>
    /// Chat 会话运行时的 UI 更新模式.
    /// <para>
    /// v1.8.8 新增. 用于解决观测者效应: 用户跑 <c>manage_profiler</c> 等诊断工具时,
    /// Chat 面板的 UI 更新会通过 UnitySynchronizationContext 走 EditorLoop tick,
    /// 间接触发 Application.UpdateScene, 干扰被测量的性能数据.
    /// </para>
    /// </summary>
    public enum SessionMode
    {
        /// <summary>
        /// 默认模式. 里程碑事件 (ToolCallStarted/Completed/Failed, ContentFlush/ReasoningFlush,
        /// TurnDone/Error, StateChanged) 触发 UI 更新; 非里程碑事件 (StreamToken/ReasoningToken 等)
        /// 沿用 v1.8.5/6 累积到里程碑一次性 flush. 所有 UIToolkit schedule.Execute.Every 动画保持
        /// v1.8.7 的关闭状态.
        /// </summary>
        Batched,

        /// <summary>
        /// 静默模式. 所有 AgentEvent (含里程碑) 都不 marshal 到主线程, 而是写入 in-memory buffer.
        /// Chat 面板 UI 完全冻结, 状态标识区显示静态 "Silent · Running".
        /// 在 TurnDone/Error/用户手动切出时一次性 flush buffer 到 UI.
        /// 用途: 用户跑性能分析工具时保证 Scene/Game 视图完全不受 Chat UI 干扰.
        /// </summary>
        Silent
    }

    /// <summary>
    /// v1.8.8: 全局单例 SessionMode 状态 + EditorPrefs 持久化.
    /// <para>
    /// 设计决策 (2026-07-23):
    /// - ChatWindow 是单例, 因此 SessionMode 也走单例, 无需 per-window 隔离
    /// - 存 EditorPrefs, key 全项目共享 (不做 per-project hash), 跨项目通用
    /// - Changed 事件供 UI (Silent 按钮样式) 和 AgentLoop.Events (mode gate) 订阅
    /// </para>
    /// </summary>
    public static class SessionModeState
    {
        private const string EditorPrefsKey = "AgentCore.ChatWindow.SessionMode";

        private static SessionMode _current;
        private static bool _loaded;

        /// <summary>
        /// mode 变化时触发. UI 侧和 AgentLoop.Events 侧都订阅.
        /// 参数是新 mode. 订阅方需自己处理"如果与旧值相同不做事"的判断 (但当前 Set 只在真变化时 raise).
        /// </summary>
        public static event Action<SessionMode> Changed;

        /// <summary>
        /// 当前 mode. 首次访问时从 EditorPrefs 懒加载, 默认 Batched.
        /// </summary>
        public static SessionMode Current
        {
            get
            {
                EnsureLoaded();
                return _current;
            }
        }

        /// <summary>
        /// 切换 mode. 只在真变化时持久化 + raise Changed.
        /// </summary>
        public static void Set(SessionMode mode)
        {
            EnsureLoaded();
            if (_current == mode) return;
            _current = mode;
            EditorPrefs.SetString(EditorPrefsKey, mode.ToString());
            try
            {
                Changed?.Invoke(mode);
            }
            catch (Exception ex)
            {
                // 订阅方异常不应影响 mode 切换; 只记录, 不 rethrow
                UnityEngine.Debug.LogError($"[AgentCore] SessionModeState.Changed subscriber threw: {ex}");
            }
        }

        /// <summary>
        /// 便利: 判断当前是否 Silent.
        /// </summary>
        public static bool IsSilent => Current == SessionMode.Silent;

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            var raw = EditorPrefs.GetString(EditorPrefsKey, SessionMode.Batched.ToString());
            _current = Enum.TryParse<SessionMode>(raw, out var parsed) ? parsed : SessionMode.Batched;
        }
    }
}
