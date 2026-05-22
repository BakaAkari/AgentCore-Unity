using AgentCore.Editor.Extensions;
using UnityEditor;
using UnityEngine.UIElements;

namespace AgentCore.Editor.Components.VCS.UI
{
    /// <summary>
    /// Built-in AgentCore Hub contribution for the Version Control panel.
    /// </summary>
    public sealed class VersionControlPanelContribution : IAgentCorePanelContribution
    {
        private const string StyleSheetPath = "Packages/com.agentcore.unity/Editor/VCS/UI/VersionControlPanel.uss";

        /// <summary>
        /// Gets the stable panel identifier.
        /// </summary>
        public string Id => "version-control";

        /// <summary>
        /// Gets the Hub navigation label.
        /// </summary>
        public string Label => "VCS";

        /// <summary>
        /// Gets the Hub navigation tooltip.
        /// </summary>
        public string Tooltip => "Version Control";

        /// <summary>
        /// Gets the Hub sorting order.
        /// </summary>
        public int Order => 300;

        /// <summary>
        /// Creates the Version Control panel visual tree.
        /// </summary>
        /// <returns>The Version Control panel root element.</returns>
        public VisualElement CreatePanel()
        {
            var panel = new VersionControlPanel();
            var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (styleSheet != null)
            {
                panel.styleSheets.Add(styleSheet);
            }

            return panel;
        }

        /// <summary>
        /// Refreshes the Version Control panel when activated.
        /// </summary>
        /// <param name="panel">The panel instance.</param>
        public void OnActivated(VisualElement panel)
        {
            if (panel is VersionControlPanel versionControlPanel)
            {
                versionControlPanel.OnActivated();
            }
        }

        /// <summary>
        /// Deactivates the Version Control panel.
        /// </summary>
        /// <param name="panel">The panel instance.</param>
        public void OnDeactivated(VisualElement panel)
        {
            if (panel is VersionControlPanel versionControlPanel)
            {
                versionControlPanel.OnDeactivated();
            }
        }
    }
}
