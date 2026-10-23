using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using AgentCore.Editor.Utils;

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
                    if (!TryBuildTortoiseArgs("update", rootPath, out var tortoiseArgs, out reason))
                        return false;
                    return TryStartProcess(
                        "TortoiseProc.exe",
                        tortoiseArgs,
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
                        VcsArgs.Join(new[] { "-cmd", "sync //...", rootPath }),
                        rootPath,
                        "P4V sync window",
                        out reason);

                default:
                    reason = $"External update window is not supported for {vcsType}.";
                    return false;
            }
        }

        // ── TortoiseProc 参数构造 ──
        // TortoiseProc 用自己的解析器处理 /key:"value"：值一直取到下一个双引号为止，不支持 \" 转义。
        // 因此：路径里出现 " 时直接报错（Windows 路径本就不允许 "）；提交信息走 /logmsgfile；多路径走 /pathfile。

        /// <summary>
        /// 构造 <c>/command:{command} /path:"{path}"</c>。路径含双引号时返回 false 并给出原因。
        /// </summary>
        public static bool TryBuildTortoiseArgs(string command, string path, out string arguments, out string error)
        {
            arguments = null;
            if (!TryQuoteTortoiseValue(path, out var quotedPath, out error))
                return false;

            arguments = $"/command:{command} /path:{quotedPath}";
            return true;
        }

        /// <summary>
        /// 构造对多个路径执行的 TortoiseProc 参数：只有一个路径时用 /path，多个路径时写入 /pathfile 临时文件
        /// （UTF-16LE、换行分隔，TortoiseProc 读取后由 /deletepathfile 自动删除），不受命令行长度限制，也不依赖 '*' 分隔。
        /// </summary>
        public static bool TryBuildTortoiseArgs(string command, IReadOnlyList<string> paths, out string arguments, out string error)
        {
            arguments = null;
            if (paths == null || paths.Count == 0)
            {
                error = "No path specified.";
                return false;
            }
            if (paths.Count == 1)
                return TryBuildTortoiseArgs(command, paths[0], out arguments, out error);

            try
            {
                var pathFile = CreateTempFile("paths", ".txt");
                File.WriteAllText(pathFile, string.Join("\n", paths), new UnicodeEncoding(false, false));
                if (!TryQuoteTortoiseValue(pathFile, out var quotedFile, out error))
                    return false;

                arguments = $"/command:{command} /pathfile:{quotedFile} /deletepathfile";
                return true;
            }
            catch (Exception ex)
            {
                error = $"Failed to write TortoiseSVN path list file: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// 把提交信息写入临时文件，返回 <c>/logmsgfile:"..."</c> 参数。信息可含引号、换行等任意字符。
        /// 消息为空时返回空字符串（不传该参数）。
        /// </summary>
        public static bool TryBuildTortoiseLogMessageArg(string message, out string argument, out string error)
        {
            argument = string.Empty;
            error = null;
            if (string.IsNullOrEmpty(message))
                return true;

            try
            {
                var file = CreateTempFile("logmsg", ".txt");
                File.WriteAllText(file, message, new UTF8Encoding(false));
                if (!TryQuoteTortoiseValue(file, out var quotedFile, out error))
                    return false;

                argument = $"/logmsgfile:{quotedFile}";
                return true;
            }
            catch (Exception ex)
            {
                error = $"Failed to write TortoiseSVN log message file: {ex.Message}";
                return false;
            }
        }

        private static bool TryQuoteTortoiseValue(string value, out string quoted, out string error)
        {
            quoted = null;
            if (string.IsNullOrEmpty(value))
            {
                error = "Path is empty.";
                return false;
            }
            if (value.IndexOf('"') >= 0)
            {
                error = $"Path contains a double quote and cannot be passed to TortoiseProc: {value}";
                return false;
            }

            error = null;
            quoted = "\"" + value + "\"";
            return true;
        }

        /// <summary>
        /// 在 %TEMP%/AgentCore/VCS 下创建临时文件；顺带清理一天前遗留的文件
        /// （logmsgfile 没有自动删除选项，外部工具读取时机不确定，不能在启动后立即删除）。
        /// </summary>
        private static string CreateTempFile(string prefix, string extension)
        {
            var dir = Path.Combine(Path.GetTempPath(), "AgentCore", "VCS");
            Directory.CreateDirectory(dir);

            try
            {
                var cutoff = DateTime.UtcNow.AddDays(-1);
                foreach (var old in Directory.GetFiles(dir))
                {
                    if (File.GetLastWriteTimeUtc(old) < cutoff)
                        File.Delete(old);
                }
            }
            catch
            {
                // 清理失败不影响本次启动
            }

            return Path.Combine(dir, $"{prefix}-{Guid.NewGuid():N}{extension}");
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

                AgentCore.Editor.Utils.AgentCoreLog.Info($"[Version Control] Launched {displayName}: {fileName} {arguments}");
                reason = displayName;
                return true;
            }
            catch (Exception ex)
            {
                reason = $"Failed to launch {displayName}: {ex.Message}";
                AgentCoreLog.Warning($"[Version Control] {reason} Command: {fileName} {arguments}");
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
