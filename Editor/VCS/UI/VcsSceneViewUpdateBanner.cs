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
            if (!VcsSettings.SceneViewUpdateBannerEnabled)
                return;

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
                if (EditorUtility.DisplayDialog(
                        "Version Control Update",
                        BuildConfirmationMessage(status),
                        "Update Now",
                        "Cancel"))
                {
                    _ = VcsRemoteStatusMonitor.SyncAsync();
                }
            }

            Handles.EndGUI();
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

            message += "\nThis will run the VCS update/sync command. Local conflicts are not auto-resolved.";
            return message;
        }
    }
}
