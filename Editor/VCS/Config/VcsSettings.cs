using UnityEditor;

namespace AgentCore.Editor.Components.VCS.Config
{
    /// <summary>
    /// Stores editor preferences for the optional Version Control component.
    /// </summary>
    public static class VcsSettings
    {
        private const string AutoRefreshKey = "AgentCore.VCS.AutoRefresh";
        private const string MaxCommitEntriesKey = "AgentCore.VCS.MaxCommitEntries";
        private const string CheckRemoteStatusOnRefreshKey = "AgentCore.VCS.CheckRemoteStatusOnRefresh";
        private const string SceneViewUpdateBannerEnabledKey = "AgentCore.VCS.SceneViewUpdateBannerEnabled";
        private const string PeriodicRemoteStatusCheckEnabledKey = "AgentCore.VCS.PeriodicRemoteStatusCheckEnabled";
        private const string RemoteStatusCheckIntervalMinutesKey = "AgentCore.VCS.RemoteStatusCheckIntervalMinutes";

        /// <summary>
        /// Gets or sets whether the Version Control panel should refresh when opened.
        /// </summary>
        public static bool AutoRefreshOnOpen
        {
            get => EditorPrefs.GetBool(AutoRefreshKey, true);
            set => EditorPrefs.SetBool(AutoRefreshKey, value);
        }

        /// <summary>
        /// Gets or sets the maximum number of commits shown by default in VCS UI queries.
        /// </summary>
        public static int MaxCommitEntries
        {
            get => EditorPrefs.GetInt(MaxCommitEntriesKey, 20);
            set => EditorPrefs.SetInt(MaxCommitEntriesKey, value < 1 ? 1 : value);
        }

        /// <summary>
        /// Gets or sets whether refresh operations should also check remote update status.
        /// </summary>
        public static bool CheckRemoteStatusOnRefresh
        {
            get => EditorPrefs.GetBool(CheckRemoteStatusOnRefreshKey, true);
            set => EditorPrefs.SetBool(CheckRemoteStatusOnRefreshKey, value);
        }

        /// <summary>
        /// Gets or sets whether SceneView should show a top banner when remote updates are available.
        /// </summary>
        public static bool SceneViewUpdateBannerEnabled
        {
            get => EditorPrefs.GetBool(SceneViewUpdateBannerEnabledKey, true);
            set => EditorPrefs.SetBool(SceneViewUpdateBannerEnabledKey, value);
        }

        /// <summary>
        /// Gets or sets whether remote status should be checked periodically in the editor.
        /// </summary>
        public static bool PeriodicRemoteStatusCheckEnabled
        {
            get => EditorPrefs.GetBool(PeriodicRemoteStatusCheckEnabledKey, true);
            set => EditorPrefs.SetBool(PeriodicRemoteStatusCheckEnabledKey, value);
        }

        /// <summary>
        /// Gets or sets the interval in minutes for periodic remote status checks.
        /// </summary>
        public static int RemoteStatusCheckIntervalMinutes
        {
            get => EditorPrefs.GetInt(RemoteStatusCheckIntervalMinutesKey, 15);
            set => EditorPrefs.SetInt(RemoteStatusCheckIntervalMinutesKey, value < 1 ? 1 : value);
        }
    }
}
