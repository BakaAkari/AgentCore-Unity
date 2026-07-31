using AgentCore.Editor.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace AgentCore.Editor.UI
{
    /// <summary>
    /// ChatWindow 分部类 — Domain Reload 通知卡片 UI。
    /// 包含 Domain Reload 中断通知的创建、详情行构建和状态更新。
    /// </summary>
    public partial class ChatWindow
    {
        #region Domain Reload 通知 UI

        /// <summary>
        /// 在聊天区域添加 Domain Reload 中断通知卡片。
        /// 显示中断阶段、编译结果等信息，并提供恢复状态指示。
        /// </summary>
        /// <param name="phase">中断时的阶段</param>
        /// <param name="toolName">中断时正在执行的工具名（可为 null）</param>
        /// <param name="compilationSucceeded">编译是否成功</param>
        /// <param name="compilationErrors">编译错误信息（可为 null）</param>
        /// <returns>通知卡片 VisualElement 引用，用于后续状态更新</returns>
        private VisualElement AddDomainReloadNotification(
            InterruptPhase phase,
            string toolName,
            bool compilationSucceeded,
            string compilationErrors)
        {
            if (_messageContainer == null) return null;

            // === 通知卡片容器 ===
            var card = new VisualElement();
            card.AddToClassList("domain-reload-notification");

            // === 标题行 ===
            var header = new VisualElement();
            header.AddToClassList("domain-reload-notification__header");

            var headerIcon = new Label("");
            headerIcon.AddToClassList("domain-reload-notification__header-icon");
            header.Add(headerIcon);

            var headerText = new Label(AgentCore.Editor.L10n.Loc.Tr("domainReload.header", "检测到 Domain Reload 中断"));
            headerText.AddToClassList("domain-reload-notification__header-text");
            header.Add(headerText);

            card.Add(header);

            // === 详情行：中断原因 ===
            var reasonRow = CreateDetailRow(
                AgentCore.Editor.L10n.Loc.Tr("domainReload.reasonLabel", "中断原因："),
                AgentCore.Editor.L10n.Loc.Tr("domainReload.reasonValue", "编译触发 Domain Reload"));
            card.Add(reasonRow);

            // === 详情行：中断阶段 ===
            string phaseText = phase switch
            {
                InterruptPhase.Streaming => AgentCore.Editor.L10n.Loc.Tr("domainReload.phase.streaming", "流式响应中 (Streaming)"),
                InterruptPhase.ExecutingTool => AgentCore.Editor.L10n.Loc.Tr("domainReload.phase.executingTool", "工具执行中 (ExecutingTool)"),
                InterruptPhase.WaitingCompilation => AgentCore.Editor.L10n.Loc.Tr("domainReload.phase.waitingCompilation", "等待编译 (WaitingCompilation)"),
                _ => AgentCore.Editor.L10n.Loc.Tr("domainReload.phase.unknown", "未知阶段")
            };
            if (!string.IsNullOrEmpty(toolName))
            {
                phaseText += $" — {toolName}";
            }
            var phaseRow = CreateDetailRow(AgentCore.Editor.L10n.Loc.Tr("domainReload.phaseLabel", "中断阶段："), phaseText);
            card.Add(phaseRow);

            // === 详情行：编译结果 ===
            string compileIcon = compilationSucceeded ? "" : "";
            string compileText = compilationSucceeded
                ? AgentCore.Editor.L10n.Loc.Tr("domainReload.compileSuccess", "编译成功")
                : AgentCore.Editor.L10n.Loc.Tr("domainReload.compileFailed", "编译失败");
            if (!compilationSucceeded && !string.IsNullOrEmpty(compilationErrors))
            {
                // 截断过长的错误信息
                var errMsg = compilationErrors.Length > 100
                    ? compilationErrors.Substring(0, 100) + "..."
                    : compilationErrors;
                compileText += $" — {errMsg}";
            }
            var compileRow = CreateDetailRow(AgentCore.Editor.L10n.Loc.Tr("domainReload.compileLabel", "编译结果："), $"{compileIcon} {compileText}");
            // 为编译结果值添加颜色修饰
            var compileValue = compileRow.Q<Label>(className: "domain-reload-notification__detail-value");
            if (compileValue != null)
            {
                compileValue.AddToClassList(compilationSucceeded
                    ? "domain-reload-notification__compile-success"
                    : "domain-reload-notification__compile-error");
            }
            card.Add(compileRow);

            // === 状态行（初始为"恢复中..."） ===
            var statusRow = new VisualElement();
            statusRow.AddToClassList("domain-reload-notification__status");
            statusRow.name = "reload-notification-status";

            var statusIcon = new Label("");
            statusIcon.AddToClassList("domain-reload-notification__status-icon");
            statusIcon.name = "reload-status-icon";
            statusRow.Add(statusIcon);

            var statusText = new Label(AgentCore.Editor.L10n.Loc.Tr("domainReload.status.recovering", "正在恢复会话..."));
            statusText.AddToClassList("domain-reload-notification__status-text");
            statusText.name = "reload-status-text";
            statusRow.Add(statusText);

            card.Add(statusRow);

            _messageListManager?.AddItem(card);
            ScrollToBottom(force: true); // Domain Reload 通知添加，强制滚动到底部

            AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] Domain Reload notification added: phase={phase}, tool={toolName}, " +
                      $"compilationOk={compilationSucceeded}");

            return card;
        }

        /// <summary>
        /// 创建通知卡片的详情行（标签 + 值）。
        /// </summary>
        /// <param name="label">标签文本</param>
        /// <param name="value">值文本</param>
        /// <returns>详情行 VisualElement</returns>
        private static VisualElement CreateDetailRow(string label, string value)
        {
            var row = new VisualElement();
            row.AddToClassList("domain-reload-notification__detail");

            var labelElem = new Label(label);
            labelElem.AddToClassList("domain-reload-notification__detail-label");
            row.Add(labelElem);

            var valueElem = new Label(value);
            valueElem.AddToClassList("domain-reload-notification__detail-value");
            row.Add(valueElem);

            return row;
        }

        /// <summary>
        /// 更新 Domain Reload 通知卡片的恢复状态。
        /// </summary>
        /// <param name="card">通知卡片 VisualElement（由 AddDomainReloadNotification 返回）</param>
        /// <param name="success">恢复是否成功</param>
        /// <param name="errorMessage">失败时的错误信息（可为 null）</param>
        private static void UpdateDomainReloadNotificationStatus(
            VisualElement card,
            bool success,
            string errorMessage = null)
        {
            if (card == null) return;

            // 查找状态行元素
            var statusIcon = card.Q<Label>("reload-status-icon");
            var statusText = card.Q<Label>("reload-status-text");

            if (success)
            {
                // 恢复成功
                card.AddToClassList("domain-reload-notification--success");
                if (statusIcon != null) statusIcon.text = "";
                if (statusText != null) statusText.text = AgentCore.Editor.L10n.Loc.Tr("domainReload.status.recovered", "会话已恢复，继续执行中");
            }
            else
            {
                // 恢复失败
                card.AddToClassList("domain-reload-notification--error");
                if (statusIcon != null) statusIcon.text = "";

                var failText = AgentCore.Editor.L10n.Loc.Tr("domainReload.recoverStatus.failed", "恢复失败");
                if (!string.IsNullOrEmpty(errorMessage))
                {
                    failText += $"：{errorMessage}";
                }
                failText += AgentCore.Editor.L10n.Loc.Tr("domainReload.hintSuggestion", "\n 建议：请手动重新发送消息继续操作");

                if (statusText != null) statusText.text = failText;
            }

            AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] Domain Reload notification status updated: success={success}" +
                      (string.IsNullOrEmpty(errorMessage) ? "" : $", error={errorMessage}"));
        }

        #endregion
    }
}
