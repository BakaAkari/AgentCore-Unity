using System;
using System.Collections.Generic;

namespace AgentCore.Editor.Workspace.Config
{
    /// <summary>
    /// 项目级 Workspace 配置，存储在 WorkspaceRoot/.agentcore/workspace.json。
    /// 由团队通过 VCS 共享，覆盖自动发现的 Scope Root 配置。
    /// </summary>
    [Serializable]
    public sealed class WorkspaceConfig
    {
        /// <summary>配置文件格式版本，用于向后兼容迁移。</summary>
        public int Version { get; set; } = 1;

        /// <summary>配置文件最后修改时间（ISO 8601 UTC）。</summary>
        public string LastModified { get; set; }

        /// <summary>
        /// 用户自定义的 Scope Root 配置列表。
        /// 与自动发现结果合并：已存在的条目覆盖字段，新条目追加。
        /// </summary>
        public List<WorkspaceRootOverride> ScopeRoots { get; set; } = new List<WorkspaceRootOverride>();

        /// <summary>
        /// 可选备注（供团队填写项目说明，不影响解析逻辑）。
        /// </summary>
        public string Notes { get; set; }
    }
}
