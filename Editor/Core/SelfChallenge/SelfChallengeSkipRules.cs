using System.Linq;
using System.Text.RegularExpressions;

namespace AgentCore.Editor.Core.SelfChallenge
{
    /// <summary>
    /// Node A skip 判定规则（v0.9 §1.2.1 精简版 + v0.10 立场校准）。
    /// <para>
    /// **v0.10 立场**：Skip 判定只做**纯格式识别**，不做任何语义/词义/意图分类。
    /// v0.8 曾包含的 R2（纯代码块）/ R4（堆栈跟踪）/ R5（确认词白名单）**全部取消**——它们含关键词穷举，
    /// 违反"不做穷举"的核心立场。
    /// </para>
    /// <para>
    /// 剩余规则：
    /// <list type="bullet">
    ///   <item><b>R1</b>：消息去除所有空白后 ≤ <see cref="SelfChallengeConfig.R1_ShortMessageMaxChars"/> 个 Unicode 字符</item>
    ///   <item><b>R3</b>：消息是纯 URL（<c>^\s*https?://\S+\s*$</c>）</item>
    /// </list>
    /// 任一命中即 skip。**没有例外情况**（即使处于 WaitingForClarification 状态，只要满足 R1/R3 也 skip）。
    /// </para>
    /// </summary>
    public static class SelfChallengeSkipRules
    {
        // 编译一次；线程安全。
        private static readonly Regex PureUrlRegex = new Regex(
            @"^\s*https?://\S+\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// 评估用户消息是否需要 skip Node A。
        /// </summary>
        /// <param name="userMessage">用户消息原文；<c>null</c> 或空字符串视为 skip（R1 命中）。</param>
        /// <param name="skipReason">
        /// 返回 skip 原因常量（<see cref="SelfChallengeConfig.SkipReasonR1Short"/> 或
        /// <see cref="SelfChallengeConfig.SkipReasonR3Url"/>）；不 skip 时为 <c>null</c>。
        /// </param>
        /// <returns><c>true</c> 表示应该 skip Node A；<c>false</c> 表示正常触发。</returns>
        public static bool ShouldSkip(string userMessage, out string skipReason)
        {
            // R1: null/空/去空白后 ≤ 15 字符
            if (IsShortMessage(userMessage))
            {
                skipReason = SelfChallengeConfig.SkipReasonR1Short;
                return true;
            }

            // R3: 纯 URL
            if (IsPureUrl(userMessage))
            {
                skipReason = SelfChallengeConfig.SkipReasonR3Url;
                return true;
            }

            skipReason = null;
            return false;
        }

        /// <summary>
        /// R1 规则：消息去除所有空白后的 Unicode 字符数 ≤
        /// <see cref="SelfChallengeConfig.R1_ShortMessageMaxChars"/>。
        /// <para>
        /// <c>null</c> / 空字符串 / 纯空白**都视为命中**（长度 = 0）。
        /// </para>
        /// </summary>
        public static bool IsShortMessage(string userMessage)
        {
            if (string.IsNullOrEmpty(userMessage))
                return true;

            // 去除所有 whitespace（包括 tab / newline / 全角空格）后计数
            int nonWhitespaceCount = userMessage.Count(c => !char.IsWhiteSpace(c));
            return nonWhitespaceCount <= SelfChallengeConfig.R1_ShortMessageMaxChars;
        }

        /// <summary>
        /// R3 规则：消息是否是**单个** URL（前后可有空白）。
        /// <para>
        /// 匹配 <c>^\s*https?://\S+\s*$</c>；多个 URL 或 URL 前后夹杂其他文本都**不算**纯 URL。
        /// </para>
        /// </summary>
        public static bool IsPureUrl(string userMessage)
        {
            if (string.IsNullOrEmpty(userMessage))
                return false;

            return PureUrlRegex.IsMatch(userMessage);
        }
    }
}
