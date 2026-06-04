namespace AgentCore.Editor.Components.Indexing.Query
{
    /// <summary>
    /// 符号搜索查询参数。
    /// </summary>
    public sealed class SearchQuery
    {
        /// <summary>搜索关键词（符号名称或完全限定名的一部分）。</summary>
        public string Query { get; set; }

        /// <summary>
        /// 符号类型过滤（null 表示不过滤）。
        /// 可选值：class / interface / struct / enum / method / property / field / event / constructor / delegate。
        /// </summary>
        public string SymbolType { get; set; }

        /// <summary>Scope 类型过滤（null 表示不过滤）。</summary>
        public Models.IndexScopeType? ScopeType { get; set; }

        /// <summary>Scope 名称过滤（null 表示不过滤，大小写不敏感）。</summary>
        public string ScopeName { get; set; }

        /// <summary>Root ID 过滤（0 表示不过滤）。</summary>
        public int RootId { get; set; }

        /// <summary>Root 角色过滤（null 表示不过滤）。</summary>
        public Models.IndexRootRole? Role { get; set; }

        /// <summary>是否包含 Plugin 类型的 Root（默认 false）。</summary>
        public bool IncludePlugins { get; set; } = false;

        /// <summary>是否包含 Engine 类型的 Root（默认 true）。</summary>
        public bool IncludeEngine { get; set; } = true;

        /// <summary>是否包含 Generated 类型的 Root（默认 false）。</summary>
        public bool IncludeGenerated { get; set; } = false;

        /// <summary>只读过滤（null 表示不过滤，true 只返回只读，false 只返回可写）。</summary>
        public bool? ReadOnly { get; set; }

        /// <summary>是否启用模糊匹配（包含匹配，默认 true）。</summary>
        public bool Fuzzy { get; set; } = true;

        /// <summary>是否使用正则表达式匹配（默认 false）。</summary>
        public bool Regex { get; set; } = false;

        /// <summary>返回结果数量上限（默认 50，最大 200）。</summary>
        public int Limit { get; set; } = 50;

        /// <summary>命名空间过滤（null 表示不过滤，前缀匹配）。</summary>
        public string Namespace { get; set; }
    }
}
