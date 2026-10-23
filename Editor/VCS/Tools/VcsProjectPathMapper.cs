using System;
using System.Collections.Generic;
using System.IO;
using AgentCore.Editor.Components.VCS.UI;

namespace AgentCore.Editor.Components.VCS.Tools
{
    /// <summary>
    /// version_control 工具的路径约定：输入与输出都以 <b>Unity 工程根</b>为基准。
    /// <para>
    /// 适配器在 VCS 根（SVN 工作副本根 / Git 仓库根）中执行命令，返回的相对路径相对于 VCS 根；Unity 工程位于其子目录时
    /// （如 <c>Game/项目 A/Assets/...</c>），与工具描述"相对于工程根"不一致。这里统一换算：
    /// 输出路径在工程内时改为工程相对路径（<c>Assets/...</c>），工程外的路径保持适配器原样（VCS 根相对或绝对）；
    /// 输入同时接受工程相对、VCS 根相对与绝对路径。纯字符串 / 文件系统逻辑，不调用 Unity API。
    /// </para>
    /// </summary>
    public sealed class VcsProjectPathMapper
    {
        public string VcsRoot { get; }
        public string ProjectRoot { get; }

        public VcsProjectPathMapper(string vcsRoot, string projectRoot)
        {
            VcsRoot = vcsRoot;
            ProjectRoot = projectRoot;
        }

        /// <summary>Unity 工程是否就是 VCS 根（此时两种相对路径相同，无需换算）。</summary>
        public bool ProjectIsVcsRoot => SamePath(VcsRoot, ProjectRoot);

        /// <summary>
        /// 适配器输出路径（VCS 根相对或绝对）→ 工程相对路径（正斜杠）；工程根本身返回 <c>.</c>；
        /// 不在工程内、Perforce depot 路径（<c>//depot/...</c>）或无法解析时原样返回。
        /// </summary>
        public string ToOutput(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.StartsWith("//", StringComparison.Ordinal))
                return path;
            if (string.IsNullOrWhiteSpace(VcsRoot) || string.IsNullOrWhiteSpace(ProjectRoot))
                return path;

            var relative = VcsAssetPaths.ToUnityRelativePath(path, VcsRoot, ProjectRoot);
            if (relative == null)
                return path;
            return relative.Length == 0 ? "." : relative;
        }

        public List<string> ToOutput(IEnumerable<string> paths)
        {
            var result = new List<string>();
            if (paths == null)
                return result;
            foreach (var path in paths)
                result.Add(ToOutput(path));
            return result;
        }

        /// <summary>按路径为键的字典（failed_files / skipped_files）：只换算键。</summary>
        public Dictionary<string, string> ToOutputKeys(IDictionary<string, string> byPath)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (byPath == null)
                return result;
            foreach (var pair in byPath)
            {
                var key = ToOutput(pair.Key) ?? pair.Key ?? string.Empty;
                result[key] = result.TryGetValue(key, out var existing) && !string.IsNullOrEmpty(existing)
                    ? existing + "; " + pair.Value
                    : pair.Value;
            }
            return result;
        }

        /// <summary>
        /// 输入路径 → 可交给适配器的路径。VCS 根相对路径存在时原样返回；否则按工程相对解析，
        /// 存在（或父目录存在，例如已删除的文件）且位于 VCS 根之内时返回绝对路径；都不成立时原样返回。
        /// SVN 适配器自己已做同样的解析（SvnAdapter.ResolvePath），此方法用于 Git / Perforce。
        /// </summary>
        public string ResolveInput(string path, Func<string, bool> exists = null)
        {
            if (string.IsNullOrWhiteSpace(path) || path.StartsWith("//", StringComparison.Ordinal))
                return path;
            if (string.IsNullOrWhiteSpace(VcsRoot) || string.IsNullOrWhiteSpace(ProjectRoot) || ProjectIsVcsRoot)
                return path;

            exists = exists ?? DefaultExists;
            try
            {
                if (Path.IsPathRooted(path))
                    return path;

                var fromRoot = Path.GetFullPath(Path.Combine(VcsRoot, path));
                if (SafeExists(exists, fromRoot))
                    return path;

                var fromProject = Path.GetFullPath(Path.Combine(ProjectRoot, path));
                if (!IsUnder(fromProject, VcsRoot))
                    return path;
                if (SafeExists(exists, fromProject))
                    return fromProject;

                var projectParent = Path.GetDirectoryName(fromProject);
                var rootParent = Path.GetDirectoryName(fromRoot);
                if (!SafeExists(exists, rootParent) && SafeExists(exists, projectParent))
                    return fromProject;
            }
            catch (Exception)
            {
                // 非法路径：交给适配器报错
            }
            return path;
        }

        private static bool DefaultExists(string path) => File.Exists(path) || Directory.Exists(path);

        private static bool SafeExists(Func<string, bool> exists, string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;
            try { return exists(path); }
            catch { return false; }
        }

        private static string Norm(string path)
        {
            return Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');
        }

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
                return false;
            try { return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private static bool IsUnder(string path, string root)
        {
            try
            {
                var p = Norm(path);
                var r = Norm(root);
                return string.Equals(p, r, StringComparison.OrdinalIgnoreCase)
                       || p.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
