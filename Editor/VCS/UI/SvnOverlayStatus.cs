using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;

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
        /// <summary>本工作副本持有锁。</summary>
        LockedByMe = 1 << 7,
        /// <summary>仓库中被其他人（或其他工作副本）锁定，来自最近一次远端检查（svn status -u）。</summary>
        LockedByOther = 1 << 8,
        /// <summary>文件夹内有改动（由子项向上传播）。</summary>
        ChildChanged = 1 << 9,

        LocalChangeMask = Modified | Added | Deleted | Conflicted | Unversioned | Missing
    }

    /// <summary>
    /// 角标快照：键为 Unity 资产路径（<c>Assets/...</c>，正斜杠，比较忽略大小写）。
    /// .meta 的状态并入对应资产，因为 Project 窗口不显示 .meta。
    /// </summary>
    public sealed class SvnOverlaySnapshot
    {
        public readonly Dictionary<string, SvnOverlayFlags> Flags = new Dictionary<string, SvnOverlayFlags>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> LockOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>解析过程中 XML 不完整（输出被截断等）；已解析的部分仍然有效。</summary>
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
    }

    /// <summary>
    /// 解析 <c>svn status --xml</c>、<c>svn propget svn:needs-lock -R --xml</c> 与 <c>svn status -u --xml</c> 的输出，
    /// 生成 Project 窗口角标。纯逻辑，不调用 Unity API，可在线程池执行。
    /// <para>
    /// 路径换算：svn 输出的路径相对于命令的工作目录（<paramref name="baseDirectory"/>，可为工作副本根或 Unity 工程根），
    /// 也可能是绝对路径（propget 对绝对目标）；统一经 <see cref="VcsAssetPaths.ToUnityRelativePath"/> 换算，
    /// Unity 工程之外的条目丢弃。
    /// </para>
    /// </summary>
    public static class SvnOverlayStatusParser
    {
        private const string MetaExtension = ".meta";

        /// <summary>解析本地 <c>svn status --xml</c>：改动状态与本工作副本持有的锁。</summary>
        public static void ApplyStatusXml(string xml, string baseDirectory, string unityRoot, SvnOverlaySnapshot snapshot)
        {
            ForEachEntry(xml, snapshot, entry =>
            {
                var assetPath = ToAssetKey(entry.Path, baseDirectory, unityRoot);
                if (assetPath == null)
                    return;

                var flags = MapItem(entry.WcItem, entry.WcProps, entry.TreeConflicted);
                if (!string.IsNullOrEmpty(entry.WcLockToken))
                    flags |= SvnOverlayFlags.LockedByMe;
                snapshot.Add(assetPath, flags);
            });
        }

        /// <summary>
        /// 解析远端检查的 <c>svn status -u --xml</c>：仓库锁的 token 与本地锁不同（或本地无锁）即为他人锁定。
        /// 只取锁信息，改动状态以本地 status 为准。
        /// </summary>
        public static void ApplyRemoteLocksXml(string xml, string baseDirectory, string unityRoot, SvnOverlaySnapshot snapshot)
        {
            ForEachEntry(xml, snapshot, entry =>
            {
                if (string.IsNullOrEmpty(entry.ReposLockToken) && string.IsNullOrEmpty(entry.ReposLockOwner))
                    return;
                if (!string.IsNullOrEmpty(entry.WcLockToken) && entry.WcLockToken == entry.ReposLockToken)
                    return; // 自己的锁

                var assetPath = ToAssetKey(entry.Path, baseDirectory, unityRoot);
                if (assetPath == null)
                    return;
                snapshot.Add(assetPath, SvnOverlayFlags.LockedByOther);
                if (!string.IsNullOrEmpty(entry.ReposLockOwner))
                    snapshot.LockOwners[assetPath] = entry.ReposLockOwner;
            });
        }

        /// <summary>解析 <c>svn propget svn:needs-lock -R --xml</c>：每个 target 即带该属性的文件。</summary>
        public static void ApplyNeedsLockXml(string xml, string baseDirectory, string unityRoot, SvnOverlaySnapshot snapshot)
        {
            if (string.IsNullOrWhiteSpace(xml))
                return;
            try
            {
                using (var reader = XmlReader.Create(new StringReader(xml), CreateSettings()))
                {
                    while (reader.Read())
                    {
                        if (reader.NodeType != XmlNodeType.Element || reader.Name != "target")
                            continue;
                        var assetPath = ToAssetKey(reader.GetAttribute("path"), baseDirectory, unityRoot);
                        if (assetPath != null)
                            snapshot.Add(assetPath, SvnOverlayFlags.NeedsLock);
                    }
                }
            }
            catch (XmlException)
            {
                snapshot.IsIncomplete = true;
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

        private static SvnOverlayFlags MapItem(string item, string props, bool treeConflicted)
        {
            var flags = SvnOverlayFlags.None;
            if (treeConflicted)
                flags |= SvnOverlayFlags.Conflicted;

            switch (item)
            {
                case "modified":
                case "replaced":
                case "merged":
                case "obstructed":
                case "incomplete":
                    flags |= SvnOverlayFlags.Modified;
                    break;
                case "added":
                    flags |= SvnOverlayFlags.Added;
                    break;
                case "deleted":
                    flags |= SvnOverlayFlags.Deleted;
                    break;
                case "conflicted":
                    flags |= SvnOverlayFlags.Conflicted;
                    break;
                case "unversioned":
                    flags |= SvnOverlayFlags.Unversioned;
                    break;
                case "missing":
                    flags |= SvnOverlayFlags.Missing;
                    break;
            }

            if (props == "modified")
                flags |= SvnOverlayFlags.Modified;
            else if (props == "conflicted")
                flags |= SvnOverlayFlags.Conflicted;
            return flags;
        }

        private sealed class Entry
        {
            public string Path;
            public string WcItem;
            public string WcProps;
            public bool TreeConflicted;
            public string WcLockToken;
            public string ReposLockToken;
            public string ReposLockOwner;
        }

        private static XmlReaderSettings CreateSettings()
        {
            return new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreWhitespace = true
            };
        }

        /// <summary>
        /// 流式遍历 status XML 的 entry；XML 被截断时保留已读到的条目并标记 <see cref="SvnOverlaySnapshot.IsIncomplete"/>。
        /// </summary>
        private static void ForEachEntry(string xml, SvnOverlaySnapshot snapshot, Action<Entry> onEntry)
        {
            if (string.IsNullOrWhiteSpace(xml))
                return;

            try
            {
                using (var reader = XmlReader.Create(new StringReader(xml), CreateSettings()))
                {
                    Entry current = null;
                    string section = null; // "wc-status" / "repos-status"
                    var inLock = false;
                    string textTarget = null;

                    while (reader.Read())
                    {
                        switch (reader.NodeType)
                        {
                            case XmlNodeType.Element:
                                var empty = reader.IsEmptyElement;
                                switch (reader.Name)
                                {
                                    case "entry":
                                        current = new Entry { Path = reader.GetAttribute("path") };
                                        if (empty)
                                        {
                                            onEntry(current);
                                            current = null;
                                        }
                                        break;
                                    case "wc-status":
                                        if (current != null)
                                        {
                                            current.WcItem = reader.GetAttribute("item");
                                            current.WcProps = reader.GetAttribute("props");
                                            current.TreeConflicted = reader.GetAttribute("tree-conflicted") == "true";
                                            section = empty ? null : "wc-status";
                                        }
                                        break;
                                    case "repos-status":
                                        if (current != null)
                                            section = empty ? null : "repos-status";
                                        break;
                                    case "lock":
                                        inLock = current != null && section != null && !empty;
                                        break;
                                    case "token":
                                    case "owner":
                                        textTarget = inLock && !empty ? reader.Name : null;
                                        break;
                                }
                                break;

                            case XmlNodeType.Text:
                            case XmlNodeType.CDATA:
                                if (current != null && textTarget != null)
                                {
                                    var value = reader.Value;
                                    if (section == "wc-status" && textTarget == "token")
                                        current.WcLockToken = value;
                                    else if (section == "repos-status" && textTarget == "token")
                                        current.ReposLockToken = value;
                                    else if (section == "repos-status" && textTarget == "owner")
                                        current.ReposLockOwner = value;
                                }
                                break;

                            case XmlNodeType.EndElement:
                                switch (reader.Name)
                                {
                                    case "entry":
                                        if (current != null)
                                            onEntry(current);
                                        current = null;
                                        section = null;
                                        inLock = false;
                                        break;
                                    case "wc-status":
                                    case "repos-status":
                                        section = null;
                                        break;
                                    case "lock":
                                        inLock = false;
                                        break;
                                    case "token":
                                    case "owner":
                                        textTarget = null;
                                        break;
                                }
                                break;
                        }
                    }
                }
            }
            catch (XmlException)
            {
                snapshot.IsIncomplete = true;
            }
        }
    }
}
