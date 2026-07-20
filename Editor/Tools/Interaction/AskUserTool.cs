using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Tools.Safety;
using Newtonsoft.Json.Linq;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Tools.Interaction
{
    /// <summary>
    /// 元工具：允许 Agent 在任务执行中途主动向用户提出决策性问题，收束实现方向。
    /// <para>
    /// 设计目的：当 Agent 遇到影响实现方向的岔路口（多种合理方案、需求有歧义、继续做必须基于假设）时，
    /// 应优先调用本工具让用户拍板，而不是凭幻觉/猜测往可能错误的方向无限执行。
    /// </para>
    /// <para>
    /// 挂起-唤醒机制（与 SelfChallenge 模块完全无关，复刻 WaitingCompilation 范式）：
    /// 本工具是纯函数——只解析参数、返回带 <see cref="ToolResult.IsAwaitingUserInput"/> 标志的结果，
    /// 不接触 UI、不阻塞。loop 层（ExecuteToolCallsAsync）检测到该标志后，注册挂起请求到 ChatWindow 面板、
    /// 切到 <c>WaitingForUserInput</c> 状态、截断退出循环（loop 结束，不空等）。
    /// 用户事后应答（点选项/自己描述）→ AgentLoop.ResumeFromUserInput 把答案作为本次 tool_call 的
    /// 真实 tool_result 写入 → TriggerResumeLLMCall 唤醒 loop 继续。跨 domain reload 存活。
    /// </para>
    /// </summary>
    [AgentTool("ask_user",
        Description = "Ask the user a decision-shaping question MID-TASK when the path forward is genuinely ambiguous and guessing risks going the wrong direction. " +
            "Provide a clear 'question' and up to 4 candidate 'options' the user can pick from with a button; the UI auto-appends a 'type my own answer' entry so the user can also free-text. " +
            "USE FOR: multiple reasonable approaches with meaningful trade-offs, ambiguous requirements, or a decision that would otherwise force you to assume. " +
            "DO NOT USE FOR: things you can determine yourself, trivial choices, or dangerous-action confirmation (that is handled separately). " +
            "The agent loop suspends after this call and resumes with the user's answer as this tool's result — treat that answer as the user's directive and proceed accordingly.",
        Category = "Interaction",
        RequiresMainThread = false,
        RiskLevel = ToolRiskLevel.Low,
        Capabilities = ToolCapability.None,
        Visibility = ToolVisibility.AlwaysVisible)]
    public class AskUserTool : IAgentTool
    {
        private const int MaxOptions = 4;

        private static readonly JObject _parametersSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""question"": {
                    ""type"": ""string"",
                    ""description"": ""The decision-shaping question to ask the user. Be specific about what you need decided and why it matters to the task.""
                },
                ""options"": {
                    ""type"": ""array"",
                    ""items"": { ""type"": ""string"" },
                    ""description"": ""Up to 4 candidate answers, each rendered as a clickable button. Omit or leave empty for an open-ended question (user free-texts). Do NOT enumerate options inside the question text — put each option here.""
                }
            },
            ""required"": [""question""]
        }");

        public ToolMetadata Metadata => new ToolMetadata(
            name: "ask_user",
            description: "Ask the user a decision-shaping question mid-task to converge on direction instead of guessing.",
            category: "Interaction",
            parametersSchema: _parametersSchema,
            requiresMainThread: false);

        public Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            string question;
            try
            {
                question = ToolHelpers.GetRequiredString(parameters, "question");
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(
                    $"ask_user requires a 'question' parameter: {ex.Message}", sw.Elapsed.TotalMilliseconds));
            }

            // 读取 options（可空），截断到 MaxOptions，去空白项
            var options = new List<string>();
            var arr = ToolHelpers.GetOptionalArray(parameters, "options");
            if (arr != null)
            {
                foreach (var tok in arr)
                {
                    var s = tok?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(s))
                        options.Add(s);
                    if (options.Count >= MaxOptions)
                        break;
                }
            }

            // 纯函数：返回挂起标志 + 问题/选项。真正的 UI 交互 + loop 挂起/唤醒由 loop 层接管。
            // 此处的 Output 只是占位（正常情况下 loop 截断后不会把它作为最终 tool_result 发给 LLM；
            // 用户应答后由 ResumeFromUserInput 写入真实答案。仅无 UI 兜底时会用到）。
            var result = ToolResult.Ok(
                "[ask_user] 已向用户提出问题，正在等待应答。用户的回答将作为随后的一条 user 消息到达——请依据那条消息继续，不要在此凭空假设答案。",
                sw.Elapsed.TotalMilliseconds);
            result.IsAwaitingUserInput = true;
            result.AskUserQuestion = question;
            result.AskUserOptions = options;

            AgentCoreLog.Info($"[AgentCore][ask_user] Question raised, loop will suspend: {question}");
            return Task.FromResult(result);
        }
    }
}
