using System;
using System.Collections.Generic;
using AgentCore.Editor.Components.VCS.Tools;

namespace AgentCore.Editor.Components.VCS.UI
{
    /// <summary>
    /// Project 窗口 SVN 角标的状态位。
    /// </summary>
    [Flags]
    public enum SvnOverlayFlags
    {
        None = 0,
        Modified = 1 << 0,
        Added = 1 << 1,
        Deleted = 1 << 2,
        Conflicted = 1 << 3,
        Unversioned = 1 << 4,
        Missing = 1 << 5,
        /// <summary>带 svn:needs-lock 属性（未加锁时工作副本中为只读）。</summary>
        NeedsLock = 1 << 6,
        /// <summary>本工作副本持有锁（K）。</summary>
        LockedByMe = 1 << 7,
        /// <summary>仓库中被其他人（或其他工作副本）锁定（O），来自最近一次远端检查（svn status -u）。</summary>
        LockedByOther = 1 << 8,
        /// <summary>文件夹内有改动（由子项向上传播）。</summary>
        ChildChanged = 1 << 9,
        /// <summary>本工作副本的锁已被他人抢走（T）或已失效（B），来自最近一次远端检查。</summary>
        LockLost = 1 << 10,

        LocalChangeMask = Modified | Added | Deleted | Conflicted | Unversioned | Missing
    }

    /// <summary>
    /// 角标快照：键为 Unity 资产路径（<c>Assets/...</c>，正斜杠，比较忽略大小写）。
    /// .meta 的状态并入对应资产，因为 Project 窗口不显示 .meta。
    /// </summary>
    public sealed class SvnOverlaySnapshot
    {
        public readonly Dictionary<string, SvnOverlayFlags> Flags = new Dictionary<string, SvnOverlayFlags>(StringComparer.OrdinalIgnoreCase);

        /// <summary>锁持有者（他人锁定 / 抢走锁的人 / 本人锁定时的用户名）。</summary>
        public readonly Dictionary<string, string> LockOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>锁注释（svn lock -m）。</summary>
        public readonly Dictionary<string, string> LockComments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>解析过程中输出不完整（被截断等）；已解析的部分仍然有效。</summary>
        public bool IsIncomplete;

        public bool IsEmpty => Flags.Count == 0;

        public SvnOverlayFlags Get(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return SvnOverlayFlags.None;
            return Flags.TryGetValue(assetPath, out var flags) ? flags : SvnOverlayFlags.None;
        }

        public void Add(string assetPath, SvnOverlayFlags flags)
        {
            if (string.IsNullOrEmpty(assetPath) || flags == SvnOverlayFlags.None)
                return;
            Flags.TryGetValue(assetPath, out var existing);
            Flags[assetPath] = existing | flags;
        }

        /// <summary>记录（或清除）锁持有者与注释。</summary>
        public void SetLockInfo(string assetPath, string owner, string comment)
        {
            if (string.IsNullOrEmpty(assetPath))
                return;
            if (string.IsNullOrEmpty(owner))
                LockOwners.Remove(assetPath);
            else
                LockOwners[assetPath] = owner;
            if (string.IsNullOrEmpty(comment))
                LockComments.Remove(assetPath);
            else
                LockComments[assetPath] = comment;
        }
    }

    /// <summary>
    /// 由 SvnAdapter 的结构化结果（<see cref="VcsFileStatus"/>：State / LockState / LockOwner / LockComment）
    /// 与整树 needs-lock propget 生成 Project 窗口角标。不再自行解析 status XML（解析统一在 <see cref="SvnStatusXmlParser"/>）。
    /// 纯逻辑，不调用 Unity API，可在线程池执行。
    /// <para>
    /// 路径换算：适配器返回的路径相对于其工作目录（<c>baseDirectory</c>，即工作副本根），propget 也可能给出绝对路径；
    /// 统一经 <see cref="VcsAssetPaths.ToUnityRelativePath"/> 换算，Unity 工程之外的条目丢弃，.meta 并入资产本身。
    /// </para>
    /// </summary>
    public static class SvnOverlayStatusParser
    {
        private const string MetaExtension = ".meta";

        /// <summary>
        /// 本地状态（svn status，不带 -u）：改动状态来自 <see cref="VcsStatusResult.Files"/>，
        /// 本工作副本持有的锁（K）来自 <see cref="VcsStatusResult.LockedFiles"/>。
        /// </summary>
        public static void ApplyLocalStatus(VcsStatusResult status, string baseDirectory, string unityRoot, SvnOverlaySnapshot snapshot)
        {
            if (status == null || snapshot == null)
                return;
            if (status.Truncated)
                snapshot.IsIncomplete = true;
            ApplyLocalStatus(status.Files, status.LockedFiles, baseDirectory, unityRoot, snapshot);
        }

        /// <inheritdoc cref="ApplyLocalStatus(VcsStatusResult, string, string, SvnOverlaySnapshot)"/>
        public static void ApplyLocalStatus(IEnumerable<VcsFileStatus> files, IEnumerable<VcsFileStatus> lockedFiles,
            string baseDirectory, string unityRoot, SvnOverlaySnapshot snapshot)
        {
            if (snapshot == null)
                return;

            if (files != null)
            {
                foreach (var file in files)
                {
                    if (file == null)
                        continue;
                    var key = ToAssetKey(file.FilePath, baseDirectory, unityRoot);
                    if (key != null)
                        snapshot.Add(key, MapState(file.State));
                }
            }

            if (lockedFiles != null)
            {
                foreach (var file in lockedFiles)
                {
                    // 不带 -u 时只可能是 K；T / B 的判定需要远端信息，见 ApplyRemoteLocks
                    if (file == null || file.LockState != "K")
                        continue;
                    var key = ToAssetKey(file.FilePath, baseDirectory, unityRoot);
                    if (key == null)
                        continue;
                    snapshot.Add(key, SvnOverlayFlags.LockedByMe);
                    snapshot.SetLockInfo(key, file.LockOwner, file.LockComment);
                }
            }
        }

        /// <summary>
        /// 远端检查（svn status -u）的锁：O → 他人锁定（附持有者 / 注释）；T / B → 本人的锁被抢 / 已失效。
        /// 本地状态比远端快照新：本地已持有锁（K）时忽略过期的 O；本地已没有锁时忽略过期的 T / B。
        /// 需在 ApplyLocalStatus 之后调用。
        /// </summary>
        public static void ApplyRemoteLocks(IEnumerable<VcsFileStatus> lockedFiles, string baseDirectory, string unityRoot, SvnOverlaySnapshot snapshot)
        {
            if (lockedFiles == null || snapshot == null)
                return;

            foreach (var file in lockedFiles)
            {
                if (file == null || string.IsNullOrEmpty(file.LockState))
                    continue;
                var key = ToAssetKey(file.FilePath, baseDirectory, unityRoot);
                if (key == null)
                    continue;

                var current = snapshot.Get(key);
                var lockedByMeNow = (current & SvnOverlayFlags.LockedByMe) != 0;
                switch (file.LockState)
                {
                    case "O":
                        if (lockedByMeNow)
                            continue;
                        snapshot.Add(key, SvnOverlayFlags.LockedByOther);
                        snapshot.SetLockInfo(key, file.LockOwner, file.LockComment);
                        break;
                    case "T":
                    case "B":
                        if (!lockedByMeNow)
                            continue;
                        snapshot.Flags[key] = (current & ~SvnOverlayFlags.LockedByMe) | SvnOverlayFlags.LockLost;
                        // T：仓库里现在是别人的锁，记录新持有者；B：锁已不存在
                        if (file.LockState == "T")
                            snapshot.SetLockInfo(key, file.LockOwner, file.LockComment);
                        else
                            snapshot.SetLockInfo(key, null, null);
                        break;
                }
            }
        }

        /// <summary>整树 <c>svn propget svn:needs-lock -R --xml</c> 的输出：每个 target 即带该属性的文件。</summary>
        public static void ApplyNeedsLockXml(string xml, string baseDirectory, string unityRoot, SvnOverlaySnapshot snapshot)
        {
            if (snapshot == null)
                return;
            var targets = SvnStatusXmlParser.ParsePropgetTargets(xml, out var incomplete);
            if (incomplete)
                snapshot.IsIncomplete = true;
            ApplyNeedsLock(targets, baseDirectory, unityRoot, snapshot);
        }

        /// <summary>带 svn:needs-lock 的路径列表。</summary>
        public static void ApplyNeedsLock(IEnumerable<string> paths, string baseDirectory, string unityRoot, SvnOverlaySnapshot snapshot)
        {
            if (paths == null || snapshot == null)
                return;
            foreach (var path in paths)
            {
                var key = ToAssetKey(path, baseDirectory, unityRoot);
                if (key != null)
                    snapshot.Add(key, SvnOverlayFlags.NeedsLock);
            }
        }

        /// <summary>把子项的本地改动 / 冲突向上传播到所有祖先文件夹（不含 <c>Assets</c> 之上）。</summary>
        public static void PropagateToFolders(SvnOverlaySnapshot snapshot)
        {
            var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in snapshot.Flags)
            {
                if ((pair.Value & SvnOverlayFlags.LocalChangeMask) == 0)
                    continue;
                var path = pair.Key;
                var slash = path.LastIndexOf('/');
                while (slash > 0)
                {
                    path = path.Substring(0, slash);
                    if (!parents.Add(path))
                        break; // 更上层已处理过
                    slash = path.LastIndexOf('/');
                }
            }

            foreach (var parent in parents)
                snapshot.Add(parent, SvnOverlayFlags.ChildChanged);
        }

        /// <summary>
        /// 把 svn 路径换算为角标键：Unity 资产路径，.meta 并入资产本身；不在 Assets / Packages 下返回 null。
        /// </summary>
        public static string ToAssetKey(string svnPath, string baseDirectory, string unityRoot)
        {
            var relative = VcsAssetPaths.ToUnityRelativePath(svnPath, baseDirectory, unityRoot);
            if (relative != null && relative.EndsWith(MetaExtension, StringComparison.OrdinalIgnoreCase))
                relative = relative.Substring(0, relative.Length - MetaExtension.Length);
            return VcsAssetPaths.IsAssetDatabasePath(relative) ? relative : null;
        }

        /// <summary>适配器的文件状态 → 角标位。</summary>
        public static SvnOverlayFlags MapState(VcsFileState state)
        {
            switch (state)
            {
                case VcsFileState.Modified:
                    return SvnOverlayFlags.Modified;
                case VcsFileState.Added:
                case VcsFileState.Renamed:
                case VcsFileState.Copied:
                    return SvnOverlayFlags.Added;
                case VcsFileState.Deleted:
                    return SvnOverlayFlags.Deleted;
                case VcsFileState.Conflicted:
                    return SvnOverlayFlags.Conflicted;
                case VcsFileState.Untracked:
                    return SvnOverlayFlags.Unversioned;
                case VcsFileState.Missing:
                    return SvnOverlayFlags.Missing;
                default:
                    return SvnOverlayFlags.None;
            }
        }
    }
}
