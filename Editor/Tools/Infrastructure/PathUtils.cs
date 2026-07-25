using System;
using System.IO;
using UnityEngine;

namespace AgentCore.Editor.Tools.Infrastructure
{
    /// <summary>
    /// Filesystem path helpers for tool return values.
    ///
    /// **CRITICAL RULE**: All tool JSON responses exposing a file/folder path
    /// MUST route the string through <see cref="ToUnityPath"/> to guarantee
    /// cross-platform consistency. Windows native APIs (Path.Combine, FileInfo.FullName,
    /// Directory.GetCurrentDirectory) return backslashes; Unity conventions use
    /// forward slashes. Direct assignment breaks cross-platform data consistency
    /// (a JSON payload readable on macOS becomes semantically different on Windows).
    ///
    /// See SOUL.md §2.14 "File Path Return Values".
    /// </summary>
    public static class PathUtils
    {
        /// <summary>
        /// Normalize any filesystem path to Unity-style forward slashes.
        /// Idempotent. Returns input unchanged if null/empty.
        /// </summary>
        public static string ToUnityPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            return path.Replace('\\', '/');
        }

        /// <summary>
        /// Absolute project root (parent of Assets), normalized to forward slashes.
        /// Trailing slash removed.
        /// </summary>
        public static string ProjectRoot
        {
            get
            {
                var raw = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                return ToUnityPath(raw).TrimEnd('/');
            }
        }

        /// <summary>
        /// Make an absolute path relative to project root, or return normalized absolute
        /// path if the input lies outside the project. Always uses forward slashes.
        /// </summary>
        public static string ToProjectRelative(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath)) return absolutePath;
            var normalized = ToUnityPath(absolutePath);
            var root = ProjectRoot;
            if (normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                var relative = normalized.Substring(root.Length);
                return relative.TrimStart('/');
            }
            return normalized;
        }
    }
}
