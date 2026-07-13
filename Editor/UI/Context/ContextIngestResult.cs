using System.Collections.Generic;

namespace AgentCore.Editor.UI.Context
{
    /// <summary>
    /// 单次 Context 注入的采集结果。
    /// 由 Collector 生成，由 ChatWindow 输入框注入路径消费。
    /// </summary>
    public class ContextIngestResult
    {
        /// <summary>
        /// 简短标签，用作 markdown 段的标题（例如 "Selection: Cube (+2 more)"）。
        /// </summary>
        public string Label { get; }

        /// <summary>
        /// 采集出的完整 markdown 文本，不含标题。
        /// 由 <see cref="ContextIngestFormatter"/> 统一包裹成 [@Label]\n```lang\n...\n``` 格式。
        /// </summary>
        public string Content { get; }

        /// <summary>
        /// 内容是否被截断（用于警告用户）。
        /// </summary>
        public bool Truncated { get; }

        /// <summary>
        /// 采集期间的额外警告消息（例如 "Scene too large"）。为 null 表示无警告。
        /// </summary>
        public string Warning { get; }

        /// <summary>
        /// 粗略 token 估算（Content 长度 / 3）。仅用于软上限提示，不影响 LLM 实际使用。
        /// </summary>
        public int EstimatedTokens => string.IsNullOrEmpty(Content) ? 0 : Content.Length / 3;

        private ContextIngestResult(string label, string content, bool truncated, string warning)
        {
            Label = label;
            Content = content;
            Truncated = truncated;
            Warning = warning;
        }

        /// <summary>
        /// 正常采集成功的结果。
        /// </summary>
        public static ContextIngestResult Ok(string label, string content, bool truncated = false)
            => new ContextIngestResult(label, content, truncated, null);

        /// <summary>
        /// 采集完成但需要展示警告（内容依然可用）。
        /// </summary>
        public static ContextIngestResult OkWithWarning(string label, string content, string warning, bool truncated = false)
            => new ContextIngestResult(label, content, truncated, warning);

        /// <summary>
        /// 无内容可采集（例如空选择、无焦点匹配），调用方应静默忽略。
        /// </summary>
        public static ContextIngestResult Empty()
            => new ContextIngestResult(null, null, false, null);

        /// <summary>
        /// 是否为空结果。
        /// </summary>
        public bool IsEmpty => string.IsNullOrEmpty(Content);
    }

    /// <summary>
    /// 采集参数常量。
    /// 所有阈值集中在此便于统一调整；不暴露为设置项（ADR-17 默认最佳）。
    /// </summary>
    internal static class ContextIngestLimits
    {
        /// <summary>Selection 数量降级阈值：超过此值只列名称不列组件详情。</summary>
        public const int SelectionDetailLimit = 20;

        /// <summary>Selection 数量硬上限：超过此值只保留前 N 个名称 + 省略号。</summary>
        public const int SelectionNameLimit = 100;

        /// <summary>组件 SerializedProperty 采集深度上限，避免嵌套结构爆炸。</summary>
        public const int ComponentFieldMaxDepth = 3;

        /// <summary>单个字段字符串值截断长度。</summary>
        public const int FieldValueMaxLength = 120;

        /// <summary>单个组件最多列出的字段数。</summary>
        public const int ComponentFieldMaxCount = 20;

        /// <summary>Console 采集最大条目数。</summary>
        public const int ConsoleMaxEntries = 20;

        /// <summary>单条 Console 消息截断长度。</summary>
        public const int ConsoleMessageMaxLength = 800;

        /// <summary>单条 stack trace 截断长度。</summary>
        public const int ConsoleStackMaxLength = 400;

        /// <summary>Scene 全量遍历阈值。</summary>
        public const int SceneFullDumpThreshold = 100;

        /// <summary>Scene 中量采样阈值。</summary>
        public const int SceneModerateThreshold = 1000;

        /// <summary>Scene 大规模采样阈值。</summary>
        public const int SceneLargeThreshold = 10000;

        /// <summary>Scene 中量场景下每层最多列出的 GO 数。</summary>
        public const int SceneModerateChildrenPerLevel = 50;

        /// <summary>Scene 大规模场景下每层最多列出的 GO 数。</summary>
        public const int SceneLargeChildrenPerLevel = 20;

        /// <summary>单次采集内容字符数硬上限（超过时截断并 warn）。</summary>
        public const int SingleResultMaxChars = 15000;

        /// <summary>输入框累积 token 软上限，超过时发送前提醒。</summary>
        public const int InputBufferTokenSoftLimit = 15000;

        /// <summary>Asset 多选详情降级阈值。</summary>
        public const int AssetDetailLimit = 20;
    }
}
