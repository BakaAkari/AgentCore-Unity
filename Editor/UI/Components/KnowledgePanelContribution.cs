using AgentCore.Editor.Config;
using AgentCore.Editor.Extensions;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Components
{
    /// <summary>
    /// Built-in AgentCore Hub contribution for the Knowledge panel.
    /// Only visible when LightRAG service is enabled in settings.
    /// </summary>
    public sealed class KnowledgePanelContribution : IAgentCorePanelContribution
    {
        /// <summary>
        /// Gets the stable panel identifier.
        /// </summary>
        public string Id => "knowledge";

        /// <summary>
        /// Gets the Hub navigation label.
        /// </summary>
        public string Label => "Know";

        /// <summary>
        /// Gets the Hub navigation tooltip.
        /// </summary>
        public string Tooltip => "Knowledge";

        /// <summary>
        /// Gets the Hub sorting order.
        /// </summary>
        public int Order => 100;

        /// <summary>
        /// Creates the Knowledge panel visual tree.
        /// Returns null when LightRAG is disabled, causing the Hub to skip this panel.
        /// </summary>
        /// <returns>The Knowledge panel root element, or null if LightRAG is disabled.</returns>
        public VisualElement CreatePanel()
        {
            if (!AgentCoreSettings.instance.lightragEnabled)
                return null;

            return new KnowledgeBasePanel();
        }

        /// <summary>
        /// Refreshes the Knowledge panel when activated.
        /// </summary>
        /// <param name="panel">The panel instance.</param>
        public void OnActivated(VisualElement panel)
        {
            if (panel is KnowledgeBasePanel knowledgePanel)
            {
                knowledgePanel.OnActivated();
            }
        }

        /// <summary>
        /// Deactivates the Knowledge panel.
        /// </summary>
        /// <param name="panel">The panel instance.</param>
        public void OnDeactivated(VisualElement panel)
        {
            if (panel is KnowledgeBasePanel knowledgePanel)
            {
                knowledgePanel.OnDeactivated();
            }
        }
    }
}
