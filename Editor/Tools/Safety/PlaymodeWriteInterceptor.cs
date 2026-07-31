using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AgentCore.Editor.Tools.Safety
{
    /// <summary>
    /// Playmode 写操作拦截器 (v1.12+ ModifyRuntimeState)。
    /// <para>
    /// 核心设计 (plans/playmode-runtime-state-mutation.md §3.2):
    /// 在 Playmode 中,所有 write 类工具允许执行,但落盘 API 调用被拦截转为 NoOp。
    /// 这使 Agent 能在运行时修改内存对象 (GameObject/Component/ScriptableObject 字段),
    /// 修改在退出 Playmode 时自然消失 (Unity 原生行为),无需人工回滚。
    /// </para>
    /// <para>
    /// 语义等同:Unity 用户在 Play 中拖 Inspector 值 —— 临时生效,退出还原。
    /// </para>
    /// <para>
    /// 使用方式:write 类工具在调用底层 Unity 落盘 API 前,改用本类的静态包装方法。
    /// 返回值 <c>true</c> = 已落盘; <c>false</c> = Playmode 中被跳过 (调用方应在结果中
    /// 标注 <c>_runtime_only</c> 提示 Agent)。
    /// </para>
    /// </summary>
    public static class PlaymodeWriteInterceptor
    {
        /// <summary>
        /// 判断当前是否处于 Playmode (含即将进入/正在播放/即将退出)。
        /// 工具内部可复用此判断,避免重复查询 <see cref="EditorApplication"/>。
        /// </summary>
        public static bool IsPlaymodeActive =>
            EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode;

        /// <summary>
        /// 拦截 <c>AssetDatabase.SaveAssets()</c>。
        /// <para>
        /// Playmode 中跳过落盘 (ScriptableObject 等 asset 内存修改已通过 ApplyModifiedProperties 生效,
        /// 仅磁盘未同步 —— 这正是运行时调试期望的行为)。
        /// </para>
        /// </summary>
        /// <returns><c>true</c> 已落盘; <c>false</c> Playmode 中跳过。</returns>
        public static bool SaveAssets()
        {
            if (IsPlaymodeActive)
            {
                AgentCore.Editor.Utils.AgentCoreLog.PlaymodeIntercept(
                    "AssetDatabase.SaveAssets",
                    "Skipped in Playmode (runtime-only mutation). Asset in-memory values remain; disk file unchanged.");
                PlaymodeChangeLog.Record("(interceptor)", "SaveAssets", "assets",
                    "AssetDatabase.SaveAssets skipped in Play Mode; in-memory asset changes not persisted to disk.");
                return false;
            }
            AssetDatabase.SaveAssets();
            return true;
        }

        /// <summary>
        /// 拦截 <c>AssetDatabase.SaveAssetIfDirty(obj)</c>。
        /// </summary>
        /// <returns><c>true</c> 已落盘; <c>false</c> Playmode 中跳过。</returns>
        public static bool SaveAssetIfDirty(UnityEngine.Object asset)
        {
            if (IsPlaymodeActive)
            {
                AgentCore.Editor.Utils.AgentCoreLog.PlaymodeIntercept(
                    "AssetDatabase.SaveAssetIfDirty",
                    $"Skipped in Playmode for asset '{(asset != null ? asset.name : "null")}' (runtime-only mutation).");
                return false;
            }
            return AssetDatabase.SaveAssetIfDirty(asset);
        }

        /// <summary>
        /// 拦截 <c>EditorSceneManager.SaveScene(scene)</c> 或带 path 重载。
        /// <para>
        /// Playmode 中场景对象修改只在内存生效 (Unity 进入 Play 时序列化快照,退出时恢复),
        /// 显式 SaveScene 会把运行时脏状态落盘 —— 与运行时调试语义冲突,必须拦截。
        /// </para>
        /// </summary>
        /// <param name="scene">要保存的场景</param>
        /// <param name="path">目标路径; null/空 则用场景当前路径</param>
        /// <returns><c>true</c> 已落盘; <c>false</c> Playmode 中跳过。</returns>
        public static bool SaveScene(Scene scene, string path = null)
        {
            if (IsPlaymodeActive)
            {
                AgentCore.Editor.Utils.AgentCoreLog.PlaymodeIntercept(
                    "EditorSceneManager.SaveScene",
                    $"Skipped in Playmode: scene '{scene.name}' modifications are runtime-only (disk untouched).");
                PlaymodeChangeLog.Record("(interceptor)", "SaveScene", scene.name,
                    $"SaveScene skipped in Play Mode; runtime scene changes to '{scene.name}' not persisted.");
                return false;
            }
            return string.IsNullOrEmpty(path)
                ? EditorSceneManager.SaveScene(scene)
                : EditorSceneManager.SaveScene(scene, path);
        }

        /// <summary>
        /// 拦截 <c>File.WriteAllText(path, content)</c>。
        /// <para>
        /// <c>.cs</c> 源码在 Playmode 中修改无意义 (需 Domain Reload 才生效,而 Domain Reload 会退出 Playmode)。
        /// 其他类型文件默认也拦截 —— 走 AssetDatabase 路径更安全且与拦截器语义一致。
        /// </para>
        /// </summary>
        /// <returns><c>true</c> 已落盘; <c>false</c> Playmode 中跳过。</returns>
        public static bool WriteFile(string path, string content)
        {
            if (IsPlaymodeActive)
            {
                if (path != null && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    AgentCore.Editor.Utils.AgentCoreLog.PlaymodeIntercept(
                        "File.WriteAllText",
                        $"Refused in Playmode: source file '{path}' modifications require Domain Reload (would exit Playmode).");
                }
                else
                {
                    AgentCore.Editor.Utils.AgentCoreLog.PlaymodeIntercept(
                        "File.WriteAllText",
                        $"Skipped in Playmode: file '{path}' not written to disk (runtime-only context).");
                }
                return false;
            }
            File.WriteAllText(path, content);
            return true;
        }

        /// <summary>
        /// 拦截 <c>File.WriteAllBytes(path, bytes)</c>。
        /// </summary>
        /// <returns><c>true</c> 已落盘; <c>false</c> Playmode 中跳过。</returns>
        public static bool WriteAllBytes(string path, byte[] bytes)
        {
            if (IsPlaymodeActive)
            {
                AgentCore.Editor.Utils.AgentCoreLog.PlaymodeIntercept(
                    "File.WriteAllBytes",
                    $"Skipped in Playmode: file '{path}' not written to disk (runtime-only context).");
                return false;
            }
            File.WriteAllBytes(path, bytes);
            return true;
        }

        /// <summary>
        /// 拦截 <c>AssetDatabase.CreateAsset(obj, path)</c>。
        /// <para>
        /// 创建 asset 一定落盘 —— 与运行时调试语义冲突,Playmode 中拒绝。
        /// (Agent 若需临时对象,应直接 new 内存实例或 Instantiate,无需落盘。)
        /// </para>
        /// </summary>
        /// <returns><c>true</c> 已创建并落盘; <c>false</c> Playmode 中拒绝。</returns>
        public static bool CreateAsset(UnityEngine.Object obj, string path)
        {
            if (IsPlaymodeActive)
            {
                AgentCore.Editor.Utils.AgentCoreLog.PlaymodeIntercept(
                    "AssetDatabase.CreateAsset",
                    $"Refused in Playmode: cannot create asset '{path}' at runtime (would write to disk). Use in-memory instances for runtime testing.");
                return false;
            }
            AssetDatabase.CreateAsset(obj, path);
            return true;
        }

        /// <summary>
        /// 拦截 <c>AssetDatabase.DeleteAsset(path)</c>。
        /// <para>
        /// 磁盘删除是不可撤销的破坏操作,Playmode 中拒绝。
        /// </para>
        /// </summary>
        /// <returns><c>true</c> 已删除; <c>false</c> Playmode 中拒绝。</returns>
        public static bool DeleteAsset(string path)
        {
            if (IsPlaymodeActive)
            {
                AgentCore.Editor.Utils.AgentCoreLog.PlaymodeIntercept(
                    "AssetDatabase.DeleteAsset",
                    $"Refused in Playmode: cannot delete asset '{path}' (irreversible disk operation). Exit Playmode first.");
                return false;
            }
            return AssetDatabase.DeleteAsset(path);
        }

        /// <summary>
        /// 拦截 <c>AssetDatabase.MoveAsset(src, dst)</c>。
        /// </summary>
        /// <returns>移动结果代码; Playmode 中返回 <see cref="AssetDatabase.MoveAsset"/> 失败语义。</returns>
        public static string MoveAsset(string sourcePath, string destinationPath)
        {
            if (IsPlaymodeActive)
            {
                AgentCore.Editor.Utils.AgentCoreLog.PlaymodeIntercept(
                    "AssetDatabase.MoveAsset",
                    $"Refused in Playmode: cannot move asset '{sourcePath}' → '{destinationPath}' (disk operation).");
                return "Move was refused in Playmode.";
            }
            return AssetDatabase.MoveAsset(sourcePath, destinationPath);
        }

        /// <summary>
        /// 拦截 <c>AssetDatabase.ImportAsset(path, options)</c>。
        /// <para>
        /// 二次分析结论 (plans/playmode-runtime-state-mutation.md 决策):
        /// ImportAsset 对 .cs 文件会触发 Domain Reload (退出 Playmode),但 .cs 写入在前置的
        /// <see cref="WriteFile"/> 已被拦截,不会到达此路径;对其他类型 (.txt/.json/非脚本) 的
        /// ImportAsset 是幂等的 (从磁盘读旧内容,内存无变化) → 让其 pass through 即可,无需拦截。
        /// </para>
        /// <para>
        /// 此包装方法保留供后续统一审计,当前 Playmode 下直接透传。
        /// </para>
        /// </summary>
        public static void ImportAsset(string path, ImportAssetOptions options = ImportAssetOptions.Default)
        {
            AssetDatabase.ImportAsset(path, options);
        }

        /// <summary>
        /// 拦截 <c>PrefabUtility.SaveAsPrefabAsset(go, path)</c>。
        /// <para>
        /// Prefab 保存本质是落盘 (写 .prefab 文件),Playmode 中拒绝。
        /// (plans §4.3: ApplyPrefabInstance / SaveAsPrefabAsset 必须硬禁止。)
        /// </para>
        /// </summary>
        /// <returns><c>true</c> 已保存; <c>false</c> Playmode 中拒绝。</returns>
        public static bool SaveAsPrefabAsset(GameObject go, string path)
        {
            if (IsPlaymodeActive)
            {
                AgentCore.Editor.Utils.AgentCoreLog.PlaymodeIntercept(
                    "PrefabUtility.SaveAsPrefabAsset",
                    $"Refused in Playmode: cannot save prefab '{path}' (would write to disk). Runtime prefab instance overrides are in-memory only.");
                return false;
            }
            return PrefabUtility.SaveAsPrefabAsset(go, path) != null;
        }
    }
}
