namespace AgentCore.Editor.Tools.Safety
{
    /// <summary>
    /// 工具风险等级（G.1 治理层）。
    /// <para>
    /// 描述一个工具/Action 在最坏情况下对工程的影响范围与可恢复性。
    /// 与 <see cref="AgentCore.Editor.Workspace.Safety.WorkspaceOperationRisk"/> 是
    /// <b>正交</b>关系：
    /// </para>
    /// <list type="bullet">
    ///   <item><description>本枚举描述<b>工具能力本身的危险性</b>（"做了什么"）。</description></item>
    ///   <item><description><c>WorkspaceOperationRisk</c> 描述<b>目标路径的脆弱性</b>（"在哪里做"）。</description></item>
    ///   <item><description>最终风险由 <see cref="ToolRiskPolicy"/> 合并两个维度后产出。</description></item>
    /// </list>
    /// <para>
    /// 默认值规则：未在 <c>[AgentTool]</c> 上显式声明 <c>RiskLevel</c> 的工具，
    /// 由 <see cref="ToolRiskPolicy"/> 视为 <see cref="Medium"/>（保守默认），
    /// 强制要求每个工具在 G.1.d 阶段显式声明，避免漏判。
    /// </para>
    /// </summary>
    public enum ToolRiskLevel
    {
        /// <summary>
        /// 只读 — 不修改任何状态。
        /// <para>例：read_file、list_directory、search_code、get_console_logs。</para>
        /// </summary>
        ReadOnly = 0,

        /// <summary>
        /// 低风险 — 修改局部、可撤销、影响范围明确。
        /// <para>例：select_gameobject、focus_scene_view、log_message。</para>
        /// </summary>
        Low = 1,

        /// <summary>
        /// 中等风险 — 修改 Scene/Asset 内容，影响有限但需关注。
        /// <para>例：create_gameobject、modify_component、create_prefab。</para>
        /// <para>未显式声明 RiskLevel 时的默认值。</para>
        /// </summary>
        Medium = 2,

        /// <summary>
        /// 高风险 — 修改脚本/项目设置/Package 等可触发编译或长期影响的内容。
        /// <para>例：manage_script 写入、modify_player_settings、apply_workspace_config。</para>
        /// </summary>
        High = 3,

        /// <summary>
        /// 破坏性 — 删除、覆盖、移动等不可逆或难恢复的操作。
        /// <para>例：manage_file delete、git_reset_hard、manage_assets delete。</para>
        /// </summary>
        Destructive = 4,

        /// <summary>
        /// 外部副作用 — 发起对外网络/外部进程调用，可能产生不可见副作用或费用。
        /// <para>例：mem0 写入、LightRAG 索引、HTTP API 工具、git_push。</para>
        /// </summary>
        External = 5,

        /// <summary>
        /// 任意代码执行 — 等同于在工程域内运行未审计代码。
        /// <para>例：execute_code、运行外部脚本、动态加载 DLL。</para>
        /// <para>始终强制确认，永远不应被静默放行。</para>
        /// </summary>
        CodeExecution = 6
    }
}
