using AgentCore.Editor.Extensions;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Components
{
    /// <summary>
    /// Built-in AgentCore Hub contribution for the Memory panel.
    /// </summary>
    public sealed class MemoryPanelContribution : IAgentCorePanelContribution
    {
        /// <summary>
        /// Gets the stable panel identifier.
        /// </summary>
        public string Id => "memory";

        /// <summary>
        /// Gets the Hub navigation label.
        /// </summary>
        public string Label => "Mem";

        /// <summary>
        /// Gets the Hub navigation tooltip.
        /// </summary>
        public string Tooltip => "Memory";

        /// <summary>
        /// Gets the Hub sorting order.
        /// </summary>
        public int Order => 200;

        /// <summary>
        /// Creates the Memory panel visual tree.
        /// </summary>
        /// <returns>The Memory panel root element.</returns>
        public VisualElement CreatePanel()
        {
            return new MemoryPanel();
        }

        /// <summary>
        /// Refreshes the Memory panel when activated.
        /// </summary>
        /// <param name="panel">The panel instance.</param>
        public void OnActivated(VisualElement panel)
        {
            if (panel is MemoryPanel memoryPanel)
            {
                memoryPanel.OnActivated();
            }
        }

        /// <summary>
        /// Deactivates the Memory panel.
        /// </summary>
        /// <param name="panel">The panel instance.</param>
        public void OnDeactivated(VisualElement panel)
        {
            if (panel is MemoryPanel memoryPanel)
            {
                memoryPanel.OnDeactivated();
            }
        }
    }
}
