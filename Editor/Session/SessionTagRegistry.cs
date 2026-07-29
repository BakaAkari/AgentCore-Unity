using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentCore.Editor.Utils;
using Newtonsoft.Json;

namespace AgentCore.Editor.Session
{
    /// <summary>tag 元数据条目：名称 + 显示顺序（越小越靠前）。未来可扩展 Color 等字段。</summary>
    public class TagMetadata
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("order")]
        public int Order { get; set; }
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

        /// <summary>把 tag 置顶（order = min-1 或 0）。</summary>
        public static void PinTagToTop(string tagName)
        {
            if (string.IsNullOrEmpty(tagName)) return;
            var all = LoadAll();
            EnsureRegistered(all, tagName);
            // 找到最小 order，置为它 - 1（或者重新分配整个数组）。
            // 简单起见：把目标移到最前，然后重排 0..N-1 保持稳定。
            var entry = all.FirstOrDefault(t => string.Equals(t.Name, tagName, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return;
            all.Remove(entry);
            all.Insert(0, entry);
            RenumberOrder(all);
            SaveAll(all);
        }

        /// <summary>tag 上移一位。若已在顶部则不动。</summary>
        public static void MoveTagUp(string tagName)
        {
            if (string.IsNullOrEmpty(tagName)) return;
            var all = LoadAll();
            EnsureRegistered(all, tagName);
            all = all.OrderBy(t => t.Order).ToList();
            var idx = all.FindIndex(t => string.Equals(t.Name, tagName, StringComparison.OrdinalIgnoreCase));
            if (idx <= 0) return;
            (all[idx - 1], all[idx]) = (all[idx], all[idx - 1]);
            RenumberOrder(all);
            SaveAll(all);
        }

        /// <summary>tag 下移一位。若已在底部则不动。</summary>
        public static void MoveTagDown(string tagName)
        {
            if (string.IsNullOrEmpty(tagName)) return;
            var all = LoadAll();
            EnsureRegistered(all, tagName);
            all = all.OrderBy(t => t.Order).ToList();
            var idx = all.FindIndex(t => string.Equals(t.Name, tagName, StringComparison.OrdinalIgnoreCase));
            if (idx < 0 || idx >= all.Count - 1) return;
            (all[idx + 1], all[idx]) = (all[idx], all[idx + 1]);
            RenumberOrder(all);
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

        /// <summary>把当前顺序重新赋值为 0..N-1，保持稳定。传入的 list 应已按目标顺序排列。</summary>
        private static void RenumberOrder(List<TagMetadata> ordered)
        {
            for (int i = 0; i < ordered.Count; i++)
            {
                ordered[i].Order = i;
            }
        }
    }
}
