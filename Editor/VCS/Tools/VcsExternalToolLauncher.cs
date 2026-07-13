using System;
using UnityEngine;

namespace AgentCore.Editor.Components.VCS.Tools
{
    /// <summary>
    /// Attempts to launch external VCS GUI tools (TortoiseSVN, Git GUI, P4V) for common operations.
    /// Callers get a boolean success flag plus a human-readable reason for failures so they can
    /// present it to the user or fall back to an in-process implementation.
    /// </summary>
    /// <remarks>
    /// This launcher intentionally uses <c>UseShellExecute = true</c> so PATH resolution and
    /// Windows registry-based launch shims (e.g. TortoiseProc installed under Program Files)
    /// work without hard-coding install locations. A missing executable surfaces as a
    /// <see cref="System.ComponentModel.Win32Exception"/> which is captured in the reason.
    /// </remarks>
    public static class VcsExternalToolLauncher
    {
        /// <summary>
        /// Attempts to open the external VCS GUI's update/sync window for the current repository.
        /// </summary>
        /// <param name="reason">
        /// On success, a short description of the tool that was launched.
        /// On failure, the error message (e.g. "TortoiseProc.exe not found" or "no VCS detected").
        /// </param>
        /// <returns><c>true</c> if a process was successfully started; otherwise <c>false</c>.</returns>
        public static bool TryOpenUpdateWindow(out string reason)
        {
            var vcsType = VcsDetector.DetectVcs();
            if (vcsType == VcsType.None)
            {
                reason = "No version control system detected for this project.";
                return false;
            }

            var rootPath = VcsDetector.GetVcsRootPath();
            if (string.IsNullOrEmpty(rootPath))
            {
                reason = "VCS root path could not be resolved.";
                return false;
            }

            switch (vcsType)
            {
                case VcsType.Svn:
                    return TryStartProcess(
                        "TortoiseProc.exe",
                        $"/command:update /path:\"{rootPath}\"",
                        rootPath,
                        "TortoiseSVN update window",
                        out reason);

                case VcsType.Git:
                    // git-gui is bundled with Git for Windows and available on macOS/Linux
                    // when the git package includes Tcl/Tk. Falls through to reason on failure.
                    return TryStartProcess(
                        "git",
                        "gui",
                        rootPath,
                        "Git GUI window",
                        out reason);

                case VcsType.Perforce:
                    return TryStartProcess(
                        "p4v",
                        $"-cmd \"sync //...\" \"{rootPath}\"",
                        rootPath,
                        "P4V sync window",
                        out reason);

                default:
                    reason = $"External update window is not supported for {vcsType}.";
                    return false;
            }
        }

        /// <summary>
        /// Launches an external process with shell resolution enabled.
        /// </summary>
        /// <param name="fileName">Executable name or full path.</param>
        /// <param name="arguments">Command line arguments (already quoted as needed).</param>
        /// <param name="workingDirectory">Working directory for the child process.</param>
        /// <param name="displayName">Human-readable label used in log/user messages.</param>
        /// <param name="reason">On failure, contains the underlying error message; on success, contains the display name.</param>
        public static bool TryStartProcess(
            string fileName,
            string arguments,
            string workingDirectory,
            string displayName,
            out string reason)
        {
            if (string.IsNullOrWhiteSpace(workingDirectory))
            {
                reason = "Working directory is empty.";
                return false;
            }

            try
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = true,
                    WorkingDirectory = workingDirectory
                };

                var process = System.Diagnostics.Process.Start(startInfo);
                if (process == null)
                {
                    reason = $"Failed to start {displayName} (no process handle returned).";
                    return false;
                }

                Debug.Log($"[Version Control] Launched {displayName}: {fileName} {arguments}");
                reason = displayName;
                return true;
            }
            catch (Exception ex)
            {
                reason = $"Failed to launch {displayName}: {ex.Message}";
                Debug.LogWarning($"[Version Control] {reason} Command: {fileName} {arguments}");
                return false;
            }
        }

        /// <summary>
        /// Builds a standard "external tool unavailable" message for the given VCS type and operation.
        /// </summary>
        public static string BuildUnavailableMessage(VcsType vcsType, string operation)
        {
            var toolHint = vcsType switch
            {
                VcsType.Svn => "TortoiseSVN (TortoiseProc.exe on PATH or installed to its default location)",
                VcsType.Git => "Git GUI (comes with Git for Windows; requires Tcl/Tk on macOS/Linux)",
                VcsType.Perforce => "Perforce P4V",
                _ => "the corresponding desktop VCS client"
            };

            return $"{operation} requires {toolHint}. Please install or configure it, then retry.";
        }
    }
}
