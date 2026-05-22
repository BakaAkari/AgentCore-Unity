namespace AgentCore.Editor.Config.Settings
{
    /// <summary>
    /// Describes a standalone AgentCore settings page section that can be rendered by the settings hub.
    /// </summary>
    public interface IAgentCoreSettingsSection
    {
        /// <summary>
        /// Gets the stable section identifier. This value must not change when the display title changes.
        /// </summary>
        string Id { get; }

        /// <summary>
        /// Gets the user-facing section title shown in navigation.
        /// </summary>
        string Title { get; }

        /// <summary>
        /// Gets the short section description shown in the content header.
        /// </summary>
        string Description { get; }

        /// <summary>
        /// Gets the broad category used for grouping and future filtering.
        /// </summary>
        string Category { get; }

        /// <summary>
        /// Gets the navigation sort order.
        /// </summary>
        int Order { get; }

        /// <summary>
        /// Returns whether this section should be visible for the current context.
        /// </summary>
        /// <param name="context">The current settings context.</param>
        /// <returns>True if the section should be shown.</returns>
        bool IsVisible(AgentCoreSettingsContext context);

        /// <summary>
        /// Called when the section becomes the active section.
        /// </summary>
        /// <param name="context">The current settings context.</param>
        void OnActivate(AgentCoreSettingsContext context);

        /// <summary>
        /// Called when the section is no longer the active section.
        /// </summary>
        /// <param name="context">The current settings context.</param>
        void OnDeactivate(AgentCoreSettingsContext context);

        /// <summary>
        /// Draws the section content.
        /// </summary>
        /// <param name="context">The current settings context.</param>
        void Draw(AgentCoreSettingsContext context);
    }
}
