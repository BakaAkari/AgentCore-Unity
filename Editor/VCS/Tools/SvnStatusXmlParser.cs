using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace AgentCore.Editor.Components.VCS.Tools
{
    /// <summary>
    /// <c>svn status --xml</c> 解析结果。
    /// </summary>
    public sealed class SvnStatusParseResult
    {
        /// <summary>本地有变更（或冲突）的条目；normal / ignored 条目不包含在内。</summary>
        public List<VcsFileStatus> Files { get; } = new List<VcsFileStatus>();

        /// <summary>
        /// 带锁信息的条目（K 本工作副本持有锁 / O 他人持有锁 / T 锁被偷 / B 锁已失效），
        /// 包括内容未修改、只是被锁定的文件（它们不在 <see cref="Files"/> 中）。O / T / B 只有 <c>-u</c> 时才能看到。
        /// </summary>
        public List<VcsFileStatus> LockedFiles { get; } = new List<VcsFileStatus>();

        /// <summary>远端有更新的路径（仅 <c>svn status -u --xml</c> 输出含 repos-status 时有值）。</summary>
        public List<string> RemoteChangedFiles { get; } = new List<string>();

        /// <summary><c>&lt;against revision="N"/&gt;</c> 中的远端版本号（-u 时才有）。</summary>
        public string AgainstRevision { get; set; }

        /// <summary>需要提醒用户的情况（如工作副本被锁、需要 cleanup）。</summary>
        public List<string> Warnings { get; } = new List<string>();

        /// <summary>XML 不完整（输出被截断等），已解析出的条目仍然保留。</summary>
        public bool IsIncomplete { get; set; }

        /// <summary>解析错误信息（IsIncomplete 时有值）。</summary>
        public string Error { get; set; }
    }

    /// <summary>
    /// 解析 <c>svn status --xml</c> / <c>svn status -u --xml</c>。
    /// <para>
    /// 用 XmlReader 流式读取：输出被截断时仍能拿到截断点之前的条目，并标记 <see cref="SvnStatusParseResult.IsIncomplete"/>，
    /// 而不是整体解析失败后静默返回空列表。
    /// </para>
    /// <para>
    /// 冲突判定覆盖三种情况：<c>item="conflicted"</c>（文本冲突）、<c>props="conflicted"</c>（属性冲突）、
    /// <c>tree-conflicted="true"</c>（树冲突）。只改了属性（<c>item="normal" props="modified"</c>）也算 Modified。
    /// 另外解析 svn status 文本输出中第 3 列（L 工作副本锁）、第 5 列（S 已切换）、第 6 列（K/O/T/B 锁）
    /// 与 move 信息（moved-from / moved-to）、copied（+ 带历史添加）。
    /// </para>
    /// </summary>
    public static class SvnStatusXmlParser
    {
        private sealed class EntryState
        {
            public string Path;
            public string Item, Props, TreeConflicted, Copied, Switched, WcLocked, MovedFrom, MovedTo;
            public string ReposItem, ReposProps;
            public bool HasReposStatus;
            public string WcLockToken, WcLockOwner, WcLockComment, ReposLockToken, ReposLockOwner, ReposLockComment;
        }

        public static SvnStatusParseResult Parse(string xml)
        {
            var result = new SvnStatusParseResult();
            if (string.IsNullOrWhiteSpace(xml))
                return result;

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                IgnoreComments = true,
                IgnoreWhitespace = true
            };

            EntryState entry = null;
            // 当前所在的状态块：wc-status / repos-status / null
            string block = null;
            var inLock = false;
            string textField = null;
            var wcLockedSeen = false;

            try
            {
                using (var reader = XmlReader.Create(new StringReader(xml), settings))
                {
                    while (reader.Read())
                    {
                        switch (reader.NodeType)
                        {
                            case XmlNodeType.Element:
                                switch (reader.Name)
                                {
                                    case "entry":
                                        entry = new EntryState { Path = reader.GetAttribute("path") };
                                        block = null;
                                        inLock = false;
                                        if (reader.IsEmptyElement)
                                        {
                                            AddEntry(result, entry);
                                            entry = null;
                                        }
                                        break;
                                    case "wc-status":
                                        if (entry != null)
                                        {
                                            entry.Item = reader.GetAttribute("item");
                                            entry.Props = reader.GetAttribute("props");
                                            entry.TreeConflicted = reader.GetAttribute("tree-conflicted");
                                            entry.Copied = reader.GetAttribute("copied");
                                            entry.Switched = reader.GetAttribute("switched");
                                            entry.WcLocked = reader.GetAttribute("wc-locked");
                                            entry.MovedFrom = reader.GetAttribute("moved-from");
                                            entry.MovedTo = reader.GetAttribute("moved-to");
                                            if (!reader.IsEmptyElement)
                                                block = "wc";
                                        }
                                        break;
                                    case "repos-status":
                                        if (entry != null)
                                        {
                                            entry.HasReposStatus = true;
                                            entry.ReposItem = reader.GetAttribute("item");
                                            entry.ReposProps = reader.GetAttribute("props");
                                            if (!reader.IsEmptyElement)
                                                block = "repos";
                                        }
                                        break;
                                    case "lock":
                                        inLock = entry != null && block != null && !reader.IsEmptyElement;
                                        break;
                                    case "token":
                                    case "owner":
                                    case "comment":
                                        textField = inLock && !reader.IsEmptyElement ? reader.Name : null;
                                        break;
                                    case "against":
                                        result.AgainstRevision = reader.GetAttribute("revision");
                                        break;
                                }
                                break;

                            case XmlNodeType.Text:
                            case XmlNodeType.CDATA:
                                if (textField != null && entry != null)
                                {
                                    var value = reader.Value;
                                    if (block == "wc")
                                    {
                                        if (textField == "token") entry.WcLockToken = value;
                                        else if (textField == "owner") entry.WcLockOwner = value;
                                        else entry.WcLockComment = (entry.WcLockComment ?? string.Empty) + value;
                                    }
                                    else if (block == "repos")
                                    {
                                        if (textField == "token") entry.ReposLockToken = value;
                                        else if (textField == "owner") entry.ReposLockOwner = value;
                                        else entry.ReposLockComment = (entry.ReposLockComment ?? string.Empty) + value;
                                    }
                                }
                                break;

                            case XmlNodeType.EndElement:
                                switch (reader.Name)
                                {
                                    case "token":
                                    case "owner":
                                    case "comment":
                                        textField = null;
                                        break;
                                    case "lock":
                                        inLock = false;
                                        break;
                                    case "wc-status":
                                    case "repos-status":
                                        block = null;
                                        break;
                                    case "entry":
                                        if (entry != null)
                                        {
                                            if (string.Equals(entry.WcLocked, "true", StringComparison.OrdinalIgnoreCase))
                                                wcLockedSeen = true;
                                            AddEntry(result, entry);
                                        }
                                        entry = null;
                                        break;
                                }
                                break;
                        }
                    }
                }
            }
            catch (XmlException ex)
            {
                // 截断点之前完整的 entry 已加入结果；未闭合的那一条丢弃
                result.IsIncomplete = true;
                result.Error = ex.Message;
            }

            if (wcLockedSeen)
                result.Warnings.Add("Parts of the working copy are locked by an interrupted svn operation (status column 'L'). Run version_control action 'cleanup' before other write operations.");

            return result;
        }

        /// <summary>
        /// wc-status 属性 → 文件状态。返回 null 表示无本地变更（normal / none / external 且属性未改）。
        /// </summary>
        public static VcsFileState? MapWorkingCopyState(string item, string props, string treeConflicted, out string description)
        {
            description = null;

            if (item == "conflicted")
            {
                description = "Conflicted";
                return VcsFileState.Conflicted;
            }
            if (string.Equals(treeConflicted, "true", StringComparison.OrdinalIgnoreCase))
            {
                description = "Conflicted (tree conflict)";
                return VcsFileState.Conflicted;
            }
            if (props == "conflicted")
            {
                description = "Conflicted (property conflict)";
                return VcsFileState.Conflicted;
            }

            switch (item)
            {
                case "modified":
                    description = props == "modified" ? "Modified (content + properties)" : "Modified";
                    return VcsFileState.Modified;
                case "replaced":
                    description = "Replaced";
                    return VcsFileState.Modified;
                case "added":
                    description = "Added";
                    return VcsFileState.Added;
                case "deleted":
                    description = "Deleted";
                    return VcsFileState.Deleted;
                case "unversioned":
                    description = "Unversioned";
                    return VcsFileState.Untracked;
                case "ignored":
                    description = "Ignored";
                    return VcsFileState.Ignored;
                case "missing":
                    description = "Missing";
                    return VcsFileState.Missing;
                case "obstructed":
                    description = "Obstructed (versioned item replaced by a different kind)";
                    return VcsFileState.Modified;
                case "incomplete":
                    description = "Incomplete (interrupted checkout/update)";
                    return VcsFileState.Modified;
            }

            // normal / none / external 等：只有属性改动时才算变更
            if (props == "modified")
            {
                description = "Properties modified";
                return VcsFileState.Modified;
            }

            return null;
        }

        /// <summary>
        /// 由本地锁 token 与远端锁 token 推导 svn status 第 6 列：
        /// K 本工作副本持有锁；O 他人持有锁；T 本地锁已被他人抢走；B 本地锁已失效（仅 -u 时能判定 T / B）。
        /// </summary>
        public static string MapLockState(string wcToken, string reposToken, bool hasReposStatus)
        {
            var hasWc = !string.IsNullOrEmpty(wcToken);
            var hasRepos = !string.IsNullOrEmpty(reposToken);
            if (hasWc && hasRepos)
                return string.Equals(wcToken, reposToken, StringComparison.Ordinal) ? "K" : "T";
            if (hasWc)
                return hasReposStatus ? "B" : "K";
            if (hasRepos)
                return "O";
            return null;
        }

        private static void AddEntry(SvnStatusParseResult result, EntryState e)
        {
            if (string.IsNullOrEmpty(e.Path))
                return;

            var lockState = MapLockState(e.WcLockToken, e.ReposLockToken, e.HasReposStatus);
            var lockOwner = lockState == "K" || lockState == "B" ? e.WcLockOwner ?? e.ReposLockOwner : e.ReposLockOwner ?? e.WcLockOwner;
            var lockComment = lockState == "K" || lockState == "B" ? e.WcLockComment ?? e.ReposLockComment : e.ReposLockComment ?? e.WcLockComment;
            var switched = string.Equals(e.Switched, "true", StringComparison.OrdinalIgnoreCase);

            var state = MapWorkingCopyState(e.Item, e.Props, e.TreeConflicted, out var description);
            VcsFileStatus status = null;
            if (state.HasValue && state.Value != VcsFileState.Ignored)
            {
                var finalState = state.Value;
                string originalPath = null;
                if (finalState == VcsFileState.Added && !string.IsNullOrEmpty(e.MovedFrom))
                {
                    // svn move 的目标端：带历史添加 + moved-from
                    finalState = VcsFileState.Renamed;
                    originalPath = e.MovedFrom;
                    description = $"Moved from {e.MovedFrom}";
                }
                else if (finalState == VcsFileState.Added && string.Equals(e.Copied, "true", StringComparison.OrdinalIgnoreCase))
                {
                    finalState = VcsFileState.Copied;
                    description = "Added with history (copied)";
                }
                else if (finalState == VcsFileState.Deleted && !string.IsNullOrEmpty(e.MovedTo))
                {
                    description = $"Deleted (moved to {e.MovedTo})";
                }

                status = new VcsFileStatus
                {
                    FilePath = e.Path,
                    State = finalState,
                    StateDescription = description,
                    StatusCode = e.Item,
                    OriginalPath = originalPath,
                    MovedToPath = e.MovedTo
                };
                result.Files.Add(status);
            }

            if (lockState != null || switched)
            {
                if (status == null)
                {
                    status = new VcsFileStatus
                    {
                        FilePath = e.Path,
                        State = VcsFileState.Unmodified,
                        StateDescription = "Unmodified",
                        StatusCode = e.Item
                    };
                }
                status.LockState = lockState;
                status.LockOwner = lockOwner;
                status.LockComment = lockComment;
                status.IsSwitched = switched;
                if (switched)
                {
                    status.StateDescription += " [switched]";
                    // 被 switch 到其他分支的子目录：提交 / 更新会作用到另一个 URL，提醒一次
                    result.Warnings.Add($"'{e.Path}' is switched to a different repository URL than its parent; commits and updates there go to that branch.");
                }
                if (lockState != null)
                {
                    status.StateDescription += $" [{DescribeLock(lockState, lockOwner)}]";
                    result.LockedFiles.Add(status);
                }
            }

            // repos-status item/props 非 none 即远端有更新
            if ((!string.IsNullOrEmpty(e.ReposItem) && e.ReposItem != "none")
                || (!string.IsNullOrEmpty(e.ReposProps) && e.ReposProps != "none"))
            {
                result.RemoteChangedFiles.Add(e.Path);
            }
        }

        /// <summary>
        /// 解析 <c>svn propget PROP --xml</c>（含 <c>-R</c>）：返回带该属性的 target 路径（原样，可能是相对或绝对路径）。
        /// 流式读取，XML 被截断时保留已读到的 target 并置 <paramref name="incomplete"/>。
        /// SvnAdapter（needs-lock 提醒）与 Project 窗口角标共用这一份解析。
        /// </summary>
        public static List<string> ParsePropgetTargets(string xml, out bool incomplete)
        {
            incomplete = false;
            var paths = new List<string>();
            if (string.IsNullOrWhiteSpace(xml))
                return paths;

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreWhitespace = true
            };
            try
            {
                using (var reader = XmlReader.Create(new StringReader(xml), settings))
                {
                    string currentTarget = null;
                    while (reader.Read())
                    {
                        if (reader.NodeType == XmlNodeType.Element)
                        {
                            if (reader.Name == "target")
                                currentTarget = reader.IsEmptyElement ? null : reader.GetAttribute("path");
                            else if (reader.Name == "property" && !string.IsNullOrEmpty(currentTarget))
                            {
                                paths.Add(currentTarget);
                                currentTarget = null; // 同一 target 只记一次
                            }
                        }
                        else if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "target")
                        {
                            currentTarget = null;
                        }
                    }
                }
            }
            catch (XmlException)
            {
                incomplete = true;
            }
            return paths;
        }

        /// <summary>锁状态的可读描述。</summary>
        public static string DescribeLock(string lockState, string owner)
        {
            var by = string.IsNullOrEmpty(owner) ? string.Empty : $" by {owner}";
            switch (lockState)
            {
                case "K": return $"locked in this working copy{by}";
                case "O": return $"locked by another user/working copy{by}";
                case "T": return $"lock stolen{by}";
                case "B": return "lock broken (no longer valid)";
                default: return lockState;
            }
        }
    }
}
