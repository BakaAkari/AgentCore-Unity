using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Tools.Safety
{
    /// <summary>
    /// Play Mode 前置检查 (ADR: 1.6.4 §D3, v1.13+ ModifyRuntimeState 白名单反转)。
    ///
    /// <para><b>历史策略 (v1.10.x ~ v1.11.x)</b>:所有 write 类工具在 Play Mode 中一律 Block。
    /// 理由:Play Mode 下磁盘文件修改与运行时状态不一致,可能导致修改不生效、退出时状态混乱。
    /// Read 类不受影响。Write 判定基于 <see cref="ToolCapability"/> 位标志。
    /// v1.11+ (Bug X): ReadOnlyActions 白名单命中的 action 即便工具级是 write 类也放行。
    /// </para>
    ///
    /// <para><b>v1.12 alpha 策略 (已废弃 — 存在真实漏洞)</b>:曾改为"黑名单硬禁止、其余默认放行"。
    /// 审查发现该模型的假设（"未被列入硬禁止的 write action 都已接入 PlaymodeWriteInterceptor"）
    /// 不成立：ManageFileTool/CleanerTool/ManageCameraTool 等多个工具声明了写能力或直接调用
    /// AssetDatabase/File 落盘 API，既未接入 Interceptor 也未列入黑名单，在 Play Mode 中被
    /// 默认放行后真实写盘/删盘，与"运行时改动退出即消失"的核心语义矛盾。
    /// </para>
    ///
    /// <para><b>当前策略 (v1.13+ 白名单反转, fail-closed)</b>:
    /// <list type="bullet">
    ///   <item>Read action (工具 ReadOnlyActions 白名单命中) → 放行 (不变)</item>
    ///   <item>工具无 write 能力位 (Capabilities & WriteCapabilities == 0) → 放行 (不变)</item>
    ///   <item><b>硬禁止</b>:工具声明的 <c>PlaymodeHardBlockedActions</c> 命中,或工具 Capabilities
    ///     含 Build/Package/VCS write/ProjectSettings 等<b>触发 Domain Reload 或落盘的全局副作用</b>能力位 → Block</item>
    ///   <item><b>放行</b>:仅当 action 命中工具声明的 <c>PlaymodeRuntimeSafeActions</c> 白名单 →
    ///     放行,由工具内的 <see cref="PlaymodeWriteInterceptor"/> 拦截落盘 API 转为运行时内存操作
    ///     (退出 Playmode 自然消失,等同 Inspector 拖值)</item>
    ///   <item><b>其余 write action 一律硬禁止</b> (fail-closed 默认值) —— 未显式登记进白名单的
    ///     write action,无论是否声明了硬禁止,都视为"未验证运行时安全"而拒绝执行。</item>
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
        /// 主入口: 检查工具在 Play Mode 中是否被禁止执行 (v1.13+ 白名单反转, fail-closed)。
        /// </summary>
        /// <param name="metadata">工具元数据 (含 Capabilities / ReadOnlyActions / PlaymodeHardBlockedActions / PlaymodeRuntimeSafeActions)</param>
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

            // v1.12+ ModifyRuntimeState: 硬禁止 action (工具声明 + 全局兜底) —— 检查顺序在
            // 能力位/白名单判定之前。涉及落盘 / Domain Reload / Build 的具体 action 是最强显式
            // 否决信号,即便该 action 后续被误放进白名单也优先拒绝 (defense in depth)。
            if (IsHardBlockedAction(metadata, action, out var hardReason))
            {
                reason = hardReason;
                return true;
            }

            // v1.13+ 白名单反转 (fail-closed): 只要 action 显式登记进 PlaymodeRuntimeSafeActions,
            // 即可放行 —— 由工具内 PlaymodeWriteInterceptor 拦截落盘,转为运行时内存操作。
            // 该判定必须先于下方的"硬禁止能力位"粗粒度检查:后者是工具级(而非 action 级)拦截,
            // 若排在白名单之前会导致任何声明了硬禁止能力位的工具永远走不到白名单
            // (真实案例 v1.13.0: manage_editor 声明 ModifyProjectSettings 能力位被整体 Block,
            // 连 play_mode:stop 这个纯运行时状态切换、退出 Play Mode 的唯一工具路径也被误伤)。
            if (metadata.IsPlaymodeRuntimeSafeAction(action))
                return false;

            // v1.12+ ModifyRuntimeState: 硬禁止能力位 (Build/Package/VCS write/ProjectSettings 等)
            // —— 这些操作触发 Domain Reload 或全局落盘,与运行时内存修改语义根本冲突。
            // 仅在上面的白名单未命中时才生效,即"未被显式验证为运行时安全"的 action 才受此粗粒度拦截。
            if ((metadata.Capabilities & PlaymodeHardBlockedCapabilities) != 0)
            {
                reason =
                    $"Play Mode 中禁止执行工具 '{metadata.Name}' 的 action '{action ?? "(unspecified)"}'。\n" +
                    "原因：该工具触达 Build / Package / VCS 写 / ProjectSettings / AgentConfig / BatchExecute 等" +
                    "会触发 Domain Reload 或全局落盘副作用的能力位，此 action 未被显式登记为运行时安全，" +
                    "执行会破坏 Play Mode 会话。\n" +
                    "解决：请先退出 Play Mode 再重试；如该 action 确实只产生运行时内存效果 (不触发 Domain Reload/落盘)，" +
                    "应在工具的 [AgentTool] 特性中把它加入 PlaymodeRuntimeSafeActions 白名单。";
                return true;
            }

            reason =
                $"Play Mode 中禁止执行工具 '{metadata.Name}' 的 action '{action ?? "(unspecified)"}'。\n" +
                "原因：该 action 未被显式登记为 Playmode 运行时安全 (PlaymodeRuntimeSafeActions)，" +
                "无法确认其落盘调用已被 PlaymodeWriteInterceptor 拦截或本身不落盘，按 fail-closed 策略默认拒绝。\n" +
                "解决：请先退出 Play Mode 再重试；如该 action 确实只产生运行时内存效果，" +
                "应在工具的 [AgentTool] 特性中把它加入 PlaymodeRuntimeSafeActions 白名单。";
            return true;
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
