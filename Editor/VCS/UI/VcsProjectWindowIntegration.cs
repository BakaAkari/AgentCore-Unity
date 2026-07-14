using System.IO;
using UnityEditor;
using UnityEngine;
using AgentCore.Editor.Components.VCS.Tools;

namespace AgentCore.Editor.Components.VCS.UI
{
    /// <summary>
    /// Unity Project window VCS integration — right-click context menus for common operations.
    /// Supports Git, SVN, and Perforce via VcsExternalToolLauncher (no hardcoded tool paths).
    /// </summary>
    public static class VcsProjectWindowIntegration
    {
        private const string MenuRoot = "Assets/Version Control/";
        private const int MenuPriority = 2000;

        #region Validation

        [MenuItem(MenuRoot + "Commit", true, MenuPriority)]
        private static bool ValidateCommit() => IsVcsActive();

        [MenuItem(MenuRoot + "Update", true, MenuPriority + 1)]
        private static bool ValidateUpdate() => IsVcsActive();

        [MenuItem(MenuRoot + "Show Log", true, MenuPriority + 2)]
        private static bool ValidateShowLog() => IsVcsActive();

        [MenuItem(MenuRoot + "Show Diff", true, MenuPriority + 3)]
        private static bool ValidateShowDiff() => IsVcsActive();

        [MenuItem(MenuRoot + "Revert Changes", true, MenuPriority + 4)]
        private static bool ValidateRevertChanges() => IsVcsActive();

        [MenuItem(MenuRoot + "Cleanup", true, MenuPriority + 5)]
        private static bool ValidateCleanup() => IsVcsActive();

        private static bool IsVcsActive()
        {
            if (Selection.activeObject == null)
                return false;

            var path = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (string.IsNullOrEmpty(path))
                return false;

            return VcsDetector.DetectVcs() != VcsType.None;
        }

        #endregion

        #region Menu Items

        [MenuItem(MenuRoot + "Commit", false, MenuPriority)]
        private static void CommitSelected() => LaunchExternal("commit", "Commit");

        [MenuItem(MenuRoot + "Update", false, MenuPriority + 1)]
        private static void UpdateSelected() => LaunchExternal("update", "Update");

        [MenuItem(MenuRoot + "Show Log", false, MenuPriority + 2)]
        private static void ShowLogSelected() => LaunchExternal("log", "Log");

        [MenuItem(MenuRoot + "Show Diff", false, MenuPriority + 3)]
        private static void ShowDiffSelected() => LaunchExternal("diff", "Diff");

        [MenuItem(MenuRoot + "Revert Changes", false, MenuPriority + 4)]
        private static void RevertSelected() => LaunchExternal("revert", "Revert");

        [MenuItem(MenuRoot + "Cleanup", false, MenuPriority + 5)]
        private static void CleanupSelected() => LaunchExternal("cleanup", "Cleanup");

        #endregion

        #region Helpers

        /// <summary>
        /// Launches the appropriate external VCS GUI tool for the detected VCS type.
        /// Falls back to an inline CLI command when no GUI tool is available.
        /// </summary>
        private static void LaunchExternal(string operation, string displayName)
        {
            var vcsType = VcsDetector.DetectVcs();
            if (vcsType == VcsType.None)
            {
                Debug.LogWarning($"[VCS] No version control system detected for {displayName}.");
                return;
            }

            var rootPath = VcsDetector.GetVcsRootPath();
            var selectedPath = GetSelectedAssetAbsolutePath();

            if (!TryLaunchExternalTool(vcsType, operation, selectedPath, rootPath, out var reason))
            {
                Debug.LogWarning($"[VCS] {displayName}: {reason}");
            }
        }

        private static string GetSelectedAssetAbsolutePath()
        {
            var assetPath = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (string.IsNullOrEmpty(assetPath))
                return VcsDetector.GetVcsRootPath();

            return Path.GetFullPath(assetPath);
        }

        /// <summary>
        /// Dispatches to the appropriate external GUI tool based on VCS type.
        /// Uses VcsExternalToolLauncher for shell-based PATH resolution.
        /// </summary>
        private static bool TryLaunchExternalTool(VcsType vcsType, string operation, string targetPath, string rootPath, out string reason)
        {
            var (fileName, arguments) = vcsType switch
            {
                VcsType.Svn => ("TortoiseProc.exe", $"/command:{operation} /path:\"{targetPath}\""),
                VcsType.Git => operation switch
                {
                    "commit" => ("git", $"gui"),
                    "update" => ("git", $"pull"),
                    "log" => ("git", $"log --oneline -20"),
                    "diff" => ("git", $"difftool"),
                    "revert" => ("git", $"checkout -- \"{targetPath}\""),
                    "cleanup" => ("git", "gc --auto"),
                    _ => (null, null)
                },
                VcsType.Perforce => operation switch
                {
                    "commit" => ("p4v", $"-cmd \"submit\" \"{rootPath}\""),
                    "update" => ("p4v", $"-cmd \"sync //...\" \"{rootPath}\""),
                    "log" => ("p4v", $"-cmd \"history\" \"{targetPath}\""),
                    "diff" => ("p4v", $"-cmd \"diff\" \"{targetPath}\""),
                    "revert" => ("p4v", $"-cmd \"revert\" \"{targetPath}\""),
                    "cleanup" => ("p4v", null),
                    _ => (null, null)
                },
                _ => (null, null)
            };

            if (fileName == null)
            {
                reason = $"Operation '{operation}' is not supported for {vcsType}.";
                return false;
            }

            return VcsExternalToolLauncher.TryStartProcess(
                fileName,
                arguments ?? string.Empty,
                rootPath,
                $"{vcsType} {operation}",
                out reason);
        }

        #endregion
    }
}
