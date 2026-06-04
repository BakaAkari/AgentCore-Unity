namespace AgentCore.Editor.Config.Settings.Pages
{
    /// <summary>
    /// Describes a settings page rendered inside the AgentCore Project Settings hub.
    /// </summary>
    public interface IAgentCoreSettingsPage
    {
        /// <summary>
        /// Gets the stable page identifier.
        /// </summary>
        string Id { get; }

        /// <summary>
        /// Gets the tab label shown in the top navigation.
        /// </summary>
        string Title { get; }

        /// <summary>
        /// Gets the page description shown below the title.
        /// </summary>
        string Description { get; }

        /// <summary>
        /// Gets the sort order for tab positioning.
        /// Built-in pages use 0–500; optional component pages should use 600+.
        /// </summary>
        int Order { get; }

        /// <summary>
        /// Called when this page becomes the active page.
        /// </summary>
        /// <param name="context">The current settings context.</param>
        void OnActivate(AgentCoreSettingsContext context);

        /// <summary>
        /// Called when this page is no longer the active page.
        /// </summary>
        /// <param name="context">The current settings context.</param>
        void OnDeactivate(AgentCoreSettingsContext context);

        /// <summary>
        /// Draws the page content.
        /// </summary>
        /// <param name="context">The current settings context.</param>
        void Draw(AgentCoreSettingsContext context);
    }
}
