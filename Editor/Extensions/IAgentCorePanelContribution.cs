using UnityEngine.UIElements;

namespace AgentCore.Editor.Extensions
{
    /// <summary>
    /// Defines a panel contribution that can be discovered and mounted into the AgentCore Hub UI.
    /// </summary>
    public interface IAgentCorePanelContribution
    {
        /// <summary>
        /// Gets the stable unique identifier for this panel contribution.
        /// </summary>
        string Id { get; }

        /// <summary>
        /// Gets the short label displayed in the Hub navigation rail.
        /// </summary>
        string Label { get; }

        /// <summary>
        /// Gets the tooltip shown for the Hub navigation entry.
        /// </summary>
        string Tooltip { get; }

        /// <summary>
        /// Gets the sorting order used when rendering contributed panels.
        /// </summary>
        int Order { get; }

        /// <summary>
        /// Creates the root visual element for this contributed panel.
        /// </summary>
        /// <returns>The root visual element to mount into the Hub panel host.</returns>
        VisualElement CreatePanel();

        /// <summary>
        /// Called when this contributed panel becomes the active Hub panel.
        /// </summary>
        /// <param name="panel">The panel instance created by this contribution.</param>
        void OnActivated(VisualElement panel);

        /// <summary>
        /// Called when this contributed panel stops being the active Hub panel.
        /// </summary>
        /// <param name="panel">The panel instance created by this contribution.</param>
        void OnDeactivated(VisualElement panel);
    }
}
