using System;
using System.Collections.Generic;
using System.IO;
using AgentCore.Editor.Components.Indexing.Models;

namespace AgentCore.Editor.Components.Indexing.Roots
{
    /// <summary>
    /// 额外授权根 Provider（P2/例外场景）。
    /// 允许用户显式授权少量位于 WorkspaceRoot 之外的目录参与索引。
    /// 典型场景：共享引擎代码库、跨项目公共库、本地 SDK 目录等。
    ///
    /// Priority = 60（最低优先级，在所有自动发现之后执行）。
    /// </summary>
    public sealed class ExtraAuthorizedRootProvider : IIndexRootProvider
    {
        private readonly IReadOnlyList<ExtraAuthorizedRootEntry> _entries;

        /// <summary>
        /// 使用授权条目列表初始化 Provider。
        /// </summary>
        /// <param name="entries">用户显式授权的外部根目录条目列表。</param>
        public ExtraAuthorizedRootProvider(IReadOnlyList<ExtraAuthorizedRootEntry> entries)
        {
            _entries = entries ?? Array.Empty<ExtraAuthorizedRootEntry>() as IReadOnlyList<ExtraAuthorizedRootEntry>;
        }

        /// <summary>
        /// 使用空配置初始化 Provider（无额外授权根）。
        /// </summary>
        public ExtraAuthorizedRootProvider()
        {
            _entries = Array.Empty<ExtraAuthorizedRootEntry>();
        }

        /// <inheritdoc/>
        public string ProviderId => "extra_authorized";

        /// <inheritdoc/>
        public int Priority => 60;

        /// <inheritdoc/>
        public IReadOnlyList<IndexRoot> DiscoverRoots(IndexWorkspace workspace)
        {
            var result = new List<IndexRoot>();
            if (_entries == null || _entries.Count == 0) return result;

            foreach (var entry in _entries)
            {
                if (string.IsNullOrWhiteSpace(entry.RootPath)) continue;

                try
                {
                    var absolutePath = entry.RootPath.Replace('\\', '/').TrimEnd('/');
                    if (!Path.IsPathRooted(absolutePath)) continue; // 额外授权根必须是绝对路径
                    if (!Directory.Exists(absolutePath)) continue;

                    var scopeName = !string.IsNullOrWhiteSpace(entry.ScopeName)
                        ? entry.ScopeName
                        : Path.GetFileName(absolutePath);

                    var includePatterns = entry.IncludePatterns != null && entry.IncludePatterns.Count > 0
                        ? entry.IncludePatterns
                        : new List<string> { "*.cs" };

                    var excludePatterns = entry.ExcludePatterns != null && entry.ExcludePatterns.Count > 0
                        ? entry.ExcludePatterns
                        : new List<string> { "bin/", "obj/", "Library/", "Temp/", "Generated/" };

                    // 额外授权根默认为只读（外部目录通常不应被 AI 修改）
                    var readOnly = entry.ReadOnly ?? true;

                    result.Add(new IndexRoot
                    {
                        RootPath = absolutePath,
                        ScopeType = entry.ScopeType,
                        ScopeName = scopeName,
                        Role = entry.Role,
                        ReadOnly = readOnly,
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
    /// 用户显式授权的单条外部根目录条目。
    /// </summary>
    [Serializable]
    public sealed class ExtraAuthorizedRootEntry
    {
        /// <summary>
        /// 根目录绝对路径（必须是绝对路径，不支持相对路径）。
        /// </summary>
        public string RootPath { get; set; }

        /// <summary>
        /// UI 显示名称（可选，默认使用目录名）。
        /// </summary>
        public string DisplayName { get; set; }

        /// <summary>
        /// Scope 类型（通常为 Engine、Plugin 或 Shared）。
        /// </summary>
        public IndexScopeType ScopeType { get; set; } = IndexScopeType.Engine;

        /// <summary>
        /// Scope 名称（如 "UnityEngine"、"ThirdPartySDK"）。
        /// </summary>
        public string ScopeName { get; set; }

        /// <summary>
        /// Root 角色（通常为 EngineCode、CommercialPlugin 或 ReadOnlyReference）。
        /// </summary>
        public IndexRootRole Role { get; set; } = IndexRootRole.ReadOnlyReference;

        /// <summary>
        /// 是否只读（null 表示使用默认值 true，外部目录默认只读）。
        /// </summary>
        public bool? ReadOnly { get; set; }

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
