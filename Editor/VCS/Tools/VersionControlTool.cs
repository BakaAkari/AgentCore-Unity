using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Components.VCS.Config;
using AgentCore.Editor.Tools;
using AgentCore.Editor.Tools.Infrastructure;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace AgentCore.Editor.Components.VCS.Tools
{
    [AgentTool("version_control",
        Description = "Manage version control system (SVN/Perforce/Git). Supports read-only queries (status, log, diff, blame) and guarded write operations (stage, commit, revert, cleanup, resolve, ignore, remove, branch). Write operations require user confirmation.",
        Category = "VersionControl",
        Visibility = ToolVisibility.OnDemand,
        RequiresMainThread = true,
        MayModifyScripts = false)]
    public class VersionControlTool : IAgentTool
    {
        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""required"": [""action""],
            ""properties"": {
                ""action"": {
                    ""type"": ""string"",
                    ""enum"": [
                        ""detect_vcs"",
                        ""get_status"",
                        ""get_branch"",
                        ""get_log"",
                        ""get_diff"",
                        ""get_remote"",
                        ""get_tags"",
                        ""get_blame"",
                        ""get_sync_status"",
                        ""get_commit_info"",
                        ""get_client_info"",
                        ""get_changelist"",
                        ""get_info"",
                        ""get_file_log"",
                        ""cleanup"",
                        ""stage_files"",
                        ""unstage_files"",
                        ""commit"",
                        ""commit_files"",
                        ""create_branch"",
                        ""switch_branch"",
                        ""stash"",
                        ""stash_pop"",
                        ""checkout_files"",
                        ""revert_files"",
                        ""resolve_files"",
                        ""ignore_file"",
                        ""ignore_folder"",
                        ""ignore_extension"",
                        ""remove_files"",
                        ""submit"",
                        ""sync"",
                        ""update"",
                        ""commit_svn"",
                        ""revert_svn"",
                        ""add_files""
                    ],
                    ""description"": ""Action to perform. Read-only: detect_vcs, get_status, get_branch, get_log, get_file_log, get_diff, get_remote, get_tags, get_blame, get_sync_status, get_commit_info, get_client_info (Perforce), get_changelist (Perforce), get_info (SVN). Write operations (require confirmed=true): cleanup, stage_files, unstage_files, commit, commit_files, create_branch, switch_branch, stash, stash_pop (Git); checkout_files, revert_files, resolve_files, ignore_file, ignore_folder, ignore_extension, remove_files, submit, sync (Perforce); update, commit_svn, revert_svn, add_files (SVN).""
                },
                ""file_path"": {
                    ""type"": ""string"",
                    ""description"": ""File path for get_diff, get_blame, get_info, get_file_log, cleanup, ignore_file, ignore_folder, ignore_extension actions (relative to project root). Omit for repository-wide cleanup.""
                },
                ""file_paths"": {
                    ""type"": ""array"",
                    ""items"": { ""type"": ""string"" },
                    ""description"": ""List of file paths for stage_files, unstage_files, revert_files, checkout_files, add_files, commit_files, resolve_files, remove_files actions""
                },
                ""max_count"": {
                    ""type"": ""integer"",
                    ""description"": ""Maximum number of log entries to retrieve (default: 20, max: 100)"",
                    ""minimum"": 1,
                    ""maximum"": 100
                },
                ""revision"": {
                    ""type"": ""string"",
                    ""description"": ""Commit hash/revision number for get_commit_info action""
                },
                ""message"": {
                    ""type"": ""string"",
                    ""description"": ""Commit/submit message for commit, commit_files, commit_svn, submit, stash actions""
                },
                ""ignore_pattern"": {
                    ""type"": ""string"",
                    ""description"": ""Optional explicit ignore pattern for ignore_file, ignore_folder, ignore_extension. If omitted, the pattern is derived from file_path.""
                },
                ""branch_name"": {
                    ""type"": ""string"",
                    ""description"": ""Branch name for create_branch, switch_branch actions""
                },
                ""change_number"": {
                    ""type"": ""string"",
                    ""description"": ""Perforce changelist number for get_changelist action""
                },
                ""confirmed"": {
                    ""type"": ""boolean"",
                    ""description"": ""Must be true to execute write operations. First call without confirmed returns a preview; second call with confirmed=true executes the operation.""
                }
            }
        }");

        public ToolMetadata Metadata => new ToolMetadata(
            name: "version_control",
            description: "Manage version control system (SVN/Perforce/Git). Supports read-only queries (status, log, diff, blame) and guarded write operations (stage, commit, revert, cleanup, resolve, ignore, remove, branch). Write operations require user confirmation.",
            category: "VersionControl",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        // 操作类 actions 列表（需要确认）
        private static readonly HashSet<string> _operationActions = new HashSet<string>
        {
            "cleanup", "stage_files", "unstage_files", "commit", "commit_files",
            "create_branch", "switch_branch", "stash", "stash_pop",
            "checkout_files", "revert_files", "resolve_files", "ignore_file", "ignore_folder",
            "ignore_extension", "remove_files", "submit", "sync",
            "update", "commit_svn", "revert_svn", "add_files"
        };

        public async Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ToolResponse response;

            try
            {
                var action = ToolHelpers.GetRequiredString(parameters, "action").ToLowerInvariant();

                // 操作类 action 需要确认机制
                if (_operationActions.Contains(action))
                {
                    var confirmed = ToolHelpers.GetOptionalBool(parameters, "confirmed", false);
                    if (!confirmed)
                    {
                        response = BuildConfirmationResponse(action, parameters);
                        sw.Stop();
                        return response.ToToolResult(sw.Elapsed.TotalMilliseconds);
                    }
                }

                response = action switch
                {
                    // Phase 1: 只读查询
                    "detect_vcs" => await HandleDetectVcs(cancellationToken),
                    "get_status" => await HandleGetStatus(cancellationToken),
                    "get_branch" => await HandleGetBranch(cancellationToken),
                    "get_log" => await HandleGetLog(parameters, cancellationToken),
                    "get_file_log" => await HandleGetFileLog(parameters, cancellationToken),
                    "get_diff" => await HandleGetDiff(parameters, cancellationToken),
                    "get_remote" => await HandleGetRemote(cancellationToken),
                    "get_tags" => await HandleGetTags(cancellationToken),
                    "get_blame" => await HandleGetBlame(parameters, cancellationToken),
                    "get_sync_status" => await HandleGetSyncStatus(cancellationToken),
                    "get_commit_info" => await HandleGetCommitInfo(parameters, cancellationToken),
                    "get_client_info" => await HandleGetClientInfo(cancellationToken),
                    "get_changelist" => await HandleGetChangelist(parameters, cancellationToken),
                    "get_info" => await HandleGetInfo(parameters, cancellationToken),

                    // Phase 2: 通用操作
                    "cleanup" => await HandleCleanup(parameters, cancellationToken),
                    "stage_files" => await HandleStageFiles(parameters, cancellationToken),
                    "unstage_files" => await HandleUnstageFiles(parameters, cancellationToken),
                    "commit" => await HandleCommit(parameters, cancellationToken),
                    "commit_files" => await HandleCommitFiles(parameters, cancellationToken),
                    "resolve_files" => await HandleResolveFiles(parameters, cancellationToken),
                    "ignore_file" => await HandleIgnore(parameters, "file", cancellationToken),
                    "ignore_folder" => await HandleIgnore(parameters, "folder", cancellationToken),
                    "ignore_extension" => await HandleIgnore(parameters, "extension", cancellationToken),
                    "remove_files" => await HandleRemoveFiles(parameters, cancellationToken),

                    // Phase 2: Git 操作
                    "create_branch" => await HandleCreateBranch(parameters, cancellationToken),
                    "switch_branch" => await HandleSwitchBranch(parameters, cancellationToken),
                    "stash" => await HandleStash(parameters, cancellationToken),
                    "stash_pop" => await HandleStashPop(cancellationToken),

                    // Phase 2: Perforce 操作
                    "checkout_files" => await HandleStageFiles(parameters, cancellationToken),
                    "revert_files" => await HandleRevertFiles(parameters, cancellationToken),
                    "submit" => await HandleCommit(parameters, cancellationToken),
                    "sync" => await HandleSync(cancellationToken),

                    // Phase 2: SVN 操作
                    "update" => await HandleSync(cancellationToken),
                    "commit_svn" => await HandleCommit(parameters, cancellationToken),
                    "revert_svn" => await HandleRevertFiles(parameters, cancellationToken),
                    "add_files" => await HandleStageFiles(parameters, cancellationToken),

                    _ => ToolResponse.Fail($"Unknown action: {action}. Valid actions: {string.Join(", ", GetValidActions())}")
                };
            }
            catch (Exception ex)
            {
                response = ToolResponse.Fail($"Error executing version_control tool: {ex.Message}");
            }

            sw.Stop();
            return response.ToToolResult(sw.Elapsed.TotalMilliseconds);
        }

        // ===== 确认机制 =====

        private ToolResponse BuildConfirmationResponse(string action, JObject parameters)
        {
            var adapter = GetAdapter();
            var vcsType = adapter?.VcsType.ToString() ?? "Unknown";

            var description = action switch
            {
                "cleanup" => "Cleanup the repository working copy metadata/locks",
                "stage_files" or "add_files" or "checkout_files" => "Stage/add/check out files to version control",
                "unstage_files" => "Unstage files from staging area",
                "commit" or "commit_svn" or "submit" => "Commit/submit changes to repository",
                "commit_files" => "Commit only the specified files to repository",
                "create_branch" => "Create a new branch",
                "switch_branch" => "Switch to a different branch (may affect working directory)",
                "stash" => "Stash current working changes",
                "stash_pop" => "Pop stashed changes back to working directory",
                "revert_files" or "revert_svn" => "Revert files to repository version (DESTRUCTIVE: local changes will be lost)",
                "resolve_files" => "Mark conflicted files as resolved after conflicts have been manually fixed",
                "ignore_file" or "ignore_folder" or "ignore_extension" => "Add an ignore rule to version control ignore metadata",
                "remove_files" => "Remove/schedule delete files from the working copy (DESTRUCTIVE)",
                "sync" or "update" => "Sync/update working copy to latest version",
                _ => $"Execute {action}"
            };

            var warning = action switch
            {
                "revert_files" or "revert_svn" => " WARNING: This will permanently discard local changes to the specified files!",
                "remove_files" => " WARNING: This may delete files from disk or schedule them for deletion in version control.",
                "cleanup" => " WARNING: Cleanup may alter VCS working-copy metadata/locks. Do not run while Unity is compiling or importing assets.",
                "resolve_files" => " WARNING: This only marks conflicts as resolved; ensure conflict markers have already been fixed.",
                "ignore_file" or "ignore_folder" or "ignore_extension" => " WARNING: This modifies ignore metadata and can hide matching untracked files from status.",
                "commit_files" => " WARNING: Only the specified files will be submitted/committed; verify the file list before confirming.",
                "switch_branch" => " WARNING: Switching branches may modify files in your working directory.",
                "stash_pop" => " WARNING: This may cause merge conflicts if working directory has changes.",
                _ => "This operation will modify your version control state."
            };

            var data = new
            {
                requires_confirmation = true,
                action = action,
                vcs_type = vcsType,
                description = description,
                warning = warning,
                instruction = "To execute this operation, call again with confirmed=true"
            };

            return ToolResponse.OkWithData(data, $"Operation '{action}' requires confirmation. {description}. Call again with confirmed=true to proceed.");
        }

        // ===== Phase 1: 只读查询处理器 =====

        private Task<ToolResponse> HandleDetectVcs(CancellationToken ct)
        {
            try
            {
                var vcsType = VcsDetector.DetectVcs();
                var rootPath = VcsDetector.GetVcsRootPath();

                if (vcsType == VcsType.None)
                {
                    return Task.FromResult(ToolResponse.Fail("No version control system detected in the current project."));
                }

                var adapter = CreateAdapter(vcsType);
                var isAvailable = adapter?.IsAvailable() ?? false;

                var data = new
                {
                    vcs_type = vcsType.ToString(),
                    root_path = rootPath,
                    command_available = isAvailable,
                    priority_order = "SVN > Perforce > Git"
                };

                return Task.FromResult(ToolResponse.OkWithData(data, $"Detected {vcsType} at {rootPath}. Command available: {isAvailable}"));
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResponse.Fail($"Failed to detect VCS: {ex.Message}"));
            }
        }

        private async Task<ToolResponse> HandleGetStatus(CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail("No version control system detected or command not available.");

            var result = await adapter.GetStatusAsync(ct);

            if (!result.Success)
                return ToolResponse.Fail($"Failed to get status: {result.ErrorMessage}");

            var data = new
            {
                vcs_type = adapter.VcsType.ToString(),
                total_files = result.Files.Count,
                files = result.Files.Select(f => new
                {
                    path = f.FilePath,
                    state = f.State.ToString(),
                    description = f.StateDescription
                }).ToList(),
                summary = BuildStatusSummary(result.Files)
            };

            return ToolResponse.OkWithData(data, $"Found {result.Files.Count} changed files in working copy.");
        }

        private async Task<ToolResponse> HandleGetBranch(CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail("No version control system detected or command not available.");

            var result = await adapter.GetBranchInfoAsync(ct);

            if (!result.Success)
                return ToolResponse.Fail($"Failed to get branch info: {result.ErrorMessage}");

            var data = new
            {
                vcs_type = adapter.VcsType.ToString(),
                current_branch = result.CurrentBranch,
                current_revision = result.CurrentRevision,
                all_branches = result.AllBranches,
                branch_count = result.AllBranches?.Count ?? 0
            };

            return ToolResponse.OkWithData(data, $"Current branch: {result.CurrentBranch}, Revision: {result.CurrentRevision}");
        }

        private async Task<ToolResponse> HandleGetLog(JObject parameters, CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail("No version control system detected or command not available.");

            var maxCount = ToolHelpers.GetOptionalInt(parameters, "max_count", VcsSettings.MaxCommitEntries);
            if (maxCount > 100) maxCount = 100;
            if (maxCount < 1) maxCount = 1;

            var commits = await adapter.GetLogAsync(maxCount, ct);

            var data = new
            {
                vcs_type = adapter.VcsType.ToString(),
                commit_count = commits.Count,
                commits = commits.Select(c => new
                {
                    revision = c.Revision,
                    author = c.Author,
                    date = c.Date,
                    message = c.Message.Length > 100 ? c.Message.Substring(0, 100) + "..." : c.Message,
                    full_message = c.Message
                }).ToList()
            };

            return ToolResponse.OkWithData(data, $"Retrieved {commits.Count} commit(s) from history.");
        }

        private async Task<ToolResponse> HandleGetFileLog(JObject parameters, CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail("No version control system detected or command not available.");

            var filePath = ToolHelpers.GetOptionalString(parameters, "file_path");
            if (string.IsNullOrEmpty(filePath))
                return ToolResponse.Fail("file_path is required for get_file_log action.");

            var maxCount = ToolHelpers.GetOptionalInt(parameters, "max_count", VcsSettings.MaxCommitEntries);
            if (maxCount > 100) maxCount = 100;
            if (maxCount < 1) maxCount = 1;

            var result = await ExecutePathLogAsync(adapter, filePath, maxCount, ct);
            if (!result.Success)
                return ToolResponse.Fail($"Failed to get file log: {result.ErrorMessage}");

            var output = Truncate(result.Output, 10000, out var truncated);
            var data = new
            {
                vcs_type = adapter.VcsType.ToString(),
                file_path = filePath,
                max_count = maxCount,
                log_output = output,
                truncated = truncated
            };

            var message = $"Retrieved history for {filePath}.";
            if (truncated)
                message += " (Output truncated to 10000 characters)";

            return ToolResponse.OkWithData(data, message);
        }

        private async Task<ToolResponse> HandleGetDiff(JObject parameters, CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail("No version control system detected or command not available.");

            var filePath = ToolHelpers.GetOptionalString(parameters, "file_path");
            var diff = await adapter.GetDiffAsync(filePath, ct);

            if (string.IsNullOrEmpty(diff))
            {
                return ToolResponse.Ok("No differences found.");
            }

            // 限制 diff 输出长度
            var truncated = false;
            if (diff.Length > 10000)
            {
                diff = diff.Substring(0, 10000);
                truncated = true;
            }

            var data = new
            {
                vcs_type = adapter.VcsType.ToString(),
                file_path = filePath ?? "all files",
                diff_content = diff,
                truncated = truncated,
                length = diff.Length
            };

            var message = string.IsNullOrEmpty(filePath)
                ? "Retrieved diff for all changed files."
                : $"Retrieved diff for {filePath}.";

            if (truncated)
                message += " (Output truncated to 10000 characters)";

            return ToolResponse.OkWithData(data, message);
        }

        private async Task<ToolResponse> HandleGetRemote(CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail("No version control system detected or command not available.");

            var result = await adapter.GetRemoteInfoAsync(ct);

            if (!result.Success)
                return ToolResponse.Fail($"Failed to get remote info: {result.ErrorMessage}");

            var data = new
            {
                vcs_type = adapter.VcsType.ToString(),
                remote_name = result.RemoteName,
                remote_url = result.RemoteUrl
            };

            return ToolResponse.OkWithData(data, $"Remote: {result.RemoteName} ({result.RemoteUrl})");
        }

        private async Task<ToolResponse> HandleGetTags(CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail("No version control system detected or command not available.");

            var tags = await adapter.GetTagsAsync(ct);

            var data = new
            {
                vcs_type = adapter.VcsType.ToString(),
                tag_count = tags.Count,
                tags = tags
            };

            return ToolResponse.OkWithData(data, $"Found {tags.Count} tag(s)/label(s).");
        }

        private async Task<ToolResponse> HandleGetSyncStatus(CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail("No version control system detected or command not available.");

            var result = await adapter.GetSyncStatusAsync(ct);
            if (!result.Success)
                return ToolResponse.Fail($"Failed to get sync status: {result.ErrorMessage}");

            var data = new
            {
                vcs_type = adapter.VcsType.ToString(),
                has_remote_changes = result.HasRemoteChanges,
                has_local_changes = result.HasLocalChanges,
                has_conflicts = result.HasConflicts,
                remote_change_count = result.RemoteChangeCount,
                local_change_count = result.LocalChangeCount,
                ahead_count = result.AheadCount,
                behind_count = result.BehindCount,
                remote_changed_files = result.RemoteChangedFiles,
                conflicted_files = result.ConflictedFiles,
                summary = result.Summary
            };

            return ToolResponse.OkWithData(data, result.Summary ?? "Retrieved VCS sync status.");
        }

        private async Task<ToolResponse> HandleGetBlame(JObject parameters, CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail("No version control system detected or command not available.");

            var filePath = ToolHelpers.GetOptionalString(parameters, "file_path");
            if (string.IsNullOrEmpty(filePath))
                return ToolResponse.Fail("file_path is required for get_blame action.");

            var result = await adapter.GetBlameAsync(filePath, ct);

            if (!result.Success)
                return ToolResponse.Fail($"Failed to get blame: {result.ErrorMessage}");

            // 限制输出行数
            var maxLines = 100;
            var truncated = result.Lines.Count > maxLines;
            var displayLines = truncated ? result.Lines.Take(maxLines).ToList() : result.Lines;

            var data = new
            {
                vcs_type = adapter.VcsType.ToString(),
                file_path = filePath,
                total_lines = result.Lines.Count,
                truncated = truncated,
                lines = displayLines.Select(l => new
                {
                    line = l.LineNumber,
                    revision = l.Revision,
                    author = l.Author,
                    date = l.Date,
                    content = l.Content.Length > 120 ? l.Content.Substring(0, 120) + "..." : l.Content
                }).ToList()
            };

            var message = $"Blame for {filePath}: {result.Lines.Count} lines.";
            if (truncated)
                message += $" (Showing first {maxLines} lines)";

            return ToolResponse.OkWithData(data, message);
        }

        private async Task<ToolResponse> HandleGetCommitInfo(JObject parameters, CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail("No version control system detected or command not available.");

            var revision = ToolHelpers.GetOptionalString(parameters, "revision");
            if (string.IsNullOrEmpty(revision))
                return ToolResponse.Fail("revision is required for get_commit_info action.");

            // Git-specific: git show
            if (adapter is GitAdapter gitAdapter)
            {
                var result = await gitAdapter.GetCommitInfoAsync(revision, ct);

                if (!result.Success)
                    return ToolResponse.Fail($"Failed to get commit info: {result.ErrorMessage}");

                var data = new
                {
                    vcs_type = "Git",
                    revision = result.Revision,
                    author = result.Author,
                    date = result.Date,
                    message = result.Message,
                    changed_files = result.ChangedFiles,
                    changed_files_count = result.ChangedFiles.Count
                };

                return ToolResponse.OkWithData(data, $"Commit {revision}: {result.Message}");
            }

            // Perforce: p4 describe
            if (adapter is PerforceAdapter p4Adapter)
            {
                var result = await p4Adapter.GetChangelistAsync(revision, ct);

                if (!result.Success)
                    return ToolResponse.Fail($"Failed to get changelist info: {result.ErrorMessage}");

                var data = new
                {
                    vcs_type = "Perforce",
                    change_number = result.ChangeNumber,
                    user = result.User,
                    date = result.Date,
                    status = result.Status,
                    description = result.Description,
                    files = result.Files,
                    files_count = result.Files.Count
                };

                return ToolResponse.OkWithData(data, $"Change {result.ChangeNumber}: {result.Description}");
            }

            // SVN: svn log -r <revision>
            return ToolResponse.Fail($"get_commit_info is not supported for {adapter.VcsType}. Use get_log instead.");
        }

        private async Task<ToolResponse> HandleGetClientInfo(CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail("No version control system detected or command not available.");

            if (adapter is not PerforceAdapter p4Adapter)
                return ToolResponse.Fail("get_client_info is only available for Perforce. Current VCS: " + adapter.VcsType);

            var result = await p4Adapter.GetClientInfoAsync(ct);

            if (!result.Success)
                return ToolResponse.Fail($"Failed to get client info: {result.ErrorMessage}");

            var data = new
            {
                vcs_type = "Perforce",
                client_name = result.ClientName,
                owner = result.Owner,
                root = result.Root,
                stream = result.Stream,
                host = result.Host,
                description = result.Description,
                view_mappings = result.ViewMappings
            };

            return ToolResponse.OkWithData(data, $"Perforce client: {result.ClientName} (Owner: {result.Owner})");
        }

        private async Task<ToolResponse> HandleGetChangelist(JObject parameters, CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail("No version control system detected or command not available.");

            if (adapter is not PerforceAdapter p4Adapter)
                return ToolResponse.Fail("get_changelist is only available for Perforce. Current VCS: " + adapter.VcsType);

            var changeNumber = ToolHelpers.GetOptionalString(parameters, "change_number");
            var result = await p4Adapter.GetChangelistAsync(changeNumber, ct);

            if (!result.Success)
                return ToolResponse.Fail($"Failed to get changelist: {result.ErrorMessage}");

            var data = new
            {
                vcs_type = "Perforce",
                change_number = result.ChangeNumber,
                status = result.Status,
                user = result.User,
                date = result.Date,
                description = result.Description,
                files = result.Files,
                files_count = result.Files.Count
            };

            return ToolResponse.OkWithData(data, $"Changelist {result.ChangeNumber}: {result.Description} ({result.Files.Count} files)");
        }

        private async Task<ToolResponse> HandleGetInfo(JObject parameters, CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail("No version control system detected or command not available.");

            if (adapter is not SvnAdapter svnAdapter)
                return ToolResponse.Fail("get_info is only available for SVN. Current VCS: " + adapter.VcsType);

            var target = ToolHelpers.GetOptionalString(parameters, "file_path");
            var result = await svnAdapter.GetSvnInfoAsync(target, ct);

            if (!result.Success)
            {
                // W155010 表示路径不在 SVN 工作副本中（Untracked/未纳入版本控制）
                var errMsg = result.ErrorMessage ?? "";
                if (errMsg.Contains("W155010") || errMsg.Contains("is not a working copy") ||
                    errMsg.Contains("node not found"))
                {
                    var pathHint = string.IsNullOrEmpty(target) ? "the working directory" : $"'{target}'";
                    return ToolResponse.Fail(
                        $"SVN info failed: {pathHint} is not under version control (not tracked by SVN). " +
                        $"Use 'add_files' to schedule it for addition first. Raw error: {errMsg}");
                }
                return ToolResponse.Fail($"Failed to get SVN info: {errMsg}");
            }

            var data = new
            {
                vcs_type = "SVN",
                url = result.Url,
                repository_root = result.RepositoryRoot,
                revision = result.Revision,
                node_kind = result.NodeKind,
                last_changed_author = result.LastChangedAuthor,
                last_changed_revision = result.LastChangedRevision,
                last_changed_date = result.LastChangedDate
            };

            return ToolResponse.OkWithData(data, $"SVN info: {result.Url} (r{result.Revision})");
        }

        // ===== Phase 2: 操作类处理器 =====

        private async Task<ToolResponse> HandleCleanup(JObject parameters, CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail("No version control system detected or command not available.");

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return ToolResponse.Fail("Cleanup is blocked while Unity is compiling or importing assets. Please wait until Unity is idle.");

            var target = ToolHelpers.GetOptionalString(parameters, "file_path");
            var result = await ExecuteCleanupAsync(adapter, target, ct);
            if (!result.Success)
                return ToolResponse.Fail($"Failed to cleanup working copy: {result.ErrorMessage}");

            var data = BuildOperationData(adapter, result);
            return ToolResponse.OkWithData(data, result.Message);
        }

        private async Task<ToolResponse> HandleStageFiles(JObject parameters, CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail("No version control system detected or command not available.");

            var filePaths = GetFilePathsList(parameters);

            var result = await adapter.StageFilesAsync(filePaths, ct);

            if (!result.Success)
                return ToolResponse.Fail($"Failed to stage files: {result.ErrorMessage}");

            var data = new
            {
                vcs_type = adapter.VcsType.ToString(),
                affected_files = result.AffectedFiles,
                affected_count = result.AffectedFiles.Count,
                message = result.Message
            };

            return ToolResponse.OkWithData(data, result.Message);
        }

        private async Task<ToolResponse> HandleUnstageFiles(JObject parameters, CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail("No version control system detected or command not available.");

            var filePaths = GetFilePathsList(parameters);

            var result = await adapter.UnstageFilesAsync(filePaths, ct);

            if (!result.Success)
                return ToolResponse.Fail($"Failed to unstage files: {result.ErrorMessage}");

            var data = new
            {
                vcs_type = adapter.VcsType.ToString(),
                affected_files = result.AffectedFiles,
                affected_count = result.AffectedFiles.Count,
                message = result.Message
            };

            return ToolResponse.OkWithData(data, result.Message);
        }

        private async Task<ToolResponse> HandleCommit(JObject parameters, CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail("No version control system detected or command not available.");

            var message = ToolHelpers.GetOptionalString(parameters, "message");
            if (string.IsNullOrEmpty(message))
                return ToolResponse.Fail("message is required for commit/submit action.");

            var result = await adapter.CommitAsync(message, ct);

            if (!result.Success)
                return ToolResponse.Fail($"Failed to commit: {result.ErrorMessage}");

            var data = BuildOperationData(adapter, result);
            return ToolResponse.OkWithData(data, result.Message);
        }

        private async Task<ToolResponse> HandleCommitFiles(JObject parameters, CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail("No version control system detected or command not available.");

            var message = ToolHelpers.GetOptionalString(parameters, "message");
            if (string.IsNullOrEmpty(message))
                return ToolResponse.Fail("message is required for commit_files action.");

            var filePaths = GetFilePathsList(parameters);
            if (filePaths == null || filePaths.Count == 0)
                return ToolResponse.Fail("file_paths is required for commit_files action.");

            var result = await ExecuteCommitFilesAsync(adapter, message, filePaths, ct);
            if (!result.Success)
                return ToolResponse.Fail($"Failed to commit files: {result.ErrorMessage}");

            var data = BuildOperationData(adapter, result);
            return ToolResponse.OkWithData(data, result.Message);
        }

        private async Task<ToolResponse> HandleCreateBranch(JObject parameters, CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail("No version control system detected or command not available.");

            if (adapter is not GitAdapter gitAdapter)
                return ToolResponse.Fail("create_branch is only available for Git. Current VCS: " + adapter.VcsType);

            var branchName = ToolHelpers.GetOptionalString(parameters, "branch_name");
            if (string.IsNullOrEmpty(branchName))
                return ToolResponse.Fail("branch_name is required for create_branch action.");

            var result = await gitAdapter.CreateBranchAsync(branchName, ct);

            if (!result.Success)
                return ToolResponse.Fail($"Failed to create branch: {result.ErrorMessage}");

            var data = new
            {
                vcs_type = "Git",
                branch_name = branchName,
                message = result.Message
            };

            return ToolResponse.OkWithData(data, result.Message);
        }

        private async Task<ToolResponse> HandleSwitchBranch(JObject parameters, CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail("No version control system detected or command not available.");

            if (adapter is not GitAdapter gitAdapter)
                return ToolResponse.Fail("switch_branch is only available for Git. Current VCS: " + adapter.VcsType);

            var branchName = ToolHelpers.GetOptionalString(parameters, "branch_name");
            if (string.IsNullOrEmpty(branchName))
                return ToolResponse.Fail("branch_name is required for switch_branch action.");

            var result = await gitAdapter.SwitchBranchAsync(branchName, ct);

            if (!result.Success)
                return ToolResponse.Fail($"Failed to switch branch: {result.ErrorMessage}");

            var data = new
            {
                vcs_type = "Git",
                branch_name = branchName,
                message = result.Message
            };

            return ToolResponse.OkWithData(data, result.Message);
        }

        private async Task<ToolResponse> HandleStash(JObject parameters, CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail("No version control system detected or command not available.");

            if (adapter is not GitAdapter gitAdapter)
                return ToolResponse.Fail("stash is only available for Git. Current VCS: " + adapter.VcsType);

            var message = ToolHelpers.GetOptionalString(parameters, "message");
            var result = await gitAdapter.StashAsync(message, ct);

            if (!result.Success)
                return ToolResponse.Fail($"Failed to stash: {result.ErrorMessage}");

            var data = new
            {
                vcs_type = "Git",
                message = result.Message
            };

            return ToolResponse.OkWithData(data, result.Message);
        }

        private async Task<ToolResponse> HandleStashPop(CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail("No version control system detected or command not available.");

            if (adapter is not GitAdapter gitAdapter)
                return ToolResponse.Fail("stash_pop is only available for Git. Current VCS: " + adapter.VcsType);

            var result = await gitAdapter.StashPopAsync(ct);

            if (!result.Success)
                return ToolResponse.Fail($"Failed to pop stash: {result.ErrorMessage}");

            var data = new
            {
                vcs_type = "Git",
                message = result.Message
            };

            return ToolResponse.OkWithData(data, result.Message);
        }

        private async Task<ToolResponse> HandleRevertFiles(JObject parameters, CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail("No version control system detected or command not available.");

            var filePaths = GetFilePathsList(parameters);
            if (filePaths == null || filePaths.Count == 0)
                return ToolResponse.Fail("file_paths is required for revert action.");

            var result = await adapter.RevertFilesAsync(filePaths, ct);

            if (!result.Success)
                return ToolResponse.Fail($"Failed to revert files: {result.ErrorMessage}");

            var data = new
            {
                vcs_type = adapter.VcsType.ToString(),
                affected_files = result.AffectedFiles,
                affected_count = result.AffectedFiles.Count,
                message = result.Message
            };

            return ToolResponse.OkWithData(data, result.Message);
        }

        private async Task<ToolResponse> HandleResolveFiles(JObject parameters, CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail("No version control system detected or command not available.");

            var filePaths = GetFilePathsList(parameters);
            if (filePaths == null || filePaths.Count == 0)
                return ToolResponse.Fail("file_paths is required for resolve_files action.");

            var result = await ExecuteResolveFilesAsync(adapter, filePaths, ct);
            if (!result.Success)
                return ToolResponse.Fail($"Failed to resolve files: {result.ErrorMessage}");

            var data = BuildOperationData(adapter, result);
            return ToolResponse.OkWithData(data, result.Message);
        }

        private async Task<ToolResponse> HandleIgnore(JObject parameters, string mode, CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail("No version control system detected or command not available.");

            var filePath = ToolHelpers.GetOptionalString(parameters, "file_path");
            var explicitPattern = ToolHelpers.GetOptionalString(parameters, "ignore_pattern");
            if (string.IsNullOrEmpty(filePath) && string.IsNullOrEmpty(explicitPattern))
                return ToolResponse.Fail("file_path or ignore_pattern is required for ignore actions.");

            var result = await ExecuteIgnoreAsync(adapter, filePath, explicitPattern, mode, ct);
            if (!result.Success)
                return ToolResponse.Fail($"Failed to update ignore rules: {result.ErrorMessage}");

            var data = BuildOperationData(adapter, result);
            return ToolResponse.OkWithData(data, result.Message);
        }

        private async Task<ToolResponse> HandleRemoveFiles(JObject parameters, CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail("No version control system detected or command not available.");

            var filePaths = GetFilePathsList(parameters);
            if (filePaths == null || filePaths.Count == 0)
                return ToolResponse.Fail("file_paths is required for remove_files action.");

            var result = await ExecuteRemoveFilesAsync(adapter, filePaths, ct);
            if (!result.Success)
                return ToolResponse.Fail($"Failed to remove files: {result.ErrorMessage}");

            var data = BuildOperationData(adapter, result);
            return ToolResponse.OkWithData(data, result.Message);
        }

        private async Task<ToolResponse> HandleSync(CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail("No version control system detected or command not available.");

            var result = await adapter.SyncAsync(ct);

            if (!result.Success)
                return ToolResponse.Fail($"Failed to sync/update: {result.ErrorMessage}");

            var data = BuildOperationData(adapter, result, new Dictionary<string, object>
            {
                { "conflicted_files", result.ConflictedFiles }
            });

            return ToolResponse.OkWithData(data, result.Message);
        }

        // ===== 辅助方法 =====

        private IVcsAdapter GetAdapter()
        {
            var vcsType = VcsDetector.DetectVcs();
            if (vcsType == VcsType.None)
                return null;

            var adapter = CreateAdapter(vcsType);
            if (adapter == null || !adapter.IsAvailable())
                return null;

            return adapter;
        }

        private IVcsAdapter CreateAdapter(VcsType vcsType)
        {
            var rootPath = VcsDetector.GetVcsRootPath();

            return vcsType switch
            {
                VcsType.Svn => new SvnAdapter(rootPath),
                VcsType.Perforce => new PerforceAdapter(rootPath),
                VcsType.Git => new GitAdapter(rootPath),
                _ => null
            };
        }

        private Dictionary<string, int> BuildStatusSummary(List<VcsFileStatus> files)
        {
            var summary = new Dictionary<string, int>();

            foreach (var file in files)
            {
                var state = file.State.ToString();
                if (summary.ContainsKey(state))
                    summary[state]++;
                else
                    summary[state] = 1;
            }

            return summary;
        }

        private List<string> GetFilePathsList(JObject parameters)
        {
            var filePaths = new List<string>();

            // 尝试从 file_paths 数组获取
            var filePathsToken = parameters["file_paths"];
            if (filePathsToken is JArray filePathsArray)
            {
                filePaths = filePathsArray.Select(t => t.ToString()).ToList();
            }

            // 如果 file_paths 为空，尝试从 file_path 单个值获取
            if (filePaths.Count == 0)
            {
                var singlePath = ToolHelpers.GetOptionalString(parameters, "file_path");
                if (!string.IsNullOrEmpty(singlePath))
                {
                    filePaths.Add(singlePath);
                }
            }

            return filePaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static IEnumerable<string> GetValidActions()
        {
            return _parametersSchema["properties"]?["action"]?["enum"]?.Select(token => token.ToString()) ?? Enumerable.Empty<string>();
        }

        private static Dictionary<string, object> BuildOperationData(IVcsAdapter adapter, VcsOperationResult result, Dictionary<string, object> extra = null)
        {
            var data = new Dictionary<string, object>
            {
                { "vcs_type", adapter.VcsType.ToString() },
                { "message", result.Message },
                { "affected_files", result.AffectedFiles },
                { "affected_count", result.AffectedFiles?.Count ?? 0 },
                { "raw_output", Truncate(result.RawOutput, 1000, out var rawOutputTruncated) },
                { "raw_output_truncated", rawOutputTruncated }
            };

            if (extra != null)
            {
                foreach (var pair in extra)
                    data[pair.Key] = pair.Value;
            }

            return data;
        }

        private async Task<CommandResult> ExecutePathLogAsync(IVcsAdapter adapter, string filePath, int maxCount, CancellationToken ct)
        {
            switch (adapter.VcsType)
            {
                case VcsType.Git:
                    return await VcsCommandExecutor.ExecuteAsync("git", $"log -{maxCount} --follow -- {Quote(filePath)}", VcsDetector.GetVcsRootPath(), timeoutSeconds: 60, ct: ct);
                case VcsType.Svn:
                    return await VcsCommandExecutor.ExecuteAsync("svn", $"log -l {maxCount} {Quote(filePath)}", VcsDetector.GetVcsRootPath(), timeoutSeconds: 60, ct: ct);
                case VcsType.Perforce:
                    return await VcsCommandExecutor.ExecuteAsync("p4", $"filelog -m {maxCount} {Quote(filePath)}", VcsDetector.GetVcsRootPath(), timeoutSeconds: 60, ct: ct);
                default:
                    return CommandFailure($"get_file_log is not supported for {adapter.VcsType}.");
            }
        }

        private async Task<VcsOperationResult> ExecuteCleanupAsync(IVcsAdapter adapter, string target, CancellationToken ct)
        {
            var rootPath = VcsDetector.GetVcsRootPath();
            var cleanupTarget = string.IsNullOrWhiteSpace(target) ? rootPath : target;
            CommandResult commandResult;

            switch (adapter.VcsType)
            {
                case VcsType.Svn:
                    commandResult = await VcsCommandExecutor.ExecuteAsync("svn", $"cleanup {Quote(cleanupTarget)}", rootPath, timeoutSeconds: 120, ct: ct);
                    break;
                case VcsType.Git:
                    commandResult = await VcsCommandExecutor.ExecuteAsync("git", "gc --auto", rootPath, timeoutSeconds: 120, ct: ct);
                    break;
                case VcsType.Perforce:
                    commandResult = await VcsCommandExecutor.ExecuteAsync("p4", "reconcile -n", rootPath, timeoutSeconds: 120, ct: ct);
                    break;
                default:
                    return OperationFailure($"cleanup is not supported for {adapter.VcsType}.");
            }

            return OperationFromCommand(commandResult, $"Cleanup completed for {adapter.VcsType}.", new List<string> { cleanupTarget });
        }

        private async Task<VcsOperationResult> ExecuteCommitFilesAsync(IVcsAdapter adapter, string message, List<string> filePaths, CancellationToken ct)
        {
            var rootPath = VcsDetector.GetVcsRootPath();
            var escapedMessage = EscapeMessage(message);
            var files = JoinQuoted(filePaths);
            CommandResult commandResult;

            switch (adapter.VcsType)
            {
                case VcsType.Git:
                    commandResult = await VcsCommandExecutor.ExecuteAsync("git", $"commit -m {Quote(escapedMessage)} -- {files}", rootPath, timeoutSeconds: 120, ct: ct);
                    break;
                case VcsType.Svn:
                    commandResult = await VcsCommandExecutor.ExecuteAsync("svn", $"commit -m {Quote(escapedMessage)} {files}", rootPath, timeoutSeconds: 120, ct: ct);
                    break;
                case VcsType.Perforce:
                    return OperationFailure("commit_files is not supported for Perforce because p4 submit scope depends on changelist state. Use submit for the active changelist.");
                default:
                    return OperationFailure($"commit_files is not supported for {adapter.VcsType}.");
            }

            return OperationFromCommand(commandResult, $"Committed {filePaths.Count} file(s).", filePaths);
        }

        private async Task<VcsOperationResult> ExecuteResolveFilesAsync(IVcsAdapter adapter, List<string> filePaths, CancellationToken ct)
        {
            var rootPath = VcsDetector.GetVcsRootPath();
            var files = JoinQuoted(filePaths);
            CommandResult commandResult;

            switch (adapter.VcsType)
            {
                case VcsType.Git:
                    commandResult = await VcsCommandExecutor.ExecuteAsync("git", $"add {files}", rootPath, ct: ct);
                    break;
                case VcsType.Svn:
                    commandResult = await VcsCommandExecutor.ExecuteAsync("svn", $"resolve --accept working {files}", rootPath, ct: ct);
                    break;
                case VcsType.Perforce:
                    commandResult = await VcsCommandExecutor.ExecuteAsync("p4", $"resolve -am {files}", rootPath, timeoutSeconds: 60, ct: ct);
                    break;
                default:
                    return OperationFailure($"resolve_files is not supported for {adapter.VcsType}.");
            }

            return OperationFromCommand(commandResult, $"Marked {filePaths.Count} file(s) resolved.", filePaths);
        }

        private async Task<VcsOperationResult> ExecuteIgnoreAsync(IVcsAdapter adapter, string filePath, string explicitPattern, string mode, CancellationToken ct)
        {
            var rootPath = VcsDetector.GetVcsRootPath();
            var pattern = string.IsNullOrWhiteSpace(explicitPattern) ? BuildIgnorePattern(filePath, mode) : explicitPattern.Trim();
            if (string.IsNullOrWhiteSpace(pattern))
                return OperationFailure("Could not determine ignore pattern.");

            switch (adapter.VcsType)
            {
                case VcsType.Git:
                    return await AppendIgnoreLineAsync(Path.Combine(rootPath, ".gitignore"), pattern, "Git", ct);
                case VcsType.Svn:
                    return await ExecuteSvnIgnoreAsync(rootPath, filePath, pattern, mode, ct);
                default:
                    return OperationFailure($"ignore actions are not supported for {adapter.VcsType}.");
            }
        }

        private async Task<VcsOperationResult> ExecuteRemoveFilesAsync(IVcsAdapter adapter, List<string> filePaths, CancellationToken ct)
        {
            var rootPath = VcsDetector.GetVcsRootPath();
            var files = JoinQuoted(filePaths);
            CommandResult commandResult;

            switch (adapter.VcsType)
            {
                case VcsType.Git:
                    commandResult = await VcsCommandExecutor.ExecuteAsync("git", $"rm {files}", rootPath, ct: ct);
                    break;
                case VcsType.Svn:
                    commandResult = await VcsCommandExecutor.ExecuteAsync("svn", $"delete --force {files}", rootPath, ct: ct);
                    break;
                case VcsType.Perforce:
                    commandResult = await VcsCommandExecutor.ExecuteAsync("p4", $"delete {files}", rootPath, ct: ct);
                    break;
                default:
                    return OperationFailure($"remove_files is not supported for {adapter.VcsType}.");
            }

            return OperationFromCommand(commandResult, $"Removed/scheduled delete for {filePaths.Count} file(s).", filePaths);
        }

        private async Task<VcsOperationResult> AppendIgnoreLineAsync(string ignoreFilePath, string pattern, string vcsName, CancellationToken ct)
        {
            try
            {
                var existing = File.Exists(ignoreFilePath) ? File.ReadAllLines(ignoreFilePath).ToList() : new List<string>();
                if (!existing.Any(line => string.Equals(line.Trim(), pattern, StringComparison.Ordinal)))
                {
                    existing.Add(pattern);
                    await Task.Run(() => File.WriteAllLines(ignoreFilePath, existing), ct);
                }

                return new VcsOperationResult
                {
                    Success = true,
                    Message = $"Updated {vcsName} ignore rule: {pattern}",
                    AffectedFiles = new List<string> { ignoreFilePath },
                    RawOutput = pattern
                };
            }
            catch (Exception ex)
            {
                return OperationFailure($"Failed to update ignore file: {ex.Message}");
            }
        }

        private async Task<VcsOperationResult> ExecuteSvnIgnoreAsync(string rootPath, string filePath, string pattern, string mode, CancellationToken ct)
        {
            var target = GetSvnIgnoreTarget(rootPath, filePath, mode);
            var propGet = await VcsCommandExecutor.ExecuteAsync("svn", $"propget svn:ignore {Quote(target)}", rootPath, ct: ct);
            var existingPatterns = propGet.Success && !string.IsNullOrEmpty(propGet.Output)
                ? propGet.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()).ToList()
                : new List<string>();

            if (!existingPatterns.Any(line => string.Equals(line, pattern, StringComparison.Ordinal)))
                existingPatterns.Add(pattern);

            var tempFile = Path.GetTempFileName();
            try
            {
                File.WriteAllLines(tempFile, existingPatterns);
                var propSet = await VcsCommandExecutor.ExecuteAsync("svn", $"propset svn:ignore --file {Quote(tempFile)} {Quote(target)}", rootPath, ct: ct);
                return OperationFromCommand(propSet, $"Updated SVN ignore rule: {pattern}", new List<string> { target });
            }
            finally
            {
                try { File.Delete(tempFile); } catch { /* ignore */ }
            }
        }

        private static VcsOperationResult OperationFromCommand(CommandResult commandResult, string successMessage, List<string> affectedFiles)
        {
            return new VcsOperationResult
            {
                Success = commandResult.Success,
                ErrorMessage = commandResult.ErrorMessage,
                Message = commandResult.Success ? successMessage : commandResult.ErrorMessage,
                AffectedFiles = affectedFiles ?? new List<string>(),
                RawOutput = string.IsNullOrEmpty(commandResult.Output) ? commandResult.Error : commandResult.Output
            };
        }

        private static VcsOperationResult OperationFailure(string message)
        {
            return new VcsOperationResult
            {
                Success = false,
                ErrorMessage = message,
                Message = message,
                AffectedFiles = new List<string>()
            };
        }

        private static CommandResult CommandFailure(string message)
        {
            return new CommandResult
            {
                Success = false,
                ErrorMessage = message,
                ExitCode = -1
            };
        }

        private static string BuildIgnorePattern(string filePath, string mode)
        {
            switch (mode)
            {
                case "folder":
                    return Path.GetFileName(Path.GetDirectoryName(filePath)) + "/";
                case "extension":
                    var extension = Path.GetExtension(filePath);
                    return string.IsNullOrEmpty(extension) ? Path.GetFileName(filePath) : "*" + extension;
                default:
                    return Path.GetFileName(filePath);
            }
        }

        private static string GetSvnIgnoreTarget(string rootPath, string filePath, string mode)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return rootPath;

            var absolutePath = Path.IsPathRooted(filePath) ? filePath : Path.Combine(rootPath, filePath);
            var directory = Directory.Exists(absolutePath) ? absolutePath : Path.GetDirectoryName(absolutePath);
            if (mode == "folder")
                directory = Path.GetDirectoryName(directory);

            return string.IsNullOrEmpty(directory) ? rootPath : directory;
        }

        private static string JoinQuoted(IEnumerable<string> values)
        {
            return string.Join(" ", values.Select(Quote));
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static string EscapeMessage(string value)
        {
            return (value ?? string.Empty).Replace("\"", "\\\"");
        }

        private static string Truncate(string value, int maxLength, out bool truncated)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                truncated = false;
                return value;
            }

            truncated = true;
            return value.Substring(0, maxLength) + "...";
        }
    }
}
