namespace AgentCore.Editor.Tools.Safety
{
    /// <summary>
    /// 工具确认后的短期信任范围。
    /// </summary>
    public enum ToolConfirmationTrustScope
    {
        /// <summary>只批准当前这一次工具调用。</summary>
        Once = 0,

        /// <summary>在当前 ChatWindow 会话生命周期内信任同一工具、action 与目标集合。</summary>
        SessionExactTarget = 1
    }
}
