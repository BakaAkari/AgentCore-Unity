using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Workspace.Resolution
{
    /// <summary>
    /// 解析 SVN 工作副本元数据。
    /// 优先使用 `svn info` 命令行解析；命令不可用时降级返回部分信息。
    /// 不阻塞 Unity Editor 主线程（超时保护）。
    /// </summary>
    public static class SvnWorkspaceInfoResolver
    {
        private const int CommandTimeoutMs = 5000;

        /// <summary>
        /// 解析指定路径的 SVN 工作副本信息。
        /// </summary>
        /// <param name="path">要查询的目录路径（通常为 UnityRoot 或其父目录）。</param>
        /// <returns>填充好的 WorkspaceVcsInfo；命令失败时返回降级结果。</returns>
        public static WorkspaceVcsInfo Resolve(string path)
        {
            var info = new WorkspaceVcsInfo { Type = WorkspaceVcsType.Svn };

            try
            {
                // 先检测 svn 命令是否可用
                if (!IsSvnCommandAvailable())
                {
                    info.IsCommandAvailable = false;
                    info.ErrorMessage = "svn command not found in PATH";
                    // 降级：尝试 .svn 目录探测
                    info.RootPath = FindSvnRootByDotSvn(path);
                    info.IsWorkingCopy = !string.IsNullOrEmpty(info.RootPath);
                    return info;
                }

                info.IsCommandAvailable = true;

                var output = RunSvnInfo(path);
                if (string.IsNullOrEmpty(output))
                {
                    info.ErrorMessage = "svn info returned empty output";
                    info.RootPath = FindSvnRootByDotSvn(path);
                    info.IsWorkingCopy = !string.IsNullOrEmpty(info.RootPath);
                    return info;
                }

                ParseSvnInfoOutput(output, info);
                info.IsWorkingCopy = !string.IsNullOrEmpty(info.RootPath);
            }
            catch (Exception ex)
            {
                info.ErrorMessage = $"SvnWorkspaceInfoResolver error: {ex.Message}";
                AgentCoreLog.Warning($"[AgentCore] {info.ErrorMessage}");
            }

            return info;
        }

        // ── 私有方法 ──────────────────────────────────────────────────────────

        private static bool IsSvnCommandAvailable()
        {
            try
            {
                var psi = BuildProcessStartInfo("svn", "--version --quiet");
                using (var p = Process.Start(psi))
                {
                    if (p == null) return false;
                    p.WaitForExit(3000);
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        private static string RunSvnInfo(string path)
        {
            try
            {
                var psi = BuildProcessStartInfo("svn", $"info \"{path}\"");
                using (var p = Process.Start(psi))
                {
                    if (p == null) return null;
                    var output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(CommandTimeoutMs);
                    return p.ExitCode == 0 ? output : null;
                }
            }
            catch { return null; }
        }

        private static void ParseSvnInfoOutput(string output, WorkspaceVcsInfo info)
        {
            // Working Copy Root Path: /path/to/root
            var rootMatch = Regex.Match(output, @"^Working Copy Root Path:\s*(.+)$", RegexOptions.Multiline);
            if (rootMatch.Success)
                info.RootPath = UnityRootResolver.NormalizePath(rootMatch.Groups[1].Value.Trim());

            // URL: https://svn.example.com/repo/branches/feature-x
            var urlMatch = Regex.Match(output, @"^URL:\s*(.+)$", RegexOptions.Multiline);
            if (urlMatch.Success)
            {
                info.Url = urlMatch.Groups[1].Value.Trim();
                info.BranchId = ExtractBranchId(info.Url);
            }

            // Repository Root: https://svn.example.com/repo
            var repoRootMatch = Regex.Match(output, @"^Repository Root:\s*(.+)$", RegexOptions.Multiline);
            if (repoRootMatch.Success)
                info.RepositoryRoot = repoRootMatch.Groups[1].Value.Trim();

            // Revision: 123456
            var revMatch = Regex.Match(output, @"^Revision:\s*(\d+)$", RegexOptions.Multiline);
            if (revMatch.Success)
                info.Revision = revMatch.Groups[1].Value.Trim();
        }

        /// <summary>
        /// 从 SVN URL 提取分支标识符。
        /// </summary>
        public static string ExtractBranchId(string url)
        {
            if (string.IsNullOrEmpty(url)) return string.Empty;

            // /branches/<name>
            var branchMatch = Regex.Match(url, @"/branches/([^/]+)");
            if (branchMatch.Success)
                return $"branches/{branchMatch.Groups[1].Value}";

            // /trunk
            if (Regex.IsMatch(url, @"/trunk(/|$)"))
                return "trunk";

            // /tags/<name>
            var tagMatch = Regex.Match(url, @"/tags/([^/]+)");
            if (tagMatch.Success)
                return $"tags/{tagMatch.Groups[1].Value}";

            return string.Empty;
        }

        /// <summary>
        /// 通过向上查找 .svn 目录来定位 SVN 工作副本根（SVN 1.7+ 只有根有 .svn）。
        /// </summary>
        private static string FindSvnRootByDotSvn(string startPath)
        {
            try
            {
                var current = new DirectoryInfo(startPath);
                string lastSvnDir = null;

                while (current != null)
                {
                    if (Directory.Exists(Path.Combine(current.FullName, ".svn")))
                        lastSvnDir = current.FullName;
                    else if (lastSvnDir != null)
                        break; // 已经离开 SVN 工作副本范围

                    current = current.Parent;
                }

                return lastSvnDir != null ? UnityRootResolver.NormalizePath(lastSvnDir) : null;
            }
            catch { return null; }
        }

        private static ProcessStartInfo BuildProcessStartInfo(string fileName, string arguments)
        {
            return new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
        }
    }
}
