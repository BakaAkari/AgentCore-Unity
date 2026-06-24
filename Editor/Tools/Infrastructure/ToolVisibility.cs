namespace AgentCore.Editor.Tools.Infrastructure
{
    /// <summary>
    /// 工具对 LLM 的可见性级别（G.3 ActiveToolScope）。
    /// <para>
    /// 控制工具在每轮 LLM 调用中是否被包含在 tool definitions 中。
    /// 通过减少每轮暴露的工具数量，降低 token 消耗并提升 LLM 决策质量。
    /// </para>
    /// </summary>
    public enum ToolVisibility
    {
        /// <summary>
        /// 始终对 LLM 可见。用于核心高频工具（场景、GameObject、组件等）。
        /// </summary>
        AlwaysVisible = 0,

        /// <summary>
        /// 按需可见。LLM 需要通过 request_tools 元工具主动激活对应分类后才可见。
        /// 用于低频工具（优化、清理、测试、构建、包管理等）。
        /// </summary>
        OnDemand = 1,

        /// <summary>
        /// 受限可见。仅在用户显式启用且 LLM 请求后才可见。
        /// 用于高风险工具（execute_code 等）。
        /// </summary>
        Restricted = 2
    }
}
