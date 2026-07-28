using UnityEditor;

namespace AgentCore.Editor.Tools.Safety
{
    /// <summary>
    /// Play Mode 前置检查（ADR: 1.6.4 §D3）。
    ///
    /// 场景：Unity 处于 Play Mode 时，磁盘文件修改与运行时状态不一致，
    ///   LLM 修改代码或场景往往不会立即生效，且退出 Play Mode 时可能造成状态混乱、
    ///   序列化冲突、Scene 反序列化错误。
    ///
    /// 策略：所有 write 类工具在 Play Mode 中一律 Block，Read 类不受影响。
    ///   Write 判定基于 <see cref="ToolCapability"/> 位标志：任一 write 位置位即视为 write 工具。
    ///   v1.11+ (Bug X): 若工具声明了 ReadOnlyActions 白名单且当前 action 命中,
    ///     即便工具级 Capabilities 是 write 类, 也放行 — 对齐 <see cref="ToolRiskPolicy"/>
    ///     line 209 / 228 的粒度修复语义 (多 action 混合读写工具的只读 action 不应连坐)。
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
        /// v1.11+ 主入口: 检查工具在 Play Mode 中是否被禁止执行 (支持 ReadOnlyActions 白名单)。
        /// </summary>
        /// <param name="metadata">工具元数据 (含 Capabilities 和 ReadOnlyActions)</param>
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

            reason =
                "Play Mode 中禁止执行 write 类工具。\n" +
                "原因：Play Mode 下修改磁盘文件与运行时状态不一致，" +
                "可能导致修改不生效、退出 Play Mode 时状态混乱、Scene 序列化冲突。\n" +
                "解决：请先退出 Play Mode 再重试。";
            return true;
        }

        /// <summary>
        /// 兼容旧签名 (v1.10.x 及更早)。新代码应使用带 metadata + action 的重载。
        /// </summary>
        public static bool IsBlockedInPlayMode(ToolCapability capabilities, out string reason)
        {
            reason = null;

            if (!EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isPlaying)
                return false;

            if ((capabilities & WriteCapabilities) == 0)
                return false;

            reason =
                "Play Mode 中禁止执行 write 类工具。\n" +
                "原因：Play Mode 下修改磁盘文件与运行时状态不一致，" +
                "可能导致修改不生效、退出 Play Mode 时状态混乱、Scene 序列化冲突。\n" +
                "解决：请先退出 Play Mode 再重试。";
            return true;
        }
    }
}
