using AgentCore.Editor.Extensions;
using UnityEditor;

namespace AgentCore.Editor.Components.VCS.Config
{
    /// <summary>
    /// Provides Project Settings UI for the optional Version Control component.
    /// </summary>
    public sealed class VcsSettingsContribution : IAgentCoreSettingsContribution
    {
        /// <summary>
        /// Gets the stable settings contribution identifier.
        /// </summary>
        public string Id => "vcs-settings";

        /// <summary>
        /// Gets the settings section title.
        /// </summary>
        public string Title => "Version Control";

        /// <summary>
        /// Gets the settings section description.
        /// </summary>
        public string Description => "Configuration for the optional Git / SVN / Perforce component.";

        /// <summary>
        /// Gets the sorting order for this contribution.
        /// </summary>
        public int Order => 300;

        /// <summary>
        /// Draws Version Control settings controls.
        /// </summary>
        public void DrawGUI()
        {
            EditorGUI.BeginChangeCheck();
            var autoRefreshOnOpen = EditorGUILayout.ToggleLeft("Refresh repository state when opening VCS panel", VcsSettings.AutoRefreshOnOpen);
            var maxCommitEntries = EditorGUILayout.IntSlider("Default commit entries", VcsSettings.MaxCommitEntries, 1, 100);

            if (EditorGUI.EndChangeCheck())
            {
                VcsSettings.AutoRefreshOnOpen = autoRefreshOnOpen;
                VcsSettings.MaxCommitEntries = maxCommitEntries;
            }
        }
    }
}
