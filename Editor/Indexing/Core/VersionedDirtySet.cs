using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace AgentCore.Editor.Components.Indexing.Core
{
    /// <summary>
    /// 带版本号的脏路径集合（纯逻辑，无 Unity 依赖）。
    /// <para>
    /// 每次标记都为路径分配一个全局递增的新版本号。索引器先取 (path, version) 快照，
    /// 处理完后用“比较并删除”移除：只有版本号仍等于快照时的值才删除。
    /// 索引过程中同一文件再次变更会得到新版本号，删除失败，该路径保留到下一轮——变更不会丢失。
    /// </para>
    /// </summary>
    public sealed class VersionedDirtySet
    {
        private readonly ConcurrentDictionary<string, long> _versions;
        private long _versionCounter;

        /// <summary>
        /// 创建空集合。路径比较默认忽略大小写（Windows 文件系统）。
        /// </summary>
        public VersionedDirtySet(IEqualityComparer<string> comparer = null)
        {
            _versions = new ConcurrentDictionary<string, long>(comparer ?? StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>当前待处理路径数。</summary>
        public int Count => _versions.Count;

        /// <summary>
        /// 标记路径为脏，返回新分配的版本号。
        /// </summary>
        /// <param name="path">已规范化的路径。</param>
        /// <param name="isNew">标记前集合中是否不存在该路径。</param>
        public long Mark(string path, out bool isNew)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));

            var version = Interlocked.Increment(ref _versionCounter);
            var added = false;
            // 直接覆盖（不取 max）：版本号全局唯一，快照之后的任何标记都会让值发生变化
            _versions.AddOrUpdate(path, _ =>
            {
                added = true;
                return version;
            }, (_, __) => version);
            isNew = added;
            return version;
        }

        /// <summary>
        /// 标记路径为脏，返回新分配的版本号。
        /// </summary>
        public long Mark(string path) => Mark(path, out _);

        /// <summary>是否包含该路径。</summary>
        public bool Contains(string path) => !string.IsNullOrEmpty(path) && _versions.ContainsKey(path);

        /// <summary>
        /// 获取路径当前版本号；不存在时返回 false。
        /// </summary>
        public bool TryGetVersion(string path, out long version)
        {
            version = 0;
            return !string.IsNullOrEmpty(path) && _versions.TryGetValue(path, out version);
        }

        /// <summary>
        /// 取快照（不删除），按版本号升序（最早标记的优先）。
        /// </summary>
        /// <param name="max">最多返回条数。</param>
        public List<KeyValuePair<string, long>> Snapshot(int max = int.MaxValue)
        {
            return _versions.ToArray()
                .OrderBy(kv => kv.Value)
                .Take(Math.Max(0, max))
                .ToList();
        }

        /// <summary>
        /// 比较并删除：仅当路径的当前版本号等于 <paramref name="version"/> 时删除。
        /// </summary>
        /// <returns>true 表示已删除；false 表示路径不存在或在快照后被再次标记。</returns>
        public bool TryRemove(string path, long version)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return ((ICollection<KeyValuePair<string, long>>)_versions)
                .Remove(new KeyValuePair<string, long>(path, version));
        }

        /// <summary>
        /// 对一批 (path, version) 执行比较并删除，返回实际删除的条数。
        /// </summary>
        public int RemoveProcessed(IEnumerable<KeyValuePair<string, long>> processed)
        {
            if (processed == null) return 0;
            var removed = 0;
            foreach (var kv in processed)
            {
                if (TryRemove(kv.Key, kv.Value))
                    removed++;
            }

            return removed;
        }

        /// <summary>
        /// 无条件删除路径（不比较版本，用于兼容旧调用方式）。
        /// </summary>
        public bool RemoveUnconditionally(string path)
        {
            return !string.IsNullOrEmpty(path) && _versions.TryRemove(path, out _);
        }

        /// <summary>清空集合。</summary>
        public void Clear() => _versions.Clear();

        /// <summary>所有路径（无序快照）。</summary>
        public List<string> Paths() => _versions.Keys.ToList();
    }
}
