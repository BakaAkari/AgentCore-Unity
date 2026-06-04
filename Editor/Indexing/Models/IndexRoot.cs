using System.Collections.Generic;

namespace AgentCore.Editor.Components.Indexing.Models
{
    /// <summary>
    /// 索引根目录，代表一个可独立索引的代码范围单元。
    /// </summary>
    public sealed class IndexRoot
    {
        /// <summary>数据库自增主键（0 表示尚未持久化）。</summary>
        public int Id { get; set; }

        /// <summary>所属 workspace 的数据库 ID。</summary>
        public int WorkspaceId { get; set; }

        /// <summary>根目录绝对路径（规范化正斜杠）。</summary>
        public string RootPath { get; set; }

        /// <summary>相对于 WorkspaceRoot 的路径（用于显示和指纹计算）。</summary>
        public string RelativeToWorkspace { get; set; }

        /// <summary>UI 显示名称。</summary>
        public string DisplayName { get; set; }

        /// <summary>业务范畴类型。</summary>
        public IndexScopeType ScopeType { get; set; }

        /// <summary>Scope 名称（如 "Battle"、"City01"、"UICommon"）。</summary>
        public string ScopeName { get; set; }

        /// <summary>操作角色，决定安全策略。</summary>
        public IndexRootRole Role { get; set; }

        /// <summary>是否只读（禁止 Agent 写入建议）。</summary>
        public bool ReadOnly { get; set; }

        /// <summary>是否启用索引。</summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>文件包含模式列表（如 ["*.cs"]）。</summary>
        public List<string> IncludePatterns { get; set; } = new List<string> { "*.cs" };

        /// <summary>文件排除模式列表（如 ["bin/", "obj/", "Library/"]）。</summary>
        public List<string> ExcludePatterns { get; set; } = new List<string>
        {
            "bin/", "obj/", "Library/", "Temp/", "Generated/"
        };

        /// <summary>提供此根的 Provider ID（用于调试和覆盖）。</summary>
        public string ProviderId { get; set; }

        /// <summary>
        /// 是否为默认搜索范围（搜索时不指定 scope 则包含此根）。
        /// Plugin / Generated / Engine 默认为 false。
        /// </summary>
        public bool IsDefaultSearchScope { get; set; } = true;

        /// <summary>
        /// 根据 ScopeType 和 Role 推断默认只读状态。
        /// </summary>
        public static bool InferReadOnly(IndexScopeType scopeType, IndexRootRole role)
        {
            if (role == IndexRootRole.CommercialPlugin ||
                role == IndexRootRole.EngineCode ||
                role == IndexRootRole.GeneratedCode ||
                role == IndexRootRole.ReadOnlyReference)
                return true;

            if (scopeType == IndexScopeType.Engine ||
                scopeType == IndexScopeType.Generated)
                return true;

            return false;
        }

        /// <summary>
        /// 根据 ScopeType 推断是否为默认搜索范围。
        /// </summary>
        public static bool InferDefaultSearchScope(IndexScopeType scopeType)
        {
            return scopeType != IndexScopeType.Generated &&
                   scopeType != IndexScopeType.Plugin &&
                   scopeType != IndexScopeType.Engine;
        }
    }
}
