using System;
using System.Text.RegularExpressions;

namespace AgentCore.Editor.Workspace.Resolution
{
    /// <summary>
    /// SVN 错误的可读提示（插件内唯一一张表）：从 svn 的 stderr / 错误信息中识别常见错误码，给出用户 / Agent 能直接照做的下一步。
    /// VcsCommandExecutor（经 SvnCommandLine.AppendErrorHint）、版本控制面板、远端检查、工作副本探测都用这里。
    /// 只做识别和提示，不拦截、不自动执行任何操作。纯字符串逻辑，可在任意线程使用。
    /// </summary>
    public static class SvnErrorHints
    {
        /// <summary>提示行前缀；<see cref="Describe"/> 据此保证同一段文本只追加一次提示。</summary>
        public const string HintPrefix = "Hint: ";

        /// <summary>识别出的 SVN 错误类别。</summary>
        public enum Kind
        {
            None,
            /// <summary>E155036：工作副本格式过旧，需要 svn upgrade。</summary>
            NeedsUpgrade,
            /// <summary>E155004 / E155037 / E155032 / E200033：工作副本被锁或上次操作中断，需要 svn cleanup。</summary>
            NeedsCleanup,
            /// <summary>E155007 / E155010：路径不在工作副本内 / 未纳入版本控制。</summary>
            NotWorkingCopy,
            /// <summary>E170001 / E215004 / E170013(认证)：需要凭据（--non-interactive 下不会弹窗）。</summary>
            AuthRequired,
            /// <summary>E230001：服务器证书不受信任。</summary>
            CertificateUntrusted,
            /// <summary>E170013 / E175002 / E731001 等：连不上服务器（离线 / 代理 / DNS）。</summary>
            Offline,
            /// <summary>E155011 / E160028 / E160024：有过期条目，需要先 update。</summary>
            OutOfDate,
            /// <summary>E155015：有未解决的冲突。</summary>
            Conflicted,
            /// <summary>W160035 / E160035 / E195022：被其他用户或工作副本锁定。</summary>
            LockedByOther,
            /// <summary>E195013：本工作副本没有持有锁。</summary>
            NotLocked,
            /// <summary>E155038：目录还原需要递归。</summary>
            NeedsRecursiveRevert,
            /// <summary>E720005 / E720032 / E720033：文件被其他程序占用。</summary>
            FileInUse,
            /// <summary>E200009 "not known to exist in the repository"：父目录待添加但不在提交范围内。</summary>
            ParentNotInCommit,
            /// <summary>svn 可执行文件找不到。</summary>
            CommandMissing
        }

        private static readonly Regex ErrorCodeRegex = new Regex(@"\b[EW](\d{6})\b", RegexOptions.Compiled);

        /// <summary>
        /// 识别错误类别。<paramref name="text"/> 可以是 stderr 原文或拼好的错误信息。
        /// 先按错误码（顺序即优先级），再按无错误码时的文本特征。
        /// </summary>
        public static Kind Classify(string text)
        {
            if (string.IsNullOrEmpty(text))
                return Kind.None;

            if (Has(text, "E155004") || Has(text, "E155037") || Has(text, "E155032") || Has(text, "E200033"))
                return Kind.NeedsCleanup;
            if (Has(text, "E155036"))
                return Kind.NeedsUpgrade;
            if (Has(text, "W160035") || Has(text, "E160035") || Has(text, "E195022"))
                return Kind.LockedByOther;
            if (Has(text, "E195013"))
                return Kind.NotLocked;
            if (Has(text, "E155038"))
                return Kind.NeedsRecursiveRevert;
            if (Has(text, "E200009") && Has(text, "not known to exist in the repository"))
                return Kind.ParentNotInCommit;
            if (Has(text, "E155011") || Has(text, "E160028") || Has(text, "E160024"))
                return Kind.OutOfDate;
            if (Has(text, "E155015"))
                return Kind.Conflicted;
            if (Has(text, "E720005") || Has(text, "E720032") || Has(text, "E720033"))
                return Kind.FileInUse;
            if (Has(text, "E230001"))
                return Kind.CertificateUntrusted;
            if (Has(text, "E170001") || Has(text, "E215004")
                || Has(text, "E170013") && Has(text, "uthenticat")
                || Has(text, "authorization failed") || Has(text, "No more credentials"))
                return Kind.AuthRequired;
            if (Has(text, "E170013") || Has(text, "E175002") || Has(text, "E731001")
                || Has(text, "E670002") || Has(text, "E730060") || Has(text, "E120108")
                || Has(text, "E210002") || Has(text, "Unable to connect"))
                return Kind.Offline;
            if (Has(text, "E155007") || Has(text, "E155010") || Has(text, "is not a working copy"))
                return Kind.NotWorkingCopy;

            // 无错误码时的文本特征
            if (Has(text, "is too old") && Has(text, "svn upgrade"))
                return Kind.NeedsUpgrade;
            if (Has(text, "svn cleanup") || Has(text, "working copy") && Has(text, "locked"))
                return Kind.NeedsCleanup;
            if (Has(text, "cannot find the file") || Has(text, "No such file or directory")
                || Has(text, "ApplicationName='svn'") || Has(text, "系统找不到指定的文件") || Has(text, "svn command not found"))
                return Kind.CommandMissing;
            return Kind.None;
        }

        /// <summary>
        /// 返回针对该错误的一句话提示（英文，供 UI / 日志 / LLM 上下文使用）；无法识别时返回 null。
        /// </summary>
        /// <param name="text">svn 错误信息。</param>
        /// <param name="workingCopyRoot">工作副本根目录（可为空），用于给出具体命令。</param>
        public static string GetHint(string text, string workingCopyRoot = null)
        {
            var at = string.IsNullOrEmpty(workingCopyRoot) ? "the working copy root" : $"\"{workingCopyRoot}\"";
            switch (Classify(text))
            {
                case Kind.NeedsUpgrade:
                    return $"The SVN working copy format is older than the installed svn client (E155036). Run `svn upgrade` in {at} (TortoiseSVN: right-click → SVN Upgrade working copy), then refresh.";
                case Kind.NeedsCleanup:
                    return $"The SVN working copy is locked by an interrupted or concurrent svn operation. Close other svn clients (TortoiseSVN dialogs), run `svn cleanup` in {at} (Cleanup button / version_control action 'cleanup'), then retry.";
                case Kind.AuthRequired:
                    return "SVN authentication failed or credentials are required. AgentCore runs svn with --non-interactive and cannot prompt for a password; authenticate once with TortoiseSVN (or `svn info <url>` in a terminal and save the credentials), then retry.";
                case Kind.CertificateUntrusted:
                    return "The server's SSL certificate is not trusted. Accept it permanently once with TortoiseSVN or a terminal `svn info <url>`; AgentCore does not bypass certificate checks.";
                case Kind.Offline:
                    return "Could not reach the SVN server (offline, VPN, proxy or wrong repository URL). Local status still works; remote checks will retry later.";
                case Kind.NotWorkingCopy:
                    return "The path is not inside an SVN working copy (or it is in an unversioned folder / not checked out).";
                case Kind.OutOfDate:
                    return "Some items are out of date. Run update first (version_control action 'update'), resolve any conflicts, then commit again.";
                case Kind.Conflicted:
                    return "There are unresolved conflicts. Resolve them (resolve_files / TortoiseSVN Resolve) before committing.";
                case Kind.LockedByOther:
                    return "The file is locked by another user or working copy. Ask the lock owner to release it.";
                case Kind.NotLocked:
                    return "The file is not locked in this working copy.";
                case Kind.NeedsRecursiveRevert:
                    return "Reverting a directory with changed children needs a recursive revert (pass recursive=true).";
                case Kind.FileInUse:
                    return "A file is in use by another program (Unity, an IDE, antivirus). Close it or retry after Unity finishes importing.";
                case Kind.ParentNotInCommit:
                    return "A parent directory is scheduled for addition but not part of the commit; include it (commit_files adds such parents automatically).";
                case Kind.CommandMissing:
                    return "The svn command-line client was not found. Install it (TortoiseSVN installer → enable \"command line client tools\"); AgentCore also looks in the TortoiseSVN install folder, so it does not need to be on PATH.";
                default:
                    return null;
            }
        }

        /// <summary>
        /// 原始错误信息 + 换行 + "Hint: ..."（若能识别）。原文保留，不吞掉任何信息；
        /// 文本里已经带有提示行时原样返回（命令执行层已追加过时不会重复）。
        /// </summary>
        public static string Describe(string text, string workingCopyRoot = null)
        {
            var trimmed = (text ?? string.Empty).Trim();
            if (HasHint(trimmed))
                return trimmed;
            var hint = GetHint(text, workingCopyRoot);
            if (hint == null)
                return trimmed;
            return string.IsNullOrEmpty(trimmed) ? HintPrefix + hint : trimmed + "\n" + HintPrefix + hint;
        }

        /// <summary>文本中是否已经含有提示行。</summary>
        public static bool HasHint(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;
            return text.StartsWith(HintPrefix, StringComparison.Ordinal) || text.IndexOf("\n" + HintPrefix, StringComparison.Ordinal) >= 0;
        }

        /// <summary>提取第一个 SVN 错误码（如 "E155036"），没有时返回 null。</summary>
        public static string FirstErrorCode(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;
            var match = ErrorCodeRegex.Match(text);
            return match.Success ? match.Value : null;
        }

        private static bool Has(string text, string value)
        {
            return text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
