using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Workspace.Resolution
{
    /// <summary>
    /// 解析 SVN 工作副本元数据。
    /// <list type="bullet">
    /// <item><see cref="ResolveFast"/>：只做 .svn 目录探测（纯文件系统），可在主线程调用。</item>
    /// <item><see cref="Resolve"/>：运行一次 `svn info --xml`（最长约 15 秒），
    ///       <b>不要在主线程调用</b>；由 WorkspaceContextService 在后台探测。命令不可用时降级返回部分信息。</item>
    /// </list>
    /// 不依赖 Unity API，可在任意线程使用。
    /// </summary>
    public static class SvnWorkspaceInfoResolver
    {
        /// <summary>后台线程运行，给足时间：Windows 冷启动 / 杀毒扫描 / 大工作副本的 wc.db 首次打开可能超过 5 秒。</summary>
        private const int CommandTimeoutMs = 15000;

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
        /// <para>
        /// 使用 <c>svn info --xml</c>：XML 输出固定为 UTF-8、标签不随 svn 语言包本地化。
        /// 纯文本输出在 Windows 上按系统 ANSI 代码页（中文系统为 GBK）编码，中文路径会被按 UTF-8 解成乱码；
        /// 本地化的 svn 还会把 "Working Copy Root Path:" 等标签翻译掉，导致整段解析失败、工作副本被判为不存在。
        /// </para>
        /// </summary>
        /// <param name="path">要查询的目录路径（通常为 UnityRoot 或其父目录）。</param>
        /// <returns>填充好的 WorkspaceVcsInfo；命令失败时返回降级结果（RootPath 来自 .svn 探测，ErrorMessage 含原因与处理提示）。</returns>
        public static WorkspaceVcsInfo Resolve(string path)
        {
            var info = new WorkspaceVcsInfo { Type = WorkspaceVcsType.Svn };

            try
            {
                // 路径末尾加 '@'：路径本身含 '@' 时，svn 会把最后一个 '@' 之后当作 peg revision
                var run = RunSvn(path, "info", "--xml", "--non-interactive", path + "@");
                if (!run.Started)
                {
                    info.IsCommandAvailable = false;
                    info.ErrorMessage = SvnErrorHints.Describe("svn command not found: " + (run.Error ?? "unknown error"));
                    // 降级：.svn 目录探测
                    info.RootPath = FindSvnRootByDotSvn(path);
                    info.IsWorkingCopy = !string.IsNullOrEmpty(info.RootPath);
                    return info;
                }

                info.IsCommandAvailable = true;

                if (run.TimedOut || run.ExitCode != 0 || string.IsNullOrWhiteSpace(run.Output))
                {
                    info.RootPath = FindSvnRootByDotSvn(path);
                    info.IsWorkingCopy = !string.IsNullOrEmpty(info.RootPath);
                    var raw = run.TimedOut
                        ? $"svn info timed out after {CommandTimeoutMs / 1000} s"
                        : (string.IsNullOrWhiteSpace(run.Error) ? $"svn info exited with code {run.ExitCode}" : run.Error.Trim());
                    info.ErrorMessage = SvnErrorHints.Describe(raw, info.RootPath);
                    return info;
                }

                if (!ParseSvnInfoXml(run.Output, info) || string.IsNullOrEmpty(info.RootPath))
                {
                    info.RootPath = info.RootPath ?? FindSvnRootByDotSvn(path);
                    info.ErrorMessage = "svn info returned unparseable output";
                }
                info.IsWorkingCopy = !string.IsNullOrEmpty(info.RootPath);
            }
            catch (Exception ex)
            {
                info.ErrorMessage = $"SvnWorkspaceInfoResolver error: {ex.Message}";
                AgentCoreLog.Warning($"[AgentCore] {info.ErrorMessage}");
            }

            return info;
        }

        /// <summary>
        /// 解析 <c>svn info --xml</c> 输出到 <paramref name="info"/>。返回是否找到 entry。
        /// </summary>
        public static bool ParseSvnInfoXml(string xml, WorkspaceVcsInfo info)
        {
            if (string.IsNullOrWhiteSpace(xml) || info == null)
                return false;

            XDocument doc;
            try
            {
                doc = XDocument.Parse(xml);
            }
            catch (Exception)
            {
                return false;
            }

            var entry = doc.Root?.Element("entry");
            if (entry == null)
                return false;

            var wcRoot = entry.Element("wc-info")?.Element("wcroot-abspath")?.Value;
            if (!string.IsNullOrWhiteSpace(wcRoot))
                info.RootPath = UnityRootResolver.NormalizePath(wcRoot.Trim());

            var url = entry.Element("url")?.Value;
            if (!string.IsNullOrWhiteSpace(url))
            {
                info.Url = url.Trim();
                info.BranchId = ExtractBranchId(info.Url);
            }

            var repoRoot = entry.Element("repository")?.Element("root")?.Value;
            if (!string.IsNullOrWhiteSpace(repoRoot))
                info.RepositoryRoot = repoRoot.Trim();

            var revision = entry.Attribute("revision")?.Value;
            if (!string.IsNullOrWhiteSpace(revision))
                info.Revision = revision.Trim();

            return true;
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
        /// 通过向上查找 .svn 目录来定位 SVN 工作副本根：返回<b>最近</b>的含 .svn 的祖先目录（含自身），规范化正斜杠。
        /// <para>
        /// SVN 1.7+ 只在工作副本根放 .svn，因此最近的 .svn 就是该路径所属工作副本的根，与 svn info 的
        /// wcroot-abspath 一致：Unity 工程位于大工作副本深层子目录时向上找到外层根；
        /// 工程本身（或其祖先）是 svn:externals 拉下来的独立工作副本时，返回 external 自己的根。
        /// （旧实现取"连续 .svn 链的最顶层"，对紧挨着外层根的 external 会越过它返回外层根，与 svn info 不一致，
        /// 导致每次启动快速解析与后台 svn info 的根不同、上次缓存被丢弃、Fingerprint 来回变化。）
        /// </para>
        /// 未找到时返回 null。不启动子进程，可在任意线程调用。
        /// </summary>
        public static string FindWorkingCopyRoot(string startPath)
        {
            return FindSvnRootByDotSvn(startPath);
        }

        // ── 私有方法 ──────────────────────────────────────────────────────────

        private static string FindSvnRootByDotSvn(string startPath)
        {
            if (string.IsNullOrEmpty(startPath))
                return null;

            try
            {
                var current = new DirectoryInfo(startPath);
                while (current != null)
                {
                    if (Directory.Exists(Path.Combine(current.FullName, ".svn")))
                        return UnityRootResolver.NormalizePath(current.FullName);
                    current = current.Parent;
                }
                return null;
            }
            catch { return null; }
        }

        private struct SvnRun
        {
            public bool Started;
            public bool TimedOut;
            public int ExitCode;
            public string Output;
            public string Error;
        }

        /// <summary>
        /// 运行 svn 并同步等待（仅后台线程）。stdin 立即关闭（--non-interactive 之外的兜底），
        /// stdout / stderr 异步读取避免管道写满，超时结束整棵进程树。
        /// </summary>
        private static SvnRun RunSvn(string workingDirectory, params string[] args)
        {
            var run = new SvnRun();
            var psi = new ProcessStartInfo
            {
                FileName = "svn",
                Arguments = ProcessArgs.Join(args),
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                // --xml 输出固定 UTF-8；stderr 在 Windows 上是 ANSI 代码页，这里只用来识别错误码
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            if (!string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
                psi.WorkingDirectory = workingDirectory;

            Process p;
            try
            {
                p = Process.Start(psi);
            }
            catch (Exception ex)
            {
                run.Error = ex.Message;
                return run;
            }

            if (p == null)
            {
                run.Error = "Process.Start returned null for svn";
                return run;
            }

            using (p)
            {
                run.Started = true;
                ChildProcessJob.TryAssign(p);
                try { p.StandardInput.Close(); } catch { /* 进程已退出 */ }

                var outputTask = p.StandardOutput.ReadToEndAsync();
                var errorTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(CommandTimeoutMs))
                {
                    ChildProcessJob.KillTree(p);
                    run.TimedOut = true;
                    run.ExitCode = -1;
                    return run;
                }

                // 进程已退出，读取很快完成；仍给上限，防止孙进程占着管道不放
                if (!outputTask.Wait(CommandTimeoutMs) || !errorTask.Wait(CommandTimeoutMs))
                {
                    ChildProcessJob.KillTree(p);
                    run.TimedOut = true;
                    run.ExitCode = -1;
                    return run;
                }

                run.ExitCode = p.ExitCode;
                run.Output = outputTask.Result;
                run.Error = errorTask.Result;
                return run;
            }
        }
    }
}
