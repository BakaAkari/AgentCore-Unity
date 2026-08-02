using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Tools.Safety
{
    /// <summary>
    /// ScriptableObject 运行时修改的退出还原守卫 (v1.13+ 补齐问题 5)。
    /// <para>
    /// 根因 (已核实，非猜测)：Unity 原生的"退出 Play Mode 自动还原"机制只覆盖<b>当前场景内的对象</b>
    /// (GameObject/Component)，依赖场景快照/重载；<b>Project Asset (含 ScriptableObject) 不在此范围内</b>——
    /// 无论用反射还是标准 <see cref="SerializedObject"/> + <see cref="SerializedObject.ApplyModifiedProperties"/>
    /// 写入，Unity 都不会在退出 Play Mode 时把内存值还原为磁盘值。这是 Unity 引擎层级行为，不是
    /// <see cref="PlaymodeWriteInterceptor"/> 或工具实现的 bug。
    /// </para>
    /// <para>
    /// 设计：既然引擎不提供，为 SO 补一层等价语义 —— 在 Play Mode 内首次修改某 SO 前，
    /// 记录其序列化快照 (JSON)；退出 Play Mode 时把所有被修改过的 SO 还原为快照状态，
    /// 使其行为与 GameObject 对齐 ("Play 中改值，退出即消失")。
    /// </para>
    /// <para>
    /// 调用方式：write 类工具在真正修改字段<b>之前</b>调用 <see cref="SnapshotBeforeFirstEdit"/>
    /// (仅在 Play Mode 中生效，且同一资产每个 Play Mode 会话只记录一次基线，避免多次 set 互相覆盖基线)。
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    public static class PlaymodeAssetRevertGuard
    {
        private sealed class Snapshot
        {
            public UnityEngine.Object Asset;
            public string Json;
        }

        // Key: 资产的 InstanceID —— 同一 Play Mode 会话内只保留最早 (修改前) 的基线快照。
        private static readonly Dictionary<int, Snapshot> _snapshots = new Dictionary<int, Snapshot>();

        static PlaymodeAssetRevertGuard()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        /// <summary>
        /// 在修改 <paramref name="asset"/> 之前调用。非 Play Mode 中为 no-op (不影响正常编辑流程)。
        /// 同一资产在同一 Play Mode 会话内只记录首次调用时的状态作为还原基线。
        /// </summary>
        public static void SnapshotBeforeFirstEdit(UnityEngine.Object asset)
        {
            if (asset == null || !PlaymodeWriteInterceptor.IsPlaymodeActive)
                return;

            int id = asset.GetInstanceID();
            if (_snapshots.ContainsKey(id))
                return; // 已有基线，不覆盖 (基线必须是"修改前"状态)。

            _snapshots[id] = new Snapshot
            {
                Asset = asset,
                Json = EditorJsonUtility.ToJson(asset)
            };
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.ExitingPlayMode)
                return;

            if (_snapshots.Count == 0)
                return;

            int restored = 0;
            foreach (var snapshot in _snapshots.Values)
            {
                if (snapshot.Asset == null)
                    continue; // 资产在 Play Mode 中被销毁的极端情况，跳过。

                EditorJsonUtility.FromJsonOverwrite(snapshot.Json, snapshot.Asset);
                restored++;
            }

            if (restored > 0)
            {
                AgentCore.Editor.Utils.AgentCoreLog.Info(
                    $"[PLAYMODE-INTERCEPT] Restored {restored} ScriptableObject asset(s) to pre-Play-Mode in-memory state on exit.");
            }

            _snapshots.Clear();
        }
    }
}
