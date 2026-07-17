using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Components
{
    #region Block Data Models
    
    /// <summary>
    /// Markdown 内容块类型枚举。
    /// </summary>
    internal enum ContentBlockType
    {
        Paragraph,
        Heading,
        HorizontalRule,
        CodeBlock,
        Table,
        List
    }
    
    /// <summary>
    /// Markdown 内容块基类。
    /// </summary>
    internal abstract class ContentBlock
    {
        public abstract ContentBlockType Type { get; }
    }
    
    /// <summary>
    /// 段落块（普通文本）。
    /// </summary>
    internal class ParagraphBlock : ContentBlock
    {
        public override ContentBlockType Type => ContentBlockType.Paragraph;
        public string Text { get; set; }
    }
    
    /// <summary>
    /// 标题块。
    /// </summary>
    internal class HeadingBlock : ContentBlock
    {
        public override ContentBlockType Type => ContentBlockType.Heading;
        public int Level { get; set; } // 1-6
        public string Text { get; set; }
    }
    
    /// <summary>
    /// 水平分隔线块。
    /// </summary>
    internal class HorizontalRuleBlock : ContentBlock
    {
        public override ContentBlockType Type => ContentBlockType.HorizontalRule;
    }
    
    /// <summary>
    /// 代码块。
    /// </summary>
    internal class CodeBlock : ContentBlock
    {
        public override ContentBlockType Type => ContentBlockType.CodeBlock;
        public string Language { get; set; }
        public List<string> Lines { get; set; } = new List<string>();
    }
    
    /// <summary>
    /// 表格块（存储结构化数据，由 Flex 网格渲染）。
    /// </summary>
    internal class TableBlock : ContentBlock
    {
        public override ContentBlockType Type => ContentBlockType.Table;
        /// <summary>表头单元格</summary>
        public string[] Headers { get; set; }
        /// <summary>数据行（每行为单元格数组）</summary>
        public List<string[]> Rows { get; set; } = new List<string[]>();
    }
    
    /// <summary>
    /// 列表块（无序/有序/引用）。
    /// </summary>
    internal class ListBlock : ContentBlock
    {
        public override ContentBlockType Type => ContentBlockType.List;
        public List<string> Items { get; set; } = new List<string>();
    }
    
    #endregion

    /// <summary>
    /// 内容过滤工具，用于移除 LLM 返回内容中的技术标签。
    /// </summary>
    internal static class ContentFilter
    {
        /// <summary>匹配完整的 tool_call 标签及内容</summary>
        private static readonly Regex ToolCallRegex = new(@"<tool_call>[\s\S]*?</tool_call>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>匹配完整的 tool_result 标签及内容</summary>
        private static readonly Regex ToolResultRegex = new(@"<tool_result>[\s\S]*?</tool_result>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>匹配不完整的开始标签（流式输出中可能出现）</summary>
        private static readonly Regex IncompleteTagRegex = new(@"<(?:tool_call|tool_result)>[\s\S]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>匹配连续3个及以上换行符（含可能的回车符），压缩为两个换行</summary>
        private static readonly Regex ExcessiveNewlinesRegex = new(@"(\s*\n){3,}", RegexOptions.Compiled);

        /// <summary>
        /// 过滤完整的 tool_call 和 tool_result 标签及其内容。
        /// 用于最终化消息时的完整过滤。
        /// </summary>
        /// <param name="content">原始内容</param>
        /// <returns>过滤后的内容（已废弃 — 使用 FilterCompletedToBlocks）</returns>
        [Obsolete("Use FilterCompletedToBlocks for block-based rendering")]
        public static string FilterCompleted(string content)
        {
            if (string.IsNullOrEmpty(content)) return content;

            var filtered = ToolCallRegex.Replace(content, "");
            filtered = ToolResultRegex.Replace(filtered, "");
            // 压缩过滤后产生的连续空行（3个以上换行 → 2个换行）
            filtered = ExcessiveNewlinesRegex.Replace(filtered, "\n\n");
            // 替换 SDF 字体不支持的 emoji 字符
            filtered = SanitizeUnsupportedEmoji(filtered);
            // 轻量级 Markdown 格式化（标题、表格、粗体、列表等）
            filtered = FormatMarkdown(filtered);
            return filtered.Trim();
        }

        /// <summary>
        /// 过滤完整的 tool_call 和 tool_result 标签及其内容，并解析为 block 列表。
        /// 用于最终化消息时的完整过滤和 block 渲染。
        /// </summary>
        /// <param name="content">原始内容</param>
        /// <returns>过滤并解析后的 content block 列表</returns>
        public static List<ContentBlock> FilterCompletedToBlocks(string content)
        {
            if (string.IsNullOrEmpty(content)) return new List<ContentBlock>();

            var filtered = ToolCallRegex.Replace(content, "");
            filtered = ToolResultRegex.Replace(filtered, "");
            // 压缩过滤后产生的连续空行（3个以上换行 → 2个换行）
            filtered = ExcessiveNewlinesRegex.Replace(filtered, "\n\n");
            // 替换 SDF 字体不支持的 emoji 字符
            filtered = SanitizeUnsupportedEmoji(filtered);
            // 解析为 block 列表
            var blocks = ParseMarkdownToBlocks(filtered.Trim());
            // 过滤首行无意义的水平分隔线（LLM 有时在响应开头输出 ---）
            if (blocks.Count > 0 && blocks[0].Type == ContentBlockType.HorizontalRule)
            {
                blocks.RemoveAt(0);
            }
            return blocks;
        }

        /// <summary>
        /// 流式过滤：移除已完成的标签对，并截断不完整的标签开头。
        /// 用于流式输出过程中的实时过滤。
        /// </summary>
        /// <param name="content">当前累积的内容</param>
        /// <returns>过滤后的显示内容</returns>
        public static string FilterStreaming(string content)
        {
            if (string.IsNullOrEmpty(content)) return content;

            // 先移除已完成的标签对
            var filtered = ToolCallRegex.Replace(content, "");
            filtered = ToolResultRegex.Replace(filtered, "");

            // 截断不完整的标签开头（避免显示半截标签）
            filtered = IncompleteTagRegex.Replace(filtered, "");

            // 压缩过滤后产生的连续空行（3个以上换行 → 2个换行）
            filtered = ExcessiveNewlinesRegex.Replace(filtered, "\n\n");
            // 移除 SDF 字体不支持的 emoji（流式阶段也需要，否则渲染时仍会触发字体警告）
            filtered = SanitizeUnsupportedEmoji(filtered);
            // 流式阶段跳过 Markdown 格式化，以减少 CPU 开销
            // filtered = FormatMarkdown(filtered);
            // 去除开头的空行，保留末尾的自然截断
            filtered = filtered.TrimStart('\n', '\r');

            return filtered.TrimEnd();
        }

        /// <summary>
        /// 移除 SDF 字体不支持的 emoji 及相关字符。
        /// <para>
        /// Unity 默认 UI Toolkit 字体（Inter SDF）不支持以下字符：
        /// <list type="bullet">
        ///   <item>Supplementary Multilingual Plane 中的图形字符（通过 surrogate pairs 编码）</item>
        ///   <item>BMP 中的 Miscellaneous Symbols（U+2600-U+26FF: ☀⚡⚠♻ 等）</item>
        ///   <item>BMP 中的 Dingbats（U+2700-U+27BF: ✅❌✂✈ 等）</item>
        ///   <item>变体选择符（U+FE0E/FE0F）和零宽连接符（U+200D）</item>
        /// </list>
        /// 渲染时触发 "Font ... does not contain ... Unicode (Hex)" 警告。
        /// 此方法将这些字符移除，保证输出文本不会触发字体回退警告。
        /// </para>
        /// </summary>
        /// <param name="text">可能包含不受支持图形字符的文本</param>
        /// <returns>移除不受支持字符后的字体安全文本</returns>
        internal static string SanitizeUnsupportedEmoji(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            bool needsSanitize = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsSurrogate(c) || IsUnsupportedBmpEmoji(c))
                {
                    needsSanitize = true;
                    break;
                }
            }

            if (!needsSanitize) return text;

            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                // 跳过 surrogate pairs（BMP 外 emoji）
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    {
                        i++;
                    }
                    continue;
                }

                if (char.IsLowSurrogate(c))
                {
                    continue;
                }

                // 跳过 BMP 内 SDF 字体不支持的 emoji / 符号
                if (IsUnsupportedBmpEmoji(c))
                {
                    continue;
                }

                sb.Append(c);
            }

            return sb.ToString();
        }

        /// <summary>
        /// 判断 BMP 字符是否属于 SDF 字体不支持的 emoji/符号范围。
        /// </summary>
        private static bool IsUnsupportedBmpEmoji(char c)
        {
            // 变体选择符 (Variation Selectors) 和零宽连接符 (ZWJ)
            if (c == (char)0xFE0E || c == (char)0xFE0F || c == (char)0x200D)
                return true;

            // Miscellaneous Symbols: U+2600-U+26FF (☀⚡⚠♻☎☑ 等)
            if (c >= (char)0x2600 && c <= (char)0x26FF)
                return true;

            // Dingbats: U+2700-U+27BF (✅❌✂✈✉✓✔✖ 等)
            if (c >= (char)0x2700 && c <= (char)0x27BF)
                return true;

            return false;
        }

        #region Markdown 格式化 (旧版 — 字符串输出)

        /// <summary>
        /// 轻量级 Markdown → 可读纯文本格式化（已废弃 — 使用 ParseMarkdownToBlocks）。
        /// <para>
        /// 不使用任何 Rich Text 标签（无 &lt;b&gt;、&lt;i&gt;、&lt;size&gt;），
        /// 仅通过纯文本符号和排版优化可读性：
        /// <list type="bullet">
        ///   <item>标题 <c># Title</c> → <c>═══ Title ═══</c></item>
        ///   <item>标题 <c>## Title</c> → <c>── Title ──</c></item>
        ///   <item>标题 <c>### Title</c> → <c>【Title】</c></item>
        ///   <item>标题 <c>#### Title</c> → <c>▸ Title</c></item>
        ///   <item>表格 → 对齐的纯文本表格（去掉分隔线行）</item>
        ///   <item>粗体 <c>**text**</c> → 直接去掉标记符号</item>
        ///   <item>斜体 <c>*text*</c> → 直接去掉标记符号</item>
        ///   <item>无序列表 <c>- item</c> → <c>  · item</c></item>
        ///   <item>有序列表 <c>1. item</c> → <c>  1) item</c></item>
        ///   <item>代码块保持原样（加缩进标记）</item>
        ///   <item>水平线 <c>---</c> → <c>────────</c></item>
        /// </list>
        /// </para>
        /// </summary>
        /// <param name="content">过滤后的内容</param>
        /// <returns>格式化后的可读文本</returns>
        [Obsolete("Use ParseMarkdownToBlocks for block-based rendering")]
        internal static string FormatMarkdown(string content)
        {
            if (string.IsNullOrEmpty(content)) return content;

            var lines = content.Split('\n');
            var result = new List<string>(lines.Length);
            var inCodeBlock = false;
            var tableBuffer = new List<string>();

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();

                // 代码块：保持原样不处理
                if (trimmed.StartsWith("```"))
                {
                    // 先刷新表格缓冲区
                    if (tableBuffer.Count > 0)
                    {
                        result.AddRange(FormatTable(tableBuffer));
                        tableBuffer.Clear();
                    }

                    inCodeBlock = !inCodeBlock;
                    if (inCodeBlock)
                    {
                        // 代码块开始：添加语言标记行
                        var lang = trimmed.Length > 3 ? trimmed.Substring(3).Trim() : "";
                        result.Add(string.IsNullOrEmpty(lang) ? "  ──── code ────" : $"  ──── {lang} ────");
                    }
                    else
                    {
                        // 代码块结束
                        result.Add("  ────────────");
                    }
                    continue;
                }

                if (inCodeBlock)
                {
                    // 代码块内容：加缩进
                    result.Add("    " + line);
                    continue;
                }

                // 表格行检测：收集连续的表格行
                if (trimmed.StartsWith("|") && trimmed.EndsWith("|"))
                {
                    tableBuffer.Add(trimmed);
                    continue;
                }

                // 如果之前有表格缓冲，先刷新
                if (tableBuffer.Count > 0)
                {
                    result.AddRange(FormatTable(tableBuffer));
                    tableBuffer.Clear();
                }

                // 标题：### Title → 【Title】
                if (trimmed.StartsWith("#"))
                {
                    result.Add(FormatHeading(trimmed));
                    continue;
                }

                // 水平线：--- 或 *** 或 ___ → ────────
                if (IsHorizontalRule(trimmed))
                {
                    result.Add("────────────────────");
                    continue;
                }

                // 无序列表：- item 或 * item → · item
                if ((trimmed.StartsWith("- ") || trimmed.StartsWith("* ")) && trimmed.Length > 2)
                {
                    var indent = line.Length - line.TrimStart().Length;
                    var prefix = new string(' ', indent);
                    var itemText = FormatInlineStyles(trimmed.Substring(2));
                    result.Add($"{prefix}  · {itemText}");
                    continue;
                }

                // 有序列表：1. item → 1) item
                var orderedMatch = Regex.Match(trimmed, @"^(\d+)\.\s+(.+)$");
                if (orderedMatch.Success)
                {
                    var indent = line.Length - line.TrimStart().Length;
                    var prefix = new string(' ', indent);
                    var num = orderedMatch.Groups[1].Value;
                    var itemText = FormatInlineStyles(orderedMatch.Groups[2].Value);
                    result.Add($"{prefix}  {num}) {itemText}");
                    continue;
                }

                // 引用块：> text → │ text
                if (trimmed.StartsWith("> "))
                {
                    var quoteText = FormatInlineStyles(trimmed.Substring(2));
                    result.Add($"  │ {quoteText}");
                    continue;
                }
                if (trimmed == ">")
                {
                    result.Add("  │");
                    continue;
                }

                // 普通行：处理内联样式
                result.Add(FormatInlineStyles(line));
            }

            // 刷新末尾的表格缓冲
            if (tableBuffer.Count > 0)
            {
                result.AddRange(FormatTable(tableBuffer));
            }

            return string.Join("\n", result);
        }

        /// <summary>
        /// 格式化 Markdown 标题行（纯文本，无 Rich Text 标签）。
        /// </summary>
        private static string FormatHeading(string line)
        {
            // 计算标题级别
            int level = 0;
            while (level < line.Length && line[level] == '#') level++;
            var title = line.Substring(level).Trim();
            title = FormatInlineStyles(title);

            return level switch
            {
                1 => $"═══ {title} ═══",
                2 => $"── {title} ──",
                3 => $"【{title}】",
                4 => $"▸ {title}",
                _ => $"▸ {title}"
            };
        }

        /// <summary>
        /// 格式化内联样式（纯文本，无 Rich Text 标签）。
        /// 粗体和斜体标记符号直接去除，内联代码用方括号标记，链接展开为文本。
        /// </summary>
        private static string FormatInlineStyles(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // 粗体 **text** → text（直接去掉标记符号）
            text = Regex.Replace(text, @"\*\*(.+?)\*\*", "$1");

            // 斜体 *text*（不匹配 **）→ text（直接去掉标记符号）
            text = Regex.Replace(text, @"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)", "$1");

            // 内联代码 `code` → [code]（纯文本中用方括号标记）
            text = Regex.Replace(text, @"`([^`]+)`", "[$1]");

            // 链接 [text](url) → text (url)
            text = Regex.Replace(text, @"\[([^\]]+)\]\(([^)]+)\)", "$1 ($2)");

            return text;
        }

        /// <summary>
        /// 格式化 Markdown 表格为对齐的纯文本。
        /// 去掉分隔线行（|---|---|），保留数据行并对齐列宽。
        /// </summary>
        private static List<string> FormatTable(List<string> tableLines)
        {
            var result = new List<string>();
            var dataRows = new List<string[]>();
            var maxCols = 0;

            // 解析表格行，跳过分隔线
            foreach (var line in tableLines)
            {
                // 跳过分隔线行 |---|---|
                if (Regex.IsMatch(line, @"^\s*\|[\s\-:]+\|[\s\-:|]*$"))
                    continue;

                // 解析数据行
                var cells = ParseTableRow(line);
                if (cells.Length > 0)
                {
                    dataRows.Add(cells);
                    if (cells.Length > maxCols) maxCols = cells.Length;
                }
            }

            if (dataRows.Count == 0) return result;

            // 计算每列最大宽度
            var colWidths = new int[maxCols];
            foreach (var row in dataRows)
            {
                for (int c = 0; c < row.Length && c < maxCols; c++)
                {
                    var cellLen = GetDisplayLength(row[c]);
                    if (cellLen > colWidths[c]) colWidths[c] = cellLen;
                }
            }

            // 限制列宽，避免过宽
            for (int c = 0; c < maxCols; c++)
            {
                if (colWidths[c] > 40) colWidths[c] = 40;
                if (colWidths[c] < 2) colWidths[c] = 2;
            }

            // 输出表头分隔线
            var headerSep = new StringBuilder("  ");
            for (int c = 0; c < maxCols; c++)
            {
                if (c > 0) headerSep.Append("──┬──");
                headerSep.Append(new string('─', colWidths[c]));
            }

            // 输出数据行
            for (int r = 0; r < dataRows.Count; r++)
            {
                var row = dataRows[r];
                var sb = new StringBuilder("  ");
                for (int c = 0; c < maxCols; c++)
                {
                    if (c > 0) sb.Append("  │  ");
                    var cell = c < row.Length ? row[c] : "";
                    var displayLen = GetDisplayLength(cell);
                    sb.Append(cell);
                    // 右侧填充空格对齐
                    if (displayLen < colWidths[c])
                        sb.Append(new string(' ', colWidths[c] - displayLen));
                }
                result.Add(FormatInlineStyles(sb.ToString()));

                // 在表头行后添加分隔线
                if (r == 0 && dataRows.Count > 1)
                {
                    result.Add(headerSep.ToString());
                }
            }

            return result;
        }

        /// <summary>
        /// 解析表格行，提取单元格内容。
        /// 支持反引号内的 | 不作为分隔符（避免内联代码中的管道符号错误拆分列）。
        /// </summary>
        private static string[] ParseTableRow(string line)
        {
            // 去掉首尾的 |
            var trimmed = line.Trim();
            if (trimmed.StartsWith("|")) trimmed = trimmed.Substring(1);
            if (trimmed.EndsWith("|")) trimmed = trimmed.Substring(0, trimmed.Length - 1);

            // 状态机分割：反引号内的 | 不作为列分隔符
            var cells = new List<string>();
            var current = new StringBuilder();
            bool inBacktick = false;

            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                if (c == '`')
                {
                    inBacktick = !inBacktick;
                    current.Append(c);
                }
                else if (c == '|' && !inBacktick)
                {
                    cells.Add(current.ToString().Trim());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }
            cells.Add(current.ToString().Trim());

            return cells.ToArray();
        }

        /// <summary>
        /// 获取字符串的显示宽度（考虑中文字符占2个宽度）。
        /// </summary>
        private static int GetDisplayLength(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int len = 0;
            foreach (var ch in text)
            {
                // CJK 字符占2个宽度
                if (ch >= 0x4E00 && ch <= 0x9FFF ||
                    ch >= 0x3400 && ch <= 0x4DBF ||
                    ch >= 0xF900 && ch <= 0xFAFF ||
                    ch >= 0xFF00 && ch <= 0xFF60)
                {
                    len += 2;
                }
                else
                {
                    len += 1;
                }
            }
            return len;
        }

        /// <summary>
        /// 判断是否为水平线（---、***、___）。
        /// </summary>
        private static bool IsHorizontalRule(string trimmed)
        {
            if (trimmed.Length < 3) return false;
            // 全部由 - 或 * 或 _ 和空格组成
            var cleaned = trimmed.Replace(" ", "");
            if (cleaned.Length < 3) return false;
            return (AllSameChar(cleaned, '-') || AllSameChar(cleaned, '*') || AllSameChar(cleaned, '_'));
        }

        /// <summary>
        /// 判断字符串是否全部由同一字符组成。
        /// </summary>
        private static bool AllSameChar(string s, char c)
        {
            foreach (var ch in s)
            {
                if (ch != c) return false;
            }
            return true;
        }

        #endregion

        #region Markdown 解析为 Block（新版）

        /// <summary>
        /// 解析 Markdown 为 ContentBlock 列表。
        /// 复用 FormatMarkdown 的逻辑，但输出为结构化 block 而非纯文本。
        /// </summary>
        internal static List<ContentBlock> ParseMarkdownToBlocks(string content)
        {
            var blocks = new List<ContentBlock>();
            if (string.IsNullOrEmpty(content)) return blocks;

            var lines = content.Split('\n');
            var inCodeBlock = false;
            CodeBlock currentCodeBlock = null;
            var tableBuffer = new List<string>();
            var paragraphBuffer = new List<string>();
            var listBuffer = new List<string>();

            void FlushParagraph()
            {
                if (paragraphBuffer.Count > 0)
                {
                    var text = string.Join("\n", paragraphBuffer).Trim();
                    if (!string.IsNullOrEmpty(text))
                    {
                        blocks.Add(new ParagraphBlock { Text = FormatInlineStyles(text) });
                    }
                    paragraphBuffer.Clear();
                }
            }

            void FlushList()
            {
                if (listBuffer.Count > 0)
                {
                    blocks.Add(new ListBlock { Items = new List<string>(listBuffer) });
                    listBuffer.Clear();
                }
            }

            void FlushTable()
            {
                if (tableBuffer.Count > 0)
                {
                    // 解析为结构化数据（Headers + Rows），供 Flex 网格渲染
                    var tableBlock = new TableBlock();
                    var dataRows = new List<string[]>();
                    
                    foreach (var rawLine in tableBuffer)
                    {
                        // 跳过分隔行（|---|---|---| 模式）
                        var stripped = rawLine.Replace(" ", "").Replace("-", "").Replace("|", "").Replace(":", "");
                        if (string.IsNullOrEmpty(stripped))
                            continue;
                        var parsed = ParseTableRow(rawLine);
                        for (int ci = 0; ci < parsed.Length; ci++)
                            parsed[ci] = FormatInlineStyles(parsed[ci]);
                        dataRows.Add(parsed);
                    }
                    
                    if (dataRows.Count > 0)
                    {
                        tableBlock.Headers = dataRows[0];
                        for (int r = 1; r < dataRows.Count; r++)
                        {
                            tableBlock.Rows.Add(dataRows[r]);
                        }
                        blocks.Add(tableBlock);
                    }
                    tableBuffer.Clear();
                }
            }

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();

                // 代码块：```
                if (trimmed.StartsWith("```"))
                {
                    FlushParagraph();
                    FlushList();
                    FlushTable();

                    inCodeBlock = !inCodeBlock;
                    if (inCodeBlock)
                    {
                        // 代码块开始
                        var lang = trimmed.Length > 3 ? trimmed.Substring(3).Trim() : "";
                        currentCodeBlock = new CodeBlock { Language = lang };
                    }
                    else
                    {
                        // 代码块结束
                        if (currentCodeBlock != null)
                        {
                            blocks.Add(currentCodeBlock);
                            currentCodeBlock = null;
                        }
                    }
                    continue;
                }

                if (inCodeBlock)
                {
                    // 代码块内容
                    currentCodeBlock?.Lines.Add(line);
                    continue;
                }

                // 表格行：| ... |
                if (trimmed.StartsWith("|") && trimmed.EndsWith("|"))
                {
                    FlushParagraph();
                    FlushList();
                    tableBuffer.Add(trimmed);
                    continue;
                }

                // 如果之前有表格缓冲，先刷新
                if (tableBuffer.Count > 0)
                {
                    FlushTable();
                }

                // 标题：# ...
                if (trimmed.StartsWith("#"))
                {
                    FlushParagraph();
                    FlushList();
                    int level = 0;
                    while (level < trimmed.Length && trimmed[level] == '#') level++;
                    var title = trimmed.Substring(level).Trim();
                    blocks.Add(new HeadingBlock
                    {
                        Level = level,
                        Text = FormatInlineStyles(title)
                    });
                    continue;
                }

                // 水平线：--- / *** / ___
                if (IsHorizontalRule(trimmed))
                {
                    FlushParagraph();
                    FlushList();
                    blocks.Add(new HorizontalRuleBlock());
                    continue;
                }

                // 列表项：- item 或 * item 或 1. item 或 > quote
                bool isListItem = false;
                string listItemText = null;

                if ((trimmed.StartsWith("- ") || trimmed.StartsWith("* ")) && trimmed.Length > 2)
                {
                    var indent = line.Length - line.TrimStart().Length;
                    var prefix = new string(' ', indent);
                    var itemText = FormatInlineStyles(trimmed.Substring(2));
                    listItemText = $"{prefix}  · {itemText}";
                    isListItem = true;
                }
                else
                {
                    var orderedMatch = Regex.Match(trimmed, @"^(\d+)\.\s+(.+)$");
                    if (orderedMatch.Success)
                    {
                        var indent = line.Length - line.TrimStart().Length;
                        var prefix = new string(' ', indent);
                        var num = orderedMatch.Groups[1].Value;
                        var itemText = FormatInlineStyles(orderedMatch.Groups[2].Value);
                        listItemText = $"{prefix}  {num}) {itemText}";
                        isListItem = true;
                    }
                    else if (trimmed.StartsWith("> "))
                    {
                        var quoteText = FormatInlineStyles(trimmed.Substring(2));
                        listItemText = $"  │ {quoteText}";
                        isListItem = true;
                    }
                    else if (trimmed == ">")
                    {
                        listItemText = "  │";
                        isListItem = true;
                    }
                }

                if (isListItem)
                {
                    FlushParagraph();
                    listBuffer.Add(listItemText);
                    continue;
                }

                // 非列表项且之前有列表缓冲，先刷新
                if (listBuffer.Count > 0)
                {
                    FlushList();
                }

                // 空行 — 段落分隔
                if (string.IsNullOrWhiteSpace(line))
                {
                    FlushParagraph();
                    continue;
                }

                // 普通行 — 累积到段落
                paragraphBuffer.Add(line);
            }

            // 刷新末尾的缓冲区
            FlushParagraph();
            FlushList();
            FlushTable();

            return blocks;
        }

        #endregion
    }

    /// <summary>
    /// 流式文本显示元素。
    /// <para>
    /// 用于在助手消息气泡中逐 token 显示 LLM 流式输出的文本。
    /// 支持追加文本、设置最终文本和清空操作，并提供可选的闪烁光标效果。
    /// </para>
    /// <para>
    /// 流式阶段使用单个 Label 显示原始文本（性能优化）。
    /// Finalize 阶段切换到 block 布局，支持分隔线、代码块背景等响应式元素。
    /// </para>
    /// </summary>
    public class StreamingTextElement : VisualElement
    {
        #region 私有字段

        /// <summary>流式阶段的文本容器</summary>
        private readonly VisualElement _streamingContainer;

        /// <summary>显示文本内容的 Label（流式阶段）</summary>
        private readonly Label _textLabel;

        /// <summary>闪烁光标元素</summary>
        private readonly VisualElement _cursor;

        /// <summary>Finalize 阶段的 block 容器</summary>
        private VisualElement _blockContainer;

        /// <summary>当前累积的文本内容</summary>
        private string _currentText = "";

        /// <summary>用于累积文本的 StringBuilder</summary>
        private StringBuilder _currentTextBuilder;

        /// <summary>光标闪烁动画调度器</summary>
        private IVisualElementScheduledItem _cursorBlink;

        /// <summary>光标当前是否可见</summary>
        private bool _cursorVisible;

        /// <summary>是否已切换到 block 渲染模式</summary>
        private bool _isBlockMode;

        // --- v1.6.5 性能优化：token 缓冲 + 帧节流 ---
        // 旧实现：每 token 跑 FilterStreaming(全量文本) + Label.text 赋值 → 高频 UI relayout 卡死主线程
        // 新实现：token 累积到 _pendingBuffer，每帧只 flush 一次
        private StringBuilder _pendingBuffer = new();
        private bool _flushScheduled;
        private const int FlushIntervalMs = 16; // ~1 帧

        // v1.6.5: 流式阶段文本窗口 — 只显示尾部 N 字符，避免超长文本 Label.text 触发 O(n) layout
        // 最终化时 SetFinalText 会渲染全部内容（block 模式），流式阶段只看尾部足够
        private const int StreamingTextWindow = 4000; // ~4000 字符，足够看到当前生成进度

        #endregion

        #region 构造函数

        /// <summary>
        /// 创建流式文本显示元素。
        /// 包含一个流式容器（Label + 光标）和一个 block 容器（finalize 后显示）。
        /// </summary>
        public StreamingTextElement()
        {
            // 主容器样式
            style.flexDirection = FlexDirection.Column;
            style.flexGrow = 0;
            style.flexShrink = 0;

            // 流式阶段容器
            _streamingContainer = new VisualElement();
            _streamingContainer.style.flexDirection = FlexDirection.Row;
            _streamingContainer.style.flexWrap = Wrap.Wrap;
            _streamingContainer.style.alignItems = Align.FlexEnd;

            // 文本标签（流式阶段）
            _textLabel = new Label
            {
                name = "streaming-text-label",
                text = ""
            };
            _textLabel.style.whiteSpace = WhiteSpace.Normal;
            _textLabel.style.fontSize = 13;
            _textLabel.style.color = new StyleColor(new UnityEngine.Color(0.83f, 0.83f, 0.83f));
            _textLabel.style.flexShrink = 1;
            _textLabel.style.flexGrow = 1;
            // 启用文本选择，允许用户选中和复制文本（Unity 2022.2+）
            _textLabel.selection.isSelectable = true;
            _streamingContainer.Add(_textLabel);

            // 闪烁光标
            _cursor = new VisualElement();
            _cursor.AddToClassList("streaming-cursor");
            _cursor.style.display = DisplayStyle.None;
            _streamingContainer.Add(_cursor);

            Add(_streamingContainer);
        }

        #endregion

        #region 公开方法

        /// <summary>
        /// 追加流式文本。
        /// 每次收到一个 token 时调用此方法，文本会累积显示。
        /// v1.6.5: token 先累积到 _pendingBuffer，由 flush 定时器每帧合并写一次 Label。
        /// </summary>
        /// <param name="text">要追加的文本片段</param>
        public void AppendText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            // 累积到 builder（用于最终化）
            if (_currentTextBuilder == null)
                _currentTextBuilder = new StringBuilder();
            _currentTextBuilder.Append(text);

            // 累积到 pending buffer，稍后 flush
            _pendingBuffer.Append(text);
            ShowCursor();
            ScheduleFlush();
        }

        /// <summary>
        /// 调度一次延迟 flush。如果已有 flush 排队则不重复调度。
        /// </summary>
        private void ScheduleFlush()
        {
            if (_flushScheduled) return;
            _flushScheduled = true;
            schedule.Execute(FlushPending).StartingIn(FlushIntervalMs);
        }

        /// <summary>
        /// 将 _pendingBuffer 中的累积文本增量渲染为 block（每 16ms 最多一次）。
        /// v1.7.x 方案C：流式阶段用 block 渲染（与最终化统一路径），消除视觉跳变。
        /// 仍只渲染尾部 StreamingTextWindow 字符，避免超长文本 O(n) layout。
        /// </summary>
        private void FlushPending()
        {
            _flushScheduled = false;
            if (_pendingBuffer.Length == 0) return;

            // 全量文本（用于最终化）
            var fullText = _currentTextBuilder?.ToString() ?? "";

            // v1.7.x 方案C：流式阶段也走 block 渲染，与最终化统一渲染路径 —— 根除
            // "流式纯文本 → 最终富格式"的视觉跳变。代码块/表格在流式阶段就是深色框/网格。
            // 为控制 DOM 规模，仍只渲染尾部窗口文本（超长时截断头部）；最终化 SetFinalText 渲染全量。
            string displayText = fullText;
            if (fullText.Length > StreamingTextWindow)
            {
                displayText = "…\n" + fullText.Substring(fullText.Length - StreamingTextWindow);
            }

            RenderTextAsBlocks(displayText, isStreaming: true);

            // 清空 pending buffer（已合并渲染）
            _pendingBuffer.Clear();
        }

        /// <summary>
        /// 将文本解析为 block 并渲染进 block 容器。流式与最终化共用同一渲染路径。
        /// <para>
        /// isStreaming=true 时会补齐末尾未闭合的代码块（流式中 ``` 只开未闭），
        /// 使"正在输入的代码块"也能以深色框形态显示，而不是等闭合才突然出现。
        /// </para>
        /// </summary>
        private void RenderTextAsBlocks(string text, bool isStreaming)
        {
            EnsureBlockContainer();
            _blockContainer.Clear();

            var source = text ?? "";
            if (isStreaming)
            {
                source = CloseDanglingCodeFence(source);
            }

            var blocks = ContentFilter.FilterCompletedToBlocks(source);
            foreach (var block in blocks)
            {
                var element = CreateBlockElement(block);
                if (element != null)
                {
                    _blockContainer.Add(element);
                }
            }

            // 流式阶段：把光标追加到 block 容器末尾，保留"正在输出"的视觉反馈。
            // 上面 _blockContainer.Clear() 已把光标移出（parent 变 null），这里重新加回末尾。
            // （最终化 isStreaming=false 时不加，且 SetFinalText 会 HideCursor。）
            if (isStreaming && _cursor != null)
            {
                _cursor.RemoveFromHierarchy();
                _blockContainer.Add(_cursor);
            }
        }

        /// <summary>
        /// 若文本中代码围栏 ``` 的数量为奇数（末尾有未闭合的代码块），
        /// 在末尾补一个 ``` 使其闭合，便于流式阶段渲染"正在输入的代码块"。
        /// </summary>
        private static string CloseDanglingCodeFence(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            int fenceCount = 0;
            int idx = 0;
            while ((idx = text.IndexOf("```", idx, System.StringComparison.Ordinal)) >= 0)
            {
                fenceCount++;
                idx += 3;
            }

            // 奇数个围栏 = 末尾有未闭合代码块，补一个闭合围栏
            if (fenceCount % 2 == 1)
            {
                return text + "\n```";
            }
            return text;
        }

        /// <summary>
        /// 确保 block 容器已创建并处于显示状态，隐藏旧的流式纯文本容器。
        /// </summary>
        private void EnsureBlockContainer()
        {
            if (_blockContainer == null)
            {
                _blockContainer = new VisualElement();
                _blockContainer.name = "block-container";
                _blockContainer.style.flexDirection = FlexDirection.Column;
                _blockContainer.style.flexGrow = 0;
                _blockContainer.style.flexShrink = 0;
                Add(_blockContainer);
            }

            // 隐藏旧的流式纯文本 Label 容器（方案C 下不再使用它显示正文，仅保留光标）
            if (_streamingContainer.style.display != DisplayStyle.None)
            {
                _streamingContainer.style.display = DisplayStyle.None;
            }

            _isBlockMode = true;
        }

        /// <summary>
        /// 设置最终完整文本。
        /// 流式输出完成后调用，用全量文本做最终 block 渲染并隐藏光标。
        /// v1.7.x 方案C：流式阶段已是 block 渲染，此处只是用全量文本重渲一次 + 收光标，
        /// 不再有"纯文本→block"的模式切换，因此不产生视觉跳变。
        /// </summary>
        /// <param name="text">完整的最终文本</param>
        public void SetFinalText(string text)
        {
            // 切换前 flush 残留 token 状态
            _flushScheduled = false;
            _pendingBuffer.Clear();

            _currentText = text ?? "";
            HideCursor();

            // 用全量文本做最终渲染（非流式：不补围栏——最终文本理应已闭合）
            RenderTextAsBlocks(_currentText, isStreaming: false);
        }

        /// <summary>
        /// 清空所有文本内容并隐藏光标。
        /// </summary>
        /// <remarks>
        /// 注意：此方法清空的是文本内容（_currentText / _textLabel.text），
        /// 与 <see cref="VisualElement.Clear"/>（清空子元素集合）语义不同，
        /// 因此显式重命名为 ClearText 以避免方法隐藏（CS0108）。
        /// </remarks>
        public void ClearText()
        {
            _currentText = "";
            _textLabel.text = "";
            _pendingBuffer.Clear();
            _flushScheduled = false;
            _blockContainer?.Clear();
            HideCursor();

            // 恢复流式模式
            if (_isBlockMode)
            {
                _streamingContainer.style.display = DisplayStyle.Flex;
                if (_blockContainer != null)
                {
                    _blockContainer.RemoveFromHierarchy();
                    _blockContainer = null;
                }
                _isBlockMode = false;
            }
        }

        /// <summary>
        /// 获取当前累积的文本内容。
        /// </summary>
        public string CurrentText => _currentText;

        #endregion

        #region 光标动画

        /// <summary>
        /// 显示闪烁光标并启动动画。
        /// </summary>
        private void ShowCursor()
        {
            _cursor.AddToClassList("streaming-cursor--visible");
            _cursor.style.display = DisplayStyle.Flex;
            _cursorVisible = true;

            if (_cursorBlink == null)
            {
                _cursorBlink = schedule.Execute(() =>
                {
                    _cursorVisible = !_cursorVisible;
                    _cursor.style.opacity = _cursorVisible ? 1f : 0f;
                }).Every(530);
            }
        }

        /// <summary>
        /// 隐藏光标并停止动画。
        /// </summary>
        private void HideCursor()
        {
            _cursor.RemoveFromClassList("streaming-cursor--visible");
            _cursor.style.display = DisplayStyle.None;

            if (_cursorBlink != null)
            {
                _cursorBlink.Pause();
                _cursorBlink = null;
            }
        }

        #endregion

        #region Block 渲染

        /// <summary>
        /// 根据 ContentBlock 创建对应的 VisualElement。
        /// </summary>
        private VisualElement CreateBlockElement(ContentBlock block)
        {
            switch (block.Type)
            {
                case ContentBlockType.Paragraph:
                    return CreateParagraph((ParagraphBlock)block);

                case ContentBlockType.Heading:
                    return CreateHeading((HeadingBlock)block);

                case ContentBlockType.HorizontalRule:
                    return CreateHorizontalRule();

                case ContentBlockType.CodeBlock:
                    return CreateCodeBlock((CodeBlock)block);

                case ContentBlockType.Table:
                    return CreateTable((TableBlock)block);

                case ContentBlockType.List:
                    return CreateList((ListBlock)block);

                default:
                    return null;
            }
        }

        private VisualElement CreateParagraph(ParagraphBlock block)
        {
            var label = new Label(block.Text);
            label.AddToClassList("content-paragraph");
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.fontSize = 13;
            label.style.color = new StyleColor(new UnityEngine.Color(0.83f, 0.83f, 0.83f));
            label.style.marginTop = 6;
            label.style.marginBottom = 12;
            label.selection.isSelectable = true;
            return label;
        }

        private VisualElement CreateHeading(HeadingBlock block)
        {
            var label = new Label(block.Text);
            label.AddToClassList($"content-heading-{block.Level}");
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.color = new StyleColor(new UnityEngine.Color(0.9f, 0.85f, 0.7f));
            label.style.marginTop = block.Level == 1 ? 12 : 8;
            label.style.marginBottom = 6;
            label.selection.isSelectable = true;

            // 根据级别设置字体大小
            label.style.fontSize = block.Level switch
            {
                1 => 16,
                2 => 15,
                3 => 14,
                _ => 13
            };

            return label;
        }

        private VisualElement CreateHorizontalRule()
        {
            // 使用短横线文本 + 增大上下边距，避免换行问题且提升可读性
            var label = new Label("───");
            label.AddToClassList("content-horizontal-rule");
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.fontSize = 12;
            label.style.color = new StyleColor(new UnityEngine.Color(0.5f, 0.5f, 0.5f));
            label.style.marginTop = 12;
            label.style.marginBottom = 12;
            label.style.unityTextAlign = UnityEngine.TextAnchor.MiddleCenter;
            return label;
        }

        private VisualElement CreateCodeBlock(CodeBlock block)
        {
            var container = new VisualElement();
            container.AddToClassList("content-code-block");
            container.style.backgroundColor = new StyleColor(new UnityEngine.Color(0.15f, 0.15f, 0.15f));
            container.style.borderTopLeftRadius = 4;
            container.style.borderTopRightRadius = 4;
            container.style.borderBottomLeftRadius = 4;
            container.style.borderBottomRightRadius = 4;
            container.style.paddingTop = 8;
            container.style.paddingBottom = 8;
            container.style.paddingLeft = 10;
            container.style.paddingRight = 10;
            container.style.marginTop = 6;
            container.style.marginBottom = 6;

            // 语言标签（如果有）
            if (!string.IsNullOrEmpty(block.Language))
            {
                var langLabel = new Label(block.Language);
                langLabel.style.fontSize = 11;
                langLabel.style.color = new StyleColor(new UnityEngine.Color(0.6f, 0.6f, 0.6f));
                langLabel.style.marginBottom = 4;
                container.Add(langLabel);
            }

            // 代码内容 — 每行单独渲染，前导空格替换为非断裂空格保留缩进
            foreach (var line in block.Lines)
            {
                var displayLine = string.IsNullOrEmpty(line) ? " " : PreserveLeadingSpaces(line);
                var lineLabel = new Label(displayLine);
                lineLabel.style.whiteSpace = WhiteSpace.Normal;
                lineLabel.style.fontSize = 12;
                lineLabel.style.color = new StyleColor(new UnityEngine.Color(0.85f, 0.85f, 0.85f));
                lineLabel.selection.isSelectable = true;
                container.Add(lineLabel);
            }

            return container;
        }

        private VisualElement CreateTable(TableBlock block)
        {
            var container = new VisualElement();
            container.AddToClassList("content-table");
            container.style.marginTop = 6;
            container.style.marginBottom = 6;
            container.style.borderTopLeftRadius = 4;
            container.style.borderTopRightRadius = 4;
            container.style.borderBottomLeftRadius = 4;
            container.style.borderBottomRightRadius = 4;
            container.style.backgroundColor = new StyleColor(new UnityEngine.Color(0.15f, 0.15f, 0.15f, 0.3f));
            container.style.paddingTop = 2;
            container.style.paddingBottom = 2;

            int colCount = block.Headers?.Length ?? 0;
            if (colCount == 0) return container;

            // 表头行
            var headerRow = CreateTableRow(block.Headers, colCount, isHeader: true);
            container.Add(headerRow);

            // 分隔线
            var separator = new VisualElement();
            separator.style.height = 1;
            separator.style.backgroundColor = new StyleColor(new UnityEngine.Color(0.4f, 0.4f, 0.4f, 0.6f));
            separator.style.marginLeft = 6;
            separator.style.marginRight = 6;
            container.Add(separator);

            // 数据行
            foreach (var row in block.Rows)
            {
                var rowElement = CreateTableRow(row, colCount, isHeader: false);
                container.Add(rowElement);
            }

            return container;
        }

        /// <summary>
        /// 创建表格行（Flex Row 布局，每个单元格等宽）。
        /// </summary>
        private VisualElement CreateTableRow(string[] cells, int colCount, bool isHeader)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.paddingTop = 3;
            row.style.paddingBottom = 3;
            row.style.paddingLeft = 6;
            row.style.paddingRight = 6;

            for (int c = 0; c < colCount; c++)
            {
                var cellText = c < cells.Length ? cells[c] : "";
                var cellLabel = new Label(cellText);
                cellLabel.style.flexGrow = 1;
                cellLabel.style.flexBasis = 0;
                cellLabel.style.whiteSpace = WhiteSpace.Normal;
                cellLabel.style.fontSize = 12;
                cellLabel.style.paddingLeft = 4;
                cellLabel.style.paddingRight = 4;
                cellLabel.selection.isSelectable = true;

                if (isHeader)
                {
                    cellLabel.style.color = new StyleColor(new UnityEngine.Color(0.9f, 0.85f, 0.7f));
                    cellLabel.style.unityFontStyleAndWeight = UnityEngine.FontStyle.Bold;
                }
                else
                {
                    cellLabel.style.color = new StyleColor(new UnityEngine.Color(0.83f, 0.83f, 0.83f));
                }

                row.Add(cellLabel);
            }

            return row;
        }

        private VisualElement CreateList(ListBlock block)
        {
            var container = new VisualElement();
            container.AddToClassList("content-list");
            container.style.marginTop = 4;
            container.style.marginBottom = 4;

            var listText = string.Join("\n", block.Items);
            var label = new Label(listText);
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.fontSize = 13;
            label.style.color = new StyleColor(new UnityEngine.Color(0.83f, 0.83f, 0.83f));
            label.selection.isSelectable = true;
            container.Add(label);

            return container;
        }

        /// <summary>
        /// 将代码行的前导空格替换为非断裂空格 (U+00A0)，防止 WhiteSpace.Normal 折叠缩进。
        /// </summary>
        private static string PreserveLeadingSpaces(string line)
        {
            int leadingSpaces = 0;
            while (leadingSpaces < line.Length && line[leadingSpaces] == ' ')
                leadingSpaces++;

            if (leadingSpaces == 0) return line;

            // 前导空格 → \u00A0（非断裂空格），其余内容不变
            return new string('\u00A0', leadingSpaces) + line.Substring(leadingSpaces);
        }

        #endregion
    }
}
