namespace AgentCore.Editor.Components.Indexing.Models
{
    /// <summary>
    /// 索引根的操作角色，决定工具安全策略。
    /// 与 WorkspaceRootRole 镜像（独立程序集，不强引用）。
    /// </summary>
    public enum IndexRootRole
    {
        /// <summary>主工程可编辑代码，可读写。</summary>
        EditableProjectCode,

        /// <summary>跨模块共享代码，可读写但需提示影响范围。</summary>
        SharedCode,

        /// <summary>工作区内的功能包，可读，写入需明确 Scope。</summary>
        WorkspacePackage,

        /// <summary>商业第三方插件，默认只读。</summary>
        CommercialPlugin,

        /// <summary>自定义插件，可读写但需提示。</summary>
        CustomPlugin,

        /// <summary>引擎层代码，默认只读或强确认。</summary>
        EngineCode,

        /// <summary>构建/工具链代码，可读写但需提示构建影响。</summary>
        ToolingCode,

        /// <summary>自动生成代码，默认禁止写入。</summary>
        GeneratedCode,

        /// <summary>只读参考目录，禁止写入。</summary>
        ReadOnlyReference
    }
}
