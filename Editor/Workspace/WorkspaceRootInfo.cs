using System;

namespace AgentCore.Editor.Workspace
{
    /// <summary>
    /// WorkspaceRoot 下单个子根目录的描述信息。
    /// 由 ScopeRootResolver 自动发现或用户手动配置。
    /// </summary>
    [Serializable]
    public sealed class WorkspaceRootInfo
    {
        /// <summary>
        /// 唯一标识符，格式为 WorkspaceRoot-relative 路径的规范化形式，
        /// 例如 "unity"、"gamemodes"、"tools/build"。
        /// </summary>
        public string Id { get; set; }

        /// <summary>用于 UI 展示的友好名称。</summary>
        public string DisplayName { get; set; }

        /// <summary>绝对路径（规范化正斜杠）。</summary>
        public string AbsolutePath { get; set; }

        /// <summary>相对于 WorkspaceRoot 的路径（规范化正斜杠）。</summary>
        public string RelativePath { get; set; }

        /// <summary>业务范畴类型。</summary>
        public WorkspaceScopeType ScopeType { get; set; }

        /// <summary>
        /// 可选的自定义 Scope 名称（当 ScopeType = Unknown 或用户自定义时使用）。
        /// </summary>
        public string ScopeName { get; set; }

        /// <summary>操作角色，决定工具安全策略。</summary>
        public WorkspaceRootRole Role { get; set; }

        /// <summary>是否为只读目录（CommercialPlugin / EngineCode / ReadOnlyReference 默认 true）。</summary>
        public bool IsReadOnly { get; set; }

        /// <summary>是否为自动生成目录（GeneratedCode 默认 true）。</summary>
        public bool IsGenerated { get; set; }

        /// <summary>是否在 AgentCore 中启用（用户可在 Settings 中禁用）。</summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>是否由 ScopeRootResolver 自动检测到（false 表示用户手动添加）。</summary>
        public bool IsDetected { get; set; }

        /// <summary>
        /// 来源标记，例如 "auto"（自动发现）、"manual"（用户手动添加）、
        /// "workspace.json"（从项目配置文件加载）。
        /// </summary>
        public string Source { get; set; }
    }
}
