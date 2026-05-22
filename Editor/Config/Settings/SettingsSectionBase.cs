namespace AgentCore.Editor.Config.Settings
{
    /// <summary>
    /// Convenience base class for AgentCore settings sections.
    /// </summary>
    public abstract class SettingsSectionBase : IAgentCoreSettingsSection
    {
        /// <inheritdoc />
        public abstract string Id { get; }

        /// <inheritdoc />
        public abstract string Title { get; }

        /// <inheritdoc />
        public virtual string Description => string.Empty;

        /// <inheritdoc />
        public virtual string Category => "General";

        /// <inheritdoc />
        public virtual int Order => 0;

        /// <inheritdoc />
        public virtual bool IsVisible(AgentCoreSettingsContext context)
        {
            return true;
        }

        /// <inheritdoc />
        public virtual void OnActivate(AgentCoreSettingsContext context)
        {
        }

        /// <inheritdoc />
        public virtual void OnDeactivate(AgentCoreSettingsContext context)
        {
        }

        /// <inheritdoc />
        public abstract void Draw(AgentCoreSettingsContext context);
    }
}
