namespace AgentCore.Editor.Extensions
{
    /// <summary>
    /// Defines a settings contribution that can be discovered and drawn inside AgentCore settings.
    /// </summary>
    public interface IAgentCoreSettingsContribution
    {
        /// <summary>
        /// Gets the stable unique identifier for this settings contribution.
        /// </summary>
        string Id { get; }

        /// <summary>
        /// Gets the section title displayed in AgentCore settings.
        /// </summary>
        string Title { get; }

        /// <summary>
        /// Gets a short description for this settings contribution.
        /// </summary>
        string Description { get; }

        /// <summary>
        /// Gets the sorting order used when drawing contributed settings sections.
        /// </summary>
        int Order { get; }

        /// <summary>
        /// Draws this contribution using the active settings GUI context.
        /// </summary>
        void DrawGUI();
    }
}
