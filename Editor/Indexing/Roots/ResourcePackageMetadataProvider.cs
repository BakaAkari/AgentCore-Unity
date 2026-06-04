using System.Collections.Generic;
using AgentCore.Editor.Components.Indexing.Models;

namespace AgentCore.Editor.Components.Indexing.Roots
{
    /// <summary>
    /// 资源包元数据 Provider（预留接口，当前为 stub）。
    /// 未来将从项目资源包系统（如自研资源包管理器）读取已同步/启用资源包的
    /// Scope、Role、package_id、read_only 等元数据，并将其注册为 IndexRoot。
    ///
    /// 当前版本：不发现任何根，仅作为扩展点占位。
    /// Priority = 50（在用户配置之后执行）。
    /// </summary>
    public sealed class ResourcePackageMetadataProvider : IIndexRootProvider
    {
        /// <inheritdoc/>
        public string ProviderId => "resource_package_metadata";

        /// <inheritdoc/>
        public int Priority => 50;

        /// <inheritdoc/>
        /// <remarks>
        /// 当前为 stub 实现，始终返回空列表。
        /// 未来版本将通过以下方式发现资源包根：
        /// 1. 读取项目资源包系统的 manifest 文件（如 .agentcore/resource-packages.json）。
        /// 2. 调用资源包管理器 API（如果项目提供了 Editor API）。
        /// 3. 扫描 WorkspaceRoot 下符合资源包目录结构约定的目录。
        /// </remarks>
        public IReadOnlyList<IndexRoot> DiscoverRoots(IndexWorkspace workspace)
        {
            // TODO: 实现资源包元数据读取逻辑
            // 典型实现步骤：
            // 1. 检查 workspace.WorkspaceRoot 下是否存在资源包 manifest
            // 2. 解析 manifest 获取已启用的资源包列表
            // 3. 为每个资源包创建 IndexRoot（ScopeType=Package, Role=WorkspacePackage）
            // 4. 设置 ReadOnly 标志（已发布/锁定的资源包应标记为只读）
            return System.Array.Empty<IndexRoot>();
        }
    }
}
