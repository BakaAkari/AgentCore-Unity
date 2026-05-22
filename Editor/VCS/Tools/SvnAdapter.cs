using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace AgentCore.Editor.Components.VCS.Tools
{
    /// <summary>
    /// SVN 适配器
    /// 实现 Subversion 版本控制系统的所有查询和操作
    /// </summary>
    public class SvnAdapter : IVcsAdapter
    {
        private readonly string _workingDirectory;

        public VcsType VcsType => VcsType.Svn;

        public SvnAdapter(string workingDirectory)
        {
            _workingDirectory = workingDirectory;
        }

        public bool IsAvailable()
        {
            return VcsDetector.IsVcsCommandAvailable(VcsType.Svn);
        }

        // ===== Phase 1: 只读查询 =====

        public async Task<VcsStatusResult> GetStatusAsync(CancellationToken ct = default)
        {
            var result = new VcsStatusResult();

            try
            {
                // 使用 XML 输出格式便于解析
                var cmdResult = await VcsCommandExecutor.ExecuteAsync(
                    "svn", "status --xml", _workingDirectory, ct: ct);

                if (!cmdResult.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = cmdResult.ErrorMessage;
                    return result;
                }

                result.RawOutput = cmdResult.Output;
                result.Files = ParseSvnStatusXml(cmdResult.Output);
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to get SVN status: {ex.Message}";
            }

            return result;
        }

        public async Task<VcsBranchInfo> GetBranchInfoAsync(CancellationToken ct = default)
        {
            var result = new VcsBranchInfo();

            try
            {
                // 获取当前 URL
                var infoResult = await VcsCommandExecutor.ExecuteAsync(
                    "svn", "info --xml", _workingDirectory, ct: ct);

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
            var commits = new List<VcsCommit>();

            try
            {
                var cmdResult = await VcsCommandExecutor.ExecuteAsync(
                    "svn", $"log --limit {maxCount} --xml", _workingDirectory, ct: ct);

                if (!cmdResult.Success)
                    return commits;

                commits = ParseSvnLogXml(cmdResult.Output);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"Failed to get SVN log: {ex.Message}");
            }

            return commits;
        }

        public async Task<string> GetDiffAsync(string filePath = null, CancellationToken ct = default)
        {
            try
            {
                var args = string.IsNullOrEmpty(filePath) ? "diff" : $"diff \"{filePath}\"";
                var cmdResult = await VcsCommandExecutor.ExecuteAsync(
                    "svn", args, _workingDirectory, ct: ct);

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
                    "svn", "info --xml", _workingDirectory, ct: ct);

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
                    "svn", "info --xml", _workingDirectory, ct: ct);

                if (!infoResult.Success)
                    return tags;

                var repoRoot = ExtractRepositoryRoot(infoResult.Output);
                if (string.IsNullOrEmpty(repoRoot))
                    return tags;

                var tagsUrl = $"{repoRoot}/tags";
                var listResult = await VcsCommandExecutor.ExecuteAsync(
                    "svn", $"list \"{tagsUrl}\"", _workingDirectory, ct: ct);

                if (listResult.Success && !string.IsNullOrEmpty(listResult.Output))
                {
                    tags = listResult.Output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(t => t.TrimEnd('/'))
                        .Where(t => !string.IsNullOrEmpty(t))
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"Failed to get SVN tags: {ex.Message}");
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
                    "svn", $"blame \"{filePath}\"", _workingDirectory, ct: ct);

                if (!cmdResult.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = cmdResult.ErrorMessage;
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

        /// <summary>
        /// 获取 SVN 工作副本详细信息 (svn info --xml)
        /// </summary>
        public async Task<VcsSvnInfo> GetSvnInfoAsync(string target = null, CancellationToken ct = default)
        {
            var result = new VcsSvnInfo();

            try
            {
                var args = string.IsNullOrEmpty(target) ? "info --xml" : $"info --xml \"{target}\"";
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
                    var cmdResult = await VcsCommandExecutor.ExecuteAsync(
                        "svn", $"add \"{filePath}\"", _workingDirectory, ct: ct);

                    if (cmdResult.Success)
                    {
                        allOutput.Add(cmdResult.Output);
                        affectedFiles.Add(filePath);
                    }
                    else
                    {
                        allOutput.Add($"Failed: {filePath} - {cmdResult.ErrorMessage}");
                    }
                }

                result.Success = affectedFiles.Count > 0;
                result.Message = $"Added {affectedFiles.Count} of {filePaths.Count} file(s) to version control.";
                result.AffectedFiles = affectedFiles;
                result.RawOutput = string.Join("\n", allOutput);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to add files: {ex.Message}";
            }

            return result;
        }

        public async Task<VcsOperationResult> UnstageFilesAsync(List<string> filePaths, CancellationToken ct = default)
        {
            // SVN 没有 staging 概念，unstage 等同于 revert 新添加的文件
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
                    result.ErrorMessage = "Commit message is required.";
                    return result;
                }

                var escapedMessage = message.Replace("\"", "\\\"");
                var cmdResult = await VcsCommandExecutor.ExecuteAsync(
                    "svn", $"commit -m \"{escapedMessage}\"", _workingDirectory, ct: ct);

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
                    "svn", $"revert {files}", _workingDirectory, ct: ct);

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
                    "svn", "update", _workingDirectory, timeoutSeconds: 120, ct: ct);

                if (!cmdResult.Success)
                {
                    result.Success = false;
                    result.ErrorMessage = cmdResult.ErrorMessage;
                    return result;
                }

                result.Success = true;
                result.Message = "Update completed successfully.";
                result.RawOutput = cmdResult.Output;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to update: {ex.Message}";
            }

            return result;
        }

        // ===== 解析方法 =====

        private List<VcsFileStatus> ParseSvnStatusXml(string xmlOutput)
        {
            var files = new List<VcsFileStatus>();

            try
            {
                var doc = XDocument.Parse(xmlOutput);
                var entries = doc.Descendants("entry");

                foreach (var entry in entries)
                {
                    var path = entry.Attribute("path")?.Value;
                    var wcStatus = entry.Element("wc-status");
                    var item = wcStatus?.Attribute("item")?.Value;

                    if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(item))
                        continue;

                    var state = item switch
                    {
                        "modified" => VcsFileState.Modified,
                        "added" => VcsFileState.Added,
                        "deleted" => VcsFileState.Deleted,
                        "unversioned" => VcsFileState.Untracked,
                        "ignored" => VcsFileState.Ignored,
                        "conflicted" => VcsFileState.Conflicted,
                        "missing" => VcsFileState.Missing,
                        _ => VcsFileState.Unmodified
                    };

                    files.Add(new VcsFileStatus
                    {
                        FilePath = path,
                        State = state,
                        StateDescription = GetStateDescription(state)
                    });
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"Failed to parse SVN status XML: {ex.Message}");
            }

            return files;
        }

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
                UnityEngine.Debug.LogWarning($"Failed to parse SVN info: {ex.Message}");
            }
        }

        private List<VcsCommit> ParseSvnLogXml(string xmlOutput)
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

                    commits.Add(new VcsCommit
                    {
                        Revision = revision ?? "unknown",
                        Author = author ?? "unknown",
                        Date = date ?? "unknown",
                        Message = message ?? ""
                    });
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"Failed to parse SVN log XML: {ex.Message}");
            }

            return commits;
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
                UnityEngine.Debug.LogWarning($"Failed to parse SVN remote info: {ex.Message}");
            }
        }

        private List<VcsBlameLine> ParseSvnBlame(string output)
        {
            var lines = new List<VcsBlameLine>();

            if (string.IsNullOrEmpty(output))
                return lines;

            // svn blame 格式: <revision> <author> <content>
            var rawLines = output.Split(new[] { '\n' }, StringSplitOptions.None);
            int lineNumber = 0;

            foreach (var rawLine in rawLines)
            {
                if (string.IsNullOrEmpty(rawLine))
                    continue;

                lineNumber++;

                // 格式: "     5    alice  line content here"
                var match = Regex.Match(rawLine, @"^\s*(\d+)\s+(\S+)\s(.*)$");
                if (match.Success)
                {
                    lines.Add(new VcsBlameLine
                    {
                        LineNumber = lineNumber,
                        Revision = match.Groups[1].Value,
                        Author = match.Groups[2].Value,
                        Date = "unknown",
                        Content = match.Groups[3].Value
                    });
                }
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
                UnityEngine.Debug.LogWarning($"Failed to parse SVN info XML: {ex.Message}");
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

        private string GetStateDescription(VcsFileState state)
        {
            return state switch
            {
                VcsFileState.Modified => "Modified",
                VcsFileState.Added => "Added",
                VcsFileState.Deleted => "Deleted",
                VcsFileState.Renamed => "Renamed",
                VcsFileState.Copied => "Copied",
                VcsFileState.Untracked => "Unversioned",
                VcsFileState.Ignored => "Ignored",
                VcsFileState.Conflicted => "Conflicted",
                VcsFileState.Missing => "Missing",
                _ => "Normal"
            };
        }
    }
}
