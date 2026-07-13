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
        /// 检查工具在 Play Mode 中是否被禁止执行。
        /// </summary>
        /// <param name="capabilities">工具的能力位集合</param>
        /// <param name="reason">若返回 true，输出对 LLM/用户可读的拒绝原因</param>
        /// <returns>true = 应该阻止执行；false = 放行</returns>
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
