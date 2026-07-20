using System;
using System.Collections.Generic;
using AgentCore.Editor.LLM;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Core
{
    /// <summary>
    /// AgentLoop 的 ask_user 挂起-唤醒分部：Agent 通过 <c>ask_user</c> 工具中途向用户提问时，
    /// loop 截断结束、进入 <see cref="AgentState.WaitingForUserInput"/>；用户应答后唤醒 loop 继续。
    /// <para>
    /// 复刻 <c>WaitingCompilation</c> 挂起-唤醒范式（AgentLoop.DomainReload.cs），与 SelfChallenge 完全无关。
    /// </para>
    /// <para>关键设计（R1，代码实证定案）：ask_user 工具的占位 tool_result 会照常写入历史
    /// （BuildToolMessagesWithCompressionAsync 无差别写入），故唤醒时**不能再补第二个 result**（双 result 非法）；
    /// 改为追加一条 user 消息携带答案，再 TriggerResumeLLMCall。</para>
    /// </summary>
    public partial class AgentLoop
    {
        /// <summary>
        /// 当前挂起等待用户应答的 ask_user tool_call ID。非 null 表示 loop 正等待用户应答。
        /// 由 ExecuteToolCallsAsync 在检测到 IsAwaitingUserInput 时设置；Runner 据此截断退出循环；
        /// ResumeFromUserInput 消费后清空。
        /// </summary>
        private string _pendingUserInputToolCallId;

        /// <summary>
        /// 挂起时的问题与选项（用于 UI 面板 reload 后重建 / 状态持久化）。
        /// </summary>
        private string _pendingUserInputQuestion;
        private List<string> _pendingUserInputOptions;

        /// <summary>
        /// ask_user 触发事件：loop 层请求 UI 弹出选项面板。
        /// 参数：(toolCallId, question, options)。ChatWindow 订阅并渲染面板。
        /// 主线程 marshal 由订阅方负责。
        /// </summary>
        public event Action<string, string, List<string>> OnUserQueryRaised;

        /// <summary>是否正在等待用户应答（供 UI / 外部查询）。</summary>
        public bool IsWaitingForUserInput => !string.IsNullOrEmpty(_pendingUserInputToolCallId);

        /// <summary>当前挂起的问题文本（可能为 null）。</summary>
        public string PendingUserInputQuestion => _pendingUserInputQuestion;

        /// <summary>当前挂起的候选选项（可能为 null）。</summary>
        public IReadOnlyList<string> PendingUserInputOptions => _pendingUserInputOptions;

        /// <summary>
        /// 记录挂起请求的问题/选项（由 ExecuteToolCallsAsync 调用，配合 OnUserQueryRaised）。
        /// </summary>
        private void RecordPendingUserQuery(string toolCallId, string question, List<string> options)
        {
            _pendingUserInputToolCallId = toolCallId;
            _pendingUserInputQuestion = question;
            _pendingUserInputOptions = options;

            // 持久化，供 domain reload 后重建面板
            try
            {
                DomainReloadState.instance.SavePendingAskUser(toolCallId, question, options);
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore][ask_user] Failed to persist pending query: {ex.Message}");
            }
        }

        /// <summary>
        /// 用户应答 ask_user 后调用：把答案作为一条 user 消息追加到历史，唤醒 loop 继续。
        /// </summary>
        /// <param name="answer">用户的答案文本（点选的预设选项文本，或自行输入的文字）。</param>
        /// <param name="wasPresetOption">是否点选了预设选项（false = 自己描述）。</param>
        public void ResumeFromUserInput(string answer, bool wasPresetOption)
        {
            if (string.IsNullOrEmpty(_pendingUserInputToolCallId))
            {
                AgentCoreLog.Warning("[AgentCore][ask_user] ResumeFromUserInput called but no pending user query. Ignoring.");
                return;
            }

            if (answer == null) answer = "";

            var pendingId = _pendingUserInputToolCallId;
            var question = _pendingUserInputQuestion ?? "";
            var source = wasPresetOption ? "选择了预设选项" : "自行输入";

            AgentCoreLog.Info($"[AgentCore][ask_user] ResumeFromUserInput (toolCallId={pendingId}, preset={wasPresetOption}). Answer: {answer}");

            // R1 定案：占位 tool_result 已在历史中，此处追加一条 user 消息携带答案（不补第二个 result）。
            var continuation =
                $"[针对你刚才通过 ask_user 提出的问题：\"{question}\"] 我{source}：{answer}\n请据此继续之前的任务。";
            _messages.Add(ChatMessage.User(continuation));

            // 清空挂起状态
            _pendingUserInputToolCallId = null;
            _pendingUserInputQuestion = null;
            _pendingUserInputOptions = null;
            try { DomainReloadState.instance.ClearPendingAskUser(); }
            catch (Exception ex) { AgentCoreLog.Warning($"[AgentCore][ask_user] ClearPendingAskUser failed: {ex.Message}"); }

            // 唤醒 loop（复用 domain-reload 的通用恢复入口：sanitize 配对 + 新 assistant turn + RunToolCallLoopAsync）
            TriggerResumeLLMCall();
        }

        /// <summary>
        /// 放弃当前挂起的用户提问（窗口关闭 / 会话切换等）。仅清状态，不唤醒 loop。
        /// 历史里那个 ask_user tool_call 的占位 result 已写入，保持合法；后续若被 resume 会走 sanitize。
        /// </summary>
        public void AbandonPendingUserInput()
        {
            if (string.IsNullOrEmpty(_pendingUserInputToolCallId)) return;
            AgentCoreLog.Info($"[AgentCore][ask_user] AbandonPendingUserInput (toolCallId={_pendingUserInputToolCallId}).");
            _pendingUserInputToolCallId = null;
            _pendingUserInputQuestion = null;
            _pendingUserInputOptions = null;
            try { DomainReloadState.instance.ClearPendingAskUser(); }
            catch (Exception ex) { AgentCoreLog.Warning($"[AgentCore][ask_user] ClearPendingAskUser failed: {ex.Message}"); }
        }

        /// <summary>
        /// domain reload 后恢复挂起的 ask_user 状态（由 ChatWindow 在会话恢复后调用）。
        /// 仅恢复内存中的挂起标志，使 loop 知道自己仍在等待用户应答；UI 面板由 ChatWindow 侧重建。
        /// </summary>
        /// <returns>true 表示确有挂起状态被恢复。</returns>
        public bool RestorePendingUserInputFromReload()
        {
            try
            {
                var state = DomainReloadState.instance;
                if (!state.HasPendingAskUser) return false;

                _pendingUserInputToolCallId = state.PendingAskUserToolCallId;
                _pendingUserInputQuestion = state.PendingAskUserQuestion;
                _pendingUserInputOptions = state.PendingAskUserOptions;
                SetState(AgentState.WaitingForUserInput);
                AgentCoreLog.Info($"[AgentCore][ask_user] Restored pending query after reload (toolCallId={_pendingUserInputToolCallId}).");
                return true;
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore][ask_user] RestorePendingUserInputFromReload failed: {ex.Message}");
                return false;
            }
        }
    }
}
