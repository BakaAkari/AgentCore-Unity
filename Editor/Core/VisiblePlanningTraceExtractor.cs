using System;

namespace AgentCore.Editor.Core
{
    /// <summary>
    /// Extracts prompt-level visible planning traces from assistant content.
    /// </summary>
    public class VisiblePlanningTraceExtractor
    {
        private const string ThinkingMarker = "---THINKING---";
        private const string ActionMarker = "---ACTION---";
        private string _pendingPrefix = string.Empty;
        private string _planningTail = string.Empty;

        /// <summary>
        /// Creates a visible planning trace extractor.
        /// </summary>
        /// <param name="sourceSeparator">Reserved separator option kept for serialized/API compatibility.</param>
        public VisiblePlanningTraceExtractor(string sourceSeparator = "\n\n---\n\n")
        {
            State = VisiblePlanningTraceState.None;
        }

        /// <summary>Current parser state.</summary>
        public VisiblePlanningTraceState State { get; private set; }

        /// <summary>
        /// Restores state after session/domain reload.
        /// </summary>
        /// <param name="state">Persisted state.</param>
        public void RestoreState(VisiblePlanningTraceState state)
        {
            State = state;
            _pendingPrefix = string.Empty;
            _planningTail = string.Empty;
        }

        /// <summary>
        /// Resets parser state for a new assistant turn.
        /// </summary>
        public void Reset()
        {
            State = VisiblePlanningTraceState.None;
            _pendingPrefix = string.Empty;
            _planningTail = string.Empty;
        }

        /// <summary>
        /// Processes one streamed content token.
        /// </summary>
        /// <param name="token">Content token.</param>
        /// <returns>Split visible/reasoning delta.</returns>
        public VisiblePlanningTraceDelta Append(string token)
        {
            if (string.IsNullOrEmpty(token))
                return VisiblePlanningTraceDelta.Empty(State);

            switch (State)
            {
                case VisiblePlanningTraceState.None:
                    return ProcessPrefix(token);
                case VisiblePlanningTraceState.Buffering:
                    return ProcessPlanning(token);
                case VisiblePlanningTraceState.Completed:
                case VisiblePlanningTraceState.Invalid:
                default:
                    return new VisiblePlanningTraceDelta(token, string.Empty, State, false);
            }
        }

        /// <summary>
        /// Performs a complete non-streaming extraction pass over final assistant content.
        /// </summary>
        /// <param name="rawContent">Raw assistant content.</param>
        /// <returns>Final cleaned content and extracted reasoning.</returns>
        public static VisiblePlanningFinalResult FinalizeContent(string rawContent)
        {
            if (string.IsNullOrEmpty(rawContent))
                return new VisiblePlanningFinalResult(rawContent ?? string.Empty, string.Empty, VisiblePlanningTraceState.None);

            if (IsLikelyQuotedOrCodeExample(rawContent))
                return new VisiblePlanningFinalResult(rawContent, string.Empty, VisiblePlanningTraceState.Invalid);

            var leadingTrimmed = rawContent.TrimStart();
            var leadingOffset = rawContent.Length - leadingTrimmed.Length;
            if (!leadingTrimmed.StartsWith(ThinkingMarker, StringComparison.Ordinal))
                return new VisiblePlanningFinalResult(rawContent, string.Empty, VisiblePlanningTraceState.None);

            var afterThinkingIndex = leadingOffset + ThinkingMarker.Length;
            var actionIndex = rawContent.IndexOf(ActionMarker, afterThinkingIndex, StringComparison.Ordinal);
            if (actionIndex < 0)
                return new VisiblePlanningFinalResult(rawContent, string.Empty, VisiblePlanningTraceState.Invalid);

            var reasoning = rawContent.Substring(afterThinkingIndex, actionIndex - afterThinkingIndex).Trim('\r', '\n');
            var content = rawContent.Substring(actionIndex + ActionMarker.Length).TrimStart('\r', '\n');
            return new VisiblePlanningFinalResult(content, reasoning, VisiblePlanningTraceState.Completed);
        }

        private VisiblePlanningTraceDelta ProcessPrefix(string token)
        {
            _pendingPrefix += token;
            var trimmed = _pendingPrefix.TrimStart();

            if (trimmed.Length == 0)
                return VisiblePlanningTraceDelta.Empty(State);

            if (trimmed.StartsWith(ThinkingMarker, StringComparison.Ordinal))
            {
                if (IsLikelyQuotedOrCodeExample(_pendingPrefix))
                {
                    State = VisiblePlanningTraceState.Invalid;
                    var invalidVisible = _pendingPrefix;
                    _pendingPrefix = string.Empty;
                    return new VisiblePlanningTraceDelta(invalidVisible, string.Empty, State, false);
                }

                var offset = _pendingPrefix.IndexOf(ThinkingMarker, StringComparison.Ordinal);
                var afterMarker = _pendingPrefix.Substring(offset + ThinkingMarker.Length);
                _pendingPrefix = string.Empty;
                State = VisiblePlanningTraceState.Buffering;
                var result = ProcessPlanning(afterMarker);
                return new VisiblePlanningTraceDelta(result.VisibleContent, result.ReasoningContent, result.State, true);
            }

            if (ThinkingMarker.StartsWith(trimmed, StringComparison.Ordinal))
                return VisiblePlanningTraceDelta.Empty(State);

            State = VisiblePlanningTraceState.Invalid;
            var visible = _pendingPrefix;
            _pendingPrefix = string.Empty;
            return new VisiblePlanningTraceDelta(visible, string.Empty, State, false);
        }

        private VisiblePlanningTraceDelta ProcessPlanning(string token)
        {
            _planningTail += token;
            var actionIndex = _planningTail.IndexOf(ActionMarker, StringComparison.Ordinal);
            if (actionIndex >= 0)
            {
                var reasoning = _planningTail.Substring(0, actionIndex);
                var visible = _planningTail.Substring(actionIndex + ActionMarker.Length).TrimStart('\r', '\n');
                _planningTail = string.Empty;
                State = VisiblePlanningTraceState.Completed;
                return new VisiblePlanningTraceDelta(visible, reasoning, State, false);
            }

            var maxTail = ActionMarker.Length - 1;
            if (_planningTail.Length <= maxTail)
                return VisiblePlanningTraceDelta.Empty(State);

            var emitLength = _planningTail.Length - maxTail;
            var emitted = _planningTail.Substring(0, emitLength);
            _planningTail = _planningTail.Substring(emitLength);
            return new VisiblePlanningTraceDelta(string.Empty, emitted, State, false);
        }

        private static bool IsLikelyQuotedOrCodeExample(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            var trimmedStart = text.TrimStart();
            if (trimmedStart.StartsWith("```", StringComparison.Ordinal))
                return true;
            if (trimmedStart.StartsWith(">", StringComparison.Ordinal))
                return true;

            var thinkingIndex = text.IndexOf(ThinkingMarker, StringComparison.Ordinal);
            if (thinkingIndex < 0) return false;

            var before = text.Substring(0, thinkingIndex);
            var fenceCount = CountOccurrences(before, "```");
            return fenceCount % 2 != 0;
        }

        private static int CountOccurrences(string text, string value)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(value)) return 0;

            var count = 0;
            var index = 0;
            while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }
            return count;
        }
    }

    /// <summary>
    /// Stream split result for visible planning extraction.
    /// </summary>
    public readonly struct VisiblePlanningTraceDelta
    {
        /// <summary>Creates a visible planning trace delta.</summary>
        public VisiblePlanningTraceDelta(string visibleContent, string reasoningContent, VisiblePlanningTraceState state, bool started)
        {
            VisibleContent = visibleContent ?? string.Empty;
            ReasoningContent = reasoningContent ?? string.Empty;
            State = state;
            Started = started;
        }

        /// <summary>Visible content that belongs to the final assistant bubble.</summary>
        public string VisibleContent { get; }

        /// <summary>Extracted planning text that belongs to ThinkingDrawer.</summary>
        public string ReasoningContent { get; }

        /// <summary>Parser state after processing.</summary>
        public VisiblePlanningTraceState State { get; }

        /// <summary>Whether this delta started a visible planning trace.</summary>
        public bool Started { get; }

        /// <summary>Creates an empty delta preserving state.</summary>
        public static VisiblePlanningTraceDelta Empty(VisiblePlanningTraceState state)
        {
            return new VisiblePlanningTraceDelta(string.Empty, string.Empty, state, false);
        }
    }

    /// <summary>
    /// Final non-streaming split result for visible planning extraction.
    /// </summary>
    public readonly struct VisiblePlanningFinalResult
    {
        /// <summary>Creates a final extraction result.</summary>
        public VisiblePlanningFinalResult(string content, string reasoning, VisiblePlanningTraceState state)
        {
            Content = content ?? string.Empty;
            Reasoning = reasoning ?? string.Empty;
            State = state;
        }

        /// <summary>Cleaned assistant content.</summary>
        public string Content { get; }

        /// <summary>Extracted planning text.</summary>
        public string Reasoning { get; }

        /// <summary>Final extraction state.</summary>
        public VisiblePlanningTraceState State { get; }
    }
}
