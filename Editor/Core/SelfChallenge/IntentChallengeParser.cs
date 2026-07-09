using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AgentCore.Editor.Core.SelfChallenge
{
    /// <summary>
    /// Node A 的 <c>&lt;intent_challenge&gt;</c> 块结构校验与字段填充器。
    /// <para>
    /// 依据设计文档 v0.10 §1.2.4: 工程侧**只做结构校验**, 不涉及任何语义关键词匹配。
    /// 语义判断(歧义、破坏性、推断词、结论正确性)全部交给 LLM 在 Step 5 自校验完成。
    /// </para>
    /// <para>
    /// 支持完整 Node A(Full)与 Continuation 模式。Continuation 校验规则更宽松:
    /// 允许缺失 Step 1/2, 只校验 Step 3-cont / 4-cont / 5-cont 精简版 + &lt;consistency_correction&gt;。
    /// </para>
    /// </summary>
    public static class IntentChallengeParser
    {
        // ─── 预编译正则 ───────────────────────────────────────────────

        private static readonly Regex InterpretationLineRegex = new Regex(
            @"^\s*Interpretation\s+(\d+)\s*[::]\s*(.+?)$",
            RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Regex Step2HeaderRegex = new Regex(
            @"##\s*Step\s*2[\s\S]{0,40}?(?:歧义|Ambiguity)",
            RegexOptions.Multiline | RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex Step3HeaderRegex = new Regex(
            @"##\s*Step\s*3[\s\S]{0,50}?(?:选定|工作解读|Chosen|Working)",
            RegexOptions.Multiline | RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex Step3ContHeaderRegex = new Regex(
            @"##\s*Step\s*3-cont",
            RegexOptions.Multiline | RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex Step4HeaderRegex = new Regex(
            @"##\s*Step\s*4(?!-cont)[\s\S]{0,60}?(?:澄清|决策|Clarif|Decision)",
            RegexOptions.Multiline | RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex Step4ContHeaderRegex = new Regex(
            @"##\s*Step\s*4-cont",
            RegexOptions.Multiline | RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex Step5HeaderRegex = new Regex(
            @"##\s*Step\s*5(?!-cont)",
            RegexOptions.Multiline | RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex Step5ContHeaderRegex = new Regex(
            @"##\s*Step\s*5-cont",
            RegexOptions.Multiline | RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex KeyAssumptionsRegex = new Regex(
            @"关键假设|Key\s+Assumption",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ChosenRegex = new Regex(
            @"(?:选定的解读是|选择的解读|Chosen(?:\s+interpretation)?)\s*[::]?\s*(?:Interpretation\s*)?(\d+|.+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex DimensionARegex = new Regex(
            @"\bA\s*=\s*(yes|no)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex DimensionBRegex = new Regex(
            @"\bB\s*=\s*(severe|minor)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex DimensionCRegex = new Regex(
            @"\bC\s*=\s*(destructive|safe)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex DimensionDRegex = new Regex(
            @"\bD\s*=\s*(inferred|verbatim)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ConsistencyPassFailRegex = new Regex(
            @"(?:一致性|Consistency)\s*\d+[^\n]{0,40}?[::][^\n]{0,80}?(PASS|FAIL)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ConsistentMarkerRegex = new Regex(
            @"\[Consistent\]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex CorrectedMarkerRegex = new Regex(
            @"Corrected\s+judgement",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex Combo1HitRegex = new Regex(
            @"\[[xX✓]\][^\n]*组合\s*1|命中组合\s*1|hit\s+combo\s*1",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex Combo2HitRegex = new Regex(
            @"\[[xX✓]\][^\n]*组合\s*2|命中组合\s*2|hit\s+combo\s*2",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex DirectExecuteHitRegex = new Regex(
            @"\[[xX✓]\][^\n]*(?:都不命中|直接执行)|(?:都不命中|直接执行)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex InferredWordsRegex = new Regex(
            @"(?:推断的词|Inferred\s+words)\s*[::]?\s*(.+?)(?:$|\n)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex TopicChangeMarkerRegex = new Regex(
            @"\[TOPIC\s+CHANGE\s+DETECTED\]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // ─── 主 API ───────────────────────────────────────────────

        /// <summary>
        /// 解析并结构校验一段完整的 &lt;intent_challenge&gt;...&lt;/intent_challenge&gt; 原文。
        /// </summary>
        /// <param name="rawBlock">含开闭 marker 的完整块原文</param>
        /// <param name="data">要填充的 SelfChallengeData(仅 Node A 相关字段)</param>
        /// <returns>校验结果; Success=true 时 data 已被填充。</returns>
        public static IntentChallengeParseResult Parse(string rawBlock, SelfChallengeData data)
        {
            return ParseInternal(rawBlock, data, isContinuation: false);
        }

        /// <summary>
        /// 解析并结构校验一段完整的 &lt;intent_challenge_continuation&gt; 块。
        /// </summary>
        /// <param name="rawBlock">含开闭 marker 的完整块原文</param>
        /// <param name="data">要填充的 SelfChallengeData</param>
        /// <returns>校验结果。特殊情况: 若含 [TOPIC CHANGE DETECTED], TopicChangeDetected=true</returns>
        public static IntentChallengeParseResult ParseContinuation(string rawBlock, SelfChallengeData data)
        {
            return ParseInternal(rawBlock, data, isContinuation: true);
        }

        private static IntentChallengeParseResult ParseInternal(string rawBlock, SelfChallengeData data, bool isContinuation)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            var issues = new List<string>();
            if (string.IsNullOrWhiteSpace(rawBlock))
            {
                issues.Add("intent_challenge block is empty or null");
                return IntentChallengeParseResult.Fail(issues, topicChange: false);
            }

            // Marker 一致性检查
            string openMarker = isContinuation
                ? SelfChallengeConfig.NodeAContinuationOpenMarker
                : SelfChallengeConfig.NodeAOpenMarker;
            string closeMarker = isContinuation
                ? SelfChallengeConfig.NodeAContinuationCloseMarker
                : SelfChallengeConfig.NodeACloseMarker;

            if (!rawBlock.Contains(openMarker))
                issues.Add($"Missing opening marker '{openMarker}'");
            if (!rawBlock.Contains(closeMarker))
                issues.Add($"Missing closing marker '{closeMarker}'");

            // Continuation 模式特殊: 先检查 [TOPIC CHANGE DETECTED]
            if (isContinuation && TopicChangeMarkerRegex.IsMatch(rawBlock))
            {
                // 话题跳变: 依然回填基本字段, 让上层降级到完整 Node A
                data.NodeATriggered = true;
                data.IsNodeAContinuation = true;
                data.NodeAOutput = rawBlock;
                return IntentChallengeParseResult.Ok(topicChange: true);
            }

            // 完整模式: 校验 Step 1(Interpretations)
            List<string> interpretations = null;
            if (!isContinuation)
            {
                interpretations = ExtractInterpretations(rawBlock);
                if (interpretations.Count < SelfChallengeConfig.MinInterpretationCount)
                {
                    issues.Add($"Missing at least {SelfChallengeConfig.MinInterpretationCount} substantive Interpretations (found {interpretations.Count}, minimum {SelfChallengeConfig.MinInterpretationCount} required)");
                }
                for (int i = 0; i < interpretations.Count; i++)
                {
                    if (interpretations[i].Length < SelfChallengeConfig.MinInterpretationLength)
                    {
                        issues.Add($"Interpretation {i + 1} too short ({interpretations[i].Length} chars, minimum {SelfChallengeConfig.MinInterpretationLength} required)");
                    }
                }

                // Step 2: 歧义信号(完整模式必须, Continuation 省略)
                if (!Step2HeaderRegex.IsMatch(rawBlock))
                {
                    issues.Add("Missing \"Step 2: 找出歧义信号\" section");
                }
            }

            // Step 3: chosen + 关键假设(完整模式) 或 Step 3-cont(Continuation)
            if (isContinuation)
            {
                if (!Step3ContHeaderRegex.IsMatch(rawBlock))
                    issues.Add("Missing \"Step 3-cont\" section");
            }
            else
            {
                if (!Step3HeaderRegex.IsMatch(rawBlock))
                    issues.Add("Missing \"Step 3: 选定工作解读\" section");
                if (!KeyAssumptionsRegex.IsMatch(rawBlock))
                    issues.Add("Missing \"关键假设:\" subsection in Step 3");
            }

            // Step 4: 四维度
            if (isContinuation)
            {
                if (!Step4ContHeaderRegex.IsMatch(rawBlock))
                    issues.Add("Missing \"Step 4-cont\" section");
            }
            else
            {
                if (!Step4HeaderRegex.IsMatch(rawBlock))
                    issues.Add("Missing \"Step 4: 澄清决策\" section");
            }

            var dimAMatch = DimensionARegex.Match(rawBlock);
            var dimBMatch = DimensionBRegex.Match(rawBlock);
            var dimCMatch = DimensionCRegex.Match(rawBlock);
            var dimDMatch = DimensionDRegex.Match(rawBlock);
            if (!dimAMatch.Success) issues.Add("Missing dimension A judgement in Step 4");
            if (!dimBMatch.Success && !isContinuation) issues.Add("Missing dimension B judgement in Step 4");
            if (!dimCMatch.Success) issues.Add("Missing dimension C judgement in Step 4");
            if (!dimDMatch.Success) issues.Add("Missing dimension D judgement in Step 4");

            // Step 5: Consistency check
            if (isContinuation)
            {
                if (!Step5ContHeaderRegex.IsMatch(rawBlock))
                    issues.Add("Missing \"Step 5-cont\" section");
            }
            else
            {
                if (!Step5HeaderRegex.IsMatch(rawBlock))
                    issues.Add("Missing \"Step 5: Self-Consistency Check\" section");
            }

            // <consistency_correction> 块
            if (!rawBlock.Contains(SelfChallengeConfig.ConsistencyCorrectionOpenMarker) ||
                !rawBlock.Contains(SelfChallengeConfig.ConsistencyCorrectionCloseMarker))
            {
                issues.Add($"Missing {SelfChallengeConfig.ConsistencyCorrectionOpenMarker} block");
            }

            // Consistency check 内 PASS/FAIL 判定条数
            int requiredConsistencyChecks = isContinuation ? 3 : 4;
            int consistencyMatchCount = ConsistencyPassFailRegex.Matches(rawBlock).Count;
            bool hasConsistentMarker = ConsistentMarkerRegex.IsMatch(rawBlock);
            bool hasCorrectedMarker = CorrectedMarkerRegex.IsMatch(rawBlock);
            if (consistencyMatchCount < requiredConsistencyChecks && !hasConsistentMarker)
            {
                issues.Add($"<consistency_correction> block missing PASS/FAIL judgements (found {consistencyMatchCount}, minimum {requiredConsistencyChecks} required)");
            }

            // 最终结论: 命中组合 1 / 组合 2 / 直接执行 或 [Consistent]
            bool hasCombo1 = Combo1HitRegex.IsMatch(rawBlock);
            bool hasCombo2 = Combo2HitRegex.IsMatch(rawBlock);
            bool hasDirectExecute = DirectExecuteHitRegex.IsMatch(rawBlock);
            if (!hasCombo1 && !hasCombo2 && !hasDirectExecute && !hasConsistentMarker)
            {
                issues.Add("Missing final Step 4 conclusion (命中组合 X / 都不命中 / [Consistent])");
            }

            // ─── 若有 issues, 早退返回 ───
            if (issues.Count > 0)
            {
                return IntentChallengeParseResult.Fail(issues, topicChange: false);
            }

            // ─── 字段填充 ───
            data.NodeATriggered = true;
            data.IsNodeAContinuation = isContinuation;
            data.NodeAOutput = rawBlock;
            data.NodeASkipReason = null;

            if (!isContinuation)
            {
                data.Interpretations = interpretations;
                data.AmbiguitySignals = ExtractAmbiguitySignals(rawBlock);
                data.ChosenInterpretation = ExtractChosenInterpretation(rawBlock, interpretations);
                data.KeyAssumptions = ExtractKeyAssumptions(rawBlock);
            }
            else
            {
                data.ChosenInterpretation = ExtractContinuationChosen(rawBlock);
            }

            // Step 4 维度
            if (dimAMatch.Success)
                data.Step4A = string.Equals(dimAMatch.Groups[1].Value, "yes", StringComparison.OrdinalIgnoreCase)
                    ? Step4Ambiguity.Yes : Step4Ambiguity.No;
            if (dimBMatch.Success)
                data.Step4B = string.Equals(dimBMatch.Groups[1].Value, "severe", StringComparison.OrdinalIgnoreCase)
                    ? Step4Severity.Severe : Step4Severity.Minor;
            if (dimCMatch.Success)
                data.Step4C = string.Equals(dimCMatch.Groups[1].Value, "destructive", StringComparison.OrdinalIgnoreCase)
                    ? Step4OperationRisk.Destructive : Step4OperationRisk.Safe;
            if (dimDMatch.Success)
                data.Step4D = string.Equals(dimDMatch.Groups[1].Value, "inferred", StringComparison.OrdinalIgnoreCase)
                    ? Step4Attribution.Inferred : Step4Attribution.Verbatim;

            if (data.Step4D == Step4Attribution.Inferred)
            {
                data.InferredWords = ExtractInferredWords(rawBlock);
            }

            // 结论
            if (hasCombo1)
                data.Step4Conclusion = Step4Conclusion.Combo1;
            else if (hasCombo2)
                data.Step4Conclusion = Step4Conclusion.Combo2;
            else
                data.Step4Conclusion = Step4Conclusion.DirectExecute;

            data.TriggeredClarification = data.Step4Conclusion != Step4Conclusion.DirectExecute;

            // Step 5 verdict
            if (hasCorrectedMarker)
            {
                data.Step5Verdict = Step5Verdict.Corrected;
                data.Step5CorrectedJudgement = ExtractCorrectedJudgement(rawBlock);
            }
            else if (hasConsistentMarker)
            {
                data.Step5Verdict = Step5Verdict.Consistent;
            }
            else
            {
                // 全 PASS 但无显式 [Consistent] 标记, 保守推断为 Consistent
                data.Step5Verdict = Step5Verdict.Consistent;
            }

            return IntentChallengeParseResult.Ok(topicChange: false);
        }

        // ─── 辅助抽取函数 ───────────────────────────────────────────

        private static List<string> ExtractInterpretations(string rawBlock)
        {
            var list = new List<string>();
            var matches = InterpretationLineRegex.Matches(rawBlock);
            foreach (Match m in matches)
            {
                string content = m.Groups[2].Value.Trim();
                if (!string.IsNullOrEmpty(content))
                    list.Add(content);
            }
            return list;
        }

        private static List<string> ExtractAmbiguitySignals(string rawBlock)
        {
            var list = new List<string>();
            // 简易抽取: Step 2 到 Step 3 之间的 "- 歧义词" 行
            int start = rawBlock.IndexOf("Step 2", StringComparison.OrdinalIgnoreCase);
            if (start < 0) return list;
            int end = rawBlock.IndexOf("Step 3", start, StringComparison.OrdinalIgnoreCase);
            if (end < 0) end = rawBlock.Length;
            string segment = rawBlock.Substring(start, end - start);
            var lines = segment.Split('\n');
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
                {
                    string content = trimmed.Substring(2).Trim();
                    // 过滤"无歧义信号"这种表述
                    if (content.IndexOf("无歧义", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        content.IndexOf("no ambigu", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        continue;
                    }
                    if (!string.IsNullOrWhiteSpace(content))
                        list.Add(content);
                }
            }
            return list;
        }

        private static string ExtractChosenInterpretation(string rawBlock, List<string> interpretations)
        {
            var m = ChosenRegex.Match(rawBlock);
            if (!m.Success) return null;

            string captured = m.Groups[1].Value.Trim();
            // 如果捕获的是数字, 尝试从 interpretations 列表定位
            if (int.TryParse(captured, out int idx))
            {
                if (interpretations != null && idx >= 1 && idx <= interpretations.Count)
                    return interpretations[idx - 1];
                return $"Interpretation {idx}";
            }
            return captured;
        }

        private static string ExtractContinuationChosen(string rawBlock)
        {
            // Continuation 的 chosen 出现在 "更新后的 chosen interpretation:" 之后
            var regex = new Regex(
                @"更新后的\s*chosen\s*interpretation\s*[::]\s*(.+?)$",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);
            var m = regex.Match(rawBlock);
            if (m.Success) return m.Groups[1].Value.Trim();
            return null;
        }

        private static List<string> ExtractKeyAssumptions(string rawBlock)
        {
            var list = new List<string>();
            var regex = new Regex(
                @"关键假设\s*[::]\s*(.+?)(?=\n##|\n关键|\n\r?\n\r?\n|$)",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            var m = regex.Match(rawBlock);
            if (m.Success)
            {
                string content = m.Groups[1].Value.Trim();
                // 拆分成条目
                var lines = content.Split('\n');
                foreach (var line in lines)
                {
                    var trimmed = line.Trim().TrimStart('-', '*').Trim();
                    if (!string.IsNullOrWhiteSpace(trimmed))
                        list.Add(trimmed);
                }
                // 如果只有一段没有拆分, 整体作为一条
                if (list.Count == 0 && !string.IsNullOrWhiteSpace(content))
                    list.Add(content);
            }
            return list;
        }

        private static List<string> ExtractInferredWords(string rawBlock)
        {
            var list = new List<string>();
            var m = InferredWordsRegex.Match(rawBlock);
            if (!m.Success) return list;

            string raw = m.Groups[1].Value.Trim().Trim('[', ']', '{', '}');
            // 按逗号 / 分号 / 顿号切分
            var parts = raw.Split(new[] { ',', '，', ';', '；', '、' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                var trimmed = p.Trim().Trim('"', '\'', '`');
                if (!string.IsNullOrWhiteSpace(trimmed))
                    list.Add(trimmed);
            }
            return list;
        }

        private static string ExtractCorrectedJudgement(string rawBlock)
        {
            int start = rawBlock.IndexOf("Corrected judgement", StringComparison.OrdinalIgnoreCase);
            if (start < 0) return null;

            // 抓取 Corrected judgement 到 consistency_correction 关闭 marker 之间的内容
            int end = rawBlock.IndexOf(
                SelfChallengeConfig.ConsistencyCorrectionCloseMarker,
                start,
                StringComparison.OrdinalIgnoreCase);
            if (end < 0) end = rawBlock.Length;

            return rawBlock.Substring(start, end - start).Trim();
        }
    }

    /// <summary>
    /// IntentChallengeParser.Parse 的返回结果。
    /// </summary>
    public readonly struct IntentChallengeParseResult
    {
        private readonly IReadOnlyList<string> _issues;

        private IntentChallengeParseResult(bool success, IReadOnlyList<string> issues, bool topicChangeDetected)
        {
            Success = success;
            _issues = issues ?? Array.Empty<string>();
            TopicChangeDetected = topicChangeDetected;
        }

        /// <summary>校验是否通过。</summary>
        public bool Success { get; }

        /// <summary>Continuation 模式下检测到 [TOPIC CHANGE DETECTED] 标记(应降级为完整 Node A)。</summary>
        public bool TopicChangeDetected { get; }

        /// <summary>失败时的 issue 列表; 通过时为空。</summary>
        public IReadOnlyList<string> Issues => _issues;

        /// <summary>拼接用于 correction retry prompt 的 issue 段落。</summary>
        public string ToCorrectionPromptSection()
        {
            if (_issues == null || _issues.Count == 0) return string.Empty;
            var sb = new System.Text.StringBuilder();
            foreach (var issue in _issues)
            {
                if (!string.IsNullOrEmpty(issue))
                    sb.Append("- ").Append(issue).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>创建成功结果。</summary>
        public static IntentChallengeParseResult Ok(bool topicChange = false)
        {
            return new IntentChallengeParseResult(true, Array.Empty<string>(), topicChange);
        }

        /// <summary>创建失败结果。</summary>
        public static IntentChallengeParseResult Fail(IReadOnlyList<string> issues, bool topicChange = false)
        {
            return new IntentChallengeParseResult(false, issues ?? Array.Empty<string>(), topicChange);
        }
    }
}