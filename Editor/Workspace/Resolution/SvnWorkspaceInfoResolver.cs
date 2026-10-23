using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Workspace.Resolution
{
    /// <summary>
    /// 解析 SVN 工作副本元数据。
    /// <list type="bullet">
    /// <item><see cref="ResolveFast"/>：只做 .svn 目录探测（纯文件系统），可在主线程调用。</item>
    /// <item><see cref="Resolve"/>：运行 `svn --version` + `svn info`（最长约 8 秒），
    ///       <b>不要在主线程调用</b>；由 WorkspaceContextService 在后台探测。命令不可用时降级返回部分信息。</item>
    /// </list>
    /// 不依赖 Unity API，可在任意线程使用。
    /// </summary>
    public static class SvnWorkspaceInfoResolver
    {
        private const int CommandTimeoutMs = 5000;

        /// <summary>
        /// 快速解析：只向上查找 .svn 目录确定工作副本根，不启动 svn 子进程。
        /// URL / RepositoryRoot / BranchId / Revision 为空，<see cref="WorkspaceVcsInfo.IsCommandAvailable"/> 为 false（未探测）。
        /// </summary>
        public static WorkspaceVcsInfo ResolveFast(string path)
        {
            var info = new WorkspaceVcsInfo { Type = WorkspaceVcsType.Svn };
            info.RootPath = FindSvnRootByDotSvn(path);
            info.IsWorkingCopy = !string.IsNullOrEmpty(info.RootPath);
            if (!info.IsWorkingCopy)
                info.ErrorMessage = "No .svn directory found";
            return info;
        }

        /// <summary>
        /// 解析指定路径的 SVN 工作副本信息（会启动 svn 子进程并同步等待，只在后台线程调用）。
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
                    ChildProcessJob.TryAssign(p);
                    // 输出重定向了但不关心内容：异步读空，避免管道写满卡住子进程
                    p.StandardOutput.ReadToEndAsync();
                    p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(3000))
                    {
                        ChildProcessJob.KillTree(p);
                        return false;
                    }
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
                    ChildProcessJob.TryAssign(p);
                    // 同步 ReadToEnd 会一直等到进程退出，超时形同虚设；改为异步读取 + 超时后结束整棵进程树
                    var outputTask = p.StandardOutput.ReadToEndAsync();
                    p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(CommandTimeoutMs) || !outputTask.Wait(CommandTimeoutMs))
                    {
                        ChildProcessJob.KillTree(p);
                        return null;
                    }
                    return p.ExitCode == 0 ? outputTask.Result : null;
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
