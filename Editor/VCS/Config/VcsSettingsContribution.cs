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
        public string Description => "Git / SVN / Perforce settings.";

        /// <summary>
        /// Gets the sorting order for this contribution.
        /// </summary>
        public int Order => 300;

        /// <summary>
        /// Belongs to the VCS optional component so the settings render inline inside the component card.
        /// </summary>
        public string OwnerComponentId => "vcs";

        /// <summary>
        /// Draws Version Control settings controls.
        /// </summary>
        public void DrawGUI()
        {
            EditorGUI.BeginChangeCheck();
            var autoRefreshOnOpen = EditorGUILayout.ToggleLeft("Auto-refresh on panel open", VcsSettings.AutoRefreshOnOpen);
            var maxCommitEntries = EditorGUILayout.IntSlider("Commit entries", VcsSettings.MaxCommitEntries, 1, 100);

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Auto Refresh", EditorStyles.boldLabel);
            var autoRefreshCommitList = EditorGUILayout.ToggleLeft("Silent background refresh", VcsSettings.AutoRefreshCommitListEnabled);
            int commitRefreshInterval;
            using (new EditorGUI.DisabledScope(!autoRefreshCommitList))
            {
                commitRefreshInterval = EditorGUILayout.IntSlider("Refresh interval (s)", VcsSettings.CommitListRefreshIntervalSeconds, 10, 300);
            }

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Remote Detection", EditorStyles.boldLabel);
            var checkRemoteOnRefresh = EditorGUILayout.ToggleLeft("Check on refresh", VcsSettings.CheckRemoteStatusOnRefresh);
            var sceneViewBannerEnabled = EditorGUILayout.ToggleLeft("SceneView banner on remote updates", VcsSettings.SceneViewUpdateBannerEnabled);
            var periodicCheckEnabled = EditorGUILayout.ToggleLeft("Periodic remote check", VcsSettings.PeriodicRemoteStatusCheckEnabled);
            int intervalMinutes;
            using (new EditorGUI.DisabledScope(!periodicCheckEnabled))
            {
                intervalMinutes = EditorGUILayout.IntSlider("Check interval (min)", VcsSettings.RemoteStatusCheckIntervalMinutes, 1, 120);
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
