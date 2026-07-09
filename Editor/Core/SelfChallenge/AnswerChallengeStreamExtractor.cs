using System;

namespace AgentCore.Editor.Core.SelfChallenge
{
    /// <summary>
    /// Node B 输出的 <c>&lt;answer_challenge&gt;...&lt;/answer_challenge&gt;</c> 块的流式抽取器。
    /// 复用 IntentChallengeStreamExtractor 的状态机模式, 但 marker 换为 Node B 的。
    /// </summary>
    public class AnswerChallengeStreamExtractor
    {
        private readonly string _openMarker = SelfChallengeConfig.NodeBOpenMarker;
        private readonly string _closeMarker = SelfChallengeConfig.NodeBCloseMarker;

        private string _pendingPrefix = string.Empty;
        private string _buffer = string.Empty;
        private string _extractedBlock = string.Empty;

        /// <summary>创建 AnswerChallengeStreamExtractor。</summary>
        public AnswerChallengeStreamExtractor()
        {
            State = AnswerChallengeExtractorState.None;
        }

        /// <summary>当前状态。</summary>
        public AnswerChallengeExtractorState State { get; private set; }

        /// <summary>完整抽取到的块原文, 含 marker; 仅 Completed 时非空。</summary>
        public string ExtractedBlock => _extractedBlock;

        /// <summary>重置抽取器。</summary>
        public void Reset()
        {
            State = AnswerChallengeExtractorState.None;
            _pendingPrefix = string.Empty;
            _buffer = string.Empty;
            _extractedBlock = string.Empty;
        }

        /// <summary>处理一个流式 token。</summary>
        public AnswerChallengeDelta Append(string token)
        {
            if (string.IsNullOrEmpty(token))
                return AnswerChallengeDelta.Empty(State);

            switch (State)
            {
                case AnswerChallengeExtractorState.None:
                    return ProcessLookingForOpen(token);
                case AnswerChallengeExtractorState.Buffering:
                    return ProcessBuffering(token);
                default:
                    return new AnswerChallengeDelta(token, string.Empty, State, false);
            }
        }

        /// <summary>非流式一次性抽取。</summary>
        public static AnswerChallengeFinalResult FinalizeContent(string rawContent)
        {
            if (string.IsNullOrEmpty(rawContent))
                return new AnswerChallengeFinalResult(rawContent ?? string.Empty, string.Empty, AnswerChallengeExtractorState.None);

            string openMarker = SelfChallengeConfig.NodeBOpenMarker;
            string closeMarker = SelfChallengeConfig.NodeBCloseMarker;

            int openIdx = rawContent.IndexOf(openMarker, StringComparison.Ordinal);
            if (openIdx < 0)
                return new AnswerChallengeFinalResult(rawContent, string.Empty, AnswerChallengeExtractorState.None);

            int closeIdx = rawContent.IndexOf(closeMarker, openIdx + openMarker.Length, StringComparison.Ordinal);
            if (closeIdx < 0)
                return new AnswerChallengeFinalResult(rawContent, string.Empty, AnswerChallengeExtractorState.Invalid);

            int blockEnd = closeIdx + closeMarker.Length;
            string block = rawContent.Substring(openIdx, blockEnd - openIdx);
            string before = rawContent.Substring(0, openIdx);
            string after = rawContent.Substring(blockEnd);
            string visible = (before + after).Trim('\r', '\n');
            return new AnswerChallengeFinalResult(visible, block, AnswerChallengeExtractorState.Completed);
        }

        private AnswerChallengeDelta ProcessLookingForOpen(string token)
        {
            _pendingPrefix += token;
            int openIdx = _pendingPrefix.IndexOf(_openMarker, StringComparison.Ordinal);
            if (openIdx >= 0)
            {
                string visible = _pendingPrefix.Substring(0, openIdx);
                string afterOpen = _pendingPrefix.Substring(openIdx + _openMarker.Length);
                _pendingPrefix = string.Empty;
                _buffer = _openMarker + afterOpen;
                State = AnswerChallengeExtractorState.Buffering;

                var closeResult = TryFinishFromBuffer();
                if (closeResult != null)
                    return new AnswerChallengeDelta(visible + closeResult.Value.VisibleContent, string.Empty, State, true);

                return new AnswerChallengeDelta(visible, string.Empty, State, true);
            }

            int tail = Math.Min(_pendingPrefix.Length, _openMarker.Length - 1);
            int emitLen = _pendingPrefix.Length - tail;
            if (emitLen <= 0)
                return AnswerChallengeDelta.Empty(State);

            string emit = _pendingPrefix.Substring(0, emitLen);
            _pendingPrefix = _pendingPrefix.Substring(emitLen);
            return new AnswerChallengeDelta(emit, string.Empty, State, false);
        }

        private AnswerChallengeDelta ProcessBuffering(string token)
        {
            _buffer += token;
            var finished = TryFinishFromBuffer();
            if (finished != null)
                return finished.Value;

            int nestedOpen = _buffer.IndexOf(_openMarker, _openMarker.Length, StringComparison.Ordinal);
            if (nestedOpen >= 0)
            {
                State = AnswerChallengeExtractorState.Invalid;
                string visible = _buffer;
                _buffer = string.Empty;
                return new AnswerChallengeDelta(visible, string.Empty, State, false);
            }

            return AnswerChallengeDelta.Empty(State);
        }

        private AnswerChallengeDelta? TryFinishFromBuffer()
        {
            int closeIdx = _buffer.IndexOf(_closeMarker, _openMarker.Length, StringComparison.Ordinal);
            if (closeIdx < 0) return null;

            int blockEnd = closeIdx + _closeMarker.Length;
            _extractedBlock = _buffer.Substring(0, blockEnd);
            string trailing = _buffer.Substring(blockEnd);
            _buffer = string.Empty;
            State = AnswerChallengeExtractorState.Completed;
            return new AnswerChallengeDelta(trailing, _extractedBlock, State, false);
        }
    }

    /// <summary>Answer Challenge 抽取状态。</summary>
    public enum AnswerChallengeExtractorState
    {
        /// <summary>尚未看到 opening marker。</summary>
        None,
        /// <summary>已看到 opening marker, 累积中。</summary>
        Buffering,
        /// <summary>已完整抽取。</summary>
        Completed,
        /// <summary>结构异常。</summary>
        Invalid
    }

    /// <summary>流式抽取增量。</summary>
    public readonly struct AnswerChallengeDelta
    {
        public AnswerChallengeDelta(string visibleContent, string blockContent, AnswerChallengeExtractorState state, bool started)
        {
            VisibleContent = visibleContent ?? string.Empty;
            BlockContent = blockContent ?? string.Empty;
            State = state;
            Started = started;
        }

        public string VisibleContent { get; }
        public string BlockContent { get; }
        public AnswerChallengeExtractorState State { get; }
        public bool Started { get; }

        public static AnswerChallengeDelta Empty(AnswerChallengeExtractorState state)
            => new AnswerChallengeDelta(string.Empty, string.Empty, state, false);
    }

    /// <summary>非流式一次性抽取结果。</summary>
    public readonly struct AnswerChallengeFinalResult
    {
        public AnswerChallengeFinalResult(string visibleContent, string extractedBlock, AnswerChallengeExtractorState state)
        {
            VisibleContent = visibleContent ?? string.Empty;
            ExtractedBlock = extractedBlock ?? string.Empty;
            State = state;
        }

        public string VisibleContent { get; }
        public string ExtractedBlock { get; }
        public AnswerChallengeExtractorState State { get; }
    }
}
