namespace AgentCore.Editor.Components.Indexing.Models
{
    /// <summary>
    /// C# 符号信息，由 Roslyn 语法树提取。
    /// </summary>
    public sealed class SymbolInfo
    {
        /// <summary>数据库自增主键（0 表示尚未持久化）。</summary>
        public int Id { get; set; }

        /// <summary>所属文件的数据库 ID。</summary>
        public int FileId { get; set; }

        /// <summary>所属 workspace 的数据库 ID（冗余，加速查询）。</summary>
        public int WorkspaceId { get; set; }

        /// <summary>所属 root 的数据库 ID（冗余，加速查询）。</summary>
        public int RootId { get; set; }

        /// <summary>
        /// 符号类型：class / interface / struct / enum / method /
        /// property / field / event / constructor / delegate。
        /// </summary>
        public string SymbolType { get; set; }

        /// <summary>符号名称（不含命名空间）。</summary>
        public string Name { get; set; }

        /// <summary>完整命名空间（无命名空间时为 "&lt;global&gt;"）。</summary>
        public string Namespace { get; set; }

        /// <summary>完全限定名（Namespace.Name）。</summary>
        public string FullName { get; set; }

        /// <summary>可访问性：public / internal / protected / private / protected internal / private protected。</summary>
        public string Accessibility { get; set; }

        /// <summary>是否为静态成员。</summary>
        public bool IsStatic { get; set; }

        /// <summary>是否为抽象成员。</summary>
        public bool IsAbstract { get; set; }

        /// <summary>是否为 partial 类型。</summary>
        public bool IsPartial { get; set; }

        /// <summary>是否为 virtual 成员。</summary>
        public bool IsVirtual { get; set; }

        /// <summary>是否为 override 成员。</summary>
        public bool IsOverride { get; set; }

        /// <summary>是否为 readonly 字段。</summary>
        public bool IsReadOnly { get; set; }

        /// <summary>是否为 const 字段。</summary>
        public bool IsConst { get; set; }

        /// <summary>返回类型文本（方法/属性/字段/事件）。</summary>
        public string ReturnType { get; set; }

        /// <summary>参数列表文本（方法/构造函数/委托）。</summary>
        public string Parameters { get; set; }

        /// <summary>基类/接口列表文本（类/接口/结构体）。</summary>
        public string BaseTypes { get; set; }

        /// <summary>泛型参数文本（如 "&lt;T, TKey&gt;"）。</summary>
        public string GenericParams { get; set; }

        /// <summary>声明代码片段（声明行 ± 2 行，最多 5 行，去除方法体）。</summary>
        public string DeclarationSnippet { get; set; }

        /// <summary>符号在文件中的起始行号（1-based）。</summary>
        public int LineNumber { get; set; }

        /// <summary>所属文件绝对路径（冗余，加速查询）。</summary>
        public string FilePath { get; set; }

        /// <summary>所属 Scope 类型（冗余，加速过滤）。</summary>
        public IndexScopeType ScopeType { get; set; }

        /// <summary>所属 Scope 名称（冗余，加速过滤）。</summary>
        public string ScopeName { get; set; }

        /// <summary>所属 Root 角色（冗余，加速过滤）。</summary>
        public IndexRootRole Role { get; set; }

        /// <summary>是否只读（冗余，加速过滤）。</summary>
        public bool ReadOnly { get; set; }
    }
}
