using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Config;
using AgentCore.Editor.Core.SelfChallenge;
using AgentCore.Editor.LLM;
using UnityEngine;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Core
{
    /// <summary>
    /// AgentLoop 的 Self-Challenge 集成层(Phase 9 §3.y)。
    /// <para>
    /// 承载 Node A(Intent Self-Challenge)+ Node B(Answer Self-Challenge)的完整生命周期:
    /// prompt 注入、流式抽取器状态管理、结构校验、correction retry、reviewer 独立 LLM 调用、
    /// 主对话历史清理(v0.10 §0.6)、状态机分派(WaitingForClarification)。
    /// </para>
    /// <para>
    /// **v1.4.9 骨架 → v1.5.0-alpha 完整实施**: 依据设计文档 [`prompt-layer-hallucination-hardening-plan.md`] v0.10 定稿。
    /// </para>
    /// </summary>
    public partial class AgentLoop
    {
        #region 私有字段 — SelfChallenge 相关

        /// <summary>
        /// Node A 完整模式 stream extractor。
        /// <para>
        /// **常驻实例**: 与 <see cref="_nodeAEnabledThisTurn"/> 解耦, 每轮 LLM 调用前都会 Reset。
        /// 无论 Node A 是否启用(即使 skip / disabled), 都会参与流式剥离,
        /// 防止 LLM 因上文学习或幻觉而泄漏 <c>&lt;intent_challenge&gt;</c> 块到 UI 气泡。
        /// </para>
        /// <para>
        /// 结构校验 + IntentChallengeCompleted 事件仅在 <see cref="_nodeAEnabledThisTurn"/> 为 true
        /// 且当前模式匹配(即 <see cref="_nodeATriggerContinuation"/> = false)时触发。
        /// </para>
        /// </summary>
        private IntentChallengeStreamExtractor _intentChallengeStreamExtractor;

        /// <summary>
        /// Node A Continuation 模式 stream extractor(常驻兜底)。
        /// <para>
        /// 与 <see cref="_intentChallengeStreamExtractor"/> 并行剥离, marker 不同(<c>&lt;intent_challenge_continuation&gt;</c>)。
        /// 结构校验 + 事件仅在 <see cref="_nodeAEnabledThisTurn"/> 为 true 且 <see cref="_nodeATriggerContinuation"/> = true 时触发。
        /// </para>
        /// </summary>
        private IntentChallengeStreamExtractor _intentChallengeContinuationExtractor;

        /// <summary>当前 assistant turn 的 Node B stream extractor。</summary>
        private AnswerChallengeStreamExtractor _answerChallengeStreamExtractor;

        /// <summary>当前 assistant turn 正在构建的 SelfChallengeData(实时填充, 最终附加到 ConversationTurn)。</summary>
        private SelfChallengeData _currentSelfChallengeData;

        /// <summary>当前 assistant turn 关联的 turnId(供 AgentEvent 分派 UI 使用)。</summary>
        private string _currentSelfChallengeTurnId;

        /// <summary>本轮是否已发过 IntentChallengeCompleted 事件, 避免重复触发。</summary>
        private bool _intentChallengeEmittedThisTurn;

        /// <summary>当前进入 WaitingForClarification 状态时, 记录上一次 Node A 的 turn ID(用于 Continuation 引用)。</summary>
        private string _pendingClarificationPreviousTurnId;

        /// <summary>Continuation 模式下缓存上一轮的完整 &lt;intent_challenge&gt; 块原文。</summary>
        private string _pendingClarificationPreviousBlock;

        /// <summary>Continuation 模式下缓存上一轮用户 message 原文。</summary>
        private string _pendingClarificationPreviousUserMessage;

        /// <summary>Continuation 模式下缓存上一轮 Agent 的反问 message 原文。</summary>
        private string _pendingClarificationMessage;

        /// <summary>Node A 触发时是否使用 Continuation 模式(本轮 SendMessageAsync 计算的决策)。</summary>
        private bool _nodeATriggerContinuation;

        /// <summary>本轮 Node A 是否已启用(通过 skip 规则和 settings 计算)。</summary>
        private bool _nodeAEnabledThisTurn;

        #endregion

        #region 生命周期钩子

        /// <summary>
        /// 每次新一轮 LLM 调用开始前, 重置 SelfChallenge 运行时状态。
        /// 由 <see cref="CallLLMStreamAsync"/> 调用。
        /// <para>
        /// v1.5.0 修复: extractor 常驻实例, 若未初始化在此处懒创建。
        /// 与 <see cref="_nodeAEnabledThisTurn"/> 解耦, 使得即使 Node A 被 skip 也能兜底剥离
        /// LLM 意外泄漏的 <c>&lt;intent_challenge&gt;</c> / <c>&lt;intent_challenge_continuation&gt;</c> 块。
        /// </para>
        /// </summary>
        private void ResetSelfChallengeExtractorsForNewRound()
        {
            _intentChallengeEmittedThisTurn = false;

            if (_intentChallengeStreamExtractor == null)
                _intentChallengeStreamExtractor = new IntentChallengeStreamExtractor(IntentChallengeStreamExtractor.Mode.Full);
            else
                _intentChallengeStreamExtractor.Reset();

            if (_intentChallengeContinuationExtractor == null)
                _intentChallengeContinuationExtractor = new IntentChallengeStreamExtractor(IntentChallengeStreamExtractor.Mode.Continuation);
            else
                _intentChallengeContinuationExtractor.Reset();

            _answerChallengeStreamExtractor?.Reset();
        }

        /// <summary>
        /// 在处理新的 user message 前, 依据 settings 与 skip 规则决定本轮 Node A 是否启用及模式。
        /// 由 <see cref="SendMessageAsync"/> 调用。
        /// </summary>
        /// <param name="userMessage">用户输入的原始 message。</param>
        /// <returns>SelfChallengeData 实例(总是返回非 null, skip 时 NodeATriggered=false + NodeASkipReason 有值)</returns>
        internal SelfChallengeData PrepareSelfChallengeDataForNewTurn(string userMessage)
        {
            var settings = AgentCoreSettings.instance;
            _currentSelfChallengeData = new SelfChallengeData();
            _nodeAEnabledThisTurn = false;
            _nodeATriggerContinuation = false;

            // Phase 9 v1.5.0-alpha 极简: 单一总开关控制 Node A + Node B
            if (!settings.selfChallengeEnabled)
            {
                _currentSelfChallengeData = null;
                return null;
            }

            // ADR: self-challenge-model-tier-escape — 高级模型具备 native reasoning,
            // 自挑战与其重复 → 逃逸 Node A, 依赖 native thinking。extractor 仍常驻兜底剥离。
            // 热插拔: 每轮实时读取 selfChallengeEscapeEnabled + ActiveModelConfig.ModelName, 不缓存。
            if (settings.selfChallengeEscapeEnabled &&
                ModelCapabilityDetector.HasNativeReasoning(ActiveModelConfig.ModelName))
            {
                _currentSelfChallengeData.NodeATriggered = false;
                _currentSelfChallengeData.NodeASkipReason = "模型具备原生推理";
                _nodeAEnabledThisTurn = false;
                AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore][SelfChallenge] Node A escaped: model '{ActiveModelConfig.ModelName}' has native reasoning.");
                return _currentSelfChallengeData;
            }

            // WaitingForClarification 状态下, 走 Continuation 模式
            bool isContinuation = CurrentState == AgentState.WaitingForClarification;

            // Skip 规则(纯格式识别, R1 + R3)
            if (SelfChallengeSkipRules.ShouldSkip(userMessage, out var skipReason))
            {
                _currentSelfChallengeData.NodeATriggered = false;
                _currentSelfChallengeData.NodeASkipReason = skipReason;
                _currentSelfChallengeData.IsNodeAContinuation = isContinuation;
                if (isContinuation)
                    _currentSelfChallengeData.PreviousTurnNodeAId = _pendingClarificationPreviousTurnId;
                return _currentSelfChallengeData;
            }

            _nodeAEnabledThisTurn = true;
            _nodeATriggerContinuation = isContinuation;
            if (isContinuation)
            {
                _currentSelfChallengeData.IsNodeAContinuation = true;
                _currentSelfChallengeData.PreviousTurnNodeAId = _pendingClarificationPreviousTurnId;
            }

            // v1.5.0 修复: extractor 由 ResetSelfChallengeExtractorsForNewRound() 常驻懒创建,
            // 此处不再按模式切换实例, 而是通过 _nodeATriggerContinuation 决定哪个 extractor 参与结构校验/事件。
            return _currentSelfChallengeData;
        }

        /// <summary>
        /// 生成本轮应追加的 Node A instruction 文本。
        /// 若 Node A 未启用返回 null。
        /// </summary>
        internal string BuildNodeAInstructionForCurrentTurn()
        {
            if (!_nodeAEnabledThisTurn) return null;

            if (_nodeATriggerContinuation)
            {
                return IntentChallengePromptBuilder.BuildContinuationNodeAInstruction(
                    _pendingClarificationPreviousUserMessage ?? string.Empty,
                    _pendingClarificationPreviousBlock ?? string.Empty,
                    _pendingClarificationMessage ?? string.Empty);
            }

            return IntentChallengePromptBuilder.BuildFullNodeAInstruction();
        }

        /// <summary>
        /// 当前 turn 的 SelfChallengeData(可能为 null); 供 turn 完成时挂载。
        /// </summary>
        internal SelfChallengeData GetCurrentSelfChallengeData() => _currentSelfChallengeData;

        /// <summary>
        /// 设置当前 turn 的 turnId(在 SendMessageAsync 创建 assistantTurn 后调用)。
        /// </summary>
        internal void SetCurrentSelfChallengeTurnId(string turnId)
        {
            _currentSelfChallengeTurnId = turnId;
        }

        #endregion

        #region Node A 流式抽取器接线

        /// <summary>
        /// 供 <see cref="HandleContentToken"/> 调用: 把 token 先过 Node A extractor(Full + Continuation 并行),
        /// 再过 Node B extractor, 最后剩余 visible 部分交给 VisiblePlanningTraceExtractor。
        /// <para>
        /// v1.5.0 修复(§防止 challenge 块泄漏到 UI):
        /// 两个 Node A extractor(Full / Continuation)**始终参与流式剥离**, 与 <see cref="_nodeAEnabledThisTurn"/> 解耦,
        /// 即使 Node A 被 skip 或整体 disabled, 也能防御性剥离 LLM 因上文学习而泄漏的 challenge 块。
        /// 结构校验 + IntentChallengeCompleted 事件仅在 Node A 启用且模式匹配时触发。
        /// </para>
        /// </summary>
        /// <param name="token">原始 content token。</param>
        /// <param name="assistantTurn">当前 assistant turn。</param>
        /// <returns>剥离 challenge 块后的 visible token(可继续送给 VisiblePlanningTraceExtractor)。</returns>
        private string ProcessTokenThroughSelfChallengeExtractors(string token, ConversationTurn assistantTurn)
        {
            if (string.IsNullOrEmpty(token)) return string.Empty;

            string visible = token;

            // Node A 完整模式抽取(常驻兜底)
            if (_intentChallengeStreamExtractor != null &&
                _intentChallengeStreamExtractor.State != IntentChallengeExtractorState.Invalid)
            {
                var deltaFull = _intentChallengeStreamExtractor.Append(visible);
                visible = deltaFull.VisibleContent;

                // 仅当本轮启用 Node A 且为 Full 模式时才触发结构校验 + 事件
                if (_nodeAEnabledThisTurn && !_nodeATriggerContinuation &&
                    deltaFull.State == IntentChallengeExtractorState.Completed &&
                    !_intentChallengeEmittedThisTurn)
                {
                    OnNodeAExtractorCompleted(_intentChallengeStreamExtractor.ExtractedBlock, assistantTurn);
                }
            }

            // Node A Continuation 模式抽取(常驻兜底)
            if (_intentChallengeContinuationExtractor != null &&
                _intentChallengeContinuationExtractor.State != IntentChallengeExtractorState.Invalid)
            {
                var deltaCont = _intentChallengeContinuationExtractor.Append(visible);
                visible = deltaCont.VisibleContent;

                if (_nodeAEnabledThisTurn && _nodeATriggerContinuation &&
                    deltaCont.State == IntentChallengeExtractorState.Completed &&
                    !_intentChallengeEmittedThisTurn)
                {
                    OnNodeAExtractorCompleted(_intentChallengeContinuationExtractor.ExtractedBlock, assistantTurn);
                }
            }

            // Node B 抽取(dead code 保留兜底; Node B 实际走独立 ChatCompletionAsync 非流式路径)
            if (_answerChallengeStreamExtractor != null &&
                _answerChallengeStreamExtractor.State != AnswerChallengeExtractorState.Invalid)
            {
                var deltaB = _answerChallengeStreamExtractor.Append(visible);
                visible = deltaB.VisibleContent;
            }

            return visible;
        }

        private void OnNodeAExtractorCompleted(string block, ConversationTurn assistantTurn)
        {
            _intentChallengeEmittedThisTurn = true;

            if (_currentSelfChallengeData == null)
                _currentSelfChallengeData = new SelfChallengeData();

            IntentChallengeParseResult parseResult;
            if (_nodeATriggerContinuation)
                parseResult = IntentChallengeParser.ParseContinuation(block, _currentSelfChallengeData);
            else
                parseResult = IntentChallengeParser.Parse(block, _currentSelfChallengeData);

            if (parseResult.TopicChangeDetected)
            {
                AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore][SelfChallenge] Continuation: [TOPIC CHANGE DETECTED], will fall back to full Node A next turn.");
                // 清空 WaitingForClarification 上下文, 恢复 Idle 状态; 由上层完成状态清理
                _pendingClarificationPreviousBlock = null;
                _pendingClarificationPreviousUserMessage = null;
                _pendingClarificationMessage = null;
                _pendingClarificationPreviousTurnId = null;
            }

            if (!parseResult.Success)
            {
                AgentCoreLog.Warning($"[AgentCore][SelfChallenge] Node A structural validation failed with {parseResult.Issues.Count} issue(s). " +
                                 "Stage 3 correction retry will kick in.");
                _currentSelfChallengeData.NodeARetryCount = 0;
                _currentSelfChallengeData.NodeAOutput = block;
                // 详细 issues 存进临时字段, 供 Stage 3 correction retry 消费
                _pendingNodeAValidationIssues = parseResult.Issues;
            }
            else
            {
                _pendingNodeAValidationIssues = null;
            }

            // 挂到 turn
            if (assistantTurn != null)
            {
                assistantTurn.SelfChallenge = _currentSelfChallengeData;
            }

            // 发事件
            EmitEvent(AgentEvent.IntentChallengeCompleted(_currentSelfChallengeData, _currentSelfChallengeTurnId ?? assistantTurn?.Id));
        }

        /// <summary>
        /// 结构校验失败时保存的 issues, 供 Stage 3 correction retry 使用。
        /// </summary>
        private IReadOnlyList<string> _pendingNodeAValidationIssues;

        #endregion

        #region Node A Correction Retry(Stage 3)

        /// <summary>
        /// Node A 结构校验失败时, 触发独立小会话让 LLM 重做 Node A。
        /// </summary>
        /// <param name="userMessage">当前用户 message。</param>
        /// <param name="assistantTurn">当前 assistant turn。</param>
        /// <param name="issues">上一次输出的结构问题列表。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>成功修复返回 true; 失败(超出重试上限)返回 false。</returns>
        private async Task<bool> TryNodeACorrectionRetryAsync(
            string userMessage,
            ConversationTurn assistantTurn,
            IReadOnlyList<string> issues,
            CancellationToken ct)
        {
            var settings = AgentCoreSettings.instance;
            int maxRetries = SelfChallengeConfig.NodeARetryMax;
            if (maxRetries == 0) return false;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                if (ct.IsCancellationRequested) return false;

                AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore][SelfChallenge] Node A correction retry attempt {attempt}/{maxRetries}");

                // 独立小会话: 只包含 [原始 user query, 上一次 Node A 输出, correction 指令]
                var retryMessages = new List<ChatMessage>
                {
                    ChatMessage.System("You are performing structural correction of a Node A Intent Self-Challenge block. Follow the format exactly."),
                    ChatMessage.User(userMessage),
                    ChatMessage.Assistant(_currentSelfChallengeData?.NodeAOutput ?? string.Empty),
                    ChatMessage.System(IntentChallengePromptBuilder.BuildCorrectionRetryInstruction(issues, _nodeATriggerContinuation))
                };

                try
                {
                    var retryResult = await _llmClient.ChatCompletionAsync(retryMessages, tools: null, ct: ct);
                    var retryMessage = retryResult?.GetMessage();
                    if (retryMessage == null || string.IsNullOrEmpty(retryMessage.Content))
                    {
                        AgentCoreLog.Warning($"[AgentCore][SelfChallenge] Node A retry attempt {attempt} returned empty content.");
                        continue;
                    }

                    string newBlock;
                    var finalizeResult = _nodeATriggerContinuation
                        ? IntentChallengeStreamExtractor.FinalizeContent(retryMessage.Content, IntentChallengeStreamExtractor.Mode.Continuation)
                        : IntentChallengeStreamExtractor.FinalizeContent(retryMessage.Content, IntentChallengeStreamExtractor.Mode.Full);

                    if (finalizeResult.State != IntentChallengeExtractorState.Completed)
                    {
                        AgentCoreLog.Warning($"[AgentCore][SelfChallenge] Node A retry attempt {attempt}: FinalizeContent state = {finalizeResult.State}");
                        continue;
                    }

                    newBlock = finalizeResult.ExtractedBlock;
                    var parseResult = _nodeATriggerContinuation
                        ? IntentChallengeParser.ParseContinuation(newBlock, _currentSelfChallengeData)
                        : IntentChallengeParser.Parse(newBlock, _currentSelfChallengeData);

                    _currentSelfChallengeData.NodeARetryCount = attempt;

                    if (parseResult.Success)
                    {
                        AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore][SelfChallenge] Node A retry attempt {attempt} succeeded.");
                        if (assistantTurn != null) assistantTurn.SelfChallenge = _currentSelfChallengeData;
                        EmitEvent(AgentEvent.IntentChallengeCompleted(_currentSelfChallengeData, _currentSelfChallengeTurnId ?? assistantTurn?.Id));
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    AgentCoreLog.Warning($"[AgentCore][SelfChallenge] Node A retry attempt {attempt} threw: {ex.Message}");
                }
            }

            // exhausted → fallback: 接受不完美的 output, 记录 retry count = maxRetries + 1
            AgentCoreLog.Warning($"[AgentCore][SelfChallenge] Node A correction retry exhausted after {maxRetries} attempts. Falling back to best-effort parse.");
            _currentSelfChallengeData.NodeARetryCount = maxRetries + 1;
            if (assistantTurn != null) assistantTurn.SelfChallenge = _currentSelfChallengeData;
            EmitEvent(AgentEvent.IntentChallengeCompleted(_currentSelfChallengeData, _currentSelfChallengeTurnId ?? assistantTurn?.Id));
            return false;
        }

        #endregion

        #region Node A 完成后的分派(WaitingForClarification 处理)

        /// <summary>
        /// Node A 完成结构校验后, 依据结论决定后续走向:
        /// - DirectExecute → 正常 tool loop
        /// - Combo1 / Combo2 → 进入 WaitingForClarification, 期望 LLM 输出 [CLARIFICATION NEEDED] 反问
        /// </summary>
        /// <param name="userMessage">本轮 user message。</param>
        /// <param name="assistantTurn">当前 assistant turn。</param>
        /// <param name="assistantMessageContent">LLM 完整输出(含 Node A 块 + 后续 draft)。</param>
        /// <returns>true 表示进入 WaitingForClarification, false 表示正常继续。</returns>
        private bool HandleNodeAConclusionForFinalResponse(
            string userMessage,
            ConversationTurn assistantTurn,
            string assistantMessageContent)
        {
            if (_currentSelfChallengeData == null || !_currentSelfChallengeData.NodeATriggered)
                return false;

            // ADR-17: AllowClarificationQuestions 现为常量 true, 不再检查(检查是死代码)

            if (_currentSelfChallengeData.Step4Conclusion == null ||
                _currentSelfChallengeData.Step4Conclusion == Step4Conclusion.DirectExecute)
                return false;

            // 命中组合 1/2: 期望 LLM 已输出 [CLARIFICATION NEEDED]
            if (!ContainsClarificationNeededMarker(assistantMessageContent))
            {
                AgentCoreLog.Warning("[AgentCore][SelfChallenge] Node A concluded to ask clarification, but LLM output does not contain [CLARIFICATION NEEDED] marker. Proceeding normally.");
                return false;
            }

            // 记录 Continuation 所需的上下文
            _pendingClarificationPreviousBlock = _currentSelfChallengeData.NodeAOutput;
            _pendingClarificationPreviousUserMessage = userMessage;
            _pendingClarificationMessage = ExtractClarificationMessage(assistantMessageContent);
            _pendingClarificationPreviousTurnId = _currentSelfChallengeTurnId ?? assistantTurn?.Id;

            // 进入 WaitingForClarification 状态
            SetState(AgentState.WaitingForClarification);

            AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore][SelfChallenge] Entered WaitingForClarification state. Waiting for user reply.");
            return true;
        }

        private static readonly Regex ClarificationNeededRegex = new Regex(
            @"\[CLARIFICATION\s+NEEDED\]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static bool ContainsClarificationNeededMarker(string content)
        {
            if (string.IsNullOrEmpty(content)) return false;
            return ClarificationNeededRegex.IsMatch(content);
        }

        private static string ExtractClarificationMessage(string content)
        {
            if (string.IsNullOrEmpty(content)) return string.Empty;
            var m = ClarificationNeededRegex.Match(content);
            if (!m.Success) return string.Empty;
            return content.Substring(m.Index).Trim();
        }

        /// <summary>
        /// 用户回复澄清后清除 pending clarification 上下文。
        /// 由 <see cref="SendMessageAsync"/> 在完成 Continuation 后调用。
        /// </summary>
        internal void ClearPendingClarificationIfNeeded()
        {
            if (CurrentState == AgentState.WaitingForClarification)
            {
                SetState(AgentState.Idle);
            }

            _pendingClarificationPreviousBlock = null;
            _pendingClarificationPreviousUserMessage = null;
            _pendingClarificationMessage = null;
            _pendingClarificationPreviousTurnId = null;
        }

        #endregion

        #region Node B(Answer Self-Challenge)

        /// <summary>
        /// Node B skip 判定(§1.3.1)。
        /// </summary>
        /// <returns>true 表示应 skip; skipReason 表示原因</returns>
        private static bool ShouldSkipNodeB(string response, out string skipReason)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                skipReason = SelfChallengeConfig.SkipReasonShortResponse;
                return true;
            }

            // Response ≤ 50 字符 → skip
            int nonWhitespaceLen = response.Count(c => !char.IsWhiteSpace(c));
            if (nonWhitespaceLen <= 50)
            {
                skipReason = SelfChallengeConfig.SkipReasonShortResponse;
                return true;
            }

            // 纯问题(反问 CLARIFICATION NEEDED 类)
            if (ClarificationNeededRegex.IsMatch(response))
            {
                skipReason = SelfChallengeConfig.SkipReasonPureQuestion;
                return true;
            }

            // 判断是否是纯问句结尾(简易启发式, 语义判断有限, 但设计文档接受这个精度)
            var trimmed = response.TrimEnd();
            if (trimmed.EndsWith("?") || trimmed.EndsWith("？"))
            {
                // 只有 1-2 个问号且总字符 <100 视为纯问题
                if (nonWhitespaceLen < 100)
                {
                    skipReason = SelfChallengeConfig.SkipReasonPureQuestion;
                    return true;
                }
            }

            skipReason = null;
            return false;
        }

        /// <summary>
        /// 触发 Node B(Answer Self-Challenge)独立 LLM 调用。
        /// 依据设计文档 §1.3.2: 组装 reviewer prompt, 独立调用 LLM, 解析 &lt;answer_challenge&gt; 块。
        /// </summary>
        /// <param name="draftResponse">当前生成的 draft final response。</param>
        /// <param name="userMessage">本轮原始 user message。</param>
        /// <param name="assistantTurn">当前 assistant turn。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>Node B verdict 与相关信息。若 skip 或 exhausted, 返回默认 PASS 效果</returns>
        private async Task<AnswerChallengeResult> InvokeNodeBAsync(
            string draftResponse,
            string userMessage,
            ConversationTurn assistantTurn,
            CancellationToken ct,
            SelfChallengeData turnBoundData)
        {
            var settings = AgentCoreSettings.instance;
            // ADR §3.4 B1: turn-bound 数据隔离 — 不再读写实例字段 _currentSelfChallengeData
            var nodeBData = turnBoundData ?? new SelfChallengeData();

            // Skip 判定
            if (ShouldSkipNodeB(draftResponse, out var skipReason))
            {
                nodeBData.NodeBTriggered = false;
                nodeBData.NodeBSkipReason = skipReason;
                if (assistantTurn != null) assistantTurn.SelfChallenge = nodeBData;
                EmitEvent(AgentEvent.AnswerChallengeCompleted(nodeBData, _currentSelfChallengeTurnId ?? assistantTurn?.Id));
                return new AnswerChallengeResult(NodeBVerdict.PASS, null, null, skipped: true);
            }

            nodeBData.NodeBTriggered = true;
            nodeBData.NodeBSkipReason = null;

            // 组装 reviewer messages(压缩后完整对话 + 强角色扮演, v0.7)
            var reviewMessages = BuildReviewerMessages(userMessage, draftResponse, nodeBData);

            // 触发调用 + retry
            int maxRetries = SelfChallengeConfig.NodeBRetryMax;
            AnswerChallengeResult resultPayload = null;

            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    if (attempt > 0)
                    {
                        // 追加 correction 指令
                        reviewMessages.Add(ChatMessage.System(
                            AnswerChallengePromptBuilder.BuildCorrectionRetryInstruction(_pendingNodeBValidationIssues)));
                    }

                    var response = await _llmClient.ChatCompletionAsync(reviewMessages, tools: null, ct: ct);
                    string content = response?.GetMessage()?.Content ?? string.Empty;

                    var final = AnswerChallengeStreamExtractor.FinalizeContent(content);
                    if (final.State != AnswerChallengeExtractorState.Completed)
                    {
                        AgentCoreLog.Warning($"[AgentCore][SelfChallenge] Node B attempt {attempt}: extractor state = {final.State}");
                        _pendingNodeBValidationIssues = new[] { "Missing <answer_challenge>...</answer_challenge> block." };
                        continue;
                    }

                    var parseResult = AnswerChallengeParser.Parse(final.ExtractedBlock, draftResponse, nodeBData);
                    nodeBData.NodeBRetryCount = attempt;

                    if (parseResult.Success)
                    {
                        resultPayload = new AnswerChallengeResult(
                            nodeBData.NodeBVerdict ?? NodeBVerdict.PASS,
                            nodeBData.ReviseIssues,
                            nodeBData.BlockVerifications,
                            skipped: false);
                        break;
                    }

                    _pendingNodeBValidationIssues = parseResult.Issues;
                }
                catch (Exception ex)
                {
                    AgentCoreLog.Warning($"[AgentCore][SelfChallenge] Node B attempt {attempt} threw: {ex.Message}");
                    _pendingNodeBValidationIssues = new[] { $"Exception during Node B reviewer call: {ex.Message}" };
                }
            }

            // 超过 retry 上限
            if (resultPayload == null)
            {
                AgentCoreLog.Warning($"[AgentCore][SelfChallenge] Node B correction retry exhausted after {maxRetries + 1} attempts. Accepting draft with best-effort parse.");
                nodeBData.NodeBRetryCount = maxRetries + 1;
                nodeBData.NodeBVerdict = NodeBVerdict.PASS; // Fallback: 接受 draft
                resultPayload = new AnswerChallengeResult(NodeBVerdict.PASS, null, null, skipped: false);
            }

            if (assistantTurn != null) assistantTurn.SelfChallenge = nodeBData;
            EmitEvent(AgentEvent.AnswerChallengeCompleted(nodeBData, _currentSelfChallengeTurnId ?? assistantTurn?.Id));

            return resultPayload;
        }

        /// <summary>
        /// 结构校验失败时保存的 issues, 供下轮 retry prompt 使用。
        /// </summary>
        private IReadOnlyList<string> _pendingNodeBValidationIssues;

        /// <summary>
        /// 组装 Reviewer 的 messages 列表: 压缩后主对话历史 + 角色扮演 system prompt + 结构化 payload。
        /// v0.10 §0.6: 主历史里的 &lt;intent_challenge&gt; 块已被剥离; Node A 关键假设通过独立的 reviewer prompt 携带。
        /// </summary>
        private List<ChatMessage> BuildReviewerMessages(string userMessage, string draftResponse, SelfChallengeData turnBoundData)
        {
            var settings = AgentCoreSettings.instance;
            int maxTokens = settings.maxContextTokens > 0
                ? settings.maxContextTokens
                : ContextWindowManager.GetModelMaxTokens(ActiveModelConfig.ModelName);
            int reserveTokens = settings.reserveResponseTokens;

            // 组装压缩后主对话历史(复用现有 TrimToFit; 主历史此时应已剥离 challenge 块)
            var compressedHistory = ContextWindowManager.TrimToFit(_messages, maxTokens, reserveTokens);

            var reviewMessages = new List<ChatMessage>(compressedHistory);

            // Reviewer 角色 prompt + payload
            // ADR §3.4 B1: 使用 turnBoundData 而非 _currentSelfChallengeData 实例字段
            var reviewerInstruction = AnswerChallengePromptBuilder.BuildReviewerInstruction(
                userMessage,
                draftResponse,
                turnBoundData?.NodeAOutput ?? string.Empty);

            reviewMessages.Add(ChatMessage.System(reviewerInstruction));
            return reviewMessages;
        }

        #endregion

        #region 主历史清理(v0.10 §0.6)

        /// <summary>
        /// 从 assistant message 的 Content 中剥离所有 challenge 块, 只保留可见部分。
        /// 供 <see cref="PrepareAssistantMessageForHistory"/> 调用。
        /// v0.10 §0.6: 主对话历史不携带 challenge 块以避免 token 无谓膨胀。
        /// </summary>
        private static string StripChallengeBlocks(string content)
        {
            if (string.IsNullOrEmpty(content)) return content;

            content = StripBlock(content, SelfChallengeConfig.NodeAOpenMarker, SelfChallengeConfig.NodeACloseMarker);
            content = StripBlock(content, SelfChallengeConfig.NodeAContinuationOpenMarker, SelfChallengeConfig.NodeAContinuationCloseMarker);
            content = StripBlock(content, SelfChallengeConfig.NodeBOpenMarker, SelfChallengeConfig.NodeBCloseMarker);

            // v1.14.10: 标签块本身已剥离, 但部分模型（实测 DeepSeek-V4-Flash）会在标签**之前**
            // 额外复述一遍系统提示词里的框架说明文字（根因见 PromptEchoScrubber 类注释）。
            // 这段复述文本落在标签外, StripBlock 不处理（设计上标签外都是合法 visible 内容）,
            // 这里用完整字符串（非流式 chunk 碎片, 正则能匹配到完整句子）做一次追加净化。
            content = PromptEchoScrubber.Scrub(content);

            return content;
        }

        private static string StripBlock(string content, string openMarker, string closeMarker)
        {
            while (true)
            {
                int open = content.IndexOf(openMarker, StringComparison.Ordinal);
                if (open < 0) return content;

                int close = content.IndexOf(closeMarker, open + openMarker.Length, StringComparison.Ordinal);
                if (close < 0)
                {
                    // 未闭合: 保留原文避免误删
                    return content;
                }

                int blockEnd = close + closeMarker.Length;
                var before = content.Substring(0, open);
                var after = content.Substring(blockEnd);
                content = (before + after).Trim();
            }
        }

        #endregion

        #region 辅助数据类型

        /// <summary>
        /// Node B 调用结果快照。
        /// </summary>
        internal sealed class AnswerChallengeResult
        {
            public NodeBVerdict Verdict { get; }
            public IReadOnlyList<string> ReviseIssues { get; }
            public IReadOnlyList<string> BlockVerifications { get; }
            public bool Skipped { get; }

            public AnswerChallengeResult(NodeBVerdict verdict, IReadOnlyList<string> reviseIssues, IReadOnlyList<string> blockVerifications, bool skipped)
            {
                Verdict = verdict;
                ReviseIssues = reviseIssues;
                BlockVerifications = blockVerifications;
                Skipped = skipped;
            }
        }

        #endregion
    }
}