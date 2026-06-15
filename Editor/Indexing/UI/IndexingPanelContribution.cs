using AgentCore.Editor.Extensions;
using UnityEditor;
using UnityEngine.UIElements;

namespace AgentCore.Editor.Components.Indexing.UI
{
    /// <summary>
    /// Built-in AgentCore Hub contribution for the Code Indexing panel.
    /// Discovered automatically by <see cref="AgentCoreExtensionRegistry"/> when the
    /// <c>AGENTCORE_INDEXING</c> define is active.
    /// </summary>
    public sealed class IndexingPanelContribution : IAgentCorePanelContribution
    {
        private const string StyleSheetPath =
            "Packages/com.agentcore.unity/Editor/Indexing/UI/IndexingPanel.uss";

        /// <inheritdoc />
        public string Id => "code-indexing";

        /// <inheritdoc />
        public string Label => "Index";

        /// <inheritdoc />
        public string Tooltip => "Code Indexing";

        /// <inheritdoc />
        public int Order => 350;

        /// <inheritdoc />
        public VisualElement CreatePanel()
        {
            var panel = new IndexingPanel();
            var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (styleSheet != null)
                panel.styleSheets.Add(styleSheet);

            return panel;
        }

        /// <inheritdoc />
        public void OnActivated(VisualElement panel)
        {
            if (panel is IndexingPanel indexingPanel)
                indexingPanel.OnActivated();
        }

        /// <inheritdoc />
        public void OnDeactivated(VisualElement panel)
        {
            if (panel is IndexingPanel indexingPanel)
                indexingPanel.OnDeactivated();
        }
    }
}
