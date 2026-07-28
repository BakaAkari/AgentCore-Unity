using Unity.Profiling;

namespace AgentCore.Editor.Utils
{
    /// <summary>
    /// v1.12.0-alpha.4: 诊断用 Profiler markers, 用于分析 Unity 主线程性能.
    /// <para>
    /// 使用方式: <c>using (AgentCoreProfilerMarkers.EmitMarshalled.Auto()) { ... }</c>.
    /// 所有 marker 前缀统一 <c>AgentCore.*</c>, 便于在 Unity Profiler Hierarchy 里过滤.
    /// </para>
    /// <para>
    /// 注意: ProfilerMarker 本身开销极低 (~几 ns), 无需 <c>#if</c> 剥离, 长期保留问题不大.
    /// 如未来确定不再需要, 可整体删除本文件并 grep 引用清理.
    /// </para>
    /// </summary>
    internal static class AgentCoreProfilerMarkers
    {
        // ── Event 派发链 (AgentLoop → 主线程) ─────────────────────────────────

        /// <summary>事件被 marshal 到主线程 (AgentLoop.EmitEvent 每次调用).</summary>
        internal static readonly ProfilerMarker EmitMarshalled =
            new ProfilerMarker("AgentCore.Emit.Marshalled");

        /// <summary>每帧 drain 主线程回调队列 (含 event 派发, RunOnMainThread 各类回调).</summary>
        internal static readonly ProfilerMarker DrainQueue =
            new ProfilerMarker("AgentCore.DrainQueue");

        // ── UI 层事件处理 ────────────────────────────────────────────────────

        /// <summary>ChatWindow.HandleAgentEvent 总入口 (每个事件一次).</summary>
        internal static readonly ProfilerMarker UIHandleAgentEvent =
            new ProfilerMarker("AgentCore.UI.HandleAgentEvent");

        /// <summary>ChatWindow.UpdateContextUsagePanel — 每次 StateChanged 触发, 疑似元凶.</summary>
        internal static readonly ProfilerMarker UIUpdateContextPanel =
            new ProfilerMarker("AgentCore.UI.UpdateContextPanel");

        // ── Token 估算 (被 GetContextBudget / Compression 大量调用) ────────────

        /// <summary>AgentLoop.GetContextBudget — 全量扫描 _messages 估 token.</summary>
        internal static readonly ProfilerMarker GetContextBudget =
            new ProfilerMarker("AgentCore.GetContextBudget");

        // ── 工具执行 ─────────────────────────────────────────────────────────
        // 注: 此处曾定义 AgentCore.ToolExec marker, 但 ProfilerMarker.Begin/End 无法跨 await 到下一帧
        // (Unity Profiler 按 frame 校验配对, 跨帧会污染 Console). 工具耗时可从 Unity 内建的
        // UnitySynchronizationContext.ExecuteTasks 采样观测, 无需自定义 marker.
    }
}
