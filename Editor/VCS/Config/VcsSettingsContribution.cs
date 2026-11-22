using AgentCore.Editor.Components.VCS.Tools;
using AgentCore.Editor.Extensions;
using AgentCore.Editor.L10n;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Components.VCS.Config
{
    /// <summary>
    /// Provides Preferences UI for the optional Version Control component.
    /// Minimal: only user-facing preferences that benefit from runtime adjustment.
    /// </summary>
    public sealed class VcsSettingsContribution : IAgentCoreSettingsContribution
    {
        public string Id => "vcs-settings";
        public string Title => Loc.Tr("settings.vcs.title", "Version Control");
        public string Description => Loc.Tr("settings.vcs.description", "Git / SVN / Perforce settings.");
        public int Order => 300;
        public string OwnerComponentId => "vcs";

        public void DrawGUI()
        {
            EditorGUI.BeginChangeCheck();
            var autoRefreshOnOpen = EditorGUILayout.ToggleLeft(Loc.Tr("settings.vcs.autoRefresh", "Auto-refresh on panel open"), VcsSettings.AutoRefreshOnOpen);
            var maxCommitEntries = EditorGUILayout.IntSlider(Loc.Tr("settings.vcs.commitEntries", "Commit entries"), VcsSettings.MaxCommitEntries, 1, 100);

            if (EditorGUI.EndChangeCheck())
            {
                VcsSettings.AutoRefreshOnOpen = autoRefreshOnOpen;
                VcsSettings.MaxCommitEntries = maxCommitEntries;
            }

            DrawDetectionGUI();
        }

        /// <summary>
        /// 显示会话缓存的检测结果（只读缓存，不在重绘时探测），并提供显式重新检测入口。
        /// </summary>
        private static void DrawDetectionGUI()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Loc.Tr("settings.vcs.detection.title", "Detection"), EditorStyles.boldLabel);

            var vcsType = VcsDetector.DetectVcs();
            EditorGUILayout.LabelField(
                Loc.Tr("settings.vcs.detection.detected", "Detected VCS"),
                vcsType == VcsType.None ? Loc.Tr("settings.vcs.detection.none", "None") : vcsType.ToString());
            if (vcsType != VcsType.None)
            {
                EditorGUILayout.LabelField(Loc.Tr("settings.vcs.detection.root", "Root"), VcsDetector.GetVcsRootPath() ?? string.Empty);

                var availability = VcsDetector.GetCommandAvailability(vcsType);
                var availabilityText = availability switch
                {
                    VcsCommandAvailability.Available => Loc.Tr("settings.vcs.detection.available", "Available"),
                    VcsCommandAvailability.Unavailable => Loc.Tr("settings.vcs.detection.unavailable", "Not available"),
                    _ => Loc.Tr("settings.vcs.detection.notChecked", "Not checked yet")
                };
                EditorGUILayout.LabelField(Loc.Tr("settings.vcs.detection.cli", "Command-line tool"), availabilityText);

                var probeError = VcsDetector.GetCommandProbeError(vcsType);
                if (availability == VcsCommandAvailability.Unavailable && !string.IsNullOrEmpty(probeError))
                    EditorGUILayout.HelpBox(probeError, MessageType.Warning);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Loc.Tr("settings.vcs.remote.title", "Remote status"), EditorStyles.boldLabel);
            EditorGUILayout.LabelField(Loc.Tr("settings.vcs.remote.lastCheck", "Last check"), VcsRemoteStatusMonitor.FormatLastChecked());
            EditorGUILayout.LabelField(
                Loc.Tr("settings.vcs.remote.auto", "Automatic check"),
                Loc.Tr("settings.vcs.remote.autoValue", "Every {0} min (background)", VcsSettings.RemoteStatusCheckIntervalMinutes));

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(
                Loc.Tr("settings.vcs.remote.checkNow", "Check Remote Now"),
                Loc.Tr("settings.vcs.remote.checkNowTooltip", "Fetch remote status in the background (git fetch / svn status -u / p4 sync -n)."))))
            {
                VcsRemoteStatusMonitor.RequestCheck();
            }

            if (GUILayout.Button(new GUIContent(
                Loc.Tr("settings.vcs.detection.redetect", "Re-detect"),
                Loc.Tr("settings.vcs.detection.redetectTooltip", "Clear the cached detection result and check the VCS type and command-line tool again (runs in the background)."))))
            {
                _ = VcsDetector.Refresh();
            }
            EditorGUILayout.EndHorizontal();
        }
    }
}
