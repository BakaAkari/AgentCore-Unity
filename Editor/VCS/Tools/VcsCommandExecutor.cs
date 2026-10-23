using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Components.VCS.Tools
{
    /// <summary>
    /// VCS 命令执行器
    /// 统一处理命令行调用、输出捕获、超时控制
    /// <para>
    /// 子进程一律非交互：stdin 立即关闭，git 的凭据提示 / 分页器 / 编辑器通过环境变量禁用，
    /// 避免在 Editor 里挂起等待一个永远不会出现的输入。超时与取消会结束整个进程树
    /// （git 会派生 credential helper / ssh 等子进程，只杀父进程会留下孤儿进程继续占用锁）。
    /// </para>
    /// </summary>
    public static class VcsCommandExecutor
    {
        private const int DefaultTimeoutSeconds = 30;

        /// <summary>单个输出流的捕获上限，防止 git log / diff 之类的巨型输出撑爆内存。</summary>
        private const int MaxCapturedChars = 4 * 1024 * 1024;

        /// <summary>
        /// 执行命令并返回结果（参数列表形式，推荐）。每个元素作为一个独立 argv 传给子进程，
        /// 由 <see cref="VcsArgs.Join"/> 负责转义；调用方不应自行拼接引号。
        /// </summary>
        public static Task<CommandResult> ExecuteAsync(
            string command,
            IEnumerable<string> arguments,
            string workingDirectory,
            int timeoutSeconds = DefaultTimeoutSeconds,
            CancellationToken ct = default)
        {
            return ExecuteCoreAsync(command, VcsArgs.Join(arguments), workingDirectory, timeoutSeconds, false, ct);
        }

        /// <summary>
        /// 执行命令并原样捕获 stdout（不按行拆分、不改写换行）。用于 <c>-z</c> 之类以 NUL 分隔的输出：
        /// 按行读取会把路径里的 \r / \n 当成行尾并替换成 Environment.NewLine，破坏记录边界。
        /// stdout 截断时 <see cref="CommandResult.OutputTruncated"/> 为 true，且不追加任何提示文字。
        /// </summary>
        public static Task<CommandResult> ExecuteRawAsync(
            string command,
            IEnumerable<string> arguments,
            string workingDirectory,
            int timeoutSeconds = DefaultTimeoutSeconds,
            CancellationToken ct = default)
        {
            return ExecuteCoreAsync(command, VcsArgs.Join(arguments), workingDirectory, timeoutSeconds, true, ct);
        }

        /// <summary>
        /// 对一批路径执行同一命令：路径按 <see cref="VcsArgs.ChunkPaths"/> 拆成多组，每组一次调用，
        /// 保证单次命令行不超过 Windows 32K 上限。任一组失败即停止（不吞错误），
        /// 失败信息注明是第几组；已成功的组输出保留在 Output 中。
        /// </summary>
        /// <param name="prefixArgs">路径之前的固定参数（如 <c>add --</c>）。</param>
        /// <param name="paths">路径列表，调用方负责做好 SvnPathArg 之类的转换。</param>
        public static async Task<CommandResult> ExecuteChunkedAsync(
            string command,
            IReadOnlyList<string> prefixArgs,
            IReadOnlyList<string> paths,
            string workingDirectory,
            int timeoutSeconds = DefaultTimeoutSeconds,
            CancellationToken ct = default)
        {
            var chunks = VcsArgs.ChunkPaths(command, prefixArgs, paths);
            if (chunks.Count <= 1)
            {
                var single = new List<string>(prefixArgs ?? Array.Empty<string>());
                if (chunks.Count == 1)
                    single.AddRange(chunks[0]);
                return await ExecuteAsync(command, single, workingDirectory, timeoutSeconds, ct);
            }

            var output = new StringBuilder();
            var error = new StringBuilder();
            for (int i = 0; i < chunks.Count; i++)
            {
                var args = new List<string>(prefixArgs ?? Array.Empty<string>());
                args.AddRange(chunks[i]);
                var part = await ExecuteAsync(command, args, workingDirectory, timeoutSeconds, ct);

                if (!string.IsNullOrEmpty(part.Output))
                    output.Append(part.Output);
                if (!string.IsNullOrEmpty(part.Error))
                    error.Append(part.Error);

                if (!part.Success)
                {
                    part.Output = output.ToString();
                    part.Error = error.ToString();
                    part.ErrorMessage = $"[batch {i + 1}/{chunks.Count}, {chunks[i].Count} path(s); earlier batches already applied] {part.ErrorMessage}";
                    return part;
                }
            }

            return new CommandResult
            {
                Success = true,
                ExitCode = 0,
                Output = output.ToString(),
                Error = error.ToString()
            };
        }

        /// <summary>
        /// 执行命令并返回结果。<paramref name="arguments"/> 是已转义的完整命令行；
        /// 含外部输入（路径、分支名、提交信息等）时请改用参数列表重载。
        /// </summary>
        public static Task<CommandResult> ExecuteAsync(
            string command,
            string arguments,
            string workingDirectory,
            int timeoutSeconds = DefaultTimeoutSeconds,
            CancellationToken ct = default)
        {
            return ExecuteCoreAsync(command, arguments, workingDirectory, timeoutSeconds, false, ct);
        }

        private static async Task<CommandResult> ExecuteCoreAsync(
            string command,
            string arguments,
            string workingDirectory,
            int timeoutSeconds,
            bool rawStdout,
            CancellationToken ct)
        {
            var result = new CommandResult();
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            if (ct.IsCancellationRequested)
                return Cancelled(result);

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = command,
                        Arguments = arguments,
                        WorkingDirectory = workingDirectory,
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    },
                    EnableRaisingEvents = true
                };
                ApplyNonInteractiveEnvironment(process.StartInfo);

                if (!rawStdout)
                    process.OutputDataReceived += (sender, e) => AppendCapped(outputBuilder, e.Data);
                process.ErrorDataReceived += (sender, e) => AppendCapped(errorBuilder, e.Data);

                // 先挂 Exited 再 Start，避免进程秒退时错过事件
                var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                process.Exited += (sender, e) => exited.TrySetResult(true);

                process.Start();
                // 加入进程级 Job：编辑器退出 / 崩溃时连带结束 git/svn/p4 及其派生的 ssh、credential helper
                ChildProcessJob.TryAssign(process);
                Task rawReadTask = null;
                if (rawStdout)
                    rawReadTask = ReadRawCappedAsync(process.StandardOutput, outputBuilder, truncated => result.OutputTruncated = truncated);
                else
                    process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // 关闭 stdin：任何读取输入的提示（密码、确认）立即读到 EOF 失败，而不是挂到超时
                try { process.StandardInput.Close(); } catch { /* 进程已退出 */ }

                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));
                var cancelTask = Task.Delay(Timeout.Infinite, ct);
                var completed = await Task.WhenAny(exited.Task, timeoutTask, cancelTask);

                if (completed != exited.Task)
                {
                    ChildProcessJob.KillTree(process);
                    if (completed == cancelTask)
                        return Cancelled(result);

                    result.Success = false;
                    result.ErrorMessage = $"Command timed out after {timeoutSeconds} seconds";
                    result.ExitCode = -1;
                    result.Output = Snapshot(outputBuilder);
                    result.Error = Snapshot(errorBuilder);
                    return result;
                }

                // 进程已退出；无参 WaitForExit 等待异步读取把剩余输出刷完（.NET 文档要求）
                await Task.Run(() => process.WaitForExit());
                if (rawReadTask != null)
                    await rawReadTask;

                result.ExitCode = process.ExitCode;
                result.Success = process.ExitCode == 0;
                result.Output = Snapshot(outputBuilder);
                result.Error = Snapshot(errorBuilder);

                if (!result.Success && string.IsNullOrEmpty(result.ErrorMessage))
                {
                    result.ErrorMessage = string.IsNullOrEmpty(result.Error)
                        ? $"Command failed with exit code {result.ExitCode}"
                        : result.Error;
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to execute command: {ex.Message}";
                result.ExitCode = -1;
            }

            return result;
        }

        private static CommandResult Cancelled(CommandResult result)
        {
            result.Success = false;
            result.ErrorMessage = "Command was cancelled";
            result.ExitCode = -1;
            return result;
        }

        private static string Snapshot(StringBuilder builder)
        {
            lock (builder)
                return builder.ToString();
        }

        /// <summary>
        /// 原样读取 stdout（保留 NUL / \r / \n），超出上限后继续读取并丢弃，避免子进程因管道写满而卡住。
        /// </summary>
        private static async Task ReadRawCappedAsync(System.IO.StreamReader reader, StringBuilder builder, Action<bool> setTruncated)
        {
            var buffer = new char[8192];
            try
            {
                int read;
                while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                {
                    lock (builder)
                    {
                        var room = MaxCapturedChars - builder.Length;
                        if (room <= 0)
                        {
                            setTruncated(true);
                            continue;
                        }
                        if (read > room)
                        {
                            builder.Append(buffer, 0, room);
                            setTruncated(true);
                        }
                        else
                        {
                            builder.Append(buffer, 0, read);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 进程被杀 / 管道关闭：保留已读到的内容
            }
        }

        private static void AppendCapped(StringBuilder builder, string line)
        {
            if (line == null)
                return;
            lock (builder)
            {
                if (builder.Length >= MaxCapturedChars)
                    return;
                builder.AppendLine(line);
                if (builder.Length >= MaxCapturedChars)
                    builder.AppendLine("[AgentCore] Output truncated.");
            }
        }

        /// <summary>
        /// 禁用所有交互入口。参考 VS Code Git 扩展 / Codex：GIT_TERMINAL_PROMPT=0 让 HTTPS 凭据提示直接失败，
        /// GCM_INTERACTIVE=never 禁止 Git Credential Manager 弹窗，分页器与编辑器替换为立即返回的命令。
        /// </summary>
        private static void ApplyNonInteractiveEnvironment(ProcessStartInfo info)
        {
            var env = info.EnvironmentVariables;
            env["GIT_TERMINAL_PROMPT"] = "0";
            env["GCM_INTERACTIVE"] = "never";
            env["GIT_PAGER"] = "cat";
            env["PAGER"] = "cat";
            env["GIT_EDITOR"] = "true";
            env["GIT_SEQUENCE_EDITOR"] = "true";
            env["GIT_MERGE_AUTOEDIT"] = "no";
            env["GIT_ASKPASS"] = "";
            env["SSH_ASKPASS"] = "";
            // 不覆盖 GIT_SSH_COMMAND：会顶掉用户 core.sshCommand 里的密钥配置；ssh 无终端 + stdin 已关闭时提示会直接失败
        }
    }

    /// <summary>
    /// 子进程参数转义。每个参数按目标平台的 argv 解析规则转义后以空格拼接，
    /// 保证外部输入（含空格、引号、反斜杠）原样成为一个 argv，不会被拆分或注入额外参数。
    /// </summary>
    public static class VcsArgs
    {
        /// <summary>
        /// 单次命令行（程序名 + 参数）的字符预算。Windows CreateProcess 上限 32767，
        /// 留出余量给程序完整路径与转义膨胀。
        /// </summary>
        public const int MaxCommandLineChars = 30000;

        /// <summary>
        /// 当前是否 Windows。用 Environment 判断而非 Application.platform：
        /// 后者是 Unity API，后台线程（探测 / 远端检查）里调用不安全。
        /// </summary>
        public static readonly bool IsWindows = ProcessArgs.IsWindows;

        /// <summary>
        /// 把路径列表拆成多组，使每组 "command + prefixArgs + 该组路径" 转义后的命令行长度不超过
        /// <paramref name="maxChars"/>。顺序保持不变；单个路径本身超长时独占一组（交给子进程报错，而不是丢弃）。
        /// 路径为空时返回空列表。
        /// </summary>
        public static List<List<string>> ChunkPaths(
            string command,
            IReadOnlyList<string> prefixArgs,
            IReadOnlyList<string> paths,
            int maxChars = MaxCommandLineChars)
        {
            var chunks = new List<List<string>>();
            if (paths == null || paths.Count == 0)
                return chunks;

            var baseLength = (command ?? string.Empty).Length + 3 + Join(prefixArgs).Length;
            var current = new List<string>();
            var currentLength = baseLength;

            foreach (var path in paths)
            {
                if (path == null)
                    continue;

                var argLength = (IsWindows ? QuoteWindows(path) : QuotePosix(path)).Length + 1;
                if (current.Count > 0 && currentLength + argLength > maxChars)
                {
                    chunks.Add(current);
                    current = new List<string>();
                    currentLength = baseLength;
                }

                current.Add(path);
                currentLength += argLength;
            }

            if (current.Count > 0)
                chunks.Add(current);
            return chunks;
        }

        public static string Join(IEnumerable<string> args) => ProcessArgs.Join(args);

        /// <summary>按 CommandLineToArgvW / MSVCRT 规则转义（实现位于 <see cref="ProcessArgs"/>，与 ripgrep 等共用）。</summary>
        public static string QuoteWindows(string arg) => ProcessArgs.QuoteWindows(arg);

        /// <summary>POSIX 单引号转义（实现位于 <see cref="ProcessArgs"/>）。</summary>
        public static string QuotePosix(string arg) => ProcessArgs.QuotePosix(arg);

        /// <summary>
        /// 校验将作为 revision / 分支名 / 标签名传入的值：不能以 '-' 开头（否则会被当成选项，
        /// 例如 <c>git show --output=...</c> 写任意文件），不能含控制字符。
        /// </summary>
        public static bool IsSafeRef(string value, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(value))
            {
                error = "Reference name is empty.";
                return false;
            }
            if (value[0] == '-')
            {
                error = $"Reference name must not start with '-': {value}";
                return false;
            }
            foreach (var c in value)
            {
                if (char.IsControl(c))
                {
                    error = "Reference name must not contain control characters.";
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>
    /// 命令执行结果
    /// </summary>
    public class CommandResult
    {
        public bool Success { get; set; }
        public int ExitCode { get; set; }
        public string Output { get; set; }
        public string Error { get; set; }
        public string ErrorMessage { get; set; }

        /// <summary>stdout 超过捕获上限被截断（仅 <see cref="VcsCommandExecutor.ExecuteRawAsync"/> 设置）。</summary>
        public bool OutputTruncated { get; set; }
    }
}
