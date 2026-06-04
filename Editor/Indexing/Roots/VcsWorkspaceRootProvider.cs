using System;
using System.Collections.Generic;
using System.IO;
using AgentCore.Editor.Components.Indexing.Models;
using AgentCore.Editor.Workspace;

namespace AgentCore.Editor.Components.Indexing.Roots
{
    /// <summary>
    /// 从 WorkspaceContextService 读取已发现的 WorkspaceRoot 子根，
    /// 转换为 IndexRoot 列表。
    /// Priority = 20（在 UnityRootProvider 之后执行）。
    /// </summary>
    public sealed class VcsWorkspaceRootProvider : IIndexRootProvider
    {
        /// <inheritdoc/>
        public string ProviderId => "vcs_workspace";

        /// <inheritdoc/>
        public int Priority => 20;

        /// <inheritdoc/>
        public IReadOnlyList<IndexRoot> DiscoverRoots(IndexWorkspace workspace)
        {
            var result = new List<IndexRoot>();
            if (string.IsNullOrEmpty(workspace?.WorkspaceRoot)) return result;

            // 读取 WorkspaceContextService 的当前上下文
            WorkspaceContext ctx;
            try
            {
                ctx = WorkspaceContextService.GetCurrent();
            }
            catch
            {
                return result;
            }

            if (ctx == null || !ctx.IsValid) return result;

            // 将 WorkspaceRootInfo 转换为 IndexRoot
            foreach (var rootInfo in ctx.EnabledRoots)
            {
                // 跳过 UnityRoot 本身（由 UnityRootProvider 处理）
                if (!string.IsNullOrEmpty(workspace.UnityRoot) &&
                    string.Equals(rootInfo.AbsolutePath, workspace.UnityRoot, StringComparison.OrdinalIgnoreCase))
                    continue;

                // 跳过不存在的目录
                if (!Directory.Exists(rootInfo.AbsolutePath)) continue;

                var scopeType = MapScopeType(rootInfo.ScopeType);
                var role = MapRole(rootInfo.Role);
                var readOnly = rootInfo.IsReadOnly || IndexRoot.InferReadOnly(scopeType, role);

                result.Add(new IndexRoot
                {
                    RootPath = rootInfo.AbsolutePath,
                    RelativeToWorkspace = rootInfo.RelativePath,
                    DisplayName = rootInfo.DisplayName ?? rootInfo.Id,
                    ScopeType = scopeType,
                    ScopeName = !string.IsNullOrEmpty(rootInfo.ScopeName) ? rootInfo.ScopeName : rootInfo.DisplayName,
                    Role = role,
                    ReadOnly = readOnly,
                    IsEnabled = rootInfo.IsEnabled,
                    IsDefaultSearchScope = IndexRoot.InferDefaultSearchScope(scopeType),
                    ProviderId = ProviderId
                });
            }

            return result;
        }

        private static IndexScopeType MapScopeType(WorkspaceScopeType wst)
        {
            switch (wst)
            {
                case WorkspaceScopeType.Project: return IndexScopeType.Project;
                case WorkspaceScopeType.Map: return IndexScopeType.Map;
                case WorkspaceScopeType.Mode: return IndexScopeType.Mode;
                case WorkspaceScopeType.Package: return IndexScopeType.Package;
                case WorkspaceScopeType.Shared: return IndexScopeType.Shared;
                case WorkspaceScopeType.UI: return IndexScopeType.UI;
                case WorkspaceScopeType.Localization: return IndexScopeType.Localization;
                case WorkspaceScopeType.Engine: return IndexScopeType.Engine;
                case WorkspaceScopeType.Plugin: return IndexScopeType.Plugin;
                case WorkspaceScopeType.Tools: return IndexScopeType.Tools;
                case WorkspaceScopeType.Generated: return IndexScopeType.Generated;
                default: return IndexScopeType.Unknown;
            }
        }

        private static IndexRootRole MapRole(WorkspaceRootRole wrr)
        {
            switch (wrr)
            {
                case WorkspaceRootRole.EditableProjectCode: return IndexRootRole.EditableProjectCode;
                case WorkspaceRootRole.SharedCode: return IndexRootRole.SharedCode;
                case WorkspaceRootRole.WorkspacePackage: return IndexRootRole.WorkspacePackage;
                case WorkspaceRootRole.CommercialPlugin: return IndexRootRole.CommercialPlugin;
                case WorkspaceRootRole.CustomPlugin: return IndexRootRole.CustomPlugin;
                case WorkspaceRootRole.EngineCode: return IndexRootRole.EngineCode;
                case WorkspaceRootRole.ToolingCode: return IndexRootRole.ToolingCode;
                case WorkspaceRootRole.GeneratedCode: return IndexRootRole.GeneratedCode;
                case WorkspaceRootRole.ReadOnlyReference: return IndexRootRole.ReadOnlyReference;
                default: return IndexRootRole.EditableProjectCode;
            }
        }
    }
}
