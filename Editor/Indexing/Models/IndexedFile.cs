namespace AgentCore.Editor.Components.Indexing.Models
{
    /// <summary>
    /// 已索引文件的元数据记录。
    /// </summary>
    public sealed class IndexedFile
    {
        /// <summary>数据库自增主键（0 表示尚未持久化）。</summary>
        public int Id { get; set; }

        /// <summary>所属 workspace 的数据库 ID。</summary>
        public int WorkspaceId { get; set; }

        /// <summary>所属 index root 的数据库 ID。</summary>
        public int RootId { get; set; }

        /// <summary>文件绝对路径（规范化正斜杠）。</summary>
        public string FilePath { get; set; }

        /// <summary>相对于 RootPath 的路径。</summary>
        public string RelativeToRoot { get; set; }

        /// <summary>文件内容 MD5 hash（用于增量索引变更检测）。</summary>
        public string ContentHash { get; set; }

        /// <summary>文件最后修改时间（UTC Unix 时间戳秒）。</summary>
        public long LastModified { get; set; }

        /// <summary>最后索引时间（UTC Unix 时间戳秒）。</summary>
        public long LastIndexed { get; set; }

        /// <summary>文件大小（字节）。</summary>
        public long FileSize { get; set; }

        /// <summary>是否存在语法错误（Roslyn 解析时检测到）。</summary>
        public bool HasErrors { get; set; }

        /// <summary>解析错误信息（HasErrors = true 时填充）。</summary>
        public string ErrorMessage { get; set; }

        /// <summary>该文件中提取到的符号数量。</summary>
        public int SymbolCount { get; set; }
    }
}
