using System;
using System.Text.RegularExpressions;

namespace AgentCore.Editor.Workspace.Resolution
{
    /// <summary>
    /// SVN 错误的可读提示：从 svn 的 stderr / 错误信息中识别常见错误码，给出用户能直接照做的下一步。
    /// 只做识别和提示，不拦截、不自动执行任何操作。纯字符串逻辑，可在任意线程使用。
    /// </summary>
    public static class SvnErrorHints
    {
        /// <summary>识别出的 SVN 错误类别。</summary>
        public enum Kind
        {
            None,
            /// <summary>E155036：工作副本格式过旧，需要 svn upgrade。</summary>
            NeedsUpgrade,
            /// <summary>E155004 / E155037 / E200033：工作副本被锁或上次操作中断，需要 svn cleanup。</summary>
            NeedsCleanup,
            /// <summary>E155007 / E155010：路径不在工作副本内 / 未纳入版本控制。</summary>
            NotWorkingCopy,
            /// <summary>E170001 / E215004 / E170013(认证)：需要凭据（--non-interactive 下不会弹窗）。</summary>
            AuthRequired,
            /// <summary>E170013 / E175002 / E731001 等：连不上服务器（离线 / 代理 / DNS）。</summary>
            Offline,
            /// <summary>svn 可执行文件找不到。</summary>
            CommandMissing
        }

        private static readonly Regex ErrorCodeRegex = new Regex(@"\b[EW](\d{6})\b", RegexOptions.Compiled);

        /// <summary>
        /// 识别错误类别。<paramref name="text"/> 可以是 stderr 原文或拼好的错误信息。
        /// </summary>
        public static Kind Classify(string text)
        {
            if (string.IsNullOrEmpty(text))
                return Kind.None;

            if (Contains(text, "E155036") || Contains(text, "is too old") && Contains(text, "svn upgrade"))
                return Kind.NeedsUpgrade;
            if (Contains(text, "E155004") || Contains(text, "E155037") || Contains(text, "E200033")
                || Contains(text, "svn cleanup") || Contains(text, "working copy") && Contains(text, "locked"))
                return Kind.NeedsCleanup;
            if (Contains(text, "E170001") || Contains(text, "E215004") || Contains(text, "E230001")
                || Contains(text, "authorization failed") || Contains(text, "No more credentials"))
                return Kind.AuthRequired;
            if (Contains(text, "E170013") || Contains(text, "E175002") || Contains(text, "E731001")
                || Contains(text, "E670002") || Contains(text, "E730060") || Contains(text, "E120108")
                || Contains(text, "E210002") || Contains(text, "Unable to connect"))
                return Kind.Offline;
            if (Contains(text, "E155007") || Contains(text, "E155010") || Contains(text, "is not a working copy"))
                return Kind.NotWorkingCopy;
            if (Contains(text, "cannot find the file") || Contains(text, "No such file or directory")
                || Contains(text, "ApplicationName='svn'") || Contains(text, "系统找不到指定的文件"))
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
                    return $"The SVN working copy format is older than the installed svn client (E155036). Run `svn upgrade` in {at} (TortoiseSVN: right-click → SVN Upgrade working copy), then click Refresh.";
                case Kind.NeedsCleanup:
                    return $"The SVN working copy is locked or a previous operation was interrupted. Run `svn cleanup` in {at} (or the Cleanup button), then retry.";
                case Kind.AuthRequired:
                    return "The SVN server requires credentials. AgentCore runs svn non-interactively; log in once with TortoiseSVN or `svn info <url>` in a terminal so the credentials are cached, then retry.";
                case Kind.Offline:
                    return "Could not reach the SVN server (offline, VPN or proxy). Local status still works; remote checks will retry later.";
                case Kind.NotWorkingCopy:
                    return "This path is not inside a versioned part of the SVN working copy (unversioned folder or not checked out).";
                case Kind.CommandMissing:
                    return "The svn command-line client was not found. Install it (TortoiseSVN installer → enable \"command line client tools\") and make sure its bin folder is on PATH, then restart Unity.";
                default:
                    return null;
            }
        }

        /// <summary>
        /// 原始错误信息 + 提示（若能识别）。原文保留，提示追加在后面，不吞掉任何信息。
        /// </summary>
        public static string Describe(string text, string workingCopyRoot = null)
        {
            var hint = GetHint(text, workingCopyRoot);
            var trimmed = (text ?? string.Empty).Trim();
            if (hint == null)
                return trimmed;
            return string.IsNullOrEmpty(trimmed) ? hint : trimmed + "\n" + hint;
        }

        /// <summary>提取第一个 SVN 错误码（如 "E155036"），没有时返回 null。</summary>
        public static string FirstErrorCode(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;
            var match = ErrorCodeRegex.Match(text);
            return match.Success ? match.Value : null;
        }

        private static bool Contains(string text, string value)
        {
            return text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
