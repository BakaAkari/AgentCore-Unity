using System;
using System.Text.RegularExpressions;

namespace AgentCore.Editor.Components.Indexing.Core
{
    /// <summary>
    /// 带超时与长度上限的正则构造工具。
    /// <para>
    /// Mono 运行时没有 <c>RegexOptions.NonBacktracking</c>，灾难性回溯只能靠匹配超时兜底。
    /// 超时以 <see cref="RegexMatchTimeoutException"/> 抛出，调用方必须把它作为错误返回给模型，
    /// 不能当作“0 个结果”。
    /// </para>
    /// </summary>
    public static class SafeRegex
    {
        /// <summary>模式长度上限（字符）。</summary>
        public const int MaxPatternLength = 512;

        /// <summary>单次匹配超时。</summary>
        public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(200);

        /// <summary>
        /// 构造正则。模式过长或语法无效时抛出 <see cref="ArgumentException"/>，消息可直接返回给模型。
        /// </summary>
        public static Regex Create(string pattern, bool ignoreCase)
        {
            if (pattern == null)
                throw new ArgumentException("正则模式不能为空。");

            if (pattern.Length > MaxPatternLength)
                throw new ArgumentException(
                    $"正则模式过长（{pattern.Length} 字符，上限 {MaxPatternLength}）。请缩短模式或拆成多次搜索。");

            var options = RegexOptions.CultureInvariant;
            if (ignoreCase)
                options |= RegexOptions.IgnoreCase;

            try
            {
                return new Regex(pattern, options, MatchTimeout);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"无效的正则模式: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 生成正则超时的错误说明（返回给模型）。
        /// </summary>
        public static string DescribeTimeout(string pattern, string where)
        {
            var location = string.IsNullOrEmpty(where) ? string.Empty : $"（位置：{where}）";
            return $"regex_timeout: 正则 '{pattern}' 单次匹配超过 {MatchTimeout.TotalMilliseconds:F0}ms{location}，" +
                   "很可能存在灾难性回溯（如嵌套量词 (a+)+）。请简化模式后重试；本次搜索结果不可信，未返回部分结果。";
        }
    }
}
