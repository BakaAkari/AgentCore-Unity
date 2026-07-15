using System;
using AgentCore.Editor.Components.VCS.Config;
using AgentCore.Editor.Components.VCS.Tools;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Components.VCS.UI
{
    /// <summary>
    /// Draws a SceneView top banner when the working copy is behind remote/depot.
    /// </summary>
    [InitializeOnLoad]
    public static class VcsSceneViewUpdateBanner
    {
        private const float BannerHeight = 28f;

        static VcsSceneViewUpdateBanner()
        {
            SceneView.duringSceneGui += OnSceneGui;
            VcsRemoteStatusMonitor.StatusChanged += _ => SceneView.RepaintAll();
        }

        private static void OnSceneGui(SceneView sceneView)
        {
            var status = VcsRemoteStatusMonitor.LastStatus;
            if (status == null || !status.Success || !status.HasRemoteChanges)
                return;

            Handles.BeginGUI();
            var rect = new Rect(0f, 0f, sceneView.position.width, BannerHeight);
            EditorGUI.DrawRect(rect, new Color(0.95f, 0.68f, 0.12f, 0.96f));

            var style = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.black },
                fontSize = 12
            };

            var label = BuildBannerText(status);
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
            if (GUI.Button(rect, label, style))
            {
                HandleBannerClick(status);
            }

            Handles.EndGUI();
        }

        private static void HandleBannerClick(VcsSyncStatus status)
        {
            var confirmed = EditorUtility.DisplayDialog(
                "Version Control Update",
                BuildConfirmationMessage(status),
                "Open Update Window",
                "Cancel");

            if (!confirmed)
                return;

            if (VcsExternalToolLauncher.TryOpenUpdateWindow(out var reason))
            {
                // External GUI is now driving the update. We do not know when it finishes,
                // so refresh remote status shortly after launch to keep the banner in sync.
                ScheduleStatusRecheck();
                return;
            }

            // External GUI unavailable. Offer an in-process fallback so the user is not stuck.
            OfferCliFallback(reason);
        }

        private static void OfferCliFallback(string launchReason)
        {
            var vcsType = VcsDetector.DetectVcs();
            var message =
                $"Could not launch the external VCS GUI:\n{launchReason}\n\n" +
                VcsExternalToolLauncher.BuildUnavailableMessage(vcsType, "Update") +
                "\n\nRun the built-in CLI update instead? Local conflicts will be marked but not resolved.";

            if (!EditorUtility.DisplayDialog(
                    "Version Control Update",
                    message,
                    "Run CLI Update",
                    "Cancel"))
            {
                return;
            }

            RunInlineSyncAsync();
        }

        private static async void RunInlineSyncAsync()
        {
            try
            {
                EditorUtility.DisplayProgressBar(
                    "Version Control Update",
                    "Running VCS update...",
                    0.5f);

                var result = await VcsRemoteStatusMonitor.SyncAsync();

                EditorUtility.ClearProgressBar();

                if (result != null && result.Success)
                {
                    AssetDatabase.Refresh();

                    var conflictSuffix = (result.ConflictedFiles != null && result.ConflictedFiles.Count > 0)
                        ? $"\n\nConflicts detected in {result.ConflictedFiles.Count} file(s). Resolve them before continuing."
                        : string.Empty;

                    EditorUtility.DisplayDialog(
                        "Version Control Update",
                        (string.IsNullOrEmpty(result.Message) ? "Update completed." : result.Message) + conflictSuffix,
                        "OK");
                }
                else
                {
                    var errorMessage = result?.ErrorMessage
                                       ?? result?.Message
                                       ?? "Update failed. Check the Unity Console for details.";
                    EditorUtility.DisplayDialog(
                        "Version Control Update Failed",
                        errorMessage,
                        "OK");
                }
            }
            catch (Exception ex)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogException(ex);
                EditorUtility.DisplayDialog(
                    "Version Control Update Failed",
                    $"Unexpected error: {ex.Message}",
                    "OK");
            }
        }

        private static void ScheduleStatusRecheck()
        {
            // Give the external GUI a moment to spin up; then refresh remote status so the
            // banner disappears once the user actually pulls the changes.
            EditorApplication.delayCall += () => VcsRemoteStatusMonitor.RequestCheck();
        }

        private static string BuildBannerText(VcsSyncStatus status)
        {
            if (status.BehindCount > 0)
                return $"Version Control: remote has {status.BehindCount} commit(s) to pull. Click to update.";

            return $"Version Control: remote has {status.RemoteChangeCount} file update(s). Click to update.";
        }

        private static string BuildConfirmationMessage(VcsSyncStatus status)
        {
            var message = status.Summary;
            if (status.RemoteChangedFiles != null && status.RemoteChangedFiles.Count > 0)
            {
                var previewCount = Mathf.Min(status.RemoteChangedFiles.Count, 12);
                message += "\n\nFiles:\n";
                for (var i = 0; i < previewCount; i++)
                    message += $"- {status.RemoteChangedFiles[i]}\n";

                if (status.RemoteChangedFiles.Count > previewCount)
                    message += $"... and {status.RemoteChangedFiles.Count - previewCount} more file(s).\n";
            }

            message += "\nThis will open the corresponding external VCS update window (e.g. TortoiseSVN / Git GUI / P4V).";
            message += "\nLocal conflicts are not auto-resolved in Unity.";
            return message;
        }
    }
}
