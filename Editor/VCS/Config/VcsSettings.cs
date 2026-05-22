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
    }
}
