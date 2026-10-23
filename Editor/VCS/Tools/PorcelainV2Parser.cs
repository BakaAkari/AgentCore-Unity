using System;
using System.Collections.Generic;

namespace AgentCore.Editor.Components.VCS.Tools
{
    /// <summary>
    /// <c>git status --porcelain=v2 -z --branch</c> 的解析结果。
    /// </summary>
    public class GitPorcelainStatus
    {
        /// <summary>当前提交（<c># branch.oid</c>）；新仓库尚无提交时为 "(initial)"。</summary>
        public string BranchOid { get; set; }

        /// <summary>当前分支名（<c># branch.head</c>）；分离 HEAD 时为 "(detached)"。</summary>
        public string BranchHead { get; set; }

        /// <summary>上游分支（<c># branch.upstream</c>），未设置时为 null。</summary>
        public string Upstream { get; set; }

        /// <summary>是否有 <c># branch.ab</c> 行（上游存在且可比较）。</summary>
        public bool HasAheadBehind { get; set; }

        public int Ahead { get; set; }
        public int Behind { get; set; }

        /// <summary>所有文件记录（含忽略文件，调用方按需过滤）。</summary>
        public List<VcsFileStatus> Files { get; } = new List<VcsFileStatus>();

        /// <summary>输出末尾存在不完整记录（通常是输出被截断）。</summary>
        public bool HasIncompleteRecord { get; set; }
    }

    /// <summary>
    /// 解析 <c>git status --porcelain=v2 -z --branch</c> 输出。
    /// <para>
    /// 记录以 NUL 分隔，路径不做引号转义（非 ASCII / 空格 / 换行原样输出）：
    /// <list type="bullet">
    /// <item><c>1 XY sub mH mI mW hH hI path</c> 普通变更</item>
    /// <item><c>2 XY sub mH mI mW hH hI Xscore path</c> 重命名 / 复制，紧跟一条 NUL 分隔的 origPath</item>
    /// <item><c>u XY sub m1 m2 m3 mW h1 h2 h3 path</c> 未合并（冲突）</item>
    /// <item><c>? path</c> 未跟踪，<c>! path</c> 已忽略</item>
    /// <item><c># branch.*</c> 分支头信息；未知的 <c>#</c> 行忽略</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class PorcelainV2Parser
    {
        public static GitPorcelainStatus Parse(string output)
        {
            var result = new GitPorcelainStatus();
            if (string.IsNullOrEmpty(output))
                return result;

            var records = output.Split('\0');
            for (int i = 0; i < records.Length; i++)
            {
                var record = records[i];
                if (record.Length == 0)
                    continue;

                // 最后一段之后没有 NUL：可能是截断留下的半条记录，也可能是行模式读取追加的换行
                var isLast = i == records.Length - 1;
                if (isLast)
                {
                    if (record.Trim().Length == 0)
                        continue;
                    result.HasIncompleteRecord = true;
                    continue;
                }

                switch (record[0])
                {
                    case '#':
                        ParseHeader(record, result);
                        break;

                    case '1':
                        AddEntry(result, record, 8, null);
                        break;

                    case '2':
                        // 重命名 / 复制：下一条 NUL 记录是原路径
                        string origPath = null;
                        if (i + 1 < records.Length - 1)
                        {
                            origPath = records[i + 1];
                            i++;
                        }
                        else
                        {
                            result.HasIncompleteRecord = true;
                            i = records.Length;
                            break;
                        }
                        AddEntry(result, record, 9, origPath);
                        break;

                    case 'u':
                        AddEntry(result, record, 10, null);
                        break;

                    case '?':
                        AddSimple(result, record, VcsFileState.Untracked, "Untracked");
                        break;

                    case '!':
                        AddSimple(result, record, VcsFileState.Ignored, "Ignored");
                        break;

                    // 未知记录类型：忽略（向前兼容新版本 git）
                }
            }

            return result;
        }

        /// <summary>
        /// 判断 XY 是否代表冲突：任一侧为 U，或 DD（双方删除）/ AA（双方新增）。
        /// </summary>
        public static bool IsConflictXY(string xy)
        {
            if (string.IsNullOrEmpty(xy))
                return false;
            return xy.IndexOf('U') >= 0 || xy == "DD" || xy == "AA";
        }

        /// <summary>
        /// 由 XY 推导文件状态（. 表示该侧未修改）。
        /// </summary>
        public static VcsFileState StateFromXY(string xy)
        {
            if (IsConflictXY(xy))
                return VcsFileState.Conflicted;

            var x = xy.Length > 0 ? xy[0] : '.';
            var y = xy.Length > 1 ? xy[1] : '.';

            if (x == 'R' || y == 'R')
                return VcsFileState.Renamed;
            if (x == 'C' || y == 'C')
                return VcsFileState.Copied;
            if (x == 'A')
                return VcsFileState.Added;
            if (x == 'D' || y == 'D')
                return VcsFileState.Deleted;
            if (x == '.' && y == '.')
                return VcsFileState.Unmodified;
            return VcsFileState.Modified;
        }

        private static void ParseHeader(string record, GitPorcelainStatus result)
        {
            // "# branch.oid <commit>" / "# branch.head <name>" / "# branch.upstream <name>" / "# branch.ab +A -B"
            const string oid = "# branch.oid ";
            const string head = "# branch.head ";
            const string upstream = "# branch.upstream ";
            const string ab = "# branch.ab ";

            if (record.StartsWith(oid, StringComparison.Ordinal))
            {
                result.BranchOid = record.Substring(oid.Length);
            }
            else if (record.StartsWith(head, StringComparison.Ordinal))
            {
                result.BranchHead = record.Substring(head.Length);
            }
            else if (record.StartsWith(upstream, StringComparison.Ordinal))
            {
                result.Upstream = record.Substring(upstream.Length);
            }
            else if (record.StartsWith(ab, StringComparison.Ordinal))
            {
                var parts = record.Substring(ab.Length).Split(' ');
                if (parts.Length >= 2
                    && int.TryParse(parts[0].TrimStart('+'), out var ahead)
                    && int.TryParse(parts[1].TrimStart('-'), out var behind))
                {
                    result.Ahead = ahead;
                    result.Behind = behind;
                    result.HasAheadBehind = true;
                }
            }
            // 其他头（# stash 等）忽略
        }

        /// <summary>
        /// 解析带固定字段数的记录：前 <paramref name="fieldsBeforePath"/> 个以空格分隔的字段之后，余下全部是路径（路径本身可含空格）。
        /// </summary>
        private static void AddEntry(GitPorcelainStatus result, string record, int fieldsBeforePath, string origPath)
        {
            var parts = record.Split(new[] { ' ' }, fieldsBeforePath + 1);
            if (parts.Length < fieldsBeforePath + 1 || parts[1].Length != 2)
                return;

            var xy = parts[1];
            var path = parts[fieldsBeforePath];
            var isUnmerged = record[0] == 'u';
            var state = isUnmerged ? VcsFileState.Conflicted : StateFromXY(xy);

            result.Files.Add(new VcsFileStatus
            {
                FilePath = path,
                OriginalPath = origPath,
                State = state,
                StatusCode = xy,
                StateDescription = Describe(state, xy, origPath)
            });
        }

        private static void AddSimple(GitPorcelainStatus result, string record, VcsFileState state, string description)
        {
            if (record.Length < 3 || record[1] != ' ')
                return;

            result.Files.Add(new VcsFileStatus
            {
                FilePath = record.Substring(2),
                State = state,
                StatusCode = record[0] == '?' ? "??" : "!!",
                StateDescription = description
            });
        }

        private static string Describe(VcsFileState state, string xy, string origPath)
        {
            var name = state switch
            {
                VcsFileState.Modified => "Modified",
                VcsFileState.Added => "Added",
                VcsFileState.Deleted => "Deleted",
                VcsFileState.Renamed => "Renamed",
                VcsFileState.Copied => "Copied",
                VcsFileState.Conflicted => "Conflicted",
                _ => "Unmodified"
            };

            // XY 第一位是暂存区，第二位是工作区；附上原始码便于区分 staged / unstaged
            var text = $"{name} [{xy}]";
            if (!string.IsNullOrEmpty(origPath))
                text += $" from {origPath}";
            return text;
        }
    }
}
