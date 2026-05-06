using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Components
{
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
        /// 已知 emoji → SDF 字体安全替代字符的映射表。
        /// 仅收录在 LLM 响应中高频出现且 Inter-Regular SDF 不支持的 emoji。
        /// </summary>
        private static readonly (string Emoji, string Replacement)[] EmojiReplacements =
        {
            ("\U0001F4F7", "[Cam]"),   // 📷 Camera
            ("\U0001F50D", "[Find]"),  // 🔍 Magnifying Glass
            ("\U0001F4D0", "[Tool]"),  // 📐 Triangular Ruler
            ("\U0001F4A1", "[Idea]"),  // 💡 Light Bulb
            ("\U0001F680", "[Go]"),    // 🚀 Rocket
            ("\U0001F4E6", "[Pkg]"),   // 📦 Package
            ("\U0001F527", "[Fix]"),   // 🔧 Wrench
            ("\U0001F6E0", "[Build]"), // 🛠 Hammer and Wrench
            ("\U0001F4DD", "[Note]"),  // 📝 Memo
            ("\U0001F4C1", "[Dir]"),   // 📁 Folder
            ("\U0001F4C4", "[File]"),  // 📄 Page Facing Up
            ("\U0001F3AF", "[Hit]"),   // 🎯 Bullseye
            ("\U0001F4CA", "[Chart]"), // 📊 Bar Chart
            ("\U0001F512", "[Lock]"),  // 🔒 Lock
            ("\U0001F513", "[Open]"),  // 🔓 Unlock
        };

        /// <summary>
        /// 过滤完整的 tool_call 和 tool_result 标签及其内容。
        /// 用于最终化消息时的完整过滤。
        /// </summary>
        /// <param name="content">原始内容</param>
        /// <returns>过滤后的内容</returns>
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
            // 替换 SDF 字体不支持的 emoji 字符
            filtered = SanitizeUnsupportedEmoji(filtered);
            // 轻量级 Markdown 格式化（流式输出中也需要，否则带 tool_calls 的消息不会被格式化）
            filtered = FormatMarkdown(filtered);
            // 去除开头的空行，保留末尾的自然截断
            filtered = filtered.TrimStart('\n', '\r');

            return filtered.TrimEnd();
        }

        /// <summary>
        /// 替换 SDF 字体不支持的 emoji 字符。
        /// <para>
        /// Unity 的 Inter-Regular SDF 字体不包含 Supplementary Multilingual Plane (U+10000+) 中的
        /// emoji 字符（如 📷🔍📐 等），渲染时会显示为方块 (□)。
        /// 此方法将已知的高频 emoji 替换为可读的 ASCII 标签，
        /// 并将其余未映射的 SMP 字符（surrogate pairs）静默移除。
        /// </para>
        /// </summary>
        /// <param name="text">可能包含 emoji 的文本</param>
        /// <returns>替换后的 SDF 字体安全文本</returns>
        internal static string SanitizeUnsupportedEmoji(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // 快速路径：如果没有 surrogate pairs，直接返回
            bool hasSurrogates = false;
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i]))
                {
                    hasSurrogates = true;
                    break;
                }
            }
            if (!hasSurrogates) return text;

            // 先替换已知的 emoji 映射
            var result = text;
            foreach (var (emoji, replacement) in EmojiReplacements)
            {
                if (result.Contains(emoji))
                {
                    result = result.Replace(emoji, replacement);
                }
            }

            // 移除剩余的未映射 surrogate pairs（SMP 字符）
            // 这些字符在 SDF 字体中几乎都不支持
            var sb = new StringBuilder(result.Length);
            for (int i = 0; i < result.Length; i++)
            {
                if (char.IsHighSurrogate(result[i]))
                {
                    // 跳过 surrogate pair（高代理 + 低代理）
                    if (i + 1 < result.Length && char.IsLowSurrogate(result[i + 1]))
                    {
                        i++; // 跳过低代理
                    }
                    continue;
                }
                sb.Append(result[i]);
            }

            return sb.ToString();
        }

        #region Markdown 格式化

        /// <summary>
        /// 轻量级 Markdown → 可读纯文本格式化。
        /// <para>
        /// 不做完整的 Markdown 渲染（Unity UI Toolkit Label 不支持），
        /// 而是将 Markdown 语法转换为更易读的纯文本格式：
        /// <list type="bullet">
        ///   <item>标题 <c>### Title</c> → <c>【Title】</c></item>
        ///   <item>表格 → 对齐的纯文本表格（去掉分隔线行）</item>
        ///   <item>粗体 <c>**text**</c> → <c>&lt;b&gt;text&lt;/b&gt;</c>（Rich Text）</item>
        ///   <item>无序列表 <c>- item</c> → <c>  · item</c></item>
        ///   <item>有序列表 <c>1. item</c> → <c>  1) item</c></item>
        ///   <item>代码块保持原样（加缩进标记）</item>
        ///   <item>水平线 <c>---</c> → <c>────────</c></item>
        /// </list>
        /// </para>
        /// </summary>
        /// <param name="content">过滤后的内容</param>
        /// <returns>格式化后的可读文本</returns>
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
        /// 格式化 Markdown 标题行。
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
                1 => $"<b><size=16>═══ {title} ═══</size></b>",
                2 => $"<b><size=15>── {title} ──</size></b>",
                3 => $"<b>【{title}】</b>",
                4 => $"<b>{title}</b>",
                _ => $"<b>{title}</b>"
            };
        }

        /// <summary>
        /// 格式化内联样式：粗体、斜体、内联代码。
        /// </summary>
        private static string FormatInlineStyles(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // 粗体 **text** → <b>text</b>
            text = Regex.Replace(text, @"\*\*(.+?)\*\*", "<b>$1</b>");

            // 斜体 *text*（不匹配 **）→ <i>text</i>
            text = Regex.Replace(text, @"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)", "<i>$1</i>");

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
        /// </summary>
        private static string[] ParseTableRow(string line)
        {
            // 去掉首尾的 |
            var trimmed = line.Trim();
            if (trimmed.StartsWith("|")) trimmed = trimmed.Substring(1);
            if (trimmed.EndsWith("|")) trimmed = trimmed.Substring(0, trimmed.Length - 1);

            var cells = trimmed.Split('|');
            for (int i = 0; i < cells.Length; i++)
            {
                cells[i] = cells[i].Trim();
            }
            return cells;
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
    }

    /// <summary>
    /// 流式文本显示元素。
    /// <para>
    /// 用于在助手消息气泡中逐 token 显示 LLM 流式输出的文本。
    /// 支持追加文本、设置最终文本和清空操作，并提供可选的闪烁光标效果。
    /// </para>
    /// </summary>
    public class StreamingTextElement : VisualElement
    {
        #region 私有字段

        /// <summary>显示文本内容的 Label</summary>
        private readonly Label _textLabel;

        /// <summary>闪烁光标元素</summary>
        private readonly VisualElement _cursor;

        /// <summary>当前累积的文本内容</summary>
        private string _currentText = "";

        /// <summary>光标闪烁动画调度器</summary>
        private IVisualElementScheduledItem _cursorBlink;

        /// <summary>光标当前是否可见</summary>
        private bool _cursorVisible;

        #endregion

        #region 构造函数

        /// <summary>
        /// 创建流式文本显示元素。
        /// 包含一个文本 Label 和一个可选的闪烁光标。
        /// </summary>
        public StreamingTextElement()
        {
            // 容器样式
            style.flexDirection = FlexDirection.Row;
            style.flexWrap = Wrap.Wrap;
            style.alignItems = Align.FlexEnd;

            // 文本标签
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
            Add(_textLabel);

            // 闪烁光标
            _cursor = new VisualElement();
            _cursor.AddToClassList("streaming-cursor");
            _cursor.style.display = DisplayStyle.None;
            Add(_cursor);
        }

        #endregion

        #region 公开方法

        /// <summary>
        /// 追加流式文本。
        /// 每次收到一个 token 时调用此方法，文本会累积显示。
        /// </summary>
        /// <param name="text">要追加的文本片段</param>
        public void AppendText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            _currentText += text;
            // 流式过滤：移除已完成的标签对，截断不完整标签
            _textLabel.text = ContentFilter.FilterStreaming(_currentText);
            ShowCursor();
        }

        /// <summary>
        /// 设置最终完整文本。
        /// 流式输出完成后调用，替换当前累积的文本并隐藏光标。
        /// </summary>
        /// <param name="text">完整的最终文本</param>
        public void SetFinalText(string text)
        {
            _currentText = text ?? "";
            // 最终化时做完整过滤
            _textLabel.text = ContentFilter.FilterCompleted(_currentText);
            HideCursor();
        }

        /// <summary>
        /// 清空所有文本内容并隐藏光标。
        /// </summary>
        public void Clear()
        {
            _currentText = "";
            _textLabel.text = "";
            HideCursor();
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
    }
}
