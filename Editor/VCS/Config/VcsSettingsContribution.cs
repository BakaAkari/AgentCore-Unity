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

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Auto Refresh", EditorStyles.boldLabel);
            var autoRefreshCommitList = EditorGUILayout.ToggleLeft("Silently refresh commit list in the background", VcsSettings.AutoRefreshCommitListEnabled);
            int commitRefreshInterval;
            using (new EditorGUI.DisabledScope(!autoRefreshCommitList))
            {
                commitRefreshInterval = EditorGUILayout.IntSlider("Commit list refresh interval (seconds)", VcsSettings.CommitListRefreshIntervalSeconds, 10, 300);
            }

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Remote Update Detection", EditorStyles.boldLabel);
            var checkRemoteOnRefresh = EditorGUILayout.ToggleLeft("Check remote status when refreshing VCS panel", VcsSettings.CheckRemoteStatusOnRefresh);
            var sceneViewBannerEnabled = EditorGUILayout.ToggleLeft("Show SceneView top banner when remote updates are available", VcsSettings.SceneViewUpdateBannerEnabled);
            var periodicCheckEnabled = EditorGUILayout.ToggleLeft("Check remote status periodically in the editor", VcsSettings.PeriodicRemoteStatusCheckEnabled);
            int intervalMinutes;
            using (new EditorGUI.DisabledScope(!periodicCheckEnabled))
            {
                intervalMinutes = EditorGUILayout.IntSlider("Remote check interval (minutes)", VcsSettings.RemoteStatusCheckIntervalMinutes, 1, 120);
            }

            if (EditorGUI.EndChangeCheck())
            {
                VcsSettings.AutoRefreshOnOpen = autoRefreshOnOpen;
                VcsSettings.MaxCommitEntries = maxCommitEntries;
                VcsSettings.AutoRefreshCommitListEnabled = autoRefreshCommitList;
                VcsSettings.CommitListRefreshIntervalSeconds = commitRefreshInterval;
                VcsSettings.CheckRemoteStatusOnRefresh = checkRemoteOnRefresh;
                VcsSettings.SceneViewUpdateBannerEnabled = sceneViewBannerEnabled;
                VcsSettings.PeriodicRemoteStatusCheckEnabled = periodicCheckEnabled;
                VcsSettings.RemoteStatusCheckIntervalMinutes = intervalMinutes;
            }
        }
    }
}
