using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AgentCore.Editor.Tools.Safety
{
    /// <summary>
    /// Play Mode 中的 Undo 记录跳过器 (v1.13+)。
    /// <para>
    /// 背景：<see cref="PlaymodeRuntimeSafeActions"/> 白名单允许的写操作在 Play Mode 中执行时，
    /// 修改本就是运行时内存态，退出 Play Mode 时 Unity 自动还原 —— Undo 栈记录在这个场景下毫无意义。
    /// 更严重的是，部分 Undo API（已验证：<see cref="Undo.AddComponent"/>）在 Play Mode 中会直接
    /// 抛出 "This cannot be used during play mode" 而不是静默降级，导致工具调用失败。
    /// </para>
    /// <para>
    /// 设计：本类为所有写工具在 Play Mode 中提供统一护栏 —— Play Mode 下跳过 Undo 记录直接调用
    /// 底层 API；非 Play Mode 下完全透传给原始 <see cref="Undo"/> API，行为零变化。
    /// 调用方 (ManageGameObjectTool / ManageComponentTool / ManagePrefabTool /
    /// ManageScriptableObjectTool 等) 的白名单写路径应统一通过此类操作 Undo，
    /// 而不是直接调用 <see cref="Undo"/>，从而对任何未来新增的白名单 action 自动生效，
    /// 不需要逐个排查哪些 Undo API 在 Play Mode 下会抛异常。
    /// </para>
    /// </summary>
    public static class PlaymodeUndoGuard
    {
        private static bool IsActive => PlaymodeWriteInterceptor.IsPlaymodeActive;

        /// <summary>
        /// 添加组件。Play Mode 中直接 <c>GameObject.AddComponent</c>（跳过 Undo，因为
        /// <c>Undo.AddComponent</c> 在 Play Mode 中会抛异常）；否则走正常 Undo 记录路径。
        /// </summary>
        public static Component AddComponent(GameObject go, Type type)
            => IsActive ? go.AddComponent(type) : Undo.AddComponent(go, type);

        /// <summary>
        /// 将场景（或当前 Prefab Stage 场景）标记为已修改，用于触发保存提示 / 序列化。
        /// <para>
        /// Play Mode 中为 no-op：<see cref="EditorSceneManager.MarkSceneDirty"/> 在 Play Mode 下会直接
        /// 抛出 "This cannot be used during play mode" 异常（已验证）。Play Mode 中的场景修改本就是
        /// 运行时内存态，退出后自动还原，标脏没有意义也没有落盘路径。
        /// </para>
        /// </summary>
        public static void MarkSceneDirty(GameObject go)
        {
            if (IsActive)
                return;

            var prefabStage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage != null)
                EditorSceneManager.MarkSceneDirty(prefabStage.scene);
            else
                EditorSceneManager.MarkSceneDirty(go.scene);
        }

        /// <summary>销毁对象。Play Mode 中跳过 Undo 记录，直接 <c>Object.DestroyImmediate</c>。</summary>
        public static void DestroyObjectImmediate(UnityEngine.Object obj)
        {
            if (IsActive)
                UnityEngine.Object.DestroyImmediate(obj);
            else
                Undo.DestroyObjectImmediate(obj);
        }

        /// <summary>记录对象修改前状态。Play Mode 中为 no-op（无需回滚追踪）。</summary>
        public static void RecordObject(UnityEngine.Object obj, string name)
        {
            if (!IsActive)
                Undo.RecordObject(obj, name);
        }

        /// <summary>为新建对象注册创建 Undo。Play Mode 中为 no-op。</summary>
        public static void RegisterCreatedObjectUndo(UnityEngine.Object obj, string name)
        {
            if (!IsActive)
                Undo.RegisterCreatedObjectUndo(obj, name);
        }

        /// <summary>记录对象完整状态用于回滚。Play Mode 中为 no-op。</summary>
        public static void RegisterCompleteObjectUndo(UnityEngine.Object obj, string name)
        {
            if (!IsActive)
                Undo.RegisterCompleteObjectUndo(obj, name);
        }

        /// <summary>
        /// 开始一个 Undo 分组（用于批量操作合并为单次 Undo）。
        /// Play Mode 中返回 -1（哨兵值，表示未开启分组），<see cref="EndGroup"/> 会据此跳过。
        /// </summary>
        public static int BeginGroup(string groupName)
        {
            if (IsActive)
                return -1;

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(groupName);
            return group;
        }

        /// <summary>结束由 <see cref="BeginGroup"/> 开启的分组。传入 -1（Play Mode 哨兵值）时为 no-op。</summary>
        public static void EndGroup(int group)
        {
            if (group < 0)
                return;
            Undo.CollapseUndoOperations(group);
        }
    }
}
