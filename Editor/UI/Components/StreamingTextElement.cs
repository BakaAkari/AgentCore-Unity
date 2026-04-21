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
            // 去除开头的空行，保留末尾的自然截断
            filtered = filtered.TrimStart('\n', '\r');

            return filtered.TrimEnd();
        }
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
