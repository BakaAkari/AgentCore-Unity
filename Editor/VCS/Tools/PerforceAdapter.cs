using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Components.VCS.Tools
{
    /// <summary>
    /// Perforce 适配器
    /// 实现 Perforce 版本控制系统的所有查询和操作
    /// </summary>
    public class PerforceAdapter : IVcsAdapter
    {
        private readonly string _workingDirectory;

        public VcsType VcsType => VcsType.Perforce;

        public PerforceAdapter(string workingDirectory)
        {
            _workingDirectory = workingDirectory;
        }

        public bool IsAvailable()
        {
            return VcsDetector.IsVcsCommandAvailable(VcsType.Perforce);
        }

        // ===== Phase 1: 只读查询 =====

        public async Task<VcsStatusResult> GetStatusAsync(CancellationToken ct = default)
        {
            var result = new VcsStatusResult();

            try
            {
                // p4 opened - 显示已打开的文件
                var openedResult = await VcsCommandExecutor.ExecuteAsync(
                    "p4", "opened", _workingDirectory, ct: ct);

                // p4 diff -sa - 显示已修改但未打开的文件
                var diffResult = await VcsCommandExecutor.ExecuteAsync(
                    "p4", "diff -sa", _workingDirectory, ct: ct);

                result.RawOutput = $"=== Opened Files ===\n{openedResult.Output}\n\n=== Modified Files ===\n{diffResult.Output}";
                result.Files = ParsePerforceStatus(openedResult.Output, diffResult.Output);
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to get Perforce status: {ex.Message}";
            }

            return result;
        }

        public async Task<VcsBranchInfo> GetBranchInfoAsync(CancellationToken ct = default)
        {
            var result = new VcsBranchInfo();

            try
            {
                // 获取当前客户端信息
                var clientResult = await VcsCommandExecutor.ExecuteAsync(
                    "p4", "client -o", _workingDirectory, ct: ct);

                if (!clientResult.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = clientResult.ErrorMessage;
                    return result;
                }

                // 获取当前变更列表
                var changesResult = await VcsCommandExecutor.ExecuteAsync(
                    "p4", "changes -m 1 #have", _workingDirectory, ct: ct);

                ParsePerforceInfo(clientResult.Output, changesResult.Output, result);
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to get Perforce branch info: {ex.Message}";
            }

            return result;
        }

        public async Task<List<VcsCommit>> GetLogAsync(int maxCount = 20, CancellationToken ct = default)
        {
            var commits = new List<VcsCommit>();

            try
            {
                var cmdResult = await VcsCommandExecutor.ExecuteAsync(
                    "p4", $"changes -m {maxCount} -l", _workingDirectory, ct: ct);

                if (!cmdResult.Success)
                    return commits;

                commits = ParsePerforceChanges(cmdResult.Output);
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"Failed to get Perforce log: {ex.Message}");
            }

            return commits;
        }

        public async Task<string> GetDiffAsync(string filePath = null, CancellationToken ct = default)
        {
            try
            {
                var args = string.IsNullOrEmpty(filePath) ? "diff -du" : $"diff -du \"{filePath}\"";
                var cmdResult = await VcsCommandExecutor.ExecuteAsync(
                    "p4", args, _workingDirectory, ct: ct);

                return cmdResult.Success ? cmdResult.Output : $"Error: {cmdResult.ErrorMessage}";
            }
            catch (Exception ex)
            {
                return $"Failed to get Perforce diff: {ex.Message}";
            }
        }

        public async Task<VcsRemoteInfo> GetRemoteInfoAsync(CancellationToken ct = default)
        {
            var result = new VcsRemoteInfo();

            try
            {
                var cmdResult = await VcsCommandExecutor.ExecuteAsync(
                    "p4", "info", _workingDirectory, ct: ct);

                if (!cmdResult.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = cmdResult.ErrorMessage;
                    return result;
                }

                result.RawOutput = cmdResult.Output;
                ParsePerforceRemoteInfo(cmdResult.Output, result);
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to get Perforce remote info: {ex.Message}";
            }

            return result;
        }

        public async Task<List<string>> GetTagsAsync(CancellationToken ct = default)
        {
            var tags = new List<string>();

            try
            {
                // Perforce 使用 labels 而不是 tags
                var cmdResult = await VcsCommandExecutor.ExecuteAsync(
                    "p4", "labels", _workingDirectory, ct: ct);

                if (cmdResult.Success && !string.IsNullOrEmpty(cmdResult.Output))
                {
                    tags = ParsePerforceLabels(cmdResult.Output);
                }
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"Failed to get Perforce labels: {ex.Message}");
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

                // p4 annotate -c 显示每行的 changelist 号
                var cmdResult = await VcsCommandExecutor.ExecuteAsync(
                    "p4", $"annotate -c \"{filePath}\"", _workingDirectory, ct: ct);

                if (!cmdResult.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = cmdResult.ErrorMessage;
                    return result;
                }

                result.RawOutput = cmdResult.Output;
                result.Lines = ParsePerforceAnnotate(cmdResult.Output);
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to get Perforce annotate: {ex.Message}";
            }

            return result;
        }

        public async Task<VcsSyncStatus> GetSyncStatusAsync(CancellationToken ct = default)
        {
            var result = new VcsSyncStatus();

            try
            {
                var statusTask = GetStatusAsync(ct);
                var previewTask = VcsCommandExecutor.ExecuteAsync(
                    "p4", "sync -n", _workingDirectory, timeoutSeconds: 120, ct: ct);

                await Task.WhenAll(statusTask, previewTask);

                var localStatus = await statusTask;
                var previewResult = await previewTask;
                if (!previewResult.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = previewResult.ErrorMessage;
                    return result;
                }

                result.Success = true;
                result.RawOutput = previewResult.Output;
                result.HasLocalChanges = localStatus.Success && localStatus.Files.Count > 0;
                result.LocalChangeCount = localStatus.Success ? localStatus.Files.Count : 0;
                result.HasConflicts = localStatus.Success && localStatus.Files.Any(f => f.State == VcsFileState.Conflicted);
                result.ConflictedFiles = localStatus.Success
                    ? localStatus.Files.Where(f => f.State == VcsFileState.Conflicted).Select(f => f.FilePath).ToList()
                    : new List<string>();
                result.RemoteChangedFiles = ParsePerforcePreviewSyncFiles(previewResult.Output);
                result.RemoteChangeCount = result.RemoteChangedFiles.Count;
                result.HasRemoteChanges = result.RemoteChangeCount > 0;
                result.Summary = result.HasRemoteChanges
                    ? $"Depot has {result.RemoteChangeCount} pending file update(s)."
                    : "Workspace is up to date with depot.";
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to check Perforce sync status: {ex.Message}";
            }

            return result;
        }

        /// <summary>
        /// 获取 Perforce 客户端详细信息 (p4 client -o)
        /// </summary>
        public async Task<VcsPerforceClientInfo> GetClientInfoAsync(CancellationToken ct = default)
        {
            var result = new VcsPerforceClientInfo();

            try
            {
                var cmdResult = await VcsCommandExecutor.ExecuteAsync(
                    "p4", "client -o", _workingDirectory, ct: ct);

                if (!cmdResult.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = cmdResult.ErrorMessage;
                    return result;
                }

                result.RawOutput = cmdResult.Output;
                ParsePerforceClientOutput(cmdResult.Output, result);
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to get Perforce client info: {ex.Message}";
            }

            return result;
        }

        /// <summary>
        /// 获取 Perforce 变更列表详情 (p4 describe)
        /// </summary>
        public async Task<VcsPerforceChangelist> GetChangelistAsync(string changeNumber = null, CancellationToken ct = default)
        {
            var result = new VcsPerforceChangelist();

            try
            {
                if (string.IsNullOrEmpty(changeNumber))
                {
                    // 获取默认 pending changelist
                    var pendingResult = await VcsCommandExecutor.ExecuteAsync(
                        "p4", "changes -s pending -c $P4CLIENT -m 1", _workingDirectory, ct: ct);

                    if (pendingResult.Success && !string.IsNullOrEmpty(pendingResult.Output))
                    {
                        var match = Regex.Match(pendingResult.Output, @"^Change\s+(\d+)");
                        if (match.Success)
                            changeNumber = match.Groups[1].Value;
                    }

                    if (string.IsNullOrEmpty(changeNumber))
                    {
                        result.Success = false;
                        result.ErrorMessage = "No pending changelist found. Specify a change_number.";
                        return result;
                    }
                }

                var cmdResult = await VcsCommandExecutor.ExecuteAsync(
                    "p4", $"describe -s {changeNumber}", _workingDirectory, ct: ct);

                if (!cmdResult.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = cmdResult.ErrorMessage;
                    return result;
                }

                result.RawOutput = cmdResult.Output;
                ParsePerforceDescribe(cmdResult.Output, result);
                result.ChangeNumber = changeNumber;
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to get Perforce changelist: {ex.Message}";
            }

            return result;
        }

        // ===== Phase 2: 操作类 =====

        public async Task<VcsOperationResult> StageFilesAsync(List<string> filePaths, CancellationToken ct = default)
        {
            var result = new VcsOperationResult();

            try
            {
                if (filePaths == null || filePaths.Count == 0)
                {
                    result.Success = false;
                    result.ErrorMessage = "At least one file path is required.";
                    return result;
                }

                var allOutput = new List<string>();
                var affectedFiles = new List<string>();

                foreach (var filePath in filePaths)
                {
                    // 先尝试 edit（已存在的文件），失败则尝试 add（新文件）
                    var editResult = await VcsCommandExecutor.ExecuteAsync(
                        "p4", $"edit \"{filePath}\"", _workingDirectory, ct: ct);

                    if (editResult.Success)
                    {
                        allOutput.Add(editResult.Output);
                        affectedFiles.Add(filePath);
                    }
                    else
                    {
                        var addResult = await VcsCommandExecutor.ExecuteAsync(
                            "p4", $"add \"{filePath}\"", _workingDirectory, ct: ct);

                        if (addResult.Success)
                        {
                            allOutput.Add(addResult.Output);
                            affectedFiles.Add(filePath);
                        }
                        else
                        {
                            allOutput.Add($"Failed: {filePath} - {addResult.ErrorMessage}");
                        }
                    }
                }

                result.Success = affectedFiles.Count > 0;
                result.Message = $"Checked out/added {affectedFiles.Count} of {filePaths.Count} file(s).";
                result.AffectedFiles = affectedFiles;
                result.RawOutput = string.Join("\n", allOutput);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to checkout files: {ex.Message}";
            }

            return result;
        }

        public async Task<VcsOperationResult> UnstageFilesAsync(List<string> filePaths, CancellationToken ct = default)
        {
            // Perforce 中 "unstage" 等同于 revert 未修改的文件
            return await RevertFilesAsync(filePaths, ct);
        }

        public async Task<VcsOperationResult> CommitAsync(string message, CancellationToken ct = default)
        {
            var result = new VcsOperationResult();

            try
            {
                if (string.IsNullOrEmpty(message))
                {
                    result.Success = false;
                    result.ErrorMessage = "Submit description is required.";
                    return result;
                }

                // p4 submit -d "message"
                var escapedMessage = message.Replace("\"", "\\\"");
                var cmdResult = await VcsCommandExecutor.ExecuteAsync(
                    "p4", $"submit -d \"{escapedMessage}\"", _workingDirectory, ct: ct);

                if (!cmdResult.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = cmdResult.ErrorMessage;
                    return result;
                }

                result.Success = true;
                result.Message = $"Submitted with description: {message}";
                result.RawOutput = cmdResult.Output;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to submit: {ex.Message}";
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
                    "p4", $"revert {files}", _workingDirectory, ct: ct);

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
                    "p4", "sync", _workingDirectory, timeoutSeconds: 120, ct: ct);

                if (!cmdResult.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = cmdResult.ErrorMessage;
                    return result;
                }

                result.Success = true;
                result.Message = "Sync completed successfully.";
                result.RawOutput = cmdResult.Output;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to sync: {ex.Message}";
            }

            return result;
        }

        // ===== 解析方法 =====

        private List<string> ParsePerforcePreviewSyncFiles(string output)
        {
            var files = new List<string>();

            if (string.IsNullOrEmpty(output))
                return files;

            var lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var match = Regex.Match(line, "^(//[^#]+)#");
                if (match.Success)
                    files.Add(match.Groups[1].Value);
            }

            return files;
        }

        private List<VcsFileStatus> ParsePerforceStatus(string openedOutput, string diffOutput)
        {
            var files = new List<VcsFileStatus>();

            // 解析已打开的文件
            if (!string.IsNullOrEmpty(openedOutput))
            {
                var lines = openedOutput.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    // 格式: //depot/path/file.txt#1 - edit default change (text)
                    var match = Regex.Match(line, @"^(//[^#]+)#\d+\s+-\s+(\w+)");
                    if (match.Success)
                    {
                        var filePath = match.Groups[1].Value;
                        var action = match.Groups[2].Value;

                        var state = action.ToLowerInvariant() switch
                        {
                            "edit" => VcsFileState.Modified,
                            "add" => VcsFileState.Added,
                            "delete" => VcsFileState.Deleted,
                            "move/add" => VcsFileState.Renamed,
                            "integrate" => VcsFileState.Modified,
                            _ => VcsFileState.Modified
                        };

                        files.Add(new VcsFileStatus
                        {
                            FilePath = filePath,
                            State = state,
                            StateDescription = GetStateDescription(state)
                        });
                    }
                }
            }

            // 解析已修改但未打开的文件
            if (!string.IsNullOrEmpty(diffOutput))
            {
                var lines = diffOutput.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    if (!string.IsNullOrEmpty(line) && !files.Any(f => f.FilePath == line))
                    {
                        files.Add(new VcsFileStatus
                        {
                            FilePath = line,
                            State = VcsFileState.Modified,
                            StateDescription = "Modified (not opened)"
                        });
                    }
                }
            }

            return files;
        }

        private void ParsePerforceInfo(string clientOutput, string changesOutput, VcsBranchInfo result)
        {
            // 从 client 输出中提取客户端名称和流
            var clientMatch = Regex.Match(clientOutput, @"^Client:\s+(.+)$", RegexOptions.Multiline);
            if (clientMatch.Success)
            {
                result.CurrentBranch = clientMatch.Groups[1].Value.Trim();
            }

            var streamMatch = Regex.Match(clientOutput, @"^Stream:\s+(.+)$", RegexOptions.Multiline);
            if (streamMatch.Success)
            {
                result.CurrentBranch = streamMatch.Groups[1].Value.Trim();
            }

            // 从 changes 输出中提取最新变更号
            if (!string.IsNullOrEmpty(changesOutput))
            {
                var changeMatch = Regex.Match(changesOutput, @"^Change\s+(\d+)");
                if (changeMatch.Success)
                {
                    result.CurrentRevision = changeMatch.Groups[1].Value;
                }
            }

            result.RawOutput = $"Client: {result.CurrentBranch}\nLatest Change: {result.CurrentRevision}";
        }

        private List<VcsCommit> ParsePerforceChanges(string output)
        {
            var commits = new List<VcsCommit>();

            if (string.IsNullOrEmpty(output))
                return commits;

            // 格式: Change 12345 on 2024/01/01 by user@client 'Description...'
            var lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            VcsCommit currentCommit = null;

            foreach (var line in lines)
            {
                var match = Regex.Match(line, @"^Change\s+(\d+)\s+on\s+([\d/]+)\s+by\s+([^\s]+)\s+'(.+)'");
                if (match.Success)
                {
                    if (currentCommit != null)
                        commits.Add(currentCommit);

                    currentCommit = new VcsCommit
                    {
                        Revision = match.Groups[1].Value,
                        Date = match.Groups[2].Value,
                        Author = match.Groups[3].Value,
                        Message = match.Groups[4].Value.TrimEnd('\'')
                    };
                }
                else if (currentCommit != null && line.StartsWith("\t"))
                {
                    // 多行描述
                    currentCommit.Message += "\n" + line.Trim();
                }
            }

            if (currentCommit != null)
                commits.Add(currentCommit);

            return commits;
        }

        private void ParsePerforceRemoteInfo(string output, VcsRemoteInfo result)
        {
            var serverMatch = Regex.Match(output, @"^Server address:\s+(.+)$", RegexOptions.Multiline);
            if (serverMatch.Success)
            {
                result.RemoteUrl = serverMatch.Groups[1].Value.Trim();
            }

            var clientMatch = Regex.Match(output, @"^Client name:\s+(.+)$", RegexOptions.Multiline);
            if (clientMatch.Success)
            {
                result.RemoteName = clientMatch.Groups[1].Value.Trim();
            }
        }

        private List<string> ParsePerforceLabels(string output)
        {
            var labels = new List<string>();

            if (string.IsNullOrEmpty(output))
                return labels;

            // 格式: Label label-name 2024/01/01 'Description'
            var lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var match = Regex.Match(line, @"^Label\s+(\S+)");
                if (match.Success)
                {
                    labels.Add(match.Groups[1].Value);
                }
            }

            return labels;
        }

        private List<VcsBlameLine> ParsePerforceAnnotate(string output)
        {
            var lines = new List<VcsBlameLine>();

            if (string.IsNullOrEmpty(output))
                return lines;

            var rawLines = output.Split(new[] { '\n' }, StringSplitOptions.None);
            int lineNumber = 0;

            foreach (var rawLine in rawLines)
            {
                if (string.IsNullOrEmpty(rawLine))
                    continue;

                lineNumber++;

                // p4 annotate -c 格式: <changelist>: <content>
                var match = Regex.Match(rawLine, @"^(\d+):\s(.*)$");
                if (match.Success)
                {
                    lines.Add(new VcsBlameLine
                    {
                        LineNumber = lineNumber,
                        Revision = match.Groups[1].Value,
                        Author = "unknown", // p4 annotate 不直接显示作者
                        Date = "unknown",
                        Content = match.Groups[2].Value
                    });
                }
            }

            return lines;
        }

        private void ParsePerforceClientOutput(string output, VcsPerforceClientInfo result)
        {
            if (string.IsNullOrEmpty(output))
                return;

            var clientMatch = Regex.Match(output, @"^Client:\s+(.+)$", RegexOptions.Multiline);
            if (clientMatch.Success)
                result.ClientName = clientMatch.Groups[1].Value.Trim();

            var ownerMatch = Regex.Match(output, @"^Owner:\s+(.+)$", RegexOptions.Multiline);
            if (ownerMatch.Success)
                result.Owner = ownerMatch.Groups[1].Value.Trim();

            var rootMatch = Regex.Match(output, @"^Root:\s+(.+)$", RegexOptions.Multiline);
            if (rootMatch.Success)
                result.Root = rootMatch.Groups[1].Value.Trim();

            var streamMatch = Regex.Match(output, @"^Stream:\s+(.+)$", RegexOptions.Multiline);
            if (streamMatch.Success)
                result.Stream = streamMatch.Groups[1].Value.Trim();

            var hostMatch = Regex.Match(output, @"^Host:\s+(.+)$", RegexOptions.Multiline);
            if (hostMatch.Success)
                result.Host = hostMatch.Groups[1].Value.Trim();

            var descMatch = Regex.Match(output, @"^Description:\s*\n([\s\S]*?)(?=\n\w+:|\Z)", RegexOptions.Multiline);
            if (descMatch.Success)
                result.Description = descMatch.Groups[1].Value.Trim();

            // 解析 View 映射
            var viewMatch = Regex.Match(output, @"^View:\s*\n([\s\S]*?)(?=\n\w+:|\Z)", RegexOptions.Multiline);
            if (viewMatch.Success)
            {
                result.ViewMappings = viewMatch.Groups[1].Value
                    .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .Where(l => !string.IsNullOrEmpty(l))
                    .ToList();
            }
        }

        private void ParsePerforceDescribe(string output, VcsPerforceChangelist result)
        {
            if (string.IsNullOrEmpty(output))
                return;

            // 格式:
            // Change 12345 by user@client on 2024/01/01 10:30:00 *pending*
            //
            //     Description text
            //
            // Affected files ...
            //
            // ... //depot/path/file.txt#1 edit

            var headerMatch = Regex.Match(output, @"^Change\s+(\d+)\s+by\s+(\S+)\s+on\s+([\d/\s:]+)\s*(\*\w+\*)?");
            if (headerMatch.Success)
            {
                result.ChangeNumber = headerMatch.Groups[1].Value;
                result.User = headerMatch.Groups[2].Value;
                result.Date = headerMatch.Groups[3].Value.Trim();
                result.Status = headerMatch.Groups[4].Value.Trim('*', ' ');
            }

            // 提取描述
            var descMatch = Regex.Match(output, @"\n\n\t(.+?)(?=\n\nAffected files|\Z)", RegexOptions.Singleline);
            if (descMatch.Success)
            {
                result.Description = descMatch.Groups[1].Value.Trim();
            }

            // 提取文件列表
            var filesSection = Regex.Match(output, @"Affected files \.\.\.\n\n([\s\S]+?)$");
            if (filesSection.Success)
            {
                var fileLines = filesSection.Groups[1].Value
                    .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var line in fileLines)
                {
                    var fileMatch = Regex.Match(line, @"\.\.\.\s+(//[^#]+)#\d+\s+\w+");
                    if (fileMatch.Success)
                    {
                        result.Files.Add(fileMatch.Groups[1].Value);
                    }
                }
            }
        }

        private string GetStateDescription(VcsFileState state)
        {
            return state switch
            {
                VcsFileState.Modified => "Checked out for edit",
                VcsFileState.Added => "Marked for add",
                VcsFileState.Deleted => "Marked for delete",
                VcsFileState.Renamed => "Moved/Renamed",
                VcsFileState.Untracked => "Not in depot",
                _ => "Unmodified"
            };
        }
    }
}
