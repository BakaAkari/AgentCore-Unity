using System;
using System.Collections.Generic;

namespace AgentCore.Editor.Components.Indexing.Models
{
    /// <summary>
    /// 索引统计信息快照。
    /// </summary>
    public sealed class IndexingStats
    {
        /// <summary>所属 workspace 的数据库 ID。</summary>
        public int WorkspaceId { get; set; }

        /// <summary>Workspace 指纹。</summary>
        public string Fingerprint { get; set; }

        /// <summary>WorkspaceRoot 路径。</summary>
        public string WorkspaceRoot { get; set; }

        /// <summary>UnityRoot 路径。</summary>
        public string UnityRoot { get; set; }

        /// <summary>VCS 分线标识。</summary>
        public string BranchId { get; set; }

        /// <summary>当前存储后端类型（"jsonl" / "sqlite"）。</summary>
        public string StoreBackend { get; set; }

        /// <summary>已启用的 Root 数量。</summary>
        public int EnabledRootCount { get; set; }

        /// <summary>已索引的文件总数。</summary>
        public int TotalFiles { get; set; }

        /// <summary>已索引的符号总数。</summary>
        public int TotalSymbols { get; set; }

        /// <summary>存在语法错误的文件数。</summary>
        public int ErrorFileCount { get; set; }

        /// <summary>最后一次全量索引时间（null 表示从未执行）。</summary>
        public DateTime? LastFullIndexAt { get; set; }

        /// <summary>最后一次增量索引时间（null 表示从未执行）。</summary>
        public DateTime? LastIncrementalIndexAt { get; set; }

        /// <summary>最后一次全量索引耗时（秒）。</summary>
        public double LastFullIndexDurationSeconds { get; set; }

        /// <summary>按 Root 分组的统计信息。</summary>
        public List<RootStats> RootBreakdown { get; set; } = new List<RootStats>();

        // ── 扩展字段别名（SqliteIndexStore / SearchCodeTool 使用）──────────────

        /// <summary>存在语法错误的文件数（ErrorFileCount 的别名）。</summary>
        public int ErrorFiles
        {
            get => ErrorFileCount;
            set => ErrorFileCount = value;
        }

        /// <summary>已启用的 Root 数量（EnabledRootCount 的别名）。</summary>
        public int TotalRoots
        {
            get => EnabledRootCount;
            set => EnabledRootCount = value;
        }

        /// <summary>按 Scope 类型分组的文件数（key = scope_type 字符串）。</summary>
        public Dictionary<string, int> FilesByScope { get; set; } = new Dictionary<string, int>();

        /// <summary>按 Scope 类型分组的符号数（key = scope_type 字符串）。</summary>
        public Dictionary<string, int> SymbolsByScope { get; set; } = new Dictionary<string, int>();

        /// <summary>按 Scope 分组的统计信息（RootBreakdown 的别名）。</summary>
        public List<RootStats> ScopeBreakdown
        {
            get => RootBreakdown;
            set => RootBreakdown = value;
        }

        /// <summary>
        /// 单个 Root 的统计信息。
        /// </summary>
        public sealed class RootStats
        {
            /// <summary>Root 数据库 ID。</summary>
            public int RootId { get; set; }

            /// <summary>Root 显示名称。</summary>
            public string DisplayName { get; set; }

            /// <summary>Scope 类型。</summary>
            public IndexScopeType ScopeType { get; set; }

            /// <summary>Scope 名称。</summary>
            public string ScopeName { get; set; }

            /// <summary>是否只读。</summary>
            public bool ReadOnly { get; set; }

            /// <summary>该 Root 下已索引文件数。</summary>
            public int FileCount { get; set; }

            /// <summary>该 Root 下已索引符号数。</summary>
            public int SymbolCount { get; set; }
        }
    }
}
