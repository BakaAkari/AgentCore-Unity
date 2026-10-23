using System;
using System.Collections.Generic;
using System.IO;

namespace AgentCore.Editor.Components.VCS.UI
{
    /// <summary>
    /// VCS 路径与 Unity 资产路径之间的映射，以及 .meta 伴随文件处理。纯字符串 / 文件系统逻辑，不调用 Unity API，任意线程可用。
    /// <para>
    /// SVN 状态里的路径相对于<b>工作副本根</b>（svn status 的工作目录）；Unity 工程常位于工作副本的子目录
    /// （如 <c>client/unity/Assets/...</c>），不能直接当成 <c>Assets/...</c> 使用，需要按两者的绝对路径换算。
    /// </para>
    /// </summary>
    public static class VcsAssetPaths
    {
        private const string MetaExtension = ".meta";

        /// <summary>统一为正斜杠并去掉首尾斜杠（不改变大小写）。</summary>
        public static string Normalize(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/').Trim('/');
        }

        /// <summary>
        /// 把相对于 <paramref name="baseDirectory"/>（通常为工作副本根）的 VCS 路径换算为 Unity 工程相对路径
        /// （如 <c>Assets/Foo.prefab</c>）。路径不在 Unity 工程内时返回 null。
        /// </summary>
        public static string ToUnityRelativePath(string vcsRelativePath, string baseDirectory, string unityRoot)
        {
            if (string.IsNullOrWhiteSpace(vcsRelativePath) || string.IsNullOrWhiteSpace(baseDirectory) || string.IsNullOrWhiteSpace(unityRoot))
                return null;

            string absolute;
            try
            {
                absolute = Path.IsPathRooted(vcsRelativePath)
                    ? vcsRelativePath
                    : Path.Combine(baseDirectory, vcsRelativePath.Replace('/', Path.DirectorySeparatorChar));
                absolute = Path.GetFullPath(absolute);
            }
            catch (Exception)
            {
                return null;
            }

            return AbsoluteToUnityRelativePath(absolute, unityRoot);
        }

        /// <summary>
        /// 绝对路径 → Unity 工程相对路径（正斜杠）；不在工程内返回 null，等于工程根返回空字符串。
        /// </summary>
        public static string AbsoluteToUnityRelativePath(string absolutePath, string unityRoot)
        {
            if (string.IsNullOrWhiteSpace(absolutePath) || string.IsNullOrWhiteSpace(unityRoot))
                return null;

            string root;
            string full;
            try
            {
                root = Path.GetFullPath(unityRoot).Replace('\\', '/').TrimEnd('/');
                full = Path.GetFullPath(absolutePath).Replace('\\', '/').TrimEnd('/');
            }
            catch (Exception)
            {
                return null;
            }

            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            if (!full.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                return null;
            return full.Substring(root.Length + 1);
        }

        /// <summary>
        /// 把 VCS 路径换算为可交给 AssetDatabase 的资产路径（<c>Assets/...</c> 或 <c>Packages/...</c>），否则返回 null。
        /// </summary>
        public static string ToUnityAssetPath(string vcsRelativePath, string baseDirectory, string unityRoot)
        {
            var relative = ToUnityRelativePath(vcsRelativePath, baseDirectory, unityRoot);
            return IsAssetDatabasePath(relative) ? relative : null;
        }

        /// <summary>是否为 AssetDatabase 能识别的路径（Assets 或 Packages 下，含这两个目录本身）。</summary>
        public static bool IsAssetDatabasePath(string unityRelativePath)
        {
            if (string.IsNullOrEmpty(unityRelativePath))
                return false;
            return unityRelativePath == "Assets" || unityRelativePath.StartsWith("Assets/", StringComparison.Ordinal)
                || unityRelativePath == "Packages" || unityRelativePath.StartsWith("Packages/", StringComparison.Ordinal);
        }

        /// <summary>
        /// 为每个路径补上它的 .meta 伴随文件（以及 .meta 对应的资产本身），前提是伴随文件出现在 <paramref name="available"/> 中。
        /// 用于状态列表里的 Commit / Revert / Add：只选中资产时 .meta 的改动不会被漏掉，反之亦然。
        /// 保持原有顺序，伴随文件紧跟在原路径之后，不重复；比较时忽略大小写与斜杠方向，输出使用 <paramref name="available"/> 中的原始写法。
        /// </summary>
        public static List<string> WithMetaCompanions(IEnumerable<string> paths, IEnumerable<string> available)
        {
            var result = new List<string>();
            if (paths == null)
                return result;

            var availableByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (available != null)
            {
                foreach (var candidate in available)
                {
                    if (string.IsNullOrWhiteSpace(candidate))
                        continue;
                    var key = Normalize(candidate);
                    if (!availableByKey.ContainsKey(key))
                        availableByKey[key] = candidate;
                }
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                    continue;
                var key = Normalize(path);
                if (seen.Add(key))
                    result.Add(path);

                var companionKey = GetCompanionKey(key);
                if (companionKey != null && availableByKey.TryGetValue(companionKey, out var companion) && seen.Add(companionKey))
                    result.Add(companion);
            }

            return result;
        }

        /// <summary>
        /// 为每个绝对路径补上磁盘上存在的 .meta 伴随文件（Project 窗口选中资产 / 文件夹时使用）。
        /// </summary>
        public static List<string> WithMetaCompanionsOnDisk(IEnumerable<string> absolutePaths, Func<string, bool> exists)
        {
            var result = new List<string>();
            if (absolutePaths == null)
                return result;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in absolutePaths)
            {
                if (string.IsNullOrWhiteSpace(path))
                    continue;
                var trimmed = path.TrimEnd('/', '\\');
                if (seen.Add(Normalize(trimmed)))
                    result.Add(trimmed);

                if (trimmed.EndsWith(MetaExtension, StringComparison.OrdinalIgnoreCase))
                    continue;
                var meta = trimmed + MetaExtension;
                bool metaExists;
                try { metaExists = exists != null && exists(meta); }
                catch { metaExists = false; }
                if (metaExists && seen.Add(Normalize(meta)))
                    result.Add(meta);
            }

            return result;
        }

        private static string GetCompanionKey(string normalizedPath)
        {
            if (string.IsNullOrEmpty(normalizedPath))
                return null;
            if (normalizedPath.EndsWith(MetaExtension, StringComparison.OrdinalIgnoreCase))
            {
                var asset = normalizedPath.Substring(0, normalizedPath.Length - MetaExtension.Length);
                return asset.Length == 0 || asset.EndsWith("/", StringComparison.Ordinal) ? null : asset;
            }
            return normalizedPath + MetaExtension;
        }
    }
}
