using System;
using System.Collections.Generic;
using System.IO;
using AgentCore.Editor.Components.Indexing.Models;

namespace AgentCore.Editor.Components.Indexing.Roots
{
    /// <summary>
    /// 用户在 Settings 中手动配置的 Scope Root Provider。
    /// 允许用户为 WorkspaceRoot 内的任意目录指定 ScopeType、Role、include/exclude 规则。
    /// Priority = 40（在自动发现之后执行，用户配置可覆盖或补充自动发现结果）。
    /// </summary>
    public sealed class UserConfiguredScopeRootProvider : IIndexRootProvider
    {
        private readonly IReadOnlyList<UserScopeRootEntry> _entries;

        /// <summary>
        /// 使用用户配置条目列表初始化 Provider。
        /// </summary>
        /// <param name="entries">用户在 Settings 中配置的 Scope Root 条目列表。</param>
        public UserConfiguredScopeRootProvider(IReadOnlyList<UserScopeRootEntry> entries)
        {
            _entries = entries ?? Array.Empty<UserScopeRootEntry>() as IReadOnlyList<UserScopeRootEntry>;
        }

        /// <summary>
        /// 使用空配置初始化 Provider（无用户自定义根）。
        /// </summary>
        public UserConfiguredScopeRootProvider()
        {
            _entries = Array.Empty<UserScopeRootEntry>();
        }

        /// <inheritdoc/>
        public string ProviderId => "user_configured";

        /// <inheritdoc/>
        public int Priority => 40;

        /// <inheritdoc/>
        public IReadOnlyList<IndexRoot> DiscoverRoots(IndexWorkspace workspace)
        {
            var result = new List<IndexRoot>();
            if (_entries == null || _entries.Count == 0) return result;
            if (string.IsNullOrEmpty(workspace?.WorkspaceRoot)) return result;

            foreach (var entry in _entries)
            {
                if (string.IsNullOrWhiteSpace(entry.RootPath)) continue;

                try
                {
                    // 支持绝对路径和相对于 WorkspaceRoot 的相对路径
                    var absolutePath = Path.IsPathRooted(entry.RootPath)
                        ? entry.RootPath
                        : Path.Combine(workspace.WorkspaceRoot, entry.RootPath);

                    absolutePath = absolutePath.Replace('\\', '/').TrimEnd('/');

                    if (!Directory.Exists(absolutePath)) continue;

                    // 用户配置的 ScopeName：优先使用配置值，否则用目录名
                    var scopeName = !string.IsNullOrWhiteSpace(entry.ScopeName)
                        ? entry.ScopeName
                        : Path.GetFileName(absolutePath);

                    var includePatterns = entry.IncludePatterns != null && entry.IncludePatterns.Count > 0
                        ? entry.IncludePatterns
                        : new List<string> { "*.cs" };

                    var excludePatterns = entry.ExcludePatterns != null && entry.ExcludePatterns.Count > 0
                        ? entry.ExcludePatterns
                        : new List<string> { "bin/", "obj/", "Library/", "Temp/", "Generated/" };

                    result.Add(new IndexRoot
                    {
                        RootPath = absolutePath,
                        ScopeType = entry.ScopeType,
                        ScopeName = scopeName,
                        Role = entry.Role,
                        ReadOnly = entry.ReadOnly,
                        IncludePatterns = includePatterns,
                        ExcludePatterns = excludePatterns,
                        IsInSearchScope = IndexRoot.InferDefaultSearchScope(entry.ScopeType),
                        ProviderId = ProviderId,
                        DisplayName = !string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.DisplayName : scopeName,
                    });
                }
                catch (Exception)
                {
                    // 跳过无效条目，不中断整体发现流程
                }
            }

            return result;
        }
    }

    /// <summary>
    /// 用户在 Settings 中配置的单条 Scope Root 条目。
    /// </summary>
    [Serializable]
    public sealed class UserScopeRootEntry
    {
        /// <summary>
        /// 根目录路径（绝对路径或相对于 WorkspaceRoot 的相对路径）。
        /// </summary>
        public string RootPath { get; set; }

        /// <summary>
        /// UI 显示名称（可选，默认使用目录名）。
        /// </summary>
        public string DisplayName { get; set; }

        /// <summary>
        /// Scope 类型（如 Map、Mode、Shared 等）。
        /// </summary>
        public IndexScopeType ScopeType { get; set; } = IndexScopeType.Project;

        /// <summary>
        /// Scope 名称（如 "Battle"、"City01"、"UICommon"）。
        /// </summary>
        public string ScopeName { get; set; }

        /// <summary>
        /// Root 角色（如 EditableProjectCode、CommercialPlugin 等）。
        /// </summary>
        public IndexRootRole Role { get; set; } = IndexRootRole.EditableProjectCode;

        /// <summary>
        /// 是否只读（只读 Root 的符号不会被 AI 修改）。
        /// </summary>
        public bool ReadOnly { get; set; }

        /// <summary>
        /// 包含文件的 glob 模式列表（默认 *.cs）。
        /// </summary>
        public List<string> IncludePatterns { get; set; }

        /// <summary>
        /// 排除文件/目录的 glob 模式列表。
        /// </summary>
        public List<string> ExcludePatterns { get; set; }
    }
}
