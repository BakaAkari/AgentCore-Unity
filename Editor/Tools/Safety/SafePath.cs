using System;
using System.IO;
using AgentCore.Editor.Tools.Infrastructure;

namespace AgentCore.Editor.Tools.Safety
{
    /// <summary>
    /// 统一的路径规范化与根目录约束（共享基建 ①）。
    /// <para>
    /// 所有接收模型传入路径的工具都应经过这里，而不是各自 <c>StartsWith(root)</c>：
    /// 1) 拒绝 <c>\0</c>、NTFS 备用数据流 <c>:</c>、<c>\\?\</c> / <c>\\.\</c> 设备前缀；
    /// 2) <see cref="Path.GetFullPath(string)"/> 折叠 <c>..</c>；
    /// 3) 按路径分量判断是否在根内（带分隔符的前缀比较，避免 <c>D:\Proj2</c> 被当成 <c>D:\Proj</c> 的子目录）；
    /// 4) 根到目标之间任一已存在的祖先是符号链接 / junction 即拒绝；
    /// 5) 禁止段（<c>.env*</c>、私钥、<c>.git/config</c>）读写都拒绝。
    /// </para>
    /// <para>
    /// 参考：Gemini CLI isPathWithinRoot / hasBlockedPathSegment，Codex Path::starts_with 分量比较。
    /// Unity Mono 无 <c>FileSystemInfo.LinkTarget</c>，因此用 <see cref="FileAttributes.ReparsePoint"/> 判断链接。
    /// </para>
    /// </summary>
    public static class SafePath
    {
        /// <summary>项目根目录（Assets 的父目录），正斜杠、无尾斜杠。</summary>
        public static string ProjectRoot => PathUtils.ProjectRoot;

        /// <summary>
        /// 把相对（或绝对）路径解析到 <paramref name="root"/> 之内。
        /// 成功时 <paramref name="fullPath"/> 为平台原生分隔符的绝对路径。
        /// </summary>
        public static bool TryResolveUnder(string root, string input, out string fullPath, out string error)
        {
            fullPath = null;

            if (!CheckSyntax(input, out error))
                return false;

            string full;
            string rootFull;
            try
            {
                rootFull = Path.GetFullPath(root);
                full = Path.GetFullPath(Path.IsPathRooted(input) ? input : Path.Combine(rootFull, input));
            }
            catch (Exception ex)
            {
                error = $"Invalid path '{input}': {ex.Message}";
                return false;
            }

            if (!IsUnder(full, rootFull))
            {
                error = $"Path '{input}' resolves outside '{PathUtils.ToUnityPath(rootFull)}'.";
                return false;
            }

            if (ContainsReparsePoint(rootFull, full))
            {
                error = $"Path '{input}' goes through a symbolic link or junction, which is not allowed.";
                return false;
            }

            if (IsForbidden(full))
            {
                error = $"Access to '{input}' is forbidden (secret or credential file).";
                return false;
            }

            fullPath = full;
            return true;
        }

        /// <summary>
        /// 同 <see cref="TryResolveUnder"/>，失败时抛 <see cref="ArgumentException"/>（工具层由 dispatcher 转为失败结果）。
        /// </summary>
        public static string ResolveUnderOrThrow(string root, string input)
        {
            if (!TryResolveUnder(root, input, out var full, out var error))
                throw new ArgumentException(error);
            return full;
        }

        /// <summary>
        /// 把模型给出的路径规范化为项目内的 Unity 资产路径（<c>Assets/...</c> 或 <c>Packages/...</c>，正斜杠）。
        /// 无前缀时补 <c>Assets/</c>；项目内绝对路径转为相对路径；折叠 <c>..</c> 后仍须落在
        /// <c>Assets/</c>（或允许时 <c>Packages/</c>）之下，否则抛 <see cref="ArgumentException"/>。
        /// </summary>
        public static string ToAssetPath(string input, bool allowPackages = true)
        {
            if (string.IsNullOrEmpty(input)) return input;

            if (!CheckSyntax(input, out var error))
                throw new ArgumentException(error);

            var root = ProjectRoot;
            var path = input.Replace('\\', '/').TrimEnd('/');

            if (Path.IsPathRooted(path))
            {
                // 项目内绝对路径 → 相对；项目外直接拒绝
                var full = PathUtils.ToUnityPath(SafeGetFullPath(path, input));
                if (!IsUnder(full, root))
                    throw new ArgumentException($"Path '{input}' is outside the Unity project.");
                path = full.Length == root.Length ? "" : full.Substring(root.Length + 1);
            }
            else if (!IsAssetRootPrefixed(path))
            {
                path = "Assets/" + path;
            }

            // 折叠 ..，并确认结果仍在 Assets/（或 Packages/）之下
            var resolved = PathUtils.ToUnityPath(SafeGetFullPath(Path.Combine(root, path), input));
            if (!IsUnder(resolved, root))
                throw new ArgumentException($"Path '{input}' resolves outside the Unity project.");

            var relative = resolved.Length == root.Length ? "" : resolved.Substring(root.Length + 1);
            bool underAssets = relative == "Assets" || relative.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
            bool underPackages = relative == "Packages" || relative.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase);
            if (!underAssets && !(allowPackages && underPackages))
                throw new ArgumentException(
                    $"Path '{input}' resolves to '{relative}', which is not under Assets/{(allowPackages ? " or Packages/" : "")}.");

            if (ContainsReparsePoint(root, resolved))
                throw new ArgumentException($"Path '{input}' goes through a symbolic link or junction, which is not allowed.");

            if (IsForbidden(relative))
                throw new ArgumentException($"Access to '{input}' is forbidden (secret or credential file).");

            return relative;
        }

        /// <summary>
        /// <paramref name="fullPath"/> 是否等于或位于 <paramref name="root"/> 之内（按分量，忽略大小写）。
        /// 两个参数都应已是绝对路径。
        /// </summary>
        public static bool IsUnder(string fullPath, string root)
        {
            if (string.IsNullOrEmpty(fullPath) || string.IsNullOrEmpty(root)) return false;

            var p = PathUtils.ToUnityPath(fullPath).TrimEnd('/');
            var r = PathUtils.ToUnityPath(root).TrimEnd('/');
            if (p.Equals(r, StringComparison.OrdinalIgnoreCase)) return true;
            return p.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 从 <paramref name="root"/>（不含）到 <paramref name="fullPath"/>（含）之间，
        /// 任一已存在的路径分量是 reparse point（符号链接 / junction）即返回 true。
        /// </summary>
        public static bool ContainsReparsePoint(string root, string fullPath)
        {
            try
            {
                var r = PathUtils.ToUnityPath(Path.GetFullPath(root)).TrimEnd('/');
                var p = PathUtils.ToUnityPath(Path.GetFullPath(fullPath)).TrimEnd('/');
                if (!IsUnder(p, r) || p.Length == r.Length) return false;

                var current = r;
                foreach (var segment in p.Substring(r.Length + 1).Split('/'))
                {
                    current = current + "/" + segment;
                    if (!File.Exists(current) && !Directory.Exists(current))
                        break; // 后续分量尚未创建，不可能是链接
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        return true;
                }
                return false;
            }
            catch
            {
                // 无法确认时按"有链接"处理（fail closed）
                return true;
            }
        }

        /// <summary>
        /// 禁止段：读写都拒绝的机密文件。
        /// <c>.env*</c>（<c>.env.example</c> 除外）、<c>id_rsa*</c> / <c>id_ed25519*</c> / <c>id_ecdsa*</c>、
        /// <c>*.pem</c> / <c>*.pfx</c> / <c>*.p12</c>、<c>.git/config</c>、<c>.git-credentials</c>。
        /// </summary>
        public static bool IsForbidden(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            var segments = PathUtils.ToUnityPath(path).Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return false;

            var name = CleanSegment(segments[segments.Length - 1]);

            if (name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals(".env.example", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals(".env.sample", StringComparison.OrdinalIgnoreCase))
                return true;

            if (name.StartsWith("id_rsa", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("id_ed25519", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("id_ecdsa", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(".git-credentials", StringComparison.OrdinalIgnoreCase))
                return true;

            if (name.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".p12", StringComparison.OrdinalIgnoreCase))
                return true;

            if (segments.Length >= 2 &&
                name.Equals("config", StringComparison.OrdinalIgnoreCase) &&
                CleanSegment(segments[segments.Length - 2]).Equals(".git", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        /// <summary>
        /// 受保护路径（写入总要确认，YOLO 也不例外）。输入为项目相对路径或绝对路径。
        /// <c>ProjectSettings/</c>、<c>UserSettings/</c>、<c>Packages/manifest.json</c>、<c>.git/</c>、
        /// <c>*.asmdef</c>、<c>Assets/**/Editor/**</c>、AgentCore 自身的包目录。
        /// </summary>
        public static bool IsProtected(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            var p = PathUtils.ToUnityPath(path);
            if (Path.IsPathRooted(p))
            {
                var root = ProjectRoot;
                if (!IsUnder(p, root)) return true; // 项目外一律视为受保护
                p = p.Length == root.Length ? "" : p.Substring(root.Length + 1);
            }

            var segments = p.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return false;
            for (int i = 0; i < segments.Length; i++) segments[i] = CleanSegment(segments[i]);

            var first = segments[0];
            if (first.Equals("ProjectSettings", StringComparison.OrdinalIgnoreCase) ||
                first.Equals("UserSettings", StringComparison.OrdinalIgnoreCase))
                return true;

            if (segments.Length == 2 &&
                first.Equals("Packages", StringComparison.OrdinalIgnoreCase) &&
                (segments[1].Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ||
                 segments[1].Equals("packages-lock.json", StringComparison.OrdinalIgnoreCase)))
                return true;

            if (segments.Length >= 2 &&
                first.Equals("Packages", StringComparison.OrdinalIgnoreCase) &&
                segments[1].StartsWith("com.agentcore.", StringComparison.OrdinalIgnoreCase))
                return true;

            foreach (var s in segments)
            {
                if (s.Equals(".git", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            var name = segments[segments.Length - 1];
            if (name.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".asmref", StringComparison.OrdinalIgnoreCase))
                return true;

            if (first.Equals("Assets", StringComparison.OrdinalIgnoreCase))
            {
                // Assets/**/Editor/**：编辑器代码可在加载时执行任意逻辑
                for (int i = 1; i < segments.Length - 1; i++)
                {
                    if (segments[i].Equals("Editor", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }

        // ── 内部 ──────────────────────────────────────────────────────────────

        /// <summary>步骤 1：拒绝 NUL、备用数据流、设备 / 扩展长度前缀。</summary>
        private static bool CheckSyntax(string input, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(input))
            {
                error = "Path cannot be empty.";
                return false;
            }

            if (input.IndexOf('\0') >= 0)
            {
                error = "Path contains a NUL character.";
                return false;
            }

            if (input.StartsWith(@"\\?\", StringComparison.Ordinal) ||
                input.StartsWith(@"\\.\", StringComparison.Ordinal) ||
                input.StartsWith("//?/", StringComparison.Ordinal) ||
                input.StartsWith("//./", StringComparison.Ordinal))
            {
                error = $"Device or extended-length path prefixes are not allowed: '{input}'.";
                return false;
            }

            // 盘符（X:）之后再出现 ':' 即 NTFS 备用数据流（file.txt:stream）
            int colonSearchStart = input.Length >= 2 && input[1] == ':' && char.IsLetter(input[0]) ? 2 : 0;
            if (input.IndexOf(':', colonSearchStart) >= 0)
            {
                error = $"Path contains ':' outside a drive letter (alternate data stream?): '{input}'.";
                return false;
            }

            return true;
        }

        private static string SafeGetFullPath(string path, string original)
        {
            try { return Path.GetFullPath(path); }
            catch (Exception ex) { throw new ArgumentException($"Invalid path '{original}': {ex.Message}"); }
        }

        private static bool IsAssetRootPrefixed(string path)
        {
            return path == "Assets" || path == "Packages" ||
                   path.StartsWith("Assets/", StringComparison.Ordinal) ||
                   path.StartsWith("Packages/", StringComparison.Ordinal);
        }

        /// <summary>Windows 会忽略分量末尾的点与空格（<c>.env.</c> == <c>.env</c>），匹配前先去掉。</summary>
        private static string CleanSegment(string segment) => segment.TrimEnd('.', ' ');
    }
}
