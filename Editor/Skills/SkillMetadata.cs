using System;

namespace AgentCore.Editor.Skills
{
    /// <summary>
    /// 描述一个 Skill 的元数据（不含全文，用于 list 展示）。
    /// </summary>
    /// <remarks>
    /// 由 <see cref="SkillRegistry"/> 从磁盘扫描 SKILL.md 文件构建。
    /// 全文内容通过 <see cref="SkillRegistry.GetContent(string)"/> 按需读取，避免启动时 IO 峰值。
    /// </remarks>
    public sealed class SkillMetadata
    {
        /// <summary>Skill 名称（唯一 ID，全 lowercase，用连字符分隔）。默认取自目录名。</summary>
        public string Name { get; }

        /// <summary>Skill 简短描述（一行，供 LLM 判断是否加载）。frontmatter description 或首个 # 标题下的段落。</summary>
        public string Description { get; }

        /// <summary>Skill 分类（可选，如 "development" / "analysis" / "documentation"）。</summary>
        public string Category { get; }

        /// <summary>Skill 版本（可选）。</summary>
        public string Version { get; }

        /// <summary>SKILL.md 文件的绝对路径。</summary>
        public string FilePath { get; }

        /// <summary>文件字符长度（用于估算 token）。</summary>
        public int CharCount { get; }

        /// <summary>估算的 token 数（char / 3 的经验值，与 <see cref="Bootstrap.BootstrapContext.EstimateTokenCount"/> 一致）。</summary>
        public int EstimatedTokens => CharCount / 3;

        public SkillMetadata(
            string name,
            string description,
            string category,
            string version,
            string filePath,
            int charCount)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Description = description ?? string.Empty;
            Category = string.IsNullOrWhiteSpace(category) ? "general" : category;
            Version = string.IsNullOrWhiteSpace(version) ? "1.0" : version;
            FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
            CharCount = charCount;
        }
    }
}
