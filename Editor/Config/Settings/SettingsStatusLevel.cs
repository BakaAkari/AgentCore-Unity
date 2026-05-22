namespace AgentCore.Editor.Config.Settings
{
    /// <summary>
    /// Represents a user-facing status level in the AgentCore settings UI.
    /// </summary>
    public enum SettingsStatusLevel
    {
        /// <summary>
        /// Neutral informational status.
        /// </summary>
        None,

        /// <summary>
        /// Successful operation status.
        /// </summary>
        Success,

        /// <summary>
        /// Warning status that may require attention.
        /// </summary>
        Warning,

        /// <summary>
        /// Error status that requires action.
        /// </summary>
        Error,

        /// <summary>
        /// In-progress operation status.
        /// </summary>
        Loading
    }
}
