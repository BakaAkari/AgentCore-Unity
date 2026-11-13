using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Components.VCS.Tools
{
    /// <summary>
    /// SVN 适配器
    /// 实现 Subversion 版本控制系统的所有查询和操作
    /// <para>
    /// 约定：
    /// <list type="bullet">
    /// <item>输出要解析的命令一律 <c>--xml</c>（UTF-8，不受系统代码页影响）；文本输出由 <see cref="VcsCommandExecutor"/> 逐行解码。</item>
    /// <item>多路径操作走 <c>--targets</c> 文件（一次调用、原子、不受 32K 命令行限制），提交信息走 <c>-F 文件 --encoding UTF-8</c>。</item>
    /// <item>Unity 的 .meta 与资源同进同出：add / delete / revert / commit 自动带上对应的 .meta（或资源），
    ///       新增目录时带上祖先目录的 .meta；move 的两端一起提交 / 还原。</item>
    /// <item>svn:needs-lock 文件被修改却没有持有锁时只报告（Warnings），不阻断。</item>
    /// <item>相对路径先按 svn 工作副本根解析，不存在时再按 Unity 项目根解析（工作副本根可能在项目根之上）。</item>
    /// </list>
    /// </para>
    /// </summary>
    public class SvnAdapter : IVcsAdapter
    {
        /// <summary>update / commit 可能传输大量二进制资源：超时放宽，避免中途被杀导致工作副本被锁。</summary>
        private const int LongOperationTimeoutSeconds = 900;

        private readonly string _workingDirectory;
        private readonly string _projectDirectory;

        public VcsType VcsType => VcsType.Svn;

        /// <summary>
        /// 为 false（默认）且 Unity 项目位于工作副本根之下时，status / sync status / diff / 整体 commit
        /// 只针对 Unity 项目目录（大工作副本里只扫描项目部分，避免整库扫描很慢，也不会顺带提交项目外的改动）。
        /// 为 true 时针对整个工作副本。update 始终更新整个工作副本（避免混合版本）。
        /// </summary>
        public bool WholeWorkingCopy { get; set; }

        /// <summary>
        /// 为 true（默认）时，status 会对"已修改但未持有锁"的文件额外运行一次 propget svn:needs-lock，
        /// 填充 <see cref="VcsFileStatus.NeedsLock"/> 并给出提醒。Project 窗口角标自己做整树 propget，关掉以免重复。
        /// </summary>
        public bool AnnotateNeedsLock { get; set; } = true;

        /// <summary>适配器的工作目录（工作副本根）：status / sync status 返回的相对路径以此为基准。</summary>
        public string WorkingDirectory => _workingDirectory;

        /// <summary>Unity 项目目录相对工作副本根的 svn 参数；项目即工作副本根（或不在其下）时为 null。</summary>
        private readonly string _projectScopeArg;

        private string ScopeArg => WholeWorkingCopy ? null : _projectScopeArg;

        public SvnAdapter(string workingDirectory)
        {
            _workingDirectory = workingDirectory;
            // Unity 编辑器的当前目录就是项目根；线程安全，不依赖 Application.dataPath
            try { _projectDirectory = Directory.GetCurrentDirectory(); }
            catch { _projectDirectory = workingDirectory; }
            _projectScopeArg = ComputeProjectScopeArg(workingDirectory, _projectDirectory);
        }

        /// <summary>
        /// 项目目录严格位于工作副本根之下时返回其相对路径（作为 svn 参数，status 输出的路径格式因此与整库扫描一致）；否则 null。
        /// </summary>
        public static string ComputeProjectScopeArg(string workingDirectory, string projectDirectory)
        {
            if (string.IsNullOrEmpty(workingDirectory) || string.IsNullOrEmpty(projectDirectory))
                return null;
            try
            {
                var root = Path.GetFullPath(workingDirectory).TrimEnd('\\', '/');
                var project = Path.GetFullPath(projectDirectory).TrimEnd('\\', '/');
                if (!IsUnder(project, root))
                    return null;
                var relative = project.Substring(root.Length + 1);
                return SvnCommandLine.CanPassToSvn(relative) ? SvnCommandLine.PathArg(relative) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private List<string> WithScope(List<string> args, string scope)
        {
            if (!string.IsNullOrEmpty(scope))
                args.Add(scope);
            return args;
        }

        public bool IsAvailable()
        {
            return VcsDetector.IsVcsCommandAvailable(VcsType.Svn);
        }

        // ===== Phase 1: 只读查询 =====

        public Task<VcsStatusResult> GetStatusAsync(CancellationToken ct = default)
        {
            return GetStatusAsync(ScopeArg, ct);
        }

        private async Task<VcsStatusResult> GetStatusAsync(string scope, CancellationToken ct)
        {
            var result = new VcsStatusResult();

            try
            {
                // 使用 XML 输出格式便于解析，保持与 SVN 客户端默认 status 输出一致
                var cmdResult = await VcsCommandExecutor.ExecuteAsync(
                    "svn", WithScope(new List<string> { "status", "--xml", "--non-interactive" }, scope), _workingDirectory, ct: ct);

                if (!cmdResult.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = cmdResult.ErrorMessage;
                    return result;
                }

                result.RawOutput = cmdResult.Output;
                var parsed = SvnStatusXmlParser.Parse(cmdResult.Output);
                result.Files = parsed.Files;
                result.LockedFiles = parsed.LockedFiles;
                result.Warnings.AddRange(parsed.Warnings);
                result.Success = true;
                // 输出超过上限被截断 / XML 不完整：保留已解析的条目，同时明确标记，不静默当作"没有变更"
                result.Truncated = cmdResult.OutputTruncated || parsed.IsIncomplete;
                if (result.Truncated)
                    AgentCoreLog.Warning($"[Version Control] SVN status output incomplete ({result.Files.Count} entries parsed): {parsed.Error ?? "output truncated"}");

                if (AnnotateNeedsLock)
                    await AnnotateNeedsLockAsync(result, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to get SVN status: {ex.Message}";
            }

            return result;
        }

        /// <summary>
        /// 对内容已修改、但本工作副本没有持有锁的文件检查 svn:needs-lock：
        /// 这类文件（场景 / 预制体等二进制资源）无法合并，别人同时修改时提交会冲突。只报告，不阻断。
        /// </summary>
        private async Task AnnotateNeedsLockAsync(VcsStatusResult result, CancellationToken ct)
        {
            var candidates = result.Files
                .Where(f => f.State == VcsFileState.Modified
                            && (f.StatusCode == "modified" || f.StatusCode == "replaced")
                            && f.LockState != "K")
                .ToList();
            if (candidates.Count == 0)
                return;

            var byAbs = new Dictionary<string, VcsFileStatus>();
            var args = new List<string>();
            foreach (var file in candidates)
            {
                var abs = ResolveAbsolutePath(file.FilePath, _workingDirectory);
                if (!SvnCommandLine.CanPassToSvn(abs))
                    continue;
                byAbs[NormalizePath(abs)] = file;
                args.Add(SvnCommandLine.PathArg(abs));
            }
            if (args.Count == 0)
                return;

            var prefix = new[] { "propget", "svn:needs-lock", "--xml", "--non-interactive" };
            foreach (var chunk in VcsArgs.ChunkPaths("svn", prefix, args))
            {
                var arguments = new List<string>(prefix);
                arguments.AddRange(chunk);
                // 没有该属性的目标会让 svn 以 W200017 + 非零退出码结束，但 XML 仍然完整输出，照常解析
                var cmd = await VcsCommandExecutor.ExecuteAsync("svn", arguments, _workingDirectory, ct: ct);
                foreach (var path in ParsePropgetTargets(cmd.Output))
                {
                    if (byAbs.TryGetValue(NormalizePath(ResolveAbsolutePath(path, _workingDirectory)), out var file))
                        file.NeedsLock = true;
                }
            }

            var unlocked = candidates.Where(f => f.NeedsLock).ToList();
            if (unlocked.Count == 0)
                return;

            foreach (var file in unlocked)
                file.StateDescription += " [needs-lock, not locked]";

            var listed = string.Join(", ", unlocked.Take(10).Select(f => f.FilePath));
            var more = unlocked.Count > 10 ? $" (+{unlocked.Count - 10} more)" : string.Empty;
            result.Warnings.Add(
                $"{unlocked.Count} file(s) marked svn:needs-lock were modified without holding the lock: {listed}{more}. " +
                "They are usually binary (scenes/prefabs) and cannot be merged; lock them (lock_files) or coordinate before committing.");
        }

        /// <summary>
        /// 解析 <c>svn propget --xml</c> 输出中带属性值的 target 路径（流式，XML 被截断时保留已读到的部分）。
        /// 与 Project 窗口角标共用 <see cref="SvnStatusXmlParser.ParsePropgetTargets(string, out bool)"/>。
        /// </summary>
        public static List<string> ParsePropgetTargets(string xml)
        {
            return SvnStatusXmlParser.ParsePropgetTargets(xml, out _);
        }

        public async Task<VcsBranchInfo> GetBranchInfoAsync(CancellationToken ct = default)
        {
            var result = new VcsBranchInfo();

            try
            {
                // 获取当前 URL
                var infoResult = await VcsCommandExecutor.ExecuteAsync(
                    "svn", new List<string> { "info", "--xml", "--non-interactive" }, _workingDirectory, ct: ct);

                if (!infoResult.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = infoResult.ErrorMessage;
                    return result;
                }

                ParseSvnInfo(infoResult.Output, result);
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to get SVN branch info: {ex.Message}";
            }

            return result;
        }

        public async Task<List<VcsCommit>> GetLogAsync(int maxCount = 20, CancellationToken ct = default)
        {
            var (commits, error) = await GetLogWithErrorAsync(maxCount, ct);
            if (error != null)
                AgentCoreLog.Warning($"Failed to get SVN log: {error}");
            return commits;
        }

        /// <summary>
        /// 获取提交历史；失败时返回错误信息（<see cref="GetLogAsync"/> 的接口只能返回空列表，
        /// 认证失败 / 网络不通会被误报成"没有提交"）。
        /// </summary>
        public async Task<(List<VcsCommit> commits, string error)> GetLogWithErrorAsync(int maxCount = 20, CancellationToken ct = default)
        {
            try
            {
                // 使用 -r HEAD:1 确保从远程服务器获取最新的 commit 列表
                // 不指定 revision 范围时，svn log 默认从 working copy 的 BASE revision 开始
                // 这会导致看不到比本地 BASE 更新的远程 commit
                var cmdResult = await VcsCommandExecutor.ExecuteAsync(
                    "svn", new List<string> { "log", "-r", "HEAD:1", "--limit", maxCount.ToString(), "--xml", "--non-interactive" }, _workingDirectory,
                    timeoutSeconds: 60, ct: ct);

                if (!cmdResult.Success)
                    return (new List<VcsCommit>(), cmdResult.ErrorMessage);

                return (ParseSvnLogXml(cmdResult.Output), null);
            }
            catch (Exception ex)
            {
                return (new List<VcsCommit>(), ex.Message);
            }
        }

        /// <summary>
        /// 单个提交的详情（svn log -v -r N ^/）：查询仓库根，提交只改了工作副本以外的路径也能查到。
        /// 接受 "123" / "r123" / "HEAD" 等 svn 修订号写法。
        /// </summary>
        public async Task<VcsCommitDetail> GetCommitInfoAsync(string revision, CancellationToken ct = default)
        {
            var result = new VcsCommitDetail();
            var rev = NormalizeRevision(revision);
            if (rev == null)
            {
                result.Success = false;
                result.ErrorMessage = $"Invalid SVN revision: '{revision}'. Use a number (e.g. 1234 or r1234), HEAD, BASE, COMMITTED or PREV.";
                return result;
            }

            try
            {
                var cmd = await VcsCommandExecutor.ExecuteAsync(
                    "svn", new List<string> { "log", "-v", "--xml", "--non-interactive", "-r", rev, "^/" },
                    _workingDirectory, timeoutSeconds: 60, ct: ct);
                result.RawOutput = cmd.Output;
                if (!cmd.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = SvnCommandLine.AppendErrorHint(cmd.ErrorMessage?.Trim());
                    return result;
                }

                var commit = ParseSvnLogXml(cmd.Output).FirstOrDefault();
                if (commit == null)
                {
                    result.Success = false;
                    result.ErrorMessage = $"Revision {rev} not found.";
                    return result;
                }

                result.Success = true;
                result.Revision = commit.Revision;
                result.Author = commit.Author;
                result.Date = commit.Date;
                result.Message = commit.Message;
                result.ChangedFiles = commit.ChangedFiles;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to get SVN commit info: {ex.Message}";
            }

            return result;
        }

        /// <summary>"r123" → "123"；数字与 svn 关键字原样返回（关键字转大写）；其它返回 null。</summary>
        internal static string NormalizeRevision(string revision)
        {
            var rev = revision?.Trim();
            if (string.IsNullOrEmpty(rev))
                return null;
            if ((rev[0] == 'r' || rev[0] == 'R') && rev.Length > 1 && rev.Skip(1).All(char.IsDigit))
                rev = rev.Substring(1);
            if (rev.All(char.IsDigit))
                return rev;
            var upper = rev.ToUpperInvariant();
            return upper == "HEAD" || upper == "BASE" || upper == "COMMITTED" || upper == "PREV" ? upper : null;
        }

        /// <summary>单个文件 / 目录的提交历史（文本输出，供模型阅读）。</summary>
        public async Task<CommandResult> GetPathLogAsync(string filePath, int maxCount, CancellationToken ct = default)
        {
            var abs = ResolvePath(filePath);
            if (!SvnCommandLine.CanPassToSvn(abs))
                return new CommandResult { Success = false, ExitCode = -1, ErrorMessage = SvnCommandLine.UnrepresentablePathError(filePath) };

            return await VcsCommandExecutor.ExecuteAsync(
                "svn", new List<string> { "log", "-l", maxCount.ToString(), "--non-interactive", SvnCommandLine.PathArg(abs) },
                _workingDirectory, timeoutSeconds: 60, ct: ct);
        }

        public async Task<string> GetDiffAsync(string filePath = null, CancellationToken ct = default)
        {
            try
            {
                // --internal-diff：用户在 ~/.subversion/config 里配了 diff-cmd（如 TortoiseMerge）时，
                // 不加此参数 svn diff 会启动外部 GUI 工具并一直挂起
                var args = new List<string> { "diff", "--internal-diff", "--non-interactive" };
                if (!string.IsNullOrEmpty(filePath))
                {
                    var abs = ResolvePath(filePath);
                    if (!SvnCommandLine.CanPassToSvn(abs))
                        return $"Error: {SvnCommandLine.UnrepresentablePathError(filePath)}";
                    // 相对工作副本根传入：diff 头部显示相对路径而不是很长的绝对路径
                    args.Add(SvnCommandLine.LocalDiffPathArg(ToDisplayPath(abs)));
                }
                else
                {
                    WithScope(args, ScopeArg);
                }
                var cmdResult = await VcsCommandExecutor.ExecuteAsync(
                    "svn", args, _workingDirectory, timeoutSeconds: 60, ct: ct);

                return cmdResult.Success ? cmdResult.Output : $"Error: {cmdResult.ErrorMessage}";
            }
            catch (Exception ex)
            {
                return $"Failed to get SVN diff: {ex.Message}";
            }
        }

        public async Task<VcsRemoteInfo> GetRemoteInfoAsync(CancellationToken ct = default)
        {
            var result = new VcsRemoteInfo();

            try
            {
                var cmdResult = await VcsCommandExecutor.ExecuteAsync(
                    "svn", new List<string> { "info", "--xml", "--non-interactive" }, _workingDirectory, ct: ct);

                if (!cmdResult.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = cmdResult.ErrorMessage;
                    return result;
                }

                result.RawOutput = cmdResult.Output;
                ParseSvnRemoteInfo(cmdResult.Output, result);
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to get SVN remote info: {ex.Message}";
            }

            return result;
        }

        public async Task<List<string>> GetTagsAsync(CancellationToken ct = default)
        {
            var tags = new List<string>();

            try
            {
                // SVN 的 tags 通常在 /tags 目录下
                var infoResult = await VcsCommandExecutor.ExecuteAsync(
                    "svn", new List<string> { "info", "--xml", "--non-interactive" }, _workingDirectory, ct: ct);

                if (!infoResult.Success)
                    return tags;

                var repoRoot = ExtractRepositoryRoot(infoResult.Output);
                if (string.IsNullOrEmpty(repoRoot))
                    return tags;

                var tagsUrl = $"{repoRoot}/tags";
                var listResult = await VcsCommandExecutor.ExecuteAsync(
                    "svn", new List<string> { "list", "--xml", "--non-interactive", tagsUrl }, _workingDirectory, timeoutSeconds: 60, ct: ct);

                if (listResult.Success)
                    tags = ParseSvnListNames(listResult.Output);
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"Failed to get SVN tags: {ex.Message}");
            }

            return tags;
        }

        public async Task<VcsBlameResult> GetBlameAsync(string filePath, CancellationToken ct = default)
        {
            var result = new VcsBlameResult { FilePath = filePath };

            try
            {
                if (string.IsNullOrEmpty(filePath))
                {
                    result.Success = false;
                    result.ErrorMessage = "file_path is required for get_blame action.";
                    return result;
                }

                var abs = ResolvePath(filePath);
                if (!SvnCommandLine.CanPassToSvn(abs))
                {
                    result.Success = false;
                    result.ErrorMessage = SvnCommandLine.UnrepresentablePathError(filePath);
                    return result;
                }

                var cmdResult = await VcsCommandExecutor.ExecuteAsync(
                    "svn", new List<string> { "blame", "--non-interactive", SvnCommandLine.PathArg(abs) }, _workingDirectory, timeoutSeconds: 60, ct: ct);

                if (!cmdResult.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = cmdResult.ErrorMessage;
                    return result;
                }

                // svn:mime-type 为二进制的文件：svn 只在 stderr 打印 "Skipping binary file" 并以 0 退出
                if (string.IsNullOrEmpty(cmdResult.Output) && (cmdResult.Error ?? string.Empty).IndexOf("Skipping binary file", StringComparison.Ordinal) >= 0)
                {
                    result.Success = false;
                    result.ErrorMessage = "SVN treats this file as binary (svn:mime-type), so blame is not available. " +
                                          "If it is actually text, remove or change its svn:mime-type property. " + cmdResult.Error.Trim();
                    return result;
                }

                result.RawOutput = cmdResult.Output;
                result.Lines = ParseSvnBlame(cmdResult.Output);
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to get SVN blame: {ex.Message}";
            }

            return result;
        }

        public async Task<VcsSyncStatus> GetSyncStatusAsync(CancellationToken ct = default)
        {
            var result = new VcsSyncStatus();

            try
            {
                var statusTask = GetStatusAsync(ct);
                // -u --xml：远端更新看 repos-status，避免解析文本列（旧实现会把版本号当成路径的一部分）
                var remoteTask = VcsCommandExecutor.ExecuteAsync(
                    "svn", WithScope(new List<string> { "status", "-u", "-q", "--xml", "--non-interactive" }, ScopeArg), _workingDirectory, timeoutSeconds: 120, ct: ct);

                await Task.WhenAll(statusTask, remoteTask);

                var localStatus = await statusTask;
                var remoteStatus = await remoteTask;

                if (!remoteStatus.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = remoteStatus.ErrorMessage;
                    result.RawOutput = string.Join("\n", new[] { remoteStatus.Output, remoteStatus.Error }
                        .Where(s => !string.IsNullOrWhiteSpace(s)));
                    return result;
                }

                result.Success = true;
                result.RawOutput = remoteStatus.Output;
                result.PathBase = _workingDirectory;
                result.HasLocalChanges = localStatus.Success && localStatus.Files.Count > 0;
                result.LocalChangeCount = localStatus.Success ? localStatus.Files.Count : 0;
                var remoteParsed = SvnStatusXmlParser.Parse(remoteStatus.Output);
                result.RemoteChangedFiles = remoteParsed.RemoteChangedFiles;
                result.RemoteChangeCount = result.RemoteChangedFiles.Count;
                result.HasRemoteChanges = result.RemoteChangeCount > 0;
                // -u 才能看到他人的锁（O）与本地失效的锁（T / B）
                result.LockedFiles = remoteParsed.LockedFiles;
                result.RemoteRevision = remoteParsed.AgainstRevision;
                result.HasConflicts = localStatus.Success && localStatus.Files.Any(f => f.State == VcsFileState.Conflicted);
                result.ConflictedFiles = localStatus.Success
                    ? localStatus.Files.Where(f => f.State == VcsFileState.Conflicted).Select(f => f.FilePath).ToList()
                    : new List<string>();
                result.Summary = result.HasRemoteChanges
                    ? $"Remote has {result.RemoteChangeCount} pending update(s)."
                    : "Working copy is up to date with remote.";
                var lockedByOthers = result.LockedFiles.Count(f => f.LockState == "O");
                if (lockedByOthers > 0)
                    result.Summary += $" {lockedByOthers} file(s) are locked by other users.";
                var lostLocks = result.LockedFiles.Count(f => f.LockState == "T" || f.LockState == "B");
                if (lostLocks > 0)
                    result.Summary += $" {lostLocks} of your lock(s) were stolen or broken.";
                if (remoteParsed.IsIncomplete || remoteStatus.OutputTruncated)
                    result.Summary += " (remote status output was incomplete; the list may be partial)";
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to check SVN remote status: {ex.Message}";
            }

            return result;
        }

        /// <summary>
        /// 获取 SVN 工作副本详细信息 (svn info --xml)
        /// </summary>
        public async Task<VcsSvnInfo> GetSvnInfoAsync(string target = null, CancellationToken ct = default)
        {
            var result = new VcsSvnInfo();

            try
            {
                var args = new List<string> { "info", "--xml", "--non-interactive" };
                if (!string.IsNullOrEmpty(target))
                {
                    var abs = ResolvePath(target);
                    if (!SvnCommandLine.CanPassToSvn(abs))
                    {
                        result.Success = false;
                        result.ErrorMessage = SvnCommandLine.UnrepresentablePathError(target);
                        return result;
                    }
                    args.Add(SvnCommandLine.PathArg(abs));
                }
                var cmdResult = await VcsCommandExecutor.ExecuteAsync(
                    "svn", args, _workingDirectory, ct: ct);

                if (!cmdResult.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = cmdResult.ErrorMessage;
                    return result;
                }

                result.RawOutput = cmdResult.Output;
                ParseSvnInfoDetailed(cmdResult.Output, result);
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to get SVN info: {ex.Message}";
            }

            return result;
        }

        // ===== Phase 2: 操作类 =====

        public async Task<VcsOperationResult> StageFilesAsync(List<string> filePaths, CancellationToken ct = default)
        {
            var result = new VcsOperationResult
            {
                OperationName = "SVN Stage Files"
            };

            try
            {
                if (filePaths == null || filePaths.Count == 0)
                {
                    result.Success = false;
                    result.ErrorMessage = "At least one file path is required.";
                    result.LogLines.Add(result.ErrorMessage);
                    return result;
                }

                var allOutput = new List<string>();
                var affectedFiles = new List<string>();

                // svn status 返回的路径（相对工作副本根）规范化为绝对路径建立索引，以便与传入的相对/绝对路径匹配
                var index = await LoadStatusIndexAsync(ct, filePaths);

                result.LogLines.Add($"Preparing {filePaths.Count} SVN path(s) for commit.");

                // 先分类，再按 add / delete 分批执行：大量文件时不再每个文件启动一个 svn 进程
                var toDelete = new List<PathItem>();
                var toAdd = new List<PathItem>();
                foreach (var item in ExpandForSchedule(filePaths, index, result.LogLines))
                {
                    if (!SvnCommandLine.CanPassToSvn(item.Abs))
                    {
                        result.FailedFiles[item.Display] = SvnCommandLine.UnrepresentablePathError(item.Display);
                        result.LogLines.Add($"Failed: {item.Display} - path not representable in the system code page");
                        continue;
                    }

                    var state = EffectiveState(index, item.Abs);
                    var shouldDelete = state == VcsFileState.Missing;
                    // 若路径在 status 中找不到（state == Unmodified），也尝试执行 svn add --force：
                    // 已纳入版本控制的文件是无操作，新建文件则被添加（可能尚未被 status 捕获）
                    // 显式点名的被忽略文件 svn add 也会添加（忽略规则只作用于递归）
                    var shouldAdd = state == VcsFileState.Untracked || state == VcsFileState.Unmodified || state == VcsFileState.Ignored;

                    if (!shouldDelete && !shouldAdd)
                    {
                        // 已纳入版本控制且有变更（Modified/Added/Conflicted 等），无需 add/delete
                        affectedFiles.Add(item.Display);
                        result.LogLines.Add($"No schedule command needed: {item.Display} ({state}).");
                        continue;
                    }

                    (shouldDelete ? toDelete : toAdd).Add(item);
                }

                await ScheduleBatchAsync(new[] { "delete", "--force", "--non-interactive" }, toDelete, "scheduled delete", result, allOutput, affectedFiles, ct);
                await ScheduleBatchAsync(new[] { "add", "--parents", "--force", "--non-interactive" }, toAdd, "scheduled add", result, allOutput, affectedFiles, ct);

                result.Success = affectedFiles.Count > 0;
                result.IsPartial = result.Success && result.FailedFiles.Count > 0;
                result.Message = $"Prepared {affectedFiles.Count} of {affectedFiles.Count + result.FailedFiles.Count} path(s) for SVN commit.";
                result.AffectedFiles = affectedFiles;
                result.RawOutput = string.Join("\n", allOutput);

                if (!result.Success)
                    result.ErrorMessage = result.FailedFiles.Count > 0
                        ? "No SVN files were prepared for commit: " + string.Join("; ", result.FailedFiles.Select(kv => $"{kv.Key}: {kv.Value}").Take(5))
                        : "No SVN files were prepared for commit. Check the operation log for command output.";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to prepare SVN files: {ex.Message}";
                result.LogLines.Add(result.ErrorMessage);
            }

            return result;
        }

        /// <summary>
        /// SVN 没有暂存区：unstage 只撤销"计划"（已 add / delete / 带历史复制或移动的条目），
        /// 内容修改（Modified / Replaced / Conflicted）保持不动——直接 revert 会丢掉用户的改动。
        /// 撤销 add 的文件留在磁盘上成为未版本控制文件。
        /// </summary>
        public async Task<VcsOperationResult> UnstageFilesAsync(List<string> filePaths, CancellationToken ct = default)
        {
            var result = new VcsOperationResult { OperationName = "SVN Unstage" };

            try
            {
                if (filePaths == null || filePaths.Count == 0)
                {
                    result.Success = false;
                    result.ErrorMessage = "At least one file path is required.";
                    return result;
                }

                var index = await LoadStatusIndexAsync(ct, filePaths);
                var targets = new List<PathItem>();
                foreach (var item in ExpandWithCompanions(filePaths, index, includeChangedCompanionsOnly: true, result.LogLines))
                {
                    var status = index.Get(item.Abs);
                    var state = status?.State ?? VcsFileState.Unmodified;
                    var scheduleOnly = state == VcsFileState.Added || state == VcsFileState.Copied
                                       || state == VcsFileState.Renamed
                                       || (state == VcsFileState.Deleted);
                    if (!scheduleOnly)
                    {
                        if (item.Requested)
                            result.SkippedFiles[item.Display] = state == VcsFileState.Untracked
                                ? "not under version control"
                                : $"{state}: SVN has no staging area; content changes are kept (use revert_files to discard them)";
                        continue;
                    }
                    if (!SvnCommandLine.CanPassToSvn(item.Abs))
                    {
                        result.FailedFiles[item.Display] = SvnCommandLine.UnrepresentablePathError(item.Display);
                        continue;
                    }
                    targets.Add(item);
                }

                if (targets.Count > 0)
                {
                    // 只含计划类条目：递归撤销不会丢内容（新增目录下的子项也都是新增）
                    var cmd = await RunWithTargetsAsync(new List<string> { "revert", "--depth", "infinity", "--non-interactive" }, targets, ct);
                    result.RawOutput = cmd.Output;
                    if (!cmd.Success)
                    {
                        result.Success = false;
                        result.ErrorMessage = cmd.ErrorMessage;
                        return result;
                    }
                }

                result.AffectedFiles = targets.Select(t => t.Display).ToList();
                result.Success = targets.Count > 0 || result.FailedFiles.Count == 0;
                result.IsPartial = result.Success && result.FailedFiles.Count > 0;
                result.Message = $"Unscheduled {targets.Count} path(s) (add/delete undone; files kept on disk)."
                                 + (result.SkippedFiles.Count > 0 ? $" {result.SkippedFiles.Count} path(s) left unchanged." : string.Empty);
                if (!result.Success)
                    result.ErrorMessage = string.Join("; ", result.FailedFiles.Select(kv => $"{kv.Key}: {kv.Value}").Take(5));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to unstage files: {ex.Message}";
            }

            return result;
        }

        public async Task<VcsOperationResult> CommitAsync(string message, CancellationToken ct = default)
        {
            var result = new VcsOperationResult
            {
                OperationName = "SVN Commit"
            };

            try
            {
                if (string.IsNullOrEmpty(message))
                {
                    result.Success = false;
                    result.ErrorMessage = "Commit message is required.";
                    result.LogLines.Add(result.ErrorMessage);
                    return result;
                }

                var statusResult = await GetStatusAsync(ct);
                result.Warnings.AddRange(statusResult.Warnings);
                var missingFiles = statusResult.Files
                    .Where(f => f.State == VcsFileState.Missing)
                    .Select(f => f.FilePath)
                    .ToList();

                if (missingFiles.Count > 0)
                {
                    result.LogLines.Add($"Detected {missingFiles.Count} missing SVN file(s). Scheduling delete before commit.");
                    var deleteResult = await StageFilesAsync(missingFiles, ct);
                    result.LogLines.AddRange(deleteResult.LogLines);
                    if (!string.IsNullOrWhiteSpace(deleteResult.RawOutput))
                        result.RawOutput = deleteResult.RawOutput;

                    if (!deleteResult.Success)
                    {
                        result.Success = false;
                        result.ErrorMessage = deleteResult.ErrorMessage ?? "Failed to schedule missing SVN files for deletion.";
                        result.Message = result.ErrorMessage;
                        return result;
                    }
                }

                var unversioned = statusResult.Files.Count(f => f.State == VcsFileState.Untracked);
                if (unversioned > 0)
                    result.Warnings.Add($"{unversioned} unversioned path(s) are not part of this commit (svn commit only sends versioned changes; use add_files or commit_files to include them).");

                // 整体提交与 status 的范围一致：Unity 项目在大工作副本中时只提交项目目录
                var scopeTargets = ScopeArg == null ? null : new List<string> { _projectDirectory };
                var cmdResult = await RunCommitAsync(message, scopeTargets, depthEmpty: false, result, ct);
                return FinishCommit(result, cmdResult, message);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to commit: {ex.Message}";
                result.LogLines.Add(result.ErrorMessage);
            }

            return result;
        }

        /// <summary>
        /// 提交指定路径（commit_files）。一次 svn commit 完成（--targets 文件，原子），并补齐 svn 要求或 Unity 需要的路径：
        /// <list type="bullet">
        /// <item>未纳入版本控制的路径先 svn add，缺失（!）的先 svn delete；</item>
        /// <item>对应的 .meta / 资源有变更时一并提交；move 的两端一并提交（svn 要求）；</item>
        /// <item>新增目录下的文件：其尚未提交的新增祖先目录一并提交（svn 要求，否则 E200009），
        ///       此时用 <c>--depth empty</c> 并把请求中的目录展开为其下的变更条目，避免把未请求的兄弟文件带进提交。</item>
        /// </list>
        /// </summary>
        public async Task<VcsOperationResult> CommitFilesAsync(string message, List<string> filePaths, CancellationToken ct = default)
        {
            var result = new VcsOperationResult { OperationName = "SVN Commit Files" };

            try
            {
                if (string.IsNullOrEmpty(message))
                {
                    result.Success = false;
                    result.ErrorMessage = "Commit message is required.";
                    return result;
                }
                if (filePaths == null || filePaths.Count == 0)
                {
                    result.Success = false;
                    result.ErrorMessage = "At least one file path is required.";
                    return result;
                }

                var bad = filePaths.Where(p => !SvnCommandLine.CanPassToSvn(ResolvePath(p))).ToList();
                if (bad.Count > 0)
                {
                    // 提交应是原子的：不悄悄提交其余文件
                    result.Success = false;
                    result.ErrorMessage = SvnCommandLine.UnrepresentablePathError(string.Join(", ", bad.Take(5)));
                    return result;
                }

                var index = await LoadStatusIndexAsync(ct, filePaths);
                result.Warnings.AddRange(index.Warnings);

                // 1) 未纳入版本控制 / 缺失的路径先调度 add / delete（含 .meta 配对与祖先目录 .meta）
                var needSchedule = ExpandForSchedule(filePaths, index, result.LogLines)
                    .Where(i => { var s = EffectiveState(index, i.Abs); return s == VcsFileState.Untracked || s == VcsFileState.Missing || (s == VcsFileState.Ignored && i.Requested); })
                    .Select(i => i.Abs)
                    .ToList();
                if (needSchedule.Count > 0)
                {
                    result.LogLines.Add($"Scheduling {needSchedule.Count} unversioned/missing path(s) before commit.");
                    var stage = await StageFilesAsync(needSchedule, ct);
                    result.LogLines.AddRange(stage.LogLines);
                    foreach (var kv in stage.FailedFiles)
                        result.FailedFiles[kv.Key] = kv.Value;
                    if (!stage.Success || stage.FailedFiles.Count > 0)
                    {
                        result.Success = false;
                        result.ErrorMessage = "Could not schedule all paths for commit; nothing was committed. "
                                              + (stage.ErrorMessage ?? string.Join("; ", stage.FailedFiles.Select(kv => $"{kv.Key}: {kv.Value}").Take(5)));
                        return result;
                    }
                    index = await LoadStatusIndexAsync(ct, filePaths);
                }

                // 2) 收集提交目标
                var targets = new OrderedPathSet();
                var requestedDirs = new List<string>();
                foreach (var item in ExpandWithCompanions(filePaths, index, includeChangedCompanionsOnly: true, result.LogLines))
                {
                    targets.Add(item.Abs, item.Display);
                    if (item.Requested && Directory.Exists(item.Abs))
                        requestedDirs.Add(item.Abs);
                }

                // move 的两端必须一起提交
                foreach (var abs in targets.Keys.ToList())
                {
                    var status = index.Get(abs);
                    var counterpart = status?.State == VcsFileState.Renamed ? status.OriginalPath : status?.MovedToPath;
                    if (string.IsNullOrEmpty(counterpart))
                        continue;
                    var counterAbs = ResolveAbsolutePath(counterpart, _workingDirectory);
                    if (targets.Add(counterAbs, ToDisplayPath(counterAbs)))
                        result.LogLines.Add($"Also committing the other side of the move: {ToDisplayPath(counterAbs)}");
                }

                // 新增祖先目录（及其 .meta）
                var addedAncestors = new List<string>();
                foreach (var abs in targets.Keys.ToList())
                {
                    foreach (var ancestor in GetAncestors(abs))
                    {
                        var state = index.GetState(ancestor);
                        if (state != VcsFileState.Added && state != VcsFileState.Copied && state != VcsFileState.Renamed)
                            continue;
                        if (targets.Add(ancestor, ToDisplayPath(ancestor)))
                        {
                            addedAncestors.Add(ancestor);
                            result.LogLines.Add($"Also committing added parent directory: {ToDisplayPath(ancestor)}");
                        }
                        var meta = ancestor + UnityMetaPaths.MetaExtension;
                        if (index.Get(meta) != null && targets.Add(meta, ToDisplayPath(meta)))
                            result.LogLines.Add($"Also committing .meta of added parent directory: {ToDisplayPath(meta)}");
                    }
                }

                // 带历史复制 / 移动来的目录不能用 depth empty 提交（会丢掉复制来的子项）
                var hasCopiedDir = targets.Keys.Any(abs =>
                {
                    var s = index.GetState(abs);
                    return (s == VcsFileState.Copied || s == VcsFileState.Renamed) && Directory.Exists(abs);
                });
                var depthEmpty = addedAncestors.Count > 0 && !hasCopiedDir;
                if (depthEmpty)
                {
                    // 请求中的目录展开为其下所有已纳入版本控制的变更条目
                    foreach (var dir in requestedDirs)
                    {
                        foreach (var status in index.All)
                        {
                            if (status.State == VcsFileState.Untracked || status.State == VcsFileState.Ignored)
                                continue;
                            var abs = ResolveAbsolutePath(status.FilePath, _workingDirectory);
                            if (IsUnder(abs, dir))
                                targets.Add(abs, ToDisplayPath(abs));
                        }
                    }
                    result.LogLines.Add("Using --depth empty so that only the listed paths (plus required parents) are committed.");
                }

                var cmdResult = await RunCommitAsync(message, targets.Keys.ToList(), depthEmpty, result, ct);
                result.AffectedFiles = targets.Displays.ToList();
                return FinishCommit(result, cmdResult, message);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to commit files: {ex.Message}";
            }

            return result;
        }

        private async Task<CommandResult> RunCommitAsync(string message, List<string> absTargets, bool depthEmpty, VcsOperationResult result, CancellationToken ct)
        {
            string messageFile = null;
            string targetsFile = null;
            try
            {
                // 提交信息经 UTF-8 文件传入：argv 按系统代码页解码会丢字符；消息恰好是某个路径时 -m 会报 E205005
                messageFile = SvnCommandLine.WriteMessageFile(message, _workingDirectory);
                var arguments = new List<string> { "commit", "--non-interactive" };
                arguments.AddRange(SvnCommandLine.MessageFileArgs(messageFile));
                if (depthEmpty)
                    arguments.AddRange(new[] { "--depth", "empty" });
                if (absTargets != null)
                {
                    targetsFile = SvnCommandLine.WriteTargetsFile(absTargets.Select(SvnCommandLine.PathArg), _workingDirectory);
                    arguments.Add("--targets");
                    arguments.Add(targetsFile);
                }

                result.CommandLine = $"svn {VcsArgs.Join(arguments)}";
                result.LogLines.Add(absTargets == null
                    ? $"Run: {result.CommandLine}"
                    : $"Run: svn commit ({absTargets.Count} target(s){(depthEmpty ? ", --depth empty" : string.Empty)})");

                return await VcsCommandExecutor.ExecuteAsync(
                    "svn", arguments, _workingDirectory, timeoutSeconds: LongOperationTimeoutSeconds, ct: ct);
            }
            finally
            {
                SvnCommandLine.TryDeleteFile(messageFile);
                SvnCommandLine.TryDeleteFile(targetsFile);
            }
        }

        private static readonly Regex CommittedRevisionRegex = new Regex(@"Committed revision (\d+)\.", RegexOptions.Compiled);

        private static VcsOperationResult FinishCommit(VcsOperationResult result, CommandResult cmdResult, string message)
        {
            var commandOutput = string.Join("\n", new[] { cmdResult.Output, cmdResult.Error }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            result.RawOutput = string.Join("\n", new[] { result.RawOutput, commandOutput }
                .Where(s => !string.IsNullOrWhiteSpace(s)));

            if (!cmdResult.Success)
            {
                result.Success = false;
                result.ErrorMessage = cmdResult.ErrorMessage;
                result.Message = string.IsNullOrWhiteSpace(cmdResult.ErrorMessage)
                    ? "SVN commit failed. Check Unity Console or SVN credentials."
                    : cmdResult.ErrorMessage.Trim();
                result.LogLines.Add($"Failed: {result.Message}");
                return result;
            }

            result.Success = true;
            var match = CommittedRevisionRegex.Match(cmdResult.Output ?? string.Empty);
            if (match.Success)
            {
                result.Message = $"Committed revision {match.Groups[1].Value} with message: {message}";
                result.LogLines.Add($"Success: SVN commit completed (r{match.Groups[1].Value}).");
            }
            else
            {
                // svn 没有可提交的内容时也以 0 退出、什么都不输出：如实告知，而不是声称已提交
                result.Message = "Nothing was committed: there are no versioned local changes in the given scope.";
                result.LogLines.Add("SVN commit finished without creating a revision (nothing to commit).");
            }
            return result;
        }

        /// <summary>
        /// 分批执行 svn add / delete：路径写入 --targets 文件一次执行；
        /// 失败时对该批逐个文件重试，保证一个坏路径不会连累同批其他文件，且每个失败都写入日志。
        /// </summary>
        private async Task ScheduleBatchAsync(
            IReadOnlyList<string> prefix,
            List<PathItem> items,
            string successLabel,
            VcsOperationResult result,
            List<string> allOutput,
            List<string> affectedFiles,
            CancellationToken ct)
        {
            if (items.Count == 0)
                return;

            var batch = await RunWithTargetsAsync(new List<string>(prefix), items, ct);
            RecordCommand(result, allOutput, batch, $"svn {prefix[0]} ({items.Count} path(s), --targets)");

            if (batch.Success)
            {
                foreach (var item in items)
                {
                    affectedFiles.Add(item.Display);
                    result.LogLines.Add($"Success: {item.Display} ({successLabel})");
                }
                return;
            }

            if (items.Count == 1)
            {
                result.FailedFiles[items[0].Display] = batch.ErrorMessage?.Trim();
                result.LogLines.Add($"Failed: {items[0].Display} - {batch.ErrorMessage}");
                return;
            }

            // 整批失败：逐个重试，定位具体失败的文件
            result.LogLines.Add($"Batch of {items.Count} path(s) failed, retrying one by one: {batch.ErrorMessage}");
            foreach (var item in items)
            {
                var single = new List<string>(prefix) { SvnCommandLine.PathArg(item.Abs) };
                var singleResult = await VcsCommandExecutor.ExecuteAsync("svn", single, _workingDirectory, ct: ct);
                RecordCommand(result, allOutput, singleResult, $"svn {VcsArgs.Join(single)}");
                if (singleResult.Success)
                {
                    affectedFiles.Add(item.Display);
                    result.LogLines.Add($"Success: {item.Display} ({successLabel})");
                }
                else
                {
                    result.FailedFiles[item.Display] = singleResult.ErrorMessage?.Trim();
                    result.LogLines.Add($"Failed: {item.Display} - {singleResult.ErrorMessage}");
                }
            }
        }

        private static void RecordCommand(VcsOperationResult result, List<string> allOutput, CommandResult cmdResult, string commandLine)
        {
            if (string.IsNullOrEmpty(result.CommandLine))
                result.CommandLine = commandLine;
            result.LogLines.Add($"Run: {commandLine}");

            var commandOutput = string.Join("\n", new[] { cmdResult.Output, cmdResult.Error }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            allOutput.Add(string.IsNullOrWhiteSpace(commandOutput) ? $"$ {commandLine}" : $"$ {commandLine}\n{commandOutput.Trim()}");
        }

        /// <summary>执行 <c>svn &lt;prefix&gt; --targets 文件</c>；路径须已通过 <see cref="SvnCommandLine.CanPassToSvn"/> 检查。</summary>
        private async Task<CommandResult> RunWithTargetsAsync(List<string> prefix, IEnumerable<PathItem> items, CancellationToken ct, int timeoutSeconds = 120)
        {
            string targetsFile = null;
            try
            {
                targetsFile = SvnCommandLine.WriteTargetsFile(items.Select(i => SvnCommandLine.PathArg(i.Abs)), _workingDirectory);
                var arguments = new List<string>(prefix) { "--targets", targetsFile };
                return await VcsCommandExecutor.ExecuteAsync("svn", arguments, _workingDirectory, timeoutSeconds, ct);
            }
            finally
            {
                SvnCommandLine.TryDeleteFile(targetsFile);
            }
        }

        public Task<VcsOperationResult> RevertFilesAsync(List<string> filePaths, CancellationToken ct = default)
        {
            return RevertFilesAsync(filePaths, recursive: false, ct);
        }

        /// <summary>
        /// 还原文件（svn revert）。默认不递归：目录只还原自身属性；<paramref name="recursive"/> 为 true 时 <c>--depth infinity</c>。
        /// 对应的 .meta / 资源有本地变更时一并还原；move 的两端一并还原（只还原一端会留下孤立的删除）。
        /// 未纳入版本控制的文件不受 svn revert 影响，原样留在磁盘上并在结果中注明（不删除用户文件）；
        /// 撤销 add 的文件同样留在磁盘上。
        /// </summary>
        public async Task<VcsOperationResult> RevertFilesAsync(List<string> filePaths, bool recursive, CancellationToken ct = default)
        {
            var result = new VcsOperationResult { OperationName = "SVN Revert" };

            try
            {
                if (filePaths == null || filePaths.Count == 0)
                {
                    result.Success = false;
                    result.ErrorMessage = "At least one file path is required for revert.";
                    return result;
                }

                var index = await LoadStatusIndexAsync(ct, filePaths);
                var targets = new OrderedPathSet();
                var keptOnDisk = 0;
                foreach (var item in ExpandWithCompanions(filePaths, index, includeChangedCompanionsOnly: true, result.LogLines))
                {
                    var status = index.Get(item.Abs);
                    if (EffectiveState(index, item.Abs) == VcsFileState.Untracked)
                    {
                        if (item.Requested)
                            result.SkippedFiles[item.Display] = "unversioned: svn revert does not touch it (left on disk)";
                        continue;
                    }
                    if (!SvnCommandLine.CanPassToSvn(item.Abs))
                    {
                        result.FailedFiles[item.Display] = SvnCommandLine.UnrepresentablePathError(item.Display);
                        continue;
                    }
                    if (status != null && (status.State == VcsFileState.Added || status.State == VcsFileState.Copied || status.State == VcsFileState.Renamed))
                        keptOnDisk++;
                    targets.Add(item.Abs, item.Display);

                    var counterpart = status?.State == VcsFileState.Renamed ? status.OriginalPath : status?.MovedToPath;
                    if (!string.IsNullOrEmpty(counterpart))
                    {
                        var counterAbs = ResolveAbsolutePath(counterpart, _workingDirectory);
                        if (targets.Add(counterAbs, ToDisplayPath(counterAbs)))
                            result.LogLines.Add($"Also reverting the other side of the move: {ToDisplayPath(counterAbs)}");
                    }
                }

                if (targets.Count > 0)
                {
                    var prefix = new List<string> { "revert", "--non-interactive" };
                    if (recursive)
                        prefix.AddRange(new[] { "--depth", "infinity" });
                    var items = targets.Keys.Select(k => new PathItem(k, targets.DisplayOf(k), true)).ToList();
                    var cmdResult = await RunWithTargetsAsync(prefix, items, ct);
                    result.CommandLine = $"svn {string.Join(" ", prefix)} --targets <{items.Count} path(s)>";
                    result.RawOutput = cmdResult.Output;

                    if (!cmdResult.Success)
                    {
                        result.Success = false;
                        result.ErrorMessage = cmdResult.ErrorMessage;
                        return result;
                    }
                }

                var revertedCount = CountLines(result.RawOutput, "Reverted ");
                result.Success = targets.Count > 0 || result.FailedFiles.Count == 0;
                result.IsPartial = result.Success && result.FailedFiles.Count > 0;
                result.AffectedFiles = targets.Displays.ToList();
                result.Message = $"Reverted {revertedCount} item(s) ({targets.Count} target path(s){(recursive ? ", recursive" : string.Empty)}).";
                if (keptOnDisk > 0)
                    result.Message += $" {keptOnDisk} added item(s) were unscheduled and left on disk as unversioned files.";
                if (result.SkippedFiles.Count > 0)
                    result.Message += $" {result.SkippedFiles.Count} unversioned path(s) were left untouched.";
                if (!result.Success)
                    result.ErrorMessage = string.Join("; ", result.FailedFiles.Select(kv => $"{kv.Key}: {kv.Value}").Take(5));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to revert files: {ex.Message}";
            }

            return result;
        }

        private static readonly Regex UpdatedRevisionRegex = new Regex(@"(?:Updated to|At) revision (\d+)\.", RegexOptions.Compiled);

        /// <summary>
        /// svn update。冲突以更新后的 <c>svn status --xml</c> 为准（文本、属性、树冲突都能拿到，且不受输出编码影响）：
        /// 有冲突 → 成功但 <see cref="VcsOperationResult.IsPartial"/>；svn 非零退出但已经更新了部分内容（如 externals 失败）
        /// → 失败且 IsPartial；完全没更新 → 失败。
        /// </summary>
        public async Task<VcsOperationResult> SyncAsync(CancellationToken ct = default)
        {
            var result = new VcsOperationResult { OperationName = "SVN Update" };

            try
            {
                var cmdResult = await VcsCommandExecutor.ExecuteAsync(
                    "svn", new List<string> { "update", "--accept", "postpone", "--non-interactive" }, _workingDirectory,
                    timeoutSeconds: LongOperationTimeoutSeconds, ct: ct);

                result.RawOutput = string.Join("\n", new[] { cmdResult.Output, cmdResult.Error }.Where(s => !string.IsNullOrWhiteSpace(s)));
                var revisions = UpdatedRevisionRegex.Matches(cmdResult.Output ?? string.Empty);
                var revision = revisions.Count > 0 ? revisions[0].Groups[1].Value : null;

                // 冲突列表：以 status 为准；status 失败时退回解析 update 文本输出
                var status = await GetStatusAsync(ct);
                // status 只覆盖项目目录（见 WholeWorkingCopy）而 update 作用于整个工作副本：再合并 update 输出里的冲突行
                result.ConflictedFiles = status.Success
                    ? status.Files.Where(f => f.State == VcsFileState.Conflicted).Select(f => f.FilePath).ToList()
                    : new List<string>();
                var known = new HashSet<string>(result.ConflictedFiles.Select(f => NormalizePath(ResolveAbsolutePath(f, _workingDirectory))));
                foreach (var path in ParseSvnUpdateConflicts(cmdResult.Output))
                {
                    if (known.Add(NormalizePath(ResolveAbsolutePath(path, _workingDirectory))))
                        result.ConflictedFiles.Add(path);
                }
                if (status.Success)
                    result.Warnings.AddRange(status.Warnings);

                foreach (var line in SplitLines(cmdResult.Output))
                {
                    if (line.StartsWith("Skipped ", StringComparison.Ordinal))
                        result.Warnings.Add(line.Trim());
                }

                if (!cmdResult.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = cmdResult.ErrorMessage;
                    // 已有 "Updated to / At revision" 说明主体更新已完成，只是部分失败（externals、被占用的文件等）
                    result.IsPartial = revision != null;
                    result.Message = result.IsPartial
                        ? $"Update reached revision {revision} but reported errors: {cmdResult.ErrorMessage?.Trim()}"
                        : (string.IsNullOrEmpty(cmdResult.ErrorMessage) ? "Update failed." : cmdResult.ErrorMessage.Trim());
                    return result;
                }

                result.Success = true;
                var revisionText = revision != null ? $" to revision {revision}" : string.Empty;
                if (result.ConflictedFiles.Count > 0)
                {
                    result.IsPartial = true;
                    result.Message = $"Updated{revisionText} with {result.ConflictedFiles.Count} conflict(s). Resolve conflicts before continuing.";
                }
                else if (result.Warnings.Any(w => w.StartsWith("Skipped ", StringComparison.Ordinal)))
                {
                    result.IsPartial = true;
                    result.Message = $"Updated{revisionText}, but some paths were skipped (see warnings).";
                }
                else
                {
                    result.Message = $"Updated{revisionText} successfully.";
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to update: {ex.Message}";
                result.Message = result.ErrorMessage;
            }

            return result;
        }

        /// <summary>标记冲突已解决（svn resolve --accept working），--targets 一次执行。</summary>
        public async Task<VcsOperationResult> ResolveFilesAsync(List<string> filePaths, CancellationToken ct = default)
        {
            return await RunSimplePathOperationAsync(
                "SVN Resolve", new List<string> { "resolve", "--accept", "working", "--non-interactive" },
                filePaths, includeCompanions: false, n => $"Marked {n} path(s) resolved.", ct);
        }

        /// <summary>
        /// svn delete --force（remove_files）：对应的 .meta / 资源一并删除；未纳入版本控制的文件也会从磁盘删除。
        /// </summary>
        public async Task<VcsOperationResult> DeleteFilesAsync(List<string> filePaths, CancellationToken ct = default)
        {
            return await RunSimplePathOperationAsync(
                "SVN Delete", new List<string> { "delete", "--force", "--non-interactive" },
                filePaths, includeCompanions: true, n => $"Removed/scheduled delete for {n} path(s).", ct);
        }

        /// <summary>
        /// 锁定文件（svn lock）。被他人锁定的文件不会被抢锁（不加 --force），逐个报告结果。
        /// </summary>
        public async Task<VcsOperationResult> LockFilesAsync(List<string> filePaths, string comment = null, CancellationToken ct = default)
        {
            return await RunLockOperationAsync(true, filePaths, comment, ct);
        }

        /// <summary>释放本工作副本持有的锁（svn unlock，不加 --force，不会打破他人的锁）。</summary>
        public async Task<VcsOperationResult> UnlockFilesAsync(List<string> filePaths, CancellationToken ct = default)
        {
            return await RunLockOperationAsync(false, filePaths, null, ct);
        }

        /// <summary>
        /// 移动 / 重命名（svn move --parents）。资源的 .meta 一起移动，保持 GUID 不变、引用不断。
        /// 未纳入版本控制的一端（资源或 .meta）改为普通文件系统移动，并在资源已受控时把移动后的 .meta 加入版本控制。
        /// 目标的相对路径与源使用同一个基准（工作副本根或 Unity 项目根）。
        /// </summary>
        public async Task<VcsOperationResult> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct = default)
        {
            var result = new VcsOperationResult { OperationName = "SVN Move" };
            if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destinationPath))
            {
                result.Success = false;
                result.ErrorMessage = "Both source and destination paths are required.";
                return result;
            }

            try
            {
                // 传入的是 .meta 时按资源处理：.meta 总是跟随资源
                if (UnityMetaPaths.IsMeta(sourcePath) && UnityMetaPaths.IsMeta(destinationPath))
                {
                    sourcePath = UnityMetaPaths.GetCompanion(sourcePath);
                    destinationPath = UnityMetaPaths.GetCompanion(destinationPath);
                }

                var srcAbs = ResolvePath(sourcePath.Trim());
                var dstAbs = ResolveDestinationPath(sourcePath.Trim(), srcAbs, destinationPath.Trim());
                var srcDisplay = ToDisplayPath(srcAbs);
                var dstDisplay = ToDisplayPath(dstAbs);

                if (!SvnCommandLine.CanPassToSvn(srcAbs) || !SvnCommandLine.CanPassToSvn(dstAbs))
                {
                    result.Success = false;
                    result.ErrorMessage = SvnCommandLine.UnrepresentablePathError(SvnCommandLine.CanPassToSvn(srcAbs) ? destinationPath : sourcePath);
                    return result;
                }
                if (!File.Exists(srcAbs) && !Directory.Exists(srcAbs))
                {
                    result.Success = false;
                    result.ErrorMessage = $"Source not found: {srcDisplay}";
                    return result;
                }
                if (File.Exists(dstAbs) || Directory.Exists(dstAbs))
                {
                    result.Success = false;
                    result.ErrorMessage = $"Destination already exists: {dstDisplay}";
                    return result;
                }

                var outputs = new List<string>();
                var main = await MoveOneAsync(srcAbs, dstAbs, ct);
                outputs.Add(main.output);
                if (!main.ok)
                {
                    result.Success = false;
                    result.ErrorMessage = main.error;
                    result.RawOutput = main.output;
                    return result;
                }
                result.AffectedFiles.Add(dstDisplay);
                result.LogLines.Add(main.versioned ? $"svn move {srcDisplay} -> {dstDisplay}" : $"moved unversioned {srcDisplay} -> {dstDisplay} (file system)");

                var srcMeta = srcAbs.TrimEnd('/', '\\') + UnityMetaPaths.MetaExtension;
                var dstMeta = dstAbs.TrimEnd('/', '\\') + UnityMetaPaths.MetaExtension;
                if (File.Exists(srcMeta))
                {
                    if (File.Exists(dstMeta))
                    {
                        result.FailedFiles[ToDisplayPath(srcMeta)] = $"Destination .meta already exists: {ToDisplayPath(dstMeta)}";
                    }
                    else
                    {
                        var meta = await MoveOneAsync(srcMeta, dstMeta, ct);
                        outputs.Add(meta.output);
                        if (!meta.ok)
                        {
                            result.FailedFiles[ToDisplayPath(srcMeta)] = meta.error;
                        }
                        else
                        {
                            result.AffectedFiles.Add(ToDisplayPath(dstMeta));
                            if (!meta.versioned && main.versioned)
                            {
                                // 资源受控而 .meta 不受控：移动后把 .meta 加入版本控制，避免提交出没有 .meta 的资源
                                var add = await VcsCommandExecutor.ExecuteAsync(
                                    "svn", new List<string> { "add", "--parents", "--force", "--non-interactive", SvnCommandLine.PathArg(dstMeta) },
                                    _workingDirectory, ct: ct);
                                outputs.Add(add.Output);
                                if (!add.Success)
                                    result.FailedFiles[ToDisplayPath(dstMeta)] = $"Moved, but svn add failed: {add.ErrorMessage?.Trim()}";
                            }
                        }
                    }
                }

                result.RawOutput = string.Join("\n", outputs.Where(o => !string.IsNullOrWhiteSpace(o)));
                result.Success = true;
                result.IsPartial = result.FailedFiles.Count > 0;
                result.Message = $"Moved {srcDisplay} -> {dstDisplay}" + (main.versioned ? " (scheduled in SVN; commit to record the move)." : " (unversioned, file system only).");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to move: {ex.Message}";
            }

            return result;
        }

        /// <summary>
        /// 移动单个路径：先 svn move；源不受版本控制时退回文件系统移动。返回是否走了 svn。
        /// </summary>
        private async Task<(bool ok, bool versioned, string error, string output)> MoveOneAsync(string srcAbs, string dstAbs, CancellationToken ct)
        {
            var cmd = await VcsCommandExecutor.ExecuteAsync(
                "svn", new List<string> { "move", "--parents", "--non-interactive", SvnCommandLine.PathArg(srcAbs), SvnCommandLine.PathArg(dstAbs) },
                _workingDirectory, ct: ct);
            if (cmd.Success)
                return (true, true, null, cmd.Output);

            var error = (cmd.ErrorMessage ?? string.Empty).Trim();
            if (!IsNotVersionedError(error))
                return (false, true, error, cmd.Output);

            try
            {
                var parent = Path.GetDirectoryName(dstAbs);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);
                if (Directory.Exists(srcAbs))
                    Directory.Move(srcAbs, dstAbs);
                else
                    File.Move(srcAbs, dstAbs);
                return (true, false, null, cmd.Output);
            }
            catch (Exception ex)
            {
                return (false, false, $"File system move failed: {ex.Message}", cmd.Output);
            }
        }

        /// <summary>svn 报告源不在版本控制下（E155010 / W155010 / E200005 / E200009）。</summary>
        internal static bool IsNotVersionedError(string error)
        {
            if (string.IsNullOrEmpty(error))
                return false;
            return error.Contains("E155010") || error.Contains("W155010") || error.Contains("E200005") || error.Contains("E200009")
                   || error.IndexOf("not under version control", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>目标相对路径与源采用同一个基准：源按 Unity 项目根解析出来的，目标也按项目根解析。</summary>
        private string ResolveDestinationPath(string sourceInput, string sourceAbs, string destinationInput)
        {
            if (Path.IsPathRooted(destinationInput))
                return destinationInput;
            if (!Path.IsPathRooted(sourceInput) && !string.IsNullOrEmpty(_projectDirectory)
                && !string.Equals(NormalizePath(_projectDirectory), NormalizePath(_workingDirectory), StringComparison.Ordinal)
                && string.Equals(NormalizePath(sourceAbs), NormalizePath(ResolveAbsolutePath(sourceInput, _projectDirectory)), StringComparison.Ordinal))
                return ResolveAbsolutePath(destinationInput, _projectDirectory);
            return ResolveAbsolutePath(destinationInput, _workingDirectory);
        }

        private async Task<VcsOperationResult> RunLockOperationAsync(bool doLock, List<string> filePaths, string comment, CancellationToken ct)
        {
            var result = new VcsOperationResult { OperationName = doLock ? "SVN Lock" : "SVN Unlock" };
            if (filePaths == null || filePaths.Count == 0)
            {
                result.Success = false;
                result.ErrorMessage = "At least one file path is required.";
                return result;
            }

            string messageFile = null;
            try
            {
                var items = new List<PathItem>();
                foreach (var path in filePaths)
                {
                    var abs = ResolvePath(path);
                    if (!SvnCommandLine.CanPassToSvn(abs))
                        result.FailedFiles[path] = SvnCommandLine.UnrepresentablePathError(path);
                    else if (!File.Exists(abs))
                        result.FailedFiles[path] = "File not found (only existing files can be locked/unlocked).";
                    else
                        items.Add(new PathItem(abs, path, true));
                }

                var prefix = new List<string> { doLock ? "lock" : "unlock", "--non-interactive" };
                if (doLock && !string.IsNullOrEmpty(comment))
                {
                    messageFile = SvnCommandLine.WriteMessageFile(comment, _workingDirectory);
                    prefix.AddRange(SvnCommandLine.MessageFileArgs(messageFile));
                }

                var outputs = new List<string>();
                if (items.Count > 0)
                {
                    var batch = await RunWithTargetsAsync(prefix, items, ct, timeoutSeconds: 120);
                    outputs.Add(batch.Output);
                    var errors = new Dictionary<PathItem, string>();
                    if (!batch.Success)
                    {
                        // 一个目标出错 svn 可能整批放弃：逐个重试，得到每个文件各自的结果
                        foreach (var item in items)
                        {
                            var single = await RunWithTargetsAsync(prefix, new[] { item }, ct, timeoutSeconds: 120);
                            outputs.Add(single.Output);
                            if (!single.Success)
                                errors[item] = single.ErrorMessage?.Trim();
                        }
                    }

                    // 以 status 中的 K 锁为准核对结果（svn lock 的输出只有文件名，不含路径）
                    var index = await LoadStatusIndexAsync(ct, filePaths);
                    foreach (var item in items)
                    {
                        var heldHere = index.Get(item.Abs)?.LockState == "K";
                        if (heldHere == doLock)
                            result.AffectedFiles.Add(item.Display);
                        else
                            result.FailedFiles[item.Display] = errors.TryGetValue(item, out var err) && !string.IsNullOrEmpty(err)
                                ? err
                                : (batch.Success ? "svn reported success but the lock state did not change." : batch.ErrorMessage?.Trim());
                    }
                }

                result.RawOutput = string.Join("\n", outputs.Where(o => !string.IsNullOrWhiteSpace(o)));
                result.Success = result.AffectedFiles.Count > 0;
                result.IsPartial = result.Success && result.FailedFiles.Count > 0;
                result.Message = doLock
                    ? $"Locked {result.AffectedFiles.Count} of {filePaths.Count} file(s)."
                    : $"Unlocked {result.AffectedFiles.Count} of {filePaths.Count} file(s).";
                if (!result.Success)
                    result.ErrorMessage = string.Join("; ", result.FailedFiles.Select(kv => $"{kv.Key}: {kv.Value}").Take(5));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to {(doLock ? "lock" : "unlock")} files: {ex.Message}";
            }
            finally
            {
                SvnCommandLine.TryDeleteFile(messageFile);
            }

            return result;
        }

        private async Task<VcsOperationResult> RunSimplePathOperationAsync(
            string operationName, List<string> prefix, List<string> filePaths, bool includeCompanions,
            Func<int, string> successMessage, CancellationToken ct)
        {
            var result = new VcsOperationResult { OperationName = operationName };
            if (filePaths == null || filePaths.Count == 0)
            {
                result.Success = false;
                result.ErrorMessage = "At least one file path is required.";
                return result;
            }

            try
            {
                var index = includeCompanions ? await LoadStatusIndexAsync(ct, filePaths) : StatusIndex.Empty;
                IEnumerable<PathItem> expanded = includeCompanions
                    ? ExpandWithCompanions(filePaths, index, includeChangedCompanionsOnly: false, result.LogLines)
                    : filePaths.Select(p => new PathItem(ResolvePath(p), p, true));

                var items = new List<PathItem>();
                foreach (var item in expanded)
                {
                    if (!SvnCommandLine.CanPassToSvn(item.Abs))
                        result.FailedFiles[item.Display] = SvnCommandLine.UnrepresentablePathError(item.Display);
                    else
                        items.Add(item);
                }

                var allOutput = new List<string>();
                var affected = new List<string>();
                await ScheduleBatchAsync(prefix, items, operationName, result, allOutput, affected, ct);

                result.AffectedFiles = affected;
                result.RawOutput = string.Join("\n", allOutput);
                result.Success = affected.Count > 0;
                result.IsPartial = result.Success && result.FailedFiles.Count > 0;
                result.Message = successMessage(affected.Count);
                if (!result.Success)
                    result.ErrorMessage = string.Join("; ", result.FailedFiles.Select(kv => $"{kv.Key}: {kv.Value}").Take(5));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"{operationName} failed: {ex.Message}";
            }

            return result;
        }

        // ===== 路径展开（.meta 配对 / 祖先目录） =====

        /// <summary>一个待处理路径：绝对路径 + 展示给用户的路径 + 是否为用户直接请求（否则是自动补齐的）。</summary>
        private sealed class PathItem
        {
            public readonly string Abs;
            public readonly string Display;
            public readonly bool Requested;

            public PathItem(string abs, string display, bool requested)
            {
                Abs = abs;
                Display = display;
                Requested = requested;
            }
        }

        /// <summary>按规范化绝对路径去重、保持插入顺序的路径集合。</summary>
        private sealed class OrderedPathSet
        {
            private readonly List<string> _keys = new List<string>();
            private readonly Dictionary<string, (string abs, string display)> _map = new Dictionary<string, (string, string)>();

            public int Count => _keys.Count;
            public IEnumerable<string> Keys => _keys.Select(k => _map[k].abs);
            public IEnumerable<string> Displays => _keys.Select(k => _map[k].display);

            public bool Add(string abs, string display)
            {
                var key = NormalizePath(abs);
                if (_map.ContainsKey(key))
                    return false;
                _map[key] = (abs, display);
                _keys.Add(key);
                return true;
            }

            public string DisplayOf(string abs) => _map.TryGetValue(NormalizePath(abs), out var v) ? v.display : abs;
        }

        /// <summary>status 结果按规范化绝对路径索引。</summary>
        private sealed class StatusIndex
        {
            public static readonly StatusIndex Empty = new StatusIndex();

            private readonly Dictionary<string, VcsFileStatus> _byAbs = new Dictionary<string, VcsFileStatus>();
            public List<VcsFileStatus> All { get; } = new List<VcsFileStatus>();
            public List<string> Warnings { get; } = new List<string>();

            public void Add(string abs, VcsFileStatus status)
            {
                var key = NormalizePath(abs);
                if (!_byAbs.ContainsKey(key))
                {
                    _byAbs[key] = status;
                    All.Add(status);
                }
            }

            public VcsFileStatus Get(string abs) =>
                abs != null && _byAbs.TryGetValue(NormalizePath(abs), out var s) ? s : null;

            public VcsFileState GetState(string abs) => Get(abs)?.State ?? VcsFileState.Unmodified;
        }

        private async Task<StatusIndex> LoadStatusIndexAsync(CancellationToken ct, IEnumerable<string> paths = null)
        {
            var index = new StatusIndex();
            // 请求的路径都在项目目录内时只扫描项目目录；否则扫描整个工作副本
            var scope = ScopeArg;
            if (scope != null && paths != null && paths.Any(p => !string.IsNullOrEmpty(p) && !IsUnder(ResolvePath(p), _projectDirectory)))
                scope = null;
            var status = await GetStatusAsync(scope, ct);
            if (!status.Success)
            {
                // status 失败（如工作副本被锁）时退化为不做配对补齐，后续 svn 命令会给出真正的错误
                index.Warnings.Add($"svn status failed, .meta pairing was skipped: {status.ErrorMessage}");
                return index;
            }
            index.Warnings.AddRange(status.Warnings);
            foreach (var file in status.Files)
                index.Add(ResolveAbsolutePath(file.FilePath, _workingDirectory), file);
            foreach (var file in status.LockedFiles)
                index.Add(ResolveAbsolutePath(file.FilePath, _workingDirectory), file);
            return index;
        }

        /// <summary>
        /// 展开请求路径：每个路径之后紧跟其 .meta 配对（或 .meta 对应的资源）。
        /// <paramref name="includeChangedCompanionsOnly"/> 为 true 时只带上 status 中有变更的配对路径；
        /// 为 false 时（删除）只要配对路径在磁盘上存在或在 status 中就带上。
        /// </summary>
        private List<PathItem> ExpandWithCompanions(IEnumerable<string> filePaths, StatusIndex index, bool includeChangedCompanionsOnly, List<string> log)
        {
            var seen = new HashSet<string>();
            var items = new List<PathItem>();
            foreach (var path in filePaths)
            {
                if (string.IsNullOrWhiteSpace(path))
                    continue;
                var abs = ResolvePath(path);
                if (seen.Add(NormalizePath(abs)))
                    items.Add(new PathItem(abs, Path.IsPathRooted(path) ? ToDisplayPath(abs) : path, true));

                // 目录不作为自动补齐的配对路径：svn add / delete 目录是递归的，会带上用户没点名的内容
                // （目录本身由 add --parents / 祖先目录逻辑处理）
                var companion = UnityMetaPaths.GetCompanion(abs);
                if (companion == null || seen.Contains(NormalizePath(companion)) || Directory.Exists(companion))
                    continue;

                var exists = File.Exists(companion) || Directory.Exists(companion);
                bool include;
                if (includeChangedCompanionsOnly)
                {
                    // 只带有变更的配对；只锁定 / 未修改的条目（LockedFiles）不需要处理
                    var companionState = EffectiveState(index, companion);
                    include = companionState != VcsFileState.Unmodified && companionState != VcsFileState.Ignored
                              && (companionState != VcsFileState.Untracked || exists);
                }
                else
                {
                    include = index.Get(companion) != null || exists;
                }
                if (!include)
                    continue;

                seen.Add(NormalizePath(companion));
                var display = ToDisplayPath(companion);
                items.Add(new PathItem(companion, display, false));
                log?.Add($"Also including Unity counterpart: {display}");
            }
            return items;
        }

        /// <summary>
        /// add / delete 的展开：在 <see cref="ExpandWithCompanions"/> 之上，为每个将被新增的路径补上
        /// 未纳入版本控制的祖先目录 .meta（svn add --parents 会添加祖先目录，但不会添加目录旁边的 .meta）。
        /// </summary>
        private List<PathItem> ExpandForSchedule(IEnumerable<string> filePaths, StatusIndex index, List<string> log)
        {
            var items = ExpandWithCompanions(filePaths, index, includeChangedCompanionsOnly: true, log);
            var seen = new HashSet<string>(items.Select(i => NormalizePath(i.Abs)));
            var extra = new List<PathItem>();
            foreach (var item in items)
            {
                var state = EffectiveState(index, item.Abs);
                if (state != VcsFileState.Untracked && state != VcsFileState.Unmodified)
                    continue;
                foreach (var ancestor in GetAncestors(item.Abs))
                {
                    var meta = ancestor + UnityMetaPaths.MetaExtension;
                    if (EffectiveState(index, meta) != VcsFileState.Untracked || !File.Exists(meta) || !seen.Add(NormalizePath(meta)))
                        continue;
                    var display = ToDisplayPath(meta);
                    extra.Add(new PathItem(meta, display, false));
                    log?.Add($"Also including .meta of new parent directory: {display}");
                }
            }
            items.AddRange(extra);
            return items;
        }

        /// <summary>
        /// 路径的实际状态：svn status 对未纳入版本控制的目录只列出目录本身，其下的文件不出现；
        /// 这类文件按祖先目录的状态（Untracked / Ignored）处理。
        /// </summary>
        private VcsFileState EffectiveState(StatusIndex index, string abs)
        {
            var status = index.Get(abs);
            if (status != null)
                return status.State;
            foreach (var ancestor in GetAncestors(abs))
            {
                var state = index.Get(ancestor)?.State;
                if (state == VcsFileState.Untracked || state == VcsFileState.Ignored)
                    return state.Value;
            }
            return VcsFileState.Unmodified;
        }

        /// <summary>工作副本根之下（不含根本身）的所有祖先目录，由近及远。</summary>
        private IEnumerable<string> GetAncestors(string abs)
        {
            var root = NormalizePath(Path.GetFullPath(_workingDirectory)).TrimEnd('/');
            string current;
            try { current = Path.GetDirectoryName(Path.GetFullPath(abs).TrimEnd('\\', '/')); }
            catch { yield break; }

            while (!string.IsNullOrEmpty(current))
            {
                var norm = NormalizePath(current).TrimEnd('/');
                if (norm.Length <= root.Length || !norm.StartsWith(root + "/", StringComparison.Ordinal))
                    yield break;
                yield return current;
                current = Path.GetDirectoryName(current);
            }
        }

        private static bool IsUnder(string abs, string dir)
        {
            var a = NormalizePath(abs).TrimEnd('/');
            var d = NormalizePath(dir).TrimEnd('/');
            return a.StartsWith(d + "/", StringComparison.Ordinal);
        }

        /// <summary>
        /// 解析传入路径：绝对路径原样；相对路径先按工作副本根，不存在时再按 Unity 项目根
        /// （工作副本根可能在项目根之上，而模型通常给出 "Assets/..." 这样相对项目根的路径）。
        /// </summary>
        internal string ResolvePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;
            if (Path.IsPathRooted(path))
                return path;

            var fromWc = ResolveAbsolutePath(path, _workingDirectory);
            if (File.Exists(fromWc) || Directory.Exists(fromWc) || string.IsNullOrEmpty(_projectDirectory)
                || string.Equals(NormalizePath(_projectDirectory), NormalizePath(_workingDirectory), StringComparison.Ordinal))
                return fromWc;

            var fromProject = ResolveAbsolutePath(path, _projectDirectory);
            if ((File.Exists(fromProject) || Directory.Exists(fromProject)) && IsUnder(fromProject, _workingDirectory))
                return fromProject;
            // 都不存在（可能是已删除的文件）：父目录存在的那个更可能是对的
            var projectParent = Path.GetDirectoryName(fromProject);
            var wcParent = Path.GetDirectoryName(fromWc);
            if (!Directory.Exists(wcParent) && Directory.Exists(projectParent) && IsUnder(fromProject, _workingDirectory))
                return fromProject;
            return fromWc;
        }

        private string ToDisplayPath(string abs)
        {
            try
            {
                var root = Path.GetFullPath(_workingDirectory).TrimEnd('\\', '/');
                var full = Path.GetFullPath(abs);
                if (full.Length > root.Length + 1 && NormalizePath(full).StartsWith(NormalizePath(root) + "/", StringComparison.Ordinal))
                    return full.Substring(root.Length + 1).Replace('\\', '/');
                return full;
            }
            catch
            {
                return abs;
            }
        }

        // ===== 解析方法 =====

        private void ParseSvnInfo(string xmlOutput, VcsBranchInfo result)
        {
            try
            {
                var doc = XDocument.Parse(xmlOutput);
                var entry = doc.Descendants("entry").FirstOrDefault();

                if (entry != null)
                {
                    var url = entry.Element("url")?.Value;
                    var revision = entry.Attribute("revision")?.Value;
                    var commit = entry.Element("commit");
                    var commitRevision = commit?.Attribute("revision")?.Value;

                    result.CurrentRevision = commitRevision ?? revision ?? "unknown";

                    // 从 URL 提取分支名称
                    if (!string.IsNullOrEmpty(url))
                    {
                        result.CurrentBranch = ExtractBranchFromUrl(url);
                        result.RawOutput = $"URL: {url}\nRevision: {result.CurrentRevision}";
                    }
                }
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"Failed to parse SVN info: {ex.Message}");
            }
        }

        public static List<VcsCommit> ParseSvnLogXml(string xmlOutput)
        {
            var commits = new List<VcsCommit>();

            try
            {
                var doc = XDocument.Parse(xmlOutput);
                var logEntries = doc.Descendants("logentry");

                foreach (var entry in logEntries)
                {
                    var revision = entry.Attribute("revision")?.Value;
                    var author = entry.Element("author")?.Value;
                    var date = entry.Element("date")?.Value;
                    var message = entry.Element("msg")?.Value;

                    var commit = new VcsCommit
                    {
                        Revision = revision ?? "unknown",
                        Author = author ?? "unknown",
                        Date = date ?? "unknown",
                        Message = (message ?? "").Replace("\r\n", "\n")
                    };
                    // svn log -v：<paths><path action="M" copyfrom-path=".." copyfrom-rev="..">/trunk/a</path></paths>
                    var paths = entry.Element("paths");
                    if (paths != null)
                    {
                        foreach (var path in paths.Elements("path"))
                        {
                            var line = $"{path.Attribute("action")?.Value ?? "?"} {path.Value}";
                            var copyFrom = path.Attribute("copyfrom-path")?.Value;
                            if (!string.IsNullOrEmpty(copyFrom))
                                line += $" (from {copyFrom}@{path.Attribute("copyfrom-rev")?.Value})";
                            commit.ChangedFiles.Add(line);
                        }
                    }
                    commits.Add(commit);
                }
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"Failed to parse SVN log XML: {ex.Message}");
            }

            return commits;
        }

        /// <summary>解析 <c>svn list --xml</c> 的条目名。</summary>
        public static List<string> ParseSvnListNames(string xmlOutput)
        {
            var names = new List<string>();
            if (string.IsNullOrWhiteSpace(xmlOutput))
                return names;
            try
            {
                var doc = XDocument.Parse(xmlOutput);
                foreach (var entry in doc.Descendants("entry"))
                {
                    var name = entry.Element("name")?.Value;
                    if (!string.IsNullOrEmpty(name))
                        names.Add(name.TrimEnd('/'));
                }
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"Failed to parse SVN list XML: {ex.Message}");
            }
            return names;
        }

        private void ParseSvnRemoteInfo(string xmlOutput, VcsRemoteInfo result)
        {
            try
            {
                var doc = XDocument.Parse(xmlOutput);
                var entry = doc.Descendants("entry").FirstOrDefault();

                if (entry != null)
                {
                    var url = entry.Element("url")?.Value;
                    var repository = entry.Element("repository");
                    var root = repository?.Element("root")?.Value;

                    result.RemoteUrl = url ?? "unknown";
                    result.RemoteName = root ?? "unknown";
                }
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"Failed to parse SVN remote info: {ex.Message}");
            }
        }

        /// <summary>
        /// 从 svn update 文本输出解析冲突路径（status 不可用时的后备）：
        /// 前三列（文本 / 属性 / 树）任一为 'C' 即冲突，路径从第 5 列开始。
        /// </summary>
        public static List<string> ParseSvnUpdateConflicts(string output)
        {
            var files = new List<string>();

            foreach (var line in SplitLines(output))
            {
                // 状态列只由空格和 A/D/U/C/G/E/R/B 组成，且第 5 列是空格（排除 "Conflict discovered..." 等说明行）
                if (line.Length < 6 || line[4] != ' ')
                    continue;
                var columns = line.Substring(0, 4);
                if (columns.IndexOf('C') < 0 || columns.Trim(' ', 'C', 'A', 'D', 'U', 'G', 'E', 'R', 'B').Length != 0)
                    continue;
                var path = line.Substring(4).Trim();
                if (path.Length > 0)
                    files.Add(path);
            }

            return files;
        }

        private static IEnumerable<string> SplitLines(string output)
        {
            if (string.IsNullOrEmpty(output))
                yield break;
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                if (trimmed.Length > 0)
                    yield return trimmed;
            }
        }

        private static int CountLines(string output, string prefix)
        {
            return SplitLines(output).Count(l => l.StartsWith(prefix, StringComparison.Ordinal));
        }

        private static readonly Regex BlameLineRegex = new Regex(@"^\s*(\d+|-)\s+(\S+) (.*)$", RegexOptions.Compiled);

        /// <summary>
        /// 解析 svn blame 文本输出（<c>%6ld %10s 内容</c>）。本地修改尚未提交的行修订号与作者为 "-"。
        /// </summary>
        public static List<VcsBlameLine> ParseSvnBlame(string output)
        {
            var lines = new List<VcsBlameLine>();

            if (string.IsNullOrEmpty(output))
                return lines;

            var rawLines = output.Split('\n');
            // 输出以换行结尾：最后一个空元素不是文件内容
            var count = rawLines.Length;
            if (count > 0 && rawLines[count - 1].Length == 0)
                count--;

            for (var i = 0; i < count; i++)
            {
                var rawLine = rawLines[i].TrimEnd('\r');
                var match = BlameLineRegex.Match(rawLine);
                if (!match.Success)
                {
                    // 内容为空的行："     5      alice " 之后没有内容
                    match = Regex.Match(rawLine, @"^\s*(\d+|-)\s+(\S+)\s*$");
                    if (!match.Success)
                        continue;
                }

                var revision = match.Groups[1].Value;
                var author = match.Groups[2].Value;
                lines.Add(new VcsBlameLine
                {
                    LineNumber = i + 1,
                    Revision = revision == "-" ? "working copy" : revision,
                    Author = author == "-" ? "(uncommitted)" : author,
                    Date = "unknown",
                    Content = match.Groups.Count > 3 ? match.Groups[3].Value : string.Empty
                });
            }

            return lines;
        }

        private void ParseSvnInfoDetailed(string xmlOutput, VcsSvnInfo result)
        {
            try
            {
                var doc = XDocument.Parse(xmlOutput);
                var entry = doc.Descendants("entry").FirstOrDefault();

                if (entry != null)
                {
                    result.Url = entry.Element("url")?.Value;
                    result.Revision = entry.Attribute("revision")?.Value;
                    result.NodeKind = entry.Attribute("kind")?.Value;

                    var repository = entry.Element("repository");
                    result.RepositoryRoot = repository?.Element("root")?.Value;

                    var commit = entry.Element("commit");
                    if (commit != null)
                    {
                        result.LastChangedRevision = commit.Attribute("revision")?.Value;
                        result.LastChangedAuthor = commit.Element("author")?.Value;
                        result.LastChangedDate = commit.Element("date")?.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"Failed to parse SVN info XML: {ex.Message}");
            }
        }

        private string ExtractBranchFromUrl(string url)
        {
            // 尝试从 URL 中提取分支名称
            // 例如: https://svn.example.com/repo/branches/feature-x -> feature-x
            //      https://svn.example.com/repo/trunk -> trunk

            var match = Regex.Match(url, @"/(trunk|branches/([^/]+)|tags/([^/]+))");
            if (match.Success)
            {
                if (match.Groups[1].Value == "trunk")
                    return "trunk";
                if (!string.IsNullOrEmpty(match.Groups[2].Value))
                    return match.Groups[2].Value;
                if (!string.IsNullOrEmpty(match.Groups[3].Value))
                    return match.Groups[3].Value;
            }

            return "unknown";
        }

        private string ExtractRepositoryRoot(string xmlOutput)
        {
            try
            {
                var doc = XDocument.Parse(xmlOutput);
                var entry = doc.Descendants("entry").FirstOrDefault();
                var repository = entry?.Element("repository");
                return repository?.Element("root")?.Value;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 将工作副本路径转为单个 svn 参数（见 <see cref="SvnCommandLine.PathArg"/>）。
        /// </summary>
        internal static string SvnPathArg(string path) => SvnCommandLine.PathArg(path);

        /// <summary>
        /// 将路径解析为绝对路径。
        /// 若 path 已是绝对路径则直接返回；否则以 basePath 为基准拼接。
        /// </summary>
        private static string ResolveAbsolutePath(string path, string basePath)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            // 已是绝对路径（Windows: C:\... 或 \\...，Unix: /...）
            if (Path.IsPathRooted(path))
                return path;

            // 相对路径：以 basePath 为基准拼接
            return Path.GetFullPath(Path.Combine(basePath, path));
        }

        /// <summary>
        /// 规范化路径用于比较：统一使用正斜杠、小写（Windows 不区分大小写）。
        /// </summary>
        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            var normalized = path.Replace('\\', '/');

            // Windows 文件系统不区分大小写
            if (System.Environment.OSVersion.Platform == System.PlatformID.Win32NT)
                normalized = normalized.ToLowerInvariant();

            return normalized;
        }
    }
}
