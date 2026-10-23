using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Components.VCS.Tools
{
    /// <summary>
    /// svn 命令行的参数、编码与错误提示辅助。
    /// <para>
    /// Windows 上 svn.exe（含 TortoiseSVN 自带的命令行）是 ANSI 程序，实测（1.14）：
    /// <list type="bullet">
    /// <item>argv 与 <c>--targets</c> 文件都按系统 ANSI 代码页（ACP，中文系统为 936）解码，
    ///       ACP 表示不了的字符（如中文系统上的韩文、emoji）会变成 '?'，svn 随后报"找不到文件"；</item>
    /// <item>非 <c>--xml</c> 的文本输出按控制台代码页编码（子进程新建的隐藏控制台默认 OEM 代码页），
    ///       而 diff / blame 里的文件内容是文件原始字节（Unity 项目通常是 UTF-8），同一输出里两种编码混排；</item>
    /// <item><c>LANG</c> / <c>LC_ALL</c> 对 Windows 版 svn 无效；<c>--xml</c> 输出始终是 UTF-8。</item>
    /// </list>
    /// 因此：能用 <c>--xml</c> 的地方都用 <c>--xml</c>；文本输出逐行解码（合法 UTF-8 行按 UTF-8，否则按控制台代码页）；
    /// 提交信息走 <c>-F 文件 --encoding UTF-8</c>；路径表走按 ACP 写出的 <c>--targets</c> 文件；
    /// ACP 表示不了的路径提前给出明确错误，而不是让 svn 报一个误导性的"not found"。
    /// </para>
    /// </summary>
    public static class SvnCommandLine
    {
        /// <summary>svn 解析 argv / --targets 文件所用的编码（Windows: ACP；其他平台: UTF-8）。</summary>
        public static Encoding ArgumentEncoding => SvnExecutable.ArgumentEncoding;

        /// <summary>svn 文本输出所用的编码（Windows: 新建控制台的默认代码页即 OEM；其他平台: UTF-8）。</summary>
        public static Encoding OutputEncoding => SvnExecutable.OutputEncoding;

        /// <summary>命令是否为 svn（<c>svn</c>、<c>svn.exe</c>、<c>C:\...\svn.exe</c>）。</summary>
        public static bool IsSvnCommand(string command)
        {
            if (string.IsNullOrEmpty(command))
                return false;
            try
            {
                var name = Path.GetFileNameWithoutExtension(command.Trim().Trim('"'));
                return string.Equals(name, "svn", StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        // ===== 可执行文件定位（统一委托给 AgentCore.Editor.Utils.SvnExecutable） =====

        /// <summary>
        /// 解析要启动的 svn 可执行文件：命令不是裸的 "svn" 时原样返回；否则交给 <see cref="SvnExecutable.Resolve"/>
        /// （PATH → TortoiseSVN 注册表 Directory → Program Files）。
        /// </summary>
        public static string ResolveExecutable(string command)
        {
            if (!string.Equals(command?.Trim(), "svn", StringComparison.OrdinalIgnoreCase) || !VcsArgs.IsWindows)
                return command;
            return SvnExecutable.Resolve();
        }

        /// <inheritdoc cref="SvnExecutable.FindSvnExecutable"/>
        public static string FindSvnExecutable(string pathVariable, string tortoiseDirectory, IEnumerable<string> programFilesDirs, Func<string, bool> fileExists)
            => SvnExecutable.FindSvnExecutable(pathVariable, tortoiseDirectory, programFilesDirs, fileExists);

        // ===== 参数 =====

        /// <summary>
        /// 将工作副本路径转为单个 svn 参数：以 '-' 开头的相对路径加 "./" 前缀，避免被当成选项；
        /// 含 '@' 的路径追加 '@'，避免最后一个 '@' 之后的部分被解析为 peg revision（--targets 文件里同样适用）。
        /// </summary>
        public static string PathArg(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;
            if (path[0] == '-')
                path = "./" + path;
            return path.IndexOf('@') >= 0 ? path + "@" : path;
        }

        /// <summary>
        /// 本地 <c>svn diff</c>（不带 -r / --old / --new）的路径参数：svn 1.14 此时按字面解析目标、不剥离 peg，
        /// 追加 '@' 反而会报 E155010 "node ... not found"（实测）。只处理 '-' 开头的路径。
        /// </summary>
        public static string LocalDiffPathArg(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;
            return path[0] == '-' ? "./" + path : path;
        }

        /// <summary>文本能否无损地按 <see cref="ArgumentEncoding"/> 传给 svn（argv / --targets 文件）。</summary>
        public static bool CanPassToSvn(string value) => CanEncode(value, ArgumentEncoding);

        /// <summary>文本能否用指定编码无损往返。</summary>
        public static bool CanEncode(string value, Encoding encoding)
        {
            if (string.IsNullOrEmpty(value) || encoding is UTF8Encoding)
                return true;
            var ascii = true;
            foreach (var c in value)
            {
                if (c > 0x7F)
                {
                    ascii = false;
                    break;
                }
            }
            if (ascii)
                return true;
            try
            {
                return string.Equals(encoding.GetString(encoding.GetBytes(value)), value, StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>路径含系统代码页表示不了的字符时给出的错误说明。</summary>
        public static string UnrepresentablePathError(string path)
        {
            return $"Path contains characters that the svn command line cannot receive on this system (ANSI code page {ArgumentEncoding.CodePage}): {path}. " +
                   "svn.exe reads arguments in the system code page; rename the file, use TortoiseSVN for it, or enable Windows 'Beta: Use Unicode UTF-8 for worldwide language support'.";
        }

        /// <summary>
        /// 临时文件目录：优先系统临时目录；其路径本身含 ACP 表示不了的字符（如非中文用户名）时，
        /// 改用工作副本的 <c>.svn/tmp</c>（svn 自己的临时目录，不会出现在 status 里）。
        /// </summary>
        public static string GetTempDirectory(string workingDirectory)
        {
            var temp = Path.GetTempPath();
            if (CanPassToSvn(temp))
                return temp;
            if (!string.IsNullOrEmpty(workingDirectory))
            {
                var svnTmp = Path.Combine(workingDirectory, ".svn", "tmp");
                if (Directory.Exists(svnTmp))
                    return svnTmp;
            }
            return temp;
        }

        /// <summary>
        /// 写出 <c>--targets</c> 文件（每行一个已经过 <see cref="PathArg"/> 处理的路径，按 ACP 编码），返回文件路径。
        /// 调用方负责删除；路径须事先用 <see cref="CanPassToSvn"/> 过滤。
        /// </summary>
        public static string WriteTargetsFile(IEnumerable<string> svnPathArgs, string workingDirectory)
        {
            var file = Path.Combine(GetTempDirectory(workingDirectory), $"agentcore-svn-{Guid.NewGuid():N}.targets");
            var sb = new StringBuilder();
            foreach (var arg in svnPathArgs)
            {
                if (string.IsNullOrEmpty(arg))
                    continue;
                sb.Append(arg).Append('\n');
            }
            File.WriteAllBytes(file, ArgumentEncoding.GetBytes(sb.ToString()));
            return file;
        }

        /// <summary>
        /// 写出提交 / 锁定说明文件（UTF-8 无 BOM），配合 <c>-F 文件 --encoding UTF-8</c> 使用：
        /// 消息原样保留（emoji、任意语言），也不会触发 svn 的"log message is a pathname"(E205005) 检查。
        /// </summary>
        public static string WriteMessageFile(string message, string workingDirectory)
        {
            var file = Path.Combine(GetTempDirectory(workingDirectory), $"agentcore-svn-{Guid.NewGuid():N}.msg");
            File.WriteAllText(file, message ?? string.Empty, new UTF8Encoding(false));
            return file;
        }

        /// <summary>提交信息参数：<c>-F 文件 --encoding UTF-8</c>。</summary>
        public static List<string> MessageFileArgs(string messageFile)
        {
            return new List<string> { "-F", messageFile, "--encoding", "UTF-8" };
        }

        public static void TryDeleteFile(string path)
        {
            if (string.IsNullOrEmpty(path))
                return;
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // 临时文件删除失败不影响结果
            }
        }

        // ===== 输出解码 =====

        /// <inheritdoc cref="SvnExecutable.DecodeOutput"/>
        public static string DecodeOutput(byte[] bytes, int count, Encoding fallback = null)
            => SvnExecutable.DecodeOutput(bytes, count, fallback);

        /// <inheritdoc cref="SvnExecutable.IsValidUtf8"/>
        public static bool IsValidUtf8(byte[] bytes, int start, int length)
            => SvnExecutable.IsValidUtf8(bytes, start, length);

        // ===== 错误提示 =====

        /// <summary>
        /// 为常见 svn 错误码附加一行可操作的提示（工作副本被锁、需要认证、证书不受信任等）。
        /// 不改变原始错误文本；没有匹配的错误码时原样返回。
        /// </summary>
        public static string AppendErrorHint(string error)
        {
            if (string.IsNullOrEmpty(error))
                return error;

            var hint = GetErrorHint(error);
            return hint == null ? error : error.TrimEnd() + "\nHint: " + hint;
        }

        public static string GetErrorHint(string error)
        {
            if (string.IsNullOrEmpty(error))
                return null;

            if (Has(error, "E155004") || Has(error, "E155037") || Has(error, "E155032"))
                return "The working copy is locked by an interrupted or concurrent svn operation. Close other svn clients (TortoiseSVN dialogs) and run version_control action 'cleanup', then retry.";
            if (Has(error, "E155036"))
                return "The working copy format is older than this svn client. Run 'svn upgrade' (or TortoiseSVN > Upgrade working copy) once.";
            if (Has(error, "E155007"))
                return "The path is not inside an SVN working copy.";
            if (Has(error, "E215004") || Has(error, "E170001") || (Has(error, "E170013") && Has(error, "uthenticat")))
                return "SVN authentication failed. AgentCore runs svn with --non-interactive and cannot prompt for a password; authenticate once with TortoiseSVN (or run 'svn info <url>' in a terminal and save the credentials), then retry.";
            if (Has(error, "E230001"))
                return "The server's SSL certificate is not trusted. Accept it permanently once with TortoiseSVN or a terminal 'svn info <url>'; AgentCore does not bypass certificate checks.";
            if (Has(error, "E170013") || Has(error, "E731001") || Has(error, "E670002") || Has(error, "E175002"))
                return "Could not reach the SVN server. Check the network / VPN and the repository URL.";
            if (Has(error, "E155011") || Has(error, "E160028") || Has(error, "E160024"))
                return "Some items are out of date. Run version_control action 'update' first, resolve any conflicts, then commit again.";
            if (Has(error, "E155015"))
                return "There are unresolved conflicts. Resolve them (resolve_files) before committing.";
            if (Has(error, "W160035") || Has(error, "E160035") || Has(error, "E195022"))
                return "The file is locked by another user or working copy. Ask the lock owner to release it.";
            if (Has(error, "E195013"))
                return "The file is not locked in this working copy.";
            if (Has(error, "E155038"))
                return "Reverting a directory with changed children needs a recursive revert (pass recursive=true).";
            if (Has(error, "E720005") || Has(error, "E720032") || Has(error, "E720033"))
                return "A file is in use by another program (Unity, an IDE, antivirus). Close it or retry after Unity finishes importing.";
            if (Has(error, "E200009") && Has(error, "not known to exist in the repository"))
                return "A parent directory is scheduled for addition but not part of the commit; include it (commit_files adds such parents automatically).";
            return null;
        }

        private static bool Has(string text, string token) => text.IndexOf(token, StringComparison.Ordinal) >= 0;
    }

    /// <summary>
    /// Unity 资源与 .meta 的配对规则。.meta 里存放 GUID：只提交 / 删除 / 还原 / 移动资源而漏掉 .meta，
    /// 其他人更新后 GUID 会重新生成，所有引用丢失。
    /// </summary>
    public static class UnityMetaPaths
    {
        public const string MetaExtension = ".meta";

        public static bool IsMeta(string path) =>
            !string.IsNullOrEmpty(path) && path.EndsWith(MetaExtension, StringComparison.OrdinalIgnoreCase);

        /// <summary>资源 ↔ .meta 的配对路径：<c>a.png</c> → <c>a.png.meta</c>，<c>a.png.meta</c> → <c>a.png</c>。</summary>
        public static string GetCompanion(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            var trimmed = path.TrimEnd('/', '\\');
            if (trimmed.Length == 0)
                return null;
            return IsMeta(trimmed)
                ? trimmed.Substring(0, trimmed.Length - MetaExtension.Length)
                : trimmed + MetaExtension;
        }
    }
}
