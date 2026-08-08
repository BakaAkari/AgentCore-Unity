using System;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentCore.Editor.Core.SelfChallenge
{
    /// <summary>
    /// v1.14.10: 通用的"提示词回声"净化层。
    /// <para>
    /// 背景（根因见 <see cref="IntentChallengePromptBuilder"/> / <see cref="AnswerChallengePromptBuilder"/>
    /// 的对应修复注释）：部分模型（实测 DeepSeek-V4-Flash）在看到系统提示词里的框架性说明文字后，
    /// 倾向于先逐字复述一遍才开始正式输出结构化块。这段复述文本出现在 <c>&lt;intent_challenge&gt;</c>
    /// 等标签**之外**，属于设计上"标签外都是 visible 内容"的正常路径，因此不会被
    /// <see cref="IntentChallengeStreamExtractor"/> 的标签剥离逻辑拦截，直接泄漏到用户可见的正文里。
    /// </para>
    /// <para>
    /// 设计原则（不做"每个模型一个适配器类"）：
    /// 这类问题的本质不是"某个模型特殊"，而是"任何模型都可能把我们自己写的指令文字复述回来"——
    /// 检测逻辑完全不需要知道是哪个模型说的话，只需要知道"这段文字是不是我们自己注入的提示词"。
    /// 因此本类<b>不按模型名分支</b>，对所有模型统一生效；不含此类文本的模型输出不会误触发。
    /// </para>
    /// <para>
    /// 参考文本来源：直接引用 <see cref="IntentChallengePromptBuilder"/> /
    /// <see cref="AnswerChallengePromptBuilder"/> 里最容易被复述的框架性说明句（不是整段模板——
    /// 标签内的正常 Step 内容合法出现在其块内，只有标签外的独立说明句才是回声嫌疑对象）。
    /// 提示词措辞以后再改动时，记得同步更新这里的参考句（两处改动通常同批提交，diff 能互相印证）。
    /// </para>
    /// <para>
    /// 匹配策略（保守优先，观察效果再收紧）：每条参考句转换为"空白容忍"的正则（内部连续空白
    /// 统一匹配为 <c>\s+</c>），对齐模型输出换行方式与常量定义不完全一致的情况；只做逐句精确
    /// 匹配，不做模糊/语义相似度，避免误杀用户输入里恰好措辞相近但语义不同的正常内容——这类
    /// 冲突概率极低，因为参考句都是特定工程话术，不是自然语言里会被随口说出的句子。
    /// </para>
    /// </summary>
    public static class PromptEchoScrubber
    {
        /// <summary>
        /// 已知会注入给 LLM 的框架性说明句（去 marker、去动态数据后的静态部分）。
        /// 与 PromptBuilder 里的实际 <c>sb.Append(...)</c> 字符串保持逐字一致。
        /// </summary>
        private static readonly string[] ReferenceEchoLines =
        {
            // IntentChallengePromptBuilder.BuildFullNodeAInstruction 框架说明
            "在开始规划任何工具调用前, 你必须先完成一次\"需求自我挑战\"。禁止跳过、禁止简写。",
            "你的输出必须以下面的完整块开头, 完成后才能继续正常输出或调用工具。",
            "Step 5 完成后的下一步动作",
            "若上面的结论是\"都不命中, 直接执行\"→ 紧接着开始调工具或直接回答, 不要重复输出这条规则本身",
            "若上面的结论是\"命中组合 X\"→ 紧接着直接输出 `[CLARIFICATION NEEDED]` 反问, 禁止调工具, 不要重复输出这条规则本身",

            // IntentChallengePromptBuilder.BuildContinuationNodeAInstruction 框架说明
            "上一轮你判定需要向用户反问, 用户现在给出了回复。你需要根据用户回复更新 chosen interpretation。",
            "Continuation 模式只做 Step 3-cont / 4-cont / 5-cont 精简版, 禁止重做 Step 1 / Step 2。",
            "你的输出必须以下面完整块开头:",

            // AnswerChallengePromptBuilder.BuildReviewerInstruction 框架说明
            "你现在的角色是 skeptical reviewer。你默认认为 <draft_answer> 里有错误, 你的任务是找出这些错误。从不假设 draft 是对的。你不是 Agent 本人, 你是一个第三方审查员, 被雇来找 Agent 的问题。",
            "本 Step 4 输出的禁止事项",
        };

        /// <summary>预编译的空白容忍正则，与 <see cref="ReferenceEchoLines"/> 一一对应，懒加载。</summary>
        private static readonly Regex[] CompiledPatterns = BuildPatterns();

        private static Regex[] BuildPatterns()
        {
            var patterns = new Regex[ReferenceEchoLines.Length];
            for (int i = 0; i < ReferenceEchoLines.Length; i++)
            {
                // 逐字符转义原文, 再把"连续空白"替换为 \s+, 使正则对换行/多空格宽容匹配。
                string escaped = Regex.Escape(ReferenceEchoLines[i]);
                string tolerant = Regex.Replace(escaped, @"(\\\s)+", @"\s+");
                patterns[i] = new Regex(tolerant, RegexOptions.Compiled | RegexOptions.Singleline);
            }
            return patterns;
        }

        /// <summary>
        /// 对流式抽取器即将释放的 visible 文本做回声检测与剔除。
        /// </summary>
        /// <param name="text">标签外、即将释放给 UI 的 visible 文本片段。</param>
        /// <returns>剔除已识别回声句后的文本；若无命中原样返回。</returns>
        public static string Scrub(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            string result = text;
            foreach (var pattern in CompiledPatterns)
            {
                if (pattern.IsMatch(result))
                {
                    result = pattern.Replace(result, string.Empty);
                }
            }
            return result;
        }
    }
}
