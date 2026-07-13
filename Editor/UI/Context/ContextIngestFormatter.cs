using System.Text;

namespace AgentCore.Editor.UI.Context
{
    /// <summary>
    /// 统一 markdown 格式化器。
    /// 将 <see cref="ContextIngestResult"/> 包裹成可注入输入框的 markdown 块。
    /// </summary>
    public static class ContextIngestFormatter
    {
        private const string BlockOpen = "```";
        private const string BlockClose = "```";
        private const string Ellipsis = "\n... (truncated)";

        /// <summary>
        /// 生成注入到输入框的完整 markdown 块。
        /// 格式：
        ///   [@Label]
        ///   ```
        ///   {Content}
        ///   ```
        /// 保证以 \n 结尾，方便追加到输入框光标位置后用户直接换行继续写。
        /// </summary>
        public static string Format(ContextIngestResult result)
        {
            if (result == null || result.IsEmpty) return string.Empty;

            var sb = new StringBuilder(result.Content.Length + 64);
            sb.Append('[').Append('@').Append(result.Label).Append(']').Append('\n');

            if (!string.IsNullOrEmpty(result.Warning))
            {
                sb.Append("> ").Append(result.Warning).Append('\n');
            }

            sb.Append(BlockOpen).Append('\n');

            var content = result.Content;
            if (content.Length > ContextIngestLimits.SingleResultMaxChars)
            {
                content = content.Substring(0, ContextIngestLimits.SingleResultMaxChars) + Ellipsis;
            }

            sb.Append(content);
            if (!content.EndsWith("\n")) sb.Append('\n');

            sb.Append(BlockClose).Append('\n');

            return sb.ToString();
        }

        /// <summary>
        /// 截断字符串到指定最大长度并添加省略号。
        /// </summary>
        public static string TruncateValue(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
            if (value.Length <= maxLength) return value;
            return value.Substring(0, maxLength) + "...";
        }
    }
}
