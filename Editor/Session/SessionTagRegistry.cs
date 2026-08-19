using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentCore.Editor.Utils;
using Newtonsoft.Json;

namespace AgentCore.Editor.Session
{
    /// <summary>tag 元数据条目：名称 + 显示顺序（越小越靠前）+ 是否置顶。未来可扩展 Color 等字段。</summary>
    public class TagMetadata
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("order")]
        public int Order { get; set; }

        /// <summary>是否置顶。置顶 tag 在任何排序模式下恒排在最前（不进排序键，仅按置顶优先）。</summary>
        [JsonProperty("pinned", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool IsPinned { get; set; }
    }

    /// <summary>tag 列表排序模式（持久化到 EditorPrefs）。</summary>
    public enum SessionTagSortMode
    {
        /// <summary>手动顺序：registry 的 order 字段 + 未登记字典序（默认，等同历史行为）。</summary>
        Manual = 0,
        /// <summary>按 tag 名称字母序（A→Z，大小写不敏感）。</summary>
        Name = 1,
        /// <summary>按修改时间：tag 内 session 最近 UpdatedAt 最大者优先（降序）。</summary>
        Modified = 2,
        /// <summary>按创建时间：tag 内 session 最早 CreatedAt 最小者优先（降序，即最老在前）。</summary>
        Created = 3
    }

    /// <summary>tag registry 文件根对象（预留未来加其他配置的余地）。</summary>
    internal class TagRegistryFile
    {
        [JsonProperty("tags")]
        public List<TagMetadata> Tags { get; set; } = new List<TagMetadata>();
    }

    /// <summary>
    /// Session tag 元数据仓库。存 tag 排序、便于未来加 tag 颜色等属性。
    /// 存储路径: {ProjectRoot}/Library/AgentCore/session-tags.json
    /// </summary>
    public static class SessionTagRegistry
    {
        private const string LogPrefix = "[SessionTagRegistry] ";
        private const string FileName = "session-tags.json";

        private static string GetFilePath()
        {
            var dir = Path.Combine(UnityEngine.Application.dataPath, "..", "Library", "AgentCore");
            return Path.Combine(dir, FileName);
        }

        /// <summary>加载所有 tag 元数据。文件不存在时返回空列表。</summary>
        public static List<TagMetadata> LoadAll()
        {
            try
            {
                var path = GetFilePath();
                if (!File.Exists(path)) return new List<TagMetadata>();
                var json = File.ReadAllText(path);
                var file = JsonConvert.DeserializeObject<TagRegistryFile>(json);
                return file?.Tags ?? new List<TagMetadata>();
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"{LogPrefix}Failed to load tag registry: {ex.Message}");
                return new List<TagMetadata>();
            }
        }

        /// <summary>保存所有 tag 元数据。会重写文件。</summary>
        public static void SaveAll(List<TagMetadata> tags)
        {
            try
            {
                var path = GetFilePath();
                var dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var file = new TagRegistryFile { Tags = tags ?? new List<TagMetadata>() };
                var json = JsonConvert.SerializeObject(file, Formatting.Indented);
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"{LogPrefix}Failed to save tag registry: {ex.Message}");
            }
        }

        /// <summary>获取所有 tag 名称 -> Order 的字典。未登记的 tag 不在其中。大小写不敏感。</summary>
        public static Dictionary<string, int> LoadOrderMap()
        {
            var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in LoadAll())
            {
                if (!string.IsNullOrEmpty(t.Name) && !dict.ContainsKey(t.Name))
                    dict[t.Name] = t.Order;
            }
            return dict;
        }

        /// <summary>
        /// 重命名 tag：更新 registry + 批量更新所有引用该 tag 的 session 的 Tag 字段。
        /// oldName 与 newName 相同或 newName 为空 -> 直接返回。
        /// </summary>
        public static void RenameTag(string oldName, string newName)
        {
            if (string.IsNullOrEmpty(oldName)) return;
            if (string.IsNullOrWhiteSpace(newName)) return;
            var trimmedNew = newName.Trim();
            if (string.Equals(oldName, trimmedNew, StringComparison.Ordinal)) return;

            // 1. 更新 registry：如果 oldName 已登记则改名；如果 newName 已登记则合并（保留 newName 的 order）。
            var all = LoadAll();
            var oldEntry = all.FirstOrDefault(t => string.Equals(t.Name, oldName, StringComparison.OrdinalIgnoreCase));
            var newEntry = all.FirstOrDefault(t => string.Equals(t.Name, trimmedNew, StringComparison.OrdinalIgnoreCase));

            if (newEntry != null)
            {
                // 合并：把 oldEntry 删掉，session 都指向 newEntry.Name。
                if (oldEntry != null) all.Remove(oldEntry);
            }
            else if (oldEntry != null)
            {
                oldEntry.Name = trimmedNew;
            }
            else
            {
                // 老 tag 未登记：新建一个占位条目，用 max+1 作为 order（放最后）。
                var maxOrder = all.Count > 0 ? all.Max(t => t.Order) : -1;
                all.Add(new TagMetadata { Name = trimmedNew, Order = maxOrder + 1 });
            }
            SaveAll(all);

            // 2. 批量更新 session
            var manager = SessionManager.Instance;
            var affected = manager.GetSessionList()
                .Where(s => string.Equals(s.Tag, oldName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var s in affected)
            {
                manager.SetSessionTag(s.Id, trimmedNew);
            }

            AgentCoreLog.Info($"{LogPrefix}Renamed tag {oldName} -> {trimmedNew} ({affected.Count} sessions updated).");
        }

        /// <summary>把 tag 置顶（IsPinned=true，保留其 order 以便取消置顶后恢复原顺序）。</summary>
        public static void PinTagToTop(string tagName)
        {
            if (string.IsNullOrEmpty(tagName)) return;
            var all = LoadAll();
            EnsureRegistered(all, tagName);
            var entry = all.FirstOrDefault(t => string.Equals(t.Name, tagName, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return;
            entry.IsPinned = true;
            SaveAll(all);
        }

        /// <summary>取消置顶（IsPinned=false）。仅当该 tag 当前已置顶时有效；未登记则先登记（不置顶）。</summary>
        public static void UnpinTag(string tagName)
        {
            if (string.IsNullOrEmpty(tagName)) return;
            var all = LoadAll();
            EnsureRegistered(all, tagName);
            var entry = all.FirstOrDefault(t => string.Equals(t.Name, tagName, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return;
            entry.IsPinned = false;
            SaveAll(all);
        }

        /// <summary>
        /// 删除 tag：把所有引用该 tag 的 session 的 Tag 字段清空（变未分类），
        /// 并从 registry 移除该条目。
        /// </summary>
        public static void DeleteTag(string tagName)
        {
            if (string.IsNullOrEmpty(tagName)) return;

            // 1. 从 registry 移除
            var all = LoadAll();
            var entry = all.FirstOrDefault(t => string.Equals(t.Name, tagName, StringComparison.OrdinalIgnoreCase));
            if (entry != null)
            {
                all.Remove(entry);
                SaveAll(all);
            }

            // 2. 批量清空 session.Tag
            var manager = SessionManager.Instance;
            var affected = manager.GetSessionList()
                .Where(s => string.Equals(s.Tag, tagName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var s in affected)
            {
                manager.SetSessionTag(s.Id, null);
            }

            AgentCoreLog.Info($"{LogPrefix}Deleted tag {tagName} ({affected.Count} sessions unassigned).");
        }

        /// <summary>确保 tag 在 registry 里有一条记录；若无则追加到最后。修改就地生效于 all。</summary>
        private static void EnsureRegistered(List<TagMetadata> all, string tagName)
        {
            if (all.Any(t => string.Equals(t.Name, tagName, StringComparison.OrdinalIgnoreCase))) return;
            var maxOrder = all.Count > 0 ? all.Max(t => t.Order) : -1;
            all.Add(new TagMetadata { Name = tagName, Order = maxOrder + 1 });
        }

        #region 排序模式（EditorPrefs 持久化）

        private const string SortModePrefKey = "AgentCore.SessionOrg.SortMode";

        /// <summary>读取当前 tag 排序模式（默认 Created=按创建时间；菜单不含 Manual，此默认兜底旧 EditorPrefs 值 0=Manual）。</summary>
        public static SessionTagSortMode GetSortMode()
        {
            return (SessionTagSortMode)UnityEditor.EditorPrefs.GetInt(SortModePrefKey, (int)SessionTagSortMode.Created);
        }

        /// <summary>保存 tag 排序模式。</summary>
        public static void SetSortMode(SessionTagSortMode mode)
        {
            UnityEditor.EditorPrefs.SetInt(SortModePrefKey, (int)mode);
        }

        /// <summary>查询指定 tag 是否已置顶。未登记或不存在返回 false。</summary>
        public static bool IsTagPinned(string tagName)
        {
            if (string.IsNullOrEmpty(tagName)) return false;
            var entry = LoadAll().FirstOrDefault(t => string.Equals(t.Name, tagName, StringComparison.OrdinalIgnoreCase));
            return entry != null && entry.IsPinned;
        }

        #endregion
    }
}
