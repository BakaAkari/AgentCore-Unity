using System;
using System.Collections.Generic;
using System.Text;

namespace AgentCore.Editor.Utils
{
    /// <summary>
    /// 子进程参数转义（argv 列表 → <see cref="System.Diagnostics.ProcessStartInfo.Arguments"/> 字符串）。
    /// <para>
    /// Unity 的 Mono 配置没有 <c>ProcessStartInfo.ArgumentList</c>，只能拼一条命令行：
    /// 每个参数按目标平台的 argv 解析规则转义后以空格拼接，保证外部输入（含空格、引号、反斜杠）
    /// 原样成为一个 argv，不会被拆分或注入额外参数。
    /// Windows 按 CommandLineToArgvW / MSVCRT 规则（Rust / C 程序都用这套规则解析）；
    /// macOS / Linux 上 Mono 按 shell 规则拆分 Arguments，因此用单引号包裹。
    /// </para>
    /// </summary>
    public static class ProcessArgs
    {
        /// <summary>
        /// 当前是否 Windows。用 Environment 判断而非 Application.platform：
        /// 后者是 Unity API，后台线程里调用不安全。
        /// </summary>
        public static readonly bool IsWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;

        /// <summary>按当前平台规则转义并以空格拼接；null 参数跳过。</summary>
        public static string Join(IEnumerable<string> args)
        {
            return Join(args, IsWindows);
        }

        /// <summary>按指定平台规则转义并以空格拼接（测试用，可在任意平台验证两套规则）。</summary>
        public static string Join(IEnumerable<string> args, bool windows)
        {
            if (args == null)
                return string.Empty;
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

        /// <summary>按当前平台规则转义单个参数。</summary>
        public static string Quote(string arg)
        {
            return IsWindows ? QuoteWindows(arg) : QuotePosix(arg);
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
    }
}
