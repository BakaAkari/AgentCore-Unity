using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AgentCore.Editor.Components.Indexing.Models;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Components.Indexing.Core
{
    /// <summary>
    /// 决定一个 <see cref="IndexRoot"/> 下哪些文件参与索引，全量扫描与定向增量共用同一套规则。
    /// <para>
    /// 规则：文件名匹配 IncludePatterns；不命中 ExcludePatterns；所在目录未被剪枝。剪枝的目录：
    /// VCS / IDE 元数据目录（任意深度）；Unity 工程根（含 Assets 与 ProjectSettings 的目录）下的
    /// Library / Temp / Logs / obj / UserSettings；以及本身是另一个 Root 的子目录（由该 Root 负责，
    /// 避免祖先 Root 重复扫描 Unity 工程或插件目录）。
    /// </para>
    /// <para>
    /// 枚举按目录逐层手动递归，单个目录不可读只跳过该目录并把结果标为不完整，
    /// 调用方据此避免把不完整 Root 下的已索引文件当成"已删除"。
    /// </para>
    /// </summary>
    public static class IndexRootScanner
    {
        /// <summary>任意深度都剪掉的目录名（VCS / IDE 元数据）。</summary>
        private static readonly HashSet<string> s_alwaysPrunedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".svn", ".git", ".hg", ".vs", ".idea"
        };

        /// <summary>Unity 工程根下的生成/缓存目录（只在 Unity 工程根下剪掉，Assets 里同名目录不受影响）。</summary>
        private static readonly HashSet<string> s_unityProjectGeneratedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Library", "Temp", "Logs", "obj", "UserSettings"
        };

        private const int MaxReportedErrors = 5;

        /// <summary>单个 Root 的扫描结果。</summary>
        public sealed class ScanResult
        {
            /// <summary>匹配的文件（规范化正斜杠的绝对路径）。</summary>
            public List<string> Files { get; } = new List<string>();

            /// <summary>是否有目录因异常没能枚举（为 false 时不能据此判定文件已删除）。</summary>
            public bool Complete { get; set; } = true;
        }

        /// <summary>
        /// 扫描 Root 下所有应索引的文件。
        /// </summary>
        /// <param name="root">要扫描的 Root。</param>
        /// <param name="allRoots">解析出的全部 Root（含未参与本次运行的），用于剪掉嵌套的子 Root。</param>
        /// <param name="maxFileSizeBytes">超过此大小的文件跳过。</param>
        /// <param name="ct">取消令牌。</param>
        public static ScanResult Scan(IndexRoot root, IReadOnlyList<IndexRoot> allRoots, long maxFileSizeBytes, CancellationToken ct)
        {
            var result = new ScanResult();
            var rootPath = Normalize(root.RootPath);
            var includePatterns = GetIncludePatterns(root);
            var nestedRoots = GetNestedRootPaths(rootPath, allRoots);
            var errors = 0;

            var pending = new Stack<string>();
            pending.Push(rootPath);
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var dir = pending.Pop();

                string[] files;
                string[] subDirs;
                try
                {
                    files = Directory.GetFiles(dir);
                    subDirs = Directory.GetDirectories(dir);
                }
                catch (Exception ex)
                {
                    result.Complete = false;
                    if (errors++ < MaxReportedErrors)
                        AgentCoreLog.Warning($"[CodebaseIndexer] Cannot enumerate '{dir}', skipping it: {ex.Message}");
                    continue;
                }

                foreach (var file in files)
                {
                    var normalized = Normalize(file);
                    if (!MatchesInclude(Path.GetFileName(normalized), includePatterns)) continue;
                    if (IsExcluded(normalized, rootPath, root.ExcludePatterns)) continue;
                    try
                    {
                        if (new FileInfo(file).Length > maxFileSizeBytes) continue;
                    }
                    catch
                    {
                        continue;
                    }
                    result.Files.Add(normalized);
                }

                var dirIsUnityProject = IsUnityProjectRoot(dir);
                foreach (var sub in subDirs)
                {
                    var normalizedSub = Normalize(sub);
                    if (ShouldPruneDirectory(normalizedSub, dirIsUnityProject, rootPath, root.ExcludePatterns, nestedRoots)) continue;
                    pending.Push(normalizedSub);
                }
            }

            return result;
        }

        /// <summary>
        /// 定向增量用：判断一个文件是否会被 <see cref="Scan"/> 收录（不检查大小与存在性）。
        /// </summary>
        public static bool ShouldIndex(string filePath, IndexRoot root, IReadOnlyList<IndexRoot> allRoots)
        {
            var normalized = Normalize(filePath);
            var rootPath = Normalize(root.RootPath);
            if (!normalized.StartsWith(rootPath + "/", StringComparison.OrdinalIgnoreCase)) return false;
            if (!MatchesInclude(Path.GetFileName(normalized), GetIncludePatterns(root))) return false;
            if (IsExcluded(normalized, rootPath, root.ExcludePatterns)) return false;

            var nestedRoots = GetNestedRootPaths(rootPath, allRoots);
            var parent = rootPath;
            var segments = normalized.Substring(rootPath.Length + 1).Split('/');
            for (int i = 0; i < segments.Length - 1; i++)
            {
                var dir = parent + "/" + segments[i];
                if (ShouldPruneDirectory(dir, IsUnityProjectRoot(parent), rootPath, root.ExcludePatterns, nestedRoots))
                    return false;
                parent = dir;
            }
            return true;
        }

        /// <summary>
        /// 判断文件是否应被排除。
        /// 排除规则支持：目录前缀（以 / 结尾）、文件名通配符（*.generated.cs）、精确路径片段。
        /// </summary>
        public static bool IsExcluded(string normalizedFilePath, string rootPath, List<string> excludePatterns)
        {
            if (excludePatterns == null || excludePatterns.Count == 0)
                return false;

            // 获取相对于 root 的路径（用于模式匹配）
            var normalizedRoot = rootPath.Replace('\\', '/').TrimEnd('/') + '/';
            var relativePath = normalizedFilePath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                ? normalizedFilePath.Substring(normalizedRoot.Length)
                : normalizedFilePath;

            foreach (var pattern in excludePatterns)
            {
                if (string.IsNullOrEmpty(pattern)) continue;

                // 目录排除（以 / 结尾，如 "bin/"、"obj/"）
                if (pattern.EndsWith("/"))
                {
                    if (ContainsDirectorySegment(relativePath, pattern.TrimEnd('/')))
                        return true;
                }
                // 通配符模式（如 "*.generated.cs"）
                else if (pattern.Contains('*') || pattern.Contains('?'))
                {
                    if (MatchesWildcard(Path.GetFileName(normalizedFilePath), pattern))
                        return true;
                }
                // 精确路径片段
                else
                {
                    if (relativePath.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 简单通配符匹配（支持 * 和 ?）。
        /// </summary>
        public static bool MatchesWildcard(string input, string pattern)
        {
            // 常见的 "*.cs" 这类后缀模式直接比较，避免每个文件都走正则
            if (pattern.Length > 1 && pattern[0] == '*' && pattern.IndexOfAny(new[] { '*', '?' }, 1) < 0)
                return input.EndsWith(pattern.Substring(1), StringComparison.OrdinalIgnoreCase);

            var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(
                input, regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private static bool ShouldPruneDirectory(
            string normalizedDir,
            bool parentIsUnityProject,
            string rootPath,
            List<string> excludePatterns,
            HashSet<string> nestedRoots)
        {
            var name = Path.GetFileName(normalizedDir);
            if (s_alwaysPrunedNames.Contains(name)) return true;
            if (parentIsUnityProject && s_unityProjectGeneratedNames.Contains(name)) return true;
            if (nestedRoots.Contains(normalizedDir)) return true;

            // 目录型排除模式在枚举阶段就剪掉，不必再进入目录
            if (excludePatterns != null && normalizedDir.Length > rootPath.Length)
            {
                var relativeDir = normalizedDir.Substring(rootPath.Length + 1) + "/";
                foreach (var pattern in excludePatterns)
                {
                    if (!string.IsNullOrEmpty(pattern) && pattern.EndsWith("/") &&
                        ContainsDirectorySegment(relativeDir, pattern.TrimEnd('/')))
                        return true;
                }
            }
            return false;
        }

        private static bool ContainsDirectorySegment(string relativePath, string segment)
        {
            return relativePath.StartsWith(segment + "/", StringComparison.OrdinalIgnoreCase) ||
                   relativePath.IndexOf("/" + segment + "/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>同时含 Assets 与 ProjectSettings 子目录的目录视为 Unity 工程根（包括嵌在祖先 Root 里的其他工程）。</summary>
        private static bool IsUnityProjectRoot(string dir)
        {
            try
            {
                return Directory.Exists(Path.Combine(dir, "Assets")) && Directory.Exists(Path.Combine(dir, "ProjectSettings"));
            }
            catch
            {
                return false;
            }
        }

        private static HashSet<string> GetNestedRootPaths(string rootPath, IReadOnlyList<IndexRoot> allRoots)
        {
            var nested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (allRoots == null) return nested;
            var prefix = rootPath + "/";
            foreach (var other in allRoots)
            {
                if (other == null || string.IsNullOrEmpty(other.RootPath)) continue;
                var otherPath = Normalize(other.RootPath);
                if (otherPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    nested.Add(otherPath);
            }
            return nested;
        }

        private static List<string> GetIncludePatterns(IndexRoot root)
        {
            return root.IncludePatterns != null && root.IncludePatterns.Count > 0
                ? root.IncludePatterns.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string> { "*.cs" };
        }

        private static bool MatchesInclude(string fileName, List<string> includePatterns)
        {
            foreach (var pattern in includePatterns)
            {
                if (!string.IsNullOrEmpty(pattern) && MatchesWildcard(fileName, pattern))
                    return true;
            }
            return false;
        }

        private static string Normalize(string path)
        {
            return path.Replace('\\', '/').TrimEnd('/');
        }
    }
}
