namespace AgentCore.Editor.Config.Settings
{
    /// <summary>
    /// Provides shared dependencies and transient state to AgentCore settings sections.
    /// </summary>
    public sealed class AgentCoreSettingsContext
    {
        /// <summary>
        /// Creates a settings context.
        /// </summary>
        /// <param name="settings">The persistent AgentCore settings.</param>
        /// <param name="state">The transient settings UI state.</param>
        /// <param name="ui">The shared settings UI helpers.</param>
        public AgentCoreSettingsContext(AgentCoreSettings settings, AgentCoreSettingsState state, AgentCoreSettingsUi ui)
        {
            Settings = settings;
            State = state;
            Ui = ui;
        }

        /// <summary>
        /// Gets the persistent AgentCore settings object.
        /// </summary>
        public AgentCoreSettings Settings { get; }

        /// <summary>
        /// Gets the transient settings UI state.
        /// </summary>
        public AgentCoreSettingsState State { get; }

        /// <summary>
        /// Gets shared settings UI helpers.
        /// </summary>
        public AgentCoreSettingsUi Ui { get; }

        /// <summary>
        /// Creates a new settings context using the global AgentCore settings singleton.
        /// </summary>
        /// <returns>A new settings context.</returns>
        public static AgentCoreSettingsContext Create()
        {
            return new AgentCoreSettingsContext(
                AgentCoreSettings.instance,
                new AgentCoreSettingsState(),
                new AgentCoreSettingsUi());
        }
    }
}
