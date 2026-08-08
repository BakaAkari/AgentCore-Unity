using System.Text;

namespace AgentCore.Editor.Core.SelfChallenge
{
    /// <summary>
    /// Node A(Intent Self-Challenge)prompt 构造器。
    /// <para>
    /// 生成追加在用户 message 之后的 system-level instruction 文本, 严格对齐
    /// 设计文档 v0.10 §1.2.2(完整模式)与 §1.2.5(Continuation 模式)。
    /// </para>
    /// <para>
    /// **纯函数**: 无状态, 无副作用, 线程安全。所有 marker / 数字阈值均从
    /// <see cref="SelfChallengeConfig"/> 引用, 不硬编码。
    /// </para>
    /// </summary>
    public static class IntentChallengePromptBuilder
    {
        /// <summary>
        /// 构造 Node A 完整模式 instruction。
        /// <para>
        /// 使用场景: 新用户 message 触发 Node A(非 WaitingForClarification 后的 Continuation)。
        /// 追加位置: 作为独立 system message 紧跟在最后一条 user message 之后。
        /// </para>
        /// </summary>
        /// <returns>system-level instruction 完整文本(约 500-800 tokens)。</returns>
        public static string BuildFullNodeAInstruction()
        {
            var sb = new StringBuilder(3072);
            sb.Append("[SYSTEM INSTRUCTION — Node A: Intent Self-Challenge]\n\n");
            sb.Append("在开始规划任何工具调用前, 你必须先完成一次\"需求自我挑战\"。禁止跳过、禁止简写。\n");
            sb.Append("你的输出必须以下面的完整块开头, 完成后才能继续正常输出或调用工具。\n\n");

            sb.Append(SelfChallengeConfig.NodeAOpenMarker).Append("\n\n");

            // Step 1
            sb.Append("## Step 1: 提出至少 ")
              .Append(SelfChallengeConfig.MinInterpretationCount)
              .Append(" 种对用户需求的**不同解读**\n\n");
            sb.Append("列出用户可能想问的不同事情。每种解读必须**结构上有差异**, 不能是同一件事的不同措辞。\n");
            sb.Append("每个 Interpretation 至少 ")
              .Append(SelfChallengeConfig.MinInterpretationLength)
              .Append(" 个字符, 避免\"同上\"/\"略\"之类偷懒。\n\n");
            sb.Append("Interpretation 1: [具体、可操作]\n");
            sb.Append("Interpretation 2: [不同粒度 / 不同数量 / 不同范围]\n");
            sb.Append("Interpretation 3: [如果用户提问模糊, 最\"错\"的解读会是什么?]\n\n");

            // Step 2
            sb.Append("## Step 2: 找出**歧义信号**\n\n");
            sb.Append("用户提问中哪些词是模糊的?(举例: 数量词、指代词、量词、隐含单/复数、时间范围、空间范围)\n");
            sb.Append("- 歧义词 1: \"{词}\" — 可能指 A 或 B\n");
            sb.Append("- 歧义词 2: ...\n");
            sb.Append("如果没有歧义, 明确写: **无歧义信号**。\n\n");

            // Step 3
            sb.Append("## Step 3: 选定工作解读\n\n");
            sb.Append("我选定的解读是: Interpretation X\n");
            sb.Append("选择理由: {基于什么假设 / 上下文 / 之前的对话}\n");
            sb.Append("关键假设: {如果这个假设错了, 答案会完全不同的 1~2 个点}\n\n");

            // Step 4
            sb.Append("## Step 4: 澄清决策(v0.9 组合触发)\n\n");
            sb.Append("**判断以下四个维度, 每个必须明确回答**:\n\n");
            sb.Append("- 维度 A(歧义): Step 2 里是否列出了歧义词?\n");
            sb.Append("  - `A=yes`(有歧义) / `A=no`(零歧义)\n");
            sb.Append("- 维度 B(差异严重度): Interpretation 3(最\"错\"解读)与 chosen 解读的**行为差异**是否严重?\n");
            sb.Append("  - 严重定义: 会造成不可逆修改、误导后续多轮 tool call、或用户会需要重新操作\n");
            sb.Append("  - `B=severe`(差异严重) / `B=minor`(差异不严重)\n");
            sb.Append("- 维度 C(破坏性): 我要执行的操作是否**破坏性**?\n");
            sb.Append("  - **破坏性**: 删除 / 覆盖 / 覆写 / 批量修改现有 / 无法撤销\n");
            sb.Append("  - **非破坏性**: 新建 / 追加 / 只读查询 / 可撤销的修改\n");
            sb.Append("  - **由你根据 chosen interpretation 自行判断**(不是工程侧穷举动词列表)\n");
            sb.Append("  - `C=destructive` / `C=safe`\n");
            sb.Append("- 维度 D(推断词): chosen interpretation 里的**关键名词/动词/形容词**是否都来自用户 query?\n");
            sb.Append("  - 不来自 query(是你推断出来的)的词汇, **明确列出**\n");
            sb.Append("  - `D=inferred` + 推断的词列表 / `D=verbatim`(零推断)\n\n");
            sb.Append("**反问触发条件**(组合逻辑):\n");
            sb.Append("- **组合 1**: `A=yes` **且** `B=severe`\n");
            sb.Append("- **组合 2**: `C=destructive` **且** `D=inferred`\n");
            sb.Append("- 其他情况: 直接执行\n\n");
            sb.Append("**明确写出结论**:\n");
            sb.Append("- 我的判断: A={yes/no}, B={severe/minor}, C={destructive/safe}, D={inferred/verbatim}\n");
            sb.Append("- 如果 D=inferred, 推断的词是: [列表]\n");
            sb.Append("- 触发反问?\n");
            sb.Append("  - [ ] 命中组合 1(A=yes 且 B=severe)\n");
            sb.Append("  - [ ] 命中组合 2(C=destructive 且 D=inferred)\n");
            sb.Append("  - [ ] 都不命中, 直接执行\n\n");
            sb.Append("**如果决定反问, 输出格式**:\n");
            sb.Append("```\n");
            sb.Append("[CLARIFICATION NEEDED]\n");
            sb.Append("我理解你想 {chosen interpretation 简述}, 但存在以下需要你确认的地方:\n");
            sb.Append("{根据命中组合说明具体原因}\n");
            sb.Append("请确认你想要的是:\n");
            sb.Append("1. {Interpretation 1 简述}\n");
            sb.Append("2. {Interpretation 2 简述}\n");
            sb.Append("3. {Interpretation 3 简述}(或\"其他, 请直接说明\")\n");
            sb.Append("```\n\n");
            sb.Append("**注意**: 反问 message 是本轮唯一输出, **不能再调用任何工具**。等用户回复后进入下一轮。\n\n");

            // Step 5
            sb.Append("## Step 5: Self-Consistency Check\n\n");
            sb.Append("在完成 Step 1-4 之后, 你必须**回头审视自己刚写的输出**, 验证以下四条自一致性:\n\n");
            sb.Append("- **一致性 1**(歧义 vs A): 你在 Step 2 是否列出了歧义词?如果有, A 应该 = yes; 如果没有, A 应该 = no。你在 Step 4 的 A 判断是否与此一致?\n");
            sb.Append("- **一致性 2**(破坏性 vs C): 读你的 chosen interpretation, 它描述的操作在语义上是否属于\"删除/覆盖/覆写/批量修改现有\"?如果是, C 应该 = destructive。你在 Step 4 的 C 判断是否与此一致?\n");
            sb.Append("- **一致性 3**(推断词 vs D): 读你的 chosen interpretation, 其中的**关键名词/动词/形容词是否都能在用户原始 query 里找到**?如果不能, D 应该 = inferred 且必须列出推断的词。你在 Step 4 的 D 判断是否与此一致?\n");
            sb.Append("- **一致性 4**(结论): 根据你自己的 A/B/C/D 判断, 套用组合 1/组合 2 逻辑, Step 4 的结论是否正确?\n\n");
            sb.Append("**输出格式**(无论是否有不一致都必须输出这个块):\n\n");
            sb.Append(SelfChallengeConfig.ConsistencyCorrectionOpenMarker).Append("\n");
            sb.Append("Consistency check:\n");
            sb.Append("- 一致性 1: [PASS / FAIL — 具体不一致点]\n");
            sb.Append("- 一致性 2: [PASS / FAIL — 具体不一致点]\n");
            sb.Append("- 一致性 3: [PASS / FAIL — 具体不一致点]\n");
            sb.Append("- 一致性 4: [PASS / FAIL — 具体不一致点]\n\n");
            sb.Append("如果有 FAIL:\n");
            sb.Append("Corrected judgement:\n");
            sb.Append("- A={yes/no}, B={severe/minor}, C={destructive/safe}, D={inferred/verbatim}\n");
            sb.Append("- 新的 Step 4 结论: [命中组合 X / 都不命中, 直接执行]\n\n");
            sb.Append("如果全部 PASS:\n");
            sb.Append("[Consistent]\n");
            sb.Append(SelfChallengeConfig.ConsistencyCorrectionCloseMarker).Append("\n\n");
            sb.Append("**禁止**:\n");
            sb.Append("- 跳过 Step 5\n");
            sb.Append("- 在 corrected judgement 里逃避(例如原本 A=yes 修正为 A=no 但没解释理由)\n\n");

            // v1.14.10: 原本这段"完成 5 个 Step 后该怎么做"的指令写在 NodeACloseMarker **之后**，
            // 意图是"标签结束后, 你接下来该做什么"。但实测 DeepSeek-V4-Flash 会把这段
            // "标签后的动作说明"误当成"标签前需要先输出的内容", 逐字复述一遍才开始正式输出
            // <intent_challenge> 块, 导致这段复述文本(不属于块内, 提取器按设计不剥离标签外文本)
            // 泄漏到用户可见的正文里。根治方式: 把这段动作说明挪到 Step 5 内部、close marker
            // **之前**, 让模型清楚这是 Step 5 输出的最后一部分, 不是"看完就该复述"的独立指令。
            sb.Append("**Step 5 完成后的下一步动作**(仍属于本 Step 5 输出, 不要在这之前额外复述这条规则):\n");
            sb.Append("- 若上面的结论是\"都不命中, 直接执行\"→ 紧接着开始调工具或直接回答, **不要**重复输出这条规则本身\n");
            sb.Append("- 若上面的结论是\"命中组合 X\"→ 紧接着直接输出 `[CLARIFICATION NEEDED]` 反问, 禁止调工具, **不要**重复输出这条规则本身\n\n");

            sb.Append(SelfChallengeConfig.NodeACloseMarker).Append("\n\n");

            return sb.ToString();
        }

        /// <summary>
        /// 构造 Node A Continuation 模式 instruction(v0.9 §1.2.5)。
        /// <para>
        /// 使用场景: Agent 处于 WaitingForClarification 状态时, 用户新回复且未 skip。
        /// 引用上一轮完整 Node A 的输出, 只做 Step 3-cont / 4-cont / 5-cont 精简版。
        /// </para>
        /// </summary>
        /// <param name="previousUserMessage">用户上一轮的原始 message</param>
        /// <param name="previousIntentChallengeBlock">上一轮完整 &lt;intent_challenge&gt; 块原文</param>
        /// <param name="previousClarificationMessage">上一轮 Agent 的反问 message</param>
        /// <returns>Continuation instruction 完整文本(约 300-500 tokens 追加)</returns>
        public static string BuildContinuationNodeAInstruction(
            string previousUserMessage,
            string previousIntentChallengeBlock,
            string previousClarificationMessage)
        {
            var sb = new StringBuilder(2048);
            sb.Append("[SYSTEM INSTRUCTION — Node A Continuation: 处理澄清后的用户回复]\n\n");
            sb.Append("上一轮你判定需要向用户反问, 用户现在给出了回复。你需要根据用户回复更新 chosen interpretation。\n");
            sb.Append("Continuation 模式**只做 Step 3-cont / 4-cont / 5-cont 精简版**, **禁止重做 Step 1 / Step 2**。\n\n");

            sb.Append("**上一轮用户的原始 message**:\n");
            sb.Append(previousUserMessage ?? string.Empty).Append("\n\n");

            sb.Append("**上一轮的 Node A 输出**:\n");
            sb.Append("<previous_intent_challenge>\n");
            sb.Append(previousIntentChallengeBlock ?? string.Empty).Append("\n");
            sb.Append("</previous_intent_challenge>\n\n");

            sb.Append("**上一轮 Agent 的反问 message**:\n");
            sb.Append(previousClarificationMessage ?? string.Empty).Append("\n\n");

            sb.Append("你的输出必须以下面完整块开头:\n\n");
            sb.Append(SelfChallengeConfig.NodeAContinuationOpenMarker).Append("\n\n");

            sb.Append("## Step 3-cont: 根据用户回复更新 chosen interpretation\n\n");
            sb.Append("用户回复解决了上一轮的哪些歧义?(明确对应到上一轮 <previous_intent_challenge> 里的 Step 2 歧义词)\n");
            sb.Append("- 上一轮歧义词 1: \"{词}\" → 用户回复中的解答: \"{引用}\" → 现在明确为: {具体含义}\n");
            sb.Append("- ...\n\n");
            sb.Append("更新后的 chosen interpretation: {具体、可操作}\n");
            sb.Append("更新后的关键假设: {新的假设, 如果这个假设错答案会完全不同}\n\n");
            sb.Append("如果用户回复**与上一轮 Interpretations 完全无关**(话题跳变), 明确输出:\n");
            sb.Append("[TOPIC CHANGE DETECTED]\n");
            sb.Append("此时工程侧会重新走完整 Node A, 无需继续本 Continuation 块。\n\n");

            sb.Append("## Step 4-cont: 澄清决策(组合触发逻辑不变)\n\n");
            sb.Append("根据更新后的 chosen 判断维度 A/B/C/D:\n");
            sb.Append("- A: yes/no\n");
            sb.Append("- B: severe/minor\n");
            sb.Append("- C: destructive/safe\n");
            sb.Append("- D: inferred/verbatim(引用用户**本轮回复 + 上一轮 message**里的关键词判断)\n\n");
            sb.Append("结论:\n");
            sb.Append("- [ ] 命中组合 1(A=yes 且 B=severe)→ 继续反问\n");
            sb.Append("- [ ] 命中组合 2(C=destructive 且 D=inferred)→ 继续反问\n");
            sb.Append("- [ ] 都不命中 → 开始调工具或直接回答\n\n");

            sb.Append("## Step 5-cont: Self-Consistency Check(精简版, 3 条)\n\n");
            sb.Append(SelfChallengeConfig.ConsistencyCorrectionOpenMarker).Append("\n");
            sb.Append("- 一致性 1(用户回复是否真的解决了 Step 2 里的歧义?): PASS / FAIL\n");
            sb.Append("- 一致性 2(chosen 是否与用户回复一致?): PASS / FAIL\n");
            sb.Append("- 一致性 3(D 判断是否与用户本轮+上一轮 message 里的实际词汇一致?): PASS / FAIL\n\n");
            sb.Append("如果有 FAIL: Corrected judgement\n");
            sb.Append("如果全部 PASS: [Consistent]\n");
            sb.Append(SelfChallengeConfig.ConsistencyCorrectionCloseMarker).Append("\n\n");

            sb.Append(SelfChallengeConfig.NodeAContinuationCloseMarker).Append("\n");

            return sb.ToString();
        }

        /// <summary>
        /// 构造 Node A 结构校验失败后的 correction retry instruction。
        /// <para>
        /// 使用场景: <see cref="IntentChallengeParser.Parse"/> 返回 Success=false, 需要触发独立小会话让 LLM 重做。
        /// 用途 = Stage 3 NodeACorrectionRetryClient。
        /// </para>
        /// </summary>
        /// <param name="issues">上一次输出的具体结构问题列表</param>
        /// <param name="isContinuation">是否是 Continuation 模式的 retry</param>
        public static string BuildCorrectionRetryInstruction(System.Collections.Generic.IReadOnlyList<string> issues, bool isContinuation)
        {
            var sb = new StringBuilder(1024);
            sb.Append("[SYSTEM] Your ")
              .Append(isContinuation ? "intent_challenge_continuation" : "intent_challenge")
              .Append(" block failed structural validation:\n");

            if (issues != null)
            {
                foreach (var issue in issues)
                {
                    if (!string.IsNullOrEmpty(issue))
                        sb.Append("- ").Append(issue).Append('\n');
                }
            }

            sb.Append('\n');
            if (isContinuation)
            {
                sb.Append("Redo the intent_challenge_continuation, ensuring Step 3-cont / 4-cont / 5-cont + <consistency_correction> block are all present.\n");
            }
            else
            {
                sb.Append("Redo the intent_challenge, ensuring all 5 Steps + <consistency_correction> block are present.\n");
            }
            sb.Append("Do not skip any Step and do not add prose outside these structured sections.\n");

            // L4-A HARD CONSTRAINT: 输出必须以 open marker 开头、close marker 结尾, 消除 prose 前缀导致 FinalizeContent state=None
            sb.Append("\n[HARD CONSTRAINT]\n");
            sb.Append("Your ENTIRE output must be wrapped in the markers below — no prose before the opening marker or after the closing marker:\n");
            if (isContinuation)
            {
                sb.Append(SelfChallengeConfig.NodeAContinuationOpenMarker).Append(" ... ")
                  .Append(SelfChallengeConfig.NodeAContinuationCloseMarker).Append('\n');
            }
            else
            {
                sb.Append(SelfChallengeConfig.NodeAOpenMarker).Append(" ... ")
                  .Append(SelfChallengeConfig.NodeACloseMarker).Append('\n');
            }
            sb.Append("Start your response with the opening marker and end with the closing marker. Any text outside these markers will cause validation failure.\n");

            return sb.ToString();
        }
    }
}
