using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Components.VCS.Config;
using AgentCore.Editor.Tools;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Tools.Safety;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace AgentCore.Editor.Components.VCS.Tools
{
    [AgentTool("version_control",
        Description = "Manage version control operations for SVN, Perforce (P4), and Git repositories. " +
                      "Read actions: detect_vcs, get_status, get_branch, get_log, get_diff, get_remote, get_tags, get_blame, " +
                      "get_sync_status, get_commit_info, get_client_info, get_changelist, get_info, get_file_log. " +
                      "Write actions (the user is asked to approve each one in the editor; there is no confirm parameter): stage_files, unstage_files, commit, commit_files, " +
                      "create_branch, switch_branch, stash, stash_pop, checkout_files, revert_files, resolve_files, " +
                      "ignore_file, ignore_folder, ignore_extension, remove_files, cleanup, lock_files, unlock_files, move_files (SVN). " +
                      "USE FOR: checking file status before/after changes, reviewing diffs, committing work, managing branches. " +
                      "NOT FOR: file content reading (use manage_file), project-wide search (use search_code). " +
                      "ACTIVATE WHEN: user mentions git/svn/p4/commit/diff/branch/status/revert/blame. " +
                      "PREREQUISITE: VCS optional component enabled in Settings. Auto-detects VCS type from workspace.",
        Category = "VersionControl",
        Visibility = ToolVisibility.OnDemand,
        RequiresMainThread = true,
        MayModifyScripts = false,
        // 写操作的确认由宿主 PermissionGate 发起（ToolRiskPolicy 按能力位 + 底线规则），
        // 不再依赖模型自己传 confirmed=true —— 那等于让模型给自己审批。
        RiskLevel = ToolRiskLevel.High,
        Capabilities = ToolCapability.ReadProject | ToolCapability.VersionControlWrite,
        ReadOnlyActions = new[]
        {
            "detect_vcs", "get_status", "get_branch", "get_log", "get_file_log", "get_diff",
            "get_remote", "get_tags", "get_blame", "get_sync_status", "get_commit_info",
            "get_client_info", "get_changelist", "get_info"
        })]
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
                        ""lock_files"",
                        ""unlock_files"",
                        ""move_files"",
                        ""submit"",
                        ""sync"",
                        ""update"",
                        ""commit_svn"",
                        ""revert_svn"",
                        ""add_files""
                    ],
                    ""description"": ""Action to perform. Read-only: detect_vcs, get_status, get_branch, get_log, get_file_log, get_diff, get_remote, get_tags, get_blame, get_sync_status, get_commit_info, get_client_info (Perforce), get_changelist (Perforce), get_info (SVN). Write operations (each one is approved by the user in the editor): cleanup, stage_files, unstage_files, commit, commit_files, create_branch, switch_branch, stash, stash_pop (Git); checkout_files, revert_files, resolve_files, ignore_file, ignore_folder, ignore_extension (Git/SVN only, NOT Perforce), remove_files, submit, sync (Perforce); update, commit_svn, revert_svn, add_files, lock_files, unlock_files, move_files (SVN). SVN notes: move_files moves/renames one file or folder (svn move, keeps history) together with its .meta so GUIDs and references survive; get_commit_info accepts 1234 / r1234 / HEAD and lists repository paths changed in that revision; Unity .meta files are added/deleted/reverted/committed together with their assets automatically; get_status reports locks and svn:needs-lock files modified without a lock; operations that only partly succeed return status=partial with per-path results. Paths: inputs may be relative to the Unity project root (e.g. Assets/Foo.prefab), relative to the VCS root, or absolute; returned paths are relative to the Unity project root when the file is inside the project, otherwise relative to the VCS root (see detect_vcs root_path / project_root).""
                },
                ""file_path"": {
                    ""type"": ""string"",
                    ""description"": ""File path for get_diff, get_blame, get_info, get_file_log, cleanup, ignore_file, ignore_folder, ignore_extension actions: relative to the Unity project root (e.g. Assets/Foo.prefab); a path relative to the VCS root or an absolute path is also accepted. Omit for repository-wide cleanup.""
                },
                ""file_paths"": {
                    ""type"": ""array"",
                    ""items"": { ""type"": ""string"" },
                    ""description"": ""List of file paths for stage_files, unstage_files, revert_files, checkout_files, add_files, commit_files, resolve_files, remove_files, lock_files, unlock_files actions: relative to the Unity project root (e.g. Assets/Foo.prefab); paths relative to the VCS root or absolute paths are also accepted. Returned paths use the same project-relative form.""
                },
                ""recursive"": {
                    ""type"": ""boolean"",
                    ""description"": ""SVN revert_files / revert_svn only: revert directories recursively (--depth infinity). Default false: a directory path reverts only the directory itself.""
                },
                ""max_count"": {
                    ""type"": ""integer"",
                    ""description"": ""Maximum number of log entries to retrieve (default: 20, max: 100)"",
                    ""minimum"": 1,
                    ""maximum"": 100
                },
                ""revision"": {
                    ""type"": ""string"",
                    ""description"": ""Commit hash/revision number for get_commit_info action (SVN: 1234, r1234 or HEAD)""
                },
                ""source_path"": {
                    ""type"": ""string"",
                    ""description"": ""move_files (SVN): file or folder to move/rename, e.g. Assets/Old/Foo.prefab. Its .meta moves with it automatically.""
                },
                ""destination_path"": {
                    ""type"": ""string"",
                    ""description"": ""move_files (SVN): new path, e.g. Assets/New/Bar.prefab. Must not exist; missing parent folders are created. Relative paths use the same base as source_path.""
                },
                ""message"": {
                    ""type"": ""string"",
                    ""description"": ""Commit/submit message for commit, commit_files, commit_svn, submit, stash actions; optional lock comment for lock_files""
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
                }
            }
        }");

        public ToolMetadata Metadata => new ToolMetadata(
            name: "version_control",
            description: "Manage version control system (SVN/Perforce/Git). Supports read-only queries (status, log, diff, blame) and guarded write operations (stage, commit, revert, cleanup, resolve, ignore, remove, move, lock, branch). Write operations require user confirmation.",
            category: "VersionControl",
            parametersSchema: _parametersSchema,
            requiresMainThread: true
        );

        public async Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ToolResponse response;

            try
            {
                var action = ToolHelpers.GetRequiredString(parameters, "action").ToLowerInvariant();

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
                    "lock_files" => await HandleLockFiles(parameters, true, cancellationToken),
                    "unlock_files" => await HandleLockFiles(parameters, false, cancellationToken),
                    "move_files" => await HandleMoveFiles(parameters, cancellationToken),

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

        // ===== Phase 1: 只读查询处理器 =====

        private async Task<ToolResponse> HandleDetectVcs(CancellationToken ct)
        {
            try
            {
                // detect_vcs 是显式检测：未检测到 VCS 或命令曾探测失败时重新检测（例如刚安装了 git），
                // 否则复用会话缓存。命令探测在后台线程执行，不阻塞主线程。
                var vcsType = VcsDetector.DetectVcs();
                bool isAvailable;
                if (vcsType == VcsType.None
                    || VcsDetector.GetCommandAvailability(vcsType) == VcsCommandAvailability.Unavailable)
                {
                    isAvailable = await VcsDetector.Refresh();
                    vcsType = VcsDetector.DetectVcs();
                }
                else
                {
                    isAvailable = await VcsDetector.EnsureCommandAvailableAsync(vcsType);
                }

                var rootPath = VcsDetector.GetVcsRootPath();

                if (vcsType == VcsType.None)
                {
                    return ToolResponse.Fail("No version control system detected in the current project.");
                }

                var probeError = isAvailable ? null : VcsDetector.GetCommandProbeError(vcsType);

                var projectRoot = GetProjectRoot();
                var mapper = new VcsProjectPathMapper(rootPath, projectRoot);
                var data = new
                {
                    vcs_type = vcsType.ToString(),
                    root_path = rootPath,
                    project_root = projectRoot,
                    path_base = mapper.ProjectIsVcsRoot
                        ? "Paths are relative to the Unity project root (which is also the VCS root)."
                        : "Paths are relative to the Unity project root (e.g. Assets/...); inputs relative to root_path or absolute are also accepted. Files outside the Unity project are reported relative to root_path.",
                    command_available = isAvailable,
                    command_error = probeError,
                    priority_order = "SVN > Perforce > Git"
                };

                var message = $"Detected {vcsType} at {rootPath}. Command available: {isAvailable}";
                if (!string.IsNullOrEmpty(probeError))
                    message += $" ({probeError})";
                return ToolResponse.OkWithData(data, message);
            }
            catch (Exception ex)
            {
                return ToolResponse.Fail($"Failed to detect VCS: {ex.Message}");
            }
        }

        private async Task<ToolResponse> HandleGetStatus(CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail(AdapterUnavailableMessage);

            var result = await adapter.GetStatusAsync(ct);

            if (!result.Success)
                return ToolResponse.Fail($"Failed to get status: {result.ErrorMessage}");

            var data = new Dictionary<string, object>
            {
                { "vcs_type", adapter.VcsType.ToString() },
                { "total_files", result.Files.Count },
                { "files", result.Files.Select(DescribeFile).ToList() },
                { "summary", BuildStatusSummary(result.Files) }
            };
            if (result.LockedFiles.Count > 0)
                data["locked_files"] = result.LockedFiles.Select(DescribeFile).ToList();
            if (result.Warnings.Count > 0)
                data["warnings"] = result.Warnings;
            if (result.Truncated)
                data["truncated"] = true;

            var message = $"Found {result.Files.Count} changed files in working copy.";
            if (result.Warnings.Count > 0)
                message += " Warnings: " + string.Join(" ", result.Warnings);
            return ToolResponse.OkWithData(data, message);
        }

        private async Task<ToolResponse> HandleGetBranch(CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail(AdapterUnavailableMessage);

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
                return ToolResponse.Fail(AdapterUnavailableMessage);

            var maxCount = ToolHelpers.GetOptionalInt(parameters, "max_count", VcsSettings.MaxCommitEntries);
            if (maxCount > 100) maxCount = 100;
            if (maxCount < 1) maxCount = 1;

            List<VcsCommit> commits;
            if (adapter is SvnAdapter svnLogAdapter)
            {
                // SVN：认证失败 / 网络不通要如实报错，而不是返回"0 个提交"
                var (svnCommits, error) = await svnLogAdapter.GetLogWithErrorAsync(maxCount, ct);
                if (error != null)
                    return ToolResponse.Fail($"Failed to get log: {error}");
                commits = svnCommits;
            }
            else
            {
                commits = await adapter.GetLogAsync(maxCount, ct);
            }

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
                return ToolResponse.Fail(AdapterUnavailableMessage);

            var filePath = ToolHelpers.GetOptionalString(parameters, "file_path");
            if (string.IsNullOrEmpty(filePath))
                return ToolResponse.Fail("file_path is required for get_file_log action.");

            var maxCount = ToolHelpers.GetOptionalInt(parameters, "max_count", VcsSettings.MaxCommitEntries);
            if (maxCount > 100) maxCount = 100;
            if (maxCount < 1) maxCount = 1;

            var result = await ExecutePathLogAsync(adapter, ResolveInputPath(filePath), maxCount, ct);
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
                return ToolResponse.Fail(AdapterUnavailableMessage);

            var filePath = ToolHelpers.GetOptionalString(parameters, "file_path");
            var diff = await adapter.GetDiffAsync(ResolveInputPath(filePath), ct);

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
                return ToolResponse.Fail(AdapterUnavailableMessage);

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
                return ToolResponse.Fail(AdapterUnavailableMessage);

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
                return ToolResponse.Fail(AdapterUnavailableMessage);

            // fetch 等网络操作放到线程池执行，不占用主线程；结果同步给 VcsRemoteStatusMonitor，面板 / 角标与"上次检查时间"随之更新
            var result = await Task.Run(() => adapter.GetSyncStatusAsync(ct), ct);
            VcsRemoteStatusMonitor.RecordStatus(result);
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
                remote_changed_files = _paths.ToOutput(result.RemoteChangedFiles),
                conflicted_files = _paths.ToOutput(result.ConflictedFiles),
                locked_files = result.LockedFiles.Select(DescribeFile).ToList(),
                remote_revision = result.RemoteRevision,
                summary = result.Summary
            };

            return ToolResponse.OkWithData(data, result.Summary ?? "Retrieved VCS sync status.");
        }

        private async Task<ToolResponse> HandleGetBlame(JObject parameters, CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail(AdapterUnavailableMessage);

            var filePath = ToolHelpers.GetOptionalString(parameters, "file_path");
            if (string.IsNullOrEmpty(filePath))
                return ToolResponse.Fail("file_path is required for get_blame action.");

            var result = await adapter.GetBlameAsync(ResolveInputPath(filePath), ct);

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
                return ToolResponse.Fail(AdapterUnavailableMessage);

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

            // SVN: svn log -v -r <revision> ^/
            if (adapter is SvnAdapter svnAdapter)
            {
                var result = await svnAdapter.GetCommitInfoAsync(revision, ct);

                if (!result.Success)
                    return ToolResponse.Fail($"Failed to get commit info: {result.ErrorMessage}");

                var data = new
                {
                    vcs_type = "SVN",
                    revision = result.Revision,
                    author = result.Author,
                    date = result.Date,
                    message = result.Message,
                    // 仓库路径（如 /trunk/Assets/Foo.prefab），带动作前缀 A/M/D/R
                    changed_paths = result.ChangedFiles,
                    changed_paths_count = result.ChangedFiles.Count
                };

                return ToolResponse.OkWithData(data, $"r{result.Revision}: {result.Message}");
            }

            return ToolResponse.Fail($"get_commit_info is not supported for {adapter.VcsType}. Use get_log instead.");
        }

        private async Task<ToolResponse> HandleGetClientInfo(CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail(AdapterUnavailableMessage);

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
                return ToolResponse.Fail(AdapterUnavailableMessage);

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
                return ToolResponse.Fail(AdapterUnavailableMessage);

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
                return ToolResponse.Fail(AdapterUnavailableMessage);

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
                return ToolResponse.Fail(AdapterUnavailableMessage);

            var filePaths = GetFilePathsList(parameters);

            var result = await adapter.StageFilesAsync(filePaths, ct);
            return ToOperationResponse(adapter, result, "Failed to stage files");
        }

        private async Task<ToolResponse> HandleUnstageFiles(JObject parameters, CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail(AdapterUnavailableMessage);

            var filePaths = GetFilePathsList(parameters);

            var result = await adapter.UnstageFilesAsync(filePaths, ct);
            return ToOperationResponse(adapter, result, "Failed to unstage files");
        }

        private async Task<ToolResponse> HandleCommit(JObject parameters, CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail(AdapterUnavailableMessage);

            var message = ToolHelpers.GetOptionalString(parameters, "message");
            if (string.IsNullOrEmpty(message))
                return ToolResponse.Fail("message is required for commit/submit action.");

            var result = await adapter.CommitAsync(message, ct);
            return ToOperationResponse(adapter, result, "Failed to commit");
        }

        private async Task<ToolResponse> HandleCommitFiles(JObject parameters, CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail(AdapterUnavailableMessage);

            var message = ToolHelpers.GetOptionalString(parameters, "message");
            if (string.IsNullOrEmpty(message))
                return ToolResponse.Fail("message is required for commit_files action.");

            var filePaths = GetFilePathsList(parameters);
            if (filePaths == null || filePaths.Count == 0)
                return ToolResponse.Fail("file_paths is required for commit_files action.");

            var result = await ExecuteCommitFilesAsync(adapter, message, filePaths, ct);
            return ToOperationResponse(adapter, result, "Failed to commit files");
        }

        private async Task<ToolResponse> HandleCreateBranch(JObject parameters, CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail(AdapterUnavailableMessage);

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
                return ToolResponse.Fail(AdapterUnavailableMessage);

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
                return ToolResponse.Fail(AdapterUnavailableMessage);

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
                return ToolResponse.Fail(AdapterUnavailableMessage);

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
                return ToolResponse.Fail(AdapterUnavailableMessage);

            var filePaths = GetFilePathsList(parameters);
            if (filePaths == null || filePaths.Count == 0)
                return ToolResponse.Fail("file_paths is required for revert action.");

            var result = adapter is SvnAdapter svnAdapter
                ? await svnAdapter.RevertFilesAsync(filePaths, ToolHelpers.GetOptionalBool(parameters, "recursive", false), ct)
                : await adapter.RevertFilesAsync(filePaths, ct);
            return ToOperationResponse(adapter, result, "Failed to revert files");
        }

        private async Task<ToolResponse> HandleResolveFiles(JObject parameters, CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail(AdapterUnavailableMessage);

            var filePaths = GetFilePathsList(parameters);
            if (filePaths == null || filePaths.Count == 0)
                return ToolResponse.Fail("file_paths is required for resolve_files action.");

            var result = await ExecuteResolveFilesAsync(adapter, filePaths, ct);
            return ToOperationResponse(adapter, result, "Failed to resolve files");
        }

        private async Task<ToolResponse> HandleIgnore(JObject parameters, string mode, CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail(AdapterUnavailableMessage);

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
                return ToolResponse.Fail(AdapterUnavailableMessage);

            var filePaths = GetFilePathsList(parameters);
            if (filePaths == null || filePaths.Count == 0)
                return ToolResponse.Fail("file_paths is required for remove_files action.");

            var result = await ExecuteRemoveFilesAsync(adapter, filePaths, ct);
            return ToOperationResponse(adapter, result, "Failed to remove files");
        }

        private async Task<ToolResponse> HandleLockFiles(JObject parameters, bool doLock, CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail(AdapterUnavailableMessage);

            var action = doLock ? "lock_files" : "unlock_files";
            if (adapter is not SvnAdapter svnAdapter)
                return ToolResponse.Fail($"{action} is only available for SVN. Current VCS: " + adapter.VcsType);

            var filePaths = GetFilePathsList(parameters);
            if (filePaths == null || filePaths.Count == 0)
                return ToolResponse.Fail($"file_paths is required for {action} action.");

            var result = doLock
                ? await svnAdapter.LockFilesAsync(filePaths, ToolHelpers.GetOptionalString(parameters, "message"), ct)
                : await svnAdapter.UnlockFilesAsync(filePaths, ct);
            return ToOperationResponse(adapter, result, doLock ? "Failed to lock files" : "Failed to unlock files");
        }

        private async Task<ToolResponse> HandleMoveFiles(JObject parameters, CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail(AdapterUnavailableMessage);

            if (adapter is not SvnAdapter svnAdapter)
                return ToolResponse.Fail("move_files is only available for SVN. Current VCS: " + adapter.VcsType);

            var source = ToolHelpers.GetOptionalString(parameters, "source_path");
            var destination = ToolHelpers.GetOptionalString(parameters, "destination_path");
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination))
                return ToolResponse.Fail("source_path and destination_path are required for move_files action.");

            var result = await svnAdapter.MoveAsync(source, destination, ct);
            return ToOperationResponse(adapter, result, "Failed to move");
        }

        private async Task<ToolResponse> HandleSync(CancellationToken ct)
        {
            var adapter = GetAdapter();
            if (adapter == null)
                return ToolResponse.Fail(AdapterUnavailableMessage);

            var result = await adapter.SyncAsync(ct);
            return ToOperationResponse(adapter, result, "Failed to sync/update", new Dictionary<string, object>
            {
                { "conflicted_files", _paths.ToOutput(result.ConflictedFiles) }
            });
        }

        // ===== 辅助方法 =====

        /// <summary>
        /// GetAdapter 返回 null 时的统一错误信息。命令可用性是会话级缓存，提示 LLM 可用 detect_vcs 重新检测。
        /// </summary>
        private const string AdapterUnavailableMessage =
            "No version control system detected or command not available. Run action 'detect_vcs' to re-check (e.g. after installing the VCS CLI).";

        // 本次调用的路径换算（工程根 ↔ VCS 根），由 GetAdapter 设置；根目录不随调用变化，并发调用共享也无妨
        private VcsProjectPathMapper _paths = new VcsProjectPathMapper(null, null);
        private bool _resolveInputPaths;

        private IVcsAdapter GetAdapter()
        {
            var vcsType = VcsDetector.DetectVcs();
            if (vcsType == VcsType.None)
                return null;

            var adapter = CreateAdapter(vcsType);
            if (adapter == null || !adapter.IsAvailable())
                return null;

            _paths = new VcsProjectPathMapper(VcsDetector.GetVcsRootPath(), GetProjectRoot());
            _resolveInputPaths = !(adapter is SvnAdapter);
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
                .Select(path => ResolveInputPath(path.Trim()))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// 输入路径：工程相对 / VCS 根相对 / 绝对路径都接受。SVN 适配器自己解析（SvnAdapter.ResolvePath，
        /// 保留用户写法用于结果展示）；Git / Perforce 在 VCS 根执行命令，工程相对路径在这里换算为绝对路径。
        /// </summary>
        private string ResolveInputPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !_resolveInputPaths)
                return path;
            return _paths.ResolveInput(path);
        }

        /// <summary>Unity 工程根。编辑器进程的当前目录就是工程根（与 SvnAdapter 一致，不依赖 Application.dataPath）。</summary>
        private static string GetProjectRoot()
        {
            try { return Directory.GetCurrentDirectory(); }
            catch { return null; }
        }

        private static IEnumerable<string> GetValidActions()
        {
            return _parametersSchema["properties"]?["action"]?["enum"]?.Select(token => token.ToString()) ?? Enumerable.Empty<string>();
        }

        private Dictionary<string, object> BuildOperationData(IVcsAdapter adapter, VcsOperationResult result, Dictionary<string, object> extra = null)
        {
            var data = new Dictionary<string, object>
            {
                { "vcs_type", adapter.VcsType.ToString() },
                { "message", result.Message },
                { "affected_files", _paths.ToOutput(result.AffectedFiles) },
                { "affected_count", result.AffectedFiles?.Count ?? 0 },
                { "raw_output", Truncate(result.RawOutput, 1000, out var rawOutputTruncated) },
                { "raw_output_truncated", rawOutputTruncated }
            };

            if (result.FailedFiles != null && result.FailedFiles.Count > 0)
                data["failed_files"] = _paths.ToOutputKeys(result.FailedFiles);
            if (result.SkippedFiles != null && result.SkippedFiles.Count > 0)
                data["skipped_files"] = _paths.ToOutputKeys(result.SkippedFiles);
            if (result.Warnings != null && result.Warnings.Count > 0)
                data["warnings"] = result.Warnings;

            if (extra != null)
            {
                foreach (var pair in extra)
                    data[pair.Key] = pair.Value;
            }

            return data;
        }

        /// <summary>
        /// 把操作结果转换为 ok / partial / failed 三态响应：
        /// 部分路径失败、更新后有冲突、更新中途出错但已更新部分内容时返回 partial（逐项列出），不再报告为完全成功。
        /// </summary>
        private ToolResponse ToOperationResponse(IVcsAdapter adapter, VcsOperationResult result, string failPrefix, Dictionary<string, object> extra = null)
        {
            var hasFailures = result.FailedFiles != null && result.FailedFiles.Count > 0;
            if (!result.Success && !result.IsPartial)
            {
                var error = string.IsNullOrEmpty(result.ErrorMessage) ? result.Message : result.ErrorMessage;
                if (hasFailures && (result.FailedFiles.Count > 1 || string.IsNullOrEmpty(error)))
                    error = string.Join("; ", result.FailedFiles.Take(10).Select(kv => $"{_paths.ToOutput(kv.Key)}: {kv.Value}"));
                return ToolResponse.Fail($"{failPrefix}: {error}");
            }

            var data = BuildOperationData(adapter, result, extra);
            if (!result.IsPartial && !hasFailures)
                return ToolResponse.OkWithData(data, result.Message);

            var items = new List<ToolItemResult>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var conflicted in _paths.ToOutput(result.ConflictedFiles))
            {
                if (seen.Add(conflicted))
                    items.Add(ToolItemResult.Failed(conflicted, "conflict: resolve the file, then call resolve_files"));
            }
            if (hasFailures)
            {
                foreach (var pair in _paths.ToOutputKeys(result.FailedFiles))
                {
                    if (seen.Add(pair.Key))
                        items.Add(ToolItemResult.Failed(pair.Key, pair.Value));
                }
            }
            foreach (var affected in _paths.ToOutput(result.AffectedFiles))
            {
                if (seen.Add(affected))
                    items.Add(ToolItemResult.Succeeded(affected));
            }
            if (!items.Any(i => i.Ok))
                items.Add(ToolItemResult.Succeeded(result.OperationName ?? "operation", result.Message));
            if (!result.Success)
                items.Add(ToolItemResult.Failed(result.OperationName ?? "operation", result.ErrorMessage));
            if (items.All(i => i.Ok) && result.Warnings != null && result.Warnings.Count > 0)
                items.Add(ToolItemResult.Failed("warnings", string.Join(" | ", result.Warnings)));

            return ToolResponse.Partial(result.Message, items, data);
        }

        private object DescribeFile(VcsFileStatus f)
        {
            var entry = new Dictionary<string, object>
            {
                { "path", _paths.ToOutput(f.FilePath) },
                { "state", f.State.ToString() },
                { "description", f.StateDescription }
            };
            if (!string.IsNullOrEmpty(f.OriginalPath))
                entry["original_path"] = _paths.ToOutput(f.OriginalPath);
            if (!string.IsNullOrEmpty(f.MovedToPath))
                entry["moved_to"] = _paths.ToOutput(f.MovedToPath);
            if (!string.IsNullOrEmpty(f.LockState))
            {
                entry["lock"] = f.LockState;
                if (!string.IsNullOrEmpty(f.LockOwner))
                    entry["lock_owner"] = f.LockOwner;
                if (!string.IsNullOrEmpty(f.LockComment))
                    entry["lock_comment"] = f.LockComment;
            }
            if (f.NeedsLock)
                entry["needs_lock"] = true;
            if (f.IsSwitched)
                entry["switched"] = true;
            return entry;
        }

        private async Task<CommandResult> ExecutePathLogAsync(IVcsAdapter adapter, string filePath, int maxCount, CancellationToken ct)
        {
            switch (adapter.VcsType)
            {
                case VcsType.Git:
                    return await VcsCommandExecutor.ExecuteAsync("git", new List<string> { "log", $"-{maxCount}", "--follow", "--", filePath }, VcsDetector.GetVcsRootPath(), timeoutSeconds: 60, ct: ct);
                case VcsType.Svn:
                    return await ((SvnAdapter)adapter).GetPathLogAsync(filePath, maxCount, ct);
                case VcsType.Perforce:
                    return await VcsCommandExecutor.ExecuteAsync("p4", new List<string> { "filelog", "-m", maxCount.ToString(), filePath }, VcsDetector.GetVcsRootPath(), timeoutSeconds: 60, ct: ct);
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
                {
                    var svnTarget = string.IsNullOrWhiteSpace(target) ? rootPath : ((SvnAdapter)adapter).ResolvePath(target);
                    if (!SvnCommandLine.CanPassToSvn(svnTarget))
                        return OperationFailure(SvnCommandLine.UnrepresentablePathError(target));
                    // 大工作副本的 cleanup 可能很慢：中途被杀会让工作副本保持锁定
                    commandResult = await VcsCommandExecutor.ExecuteAsync("svn", new List<string> { "cleanup", "--non-interactive", SvnCommandLine.PathArg(svnTarget) }, rootPath, timeoutSeconds: 900, ct: ct);
                    break;
                }
                case VcsType.Git:
                    commandResult = await VcsCommandExecutor.ExecuteAsync("git", new List<string> { "gc", "--auto" }, rootPath, timeoutSeconds: 120, ct: ct);
                    break;
                case VcsType.Perforce:
                    commandResult = await VcsCommandExecutor.ExecuteAsync("p4", new List<string> { "reconcile", "-n" }, rootPath, timeoutSeconds: 120, ct: ct);
                    break;
                default:
                    return OperationFailure($"cleanup is not supported for {adapter.VcsType}.");
            }

            return OperationFromCommand(commandResult, $"Cleanup completed for {adapter.VcsType}.", new List<string> { cleanupTarget });
        }

        private async Task<VcsOperationResult> ExecuteCommitFilesAsync(IVcsAdapter adapter, string message, List<string> filePaths, CancellationToken ct)
        {
            var rootPath = VcsDetector.GetVcsRootPath();
            // 提交信息与每个路径都作为独立 argv 传入，由 VcsArgs 负责转义
            var commitMessage = message ?? string.Empty;
            CommandResult commandResult;

            switch (adapter.VcsType)
            {
                case VcsType.Git:
                    commandResult = await ExecuteGitCommitFilesAsync(rootPath, commitMessage, filePaths, ct);
                    break;
                case VcsType.Svn:
                    // 单次原子提交，自动补齐 .meta / 新增父目录 / move 另一端，见 SvnAdapter.CommitFilesAsync
                    return await ((SvnAdapter)adapter).CommitFilesAsync(commitMessage, filePaths, ct);
                case VcsType.Perforce:
                    return OperationFailure("commit_files is not supported for Perforce because p4 submit scope depends on changelist state. Use submit for the active changelist.");
                default:
                    return OperationFailure($"commit_files is not supported for {adapter.VcsType}.");
            }

            return OperationFromCommand(commandResult, $"Committed {filePaths.Count} file(s).", filePaths);
        }

        /// <summary>
        /// git commit 指定文件。提交必须是一次原子操作，不能分批：
        /// 命令行放得下时直接传路径；超过上限时改用 <c>--pathspec-from-file</c>（NUL 分隔 UTF-8 临时文件，需 git 2.25+）。
        /// </summary>
        private static async Task<CommandResult> ExecuteGitCommitFilesAsync(string rootPath, string commitMessage, List<string> filePaths, CancellationToken ct)
        {
            var prefix = new List<string> { "commit", "-m", commitMessage, "--" };
            if (VcsArgs.ChunkPaths("git", prefix, filePaths).Count <= 1)
            {
                var gitArgs = new List<string>(prefix);
                gitArgs.AddRange(filePaths);
                return await VcsCommandExecutor.ExecuteAsync("git", gitArgs, rootPath, timeoutSeconds: 120, ct: ct);
            }

            var pathFile = Path.Combine(Path.GetTempPath(), $"agentcore-commit-{Guid.NewGuid():N}.pathspec");
            try
            {
                File.WriteAllText(pathFile, string.Join("\0", filePaths) + "\0", new UTF8Encoding(false));
                var args = new List<string> { "commit", "-m", commitMessage, "--pathspec-from-file=" + pathFile, "--pathspec-file-nul" };
                return await VcsCommandExecutor.ExecuteAsync("git", args, rootPath, timeoutSeconds: 120, ct: ct);
            }
            finally
            {
                TryDeleteFile(pathFile);
            }
        }

        private static void TryDeleteFile(string path)
        {
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

        private async Task<VcsOperationResult> ExecuteResolveFilesAsync(IVcsAdapter adapter, List<string> filePaths, CancellationToken ct)
        {
            var rootPath = VcsDetector.GetVcsRootPath();
            CommandResult commandResult;

            switch (adapter.VcsType)
            {
                case VcsType.Git:
                    // 路径多时自动分批，避免超出 Windows 命令行长度上限
                    commandResult = await VcsCommandExecutor.ExecuteChunkedAsync("git", new[] { "add", "--" }, filePaths, rootPath, ct: ct);
                    break;
                case VcsType.Svn:
                    return await ((SvnAdapter)adapter).ResolveFilesAsync(filePaths, ct);
                case VcsType.Perforce:
                    commandResult = await VcsCommandExecutor.ExecuteChunkedAsync("p4", new[] { "resolve", "-am" }, filePaths, rootPath, timeoutSeconds: 60, ct: ct);
                    break;
                default:
                    return OperationFailure($"resolve_files is not supported for {adapter.VcsType}.");
            }

            return OperationFromCommand(commandResult, $"Marked {filePaths.Count} file(s) resolved.", filePaths);
        }

        private async Task<VcsOperationResult> ExecuteIgnoreAsync(IVcsAdapter adapter, string filePath, string explicitPattern, string mode, CancellationToken ct)
        {
            var rootPath = VcsDetector.GetVcsRootPath();
            if (adapter is SvnAdapter svnIgnoreAdapter)
                return await ExecuteSvnIgnoreAsync(svnIgnoreAdapter, rootPath, filePath, explicitPattern, mode, ct);

            var pattern = string.IsNullOrWhiteSpace(explicitPattern) ? BuildIgnorePattern(filePath, mode) : explicitPattern.Trim();
            if (string.IsNullOrWhiteSpace(pattern))
                return OperationFailure("Could not determine ignore pattern.");

            switch (adapter.VcsType)
            {
                case VcsType.Git:
                    return await AppendIgnoreLineAsync(Path.Combine(rootPath, ".gitignore"), pattern, "Git", ct);
                default:
                    return OperationFailure($"ignore actions are not supported for {adapter.VcsType}.");
            }
        }

        private async Task<VcsOperationResult> ExecuteRemoveFilesAsync(IVcsAdapter adapter, List<string> filePaths, CancellationToken ct)
        {
            var rootPath = VcsDetector.GetVcsRootPath();
            CommandResult commandResult;

            switch (adapter.VcsType)
            {
                case VcsType.Git:
                    // 路径多时自动分批，避免超出 Windows 命令行长度上限
                    commandResult = await VcsCommandExecutor.ExecuteChunkedAsync("git", new[] { "rm", "--" }, filePaths, rootPath, ct: ct);
                    break;
                case VcsType.Svn:
                    // 对应的 .meta / 资源一并删除，见 SvnAdapter.DeleteFilesAsync
                    return await ((SvnAdapter)adapter).DeleteFilesAsync(filePaths, ct);
                case VcsType.Perforce:
                    commandResult = await VcsCommandExecutor.ExecuteChunkedAsync("p4", new[] { "delete" }, filePaths, rootPath, ct: ct);
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

        /// <summary>
        /// SVN 忽略规则写入目标目录的 svn:ignore 属性（与已有规则合并）。
        /// <list type="bullet">
        /// <item>读取现有属性失败（属性不存在除外）时直接报错，不覆盖已有规则；</item>
        /// <item>svn:ignore 只匹配目标目录的直接子项名，不支持 "dir/" 写法：目录名不带结尾斜杠；</item>
        /// <item>Unity 项目中被忽略资源的 .meta 同样要忽略，否则会一直显示为未版本控制文件。</item>
        /// </list>
        /// </summary>
        private async Task<VcsOperationResult> ExecuteSvnIgnoreAsync(SvnAdapter svnAdapter, string rootPath, string filePath, string explicitPattern, string mode, CancellationToken ct)
        {
            var plan = BuildSvnIgnorePlan(
                string.IsNullOrWhiteSpace(filePath) ? null : svnAdapter.ResolvePath(filePath),
                rootPath, explicitPattern, mode, path => Directory.Exists(path));
            if (plan.Patterns.Count == 0)
                return OperationFailure("Could not determine ignore pattern.");
            if (!SvnCommandLine.CanPassToSvn(plan.Target))
                return OperationFailure(SvnCommandLine.UnrepresentablePathError(plan.Target));
            if (!Directory.Exists(plan.Target))
                return OperationFailure($"Ignore target directory does not exist: {plan.Target}");

            var targetArg = SvnCommandLine.PathArg(plan.Target);
            var propGet = await VcsCommandExecutor.ExecuteAsync("svn", new List<string> { "propget", "svn:ignore", "--xml", "--non-interactive", targetArg }, rootPath, ct: ct);
            // 属性不存在：svn 以 W200017 非零退出；其他错误（未纳入版本控制、工作副本被锁……）不能当作"没有规则"然后覆盖
            if (!propGet.Success && (propGet.ErrorMessage ?? string.Empty).IndexOf("W200017", StringComparison.Ordinal) < 0)
                return OperationFailure($"Could not read existing svn:ignore on {plan.Target}: {propGet.ErrorMessage}");

            var existingPatterns = ParseSvnIgnoreProperty(propGet.Output);
            var added = plan.Patterns.Where(p => !existingPatterns.Contains(p, StringComparer.Ordinal)).ToList();
            if (added.Count == 0)
            {
                return new VcsOperationResult
                {
                    Success = true,
                    Message = $"SVN ignore rule already present on {plan.Target}: {string.Join(", ", plan.Patterns)}",
                    AffectedFiles = new List<string> { plan.Target }
                };
            }

            existingPatterns.AddRange(added);
            string valueFile = null;
            try
            {
                // svn:ignore 是 svn:* 属性，值必须为 LF 换行；UTF-8 文件 + --encoding 保证中文规则不被按代码页误读
                valueFile = SvnCommandLine.WriteMessageFile(string.Join("\n", existingPatterns) + "\n", rootPath);
                var args = new List<string> { "propset", "svn:ignore", "--non-interactive" };
                args.AddRange(SvnCommandLine.MessageFileArgs(valueFile));
                args.Add(targetArg);
                var propSet = await VcsCommandExecutor.ExecuteAsync("svn", args, rootPath, ct: ct);
                var result = OperationFromCommand(propSet, $"Updated svn:ignore on {plan.Target}: added {string.Join(", ", added)} (commit the directory to share the rule).", new List<string> { plan.Target });
                return result;
            }
            finally
            {
                SvnCommandLine.TryDeleteFile(valueFile);
            }
        }

        /// <summary>svn:ignore 规则的写入位置与模式。</summary>
        public sealed class SvnIgnorePlan
        {
            public string Target;
            public List<string> Patterns = new List<string>();
        }

        /// <summary>
        /// 计算 svn:ignore 的目标目录与模式（纯函数，便于测试）。
        /// <para>file：文件名，写在所在目录；folder：目录名，写在父目录（<paramref name="absolutePath"/> 是文件时取其所在目录）；
        /// extension：<c>*.ext</c>，写在所在目录。每个模式都附带对应的 <c>.meta</c> 模式。</para>
        /// </summary>
        public static SvnIgnorePlan BuildSvnIgnorePlan(string absolutePath, string rootPath, string explicitPattern, string mode, Func<string, bool> isDirectory)
        {
            var plan = new SvnIgnorePlan();
            string pattern;
            if (string.IsNullOrWhiteSpace(absolutePath))
            {
                plan.Target = rootPath;
                pattern = explicitPattern;
            }
            else
            {
                var trimmed = absolutePath.TrimEnd('\\', '/');
                var isDir = isDirectory(trimmed);
                switch (mode)
                {
                    case "folder":
                    {
                        var folder = isDir ? trimmed : Path.GetDirectoryName(trimmed);
                        plan.Target = Path.GetDirectoryName(folder);
                        pattern = explicitPattern ?? Path.GetFileName(folder);
                        break;
                    }
                    case "extension":
                    {
                        plan.Target = isDir ? trimmed : Path.GetDirectoryName(trimmed);
                        var extension = Path.GetExtension(trimmed);
                        pattern = explicitPattern ?? (string.IsNullOrEmpty(extension) || isDir ? Path.GetFileName(trimmed) : "*" + extension);
                        break;
                    }
                    default:
                        plan.Target = isDir && !string.IsNullOrWhiteSpace(explicitPattern) ? trimmed : Path.GetDirectoryName(trimmed);
                        pattern = explicitPattern ?? Path.GetFileName(trimmed);
                        break;
                }
                if (string.IsNullOrEmpty(plan.Target))
                    plan.Target = rootPath;
            }

            pattern = pattern?.Trim().TrimEnd('/', '\\');
            if (string.IsNullOrEmpty(pattern))
                return plan;

            plan.Patterns.Add(pattern);
            if (!UnityMetaPaths.IsMeta(pattern) && pattern != "*")
                plan.Patterns.Add(pattern + UnityMetaPaths.MetaExtension);
            return plan;
        }

        /// <summary>解析 <c>svn propget svn:ignore --xml</c> 的规则列表。</summary>
        public static List<string> ParseSvnIgnoreProperty(string xml)
        {
            var patterns = new List<string>();
            if (string.IsNullOrWhiteSpace(xml))
                return patterns;
            try
            {
                var doc = System.Xml.Linq.XDocument.Parse(xml);
                foreach (var property in doc.Descendants("property"))
                {
                    foreach (var line in property.Value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.Length > 0 && !patterns.Contains(trimmed, StringComparer.Ordinal))
                            patterns.Add(trimmed);
                    }
                }
            }
            catch (Exception)
            {
                // 不完整的 XML：按无规则处理（读取失败的情况已在调用方单独拦截）
            }
            return patterns;
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
