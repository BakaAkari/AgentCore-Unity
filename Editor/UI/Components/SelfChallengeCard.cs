using System.Text;
using AgentCore.Editor.Core.SelfChallenge;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI.Components
{
    /// <summary>
    /// Self-Challenge Card — Phase 9 用户可观测 UI。
    /// <para>
    /// 位置: 在 assistant turn 的 ThinkingDrawer 之下、ToolCallGroup 之上。
    /// 默认折叠, Verdict = REVISE / BLOCK / WaitingForClarification 时自动展开。
    /// </para>
    /// <para>
    /// v1.5.0-alpha 极简版: 复用 ToolCallCard 视觉语言, 显示 Verdict 徽标 + 简短摘要 + 可展开完整块。
    /// </para>
    /// </summary>
    public class SelfChallengeCard : VisualElement
    {
        private static readonly Color BackgroundColor = new Color(0.176f, 0.176f, 0.176f);
        private static readonly Color TextPrimary = new Color(0.831f, 0.831f, 0.831f);
        private static readonly Color TextSecondary = new Color(0.533f, 0.533f, 0.533f);
        private static readonly Color VerdictPassColor = new Color(0.298f, 0.686f, 0.314f);   // #4CAF50 绿
        private static readonly Color VerdictReviseColor = new Color(0.949f, 0.612f, 0.070f); // #F29C12 橙
        private static readonly Color VerdictBlockColor = new Color(0.957f, 0.263f, 0.212f);  // #F44336 红
        private static readonly Color VerdictRunningColor = new Color(0.290f, 0.565f, 0.851f); // #4A90D9 蓝
        private static readonly Color VerdictSkippedColor = new Color(0.45f, 0.45f, 0.45f);   // 灰
        private static readonly Color DetailsBg = new Color(0.153f, 0.153f, 0.153f);

        private const string IconPass = "[v]";
        private const string IconRevise = "[~]";
        private const string IconBlock = "[!]";
        private const string IconWaiting = "[?]";
        private const string IconRunning = "[.]";
        private const string IconSkipped = "[-]";
        private const string ArrowCollapsed = ">";
        private const string ArrowExpanded = "v";

        private readonly Label _verdictIcon;
        private readonly Label _verdictText;
        private readonly Label _summaryLabel;
        private readonly Button _copyButton;
        private readonly Label _toggleArrow;
        private readonly VisualElement _detailsContainer;
        private readonly ScrollView _detailsScroll;
        private readonly TextField _detailsField;

        private bool _isExpanded;
        private bool _userToggled;
        private string _rawDetailsText = string.Empty;

        /// <summary>关联的 turn ID(用于 UI 事件路由)。</summary>
        public string TurnId { get; }

        /// <summary>当前展示的 SelfChallenge 数据快照。</summary>
        public SelfChallengeData Data { get; private set; }

        /// <summary>创建 SelfChallengeCard。</summary>
        public SelfChallengeCard(string turnId)
        {
            TurnId = turnId;
            AddToClassList("self-challenge-card");

            style.marginTop = 4;
            style.marginBottom = 4;
            style.paddingLeft = 8;
            style.paddingRight = 8;
            style.paddingTop = 4;
            style.paddingBottom = 4;
            style.backgroundColor = BackgroundColor;
            style.borderLeftWidth = 3;
            style.borderLeftColor = VerdictSkippedColor;

            // Header 单行
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.RegisterCallback<ClickEvent>(evt => { ToggleExpanded(); evt.StopPropagation(); });
            Add(header);

            _verdictIcon = new Label(IconSkipped);
            _verdictIcon.style.color = VerdictSkippedColor;
            _verdictIcon.style.marginRight = 6;
            _verdictIcon.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.Add(_verdictIcon);

            _verdictText = new Label("Self-Challenge");
            _verdictText.style.color = TextPrimary;
            _verdictText.style.unityFontStyleAndWeight = FontStyle.Bold;
            _verdictText.style.marginRight = 8;
            header.Add(_verdictText);

            _summaryLabel = new Label(string.Empty);
            _summaryLabel.style.color = TextSecondary;
            _summaryLabel.style.flexGrow = 1;
            header.Add(_summaryLabel);

            _copyButton = new Button(OnCopyClicked) { text = "Copy" };
            _copyButton.style.height = 18;
            _copyButton.style.marginRight = 4;
            _copyButton.RegisterCallback<ClickEvent>(evt => evt.StopPropagation());
            header.Add(_copyButton);

            _toggleArrow = new Label(ArrowCollapsed);
            _toggleArrow.style.color = TextSecondary;
            _toggleArrow.style.width = 12;
            header.Add(_toggleArrow);

            // Details container
            _detailsContainer = new VisualElement();
            _detailsContainer.style.marginTop = 4;
            _detailsContainer.style.paddingLeft = 6;
            _detailsContainer.style.paddingRight = 6;
            _detailsContainer.style.paddingTop = 4;
            _detailsContainer.style.paddingBottom = 4;
            _detailsContainer.style.backgroundColor = DetailsBg;
            _detailsContainer.style.display = DisplayStyle.None;
            Add(_detailsContainer);

            _detailsScroll = new ScrollView { horizontalScrollerVisibility = ScrollerVisibility.Auto };
            _detailsScroll.style.maxHeight = 240;
            _detailsContainer.Add(_detailsScroll);

            _detailsField = new TextField { multiline = true, isReadOnly = true };
            _detailsField.value = string.Empty;
            _detailsField.style.color = TextPrimary;
            _detailsField.style.whiteSpace = WhiteSpace.Normal;
            _detailsField.RegisterCallback<ClickEvent>(evt => evt.StopPropagation());
            _detailsScroll.Add(_detailsField);
        }

        /// <summary>
        /// 更新 SelfChallenge 数据; 会重新计算 Verdict 徽标与自动展开策略。
        /// </summary>
        public void SetData(SelfChallengeData data)
        {
            Data = data;
            if (data == null)
            {
                style.display = DisplayStyle.None;
                return;
            }

            style.display = DisplayStyle.Flex;

            var (icon, color, verdictText) = ResolveVerdict(data);
            _verdictIcon.text = icon;
            _verdictIcon.style.color = color;
            _verdictText.text = verdictText;
            _verdictText.style.color = color;
            style.borderLeftColor = color;

            _summaryLabel.text = BuildSummary(data);
            _rawDetailsText = BuildDetails(data);
            _detailsField.SetValueWithoutNotify(_rawDetailsText);

            // 自动展开策略(仅当用户未手动切换过)
            if (!_userToggled)
            {
                bool shouldExpand = ShouldAutoExpand(data);
                SetExpanded(shouldExpand, isUserAction: false);
            }
        }

        private static (string icon, Color color, string text) ResolveVerdict(SelfChallengeData data)
        {
            // Node B verdict 优先
            if (data.NodeBTriggered && data.NodeBVerdict != null)
            {
                switch (data.NodeBVerdict)
                {
                    case NodeBVerdict.PASS: return (IconPass, VerdictPassColor, "PASS");
                    case NodeBVerdict.REVISE: return (IconRevise, VerdictReviseColor, "REVISED");
                    case NodeBVerdict.BLOCK: return (IconBlock, VerdictBlockColor, "BLOCKED");
                }
            }

            // Node A 结论
            if (data.NodeATriggered)
            {
                if (data.TriggeredClarification)
                    return (IconWaiting, VerdictRunningColor, "Awaiting Clarification");
                if (data.NodeBTriggered)
                    return (IconRunning, VerdictRunningColor, "Reviewer running...");
                return (IconPass, VerdictPassColor, "Intent OK");
            }

            // Skip
            if (!string.IsNullOrEmpty(data.NodeASkipReason))
                return (IconSkipped, VerdictSkippedColor, $"Skipped ({data.NodeASkipReason})");

            return (IconSkipped, VerdictSkippedColor, "Not triggered");
        }

        private static bool ShouldAutoExpand(SelfChallengeData data)
        {
            if (data.TriggeredClarification) return true;
            if (data.NodeBVerdict == NodeBVerdict.REVISE) return true;
            if (data.NodeBVerdict == NodeBVerdict.BLOCK) return true;
            return false;
        }

        private static string BuildSummary(SelfChallengeData data)
        {
            var sb = new StringBuilder();
            if (data.NodeATriggered)
            {
                int interpretationsCount = data.Interpretations?.Count ?? 0;
                sb.Append($"Intent: {interpretationsCount} interpretations");
                if (data.IsNodeAContinuation) sb.Append(" [cont]");
                if (data.NodeARetryCount > 0) sb.Append($" (retry x{data.NodeARetryCount})");
            }
            else
            {
                sb.Append("Intent: skipped");
            }

            if (data.NodeBTriggered)
            {
                sb.Append($"  ·  Reviewer: {data.CounterExampleCount} counter-examples");
                if (data.NodeBRetryCount > 0) sb.Append($" (retry x{data.NodeBRetryCount})");
                if (data.DraftRegenerated) sb.Append(" [regenerated]");
            }

            return sb.ToString();
        }

        private static string BuildDetails(SelfChallengeData data)
        {
            var sb = new StringBuilder(2048);
            sb.AppendLine("═════ Node A ═════");
            if (!data.NodeATriggered)
            {
                sb.AppendLine($"[skip] Reason: {data.NodeASkipReason ?? "n/a"}");
            }
            else
            {
                if (data.IsNodeAContinuation) sb.AppendLine("[Continuation mode]");

                if (data.Interpretations != null && data.Interpretations.Count > 0)
                {
                    sb.AppendLine($"Step 1: Interpretations ({data.Interpretations.Count})");
                    for (int i = 0; i < data.Interpretations.Count; i++)
                        sb.AppendLine($"  {i + 1}. {data.Interpretations[i]}");
                }

                if (data.AmbiguitySignals != null && data.AmbiguitySignals.Count > 0)
                {
                    sb.AppendLine("Step 2: Ambiguity Signals");
                    foreach (var s in data.AmbiguitySignals)
                        sb.AppendLine($"  - {s}");
                }

                if (!string.IsNullOrEmpty(data.ChosenInterpretation))
                {
                    sb.AppendLine("Step 3: Chosen");
                    sb.AppendLine($"  → {data.ChosenInterpretation}");
                }

                if (data.KeyAssumptions != null && data.KeyAssumptions.Count > 0)
                {
                    sb.AppendLine("  Key Assumptions:");
                    foreach (var a in data.KeyAssumptions)
                        sb.AppendLine($"    - {a}");
                }

                sb.AppendLine($"Step 4: A={data.Step4A?.ToString() ?? "?"}  B={data.Step4B?.ToString() ?? "?"}  C={data.Step4C?.ToString() ?? "?"}  D={data.Step4D?.ToString() ?? "?"}");
                if (data.InferredWords != null && data.InferredWords.Count > 0)
                {
                    sb.AppendLine($"  Inferred words: {string.Join(", ", data.InferredWords)}");
                }
                sb.AppendLine($"  Conclusion: {data.Step4Conclusion?.ToString() ?? "?"}");

                sb.AppendLine($"Step 5: Verdict = {data.Step5Verdict?.ToString() ?? "?"}");
                if (!string.IsNullOrEmpty(data.Step5CorrectedJudgement))
                    sb.AppendLine($"  Corrected: {data.Step5CorrectedJudgement}");
            }

            sb.AppendLine();
            sb.AppendLine("═════ Node B ═════");
            if (!data.NodeBTriggered)
            {
                sb.AppendLine($"[skip] Reason: {data.NodeBSkipReason ?? "n/a"}");
            }
            else
            {
                sb.AppendLine($"Verdict: {data.NodeBVerdict?.ToString() ?? "?"}");
                sb.AppendLine($"Counter-Examples: {data.CounterExampleCount}");
                if (data.CounterExampleQuotes != null && data.CounterExampleQuotes.Count > 0)
                {
                    foreach (var q in data.CounterExampleQuotes)
                        sb.AppendLine($"  <quote> {q}");
                }
                if (data.ReviseIssues != null && data.ReviseIssues.Count > 0)
                {
                    sb.AppendLine("REVISE Issues:");
                    foreach (var iss in data.ReviseIssues)
                        sb.AppendLine($"  - {iss}");
                }
                if (data.BlockVerifications != null && data.BlockVerifications.Count > 0)
                {
                    sb.AppendLine("BLOCK Verifications:");
                    foreach (var v in data.BlockVerifications)
                        sb.AppendLine($"  - {v}");
                }
                if (data.DraftRegenerated) sb.AppendLine("[Draft regenerated after REVISE]");
            }

            sb.AppendLine();
            sb.AppendLine($"Total tokens estimate: {data.TotalTokensEstimate}");
            sb.AppendLine($"Duration: {data.TotalDurationMs}ms");

            return sb.ToString();
        }

        private void ToggleExpanded()
        {
            SetExpanded(!_isExpanded, isUserAction: true);
        }

        private void SetExpanded(bool expanded, bool isUserAction)
        {
            if (isUserAction) _userToggled = true;
            _isExpanded = expanded;
            _toggleArrow.text = expanded ? ArrowExpanded : ArrowCollapsed;
            _detailsContainer.style.display = expanded ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void OnCopyClicked()
        {
            EditorGUIUtility.systemCopyBuffer = _rawDetailsText ?? string.Empty;
        }
    }
}
