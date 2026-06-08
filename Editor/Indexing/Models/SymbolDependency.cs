namespace AgentCore.Editor.Components.Indexing.Models
{
    /// <summary>
    /// C# 类型依赖关系记录，由 DependencyExtractor 从 SyntaxTree 中提取。
    /// 表示"某个符号/文件引用了某个类型"的有向边。
    /// </summary>
    public sealed class SymbolDependency
    {
        /// <summary>数据库自增主键（0 表示尚未持久化）。</summary>
        public int Id { get; set; }

        /// <summary>所属 workspace 的数据库 ID。</summary>
        public int WorkspaceId { get; set; }

        /// <summary>发起依赖的文件 ID（来源文件）。</summary>
        public int FromFileId { get; set; }

        /// <summary>
        /// 发起依赖的符号 ID（来源符号）。
        /// null 表示文件级依赖（如 using 指令），不归属于具体符号。
        /// </summary>
        public int? FromSymbolId { get; set; }

        /// <summary>
        /// 被引用的类型名称（简名或全名，取决于源码写法）。
        /// 例如："MonoBehaviour"、"UnityEngine.MonoBehaviour"、"List&lt;PlayerController&gt;" 中的 "PlayerController"。
        /// </summary>
        public string ToTypeName { get; set; }

        /// <summary>
        /// 解析后的目标符号 ID（在本 workspace 中找到对应符号时填充）。
        /// null 表示外部类型（如 UnityEngine、System 等），或尚未解析。
        /// </summary>
        public int? ToSymbolId { get; set; }

        /// <summary>
        /// 依赖关系类型。
        /// 可选值：inheritance / interface_impl / field_type / method_param /
        ///         method_return / attribute / generic_arg / using_directive。
        /// </summary>
        public string DependencyKind { get; set; }

        /// <summary>依赖关系在源文件中的行号（1-based）。</summary>
        public int SourceLine { get; set; }
    }

    /// <summary>
    /// 依赖关系类型常量，与 <see cref="SymbolDependency.DependencyKind"/> 对应。
    /// </summary>
    public static class DependencyKind
    {
        /// <summary>继承基类：class A : B</summary>
        public const string Inheritance = "inheritance";

        /// <summary>实现接口：class A : IB</summary>
        public const string InterfaceImpl = "interface_impl";

        /// <summary>字段类型引用：private PlayerController _player;</summary>
        public const string FieldType = "field_type";

        /// <summary>方法参数类型：void Foo(PlayerController p)</summary>
        public const string MethodParam = "method_param";

        /// <summary>方法返回类型：PlayerController GetPlayer()</summary>
        public const string MethodReturn = "method_return";

        /// <summary>Attribute 引用：[SerializeField]</summary>
        public const string Attribute = "attribute";

        /// <summary>泛型参数：List&lt;PlayerController&gt;</summary>
        public const string GenericArg = "generic_arg";

        /// <summary>using 指令（命名空间级）：using UnityEngine;</summary>
        public const string UsingDirective = "using_directive";
    }
}
