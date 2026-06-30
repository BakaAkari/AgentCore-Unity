using AgentCore.Editor.Config;
using AgentCore.Editor.Extensions;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Components
{
    /// <summary>
    /// Built-in AgentCore Hub contribution for the Memory panel.
    /// Only visible when mem0 service is enabled in settings.
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
        /// Returns null when mem0 is disabled, causing the Hub to skip this panel.
        /// </summary>
        /// <returns>The Memory panel root element, or null if mem0 is disabled.</returns>
        public VisualElement CreatePanel()
        {
            if (!AgentCoreSettings.instance.mem0Enabled)
                return null;

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
