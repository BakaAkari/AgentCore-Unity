using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace AgentCore.Editor.Utils
{
    /// <summary>
    /// svn.exe 的唯一定位与解码入口（AgentCore.Editor 与 AgentCore.VCS.Editor 共用）。
    /// <list type="bullet">
    /// <item>定位：PATH → 注册表 HKLM\SOFTWARE\TortoiseSVN 的 Directory\bin → Program Files\TortoiseSVN\bin；</item>
    /// <item>编码：argv / --targets 按 ACP，文本输出按控制台（OEM）代码页，<c>--xml</c> 始终 UTF-8；</item>
    /// <item>解码在字节层面逐行进行：合法 UTF-8 行按 UTF-8，否则按控制台代码页；</item>
    /// <item><see cref="Run"/>：后台线程同步运行（Job Object 绑定，超时 KillTree），供不能依赖 VCS 程序集的代码使用。</item>
    /// </list>
    /// 不调用 Unity API，线程安全。
    /// </summary>
    public static class SvnExecutable
    {
        // ===== 定位 =====

        private static readonly object ExecutableLock = new object();
        private static string _resolved;

        /// <summary>
        /// 解析要启动的 svn 可执行文件：PATH 中有 svn.exe 时返回 "svn"，否则返回 TortoiseSVN 自带的 bin\svn.exe；
        /// 都找不到时返回 "svn"（让启动失败给出原生错误，且不缓存失败：装好 TortoiseSVN 后无需重启 Unity）。
        /// </summary>
        public static string Resolve()
        {
            if (!ProcessArgs.IsWindows)
                return "svn";

            lock (ExecutableLock)
            {
                if (_resolved != null)
                    return _resolved;
            }

            var found = FindSvnExecutable(
                Environment.GetEnvironmentVariable("PATH"),
                ReadTortoiseSvnRegistry("Directory"),
                new[]
                {
                    Environment.GetEnvironmentVariable("ProgramW6432"),
                    Environment.GetEnvironmentVariable("ProgramFiles"),
                    Environment.GetEnvironmentVariable("ProgramFiles(x86)")
                },
                File.Exists);
            if (found == null)
                return "svn";

            lock (ExecutableLock)
                _resolved = found;
            return found;
        }

        /// <summary>
        /// 定位 svn.exe（纯函数，便于测试）：PATH 中存在时返回 "svn"，
        /// 否则返回 TortoiseSVN 目录 / Program Files 下第一个存在的 bin\svn.exe，都没有时返回 null。
        /// </summary>
        public static string FindSvnExecutable(string pathVariable, string tortoiseDirectory, IEnumerable<string> programFilesDirs, Func<string, bool> fileExists)
        {
            if (!string.IsNullOrEmpty(pathVariable))
            {
                foreach (var dir in pathVariable.Split(';'))
                {
                    var trimmed = dir.Trim().Trim('"');
                    if (trimmed.Length == 0)
                        continue;
                    try
                    {
                        if (fileExists(Path.Combine(trimmed, "svn.exe")))
                            return "svn";
                    }
                    catch (ArgumentException)
                    {
                        // PATH 中的非法条目
                    }
                }
            }

            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(tortoiseDirectory))
            {
                try { candidates.Add(Path.Combine(Environment.ExpandEnvironmentVariables(tortoiseDirectory.Trim().Trim('"')), "bin", "svn.exe")); }
                catch (ArgumentException) { }
            }
            if (programFilesDirs != null)
            {
                foreach (var dir in programFilesDirs)
                {
                    if (!string.IsNullOrEmpty(dir))
                        candidates.Add(Path.Combine(dir, "TortoiseSVN", "bin", "svn.exe"));
                }
            }
            foreach (var candidate in candidates)
            {
                try
                {
                    if (fileExists(candidate))
                        return candidate;
                }
                catch (ArgumentException)
                {
                }
            }
            return null;
        }

        /// <summary>
        /// 读取 HKLM\SOFTWARE\TortoiseSVN 下的字符串值（Directory / ProcPath 等）。
        /// 通过反射调用 Microsoft.Win32.Registry（部分 API 兼容级别下不能直接引用）；非 Windows / 失败时返回 null。
        /// svn.exe 与 TortoiseProc.exe 的定位共用此方法。
        /// </summary>
        public static string ReadTortoiseSvnRegistry(string valueName)
        {
            if (!ProcessArgs.IsWindows || string.IsNullOrEmpty(valueName))
                return null;
            try
            {
                var registryType = Type.GetType("Microsoft.Win32.Registry, mscorlib")
                                   ?? Type.GetType("Microsoft.Win32.Registry, Microsoft.Win32.Registry");
                var getValue = registryType?.GetMethod("GetValue", new[] { typeof(string), typeof(string), typeof(object) });
                if (getValue == null)
                    return null;
                return getValue.Invoke(null, new object[] { @"HKEY_LOCAL_MACHINE\SOFTWARE\TortoiseSVN", valueName, null }) as string;
            }
            catch
            {
                return null;
            }
        }

        // ===== 编码 =====

        private static Encoding _argumentEncoding;
        private static Encoding _outputEncoding;

        /// <summary>svn 解析 argv / --targets 文件所用的编码（Windows: ACP；其他平台: UTF-8）。</summary>
        public static Encoding ArgumentEncoding => _argumentEncoding ??= ResolveCodePage(useOem: false);

        /// <summary>svn 文本输出所用的编码（Windows: 新建控制台的默认代码页即 OEM；其他平台: UTF-8）。</summary>
        public static Encoding OutputEncoding => _outputEncoding ??= ResolveCodePage(useOem: true);

        [DllImport("kernel32.dll")]
        private static extern uint GetACP();

        [DllImport("kernel32.dll")]
        private static extern uint GetOEMCP();

        private static Encoding ResolveCodePage(bool useOem)
        {
            if (!ProcessArgs.IsWindows)
                return new UTF8Encoding(false);
            try
            {
                var codePage = (int)(useOem ? GetOEMCP() : GetACP());
                if (codePage == 65001 || codePage <= 0)
                    return new UTF8Encoding(false);
                return Encoding.GetEncoding(codePage);
            }
            catch (Exception)
            {
                // 拿不到代码页或运行时缺少对应编码（I18N 程序集）：退回 UTF-8，至少 --xml 输出不受影响
                return new UTF8Encoding(false);
            }
        }

        /// <summary>
        /// 逐行解码 svn 输出：合法 UTF-8 的行（含 --xml 输出、UTF-8 文件内容）按 UTF-8，
        /// 其余行（svn 自己按控制台代码页输出的路径、提示）按 <paramref name="fallback"/>（默认 <see cref="OutputEncoding"/>）。
        /// 按 '\n' 切分对 UTF-8 与 GBK 等 DBCS 都安全（多字节序列里不会出现 0x0A）。
        /// </summary>
        public static string DecodeOutput(byte[] bytes, int count, Encoding fallback = null)
        {
            if (bytes == null || count <= 0)
                return string.Empty;
            fallback ??= OutputEncoding;
            var utf8 = new UTF8Encoding(false);
            if (fallback is UTF8Encoding)
                return StripBom(utf8.GetString(bytes, 0, count));

            var sb = new StringBuilder(count);
            var start = 0;
            while (start < count)
            {
                var end = Array.IndexOf(bytes, (byte)'\n', start, count - start);
                var lineEnd = end < 0 ? count : end + 1;
                var length = lineEnd - start;
                sb.Append(IsValidUtf8(bytes, start, length)
                    ? utf8.GetString(bytes, start, length)
                    : fallback.GetString(bytes, start, length));
                start = lineEnd;
            }
            return StripBom(sb.ToString());
        }

        private static string StripBom(string value)
        {
            return value.Length > 0 && value[0] == '﻿' ? value.Substring(1) : value;
        }

        /// <summary>严格校验 UTF-8（拒绝过长编码、代理区与超出 U+10FFFF 的序列）。</summary>
        public static bool IsValidUtf8(byte[] bytes, int start, int length)
        {
            var i = start;
            var end = start + length;
            while (i < end)
            {
                var b = bytes[i];
                if (b < 0x80)
                {
                    i++;
                    continue;
                }

                int extra;
                int min;
                if (b >= 0xC2 && b <= 0xDF) { extra = 1; min = 0x80; }
                else if (b >= 0xE0 && b <= 0xEF) { extra = 2; min = 0x800; }
                else if (b >= 0xF0 && b <= 0xF4) { extra = 3; min = 0x10000; }
                else return false;

                if (i + extra >= end)
                    return false; // 序列被截断

                var cp = b & (0x3F >> extra);
                for (var k = 1; k <= extra; k++)
                {
                    var c = bytes[i + k];
                    if ((c & 0xC0) != 0x80)
                        return false;
                    cp = (cp << 6) | (c & 0x3F);
                }
                if (cp < min || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF))
                    return false;
                i += extra + 1;
            }
            return true;
        }

        // ===== 同步运行（仅后台线程） =====

        /// <summary><see cref="Run"/> 的结果。</summary>
        public struct RunResult
        {
            /// <summary>进程是否成功启动（false 表示 svn 不可用，见 <see cref="Error"/>）。</summary>
            public bool Started;
            public bool TimedOut;
            public int ExitCode;
            public string Output;
            public string Error;
        }

        /// <summary>
        /// 运行 svn 并同步等待——<b>只在后台线程调用</b>（异步路径请用 VcsCommandExecutor）。
        /// 使用 <see cref="Resolve"/> 定位的 svn.exe；stdin 立即关闭；stdout / stderr 以字节读取后经
        /// <see cref="DecodeOutput"/> 解码（--xml 为 UTF-8，文本回退控制台代码页）；
        /// 进程绑定到 <see cref="ChildProcessJob"/>，超时结束整棵进程树。
        /// </summary>
        public static RunResult Run(string workingDirectory, int timeoutMs, params string[] args)
        {
            var run = new RunResult();
            var psi = new ProcessStartInfo
            {
                FileName = Resolve(),
                Arguments = ProcessArgs.Join(args),
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
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

                var outputTask = ReadAllBytesAsync(p.StandardOutput.BaseStream);
                var errorTask = ReadAllBytesAsync(p.StandardError.BaseStream);
                if (!p.WaitForExit(timeoutMs))
                {
                    ChildProcessJob.KillTree(p);
                    run.TimedOut = true;
                    run.ExitCode = -1;
                    return run;
                }

                // 进程已退出，读取很快完成；仍给上限，防止孙进程占着管道不放
                if (!outputTask.Wait(timeoutMs) || !errorTask.Wait(timeoutMs))
                {
                    ChildProcessJob.KillTree(p);
                    run.TimedOut = true;
                    run.ExitCode = -1;
                    return run;
                }

                run.ExitCode = p.ExitCode;
                var output = outputTask.Result;
                var error = errorTask.Result;
                run.Output = DecodeOutput(output, output.Length);
                run.Error = DecodeOutput(error, error.Length);
                return run;
            }
        }

        private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
        {
            using (var buffer = new MemoryStream())
            {
                await stream.CopyToAsync(buffer).ConfigureAwait(false);
                return buffer.ToArray();
            }
        }
    }
}
