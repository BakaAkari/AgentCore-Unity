using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

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
            return ExecuteAsync(command, VcsArgs.Join(arguments), workingDirectory, timeoutSeconds, ct);
        }

        /// <summary>
        /// 执行命令并返回结果。<paramref name="arguments"/> 是已转义的完整命令行；
        /// 含外部输入（路径、分支名、提交信息等）时请改用参数列表重载。
        /// </summary>
        public static async Task<CommandResult> ExecuteAsync(
            string command,
            string arguments,
            string workingDirectory,
            int timeoutSeconds = DefaultTimeoutSeconds,
            CancellationToken ct = default)
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

                process.OutputDataReceived += (sender, e) => AppendCapped(outputBuilder, e.Data);
                process.ErrorDataReceived += (sender, e) => AppendCapped(errorBuilder, e.Data);

                // 先挂 Exited 再 Start，避免进程秒退时错过事件
                var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                process.Exited += (sender, e) => exited.TrySetResult(true);

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // 关闭 stdin：任何读取输入的提示（密码、确认）立即读到 EOF 失败，而不是挂到超时
                try { process.StandardInput.Close(); } catch { /* 进程已退出 */ }

                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));
                var cancelTask = Task.Delay(Timeout.Infinite, ct);
                var completed = await Task.WhenAny(exited.Task, timeoutTask, cancelTask);

                if (completed != exited.Task)
                {
                    KillProcessTree(process);
                    if (completed == cancelTask)
                        return Cancelled(result);

                    result.Success = false;
                    result.ErrorMessage = $"Command timed out after {timeoutSeconds} seconds";
                    result.ExitCode = -1;
                    result.Output = outputBuilder.ToString();
                    result.Error = errorBuilder.ToString();
                    return result;
                }

                // 进程已退出；无参 WaitForExit 等待异步读取把剩余输出刷完（.NET 文档要求）
                await Task.Run(() => process.WaitForExit());

                result.ExitCode = process.ExitCode;
                result.Success = process.ExitCode == 0;
                result.Output = outputBuilder.ToString();
                result.Error = errorBuilder.ToString();

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

        /// <summary>
        /// 结束进程及其子进程。Unity 的 Mono <see cref="Process.Kill()"/> 只结束父进程，
        /// Windows 上用 taskkill /T 收掉整棵树。
        /// </summary>
        private static void KillProcessTree(Process process)
        {
            try
            {
                if (process.HasExited)
                    return;
            }
            catch
            {
                return;
            }

            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                try
                {
                    using var killer = Process.Start(new ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = $"/T /F /PID {process.Id}",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    if (killer != null && killer.WaitForExit(5000))
                        return;
                }
                catch
                {
                    // 回退到只结束父进程
                }
            }

            try { process.Kill(); } catch { /* 已退出 */ }
        }
    }

    /// <summary>
    /// 子进程参数转义。每个参数按目标平台的 argv 解析规则转义后以空格拼接，
    /// 保证外部输入（含空格、引号、反斜杠）原样成为一个 argv，不会被拆分或注入额外参数。
    /// </summary>
    public static class VcsArgs
    {
        public static string Join(IEnumerable<string> args)
        {
            if (args == null)
                return string.Empty;
            bool windows = Application.platform == RuntimePlatform.WindowsEditor;
            var sb = new StringBuilder();
            foreach (var arg in args)
            {
                if (arg == null)
                    continue;
                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append(windows ? QuoteWindows(arg) : QuotePosix(arg));
            }
            return sb.ToString();
        }

        /// <summary>按 CommandLineToArgvW / MSVCRT 规则转义：反斜杠仅在引号前（或结尾引号前）需要加倍。</summary>
        public static string QuoteWindows(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
                return arg;

            var sb = new StringBuilder(arg.Length + 2);
            sb.Append('"');
            for (int i = 0; i < arg.Length; i++)
            {
                int backslashes = 0;
                while (i < arg.Length && arg[i] == '\\')
                {
                    backslashes++;
                    i++;
                }

                if (i == arg.Length)
                {
                    sb.Append('\\', backslashes * 2);
                    break;
                }

                if (arg[i] == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                }
                else
                {
                    sb.Append('\\', backslashes);
                    sb.Append(arg[i]);
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>POSIX（Mono 在 macOS / Linux 上按 shell 规则拆分）：单引号包裹，内部单引号写作 '\''。</summary>
        public static string QuotePosix(string arg)
        {
            if (arg.Length > 0 && IsPosixSafe(arg))
                return arg;
            return "'" + arg.Replace("'", "'\\''") + "'";
        }

        private static bool IsPosixSafe(string arg)
        {
            foreach (var c in arg)
            {
                if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' || c == '/' || c == '=' || c == ':' || c == '@' || c == ',' || c == '+'))
                    return false;
            }
            return true;
        }

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
    }
}
