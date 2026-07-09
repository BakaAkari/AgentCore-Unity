using System.Text;

namespace AgentCore.Editor.Core.SelfChallenge
{
    /// <summary>
    /// Node B(Answer Self-Challenge)Reviewer prompt 构造器。
    /// 严格对齐设计文档 v0.10 §1.3.3 模板。
    /// </summary>
    public static class AnswerChallengePromptBuilder
    {
        /// <summary>
        /// 构造 Node B Reviewer 完整 instruction。
        /// </summary>
        /// <param name="userQuery">用户原始 query。</param>
        /// <param name="draftAnswer">当前生成的 draft final response。</param>
        /// <param name="intentChallengeBlock">Node A 输出的完整 &lt;intent_challenge&gt; 块(含关键假设)。</param>
        public static string BuildReviewerInstruction(string userQuery, string draftAnswer, string intentChallengeBlock)
        {
            var sb = new StringBuilder(4096);

            sb.Append("<user_query>\n").Append(userQuery ?? string.Empty).Append("\n</user_query>\n\n");

            if (!string.IsNullOrWhiteSpace(intentChallengeBlock))
            {
                sb.Append("<my_intent_challenge>\n").Append(intentChallengeBlock).Append("\n</my_intent_challenge>\n\n");
            }

            sb.Append("<draft_answer>\n").Append(draftAnswer ?? string.Empty).Append("\n</draft_answer>\n\n");

            sb.Append("---\n\n");
            sb.Append("[SYSTEM INSTRUCTION — Answer Self-Challenge Reviewer]\n\n");

            sb.Append("## 角色\n\n");
            sb.Append("你现在的角色是 skeptical reviewer。你**默认认为 <draft_answer> 里有错误**, 你的任务是找出这些错误。**从不假设 draft 是对的**。你**不是 Agent 本人**, 你是一个第三方审查员, 被雇来找 Agent 的问题。\n\n");

            sb.Append("## 强制输出格式(禁止跳过、禁止简写)\n\n");
            sb.Append(SelfChallengeConfig.NodeBOpenMarker).Append("\n\n");

            // Step 1
            sb.Append("## Step 1: Assumption Verification\n\n");
            sb.Append("对照 <my_intent_challenge> 里 Step 3 声明的\"关键假设\", 逐一核对:\n");
            sb.Append("- 假设 1: {原文引用} → 在 tool 结果中的证据是: {引用具体 tool call 结果 + 数据} → 假设**是 / 否**成立\n");
            sb.Append("- 假设 2: ...\n\n");
            sb.Append("如果**任一假设**没有在证据里被明确验证, 标记为 **UNVERIFIED**。\n\n");

            // Step 2
            sb.Append("## Step 2: Counter-Examples (至少 ")
              .Append(SelfChallengeConfig.MinCounterExampleCount)
              .Append(" 个)\n\n");
            sb.Append("假设 <draft_answer> 是错的。**在什么情况下它会错**?至少给出 ")
              .Append(SelfChallengeConfig.MinCounterExampleCount)
              .Append(" 个具体场景。\n\n");
            sb.Append("**每个 Counter-Example 必须包含至少一个 ")
              .Append(SelfChallengeConfig.DraftQuoteOpenMarker)
              .Append("...")
              .Append(SelfChallengeConfig.DraftQuoteCloseMarker)
              .Append(" 标记**, 标记里是**从 draft 里逐字复制**的原文引用(工程侧会校验 quote 内容确实存在于 draft):\n\n");
            sb.Append("- Counter-Example 1: 如果 {某个具体条件}, 那么 draft 里说的 ")
              .Append(SelfChallengeConfig.DraftQuoteOpenMarker)
              .Append("逐字复制的原文")
              .Append(SelfChallengeConfig.DraftQuoteCloseMarker)
              .Append(" 就是错的, 因为 {原因}\n");
            sb.Append("- Counter-Example 2: 如果 {某个具体条件}, 那么 draft 里说的 ")
              .Append(SelfChallengeConfig.DraftQuoteOpenMarker)
              .Append("另一处原文")
              .Append(SelfChallengeConfig.DraftQuoteCloseMarker)
              .Append(" 就是错的, 因为 {原因}\n");
            sb.Append("- Counter-Example 3: ...\n\n");
            sb.Append("**禁止**:\n");
            sb.Append("- 用\"如果数据不准确\"/\"如果我理解错了\" 等通用无信息的假设\n");
            sb.Append("- 引用**通用单词**(如 <8 个字符的短通用词)\n");
            sb.Append("- 引用不指向具体断言的内容\n\n");
            sb.Append("**引用长度要求**: ")
              .Append(SelfChallengeConfig.DraftQuoteOpenMarker)
              .Append(" 内容应至少 ")
              .Append(SelfChallengeConfig.MinDraftQuoteLength)
              .Append(" 个字符, 包含完整语义单元。\n\n");

            // Step 3
            sb.Append("## Step 3: Completeness Check\n\n");
            sb.Append("用户 query 里问的每一件事, draft 都覆盖了吗?\n");
            sb.Append("- User 问的 Part 1: \"{引用 query 一部分}\" → draft 覆盖情况: **完整 / 部分 / 未提及**\n");
            sb.Append("- User 问的 Part 2: ...\n\n");

            // Step 4
            sb.Append("## Step 4: Verdict\n\n");
            sb.Append("在完成 Step 1~3 之后, 做出结论:\n\n");
            sb.Append("- [ ] **PASS**: 所有假设已验证, 无 counter-example 站得住脚, 全部完整覆盖。draft 可以发送。\n");
            sb.Append("- [ ] **REVISE**: 发现至少一处需要修正。列出必须修正的问题:\n");
            sb.Append("    - Issue 1: {具体问题}, 修正方向: {具体建议}\n");
            sb.Append("    - ...\n");
            sb.Append("- [ ] **BLOCK**: 发现关键假设未验证, 必须先做验证性 tool call, 不能直接回复用户。列出需要做的验证:\n");
            sb.Append("    - Verification needed 1: {具体做什么}\n");
            sb.Append("    - ...\n\n");

            sb.Append(SelfChallengeConfig.NodeBCloseMarker).Append("\n\n");

            sb.Append("**禁止**:\n");
            sb.Append("- 写\"draft 看起来没问题\"/\"已经足够完整\" 这类模糊评价\n");
            sb.Append("- 跳过任何 Step\n");
            sb.Append("- Verdict 不做选择(必须勾选 PASS/REVISE/BLOCK 其中一个)\n");

            return sb.ToString();
        }

        /// <summary>
        /// Node B 结构校验失败后的 correction retry instruction。
        /// </summary>
        public static string BuildCorrectionRetryInstruction(System.Collections.Generic.IReadOnlyList<string> issues)
        {
            var sb = new StringBuilder(1024);
            sb.Append("[SYSTEM] Your <answer_challenge> block failed structural validation:\n");
            if (issues != null)
            {
                foreach (var issue in issues)
                {
                    if (!string.IsNullOrEmpty(issue))
                        sb.Append("- ").Append(issue).Append('\n');
                }
            }
            sb.Append('\n');
            sb.Append("Redo the <answer_challenge> block. Ensure all 4 Steps are present, at least ")
              .Append(SelfChallengeConfig.MinCounterExampleCount)
              .Append(" Counter-Examples with ")
              .Append(SelfChallengeConfig.DraftQuoteOpenMarker)
              .Append(" tags (each ≥")
              .Append(SelfChallengeConfig.MinDraftQuoteLength)
              .Append(" chars quoted verbatim from the draft), and a clear PASS/REVISE/BLOCK verdict.\n");
            return sb.ToString();
        }

        /// <summary>
        /// REVISE verdict 触发 draft 重新生成时, 组装的 feedback prompt。
        /// </summary>
        public static string BuildDraftRegenerationFeedback(System.Collections.Generic.IReadOnlyList<string> reviseIssues)
        {
            var sb = new StringBuilder(1024);
            sb.Append("[SYSTEM] Your previous draft response has been reviewed and requires revision.\n");
            sb.Append("Reviewer identified the following issues that MUST be fixed:\n\n");
            if (reviseIssues != null)
            {
                int idx = 1;
                foreach (var issue in reviseIssues)
                {
                    if (!string.IsNullOrEmpty(issue))
                    {
                        sb.Append(idx).Append(". ").Append(issue).Append('\n');
                        idx++;
                    }
                }
            }
            sb.Append("\nRegenerate the final response addressing every issue above. Keep the same overall structure but fix the specific problems. Do NOT repeat the previous mistakes.\n");
            return sb.ToString();
        }
    }
}
