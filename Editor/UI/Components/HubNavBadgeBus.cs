using System;
using System.Collections.Generic;

namespace AgentCore.Editor.UI.Components
{
    /// <summary>
    /// Hub 导航按钮角标状态快照。
    /// <para>
    /// 描述某个 Hub 模块导航按钮的动态外观：可选的运行时标签覆盖（<see cref="LabelOverride"/>）
    /// 与告警高亮开关（<see cref="Alert"/>）。这是一个纯数据载体，不含任何具体模块（VCS / Indexing 等）的知识。
    /// </para>
    /// </summary>
    public sealed class HubNavBadgeState
    {
        /// <summary>
        /// 目标 Hub 模块 ID（对应 <see cref="HubModuleDefinition.Id"/>）。
        /// </summary>
        public string ModuleId;

        /// <summary>
        /// 运行时标签覆盖。非 null 时用于替换导航按钮显示文字（例如 VCS 模块按检测到的类型显示 SVN/GIT/P4）；
        /// 为 null 时保留按钮原始标签。
        /// </summary>
        public string LabelOverride;

        /// <summary>
        /// 是否高亮告警（例如远端有更新时按钮变黄）。
        /// </summary>
        public bool Alert;

        /// <summary>
        /// 创建当前快照的深拷贝。
        /// </summary>
        public HubNavBadgeState Clone()
        {
            return new HubNavBadgeState
            {
                ModuleId = ModuleId,
                LabelOverride = LabelOverride,
                Alert = Alert
            };
        }
    }

    /// <summary>
    /// 进程内的 Hub 导航角标事件总线。
    /// <para>
    /// 任意扩展模块（VCS、Indexing 等）都可以通过 <see cref="Publish"/> 推送其导航按钮的动态状态
    /// （标签覆盖 / 告警高亮），由持有 <see cref="HubRail"/> 的宿主（ChatWindow）订阅并转调按钮更新。
    /// </para>
    /// <para>
    /// 该总线**保留每个模块的最新状态**：由于扩展模块的驱动器通常在 <c>[InitializeOnLoad]</c> 阶段
    /// 就开始推送（可能早于 ChatWindow 打开），宿主在构建 HubRail 后可通过 <see cref="TryGetState"/>
    /// / <see cref="Snapshot"/> 主动拉取当前状态，避免错过早期事件导致按钮外观不同步。
    /// </para>
    /// <para>
    /// 位于主 <c>AgentCore.Editor</c> 程序集，是通用基础设施，不依赖任何具体模块的 asmdef。
    /// </para>
    /// </summary>
    public static class HubNavBadgeBus
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, HubNavBadgeState> _states =
            new Dictionary<string, HubNavBadgeState>(StringComparer.Ordinal);

        /// <summary>
        /// 当任意模块的导航角标状态变化时触发，参数为该模块的最新状态快照。
        /// </summary>
        public static event Action<HubNavBadgeState> BadgeChanged;

        /// <summary>
        /// 推送某个模块的导航角标状态。会覆盖该模块上一次的状态并触发 <see cref="BadgeChanged"/>。
        /// </summary>
        /// <param name="state">要发布的状态。<c>state</c> 或其 <c>ModuleId</c> 为空时忽略。</param>
        public static void Publish(HubNavBadgeState state)
        {
            if (state == null || string.IsNullOrWhiteSpace(state.ModuleId))
                return;

            HubNavBadgeState snapshot;
            lock (_lock)
            {
                snapshot = state.Clone();
                _states[snapshot.ModuleId] = snapshot;
            }

            BadgeChanged?.Invoke(snapshot.Clone());
        }

        /// <summary>
        /// 获取指定模块当前已知的导航角标状态。
        /// </summary>
        /// <param name="moduleId">模块 ID。</param>
        /// <param name="state">输出：该模块的状态快照（存在时）。</param>
        /// <returns>存在已发布状态时返回 <c>true</c>。</returns>
        public static bool TryGetState(string moduleId, out HubNavBadgeState state)
        {
            state = null;
            if (string.IsNullOrWhiteSpace(moduleId))
                return false;

            lock (_lock)
            {
                if (_states.TryGetValue(moduleId, out var found))
                {
                    state = found.Clone();
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 获取当前所有已发布模块状态的快照列表。宿主在构建 HubRail 后可据此一次性同步全部按钮外观。
        /// </summary>
        public static IReadOnlyList<HubNavBadgeState> Snapshot()
        {
            lock (_lock)
            {
                var result = new List<HubNavBadgeState>(_states.Count);
                foreach (var kvp in _states)
                    result.Add(kvp.Value.Clone());
                return result;
            }
        }
    }
}
