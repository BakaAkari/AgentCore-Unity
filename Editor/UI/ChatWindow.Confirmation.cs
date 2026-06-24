using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Safety;
using UnityEditor;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI
{
    /// <summary>
    /// ChatWindow embedded tool confirmation UI.
    /// </summary>
    public partial class ChatWindow
    {
        private const int ConfirmationTimeoutSeconds = 120;
        private const int MaxConfirmationItems = 6;
        private const int MaxConfirmationValueLength = 180;

        private readonly HashSet<string> _trustedToolConfirmations = new HashSet<string>(StringComparer.Ordinal);

        private sealed class PendingToolConfirmation
        {
            public ToolConfirmationRequest Request;
            public TaskCompletionSource<bool> Completion;
            public CancellationTokenRegistration CancellationRegistration;
            public CancellationTokenSource TimeoutSource;
        }

        /// <summary>
        /// Requests confirmation through the embedded ChatWindow panel.
        /// </summary>
        /// <param name="request">The confirmation request.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True when approved; otherwise false.</returns>
        private Task<bool> RequestEmbeddedToolConfirmationAsync(ToolConfirmationRequest request, CancellationToken ct)
        {
            if (request == null || ct.IsCancellationRequested)
            {
                return Task.FromResult(false);
            }

            if (IsToolConfirmationTrusted(request))
            {
                return Task.FromResult(true);
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new PendingToolConfirmation
            {
                Request = request,
                Completion = tcs
            };

            pending.CancellationRegistration = ct.Register(() => ScheduleResolvePendingToolConfirmation(pending, false));
            pending.TimeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(ConfirmationTimeoutSeconds));
            pending.TimeoutSource.Token.Register(() => ScheduleResolvePendingToolConfirmation(pending, false));

            EditorApplication.delayCall += () => EnqueuePendingToolConfirmation(pending);
            return tcs.Task;
        }

        private void InitializeToolConfirmationPanel(VisualElement chatArea, VisualElement inputArea)
        {
            if (chatArea == null || inputArea == null)
            {
                return;
            }

            _toolConfirmationPanel = new VisualElement { name = "tool-confirmation-panel" };
            _toolConfirmationPanel.AddToClassList("tool-confirmation-panel");
            _toolConfirmationPanel.style.display = DisplayStyle.None;

            var inputIndex = chatArea.IndexOf(inputArea);
            if (inputIndex >= 0)
            {
                chatArea.Insert(inputIndex, _toolConfirmationPanel);
            }
            else
            {
                chatArea.Add(_toolConfirmationPanel);
            }
        }

        private void EnqueuePendingToolConfirmation(PendingToolConfirmation pending)
        {
            if (pending == null || pending.Completion.Task.IsCompleted)
            {
                CleanupPendingToolConfirmation(pending);
                return;
            }

            if (IsToolConfirmationTrusted(pending.Request))
            {
                CompletePendingToolConfirmation(pending, true, updateUi: false);
                return;
            }

            if (_toolConfirmationPanel == null)
            {
                CompletePendingToolConfirmation(pending, false, updateUi: false);
                return;
            }

            _pendingToolConfirmations.Enqueue(pending);
            RenderNextToolConfirmation();
        }

        private void RenderNextToolConfirmation()
        {
            if (_activeToolConfirmation != null)
            {
                return;
            }

            while (_pendingToolConfirmations.Count > 0)
            {
                var next = _pendingToolConfirmations.Dequeue();
                if (next != null && !next.Completion.Task.IsCompleted)
                {
                    if (IsToolConfirmationTrusted(next.Request))
                    {
                        CompletePendingToolConfirmation(next, true, updateUi: false);
                        continue;
                    }

                    _activeToolConfirmation = next;
                    RenderToolConfirmation(next);
                    return;
                }

                CleanupPendingToolConfirmation(next);
            }

            HideToolConfirmationPanel();
        }

        private void RenderToolConfirmation(PendingToolConfirmation pending)
        {
            var request = pending.Request;
            _toolConfirmationPanel.Clear();
            _toolConfirmationPanel.style.display = DisplayStyle.Flex;

            var header = new VisualElement();
            header.AddToClassList("tool-confirmation-header");

            var title = new Label(string.IsNullOrEmpty(request.Title) ? request.ToolName : request.Title);
            title.AddToClassList("tool-confirmation-title");
            header.Add(title);

            var risk = new Label(BuildRiskText(request));
            risk.AddToClassList("tool-confirmation-risk");
            header.Add(risk);
            _toolConfirmationPanel.Add(header);

            if (!string.IsNullOrEmpty(request.Description))
            {
                var description = new Label(Truncate(request.Description, 420));
                description.AddToClassList("tool-confirmation-description");
                _toolConfirmationPanel.Add(description);
            }

            AddConfirmationList("Why confirmation is required", request.Reasons);
            AddConfirmationMap("Parameters", request.ParameterSummary);
            AddConfirmationList("Targets", request.Targets);

            var footer = new VisualElement();
            footer.AddToClassList("tool-confirmation-footer");

            var meta = new Label($"Waiting for approval · auto reject in {ConfirmationTimeoutSeconds}s");
            meta.AddToClassList("tool-confirmation-meta");
            footer.Add(meta);

            var buttons = new VisualElement();
            buttons.AddToClassList("tool-confirmation-buttons");

            var reject = new Button(() => ResolvePendingToolConfirmation(pending, false)) { text = "Reject" };
            reject.AddToClassList("tool-confirmation-button");
            reject.AddToClassList("tool-confirmation-button--reject");
            buttons.Add(reject);

            if (CanTrustForSession(request))
            {
                var trust = new Button(() => ResolvePendingToolConfirmationWithTrust(pending)) { text = "Trust Session" };
                trust.AddToClassList("tool-confirmation-button");
                trust.AddToClassList("tool-confirmation-button--trust");
                buttons.Add(trust);
            }

            var approve = new Button(() => ResolvePendingToolConfirmation(pending, true)) { text = "Approve" };
            approve.AddToClassList("tool-confirmation-button");
            approve.AddToClassList("tool-confirmation-button--approve");
            buttons.Add(approve);

            footer.Add(buttons);
            _toolConfirmationPanel.Add(footer);

            UpdateStatusLabel("等待工具确认...");
            ScrollToBottom(force: true);
        }

        private void AddConfirmationList(string title, IReadOnlyList<string> values)
        {
            if (values == null || values.Count == 0)
            {
                return;
            }

            var block = new VisualElement();
            block.AddToClassList("tool-confirmation-block");
            var sectionTitle = new Label(title);
            sectionTitle.AddToClassList("tool-confirmation-section-title");
            block.Add(sectionTitle);

            var shown = Math.Min(values.Count, MaxConfirmationItems);
            for (var i = 0; i < shown; i++)
            {
                var item = new Label("- " + Truncate(values[i], MaxConfirmationValueLength));
                item.AddToClassList("tool-confirmation-item");
                block.Add(item);
            }

            if (values.Count > shown)
            {
                var more = new Label($"... +{values.Count - shown} more");
                more.AddToClassList("tool-confirmation-item");
                block.Add(more);
            }

            _toolConfirmationPanel.Add(block);
        }

        private void AddConfirmationMap(string title, IReadOnlyDictionary<string, string> values)
        {
            if (values == null || values.Count == 0)
            {
                return;
            }

            var block = new VisualElement();
            block.AddToClassList("tool-confirmation-block");
            var sectionTitle = new Label(title);
            sectionTitle.AddToClassList("tool-confirmation-section-title");
            block.Add(sectionTitle);

            var count = 0;
            foreach (var pair in values)
            {
                if (count >= MaxConfirmationItems)
                {
                    var more = new Label($"... +{values.Count - count} more");
                    more.AddToClassList("tool-confirmation-item");
                    block.Add(more);
                    break;
                }

                var item = new Label($"- {pair.Key}: {Truncate(pair.Value, MaxConfirmationValueLength)}");
                item.AddToClassList("tool-confirmation-item");
                block.Add(item);
                count++;
            }

            _toolConfirmationPanel.Add(block);
        }

        private void ResolvePendingToolConfirmation(PendingToolConfirmation pending, bool approved)
        {
            CompletePendingToolConfirmation(pending, approved, updateUi: true);
        }

        private void ResolvePendingToolConfirmationWithTrust(PendingToolConfirmation pending)
        {
            if (pending?.Request != null)
            {
                _trustedToolConfirmations.Add(BuildTrustKey(pending.Request));
            }

            CompletePendingToolConfirmation(pending, true, updateUi: true);
        }

        private void ScheduleResolvePendingToolConfirmation(PendingToolConfirmation pending, bool approved)
        {
            if (pending == null)
            {
                return;
            }

            EditorApplication.delayCall += () => CompletePendingToolConfirmation(pending, approved, updateUi: true);
        }

        private void CompletePendingToolConfirmation(PendingToolConfirmation pending, bool approved, bool updateUi)
        {
            if (pending == null)
            {
                return;
            }

            if (pending.Completion.TrySetResult(approved))
            {
                CleanupPendingToolConfirmation(pending);
            }

            if (!updateUi || !ReferenceEquals(_activeToolConfirmation, pending))
            {
                return;
            }

            _activeToolConfirmation = null;
            HideToolConfirmationPanel();
            RenderNextToolConfirmation();
        }

        private void CleanupPendingToolConfirmation(PendingToolConfirmation pending)
        {
            if (pending == null)
            {
                return;
            }

            pending.CancellationRegistration.Dispose();
            pending.TimeoutSource?.Dispose();
        }

        private void HideToolConfirmationPanel()
        {
            if (_toolConfirmationPanel == null)
            {
                return;
            }

            _toolConfirmationPanel.Clear();
            _toolConfirmationPanel.style.display = DisplayStyle.None;
        }

        private void ClearPendingToolConfirmations()
        {
            if (_activeToolConfirmation != null)
            {
                CompletePendingToolConfirmation(_activeToolConfirmation, false, updateUi: false);
                _activeToolConfirmation = null;
            }

            while (_pendingToolConfirmations.Count > 0)
            {
                CompletePendingToolConfirmation(_pendingToolConfirmations.Dequeue(), false, updateUi: false);
            }

            _trustedToolConfirmations.Clear();
            HideToolConfirmationPanel();
        }

        private bool IsToolConfirmationTrusted(ToolConfirmationRequest request)
        {
            return request != null && _trustedToolConfirmations.Contains(BuildTrustKey(request));
        }

        private static bool CanTrustForSession(ToolConfirmationRequest request)
        {
            if (request?.AllowedTrustScopes == null)
            {
                return false;
            }

            foreach (var scope in request.AllowedTrustScopes)
            {
                if (scope == ToolConfirmationTrustScope.SessionExactTarget)
                {
                    return true;
                }
            }

            return false;
        }

        private static string BuildTrustKey(ToolConfirmationRequest request)
        {
            var sb = new StringBuilder();
            sb.Append(NormalizeTrustPart(request.ToolName));
            sb.Append('|').Append(NormalizeTrustPart(request.Action));
            sb.Append('|').Append(request.Risk.ToolRisk);
            sb.Append('|').Append(request.Risk.PathRisk);
            sb.Append('|').Append(request.Risk.Capabilities);

            if (request.Targets != null)
            {
                foreach (var target in request.Targets)
                {
                    sb.Append('|').Append(NormalizeTrustPart(target));
                }
            }

            return sb.ToString();
        }

        private static string BuildRiskText(ToolConfirmationRequest request)
        {
            var risk = request.Risk;
            var sb = new StringBuilder();
            sb.Append(request.ToolName);
            if (!string.IsNullOrEmpty(request.Action))
            {
                sb.Append(" / ").Append(request.Action);
            }

            sb.Append(" · Risk: ").Append(risk.ToolRisk);
            sb.Append(" · Path: ").Append(risk.PathRisk);
            sb.Append(" · Capabilities: ").Append(risk.Capabilities);
            return sb.ToString();
        }

        private static string NormalizeTrustPart(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().Replace('\\', '/').ToLowerInvariant();
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value ?? string.Empty;
            }

            return value.Substring(0, Math.Max(0, maxLength - 3)) + "...";
        }
    }
}
