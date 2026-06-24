using System;

namespace AgentCore.Editor.Tools.Safety
{
    /// <summary>
    /// 工具能力位（G.1 治理层）。
    /// <para>
    /// 用 Flags 描述工具实际触达的能力面，供 <see cref="ToolRiskPolicy"/> 与未来的
    /// MCP / Plugin / TOOLS.md 暴露策略基于能力维度进行筛选与策略合并。
    /// </para>
    /// <para>
    /// 与 <see cref="ToolRiskLevel"/> 的关系：RiskLevel 描述"最坏情况严重程度"，
    /// Capabilities 描述"触达哪些受控资源"。二者结合可解释一个工具为什么需要确认/拦截。
    /// </para>
    /// </summary>
    [Flags]
    public enum ToolCapability : uint
    {
        /// <summary>未声明任何能力（默认）。</summary>
        None = 0,

        /// <summary>读取工程内文件、Asset、Scene、Console 等。</summary>
        ReadProject = 1u << 0,

        /// <summary>写入工程文件（非脚本）。</summary>
        WriteProjectFiles = 1u << 1,

        /// <summary>删除工程文件 / Asset。</summary>
        DeleteProjectFiles = 1u << 2,

        /// <summary>修改 Scene 内容（GameObject/组件层级）。</summary>
        ModifyScene = 1u << 3,

        /// <summary>修改 Asset（材质、Prefab、ScriptableObject 等）。</summary>
        ModifyAssets = 1u << 4,

        /// <summary>修改 C# 脚本（会触发编译，等同 High 起步）。</summary>
        ModifyScripts = 1u << 5,

        /// <summary>执行动态代码（C# 表达式、外部脚本、反射调用任意方法）。</summary>
        ExecuteCode = 1u << 6,

        /// <summary>安装 / 移除 / 升级 UPM 包。</summary>
        InstallPackages = 1u << 7,

        /// <summary>触发构建/Player 输出/出包。</summary>
        BuildPlayer = 1u << 8,

        /// <summary>发起外部网络请求（含 LLM / 云服务 / 自定义 HTTP）。</summary>
        NetworkAccess = 1u << 9,

        /// <summary>写入版本控制（commit / push / reset / 分支切换等）。</summary>
        VersionControlWrite = 1u << 10,

        /// <summary>批量执行 — 一次调用展开为多个子操作。</summary>
        BatchExecute = 1u << 11,

        /// <summary>修改工程设置 / Editor 配置 / Player Settings。</summary>
        ModifyProjectSettings = 1u << 12,

        /// <summary>修改 AgentCore 自身配置（API Key、Workspace 配置、Bootstrap 文件）。</summary>
        ModifyAgentConfig = 1u << 13
    }
}
