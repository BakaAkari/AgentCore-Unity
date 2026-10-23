using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCore.Editor.Components.VCS.Tools
{
    /// <summary>
    /// 版本控制系统适配器接口
    /// 定义所有 VCS 适配器必须实现的通用操作
    /// </summary>
    public interface IVcsAdapter
    {
        /// <summary>
        /// VCS 类型
        /// </summary>
        VcsType VcsType { get; }

        /// <summary>
        /// 检查 VCS 命令是否可用
        /// </summary>
        bool IsAvailable();

        // ===== Phase 1: 只读查询 =====

        /// <summary>
        /// 获取工作区状态
        /// </summary>
        Task<VcsStatusResult> GetStatusAsync(CancellationToken ct = default);

        /// <summary>
        /// 获取当前分支/工作区信息
        /// </summary>
        Task<VcsBranchInfo> GetBranchInfoAsync(CancellationToken ct = default);

        /// <summary>
        /// 获取提交历史
        /// </summary>
        Task<List<VcsCommit>> GetLogAsync(int maxCount = 20, CancellationToken ct = default);

        /// <summary>
        /// 获取文件差异
        /// </summary>
        Task<string> GetDiffAsync(string filePath = null, CancellationToken ct = default);

        /// <summary>
        /// 获取远程仓库信息
        /// </summary>
        Task<VcsRemoteInfo> GetRemoteInfoAsync(CancellationToken ct = default);

        /// <summary>
        /// 获取标签列表
        /// </summary>
        Task<List<string>> GetTagsAsync(CancellationToken ct = default);

        /// <summary>
        /// 获取文件的逐行注释信息 (blame/annotate)
        /// Git: git blame, SVN: svn blame, Perforce: p4 annotate
        /// </summary>
        Task<VcsBlameResult> GetBlameAsync(string filePath, CancellationToken ct = default);

        /// <summary>
        /// 获取本地工作区与远端的同步状态。
        /// Git: fetch + rev-list, SVN: svn status -u -q, Perforce: p4 sync -n
        /// </summary>
        Task<VcsSyncStatus> GetSyncStatusAsync(CancellationToken ct = default);

        // ===== Phase 2: 操作类（需要确认） =====

        /// <summary>
        /// 暂存/添加文件到版本控制
        /// Git: git add, SVN: svn add, Perforce: p4 add/edit
        /// </summary>
        Task<VcsOperationResult> StageFilesAsync(List<string> filePaths, CancellationToken ct = default);

        /// <summary>
        /// 取消暂存文件
        /// Git: git reset HEAD, SVN: N/A, Perforce: p4 revert (未修改的)
        /// </summary>
        Task<VcsOperationResult> UnstageFilesAsync(List<string> filePaths, CancellationToken ct = default);

        /// <summary>
        /// 提交变更
        /// Git: git commit, SVN: svn commit, Perforce: p4 submit
        /// </summary>
        Task<VcsOperationResult> CommitAsync(string message, CancellationToken ct = default);

        /// <summary>
        /// 还原文件到版本控制状态
        /// Git: git checkout/restore, SVN: svn revert, Perforce: p4 revert
        /// </summary>
        Task<VcsOperationResult> RevertFilesAsync(List<string> filePaths, CancellationToken ct = default);

        /// <summary>
        /// 同步/更新到最新版本
        /// Git: git pull, SVN: svn update, Perforce: p4 sync
        /// </summary>
        Task<VcsOperationResult> SyncAsync(CancellationToken ct = default);
    }

    // ===== 数据类 =====

    /// <summary>
    /// VCS 状态结果
    /// </summary>
    public class VcsStatusResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public List<VcsFileStatus> Files { get; set; } = new List<VcsFileStatus>();
        public string RawOutput { get; set; }

        /// <summary>输出过大被截断，Files 可能不完整。</summary>
        public bool Truncated { get; set; }

        /// <summary>
        /// 带锁信息的文件（SVN：K/O/T/B），包括内容未修改、只是被锁定的文件。其他 VCS 为空。
        /// </summary>
        public List<VcsFileStatus> LockedFiles { get; set; } = new List<VcsFileStatus>();

        /// <summary>
        /// 需要提醒的情况（只报告不阻断），如：修改了 svn:needs-lock 文件却没有持有锁、工作副本需要 cleanup。
        /// </summary>
        public List<string> Warnings { get; set; } = new List<string>();
    }

    /// <summary>
    /// 文件状态
    /// </summary>
    public class VcsFileStatus
    {
        public string FilePath { get; set; }
        public VcsFileState State { get; set; }
        public string StateDescription { get; set; }

        /// <summary>重命名 / 复制前的原路径（Git porcelain v2 的 origPath），其他情况为 null。</summary>
        public string OriginalPath { get; set; }

        /// <summary>VCS 原始状态码（如 Git 的 XY），可能为 null。</summary>
        public string StatusCode { get; set; }

        /// <summary>SVN move 的源端：该路径已被移动到的新路径（moved-to），其他情况为 null。</summary>
        public string MovedToPath { get; set; }

        /// <summary>
        /// SVN 锁状态（svn status 第 6 列）：K 本工作副本持有锁，O 他人持有锁，T 锁被抢，B 锁已失效；无锁为 null。
        /// </summary>
        public string LockState { get; set; }

        /// <summary>锁持有者（有锁时）。</summary>
        public string LockOwner { get; set; }

        /// <summary>锁的注释（svn lock -m）；没有锁或没有注释时为 null</summary>
        public string LockComment { get; set; }

        /// <summary>文件设置了 svn:needs-lock（只对已修改、未持有锁的文件检测；见 SvnAdapter.AnnotateNeedsLock）。</summary>
        public bool NeedsLock { get; set; }

        /// <summary>SVN：该路径被 switch 到了与父目录不同的 URL（svn status 第 5 列 S）。</summary>
        public bool IsSwitched { get; set; }
    }

    /// <summary>
    /// 文件状态枚举
    /// </summary>
    public enum VcsFileState
    {
        Unmodified,
        Modified,
        Added,
        Deleted,
        Renamed,
        Copied,
        Untracked,
        Ignored,
        Conflicted,
        Missing
    }

    /// <summary>
    /// 分支信息
    /// </summary>
    public class VcsBranchInfo
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public string CurrentBranch { get; set; }
        public string CurrentRevision { get; set; }
        public List<string> AllBranches { get; set; } = new List<string>();
        public string RawOutput { get; set; }
    }

    /// <summary>
    /// 提交记录
    /// </summary>
    public class VcsCommit
    {
        public string Revision { get; set; }
        public string Author { get; set; }
        public string Date { get; set; }
        public string Message { get; set; }
        public List<string> ChangedFiles { get; set; } = new List<string>();
    }

    /// <summary>
    /// 远程仓库信息
    /// </summary>
    public class VcsRemoteInfo
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public string RemoteUrl { get; set; }
        public string RemoteName { get; set; }
        public string RawOutput { get; set; }
    }

    /// <summary>
    /// Blame/Annotate 结果
    /// </summary>
    public class VcsBlameResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public string FilePath { get; set; }
        public List<VcsBlameLine> Lines { get; set; } = new List<VcsBlameLine>();
        public string RawOutput { get; set; }
    }

    /// <summary>
    /// Blame 单行信息
    /// </summary>
    public class VcsBlameLine
    {
        public int LineNumber { get; set; }
        public string Revision { get; set; }
        public string Author { get; set; }
        public string Date { get; set; }
        public string Content { get; set; }
    }

    /// <summary>
    /// 远端同步状态。
    /// </summary>
    public class VcsSyncStatus
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public bool HasRemoteChanges { get; set; }
        public bool HasLocalChanges { get; set; }
        public bool HasConflicts { get; set; }
        public int RemoteChangeCount { get; set; }
        public int LocalChangeCount { get; set; }
        public int BehindCount { get; set; }
        public int AheadCount { get; set; }
        public List<string> RemoteChangedFiles { get; set; } = new List<string>();
        public List<string> ConflictedFiles { get; set; } = new List<string>();

        /// <summary>
        /// SVN：仓库中所有带锁的文件（<see cref="VcsFileStatus.LockState"/>：K 本人 / O 他人 / T 被抢 / B 已失效），
        /// 含锁持有者与注释。O / T / B 只有远端检查（svn status -u）才能拿到。
        /// </summary>
        public List<VcsFileStatus> LockedFiles { get; set; } = new List<VcsFileStatus>();

        /// <summary>SVN：本次检查对比的仓库 HEAD 版本号（svn status -u 的 against revision）；未知时为 null。</summary>
        public string RemoteRevision { get; set; }

        /// <summary>
        /// <see cref="LockedFiles"/> / <see cref="RemoteChangedFiles"/> / <see cref="ConflictedFiles"/> 中相对路径的基准目录
        /// （适配器的工作目录，SVN 为工作副本根）；未知时为 null。
        /// </summary>
        public string PathBase { get; set; }
        public string Summary { get; set; }
        public string RawOutput { get; set; }
    }

    /// <summary>
    /// VCS 操作结果（Phase 2 操作类）
    /// </summary>
    public class VcsOperationResult
    {
        public bool Success { get; set; }
        public string OperationName { get; set; }
        public string CommandLine { get; set; }
        public string ErrorMessage { get; set; }
        public string Message { get; set; }
        public string RawOutput { get; set; }
        public List<string> LogLines { get; set; } = new List<string>();
        public List<string> AffectedFiles { get; set; } = new List<string>();
        public List<string> ConflictedFiles { get; set; } = new List<string>();

        /// <summary>
        /// 部分完成：操作已生效但有遗留问题（部分路径失败、更新后有冲突、外部定义更新失败等）。
        /// 工具层据此返回 partial 而不是 ok。
        /// </summary>
        public bool IsPartial { get; set; }

        /// <summary>逐个失败的路径及原因（路径 → 错误）。</summary>
        public Dictionary<string, string> FailedFiles { get; set; } = new Dictionary<string, string>();

        /// <summary>未执行任何操作的路径及原因（如还原未纳入版本控制的文件）。</summary>
        public Dictionary<string, string> SkippedFiles { get; set; } = new Dictionary<string, string>();

        /// <summary>需要提醒的情况（只报告不阻断）。</summary>
        public List<string> Warnings { get; set; } = new List<string>();
    }

    /// <summary>
    /// 提交详情（git show / svn log -r / p4 describe）
    /// </summary>
    public class VcsCommitDetail
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public string Revision { get; set; }
        public string Author { get; set; }
        public string Date { get; set; }
        public string Message { get; set; }
        public List<string> ChangedFiles { get; set; } = new List<string>();
        public string Diff { get; set; }
        public string RawOutput { get; set; }
    }

    /// <summary>
    /// SVN Info 结果
    /// </summary>
    public class VcsSvnInfo
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public string Url { get; set; }
        public string RepositoryRoot { get; set; }
        public string Revision { get; set; }
        public string LastChangedAuthor { get; set; }
        public string LastChangedRevision { get; set; }
        public string LastChangedDate { get; set; }
        public string NodeKind { get; set; }
        public string RawOutput { get; set; }
    }

    /// <summary>
    /// Perforce Client Info 结果
    /// </summary>
    public class VcsPerforceClientInfo
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public string ClientName { get; set; }
        public string Owner { get; set; }
        public string Root { get; set; }
        public string Stream { get; set; }
        public string Host { get; set; }
        public string Description { get; set; }
        public List<string> ViewMappings { get; set; } = new List<string>();
        public string RawOutput { get; set; }
    }

    /// <summary>
    /// Perforce Changelist 结果
    /// </summary>
    public class VcsPerforceChangelist
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public string ChangeNumber { get; set; }
        public string Status { get; set; }
        public string Description { get; set; }
        public string User { get; set; }
        public string Date { get; set; }
        public List<string> Files { get; set; } = new List<string>();
        public string RawOutput { get; set; }
    }
}
