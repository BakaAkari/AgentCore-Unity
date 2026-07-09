using System;

namespace AgentCore.Editor.Core.SelfChallenge
{
    /// <summary>
    /// Node A 输出的 <c>&lt;intent_challenge&gt;</c> / <c>&lt;intent_challenge_continuation&gt;</c>
    /// 块的流式抽取器。
    /// <para>
    /// 复用 <see cref="VisiblePlanningTraceExtractor"/> 的状态机模式, 但语义不同:
    /// - VisiblePlanningTraceExtractor: 分离 reasoning 与 visible, 只取第一段
    /// - IntentChallengeStreamExtractor: 抽取块内容, 允许 tag 前后有其他 visible 文本
    /// </para>
    /// <para>
    /// **状态机**:
    /// <list type="bullet">
    ///   <item>None → 等待 opening tag(可能有 leading visible 内容)</item>
    ///   <item>Buffering → 已看到 opening tag, 累积直到 closing tag</item>
    ///   <item>Completed → 已抽取到完整块; 后续内容全部是 visible</item>
    ///   <item>Invalid → 未闭合 / 嵌套 / 其他异常; visible 内容原样返回</item>
    /// </list>
    /// </para>
    /// </summary>
    public class IntentChallengeStreamExtractor
    {
        /// <summary>
        /// 抽取模式: 完整 Node A 还是 Continuation。
        /// </summary>
        public enum Mode
        {
            /// <summary>完整 Node A, marker = <c>&lt;intent_challenge&gt;</c></summary>
            Full,
            /// <summary>Continuation, marker = <c>&lt;intent_challenge_continuation&gt;</c></summary>
            Continuation
        }

        private readonly Mode _mode;
        private readonly string _openMarker;
        private readonly string _closeMarker;

        private string _pendingPrefix = string.Empty;
        private string _buffer = string.Empty;
        private string _extractedBlock = string.Empty;

        /// <summary>
        /// 创建 IntentChallengeStreamExtractor 实例。
        /// </summary>
        /// <param name="mode">抽取模式, 决定使用的 marker。</param>
        public IntentChallengeStreamExtractor(Mode mode = Mode.Full)
        {
            _mode = mode;
            if (mode == Mode.Full)
            {
                _openMarker = SelfChallengeConfig.NodeAOpenMarker;
                _closeMarker = SelfChallengeConfig.NodeACloseMarker;
            }
            else
            {
                _openMarker = SelfChallengeConfig.NodeAContinuationOpenMarker;
                _closeMarker = SelfChallengeConfig.NodeAContinuationCloseMarker;
            }
            State = IntentChallengeExtractorState.None;
        }

        /// <summary>当前抽取器状态。</summary>
        public IntentChallengeExtractorState State { get; private set; }

        /// <summary>抽取模式(创建后不变)。</summary>
        public Mode ExtractorMode => _mode;

        /// <summary>
        /// 完整抽取到的块原文, 含 opening / closing marker。
        /// 仅当 <see cref="State"/> = <see cref="IntentChallengeExtractorState.Completed"/> 时非空。
        /// </summary>
        public string ExtractedBlock => _extractedBlock;

        /// <summary>
        /// 重置抽取器, 用于新一轮 assistant turn。
        /// </summary>
        public void Reset()
        {
            State = IntentChallengeExtractorState.None;
            _pendingPrefix = string.Empty;
            _buffer = string.Empty;
            _extractedBlock = string.Empty;
        }

        /// <summary>
        /// 从持久化状态恢复(用于 domain reload 后)。
        /// </summary>
        public void RestoreState(IntentChallengeExtractorState state, string partialBuffer, string extractedBlock)
        {
            State = state;
            _pendingPrefix = string.Empty;
            _buffer = partialBuffer ?? string.Empty;
            _extractedBlock = extractedBlock ?? string.Empty;
        }

        /// <summary>
        /// 处理一个流式 content token。
        /// </summary>
        /// <param name="token">流式 content token。</param>
        /// <returns>该 token 里应对外可见(不属于 challenge 块内)的部分 + 属于块内容的部分。</returns>
        public IntentChallengeDelta Append(string token)
        {
            if (string.IsNullOrEmpty(token))
                return IntentChallengeDelta.Empty(State);

            switch (State)
            {
                case IntentChallengeExtractorState.None:
                    return ProcessLookingForOpen(token);
                case IntentChallengeExtractorState.Buffering:
                    return ProcessBuffering(token);
                case IntentChallengeExtractorState.Completed:
                case IntentChallengeExtractorState.Invalid:
                default:
                    // 已完成 / 已失效: 后续 token 全部是 visible
                    return new IntentChallengeDelta(token, string.Empty, State, false);
            }
        }

        /// <summary>
        /// 非流式一次性抽取。用于处理已完整的历史 assistant content。
        /// </summary>
        /// <param name="rawContent">完整 assistant content。</param>
        /// <param name="mode">抽取模式。</param>
        /// <returns>抽取结果, 含清洗后的 visible 内容与 block 原文。</returns>
        public static IntentChallengeFinalResult FinalizeContent(string rawContent, Mode mode = Mode.Full)
        {
            if (string.IsNullOrEmpty(rawContent))
            {
                return new IntentChallengeFinalResult(rawContent ?? string.Empty, string.Empty, IntentChallengeExtractorState.None);
            }

            string openMarker, closeMarker;
            if (mode == Mode.Full)
            {
                openMarker = SelfChallengeConfig.NodeAOpenMarker;
                closeMarker = SelfChallengeConfig.NodeACloseMarker;
            }
            else
            {
                openMarker = SelfChallengeConfig.NodeAContinuationOpenMarker;
                closeMarker = SelfChallengeConfig.NodeAContinuationCloseMarker;
            }

            int openIdx = rawContent.IndexOf(openMarker, StringComparison.Ordinal);
            if (openIdx < 0)
            {
                return new IntentChallengeFinalResult(rawContent, string.Empty, IntentChallengeExtractorState.None);
            }

            int closeIdx = rawContent.IndexOf(closeMarker, openIdx + openMarker.Length, StringComparison.Ordinal);
            if (closeIdx < 0)
            {
                // Opening tag 有但未闭合 → 视为 Invalid, visible 保留原文
                return new IntentChallengeFinalResult(rawContent, string.Empty, IntentChallengeExtractorState.Invalid);
            }

            int blockEnd = closeIdx + closeMarker.Length;
            string block = rawContent.Substring(openIdx, blockEnd - openIdx);
            string before = rawContent.Substring(0, openIdx);
            string after = rawContent.Substring(blockEnd);
            // 剥离块本身后, 保留 tag 前后 visible 内容; 中间用换行连接避免拼接
            string visible = (before + after).Trim('\r', '\n');
            return new IntentChallengeFinalResult(visible, block, IntentChallengeExtractorState.Completed);
        }

        private IntentChallengeDelta ProcessLookingForOpen(string token)
        {
            _pendingPrefix += token;
            int openIdx = _pendingPrefix.IndexOf(_openMarker, StringComparison.Ordinal);
            if (openIdx >= 0)
            {
                // 找到开始 marker: 前面部分是 visible, 后面部分进 buffer
                string visible = _pendingPrefix.Substring(0, openIdx);
                string afterOpen = _pendingPrefix.Substring(openIdx + _openMarker.Length);
                _pendingPrefix = string.Empty;
                _buffer = _openMarker + afterOpen; // 缓冲区始终从 open marker 起

                State = IntentChallengeExtractorState.Buffering;
                // 立即检查 buffer 中是否已经有 close marker(短流场景)
                var closeResult = TryFinishFromBuffer();
                if (closeResult != null)
                    return new IntentChallengeDelta(visible + closeResult.Value.VisibleContent, string.Empty, State, true);

                return new IntentChallengeDelta(visible, string.Empty, State, true);
            }

            // 尚未找到 open marker: 检查是否有可能的前缀部分匹配, 保留 tail
            // 若 _pendingPrefix 结尾可能是 opening marker 的前缀, 保留最多 openMarker.Length - 1 字符
            int tail = Math.Min(_pendingPrefix.Length, _openMarker.Length - 1);
            int emitLen = _pendingPrefix.Length - tail;
            if (emitLen <= 0)
                return IntentChallengeDelta.Empty(State);

            string emit = _pendingPrefix.Substring(0, emitLen);
            _pendingPrefix = _pendingPrefix.Substring(emitLen);
            return new IntentChallengeDelta(emit, string.Empty, State, false);
        }

        private IntentChallengeDelta ProcessBuffering(string token)
        {
            _buffer += token;
            var finished = TryFinishFromBuffer();
            if (finished != null)
                return finished.Value;

            // 未找到 close marker, 检查是否有嵌套的 open marker(视为 Invalid)
            int nestedOpen = _buffer.IndexOf(_openMarker, _openMarker.Length, StringComparison.Ordinal);
            if (nestedOpen >= 0)
            {
                // 嵌套异常
                State = IntentChallengeExtractorState.Invalid;
                string visible = _buffer;
                _buffer = string.Empty;
                return new IntentChallengeDelta(visible, string.Empty, State, false);
            }

            return IntentChallengeDelta.Empty(State);
        }

        private IntentChallengeDelta? TryFinishFromBuffer()
        {
            int closeIdx = _buffer.IndexOf(_closeMarker, _openMarker.Length, StringComparison.Ordinal);
            if (closeIdx < 0)
                return null;

            int blockEnd = closeIdx + _closeMarker.Length;
            _extractedBlock = _buffer.Substring(0, blockEnd);
            string trailing = _buffer.Substring(blockEnd);
            _buffer = string.Empty;
            State = IntentChallengeExtractorState.Completed;
            return new IntentChallengeDelta(trailing, _extractedBlock, State, false);
        }
    }

    /// <summary>
    /// IntentChallengeStreamExtractor 的抽取状态。
    /// </summary>
    public enum IntentChallengeExtractorState
    {
        /// <summary>尚未看到 opening marker。</summary>
        None,
        /// <summary>已看到 opening marker, 正在累积直到 closing marker。</summary>
        Buffering,
        /// <summary>已完整抽取一个块。</summary>
        Completed,
        /// <summary>结构异常(嵌套 / 未闭合)。</summary>
        Invalid
    }

    /// <summary>
    /// 流式抽取的增量结果。
    /// </summary>
    public readonly struct IntentChallengeDelta
    {
        /// <summary>创建增量。</summary>
        public IntentChallengeDelta(string visibleContent, string blockContent, IntentChallengeExtractorState state, bool started)
        {
            VisibleContent = visibleContent ?? string.Empty;
            BlockContent = blockContent ?? string.Empty;
            State = state;
            Started = started;
        }

        /// <summary>本次 token 里应对外可见的部分(不属于 challenge 块内)。</summary>
        public string VisibleContent { get; }

        /// <summary>本次 token 里属于 challenge 块的部分(仅 Completed 时非空)。</summary>
        public string BlockContent { get; }

        /// <summary>处理后状态。</summary>
        public IntentChallengeExtractorState State { get; }

        /// <summary>本次是否首次进入 Buffering 状态。</summary>
        public bool Started { get; }

        /// <summary>创建空增量, 保持状态。</summary>
        public static IntentChallengeDelta Empty(IntentChallengeExtractorState state)
        {
            return new IntentChallengeDelta(string.Empty, string.Empty, state, false);
        }
    }

    /// <summary>
    /// 非流式一次性抽取的结果。
    /// </summary>
    public readonly struct IntentChallengeFinalResult
    {
        /// <summary>创建结果。</summary>
        public IntentChallengeFinalResult(string visibleContent, string extractedBlock, IntentChallengeExtractorState state)
        {
            VisibleContent = visibleContent ?? string.Empty;
            ExtractedBlock = extractedBlock ?? string.Empty;
            State = state;
        }

        /// <summary>剥离掉 challenge 块后的可见内容。</summary>
        public string VisibleContent { get; }

        /// <summary>抽取到的完整块原文, 含 marker。</summary>
        public string ExtractedBlock { get; }

        /// <summary>最终抽取状态。</summary>
        public IntentChallengeExtractorState State { get; }
    }
}
