using System;
using AgentCore.Editor.Core;
using AgentCore.Editor.Session;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.UI
{
    /// <summary>
    /// ChatWindow 分部类 — 会话恢复逻辑。
    /// 包含窗口启动时的会话恢复和 Domain Reload 后的自动恢复流程。
    /// </summary>
    public partial class ChatWindow
    {
        #region 会话恢复

        /// <summary>
        /// 尝试恢复上一次的会话。
        /// 在窗口创建时调用，从 SessionManager 加载上一次的会话并重建 UI。
        /// </summary>
        private void TryRestoreSession()
        {
            if (_agentLoop == null) return;

            try
            {
                var session = SessionManager.Instance.TryRestoreLastSession();
                if (session == null || session.Turns == null || session.Turns.Count == 0)
                {
                    Debug.Log("[AgentCore] No previous session to restore, starting fresh.");
                    // 即使没有会话可恢复，也要清除可能残留的中断标记
                    DomainReloadState.instance.ClearInterruption();
                    // 修复 #5: Domain Reload 路径中延迟了会话创建，如果恢复失败则在此补创建
                    EnsureSessionExists();
                    return;
                }

                // 通过 AgentLoop.LoadSession 恢复对话状态
                if (!_agentLoop.LoadSession(session.Id))
                {
                    Debug.LogWarning("[AgentCore] Failed to restore session via AgentLoop.");
                    DomainReloadState.instance.ClearInterruption();
                    // 修复 #5: 恢复失败时也需要确保有活动会话
                    EnsureSessionExists();
                    return;
                }

                // 重建 UI 消息气泡
                RebuildMessageBubbles();

                // Phase 4.5: 恢复文件变更面板（Domain Reload 后 FileChangeTracker 数据已在 LoadSession 中恢复）
                _agentLoop.EmitFileChangesUpdatedEvent();

                Debug.Log($"[AgentCore] Session restored: {session.Id} ({session.Title}, {session.Turns.Count} turns)");

                // Domain Reload Resilience Phase 2 & 3: 检查是否有中断标记并自动恢复
                var reloadState = DomainReloadState.instance;
                if (reloadState.WasInterrupted)
                {
                    Debug.Log($"[AgentCore] Domain Reload detected: session {reloadState.InterruptedSessionId} " +
                              $"was interrupted during {reloadState.InterruptPhase}" +
                              (string.IsNullOrEmpty(reloadState.LastToolName) ? "" : $" (tool: {reloadState.LastToolName})") +
                              (reloadState.HadPendingToolCalls ? " [had pending tool calls]" : "") +
                              $" at {reloadState.InterruptTimestamp}");

                    // Phase 2: 设置编译结果（Domain Reload 完成意味着编译已结束）
                    // 如果 Domain Reload 成功完成且我们的代码正在运行，说明编译通过。
                    // Unity 在编译失败时不会完成 Domain Reload（会停留在错误状态）。
                    bool compilationSucceeded = !EditorUtility.scriptCompilationFailed;
                    string compilationErrors = compilationSucceeded
                        ? null
                        : "编译失败，请检查 Unity Console 中的错误信息";
                    Debug.Log($"[AgentCore] Post-reload compilation check: succeeded={compilationSucceeded}");

                    reloadState.SetCompilationResult(compilationSucceeded, compilationErrors);

                    // Phase 3: 在聊天区域显示恢复通知卡片（带"恢复中..."状态）
                    var notificationCard = AddDomainReloadNotification(
                        reloadState.InterruptPhase,
                        reloadState.LastToolName,
                        compilationSucceeded,
                        compilationErrors);

                    // 调用 AgentLoop.TryResumeAfterReload() 触发自动恢复
                    bool resumed = _agentLoop.TryResumeAfterReload();

                    // Phase 3: 根据恢复结果更新通知卡片状态
                    if (resumed)
                    {
                        Debug.Log("[AgentCore] Domain Reload recovery initiated successfully.");
                        UpdateDomainReloadNotificationStatus(notificationCard, success: true);
                    }
                    else
                    {
                        Debug.Log("[AgentCore] Domain Reload recovery skipped or failed, continuing normally.");
                        UpdateDomainReloadNotificationStatus(notificationCard, success: false,
                            errorMessage: "恢复未执行，可能是中断阶段不支持自动恢复");
                    }
                }
                
                // 恢复上下文使用情况显示
                UpdateContextUsagePanel();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AgentCore] Failed to restore session: {ex.Message}");
                // 修复 #5: 异常时也需要确保有活动会话
                EnsureSessionExists();
            }
        }

        /// <summary>
        /// 确保 SessionManager 有活动会话。
        /// 修复 #5: Domain Reload 路径中 Initialize() 延迟了会话创建，
        /// 如果 TryRestoreSession() 未能恢复会话，则在此补创建新会话。
        /// </summary>
        private static void EnsureSessionExists()
        {
            if (string.IsNullOrEmpty(SessionManager.Instance.CurrentSessionId))
            {
                Debug.Log("[AgentCore] No active session after restore attempt, creating new session.");
                SessionManager.Instance.CreateNewSession();
            }
        }

        #endregion
    }
}
