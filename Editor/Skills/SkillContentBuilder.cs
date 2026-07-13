using System.Text;

namespace AgentCore.Editor.Skills
{
    /// <summary>
    /// 构建 Skill 内容的 system message 文本，并定义跨轮次保留标记。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Marker"/> 是本模块与 <c>ConversationCompressor</c> 之间的契约：
    /// Compressor 遇到以此前缀开头的 system 消息时必须跳过（不参与压缩），
    /// 以保证已加载的 Skill 内容在长会话中稳定可用。
    /// </para>
    /// <para>
    /// 与现有 <c>WorkspaceSnapshotBuilder.SnapshotMarker</c> / <c>"# Available Tools"</c> 平级，
    /// 是第 4 类"运行时静态上下文"标记。
    /// </para>
    /// </remarks>
    public static class SkillContentBuilder
    {
        /// <summary>
        /// Skill system message 的固定前缀。ConversationCompressor 依赖此标记跳过压缩。
        /// </summary>
        /// <remarks>
        /// 严禁修改本常量，除非同步更新 <c>ConversationCompressor.FindCompressibleRange</c>。
        /// </remarks>
        public const string Marker = "# [SKILL] ";

        /// <summary>
        /// 构建一条完整的 Skill system message 文本。
        /// </summary>
        /// <param name="skillName">Skill 名称。</param>
        /// <param name="content">Skill 全文（应已剥离 frontmatter）。</param>
        public static string Build(string skillName, string content)
        {
            var sb = new StringBuilder();
            sb.Append(Marker).AppendLine(skillName ?? "unknown");
            sb.AppendLine();
            sb.Append(content ?? string.Empty);
            return sb.ToString();
        }

        /// <summary>
        /// 判断给定 system 消息内容是否为 Skill 消息（用于 AgentLoop 去重/清理）。
        /// </summary>
        public static bool IsSkillMessage(string messageContent)
        {
            return !string.IsNullOrEmpty(messageContent) && messageContent.StartsWith(Marker);
        }

        /// <summary>
        /// 从 Skill 消息中提取 skill 名称（首行 marker 之后的内容）。返回 null 表示不是 Skill 消息或格式错误。
        /// </summary>
        public static string TryExtractName(string messageContent)
        {
            if (!IsSkillMessage(messageContent))
                return null;

            var firstLineEnd = messageContent.IndexOf('\n');
            var firstLine = firstLineEnd > 0
                ? messageContent.Substring(0, firstLineEnd).TrimEnd('\r')
                : messageContent;

            var name = firstLine.Substring(Marker.Length).Trim();
            return string.IsNullOrEmpty(name) ? null : name;
        }
    }
}
