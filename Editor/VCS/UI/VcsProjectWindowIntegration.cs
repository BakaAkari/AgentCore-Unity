using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using AgentCore.Editor.Components.VCS.Tools;

namespace AgentCore.Editor.Components.VCS.UI
{
    /// <summary>
    /// Unity Project 窗口的 VCS 集成 - 为目录和文件添加右键菜单
    /// </summary>
    public static class VcsProjectWindowIntegration
    {
        private const string MenuRoot = "Assets/Version Control/";
        private const int MenuPriority = 2000;

        #region Validation Methods

        // 单独的验证方法 - 每个菜单项一个
        [MenuItem(MenuRoot + "Commit", true, MenuPriority)]
        private static bool ValidateCommit()
        {
            return ValidateVcsOperation("Commit");
        }

        [MenuItem(MenuRoot + "Update", true, MenuPriority + 1)]
        private static bool ValidateUpdate()
        {
            return ValidateVcsOperation("Update");
        }

        [MenuItem(MenuRoot + "Show Log", true, MenuPriority + 2)]
        private static bool ValidateShowLog()
        {
            return ValidateVcsOperation("Show Log");
        }

        [MenuItem(MenuRoot + "Show Diff", true, MenuPriority + 3)]
        private static bool ValidateShowDiff()
        {
            return ValidateVcsOperation("Show Diff");
        }

        [MenuItem(MenuRoot + "Revert Changes", true, MenuPriority + 4)]
        private static bool ValidateRevertChanges()
        {
            return ValidateVcsOperation("Revert Changes");
        }

        [MenuItem(MenuRoot + "Cleanup", true, MenuPriority + 5)]
        private static bool ValidateCleanup()
        {
            return ValidateVcsOperation("Cleanup");
        }

        private static bool ValidateVcsOperation(string operation)
        {
            // 检查是否在 VCS 项目中
            var vcsType = VcsDetector.DetectVcs();
            UnityEngine.Debug.Log($"[VCS Menu] ValidateVcsOperation({operation}) - VcsType: {vcsType}");
            
            if (vcsType == VcsType.None)
            {
                UnityEngine.Debug.Log($"[VCS Menu] {operation}: No VCS detected, menu disabled");
                return false;
            }

            // 检查是否选中了资源
            if (Selection.activeObject == null)
            {
                UnityEngine.Debug.Log($"[VCS Menu] {operation}: No selection, menu disabled");
                return false;
            }

            // 获取选中资源的路径
            var path = AssetDatabase.GetAssetPath(Selection.activeObject);
            UnityEngine.Debug.Log($"[VCS Menu] {operation}: Selected path: {path}");
            
            if (string.IsNullOrEmpty(path))
            {
                UnityEngine.Debug.Log($"[VCS Menu] {operation}: Empty path, menu disabled");
                return false;
            }

            // 目前只支持 SVN
            var isSupported = vcsType == VcsType.Svn;
            UnityEngine.Debug.Log($"[VCS Menu] {operation}: Menu {(isSupported ? "enabled" : "disabled")} (SVN only)");
            return isSupported;
        }

        #endregion

        #region Menu Items

        [MenuItem(MenuRoot + "Commit", false, MenuPriority)]
        private static void CommitSelected()
        {
            var path = GetSelectedAssetPath();
            if (string.IsNullOrEmpty(path))
                return;

            var absolutePath = Path.GetFullPath(path);
            TryStartTortoiseSVN("commit", absolutePath, "Commit");
        }

        [MenuItem(MenuRoot + "Update", false, MenuPriority + 1)]
        private static void UpdateSelected()
        {
            var path = GetSelectedAssetPath();
            if (string.IsNullOrEmpty(path))
                return;

            var absolutePath = Path.GetFullPath(path);
            TryStartTortoiseSVN("update", absolutePath, "Update");
        }

        [MenuItem(MenuRoot + "Show Log", false, MenuPriority + 2)]
        private static void ShowLogSelected()
        {
            var path = GetSelectedAssetPath();
            if (string.IsNullOrEmpty(path))
                return;

            var absolutePath = Path.GetFullPath(path);
            TryStartTortoiseSVN("log", absolutePath, "Log");
        }

        [MenuItem(MenuRoot + "Show Diff", false, MenuPriority + 3)]
        private static void ShowDiffSelected()
        {
            var path = GetSelectedAssetPath();
            if (string.IsNullOrEmpty(path))
                return;

            var absolutePath = Path.GetFullPath(path);
            TryStartTortoiseSVN("diff", absolutePath, "Diff");
        }

        [MenuItem(MenuRoot + "Revert Changes", false, MenuPriority + 4)]
        private static void RevertSelected()
        {
            var path = GetSelectedAssetPath();
            if (string.IsNullOrEmpty(path))
                return;

            var absolutePath = Path.GetFullPath(path);
            TryStartTortoiseSVN("revert", absolutePath, "Revert");
        }

        [MenuItem(MenuRoot + "Cleanup", false, MenuPriority + 5)]
        private static void CleanupSelected()
        {
            var path = GetSelectedAssetPath();
            if (string.IsNullOrEmpty(path))
                return;

            var absolutePath = Path.GetFullPath(path);
            TryStartTortoiseSVN("cleanup", absolutePath, "Cleanup");
        }

        #endregion

        #region Helper Methods

        private static string GetSelectedAssetPath()
        {
            if (Selection.activeObject == null)
                return null;

            return AssetDatabase.GetAssetPath(Selection.activeObject);
        }

        private static bool TryStartTortoiseSVN(string command, string path, string displayName)
        {
            var rootPath = VcsDetector.GetVcsRootPath();
            if (string.IsNullOrEmpty(rootPath))
            {
                UnityEngine.Debug.LogWarning($"[VCS] Cannot find VCS root path for {displayName} operation.");
                return false;
            }

            var arguments = $"/command:{command} /path:\"{path}\"";

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "TortoiseProc.exe",
                    Arguments = arguments,
                    UseShellExecute = true,
                    CreateNoWindow = false,
                    WorkingDirectory = rootPath
                };

                Process.Start(startInfo);
                UnityEngine.Debug.Log($"[VCS] Opened TortoiseSVN {displayName} window for: {path}");
                return true;
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogError($"[VCS] Failed to start TortoiseSVN {displayName}: {ex.Message}\n" +
                    $"Make sure TortoiseSVN is installed and TortoiseProc.exe is in your PATH.");
                return false;
            }
        }

        #endregion
    }
}
