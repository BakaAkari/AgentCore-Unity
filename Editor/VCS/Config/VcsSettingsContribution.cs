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
        public string Title => "版本控制";

        /// <summary>
        /// Gets the settings section description.
        /// </summary>
        public string Description => "Git / SVN / Perforce 组件的高级配置。";

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
            var autoRefreshOnOpen = EditorGUILayout.ToggleLeft("打开 VCS 面板时自动刷新仓库状态", VcsSettings.AutoRefreshOnOpen);
            var maxCommitEntries = EditorGUILayout.IntSlider("默认显示的提交条数", VcsSettings.MaxCommitEntries, 1, 100);

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("自动刷新", EditorStyles.boldLabel);
            var autoRefreshCommitList = EditorGUILayout.ToggleLeft("后台静默刷新提交记录", VcsSettings.AutoRefreshCommitListEnabled);
            int commitRefreshInterval;
            using (new EditorGUI.DisabledScope(!autoRefreshCommitList))
            {
                commitRefreshInterval = EditorGUILayout.IntSlider("提交记录刷新间隔 (秒)", VcsSettings.CommitListRefreshIntervalSeconds, 10, 300);
            }

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("远程状态检测", EditorStyles.boldLabel);
            var checkRemoteOnRefresh = EditorGUILayout.ToggleLeft("刷新 VCS 面板时检查远程状态", VcsSettings.CheckRemoteStatusOnRefresh);
            var sceneViewBannerEnabled = EditorGUILayout.ToggleLeft("有远程更新时在 SceneView 顶部显示提示条", VcsSettings.SceneViewUpdateBannerEnabled);
            var periodicCheckEnabled = EditorGUILayout.ToggleLeft("在编辑器中定期检查远程状态", VcsSettings.PeriodicRemoteStatusCheckEnabled);
            int intervalMinutes;
            using (new EditorGUI.DisabledScope(!periodicCheckEnabled))
            {
                intervalMinutes = EditorGUILayout.IntSlider("远程检查间隔 (分钟)", VcsSettings.RemoteStatusCheckIntervalMinutes, 1, 120);
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
