namespace AgentCore.Editor.Tools.Safety
{
    /// <summary>
    /// 工具确认后的会话级信任范围 (v1.6.5 起)。
    /// <para>
    /// v1.6.5 破坏性变更:
    /// - 移除 <c>Once</c> (单次批准) — UI 不再提供"仅此一次允许"选项;
    /// - 移除 <c>SessionExactTarget</c> (精确目标信任) — 该粒度实用价值低;
    /// - 新增 <c>SessionLowMediumRisk</c> — 会话内所有 ReadOnly/Low/Medium 风险工具直通;
    /// - 新增 <c>SessionAll</c> — 会话内所有风险工具直通 (YOLO 模式)。
    /// </para>
    /// <para>
    /// 语义要点:
    /// - 信任范围以 <b>ChatWindow 会话</b>为界,会话切换/清除时自动失效;
    /// - 信任状态仅存于内存 (HashSet),不持久化;
    /// - <c>SessionAll</c> 是纯 YOLO,不设 Critical 硬顶,用户完全自担风险。
    /// </para>
    /// </summary>
    public enum ToolConfirmationTrustScope
    {
        /// <summary>
        /// 本会话内所有 ReadOnly/Low/Medium 风险工具直通。
        /// <para>覆盖 <see cref="ToolRiskLevel.ReadOnly"/>、<see cref="ToolRiskLevel.Low"/>、<see cref="ToolRiskLevel.Medium"/>。</para>
        /// <para>不覆盖 High / Destructive / External / CodeExecution。</para>
        /// </summary>
        SessionLowMediumRisk = 0,

        /// <summary>
        /// YOLO — 本会话内所有工具无条件直通,不区分风险等级。
        /// <para>包含 High / Destructive / External / CodeExecution 等破坏性操作,慎用。</para>
        /// </summary>
        SessionAll = 1
    }
}
