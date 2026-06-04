using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace AgentCore.Editor.Workspace
{
    /// <summary>
    /// 构建 Workspace 指纹（短 SHA256 hash）。
    /// 指纹用于后续 Session/Memory/RAG/Indexing 数据库隔离：
    /// 切换 WorkspaceRoot、Branch 或 Scope 配置后 hash 变化。
    /// </summary>
    public static class WorkspaceFingerprintBuilder
    {
        /// <summary>
        /// 根据 WorkspaceContext 的关键字段生成指纹。
        /// </summary>
        /// <param name="workspaceRoot">WorkspaceRoot 绝对路径（规范化）。</param>
        /// <param name="svnUrl">SVN URL（可为空）。</param>
        /// <param name="repositoryRoot">SVN Repository Root（可为空）。</param>
        /// <param name="branchId">分支标识符（可为空）。</param>
        /// <param name="unityRootRelativePath">UnityRoot 相对路径（可为空）。</param>
        /// <param name="enabledRootRelPaths">已启用的 Scope Root 相对路径列表（排序后参与 hash）。</param>
        /// <returns>16 位十六进制短 hash 字符串，例如 "a3f2c1d4e5b6a7f8"。</returns>
        public static string Build(
            string workspaceRoot,
            string svnUrl,
            string repositoryRoot,
            string branchId,
            string unityRootRelativePath,
            IEnumerable<string> enabledRootRelPaths)
        {
            var sb = new StringBuilder();

            // 规范化各输入，避免大小写/斜杠差异导致 hash 不稳定
            sb.Append(Normalize(workspaceRoot));
            sb.Append('|');
            sb.Append(Normalize(svnUrl));
            sb.Append('|');
            sb.Append(Normalize(repositoryRoot));
            sb.Append('|');
            sb.Append(Normalize(branchId));
            sb.Append('|');
            sb.Append(Normalize(unityRootRelativePath));
            sb.Append('|');

            // Scope Root 列表排序后拼接，保证顺序无关
            if (enabledRootRelPaths != null)
            {
                var sorted = new List<string>(enabledRootRelPaths);
                sorted.Sort(System.StringComparer.OrdinalIgnoreCase);
                foreach (var p in sorted)
                {
                    sb.Append(Normalize(p));
                    sb.Append(';');
                }
            }

            return ComputeShortHash(sb.ToString());
        }

        /// <summary>
        /// 直接从 WorkspaceContext 生成指纹（便捷重载）。
        /// </summary>
        public static string Build(WorkspaceContext context)
        {
            if (context == null)
                return ComputeShortHash("null");

            var enabledPaths = new List<string>();
            if (context.Roots != null)
            {
                foreach (var root in context.Roots)
                {
                    if (root.IsEnabled)
                        enabledPaths.Add(root.RelativePath ?? string.Empty);
                }
            }

            return Build(
                context.WorkspaceRoot,
                context.Vcs?.Url,
                context.Vcs?.RepositoryRoot,
                context.Vcs?.BranchId,
                context.UnityRootRelativePath,
                enabledPaths);
        }

        // ── 私有辅助 ──────────────────────────────────────────────────────────

        private static string Normalize(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace('\\', '/').ToLowerInvariant().TrimEnd('/');
        }

        private static string ComputeShortHash(string input)
        {
            using (var sha256 = SHA256.Create())
            {
                var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++)
                    sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
