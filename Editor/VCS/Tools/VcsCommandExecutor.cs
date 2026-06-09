using System;
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
    /// </summary>
    public static class VcsCommandExecutor
    {
        private const int DefaultTimeoutSeconds = 30;

        /// <summary>
        /// 执行命令并返回结果
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

            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = command,
                        Arguments = arguments,
                        WorkingDirectory = workingDirectory,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    }
                };

                process.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                        outputBuilder.AppendLine(e.Data);
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                        errorBuilder.AppendLine(e.Data);
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // 等待进程完成或超时
                // 注意：使用 Task.Run 包装 WaitForExit(ms) 有超时参数的版本，
                // 然后再调用无参 WaitForExit() 确保所有异步 I/O 读取事件都已触发完毕。
                // 若只调用无参 WaitForExit()，在使用 BeginOutputReadLine/BeginErrorReadLine 时
                // 可能在异步读取完成前就返回，导致输出内容不完整。
                var timeoutMs = timeoutSeconds * 1000;
                var processTask = Task.Run(() =>
                {
                    bool exited = process.WaitForExit(timeoutMs);
                    if (exited)
                    {
                        // 等待所有异步 I/O 读取事件完成（.NET 文档要求的第二次调用）
                        process.WaitForExit();
                    }
                    return exited;
                }, ct);

                bool timedOut;
                try
                {
                    timedOut = !await processTask;
                }
                catch (OperationCanceledException)
                {
                    // 取消
                    try { process.Kill(); } catch { /* ignore */ }
                    result.Success = false;
                    result.ErrorMessage = "Command was cancelled";
                    result.ExitCode = -1;
                    return result;
                }

                if (timedOut)
                {
                    // 超时
                    try { process.Kill(); } catch { /* ignore */ }
                    result.Success = false;
                    result.ErrorMessage = $"Command timed out after {timeoutSeconds} seconds";
                    result.ExitCode = -1;
                }
                else if (ct.IsCancellationRequested)
                {
                    // 取消（在 WaitForExit 返回后检测到）
                    try { process.Kill(); } catch { /* ignore */ }
                    result.Success = false;
                    result.ErrorMessage = "Command was cancelled";
                    result.ExitCode = -1;
                }
                else
                {
                    // 正常完成
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
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to execute command: {ex.Message}";
                result.ExitCode = -1;
            }

            return result;
        }

        /// <summary>
        /// 执行 Shell 命令（跨平台）
        /// </summary>
        public static Task<CommandResult> ExecuteShellAsync(
            string command,
            string workingDirectory,
            int timeoutSeconds = DefaultTimeoutSeconds,
            CancellationToken ct = default)
        {
            var isWindows = Application.platform == RuntimePlatform.WindowsEditor;
            var shellCommand = isWindows ? "cmd.exe" : "/bin/bash";
            var shellArgs = isWindows ? $"/c {command}" : $"-c \"{command}\"";

            return ExecuteAsync(shellCommand, shellArgs, workingDirectory, timeoutSeconds, ct);
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
