using UnityEngine.UIElements;

namespace AgentCore.Editor.Extensions
{
    /// <summary>
    /// Defines a lightweight status contribution that can be mounted into the AgentCore toolbar.
    /// </summary>
    public interface IAgentCoreStatusContribution
    {
        /// <summary>
        /// Gets the stable unique identifier for this status contribution.
        /// </summary>
        string Id { get; }

        /// <summary>
        /// Gets the sorting order used when rendering status elements.
        /// </summary>
        int Order { get; }

        /// <summary>
        /// Creates the toolbar status visual element.
        /// </summary>
        /// <returns>The status visual element to mount into the toolbar.</returns>
        VisualElement CreateStatusElement();
    }
}
