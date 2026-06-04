using System;
using System.Collections.Generic;
using System.IO;
using AgentCore.Editor.Workspace.Config;

namespace AgentCore.Editor.Workspace.Resolution
{
    /// <summary>
    /// 发现 WorkspaceRoot 下的业务子根目录。
    /// 按默认候选目录列表自动扫描，并合并 workspace.json 中的用户配置。
    /// </summary>
    public static class ScopeRootResolver
    {
        /// <summary>
        /// 默认候选目录：相对路径 → (ScopeType, 是否包含 UnityRoot 本身)
        /// </summary>
        private static readonly (string RelPath, WorkspaceScopeType Scope)[] DefaultCandidates =
        {
            ("unity",       WorkspaceScopeType.Project),
            ("game",        WorkspaceScopeType.Project),
            ("project",     WorkspaceScopeType.Project),
            ("gamemodes",   WorkspaceScopeType.Mode),
            ("modes",       WorkspaceScopeType.Mode),
            ("maps",        WorkspaceScopeType.Map),
            ("levels",      WorkspaceScopeType.Map),
            ("ui",          WorkspaceScopeType.UI),
            ("localization",WorkspaceScopeType.Localization),
            ("locale",      WorkspaceScopeType.Localization),
            ("tools",       WorkspaceScopeType.Tools),
            ("build",       WorkspaceScopeType.Tools),
            ("plugins",     WorkspaceScopeType.Plugin),
            ("thirdparty",  WorkspaceScopeType.Plugin),
            ("third_party", WorkspaceScopeType.Plugin),
            ("shared",      WorkspaceScopeType.Shared),
            ("common",      WorkspaceScopeType.Shared),
            ("engine",      WorkspaceScopeType.Engine),
            ("generated",   WorkspaceScopeType.Generated),
            ("gen",         WorkspaceScopeType.Generated),
        };

        /// <summary>
        /// 解析 WorkspaceRoot 下的所有子根。
        /// </summary>
        /// <param name="workspaceRoot">规范化正斜杠的 WorkspaceRoot 绝对路径。</param>
        /// <param name="unityRoot">规范化正斜杠的 UnityRoot 绝对路径（确保 UnityRoot 始终包含在列表中）。</param>
        /// <param name="config">可选的 WorkspaceConfig（来自 workspace.json），用于合并用户配置。</param>
        /// <returns>已发现的 WorkspaceRootInfo 列表（含已禁用项）。</returns>
        public static List<WorkspaceRootInfo> Resolve(
            string workspaceRoot,
            string unityRoot,
            WorkspaceConfig config = null)
        {
            var result = new List<WorkspaceRootInfo>();
            var seenRelPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrEmpty(workspaceRoot))
                return result;

            // 1. 自动扫描默认候选目录
            foreach (var (relPath, scope) in DefaultCandidates)
            {
                var absPath = UnityRootResolver.NormalizePath(
                    Path.Combine(workspaceRoot, relPath));

                if (!Directory.Exists(absPath))
                    continue;

                if (seenRelPaths.Contains(relPath))
                    continue;

                seenRelPaths.Add(relPath);
                result.Add(BuildRootInfo(workspaceRoot, absPath, relPath, scope, "auto"));
            }

            // 2. 确保 UnityRoot 始终在列表中（即使不在默认候选中）
            if (!string.IsNullOrEmpty(unityRoot))
            {
                var unityRelPath = GetRelativePath(workspaceRoot, unityRoot);
                if (!string.IsNullOrEmpty(unityRelPath) && !seenRelPaths.Contains(unityRelPath))
                {
                    seenRelPaths.Add(unityRelPath);
                    result.Add(BuildRootInfo(workspaceRoot, unityRoot, unityRelPath,
                        WorkspaceScopeType.Project, "auto"));
                }
            }

            // 3. 合并 workspace.json 用户配置（覆盖 IsEnabled / ScopeType / Role / DisplayName）
            if (config?.ScopeRoots != null)
            {
                foreach (var configRoot in config.ScopeRoots)
                {
                    if (string.IsNullOrEmpty(configRoot.RelativePath))
                        continue;

                    var normalizedRel = UnityRootResolver.NormalizePath(configRoot.RelativePath);
                    var existing = result.Find(r =>
                        string.Equals(r.RelativePath, normalizedRel, StringComparison.OrdinalIgnoreCase));

                    if (existing != null)
                    {
                        // 覆盖用户可配置字段
                        if (!string.IsNullOrEmpty(configRoot.DisplayName))
                            existing.DisplayName = configRoot.DisplayName;
                        if (configRoot.ScopeType.HasValue)
                            existing.ScopeType = configRoot.ScopeType.Value;
                        if (configRoot.Role.HasValue)
                            existing.Role = configRoot.Role.Value;
                        existing.IsEnabled = configRoot.IsEnabled;
                        existing.Source = "workspace.json";
                    }
                    else
                    {
                        // 用户手动添加的 Root
                        var absPath = UnityRootResolver.NormalizePath(
                            Path.Combine(workspaceRoot, normalizedRel));
                        if (Directory.Exists(absPath))
                        {
                            seenRelPaths.Add(normalizedRel);
                            var scope = configRoot.ScopeType ?? WorkspaceScopeType.Unknown;
                            var info = BuildRootInfo(workspaceRoot, absPath, normalizedRel, scope, "workspace.json");
                            if (configRoot.Role.HasValue)
                                info.Role = configRoot.Role.Value;
                            if (!string.IsNullOrEmpty(configRoot.DisplayName))
                                info.DisplayName = configRoot.DisplayName;
                            info.IsEnabled = configRoot.IsEnabled;
                            result.Add(info);
                        }
                    }
                }
            }

            // 4. 按 RelativePath 排序，保证输出稳定
            result.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        // ── 私有辅助 ──────────────────────────────────────────────────────────

        private static WorkspaceRootInfo BuildRootInfo(
            string workspaceRoot,
            string absPath,
            string relPath,
            WorkspaceScopeType scope,
            string source)
        {
            var role = WorkspaceRootRoleResolver.ResolveDefaultRole(scope);
            return new WorkspaceRootInfo
            {
                Id = relPath.ToLowerInvariant().Replace('/', '-').Replace('\\', '-'),
                DisplayName = Path.GetFileName(relPath.TrimEnd('/')),
                AbsolutePath = absPath,
                RelativePath = relPath,
                ScopeType = scope,
                ScopeName = scope.ToString(),
                Role = role,
                IsReadOnly = WorkspaceRootRoleResolver.IsDefaultReadOnly(role),
                IsGenerated = WorkspaceRootRoleResolver.IsDefaultGenerated(role),
                IsEnabled = true,
                IsDetected = source == "auto",
                Source = source
            };
        }

        private static string GetRelativePath(string basePath, string fullPath)
        {
            if (string.IsNullOrEmpty(basePath) || string.IsNullOrEmpty(fullPath))
                return null;

            var baseNorm = basePath.TrimEnd('/') + "/";
            var fullNorm = fullPath.TrimEnd('/');

            if (fullNorm.StartsWith(baseNorm, StringComparison.OrdinalIgnoreCase))
                return fullNorm.Substring(baseNorm.Length);

            return null;
        }
    }
}
