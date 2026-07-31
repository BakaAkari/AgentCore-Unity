using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Tools.Safety
{
    /// <summary>
    /// Play Mode 前置检查 (ADR: 1.6.4 §D3, v1.12+ ModifyRuntimeState 分级放行)。
    ///
    /// <para><b>历史策略 (v1.10.x ~ v1.11.x)</b>:所有 write 类工具在 Play Mode 中一律 Block。
    /// 理由:Play Mode 下磁盘文件修改与运行时状态不一致,可能导致修改不生效、退出时状态混乱。
    /// Read 类不受影响。Write 判定基于 <see cref="ToolCapability"/> 位标志。
    /// v1.11+ (Bug X): ReadOnlyActions 白名单命中的 action 即便工具级是 write 类也放行。
    /// </para>
    ///
    /// <para><b>当前策略 (v1.12+ ModifyRuntimeState)</b>:改为<b>分级放行</b>。
    /// 详见 plans/playmode-runtime-state-mutation.md §3.4。
    /// <list type="bullet">
    ///   <item>Read action (工具 ReadOnlyActions 白名单命中) → 放行 (不变)</item>
    ///   <item>工具无 write 能力位 (Capabilities & WriteCapabilities == 0) → 放行 (不变)</item>
    ///   <item><b>硬禁止</b>:工具声明的 <c>PlaymodeHardBlockedActions</c> 命中,或工具 Capabilities
    ///     含 Build/Package/VCS write/ProjectSettings 等<b>触发 Domain Reload 或落盘的全局副作用</b>能力位 → Block</item>
    ///   <item>其余 write action → <b>放行</b>,由工具内的 <see cref="PlaymodeWriteInterceptor"/>
    ///     拦截落盘 API (SaveAssets/SaveScene/WriteFile/CreateAsset) 转为运行时内存操作
    ///     (退出 Playmode 自然消失,等同 Inspector 拖值)</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class PlayModePreflight
    {
        /// <summary>
        /// Write 类能力位组合。任一位置位即视为"会改动项目/场景/构建"的工具。
        /// </summary>
        private const ToolCapability WriteCapabilities =
            ToolCapability.WriteProjectFiles
            | ToolCapability.DeleteProjectFiles
            | ToolCapability.ModifyScene
            | ToolCapability.ModifyAssets
            | ToolCapability.ModifyScripts
            | ToolCapability.ExecuteCode
            | ToolCapability.InstallPackages
            | ToolCapability.BuildPlayer
            | ToolCapability.VersionControlWrite
            | ToolCapability.ModifyProjectSettings
            | ToolCapability.ModifyAgentConfig
            | ToolCapability.BatchExecute;

        /// <summary>
        /// Playmode 硬禁止能力位组合 (v1.12+ ModifyRuntimeState)。
        /// <para>
        /// 这些能力位对应的操作必然触发 Domain Reload 或全局落盘副作用 (Build / Package 安装 /
        /// VCS 写 / ProjectSettings 改动 / AgentConfig 改动 / BatchExecute 外部进程),
        /// 与"运行时内存修改"语义根本冲突,在 Playmode 中一律 Block (不分 action 粒度)。
        /// </para>
        /// <para>
        /// 注意:<see cref="ToolCapability.ExecuteCode"/> 不在此列 —— 用户决策 (plans §八)
        /// 明确 ExecuteCode 在 Phase 3 直接放开 (运行时 REPL 是本方案核心价值)。
        /// ExecuteCode 的安全性由其内部的 forbidden API 静态扫描 + CancellationToken 保障。
        /// </para>
        /// </summary>
        private const ToolCapability PlaymodeHardBlockedCapabilities =
            ToolCapability.BuildPlayer
            | ToolCapability.InstallPackages
            | ToolCapability.VersionControlWrite
            | ToolCapability.ModifyProjectSettings
            | ToolCapability.ModifyAgentConfig
            | ToolCapability.BatchExecute;

        /// <summary>
        /// 跨工具通用的 Playmode 硬禁止 action 名 (v1.12+)。
        /// <para>
        /// 这些 action 无论哪个工具声明,在 Playmode 中都 Block —— 它们必然触发落盘 /
        /// Domain Reload / Build,与运行时内存修改语义冲突。
        /// </para>
        /// <para>
        /// 工具级 <c>PlaymodeHardBlockedActions</c> 字段可在此基础上追加工具特定的硬禁止 action
        /// (如 manage_scene 的 save_scene);本集合作为全局兜底,避免漏标。
        /// </para>
        /// </summary>
        private static readonly HashSet<string> GlobalHardBlockedActions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "save_scene", "save_all_scenes", "save_assets",
                "create_asset", "delete_asset", "move_asset", "rename_asset",
                "save_prefab", "apply_prefab", "save_as_prefab",
                "install", "uninstall", "add_package", "remove_package",
                "build", "build_player", "build_bundles",
                "domain_reload", "recompile", "request_script_compilation",
                "import_asset", "refresh_asset_database"
            };

        /// <summary>
        /// 主入口: 检查工具在 Play Mode 中是否被禁止执行 (v1.12+ 分级放行)。
        /// </summary>
        /// <param name="metadata">工具元数据 (含 Capabilities / ReadOnlyActions / PlaymodeHardBlockedActions)</param>
        /// <param name="action">当前调用的 action 名 (从 parameters["action"] 提取, 可为 null)</param>
        /// <param name="reason">若返回 true，输出对 LLM/用户可读的拒绝原因</param>
        /// <returns>true = 应该阻止执行；false = 放行</returns>
        public static bool IsBlockedInPlayMode(ToolMetadata metadata, string action, out string reason)
        {
            reason = null;

            if (!EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isPlaying)
                return false;

            if (metadata == null || (metadata.Capabilities & WriteCapabilities) == 0)
                return false;

            // Bug X (v1.11+): 若 action 在工具的只读白名单内, 即便工具级 Capabilities
            // 是 write 类也放行 (纯读 action 不会改动磁盘/场景/构建状态)。
            if (metadata.IsReadOnlyAction(action))
                return false;

            // v1.12+ ModifyRuntimeState: 硬禁止能力位 (Build/Package/VCS write/ProjectSettings 等)
            // —— 这些操作触发 Domain Reload 或全局落盘,与运行时内存修改语义根本冲突。
            if ((metadata.Capabilities & PlaymodeHardBlockedCapabilities) != 0)
            {
                reason =
                    $"Play Mode 中禁止执行工具 '{metadata.Name}'。\n" +
                    "原因：该工具触达 Build / Package / VCS 写 / ProjectSettings / AgentConfig / BatchExecute 等" +
                    "会触发 Domain Reload 或全局落盘副作用的能力位，与运行时内存修改语义冲突，" +
                    "执行会破坏 Play Mode 会话。\n" +
                    "解决：请先退出 Play Mode 再重试。";
                return true;
            }

            // v1.12+ ModifyRuntimeState: 硬禁止 action (工具声明 + 全局兜底)
            // —— 涉及落盘 / Domain Reload / Build 的具体 action 一律 Block。
            if (IsHardBlockedAction(metadata, action, out var hardReason))
            {
                reason = hardReason;
                return true;
            }

            // 其余 write action 放行,交给工具内的 PlaymodeWriteInterceptor 拦截落盘调用,
            // 转为运行时内存操作 (退出 Playmode 自然消失)。
            // 工具若未改造 (未通过 Interceptor 路由落盘 API),其落盘调用会照常执行 ——
            // 这是 Phase 2 全面改造前的已知过渡态,核心 3 工具 (SO/Scene/Script) 已在 Phase 1 改造。
            return false;
        }

        /// <summary>
        /// 判断给定 action 是否属于 Playmode 硬禁止 (v1.12+)。
        /// <para>
        /// 双重判定:工具声明的 <c>PlaymodeHardBlockedActions</c> + 全局 <see cref="GlobalHardBlockedActions"/>。
        /// </para>
        /// </summary>
        private static bool IsHardBlockedAction(ToolMetadata metadata, string action, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(action))
                return false;

            bool blocked = GlobalHardBlockedActions.Contains(action)
                           || (metadata != null && metadata.IsPlaymodeHardBlockedAction(action));
            if (!blocked)
                return false;

            reason =
                $"Play Mode 中禁止执行 action '{action}' (工具 '{metadata?.Name ?? "(unknown)"}')。\n" +
                "原因：该 action 涉及磁盘写 / Domain Reload / Build 等会破坏运行时内存修改语义的操作，" +
                "在 Play Mode 中硬禁止。\n" +
                "解决：请先退出 Play Mode 再重试该 action。其余 write action 可在 Play Mode 中以" +
                "运行时内存方式执行 (退出 Play Mode 后自然消失)。";
            return true;
        }

        /// <summary>
        /// 兼容旧签名 (v1.10.x 及更早)。新代码应使用带 metadata + action 的重载。
        /// <para>
        /// 注意:本重载无法感知 ReadOnlyActions / PlaymodeHardBlockedActions,采用保守的硬禁止能力位
        /// 判定 (即 v1.12+ 的 PlaymodeHardBlockedCapabilities + 其余 write 放行)。仅用于历史调用点兼容。
        /// </para>
        /// </summary>
        public static bool IsBlockedInPlayMode(ToolCapability capabilities, out string reason)
        {
            reason = null;

            if (!EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isPlaying)
                return false;

            if ((capabilities & WriteCapabilities) == 0)
                return false;

            // v1.12+ 语义:仅硬禁止能力位 Block,其余 write 放行 (由 Interceptor 兜底)。
            if ((capabilities & PlaymodeHardBlockedCapabilities) != 0)
            {
                reason =
                    "Play Mode 中禁止执行该工具。\n" +
                    "原因：该工具触达 Build / Package / VCS 写 / ProjectSettings / AgentConfig / BatchExecute 等" +
                    "会触发 Domain Reload 或全局落盘副作用的能力位。\n" +
                    "解决：请先退出 Play Mode 再重试。";
                return true;
            }

            return false;
        }
    }
}
