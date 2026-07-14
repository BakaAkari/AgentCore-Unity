using AgentCore.Editor.Extensions;
using UnityEditor;

namespace AgentCore.Editor.Components.VCS.Config
{
    /// <summary>
    /// Provides Project Settings UI for the optional Version Control component.
    /// Minimal: only user-facing preferences that benefit from runtime adjustment.
    /// </summary>
    public sealed class VcsSettingsContribution : IAgentCoreSettingsContribution
    {
        public string Id => "vcs-settings";
        public string Title => "Version Control";
        public string Description => "Git / SVN / Perforce settings.";
        public int Order => 300;
        public string OwnerComponentId => "vcs";

        public void DrawGUI()
        {
            EditorGUI.BeginChangeCheck();
            var autoRefreshOnOpen = EditorGUILayout.ToggleLeft("Auto-refresh on panel open", VcsSettings.AutoRefreshOnOpen);
            var maxCommitEntries = EditorGUILayout.IntSlider("Commit entries", VcsSettings.MaxCommitEntries, 1, 100);

            if (EditorGUI.EndChangeCheck())
            {
                VcsSettings.AutoRefreshOnOpen = autoRefreshOnOpen;
                VcsSettings.MaxCommitEntries = maxCommitEntries;
            }
        }
    }
}
