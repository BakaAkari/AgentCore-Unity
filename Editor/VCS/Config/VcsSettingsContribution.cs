using AgentCore.Editor.Components.VCS.Tools;
using AgentCore.Editor.Extensions;
using UnityEditor;
using UnityEngine;

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

            DrawDetectionGUI();
        }

        /// <summary>
        /// 显示会话缓存的检测结果（只读缓存，不在重绘时探测），并提供显式重新检测入口。
        /// </summary>
        private static void DrawDetectionGUI()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Detection", EditorStyles.boldLabel);

            var vcsType = VcsDetector.DetectVcs();
            EditorGUILayout.LabelField("Detected VCS", vcsType == VcsType.None ? "None" : vcsType.ToString());
            if (vcsType != VcsType.None)
            {
                EditorGUILayout.LabelField("Root", VcsDetector.GetVcsRootPath() ?? string.Empty);

                var availability = VcsDetector.GetCommandAvailability(vcsType);
                var availabilityText = availability switch
                {
                    VcsCommandAvailability.Available => "Available",
                    VcsCommandAvailability.Unavailable => "Not available",
                    _ => "Not checked yet"
                };
                EditorGUILayout.LabelField("Command-line tool", availabilityText);

                var probeError = VcsDetector.GetCommandProbeError(vcsType);
                if (availability == VcsCommandAvailability.Unavailable && !string.IsNullOrEmpty(probeError))
                    EditorGUILayout.HelpBox(probeError, MessageType.Warning);
            }

            if (GUILayout.Button(new GUIContent("Re-detect", "Clear the cached detection result and check the VCS type and command-line tool again (runs in the background).")))
            {
                _ = VcsDetector.Refresh();
            }
        }
    }
}
