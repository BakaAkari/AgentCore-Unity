using System;

namespace AgentCore.Editor.Components.Indexing.Core
{
    /// <summary>
    /// 索引操作的进度快照。
    /// 由 <see cref="CodebaseIndexer"/> 通过回调上报，供 UI 和工具层消费。
    /// </summary>
    public sealed class IndexingProgress
    {
        /// <summary>当前索引阶段。</summary>
        public IndexingPhase Phase { get; set; }

        /// <summary>当前正在处理的 Root 路径（可为 null）。</summary>
        public string CurrentRoot { get; set; }

        /// <summary>当前正在处理的文件路径（可为 null）。</summary>
        public string CurrentFile { get; set; }

        /// <summary>已处理的文件数。</summary>
        public int ProcessedFiles { get; set; }

        /// <summary>总文件数（扫描阶段完成后才有值，扫描中为 0）。</summary>
        public int TotalFiles { get; set; }

        /// <summary>已提取的符号数。</summary>
        public int ExtractedSymbols { get; set; }

        /// <summary>解析失败的文件数。</summary>
        public int ErrorFiles { get; set; }

        /// <summary>已跳过的文件数（超大文件、排除规则等）。</summary>
        public int SkippedFiles { get; set; }

        /// <summary>进度百分比（0-100），TotalFiles=0 时为 -1（不确定）。</summary>
        public int ProgressPercent => TotalFiles > 0
            ? Math.Min(100, (int)(ProcessedFiles * 100.0 / TotalFiles))
            : -1;

        /// <summary>是否已完成（成功或失败）。</summary>
        public bool IsCompleted { get; set; }

        /// <summary>是否成功完成（IsCompleted=true 且无致命错误）。</summary>
        public bool IsSuccess { get; set; }

        /// <summary>致命错误信息（仅 IsSuccess=false 时有值）。</summary>
        public string ErrorMessage { get; set; }

        /// <summary>操作开始时间（UTC）。</summary>
        public DateTime StartedAt { get; set; }

        /// <summary>操作完成时间（UTC，仅 IsCompleted=true 时有值）。</summary>
        public DateTime? CompletedAt { get; set; }

        /// <summary>已耗时（秒）。</summary>
        public double ElapsedSeconds => IsCompleted && CompletedAt.HasValue
            ? (CompletedAt.Value - StartedAt).TotalSeconds
            : (DateTime.UtcNow - StartedAt).TotalSeconds;

        /// <summary>
        /// 创建初始进度快照。
        /// </summary>
        public static IndexingProgress CreateStarted(IndexingPhase phase)
        {
            return new IndexingProgress
            {
                Phase = phase,
                StartedAt = DateTime.UtcNow,
            };
        }

        /// <summary>
        /// 创建成功完成快照。
        /// </summary>
        public static IndexingProgress CreateCompleted(IndexingProgress current)
        {
            return new IndexingProgress
            {
                Phase = current.Phase,
                CurrentRoot = current.CurrentRoot,
                ProcessedFiles = current.ProcessedFiles,
                TotalFiles = current.TotalFiles,
                ExtractedSymbols = current.ExtractedSymbols,
                ErrorFiles = current.ErrorFiles,
                SkippedFiles = current.SkippedFiles,
                IsCompleted = true,
                IsSuccess = true,
                StartedAt = current.StartedAt,
                CompletedAt = DateTime.UtcNow,
            };
        }

        /// <summary>
        /// 创建失败完成快照。
        /// </summary>
        public static IndexingProgress CreateFailed(IndexingProgress current, string errorMessage)
        {
            return new IndexingProgress
            {
                Phase = current.Phase,
                CurrentRoot = current.CurrentRoot,
                ProcessedFiles = current.ProcessedFiles,
                TotalFiles = current.TotalFiles,
                ExtractedSymbols = current.ExtractedSymbols,
                ErrorFiles = current.ErrorFiles,
                SkippedFiles = current.SkippedFiles,
                IsCompleted = true,
                IsSuccess = false,
                ErrorMessage = errorMessage,
                StartedAt = current.StartedAt,
                CompletedAt = DateTime.UtcNow,
            };
        }

        /// <summary>
        /// 生成人类可读的进度摘要字符串。
        /// </summary>
        public override string ToString()
        {
            if (IsCompleted)
            {
                return IsSuccess
                    ? $"索引完成：{ProcessedFiles} 个文件，{ExtractedSymbols} 个符号，耗时 {ElapsedSeconds:F1}s"
                    : $"索引失败：{ErrorMessage}";
            }

            var pct = ProgressPercent >= 0 ? $" ({ProgressPercent}%)" : "";
            return $"{Phase}: {ProcessedFiles}/{TotalFiles} 文件{pct}，{ExtractedSymbols} 符号";
        }
    }

    /// <summary>
    /// 索引操作阶段枚举。
    /// </summary>
    public enum IndexingPhase
    {
        /// <summary>初始化（解析 Workspace、发现 Root）。</summary>
        Initializing,

        /// <summary>扫描文件列表（不解析内容）。</summary>
        Scanning,

        /// <summary>全量索引（解析所有文件）。</summary>
        FullIndexing,

        /// <summary>增量索引（只处理变更文件）。</summary>
        IncrementalIndexing,

        /// <summary>写入存储（持久化到 JSONL/SQLite）。</summary>
        Persisting,

        /// <summary>已完成。</summary>
        Completed,
    }
}
