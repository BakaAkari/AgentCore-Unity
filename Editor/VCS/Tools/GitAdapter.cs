using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCore.Editor.Components.VCS.Tools
{
    /// <summary>
    /// Git 适配器
    /// 实现 Git 版本控制系统的所有查询和操作
    /// </summary>
    public class GitAdapter : IVcsAdapter
    {
        private readonly string _workingDirectory;

        public VcsType VcsType => VcsType.Git;

        public GitAdapter(string workingDirectory)
        {
            _workingDirectory = workingDirectory;
        }

        public bool IsAvailable()
        {
            return VcsDetector.IsVcsCommandAvailable(VcsType.Git);
        }

        // ===== Phase 1: 只读查询 =====

        public async Task<VcsStatusResult> GetStatusAsync(CancellationToken ct = default)
        {
            var result = new VcsStatusResult();

            try
            {
                var cmdResult = await VcsCommandExecutor.ExecuteAsync(
                    "git", "status --porcelain", _workingDirectory, ct: ct);

                if (!cmdResult.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = cmdResult.ErrorMessage;
                    return result;
                }

                result.RawOutput = cmdResult.Output;
                result.Files = ParseGitStatus(cmdResult.Output);
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to get Git status: {ex.Message}";
            }

            return result;
        }

        public async Task<VcsBranchInfo> GetBranchInfoAsync(CancellationToken ct = default)
        {
            var result = new VcsBranchInfo();

            try
            {
                // 获取当前分支
                var branchResult = await VcsCommandExecutor.ExecuteAsync(
                    "git", "branch --show-current", _workingDirectory, ct: ct);

                if (!branchResult.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = branchResult.ErrorMessage;
                    return result;
                }

                result.CurrentBranch = branchResult.Output.Trim();

                // 获取当前提交 hash
                var revResult = await VcsCommandExecutor.ExecuteAsync(
                    "git", "rev-parse HEAD", _workingDirectory, ct: ct);

                if (revResult.Success)
                {
                    result.CurrentRevision = revResult.Output.Trim();
                }

                // 获取所有分支
                var allBranchesResult = await VcsCommandExecutor.ExecuteAsync(
                    "git", "branch -a", _workingDirectory, ct: ct);

                if (allBranchesResult.Success)
                {
                    result.AllBranches = ParseGitBranches(allBranchesResult.Output);
                }

                result.Success = true;
                result.RawOutput = $"Branch: {result.CurrentBranch}\nRevision: {result.CurrentRevision}";
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to get Git branch info: {ex.Message}";
            }

            return result;
        }

        public async Task<List<VcsCommit>> GetLogAsync(int maxCount = 20, CancellationToken ct = default)
        {
            var commits = new List<VcsCommit>();

            try
            {
                var format = "--pretty=format:%H%n%an%n%ad%n%s%n---END---";
                var cmdResult = await VcsCommandExecutor.ExecuteAsync(
                    "git", $"log -{maxCount} {format} --date=iso", _workingDirectory, ct: ct);

                if (!cmdResult.Success)
                    return commits;

                commits = ParseGitLog(cmdResult.Output);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"Failed to get Git log: {ex.Message}");
            }

            return commits;
        }

        public async Task<string> GetDiffAsync(string filePath = null, CancellationToken ct = default)
        {
            try
            {
                var args = string.IsNullOrEmpty(filePath) ? "diff HEAD" : $"diff HEAD -- \"{filePath}\"";
                var cmdResult = await VcsCommandExecutor.ExecuteAsync(
                    "git", args, _workingDirectory, ct: ct);

                return cmdResult.Success ? cmdResult.Output : $"Error: {cmdResult.ErrorMessage}";
            }
            catch (Exception ex)
            {
                return $"Failed to get Git diff: {ex.Message}";
            }
        }

        public async Task<VcsRemoteInfo> GetRemoteInfoAsync(CancellationToken ct = default)
        {
            var result = new VcsRemoteInfo();

            try
            {
                var cmdResult = await VcsCommandExecutor.ExecuteAsync(
                    "git", "remote -v", _workingDirectory, ct: ct);

                if (!cmdResult.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = cmdResult.ErrorMessage;
                    return result;
                }

                result.RawOutput = cmdResult.Output;
                ParseGitRemote(cmdResult.Output, result);
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to get Git remote info: {ex.Message}";
            }

            return result;
        }

        public async Task<List<string>> GetTagsAsync(CancellationToken ct = default)
        {
            var tags = new List<string>();

            try
            {
                var cmdResult = await VcsCommandExecutor.ExecuteAsync(
                    "git", "tag -l", _workingDirectory, ct: ct);

                if (cmdResult.Success && !string.IsNullOrEmpty(cmdResult.Output))
                {
                    tags = cmdResult.Output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(t => t.Trim())
                        .Where(t => !string.IsNullOrEmpty(t))
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"Failed to get Git tags: {ex.Message}");
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

                var cmdResult = await VcsCommandExecutor.ExecuteAsync(
                    "git", $"blame --porcelain \"{filePath}\"", _workingDirectory, ct: ct);

                if (!cmdResult.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = cmdResult.ErrorMessage;
                    return result;
                }

                result.RawOutput = cmdResult.Output;
                result.Lines = ParseGitBlame(cmdResult.Output);
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to get Git blame: {ex.Message}";
            }

            return result;
        }

        /// <summary>
        /// 获取指定提交的详细信息 (git show)
        /// </summary>
        public async Task<VcsCommitDetail> GetCommitInfoAsync(string revision, CancellationToken ct = default)
        {
            var result = new VcsCommitDetail();

            try
            {
                if (string.IsNullOrEmpty(revision))
                {
                    result.Success = false;
                    result.ErrorMessage = "revision is required for get_commit_info action.";
                    return result;
                }

                // 获取提交信息
                var cmdResult = await VcsCommandExecutor.ExecuteAsync(
                    "git", $"show --stat --format=fuller \"{revision}\"", _workingDirectory, ct: ct);

                if (!cmdResult.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = cmdResult.ErrorMessage;
                    return result;
                }

                result.RawOutput = cmdResult.Output;
                ParseGitShow(cmdResult.Output, result);

                // 获取变更文件列表
                var filesResult = await VcsCommandExecutor.ExecuteAsync(
                    "git", $"diff-tree --no-commit-id --name-only -r \"{revision}\"", _workingDirectory, ct: ct);

                if (filesResult.Success && !string.IsNullOrEmpty(filesResult.Output))
                {
                    result.ChangedFiles = filesResult.Output
                        .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(f => f.Trim())
                        .Where(f => !string.IsNullOrEmpty(f))
                        .ToList();
                }

                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to get commit info: {ex.Message}";
            }

            return result;
        }

        // ===== Phase 2: 操作类 =====

        public async Task<VcsOperationResult> StageFilesAsync(List<string> filePaths, CancellationToken ct = default)
        {
            var result = new VcsOperationResult();

            try
            {
                var files = filePaths != null && filePaths.Count > 0
                    ? string.Join(" ", filePaths.Select(f => $"\"{f}\""))
                    : ".";

                var cmdResult = await VcsCommandExecutor.ExecuteAsync(
                    "git", $"add {files}", _workingDirectory, ct: ct);

                if (!cmdResult.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = cmdResult.ErrorMessage;
                    return result;
                }

                result.Success = true;
                result.Message = filePaths != null && filePaths.Count > 0
                    ? $"Staged {filePaths.Count} file(s)."
                    : "Staged all changes.";
                result.AffectedFiles = filePaths ?? new List<string>();
                result.RawOutput = cmdResult.Output;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to stage files: {ex.Message}";
            }

            return result;
        }

        public async Task<VcsOperationResult> UnstageFilesAsync(List<string> filePaths, CancellationToken ct = default)
        {
            var result = new VcsOperationResult();

            try
            {
                var files = filePaths != null && filePaths.Count > 0
                    ? string.Join(" ", filePaths.Select(f => $"\"{f}\""))
                    : ".";

                var cmdResult = await VcsCommandExecutor.ExecuteAsync(
                    "git", $"reset HEAD {files}", _workingDirectory, ct: ct);

                if (!cmdResult.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = cmdResult.ErrorMessage;
                    return result;
                }

                result.Success = true;
                result.Message = filePaths != null && filePaths.Count > 0
                    ? $"Unstaged {filePaths.Count} file(s)."
                    : "Unstaged all changes.";
                result.AffectedFiles = filePaths ?? new List<string>();
                result.RawOutput = cmdResult.Output;
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
            var result = new VcsOperationResult();

            try
            {
                if (string.IsNullOrEmpty(message))
                {
                    result.Success = false;
                    result.ErrorMessage = "Commit message is required.";
                    return result;
                }

                // 转义消息中的双引号
                var escapedMessage = message.Replace("\"", "\\\"");
                var cmdResult = await VcsCommandExecutor.ExecuteAsync(
                    "git", $"commit -m \"{escapedMessage}\"", _workingDirectory, ct: ct);

                if (!cmdResult.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = cmdResult.ErrorMessage;
                    return result;
                }

                result.Success = true;
                result.Message = $"Committed with message: {message}";
                result.RawOutput = cmdResult.Output;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to commit: {ex.Message}";
            }

            return result;
        }

        public async Task<VcsOperationResult> RevertFilesAsync(List<string> filePaths, CancellationToken ct = default)
        {
            var result = new VcsOperationResult();

            try
            {
                if (filePaths == null || filePaths.Count == 0)
                {
                    result.Success = false;
                    result.ErrorMessage = "At least one file path is required for revert.";
                    return result;
                }

                var files = string.Join(" ", filePaths.Select(f => $"\"{f}\""));
                var cmdResult = await VcsCommandExecutor.ExecuteAsync(
                    "git", $"checkout -- {files}", _workingDirectory, ct: ct);

                if (!cmdResult.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = cmdResult.ErrorMessage;
                    return result;
                }

                result.Success = true;
                result.Message = $"Reverted {filePaths.Count} file(s).";
                result.AffectedFiles = filePaths;
                result.RawOutput = cmdResult.Output;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to revert files: {ex.Message}";
            }

            return result;
        }

        public async Task<VcsOperationResult> SyncAsync(CancellationToken ct = default)
        {
            var result = new VcsOperationResult();

            try
            {
                var cmdResult = await VcsCommandExecutor.ExecuteAsync(
                    "git", "pull", _workingDirectory, timeoutSeconds: 60, ct: ct);

                if (!cmdResult.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = cmdResult.ErrorMessage;
                    return result;
                }

                result.Success = true;
                result.Message = "Pull completed successfully.";
                result.RawOutput = cmdResult.Output;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to pull: {ex.Message}";
            }

            return result;
        }

        /// <summary>
        /// 创建新分支
        /// </summary>
        public async Task<VcsOperationResult> CreateBranchAsync(string branchName, CancellationToken ct = default)
        {
            var result = new VcsOperationResult();

            try
            {
                if (string.IsNullOrEmpty(branchName))
                {
                    result.Success = false;
                    result.ErrorMessage = "Branch name is required.";
                    return result;
                }

                var cmdResult = await VcsCommandExecutor.ExecuteAsync(
                    "git", $"branch \"{branchName}\"", _workingDirectory, ct: ct);

                if (!cmdResult.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = cmdResult.ErrorMessage;
                    return result;
                }

                result.Success = true;
                result.Message = $"Created branch: {branchName}";
                result.RawOutput = cmdResult.Output;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to create branch: {ex.Message}";
            }

            return result;
        }

        /// <summary>
        /// 切换分支
        /// </summary>
        public async Task<VcsOperationResult> SwitchBranchAsync(string branchName, CancellationToken ct = default)
        {
            var result = new VcsOperationResult();

            try
            {
                if (string.IsNullOrEmpty(branchName))
                {
                    result.Success = false;
                    result.ErrorMessage = "Branch name is required.";
                    return result;
                }

                var cmdResult = await VcsCommandExecutor.ExecuteAsync(
                    "git", $"checkout \"{branchName}\"", _workingDirectory, ct: ct);

                if (!cmdResult.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = cmdResult.ErrorMessage;
                    return result;
                }

                result.Success = true;
                result.Message = $"Switched to branch: {branchName}";
                result.RawOutput = cmdResult.Output;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to switch branch: {ex.Message}";
            }

            return result;
        }

        /// <summary>
        /// 暂存当前工作区变更 (git stash)
        /// </summary>
        public async Task<VcsOperationResult> StashAsync(string message = null, CancellationToken ct = default)
        {
            var result = new VcsOperationResult();

            try
            {
                var args = string.IsNullOrEmpty(message) ? "stash" : $"stash push -m \"{message}\"";
                var cmdResult = await VcsCommandExecutor.ExecuteAsync(
                    "git", args, _workingDirectory, ct: ct);

                if (!cmdResult.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = cmdResult.ErrorMessage;
                    return result;
                }

                result.Success = true;
                result.Message = "Changes stashed successfully.";
                result.RawOutput = cmdResult.Output;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to stash: {ex.Message}";
            }

            return result;
        }

        /// <summary>
        /// 恢复暂存的变更 (git stash pop)
        /// </summary>
        public async Task<VcsOperationResult> StashPopAsync(CancellationToken ct = default)
        {
            var result = new VcsOperationResult();

            try
            {
                var cmdResult = await VcsCommandExecutor.ExecuteAsync(
                    "git", "stash pop", _workingDirectory, ct: ct);

                if (!cmdResult.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = cmdResult.ErrorMessage;
                    return result;
                }

                result.Success = true;
                result.Message = "Stash popped successfully.";
                result.RawOutput = cmdResult.Output;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to pop stash: {ex.Message}";
            }

            return result;
        }

        // ===== 解析方法 =====

        private List<VcsFileStatus> ParseGitStatus(string output)
        {
            var files = new List<VcsFileStatus>();

            if (string.IsNullOrEmpty(output))
                return files;

            var lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                if (line.Length < 3)
                    continue;

                var statusCode = line.Substring(0, 2);
                var filePath = line.Substring(3);

                var state = statusCode switch
                {
                    "??" => VcsFileState.Untracked,
                    "!!" => VcsFileState.Ignored,
                    "M " or " M" or "MM" => VcsFileState.Modified,
                    "A " or " A" or "AM" => VcsFileState.Added,
                    "D " or " D" => VcsFileState.Deleted,
                    "R " or " R" => VcsFileState.Renamed,
                    "C " or " C" => VcsFileState.Copied,
                    "UU" or "AA" or "DD" => VcsFileState.Conflicted,
                    _ => VcsFileState.Modified
                };

                files.Add(new VcsFileStatus
                {
                    FilePath = filePath,
                    State = state,
                    StateDescription = GetStateDescription(state)
                });
            }

            return files;
        }

        private List<string> ParseGitBranches(string output)
        {
            var branches = new List<string>();

            if (string.IsNullOrEmpty(output))
                return branches;

            var lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var branch = line.TrimStart('*', ' ');
                if (!string.IsNullOrEmpty(branch))
                    branches.Add(branch);
            }

            return branches;
        }

        private List<VcsCommit> ParseGitLog(string output)
        {
            var commits = new List<VcsCommit>();

            if (string.IsNullOrEmpty(output))
                return commits;

            var entries = output.Split(new[] { "---END---" }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var entry in entries)
            {
                var lines = entry.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length < 4)
                    continue;

                commits.Add(new VcsCommit
                {
                    Revision = lines[0].Trim(),
                    Author = lines[1].Trim(),
                    Date = lines[2].Trim(),
                    Message = lines[3].Trim()
                });
            }

            return commits;
        }

        private void ParseGitRemote(string output, VcsRemoteInfo result)
        {
            if (string.IsNullOrEmpty(output))
                return;

            var lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0)
                return;

            // 格式: origin  https://github.com/user/repo.git (fetch)
            var match = Regex.Match(lines[0], @"^(\S+)\s+(\S+)");
            if (match.Success)
            {
                result.RemoteName = match.Groups[1].Value;
                result.RemoteUrl = match.Groups[2].Value;
            }
        }

        private List<VcsBlameLine> ParseGitBlame(string output)
        {
            var lines = new List<VcsBlameLine>();

            if (string.IsNullOrEmpty(output))
                return lines;

            // git blame --porcelain 格式:
            // <hash> <orig_line> <final_line> <num_lines>
            // author <name>
            // author-time <timestamp>
            // ...
            // \t<content>

            var rawLines = output.Split(new[] { '\n' }, StringSplitOptions.None);
            string currentHash = null;
            string currentAuthor = null;
            string currentDate = null;
            int currentLineNumber = 0;

            foreach (var rawLine in rawLines)
            {
                if (string.IsNullOrEmpty(rawLine))
                    continue;

                // 检测新的 blame 块头
                var headerMatch = Regex.Match(rawLine, @"^([0-9a-f]{40})\s+(\d+)\s+(\d+)");
                if (headerMatch.Success)
                {
                    currentHash = headerMatch.Groups[1].Value.Substring(0, 8);
                    currentLineNumber = int.Parse(headerMatch.Groups[3].Value);
                    continue;
                }

                if (rawLine.StartsWith("author "))
                {
                    currentAuthor = rawLine.Substring(7);
                    continue;
                }

                if (rawLine.StartsWith("author-time "))
                {
                    var timestamp = rawLine.Substring(12);
                    if (long.TryParse(timestamp, out var unixTime))
                    {
                        var dt = DateTimeOffset.FromUnixTimeSeconds(unixTime);
                        currentDate = dt.ToString("yyyy-MM-dd");
                    }
                    continue;
                }

                // 内容行以 \t 开头
                if (rawLine.StartsWith("\t"))
                {
                    lines.Add(new VcsBlameLine
                    {
                        LineNumber = currentLineNumber,
                        Revision = currentHash ?? "unknown",
                        Author = currentAuthor ?? "unknown",
                        Date = currentDate ?? "unknown",
                        Content = rawLine.Substring(1)
                    });
                }
            }

            return lines;
        }

        private void ParseGitShow(string output, VcsCommitDetail result)
        {
            if (string.IsNullOrEmpty(output))
                return;

            var lines = output.Split(new[] { '\n' }, StringSplitOptions.None);

            foreach (var line in lines)
            {
                if (line.StartsWith("commit "))
                    result.Revision = line.Substring(7).Trim();
                else if (line.StartsWith("Author:"))
                    result.Author = line.Substring(7).Trim();
                else if (line.StartsWith("AuthorDate:"))
                    result.Date = line.Substring(11).Trim();
                else if (line.StartsWith("CommitDate:"))
                    result.Date = result.Date ?? line.Substring(11).Trim();
                else if (line.StartsWith("    ") && string.IsNullOrEmpty(result.Message))
                    result.Message = line.Trim();
            }
        }

        private string GetStateDescription(VcsFileState state)
        {
            return state switch
            {
                VcsFileState.Modified => "Modified",
                VcsFileState.Added => "Added",
                VcsFileState.Deleted => "Deleted",
                VcsFileState.Renamed => "Renamed",
                VcsFileState.Copied => "Copied",
                VcsFileState.Untracked => "Untracked",
                VcsFileState.Ignored => "Ignored",
                VcsFileState.Conflicted => "Conflicted",
                VcsFileState.Missing => "Missing",
                _ => "Unmodified"
            };
        }
    }
}
