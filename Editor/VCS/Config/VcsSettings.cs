using UnityEditor;

namespace AgentCore.Editor.Components.VCS.Config
{
    /// <summary>
    /// Stores editor preferences and internal constants for the optional Version Control component.
    /// Only user-facing toggles remain as EditorPrefs; operational defaults are internal constants.
    /// </summary>
    public static class VcsSettings
    {
        private const string AutoRefreshKey = "AgentCore.VCS.AutoRefresh";
        private const string MaxCommitEntriesKey = "AgentCore.VCS.MaxCommitEntries";

        // ── User-facing preferences (EditorPrefs) ──

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

        // ── Internal constants (no user-facing settings) ──

        /// <summary>Whether refresh operations should also check remote update status.</summary>
        public const bool CheckRemoteStatusOnRefresh = true;

        /// <summary>The interval in minutes for periodic remote status checks.</summary>
        public const int RemoteStatusCheckIntervalMinutes = 15;

        /// <summary>The interval in seconds for automatic commit list refresh.</summary>
        public const int CommitListRefreshIntervalSeconds = 30;
    }
}
