using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace AgentCore.Editor.Components.VCS.Tools
{
    /// <summary>
    /// <c>svn status --xml</c> 解析结果。
    /// </summary>
    public sealed class SvnStatusParseResult
    {
        /// <summary>本地有变更（或冲突）的条目；normal / ignored 条目不包含在内。</summary>
        public List<VcsFileStatus> Files { get; } = new List<VcsFileStatus>();

        /// <summary>远端有更新的路径（仅 <c>svn status -u --xml</c> 输出含 repos-status 时有值）。</summary>
        public List<string> RemoteChangedFiles { get; } = new List<string>();

        /// <summary><c>&lt;against revision="N"/&gt;</c> 中的远端版本号（-u 时才有）。</summary>
        public string AgainstRevision { get; set; }

        /// <summary>XML 不完整（输出被截断等），已解析出的条目仍然保留。</summary>
        public bool IsIncomplete { get; set; }

        /// <summary>解析错误信息（IsIncomplete 时有值）。</summary>
        public string Error { get; set; }
    }

    /// <summary>
    /// 解析 <c>svn status --xml</c> / <c>svn status -u --xml</c>。
    /// <para>
    /// 用 XmlReader 流式读取：输出被截断时仍能拿到截断点之前的条目，并标记 <see cref="SvnStatusParseResult.IsIncomplete"/>，
    /// 而不是整体解析失败后静默返回空列表。
    /// </para>
    /// <para>
    /// 冲突判定覆盖三种情况：<c>item="conflicted"</c>（文本冲突）、<c>props="conflicted"</c>（属性冲突）、
    /// <c>tree-conflicted="true"</c>（树冲突）。只改了属性（<c>item="normal" props="modified"</c>）也算 Modified。
    /// </para>
    /// </summary>
    public static class SvnStatusXmlParser
    {
        public static SvnStatusParseResult Parse(string xml)
        {
            var result = new SvnStatusParseResult();
            if (string.IsNullOrWhiteSpace(xml))
                return result;

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                IgnoreComments = true,
                IgnoreWhitespace = true
            };

            string path = null;
            string item = null, props = null, treeConflicted = null;
            string reposItem = null, reposProps = null;
            var inEntry = false;

            try
            {
                using (var reader = XmlReader.Create(new StringReader(xml), settings))
                {
                    while (reader.Read())
                    {
                        if (reader.NodeType == XmlNodeType.Element)
                        {
                            switch (reader.Name)
                            {
                                case "entry":
                                    path = reader.GetAttribute("path");
                                    item = props = treeConflicted = reposItem = reposProps = null;
                                    inEntry = !reader.IsEmptyElement;
                                    if (reader.IsEmptyElement)
                                        AddEntry(result, path, null, null, null, null, null);
                                    break;
                                case "wc-status":
                                    if (inEntry)
                                    {
                                        item = reader.GetAttribute("item");
                                        props = reader.GetAttribute("props");
                                        treeConflicted = reader.GetAttribute("tree-conflicted");
                                    }
                                    break;
                                case "repos-status":
                                    if (inEntry)
                                    {
                                        reposItem = reader.GetAttribute("item");
                                        reposProps = reader.GetAttribute("props");
                                    }
                                    break;
                                case "against":
                                    result.AgainstRevision = reader.GetAttribute("revision");
                                    break;
                            }
                        }
                        else if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "entry" && inEntry)
                        {
                            AddEntry(result, path, item, props, treeConflicted, reposItem, reposProps);
                            inEntry = false;
                        }
                    }
                }
            }
            catch (XmlException ex)
            {
                // 截断点之前完整的 entry 已加入结果；未闭合的那一条丢弃
                result.IsIncomplete = true;
                result.Error = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// wc-status 属性 → 文件状态。返回 null 表示无本地变更（normal / none / external 且属性未改）。
        /// </summary>
        public static VcsFileState? MapWorkingCopyState(string item, string props, string treeConflicted, out string description)
        {
            description = null;

            if (item == "conflicted")
            {
                description = "Conflicted";
                return VcsFileState.Conflicted;
            }
            if (string.Equals(treeConflicted, "true", StringComparison.OrdinalIgnoreCase))
            {
                description = "Conflicted (tree conflict)";
                return VcsFileState.Conflicted;
            }
            if (props == "conflicted")
            {
                description = "Conflicted (property conflict)";
                return VcsFileState.Conflicted;
            }

            switch (item)
            {
                case "modified":
                    description = props == "modified" ? "Modified (content + properties)" : "Modified";
                    return VcsFileState.Modified;
                case "replaced":
                    description = "Replaced";
                    return VcsFileState.Modified;
                case "added":
                    description = "Added";
                    return VcsFileState.Added;
                case "deleted":
                    description = "Deleted";
                    return VcsFileState.Deleted;
                case "unversioned":
                    description = "Unversioned";
                    return VcsFileState.Untracked;
                case "ignored":
                    description = "Ignored";
                    return VcsFileState.Ignored;
                case "missing":
                    description = "Missing";
                    return VcsFileState.Missing;
                case "obstructed":
                    description = "Obstructed (versioned item replaced by a different kind)";
                    return VcsFileState.Modified;
                case "incomplete":
                    description = "Incomplete (interrupted checkout/update)";
                    return VcsFileState.Modified;
            }

            // normal / none / external 等：只有属性改动时才算变更
            if (props == "modified")
            {
                description = "Properties modified";
                return VcsFileState.Modified;
            }

            return null;
        }

        private static void AddEntry(SvnStatusParseResult result, string path, string item, string props, string treeConflicted, string reposItem, string reposProps)
        {
            if (string.IsNullOrEmpty(path))
                return;

            var state = MapWorkingCopyState(item, props, treeConflicted, out var description);
            if (state.HasValue && state.Value != VcsFileState.Ignored)
            {
                result.Files.Add(new VcsFileStatus
                {
                    FilePath = path,
                    State = state.Value,
                    StateDescription = description,
                    StatusCode = item
                });
            }

            // repos-status item/props 非 none 即远端有更新
            if ((!string.IsNullOrEmpty(reposItem) && reposItem != "none")
                || (!string.IsNullOrEmpty(reposProps) && reposProps != "none"))
            {
                result.RemoteChangedFiles.Add(path);
            }
        }
    }
}
