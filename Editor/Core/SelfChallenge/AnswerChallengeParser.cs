using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AgentCore.Editor.Core.SelfChallenge
{
    /// <summary>
    /// Node B 的 &lt;answer_challenge&gt; 块结构校验与字段填充器。
    /// 依据设计文档 v0.10 §1.3.4: 工程侧只做结构校验 + draft-quote substring 校验, 语义完全交给 LLM。
    /// </summary>
    public static class AnswerChallengeParser
    {
        private static readonly Regex Step1HeaderRegex = new Regex(
            @"##\s*Step\s*1[\s\S]{0,50}?(?:Assumption|假设)",
            RegexOptions.Multiline | RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex Step2HeaderRegex = new Regex(
            @"##\s*Step\s*2[\s\S]{0,50}?(?:Counter-Example|反例)",
            RegexOptions.Multiline | RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex Step3HeaderRegex = new Regex(
            @"##\s*Step\s*3[\s\S]{0,50}?(?:Completeness|完整|Complete)",
            RegexOptions.Multiline | RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex Step4HeaderRegex = new Regex(
            @"##\s*Step\s*4[\s\S]{0,30}?(?:Verdict|结论|判决)",
            RegexOptions.Multiline | RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex CounterExampleRegex = new Regex(
            @"Counter-Example\s+(\d+)\s*[::]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // 匹配 <draft-quote>...</draft-quote>
        private static readonly Regex DraftQuoteRegex = new Regex(
            @"<draft-quote>(.*?)</draft-quote>",
            RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

        // Verdict 判定: [x] PASS / [x] REVISE / [x] BLOCK, 或直接的 "PASS"/"REVISE"/"BLOCK" 加粗表述
        private static readonly Regex VerdictPassRegex = new Regex(
            @"\[[xX✓]\][^\n]*\bPASS\b|\*\*PASS\*\*",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex VerdictReviseRegex = new Regex(
            @"\[[xX✓]\][^\n]*\bREVISE\b|\*\*REVISE\*\*",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex VerdictBlockRegex = new Regex(
            @"\[[xX✓]\][^\n]*\bBLOCK\b|\*\*BLOCK\*\*",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex IssueRegex = new Regex(
            @"Issue\s+\d+\s*[::]\s*(.+?)(?=(?:\n\s*(?:-\s+)?Issue\s+\d+|\n##|\n\s*$|$))",
            RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

        private static readonly Regex VerificationNeededRegex = new Regex(
            @"Verification\s+needed\s+\d+\s*[::]\s*(.+?)(?=(?:\n\s*(?:-\s+)?Verification|\n##|\n\s*$|$))",
            RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

        /// <summary>
        /// 解析 &lt;answer_challenge&gt; 块并填充 SelfChallengeData Node B 相关字段。
        /// </summary>
        /// <param name="rawBlock">含开闭 marker 的完整块原文。</param>
        /// <param name="draftContent">被审查的 draft 原文, 用于 draft-quote substring 校验。</param>
        /// <param name="data">要填充的 SelfChallengeData。</param>
        public static AnswerChallengeParseResult Parse(string rawBlock, string draftContent, SelfChallengeData data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            var issues = new List<string>();
            if (string.IsNullOrWhiteSpace(rawBlock))
            {
                issues.Add("answer_challenge block is empty");
                return AnswerChallengeParseResult.Fail(issues);
            }

            if (!rawBlock.Contains(SelfChallengeConfig.NodeBOpenMarker))
                issues.Add("Missing <answer_challenge> opening marker");
            if (!rawBlock.Contains(SelfChallengeConfig.NodeBCloseMarker))
                issues.Add("Missing </answer_challenge> closing marker");

            if (!Step1HeaderRegex.IsMatch(rawBlock))
                issues.Add("Missing Step 1: Assumption Verification section");
            if (!Step2HeaderRegex.IsMatch(rawBlock))
                issues.Add("Missing Step 2: Counter-Examples section");
            if (!Step3HeaderRegex.IsMatch(rawBlock))
                issues.Add("Missing Step 3: Completeness Check section");
            if (!Step4HeaderRegex.IsMatch(rawBlock))
                issues.Add("Missing Step 4: Verdict section");

            // Counter-Example 数量
            var counterExampleMatches = CounterExampleRegex.Matches(rawBlock);
            if (counterExampleMatches.Count < SelfChallengeConfig.MinCounterExampleCount)
            {
                issues.Add($"Only {counterExampleMatches.Count} Counter-Example(s) found (minimum {SelfChallengeConfig.MinCounterExampleCount} required)");
            }

            // draft-quote 校验
            var draftQuotes = new List<string>();
            var quoteMatches = DraftQuoteRegex.Matches(rawBlock);
            int minQuoteLen = SelfChallengeConfig.MinDraftQuoteLength;

            foreach (Match m in quoteMatches)
            {
                string quote = m.Groups[1].Value?.Trim() ?? string.Empty;
                if (quote.Length < minQuoteLen)
                {
                    issues.Add($"<draft-quote> content too short ({quote.Length} chars, minimum {minQuoteLen} required): \"{Truncate(quote, 40)}\"");
                    continue;
                }
                if (!string.IsNullOrEmpty(draftContent) && !ContainsRelaxed(draftContent, quote))
                {
                    issues.Add($"<draft-quote> content not found verbatim in draft: \"{Truncate(quote, 40)}\"");
                    continue;
                }
                draftQuotes.Add(quote);
            }

            if (draftQuotes.Count < SelfChallengeConfig.MinCounterExampleCount)
            {
                issues.Add($"Insufficient valid <draft-quote> tags ({draftQuotes.Count} valid, minimum {SelfChallengeConfig.MinCounterExampleCount} required)");
            }

            // Verdict 判定
            bool isPass = VerdictPassRegex.IsMatch(rawBlock);
            bool isRevise = VerdictReviseRegex.IsMatch(rawBlock);
            bool isBlock = VerdictBlockRegex.IsMatch(rawBlock);

            int verdictCount = 0;
            if (isPass) verdictCount++;
            if (isRevise) verdictCount++;
            if (isBlock) verdictCount++;

            if (verdictCount == 0)
            {
                issues.Add("Missing Step 4 verdict (must be one of PASS / REVISE / BLOCK)");
            }
            else if (verdictCount > 1)
            {
                issues.Add($"Multiple verdicts found ({verdictCount}); must be exactly one of PASS / REVISE / BLOCK");
            }

            if (issues.Count > 0)
            {
                return AnswerChallengeParseResult.Fail(issues);
            }

            // 填充
            data.NodeBTriggered = true;
            data.NodeBSkipReason = null;
            data.NodeBOutput = rawBlock;
            data.CounterExampleCount = counterExampleMatches.Count;
            data.CounterExampleQuotes = draftQuotes;

            if (isPass) data.NodeBVerdict = NodeBVerdict.PASS;
            else if (isRevise)
            {
                data.NodeBVerdict = NodeBVerdict.REVISE;
                data.ReviseIssues = ExtractIssues(rawBlock);
            }
            else if (isBlock)
            {
                data.NodeBVerdict = NodeBVerdict.BLOCK;
                data.BlockVerifications = ExtractVerifications(rawBlock);
            }

            return AnswerChallengeParseResult.Ok();
        }

        private static List<string> ExtractIssues(string rawBlock)
        {
            var list = new List<string>();
            foreach (Match m in IssueRegex.Matches(rawBlock))
            {
                var content = m.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(content))
                    list.Add(content);
            }
            // 兜底: 若 IssueRegex 没匹配到, 尝试按 "- " 列表提取
            if (list.Count == 0)
            {
                int reviseSectionStart = rawBlock.IndexOf("REVISE", StringComparison.OrdinalIgnoreCase);
                if (reviseSectionStart >= 0)
                {
                    var lines = rawBlock.Substring(reviseSectionStart).Split('\n');
                    foreach (var line in lines)
                    {
                        var t = line.Trim();
                        if (t.StartsWith("- ") || t.StartsWith("* "))
                        {
                            list.Add(t.Substring(2).Trim());
                        }
                    }
                }
            }
            return list;
        }

        private static List<string> ExtractVerifications(string rawBlock)
        {
            var list = new List<string>();
            foreach (Match m in VerificationNeededRegex.Matches(rawBlock))
            {
                var content = m.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(content))
                    list.Add(content);
            }
            return list;
        }

        // Substring 匹配但宽松: 空白规范化
        private static bool ContainsRelaxed(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return false;
            // 直接匹配
            if (haystack.Contains(needle)) return true;
            // 规范化空白后匹配
            string normalizedHaystack = Regex.Replace(haystack, @"\s+", " ");
            string normalizedNeedle = Regex.Replace(needle, @"\s+", " ");
            return normalizedHaystack.Contains(normalizedNeedle);
        }

        private static string Truncate(string s, int len)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Length <= len ? s : s.Substring(0, len) + "...";
        }
    }

    /// <summary>AnswerChallengeParser.Parse 的返回结果。</summary>
    public readonly struct AnswerChallengeParseResult
    {
        private readonly IReadOnlyList<string> _issues;

        private AnswerChallengeParseResult(bool success, IReadOnlyList<string> issues)
        {
            Success = success;
            _issues = issues ?? Array.Empty<string>();
        }

        public bool Success { get; }
        public IReadOnlyList<string> Issues => _issues;

        public static AnswerChallengeParseResult Ok()
            => new AnswerChallengeParseResult(true, Array.Empty<string>());

        public static AnswerChallengeParseResult Fail(IReadOnlyList<string> issues)
            => new AnswerChallengeParseResult(false, issues ?? Array.Empty<string>());
    }
}
