using System;

namespace AgentCore.Editor.Tools.Infrastructure
{
    /// <summary>
    /// 标记一个类为 AgentCore 原生工具。
    /// 被标记的类必须实现 IAgentTool 接口。
    /// ToolAutoDiscovery 会自动扫描并注册所有带此属性的类。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class AgentToolAttribute : Attribute
    {
        /// <summary>
        /// 工具名称（LLM 调用时使用的标识符）
        /// 例如: "manage_scene", "find_gameobjects"
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// 工具描述（LLM 用来理解工具用途）
        /// </summary>
        public string Description { get; set; } = "";

        /// <summary>
        /// 工具分类
        /// 例如: "Scene", "GameObject", "Script", "Asset"
        /// </summary>
        public string Category { get; set; } = "General";

        /// <summary>
        /// 是否需要在 Unity 主线程执行
        /// 大多数 Unity API 调用需要主线程
        /// </summary>
        public bool RequiresMainThread { get; set; } = true;

        /// <summary>
        /// 此工具是否可能修改脚本文件（触发编译）
        /// 用于 AgentLoop 的编译等待逻辑
        /// </summary>
        public bool MayModifyScripts { get; set; } = false;

        public AgentToolAttribute(string name)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
        }
    }
}
