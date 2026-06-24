using System;
using AgentCore.Editor.Tools.Safety;

namespace AgentCore.Editor.Tools.Infrastructure
{
    /// <summary>
    /// 标记一个类为 AgentCore 原生工具。
    /// 被标记的类必须实现 IAgentTool 接口。
    /// ToolAutoDiscovery 会自动扫描并注册所有带此属性的类。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class AgentToolAttribute : Attribute
    {
        /// <summary>
        /// 工具名称（LLM 调用时使用的标识符）
        /// 例如: "manage_scene", "find_gameobjects"
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// 工具描述（LLM 用来理解工具用途）
        /// </summary>
        public string Description { get; set; } = "";

        /// <summary>
        /// 工具分类
        /// 例如: "Scene", "GameObject", "Script", "Asset"
        /// </summary>
        public string Category { get; set; } = "General";

        /// <summary>
        /// 是否需要在 Unity 主线程执行
        /// 大多数 Unity API 调用需要主线程
        /// </summary>
        public bool RequiresMainThread { get; set; } = true;

        /// <summary>
        /// 此工具是否可能修改脚本文件（触发编译）
        /// 用于 AgentLoop 的编译等待逻辑
        /// </summary>
        public bool MayModifyScripts { get; set; } = false;

        // ---------------------------------------------------------------
        // G.1 治理层 — 工具风险元数据
        // 所有字段都带默认值，未声明的工具自动被视为 Medium 风险。
        // 现有 51 个工具无需修改即可继续编译。
        // ---------------------------------------------------------------

        /// <summary>
        /// 工具风险等级（G.1 治理层）。
        /// <para>未显式声明时，<see cref="Safety.ToolRiskPolicy"/> 视为 <see cref="ToolRiskLevel.Medium"/>。</para>
        /// <para>声明 <see cref="ToolRiskLevel.CodeExecution"/> 的工具会被无条件强制确认。</para>
        /// </summary>
        public ToolRiskLevel RiskLevel { get; set; } = ToolRiskLevel.Medium;

        /// <summary>
        /// 工具实际触达的能力位（G.1 治理层）。
        /// <para>用于审计、MCP 暴露过滤、确认面板呈现。</para>
        /// </summary>
        public ToolCapability Capabilities { get; set; } = ToolCapability.None;

        /// <summary>
        /// 是否强制要求用户确认（G.1 治理层）。
        /// <para>
        /// 即便风险等级不高，如果声明 <c>true</c>，<see cref="Safety.ToolRiskPolicy"/> 也会要求用户确认。
        /// 用于"操作虽然小但必须留痕"的工具（如修改 AgentCore 自身配置）。
        /// </para>
        /// </summary>
        public bool RequiresConfirmation { get; set; } = false;

        // ---------------------------------------------------------------
        // G.3 ActiveToolScope — 工具可见性
        // 默认 AlwaysVisible，现有工具无需修改即可保持向后兼容。
        // ---------------------------------------------------------------

        /// <summary>
        /// 工具对 LLM 的可见性级别（G.3 ActiveToolScope）。
        /// <para>
        /// <see cref="ToolVisibility.AlwaysVisible"/> — 每轮都发送给 LLM（默认）。
        /// <see cref="ToolVisibility.OnDemand"/> — LLM 通过 request_tools 激活后才可见。
        /// <see cref="ToolVisibility.Restricted"/> — 仅在用户显式启用且 LLM 请求后才可见。
        /// </para>
        /// </summary>
        public ToolVisibility Visibility { get; set; } = ToolVisibility.AlwaysVisible;

        public AgentToolAttribute(string name)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
        }
    }
}
