using System;
using System.Collections.Generic;
using UnityEditor;

namespace AgentCore.Editor.Tools.Safety
{
    /// <summary>
    /// Playmode 运行时修改追踪日志 (v1.12+ ModifyRuntimeState)。
    /// <para>
    /// 记录本次 Playmode 会话内 Agent 通过 write 类工具做的运行时内存修改,
    /// 供 <c>get_playmode_changes</c> 工具查询,帮助 Agent 自我审视"我在 Play 中改了什么"、
    /// 判断哪些改动值得退出 Playmode 后永久应用。
    /// </para>
    /// <para>
    /// 生命周期:退出 Playmode 时<b>自动清空</b> (对齐"运行时修改退出即消失"的语义)。
    /// 静态列表在 Editor 域内存活;Domain Reload 会重置 (与运行时修改本身的语义一致)。
    /// </para>
    /// <para>
    /// 线程约定:仅在主线程调用 (write 工具均 RequiresMainThread=true)。不加锁。
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    public static class PlaymodeChangeLog
    {
        /// <summary>单条运行时修改记录。</summary>
        public sealed class PlaymodeChange
        {
            public DateTime Timestamp;
            public string Tool;
            public string Action;
            public string Target;
            public string Details;
        }

        private static readonly List<PlaymodeChange> _changes = new List<PlaymodeChange>();

        /// <summary>会话内累计记录数上限,防止长时间 Play 无限增长 (超出则丢弃最旧)。</summary>
        private const int MaxEntries = 500;

        static PlaymodeChangeLog()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        /// <summary>
        /// 记录一次运行时修改。非 Playmode 中调用会被忽略 (只追踪运行时行为)。
        /// </summary>
        /// <param name="tool">工具名 (如 "manage_scriptable_object")</param>
        /// <param name="action">action 名 (如 "set")</param>
        /// <param name="target">修改目标 (asset 路径 / GameObject 名 / 属性名)</param>
        /// <param name="details">可读的修改说明</param>
        public static void Record(string tool, string action, string target, string details)
        {
            if (!EditorApplication.isPlaying)
                return;

            if (_changes.Count >= MaxEntries)
                _changes.RemoveAt(0);

            _changes.Add(new PlaymodeChange
            {
                Timestamp = DateTime.Now,
                Tool = tool,
                Action = action,
                Target = target,
                Details = details
            });
        }

        /// <summary>获取本会话已记录的运行时修改 (只读快照)。</summary>
        public static IReadOnlyList<PlaymodeChange> GetChanges() => _changes.AsReadOnly();

        /// <summary>当前记录条数。</summary>
        public static int Count => _changes.Count;

        /// <summary>手动清空 (通常无需调用;退出 Playmode 时自动清空)。</summary>
        public static void Clear() => _changes.Clear();

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                if (_changes.Count > 0)
                {
                    AgentCore.Editor.Utils.AgentCoreLog.Info(
                        $"[PLAYMODE-INTERCEPT] Session ended: {_changes.Count} in-memory change(s) discarded (never persisted to disk).");
                }
                _changes.Clear();
            }
        }
    }
}
