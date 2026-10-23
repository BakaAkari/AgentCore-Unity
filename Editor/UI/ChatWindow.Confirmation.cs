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
    /// <para>
    /// v1.6.5: Trust 语义从"精确目标 key"改为"会话级 scope 枚举":
    /// - <see cref="ToolConfirmationTrustScope.SessionLowMediumRisk"/>: 本会话内 ReadOnly/Low/Medium 直通
    /// - <see cref="ToolConfirmationTrustScope.SessionAll"/>: 本会话内所有工具直通 (YOLO)
    /// </para>
    /// </summary>
    public partial class ChatWindow
    {
        private const int ConfirmationTimeoutSeconds = 120;
        private const int MaxConfirmationItems = 6;
        private const int MaxConfirmationValueLength = 180;

        /// <summary>
        /// 本会话已激活的信任 scope 集合。
        /// <para>
        /// v1.6.5:通过 <see cref="UnityEditor.SessionState"/> 持久化,
        /// 跨 Domain Reload 保留,Editor 完全重启时归零。
        /// </para>
        /// <para>
        /// <b>初始化规则</b>:字段声明为空集合(纯 CLR,零 Unity API);
        /// 真正的加载在 <see cref="LoadSessionTrustScopesFromState"/> 里,
        /// 由 <c>CreateGUI</c> 生命周期方法显式触发。
        /// 这是 Unity 的硬要求 — ScriptableObject/EditorWindow 的字段初始化器 (等价于构造器上下文)
        /// 严禁调用 SessionState/EditorPrefs/AssetDatabase 等 Unity API。
        /// </para>
        /// </summary>
        private readonly HashSet<ToolConfirmationTrustScope> _sessionTrustScopes = new HashSet<ToolConfirmationTrustScope>();

        /// <summary>SessionState 键,存储 YOLO 会话信任状态 (跨 Domain Reload 但不跨 Editor 重启)。</summary>
        private const string SessionTrustStateKey = "AgentCore.SessionTrustScopes";

        /// <summary>
        /// 从 SessionState 恢复本会话已激活的信任 scope。
        /// <para>
        /// 必须在 <c>CreateGUI</c> 等生命周期方法中调用,不能在字段初始化器/构造器中调用
        /// (Unity 会抛 UnityException: GetString is not allowed to be called from a ScriptableObject constructor)。
        /// </para>
        /// </summary>
        private void LoadSessionTrustScopesFromState()
        {
            _sessionTrustScopes.Clear();
            var raw = SessionState.GetString(SessionTrustStateKey, string.Empty);
            if (string.IsNullOrEmpty(raw)) return;

            foreach (var token in raw.Split(','))
            {
                if (Enum.TryParse<ToolConfirmationTrustScope>(token.Trim(), out var scope))
                {
                    _sessionTrustScopes.Add(scope);
                }
            }
        }

        /// <summary>
        /// 将当前信任 scope 序列化写入 SessionState,以便 Domain Reload 后自动恢复。
        /// <para>
        /// 空守卫:<see cref="_sessionTrustScopes"/> 若为 null(极端情况下的半初始化状态),直接返回不写入。
        /// </para>
        /// </summary>
        private void SaveSessionTrustScopes()
        {
            if (_sessionTrustScopes == null) return;

            if (_sessionTrustScopes.Count == 0)
            {
                SessionState.EraseString(SessionTrustStateKey);
                return;
            }
            SessionState.SetString(SessionTrustStateKey, string.Join(",", _sessionTrustScopes));
        }

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

            if (IsToolConfirmationTrusted(pending.Request) || IsToolConfirmationAllowedByRules(pending.Request))
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
                    if (IsToolConfirmationTrusted(next.Request) || IsToolConfirmationAllowedByRules(next.Request))
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

            if (request.TriggeredByRule != null)
            {
                var ruleNotice = new Label(string.Format(
                    AgentCore.Editor.L10n.Loc.Tr("toolConfirmation.askRule", "由权限规则要求确认：{0}"),
                    request.TriggeredByRule.Describe()));
                ruleNotice.AddToClassList("tool-confirmation-description");
                _toolConfirmationPanel.Add(ruleNotice);
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

            var reject = new Button(() => ResolvePendingToolConfirmation(pending, false)) { text = AgentCore.Editor.L10n.Loc.Tr("toolConfirmation.deny", "Deny") };
            reject.AddToClassList("tool-confirmation-button");
            reject.AddToClassList("tool-confirmation-button--reject");
            buttons.Add(reject);

            // v1.6.5: 3 按钮布局 = Deny / Trust Low-Med / YOLO (All)
            // 不再提供"仅此一次允许"选项,任何非 Deny 都会开启会话级信任。
            if (IsScopeAllowed(request, ToolConfirmationTrustScope.SessionLowMediumRisk))
            {
                var trustLowMed = new Button(() => ResolvePendingToolConfirmationWithTrust(pending, ToolConfirmationTrustScope.SessionLowMediumRisk))
                {
                    text = AgentCore.Editor.L10n.Loc.Tr("toolConfirmation.trustLowMed", "Trust Low/Med for Session")
                };
                trustLowMed.tooltip = AgentCore.Editor.L10n.Loc.Tr(
                    "toolConfirmation.trustLowMedTooltip",
                    "本会话内所有 ReadOnly/Low/Medium 风险工具直通,High/破坏性操作仍会弹窗");
                trustLowMed.AddToClassList("tool-confirmation-button");
                trustLowMed.AddToClassList("tool-confirmation-button--trust");
                buttons.Add(trustLowMed);
            }

            // P2: "始终允许" —— 新建一条用户 allow 规则并批准本次。
            // ask 规则触发时不提供（ask 优先于 allow，新建 allow 不会生效）；batch_execute 外壳不参与规则匹配，也不提供。
            if (request.TriggeredByRule == null
                && !string.Equals(request.ToolName, "batch_execute", StringComparison.OrdinalIgnoreCase))
            {
                var matcherText = string.IsNullOrEmpty(request.Action)
                    ? request.ToolName
                    : request.ToolName + "." + request.Action;
                var alwaysAllow = new Button(() => ResolvePendingToolConfirmationWithAllowRule(pending))
                {
                    text = string.Format(AgentCore.Editor.L10n.Loc.Tr("toolConfirmation.alwaysAllow", "始终允许 {0}"), matcherText)
                };
                alwaysAllow.tooltip = string.Format(
                    AgentCore.Editor.L10n.Loc.Tr(
                        "toolConfirmation.alwaysAllowTooltip",
                        "新建一条用户 allow 规则（{0}），以后不再询问；可在 Project Settings > AgentCore > Permissions 中修改或删除"),
                    PermissionRulesStore.UserRelativePath);
                alwaysAllow.AddToClassList("tool-confirmation-button");
                alwaysAllow.AddToClassList("tool-confirmation-button--trust");
                buttons.Add(alwaysAllow);
            }

            if (IsScopeAllowed(request, ToolConfirmationTrustScope.SessionAll))
            {
                var yolo = new Button(() => ResolvePendingToolConfirmationWithTrust(pending, ToolConfirmationTrustScope.SessionAll))
                {
                    text = AgentCore.Editor.L10n.Loc.Tr("toolConfirmation.yolo", "YOLO (All)")
                };
                yolo.tooltip = AgentCore.Editor.L10n.Loc.Tr(
                    "toolConfirmation.yoloTooltip",
                    "本会话内所有工具直通,含删除/推送/编译等破坏性操作,慎用");
                yolo.AddToClassList("tool-confirmation-button");
                yolo.AddToClassList("tool-confirmation-button--yolo");
                buttons.Add(yolo);
            }

            footer.Add(buttons);
            _toolConfirmationPanel.Add(footer);

            UpdateStatusLabel(AgentCore.Editor.L10n.Loc.Tr("chat.status.waitingConfirmation", "等待工具确认..."));
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

        /// <summary>
        /// 用户点击"Trust Low/Med"或"YOLO"时,激活对应 scope 并批准当前请求。
        /// </summary>
        private void ResolvePendingToolConfirmationWithTrust(PendingToolConfirmation pending, ToolConfirmationTrustScope scope)
        {
            _sessionTrustScopes.Add(scope);
            SaveSessionTrustScopes();
            CompletePendingToolConfirmation(pending, true, updateUi: true);
        }

        /// <summary>
        /// 用户点击"始终允许"：为该工具（及 action）新建一条用户 allow 规则，并批准当前请求。
        /// 规则写入失败时仍批准本次（用户已显式点击），只记录警告。
        /// 规则写入成功后重新评估排队中的确认，新规则现在放行的一并自动批准（见 <see cref="ApproveQueuedConfirmationsAllowedByRules"/>）。
        /// </summary>
        private void ResolvePendingToolConfirmationWithAllowRule(PendingToolConfirmation pending)
        {
            var request = pending?.Request;
            if (request != null)
            {
                try
                {
                    PermissionRulesStore.Default.AddRule(PermissionRuleScope.User, new PermissionRule
                    {
                        effect = PermissionRule.EffectToString(PermissionRuleEffect.Allow),
                        tool = request.ToolName,
                        action = string.IsNullOrEmpty(request.Action) ? null : request.Action,
                        note = "Added from confirmation dialog",
                        enabled = true
                    });
                    ApproveQueuedConfirmationsAllowedByRules();
                }
                catch (Exception ex)
                {
                    AgentCore.Editor.Utils.AgentCoreLog.Warning($"[AgentCore] Failed to save allow rule for '{request.ToolName}': {ex.Message}");
                }
            }

            CompletePendingToolConfirmation(pending, true, updateUi: true);
        }

        /// <summary>
        /// 规则变化后（"始终允许"新建 allow 规则）重新评估排队中的确认：用与 Dispatcher 相同的匹配器
        /// （<see cref="ToolConfirmationTrust.IsAllowedByPermissionRules"/>）判定，现在被 allow 放行的直接批准并移出队列；
        /// 仍被 ask / deny 规则覆盖、或由 ask 规则触发的保持原状等待用户。当前正在显示的确认由调用方处理。
        /// </summary>
        private void ApproveQueuedConfirmationsAllowedByRules()
        {
            if (_pendingToolConfirmations.Count == 0)
            {
                return;
            }

            IReadOnlyList<ScopedPermissionRule> rules;
            try
            {
                rules = PermissionRulesStore.Default.GetActiveRules();
            }
            catch (Exception ex)
            {
                AgentCore.Editor.Utils.AgentCoreLog.Warning($"[AgentCore] Re-evaluating queued confirmations failed: {ex.Message}");
                return;
            }

            var remaining = new List<PendingToolConfirmation>(_pendingToolConfirmations.Count);
            var approved = 0;
            while (_pendingToolConfirmations.Count > 0)
            {
                var queued = _pendingToolConfirmations.Dequeue();
                if (queued == null || queued.Completion.Task.IsCompleted)
                {
                    CleanupPendingToolConfirmation(queued);
                    continue;
                }

                if (ToolConfirmationTrust.IsAllowedByPermissionRules(queued.Request, rules))
                {
                    CompletePendingToolConfirmation(queued, true, updateUi: false);
                    approved++;
                    continue;
                }

                remaining.Add(queued);
            }

            foreach (var queued in remaining)
            {
                _pendingToolConfirmations.Enqueue(queued);
            }

            if (approved > 0)
            {
                AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] Auto-approved {approved} queued confirmation(s) covered by the new allow rule.");
            }
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

            // 空守卫:_sessionTrustScopes 可能因半初始化处于 null 状态 (v1.6.5 已修复初始化顺序,但保留兜底)
            _sessionTrustScopes?.Clear();
            SaveSessionTrustScopes();
            HideToolConfirmationPanel();
        }

        /// <summary>
        /// 本会话是否处于 YOLO（供 dispatcher 跳过 deny 规则）。可能在非主线程被调用，只做只读查询。
        /// </summary>
        private bool IsSessionYoloActive()
        {
            var scopes = _sessionTrustScopes;
            return scopes != null && scopes.Contains(ToolConfirmationTrustScope.SessionAll);
        }

        /// <summary>
        /// 判定当前请求是否已被本会话某个已激活的 scope 直通（逻辑见 <see cref="ToolConfirmationTrust"/>）。
        /// </summary>
        private bool IsToolConfirmationTrusted(ToolConfirmationRequest request)
        {
            return ToolConfirmationTrust.IsTrusted(request, _sessionTrustScopes);
        }

        /// <summary>
        /// 按当前权限规则重新判定请求是否已被 allow 放行（请求生成后用户才点了"始终允许"的情况）。
        /// 规则读取失败视为未放行——只是照常等待用户，不拦截。仅在主线程调用。
        /// </summary>
        private static bool IsToolConfirmationAllowedByRules(ToolConfirmationRequest request)
        {
            try
            {
                return ToolConfirmationTrust.IsAllowedByPermissionRules(request, PermissionRulesStore.Default.GetActiveRules());
            }
            catch (Exception ex)
            {
                AgentCore.Editor.Utils.AgentCoreLog.Warning($"[AgentCore] Permission rule re-check failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 判定某个 scope 是否被 request 允许提供 (由 tool 层通过 AllowedTrustScopes 决定)。
        /// </summary>
        private static bool IsScopeAllowed(ToolConfirmationRequest request, ToolConfirmationTrustScope scope)
        {
            return ToolConfirmationTrust.IsScopeAllowed(request, scope);
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
