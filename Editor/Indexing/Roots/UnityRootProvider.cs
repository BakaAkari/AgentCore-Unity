using System.Collections.Generic;
using System.IO;
using AgentCore.Editor.Components.Indexing.Models;

namespace AgentCore.Editor.Components.Indexing.Roots
{
    /// <summary>
    /// 提供 UnityRoot 下的标准索引根（Assets、Assets/Plugins、Packages）。
    /// Priority = 10（最高优先级，最先执行）。
    /// </summary>
    public sealed class UnityRootProvider : IIndexRootProvider
    {
        /// <inheritdoc/>
        public string ProviderId => "unity_root";

        /// <inheritdoc/>
        public int Priority => 10;

        /// <inheritdoc/>
        public IReadOnlyList<IndexRoot> DiscoverRoots(IndexWorkspace workspace)
        {
            var result = new List<IndexRoot>();
            if (string.IsNullOrEmpty(workspace?.UnityRoot)) return result;

            var unityRoot = workspace.UnityRoot;

            // Assets — 主项目代码。始终以整个 Assets 为 Root，Assets/Scripts 之外的代码（如 Assets/Art 下的脚本）同样要能搜到；
            // Assets/Plugins 是单独的只读 Root，扫描时作为嵌套 Root 剪掉，这里也排除以防它未被解析出来
            var assetsPath = Path.Combine(unityRoot, "Assets").Replace('\\', '/');
            if (Directory.Exists(assetsPath))
            {
                var assetsRoot = new IndexRoot
                {
                    RootPath = assetsPath,
                    RelativeToWorkspace = MakeRelative(workspace.WorkspaceRoot, assetsPath),
                    DisplayName = "Assets",
                    ScopeType = IndexScopeType.Project,
                    ScopeName = "Project",
                    Role = IndexRootRole.EditableProjectCode,
                    ReadOnly = false,
                    IsEnabled = true,
                    IsDefaultSearchScope = true,
                    ProviderId = ProviderId
                };
                assetsRoot.ExcludePatterns.Add("Plugins/");
                result.Add(assetsRoot);
            }

            // Assets/Plugins — 第三方插件（只读）
            var pluginsPath = Path.Combine(unityRoot, "Assets", "Plugins").Replace('\\', '/');
            if (Directory.Exists(pluginsPath))
            {
                result.Add(new IndexRoot
                {
                    RootPath = pluginsPath,
                    RelativeToWorkspace = MakeRelative(workspace.WorkspaceRoot, pluginsPath),
                    DisplayName = "Assets/Plugins",
                    ScopeType = IndexScopeType.Plugin,
                    ScopeName = "Plugins",
                    Role = IndexRootRole.CommercialPlugin,
                    ReadOnly = true,
                    IsEnabled = true,
                    IsDefaultSearchScope = false,
                    ProviderId = ProviderId
                });
            }

            // Packages — UPM 包（工作区包可编辑，嵌入包只读）
            var packagesPath = Path.Combine(unityRoot, "Packages").Replace('\\', '/');
            if (Directory.Exists(packagesPath))
            {
                result.Add(new IndexRoot
                {
                    RootPath = packagesPath,
                    RelativeToWorkspace = MakeRelative(workspace.WorkspaceRoot, packagesPath),
                    DisplayName = "Packages",
                    ScopeType = IndexScopeType.Package,
                    ScopeName = "Packages",
                    Role = IndexRootRole.WorkspacePackage,
                    ReadOnly = false,
                    IsEnabled = true,
                    IsDefaultSearchScope = true,
                    ExcludePatterns = new List<string> { "bin/", "obj/", "Library/", "Temp/" },
                    ProviderId = ProviderId
                });
            }

            return result;
        }

        private static string MakeRelative(string workspaceRoot, string absolutePath)
        {
            if (string.IsNullOrEmpty(workspaceRoot)) return absolutePath;
            var root = workspaceRoot.TrimEnd('/') + "/";
            if (absolutePath.StartsWith(root, System.StringComparison.OrdinalIgnoreCase))
                return absolutePath.Substring(root.Length);
            return absolutePath;
        }
    }
}
