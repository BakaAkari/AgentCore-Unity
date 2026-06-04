namespace AgentCore.Editor.Workspace
{
    /// <summary>
    /// Workspace 子根的业务范畴类型。
    /// 用于标识 WorkspaceRoot 下各子目录的业务语义。
    /// </summary>
    public enum WorkspaceScopeType
    {
        /// <summary>主 Unity 工程代码（可编辑项目代码）。</summary>
        Project,

        /// <summary>地图/关卡资源包。</summary>
        Map,

        /// <summary>游戏模式/玩法模块包。</summary>
        Mode,

        /// <summary>通用 UPM 包或工作区包。</summary>
        Package,

        /// <summary>跨模块共享代码库。</summary>
        Shared,

        /// <summary>UI 资源与代码。</summary>
        UI,

        /// <summary>本地化资源。</summary>
        Localization,

        /// <summary>引擎层代码（只读参考）。</summary>
        Engine,

        /// <summary>第三方插件（商业或自定义）。</summary>
        Plugin,

        /// <summary>构建/工具链脚本。</summary>
        Tools,

        /// <summary>自动生成代码（禁止手动修改）。</summary>
        Generated,

        /// <summary>无法识别的目录。</summary>
        Unknown
    }
}
