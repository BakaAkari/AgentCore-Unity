using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Tools.Infrastructure
{
    /// <summary>
    /// 一次 ripgrep 搜索的参数。字段与各工具已有参数一一对应，由 <see cref="RgSearch.BuildArguments"/> 映射为 argv。
    /// </summary>
    public sealed class RgSearchOptions
    {
        /// <summary>搜索模式（正则，或 <see cref="FixedStrings"/> 时的字面量）。</summary>
        public string Pattern;

        /// <summary>按字面量搜索（--fixed-strings）。</summary>
        public bool FixedStrings;

        /// <summary>大小写敏感（-s）；false 时 -i。</summary>
        public bool CaseSensitive;

        /// <summary>rg 的工作目录；输出路径相对于它。</summary>
        public string WorkingDirectory;

        /// <summary>要搜索的路径（相对 <see cref="WorkingDirectory"/> 或绝对路径），放在 "--" 之后。为空时搜索 "."。</summary>
        public List<string> Paths = new List<string>();

        /// <summary>包含 glob（-g），例如 "*.cs"。为空 = 全部文件。</summary>
        public List<string> IncludeGlobs = new List<string>();

        /// <summary>排除 glob（自动加 '!' 前缀），例如 "obj/"、"*.prefab"。</summary>
        public List<string> ExcludeGlobs = new List<string>();

        /// <summary>搜索隐藏文件 / 目录（--hidden）。</summary>
        public bool IncludeHidden;

        /// <summary>遵守 .gitignore / .ignore / .rgignore（rg 默认）；false 时 --no-ignore。</summary>
        public bool RespectIgnoreFiles = true;

        /// <summary>最大目录深度（--max-depth）；null = 不限。</summary>
        public int? MaxDepth;

        /// <summary>跳过大于该字节数的文件（--max-filesize）；0 = 不限。</summary>
        public long MaxFileSizeBytes;

        /// <summary>--max-columns（rg 在 JSON 模式下不截断行，行文本截断由 <see cref="MaxLineChars"/> 在解析时完成）。</summary>
        public int MaxColumns = 2000;

        /// <summary>解析时单行文本截断长度。</summary>
        public int MaxLineChars = 2000;

        /// <summary>上下文行数（-C）。</summary>
        public int ContextLines;

        /// <summary>收集的匹配上限。</summary>
        public int MaxResults = 100;

        /// <summary>收满后继续计数以得到准确 totalMatches（直到超时或 <see cref="MaxCountedMatches"/>）。</summary>
        public bool KeepCountingAfterLimit = true;

        /// <summary>计数上限，达到后结束 rg 并把总数标记为下界。</summary>
        public int MaxCountedMatches = 1000000;

        /// <summary>
        /// 按路径排序输出（--sort path）。会让 rg 退化为单线程遍历，Windows 上大工程可慢一个数量级，默认关闭；
        /// 收集到的结果在返回前总会按 路径 / 行号 排序。
        /// </summary>
        public bool SortByPath;

        /// <summary>整体墙钟超时，超时结束 rg 并返回已有结果（truncatedReason = "timeout"）。</summary>
        public TimeSpan Timeout = TimeSpan.FromSeconds(10);
    }

    /// <summary>rg 运行结果状态。</summary>
    public enum RgSearchStatus
    {
        /// <summary>搜索完成（可能被截断，见 <see cref="RgSearchResult.Truncated"/>）。</summary>
        Ok,

        /// <summary>rg 无法编译该正则（如环视 / 反向引用）——调用方应回退 .NET 正则。</summary>
        RegexUnsupported,

        /// <summary>rg 无法运行或异常退出且没有可用输出——调用方应回退 .NET 实现。</summary>
        Failed
    }

    /// <summary>
    /// ripgrep 搜索结果。截断语义与 P1 的 .NET 搜索一致：truncatedReason ∈ max_results / timeout。
    /// </summary>
    public sealed class RgSearchResult
    {
        public RgSearchStatus Status;

        /// <summary>RegexUnsupported / Failed 时的说明（取自 rg stderr）。</summary>
        public string ErrorMessage;

        public List<RgMatch> Matches = new List<RgMatch>();
        public int TotalMatches;

        /// <summary>总数只是下界（超时或达到计数上限后提前结束了 rg）。</summary>
        public bool TotalIsLowerBound;

        /// <summary>已搜索文件数（rg summary）；提前结束时为已知下界（有匹配的文件数）。</summary>
        public int FilesSearched;
        public int FilesMatched;

        /// <summary>rg 报告无法读取的路径数（stderr 中的错误行）。</summary>
        public int FilesUnreadable;

        public long BytesSearched;
        public bool Truncated;
        public string TruncatedReason;

        /// <summary>rg 退出码（被结束时为 -1）。</summary>
        public int ExitCode;

        /// <summary>rg 的 stderr（截断到有限长度）。</summary>
        public string Stderr;

        /// <summary>rg 进程无法启动（文件被删除 / 无权限）。调用方可据此 <see cref="RgLocator.MarkUnavailable"/>。</summary>
        public bool StartFailed;

        /// <summary>实际执行的命令行（诊断用）。</summary>
        public string CommandLine;
    }

    /// <summary>
    /// ripgrep 文本搜索后端：线性时间正则引擎（无 ReDoS）、遵守 .gitignore、多线程，大型 Unity 工程上远快于进程内扫描。
    /// <para>
    /// 调用方通过 <see cref="RgLocator.TryGetExecutable"/> 取得 rg 路径；拿不到或本类返回非 Ok 状态时
    /// 回退原有 .NET 实现（从不因为 rg 拒绝一次搜索）。本类不调用 Unity API，可在任意线程使用。
    /// </para>
    /// </summary>
    public static class RgSearch
    {
        /// <summary>stderr 采集上限。</summary>
        private const int MaxStderrChars = 16 * 1024;

        /// <summary>
        /// 把选项映射为 rg 的 argv。始终包含 --json、--no-config；模式用 -e 传递（以 '-' 开头的模式也安全），
        /// 路径放在 "--" 之后。
        /// </summary>
        public static List<string> BuildArguments(RgSearchOptions o)
        {
            if (o == null)
                throw new ArgumentNullException(nameof(o));

            var args = new List<string>
            {
                "--json",
                "--no-config",
                // CRLF 文件里 '$' 也能匹配行尾（与 .NET 逐行 ReadAllLines 后匹配一致）
                "--crlf",
            };

            if (o.FixedStrings)
                args.Add("--fixed-strings");

            args.Add(o.CaseSensitive ? "--case-sensitive" : "--ignore-case");

            if (o.IncludeHidden)
                args.Add("--hidden");

            if (!o.RespectIgnoreFiles)
                args.Add("--no-ignore");

            if (o.MaxDepth.HasValue)
            {
                args.Add("--max-depth");
                args.Add(Math.Max(0, o.MaxDepth.Value).ToString());
            }

            if (o.MaxColumns > 0)
            {
                args.Add("--max-columns");
                args.Add(o.MaxColumns.ToString());
            }

            if (o.MaxFileSizeBytes > 0)
            {
                args.Add("--max-filesize");
                args.Add(o.MaxFileSizeBytes.ToString());
            }

            if (o.ContextLines > 0)
            {
                args.Add("--context");
                args.Add(o.ContextLines.ToString());
            }

            if (o.SortByPath)
            {
                args.Add("--sort");
                args.Add("path");
            }

            if (o.IncludeGlobs != null)
            {
                foreach (var g in o.IncludeGlobs)
                {
                    if (string.IsNullOrWhiteSpace(g))
                        continue;
                    args.Add("--glob");
                    args.Add(g.Trim());
                }
            }

            // 排除放在包含之后：rg 中后出现的 glob 优先
            if (o.ExcludeGlobs != null)
            {
                foreach (var g in o.ExcludeGlobs)
                {
                    if (string.IsNullOrWhiteSpace(g))
                        continue;
                    var trimmed = g.Trim();
                    args.Add("--glob");
                    args.Add(trimmed.StartsWith("!", StringComparison.Ordinal) ? trimmed : "!" + trimmed);
                }
            }

            args.Add("-e");
            args.Add(o.Pattern ?? string.Empty);

            args.Add("--");
            if (o.Paths != null && o.Paths.Count > 0)
            {
                foreach (var p in o.Paths)
                {
                    if (!string.IsNullOrEmpty(p))
                        args.Add(p);
                }
            }
            else
            {
                args.Add(".");
            }

            return args;
        }

        /// <summary>
        /// 运行 rg 并流式解析输出。取消时抛出 <see cref="OperationCanceledException"/>（与 .NET 实现一致）；
        /// 超时返回已有结果并标记 truncatedReason = "timeout"。
        /// </summary>
        public static RgSearchResult Run(string rgPath, RgSearchOptions options, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(rgPath))
                throw new ArgumentException("rg path is required.", nameof(rgPath));
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            ct.ThrowIfCancellationRequested();

            var args = BuildArguments(options);
            var result = new RgSearchResult
            {
                CommandLine = rgPath + " " + ProcessArgs.Join(args)
            };

            var parser = new RgJsonParser(
                options.MaxResults,
                options.ContextLines,
                options.KeepCountingAfterLimit,
                options.MaxCountedMatches,
                options.MaxLineChars);

            var stderr = new StringBuilder();
            int stderrErrorLines = 0;
            int stopReason = StopNone;

            Process process;
            try
            {
                process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = rgPath,
                        Arguments = ProcessArgs.Join(args),
                        WorkingDirectory = string.IsNullOrEmpty(options.WorkingDirectory) ? Environment.CurrentDirectory : options.WorkingDirectory,
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = new UTF8Encoding(false),
                        StandardErrorEncoding = new UTF8Encoding(false)
                    },
                };
                process.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data == null)
                        return;
                    lock (stderr)
                    {
                        // rg 对单个路径的错误形如 "rg: <path>: <reason>"；regex 解析错误是多行块，不计为不可读文件
                        if (e.Data.StartsWith("rg: ", StringComparison.Ordinal) && !e.Data.StartsWith("rg: regex parse error", StringComparison.Ordinal))
                            stderrErrorLines++;
                        if (stderr.Length < MaxStderrChars)
                            stderr.AppendLine(e.Data);
                    }
                };
                process.Start();
                ChildProcessJob.TryAssign(process);
            }
            catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException || ex is System.IO.IOException)
            {
                result.Status = RgSearchStatus.Failed;
                result.ErrorMessage = $"Failed to start ripgrep: {ex.Message}";
                result.StartFailed = true;
                return result;
            }

            using (process)
            {
                try { process.StandardInput.Close(); } catch { /* 已退出 */ }
                process.BeginErrorReadLine();

                // 超时 / 取消：结束 rg，读取循环随之读到 EOF
                using var timeoutCts = new CancellationTokenSource(options.Timeout);
                using var timeoutReg = timeoutCts.Token.Register(() =>
                {
                    Interlocked.CompareExchange(ref stopReason, StopTimeout, StopNone);
                    KillRg(process);
                });
                using var cancelReg = ct.Register(() =>
                {
                    Interlocked.CompareExchange(ref stopReason, StopCancelled, StopNone);
                    KillRg(process);
                });

                try
                {
                    string line;
                    while ((line = process.StandardOutput.ReadLine()) != null)
                    {
                        parser.ProcessLine(line);
                        if (parser.IsDone)
                        {
                            Interlocked.CompareExchange(ref stopReason, StopLimit, StopNone);
                            KillRg(process);
                            break;
                        }
                    }
                }
                catch (Exception) when (Volatile.Read(ref stopReason) != StopNone)
                {
                    // 进程被结束导致管道关闭：保留已解析的内容
                }

                parser.Complete();

                try
                {
                    if (!process.WaitForExit(5000))
                        KillRg(process);
                    else
                        process.WaitForExit(); // 刷完异步 stderr
                }
                catch
                {
                    // 进程句柄已失效
                }

                try { result.ExitCode = process.HasExited ? process.ExitCode : -1; }
                catch { result.ExitCode = -1; }
            }

            var reason = Volatile.Read(ref stopReason);
            if (reason == StopCancelled)
                ct.ThrowIfCancellationRequested();

            lock (stderr)
            {
                result.Stderr = stderr.ToString();
                result.FilesUnreadable = stderrErrorLines;
            }

            if (reason == StopNone && result.ExitCode == 2 && !parser.SawAnyMessage)
            {
                // 没有任何 JSON 输出的 exit 2：模式无法编译，或参数 / 环境错误
                result.Status = IsRegexError(result.Stderr) ? RgSearchStatus.RegexUnsupported : RgSearchStatus.Failed;
                result.ErrorMessage = SummarizeError(result.Stderr, result.ExitCode);
                return result;
            }

            if (reason == StopNone && result.ExitCode != 0 && result.ExitCode != 1 && result.ExitCode != 2)
            {
                result.Status = RgSearchStatus.Failed;
                result.ErrorMessage = SummarizeError(result.Stderr, result.ExitCode);
                return result;
            }

            result.Status = RgSearchStatus.Ok;
            result.Matches = parser.Matches;
            // rg 多线程输出顺序不固定：按 路径 / 行号 排序，展示稳定
            result.Matches.Sort((a, b) =>
            {
                var c = string.CompareOrdinal(a.Path, b.Path);
                return c != 0 ? c : a.Line.CompareTo(b.Line);
            });
            result.TotalMatches = parser.TotalMatches;
            result.FilesMatched = parser.FilesMatched;
            result.FilesSearched = parser.FilesSearched >= 0 ? parser.FilesSearched : parser.FilesMatched;
            result.BytesSearched = Math.Max(0, parser.BytesSearched);

            bool stoppedEarly = reason == StopTimeout || parser.CountCapReached || (reason == StopLimit && !options.KeepCountingAfterLimit);
            result.TotalIsLowerBound = stoppedEarly;

            if (reason == StopTimeout)
            {
                result.Truncated = true;
                result.TruncatedReason = "timeout";
            }
            else if (parser.OverLimit)
            {
                result.Truncated = true;
                result.TruncatedReason = "max_results";
            }

            return result;
        }

        private const int StopNone = 0;
        private const int StopTimeout = 1;
        private const int StopCancelled = 2;
        private const int StopLimit = 3;

        /// <summary>rg stderr 是否为正则编译错误（Rust regex 不支持环视 / 反向引用，或语法错误）。</summary>
        public static bool IsRegexError(string stderr)
        {
            if (string.IsNullOrEmpty(stderr))
                return false;
            return stderr.IndexOf("regex parse error", StringComparison.OrdinalIgnoreCase) >= 0
                   || stderr.IndexOf("error parsing regex", StringComparison.OrdinalIgnoreCase) >= 0
                   || stderr.IndexOf("regex could not be compiled", StringComparison.OrdinalIgnoreCase) >= 0
                   || stderr.IndexOf("compiled regex exceeds size limit", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>从 stderr 提取简短说明（去掉 PCRE2 建议等冗余行）。</summary>
        public static string SummarizeError(string stderr, int exitCode)
        {
            if (string.IsNullOrWhiteSpace(stderr))
                return $"ripgrep exited with code {exitCode}";

            var lines = new List<string>();
            foreach (var raw in stderr.Split('\n'))
            {
                var l = raw.TrimEnd('\r');
                if (l.Trim().Length == 0)
                    continue;
                if (l.IndexOf("--pcre2", StringComparison.Ordinal) >= 0 || l.StartsWith("and look-around", StringComparison.Ordinal))
                    continue;
                lines.Add(l.Trim());
                if (lines.Count >= 6)
                    break;
            }
            return string.Join(" ", lines);
        }

        /// <summary>
        /// 结束 rg 进程树（Scoop shim 等包装层也一并结束）。
        /// </summary>
        private static void KillRg(Process process)
        {
            ChildProcessJob.KillTree(process);
        }
    }
}
