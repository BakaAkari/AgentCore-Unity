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
        /// <para>当前宽松策略下风险等级仅用于审计展示；删除类 action 仍会触发确认。</para>
        /// </summary>
        public ToolRiskLevel RiskLevel { get; set; } = ToolRiskLevel.Medium;

        /// <summary>
        /// 工具实际触达的能力位（G.1 治理层）。
        /// <para>用于审计、MCP 暴露过滤、确认面板呈现。</para>
        /// </summary>
        public ToolCapability Capabilities { get; set; } = ToolCapability.None;

        /// <summary>
        /// 工具声明的确认偏好（G.1 治理层）。
        /// <para>
        /// 当前宽松策略下本字段仅用于审计展示；删除类 action 仍由 <see cref="Safety.ToolRiskPolicy"/> 强制确认。
        /// </para>
        /// </summary>
        public bool RequiresConfirmation { get; set; } = false;

        /// <summary>
        /// 只读 action 白名单（v1.7.16 治理层粒度修复）。
        /// <para>
        /// 用于<b>多 action 混合读写</b>工具（如 manage_scene 既有只读 get_hierarchy
        /// 又有写操作 create/open/save）。工具级 <see cref="Capabilities"/> / <see cref="RiskLevel"/>
        /// 只能声明一次，会把只读 action 一并连坐触发确认。此列表让工具显式声明哪些 action 是只读的，
        /// <see cref="Safety.ToolRiskPolicy"/> 对命中的 action 跳过风险等级 / 能力位主判据（破坏性 token 兜底仍生效）。
        /// </para>
        /// <para>
        /// 大小写不敏感。纯只读工具（无副作用能力位）或纯写工具无需声明此字段。
        /// </para>
        /// </summary>
        public string[] ReadOnlyActions { get; set; } = System.Array.Empty<string>();

        /// <summary>
        /// Playmode 硬禁止 action 列表（v1.12+ ModifyRuntimeState）。
        /// <para>
        /// 这些 action 在 Playmode 中<b>无论工具 Capabilities 如何一律 Block</b>
        /// （返回错误,不执行）。典型场景:涉及磁盘写、Domain Reload、Build、Package 安装
        /// 的 action —— 它们与"运行时内存修改"语义冲突,且执行会破坏 Playmode 会话。
        /// </para>
        /// <para>
        /// 未列入此处的 write action 在 Playmode 中<b>放行</b>,由工具内的
        /// <see cref="Safety.PlaymodeWriteInterceptor"/> 拦截落盘 API 调用,转为运行时内存操作
        /// (退出 Playmode 自然消失)。例如 manage_scriptable_object 的 "modify" 放行,
        /// 但 "create"/"delete" 应列入此字段。
        /// </para>
        /// <para>
        /// 大小写不敏感。默认空数组 —— 未声明的工具其所有 write action 在 Playmode 中放行
        /// (落盘调用由 Interceptor 兜底拦截)。
        /// </para>
        /// <para>
        /// 详见 plans/playmode-runtime-state-mutation.md §5.2 Action 分类规范。
        /// </para>
        /// </summary>
        public string[] PlaymodeHardBlockedActions { get; set; } = System.Array.Empty<string>();

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
