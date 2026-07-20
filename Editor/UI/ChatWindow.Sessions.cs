using System;
using System.Collections.Generic;
using AgentCore.Editor.Core;
using AgentCore.Editor.LLM;
using AgentCore.Editor.Session;
using AgentCore.Editor.UI.Components;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.UI
{
    /// <summary>
    /// ChatWindow 分部类 — 会话管理逻辑。
    /// 包含会话列表刷新、切换、新建、重命名、删除、导出和相对时间格式化。
    /// </summary>
    public partial class ChatWindow
    {
        #region 会话管理

        /// <summary>
        /// 仅更新当前活动会话在侧边栏列表中的标题文本，避免重建整个列表。
        /// 如果找不到对应元素（例如会话刚创建还没在列表中），则 fallback 到 RefreshSessionList()。
        /// </summary>
        private void UpdateCurrentSessionTitle()
        {
            if (_sessionListContainer == null)
            {
                return;
            }

            var currentId = SessionManager.Instance.CurrentSessionId;
            if (string.IsNullOrEmpty(currentId))
            {
                RefreshSessionList();
                return;
            }

            // 尝试通过 name 属性找到当前会话的标题 Label
            var titleLabel = _sessionListContainer.Q<Label>($"session-title-{currentId}");
            if (titleLabel == null)
            {
                // 找不到对应元素，fallback 到完整刷新
                RefreshSessionList();
                return;
            }

            // 从 SessionManager 获取最新标题
            var newTitle = SessionManager.Instance.CurrentSessionTitle;
            if (string.IsNullOrEmpty(newTitle))
            {
                newTitle = SessionData.DefaultTitle;
            }
            if (newTitle.Length > MaxTitleDisplayLength)
            {
                newTitle = newTitle.Substring(0, MaxTitleDisplayLength) + "...";
            }

            // 如果标题没有变化，跳过更新
            if (titleLabel.text == newTitle)
            {
                return;
            }

            titleLabel.text = newTitle;
        }

        /// <summary>
        /// 刷新会话列表 UI。
        /// 从 SessionManager 获取所有会话摘要并重建列表项。
        /// </summary>
        private void RefreshSessionList()
        {
            if (_sessionListContainer == null) return;

            // 重建列表时清理重命名状态，防止旧的 TextField 被销毁后
            // _renamingSessionId 残留导致所有点击被拦截
            _renamingSessionId = null;

            // 保存滚动位置，重建后恢复（避免列表跳回顶部）
            var savedScrollOffset = _sessionListScrollView?.scrollOffset ?? Vector2.zero;

            _sessionListContainer.Clear();

            var sessions = SessionManager.Instance.GetSessionList();
            var currentId = SessionManager.Instance.CurrentSessionId;

            if (sessions == null || sessions.Count == 0)
            {
                var emptyLabel = new Label("暂无会话");
                emptyLabel.style.color = new StyleColor(new Color(0.5f, 0.5f, 0.5f));
                emptyLabel.style.fontSize = 12;
                emptyLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
                emptyLabel.style.paddingTop = 20;
                _sessionListContainer.Add(emptyLabel);
                // 列表为空时不需要恢复滚动位置
                return;
            }

            foreach (var session in sessions)
            {
                var item = CreateSessionItem(session, session.Id == currentId);
                _sessionListContainer.Add(item);
            }

            // 恢复滚动位置（延迟一帧，确保布局已更新）
            if (_sessionListScrollView != null)
            {
                _sessionListScrollView.schedule.Execute(() =>
                {
                    if (_sessionListScrollView != null)
                    {
                        _sessionListScrollView.scrollOffset = savedScrollOffset;
                    }
                });
            }
        }

        /// <summary>
        /// 创建单个会话列表项 VisualElement。
        /// </summary>
        /// <param name="session">会话摘要数据</param>
        /// <param name="isActive">是否为当前活动会话</param>
        /// <returns>会话列表项 VisualElement</returns>
        private VisualElement CreateSessionItem(SessionSummary session, bool isActive)
        {
            var item = new VisualElement();
            item.name = $"session-item-{session.Id}";
            item.AddToClassList("session-item");
            item.userData = session.Id;

            if (isActive)
            {
                item.AddToClassList("session-active");
            }

            // 会话标题
            var title = session.Title;
            if (string.IsNullOrEmpty(title))
            {
                title = SessionData.DefaultTitle;
            }
            if (title.Length > MaxTitleDisplayLength)
            {
                title = title.Substring(0, MaxTitleDisplayLength) + "...";
            }

            var titleLabel = new Label(title);
            titleLabel.name = $"session-title-{session.Id}";
            titleLabel.AddToClassList("session-item-title");
            item.Add(titleLabel);

            // 最后更新时间（相对时间）
            var timeLabel = new Label(FormatRelativeTime(session.UpdatedAt));
            timeLabel.AddToClassList("session-item-time");
            item.Add(timeLabel);

            // 点击切换会话
            item.RegisterCallback<ClickEvent>(evt =>
            {
                // 如果正在重命名，不处理点击
                if (!string.IsNullOrEmpty(_renamingSessionId))
                {
                    return;
                }

                var sessionId = item.userData as string;
                if (!string.IsNullOrEmpty(sessionId))
                {
                    SwitchToSession(sessionId);
                }
            });

            // 右键上下文菜单
            item.RegisterCallback<ContextClickEvent>(evt =>
            {
                evt.StopPropagation();
                var sessionId = item.userData as string;
                if (!string.IsNullOrEmpty(sessionId))
                {
                    ShowSessionContextMenu(sessionId, session.Title, item);
                }
            });

            return item;
        }

        /// <summary>
        /// 切换到指定会话。
        /// </summary>
        /// <param name="sessionId">目标会话 ID</param>
        private void SwitchToSession(string sessionId)
        {
            if (_agentLoop == null) return;

            // 如果已经是当前会话，不做任何操作
            if (SessionManager.Instance.CurrentSessionId == sessionId)
            {
                return;
            }

            // 如果 Agent 正忙，不允许切换
            if (_agentLoop.CurrentState != AgentState.Idle)
            {
                AgentCoreLog.Warning("[AgentCore] Cannot switch session while agent is busy.");
                return;
            }

            try
            {
                // 1. 保存当前会话（ForceSave 内部会跳过无用户消息的空会话）
                SessionManager.Instance.ForceSave(
                    new List<ChatMessage>(_agentLoop.Messages),
                    new List<ConversationTurn>(_agentLoop.ConversationTurns));

                // 1.5 触发自动记忆（fire-and-forget，仅在有实际对话内容时生效）
                try
                {
                    SessionManager.Instance.TriggerAutoMemory(_agentLoop.LLMClient);
                }
                catch (Exception amEx)
                {
                    AgentCoreLog.Warning($"[AgentCore] Auto-memory trigger on session switch failed (non-fatal): {amEx.Message}");
                }

                // 2. 加载目标会话（AgentLoop.LoadSession 不再重复保存）
                if (!_agentLoop.LoadSession(sessionId))
                {
                    AgentCoreLog.Warning($"[AgentCore] Failed to switch to session: {sessionId}");
                    return;
                }

                // 3. 重建消息气泡
                RebuildMessageBubbles();

                // 3.5 Phase 4.5: 切换会话后更新文件变更面板
                // 新会话可能没有文件变更数据，需要清空面板；或者恢复了 Domain Reload 前的数据
                if (_agentLoop.FileTracker != null && _agentLoop.FileTracker.HasChanges)
                {
                    _fileChangeSummaryPanel?.UpdateChanges(_agentLoop.FileTracker.GetSummaries());
                }
                else
                {
                    _fileChangeSummaryPanel?.ClearAndHide();
                }

                // 3.6 Phase 6.0.4: 切换会话后更新上下文使用情况面板
                UpdateContextUsagePanel();

                // 4. 刷新会话列表（更新高亮）
                RefreshSessionList();

                AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] Switched to session: {sessionId}");
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"[AgentCore] Error switching session: {ex.Message}");
            }
        }

        /// <summary>
        /// 新建会话按钮点击处理。
        /// </summary>
        private void OnNewSessionClicked()
        {
            if (_agentLoop == null) return;

            if (_agentLoop.CurrentState != AgentState.Idle)
            {
                AgentCoreLog.Warning("[AgentCore] Cannot create new session while agent is busy.");
                return;
            }

            try
            {
                // 1. 重置对话（ResetConversation 内部已包含 ForceSave + TriggerAutoMemory + 创建新会话）
                ClearMessages();
                _agentLoop.ResetConversation();

                // 2. 刷新会话列表
                RefreshSessionList();

                AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] New session created.");
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"[AgentCore] Error creating new session: {ex.Message}");
            }
        }

        /// <summary>
        /// 显示会话右键上下文菜单。
        /// </summary>
        /// <param name="sessionId">会话 ID</param>
        /// <param name="currentTitle">当前标题</param>
        /// <param name="itemElement">会话列表项 VisualElement</param>
        private void ShowSessionContextMenu(string sessionId, string currentTitle, VisualElement itemElement)
        {
            var menu = new GenericMenu();

            menu.AddItem(new GUIContent("自动重命名"), false, () =>
            {
                AutoRenameSession(sessionId, itemElement);
            });

            menu.AddItem(new GUIContent("重命名"), false, () =>
            {
                BeginRenameSession(sessionId, currentTitle, itemElement);
            });

            menu.AddSeparator("");

            menu.AddItem(new GUIContent("导出/Markdown (.md)"), false, () =>
            {
                ExportSession(sessionId, SessionExporter.ExportFormat.Markdown);
            });

            menu.AddItem(new GUIContent("导出/JSON (.json)"), false, () =>
            {
                ExportSession(sessionId, SessionExporter.ExportFormat.Json);
            });

            menu.AddSeparator("");

            menu.AddItem(new GUIContent("删除"), false, () =>
            {
                DeleteSessionWithConfirm(sessionId);
            });

            menu.ShowAsContext();
        }

        /// <summary>
        /// 自动重命名会话：基于会话最近上下文调用 LLM 生成能反映当前主话题的标题。
        /// <para>
        /// 与手动重命名互补 —— 手动重命名让用户直接编辑，自动重命名交给 LLM 概括当前话题，
        /// 解决"话题漂移后标题仍停留在最初内容"的问题。
        /// </para>
        /// </summary>
        /// <param name="sessionId">目标会话 ID。</param>
        /// <param name="itemElement">会话列表项元素（用于生成期间的视觉反馈）。</param>
        private async void AutoRenameSession(string sessionId, VisualElement itemElement)
        {
            if (string.IsNullOrEmpty(sessionId)) return;

            // 生成期间在标题上给出临时反馈
            var titleLabel = itemElement?.Q<Label>($"session-title-{sessionId}");
            string originalText = titleLabel?.text;
            if (titleLabel != null)
            {
                titleLabel.text = "正在生成标题…";
            }

            string newTitle = null;
            try
            {
                newTitle = await SessionAutoTitleService.GenerateTitleAsync(sessionId);
            }
            catch (System.Exception ex)
            {
                AgentCoreLog.Error($"[ChatWindow] AutoRenameSession failed: {ex.Message}");
            }

            // await 续体在 Unity 同步上下文回到主线程；仍用 delayCall 确保 UI/存储操作安全
            EditorApplication.delayCall += () =>
            {
                if (!string.IsNullOrWhiteSpace(newTitle))
                {
                    SessionManager.Instance.RenameSession(sessionId, newTitle);
                    RefreshSessionList();
                }
                else
                {
                    // 失败：恢复原标题文本，刷新列表以清除临时提示
                    if (titleLabel != null && originalText != null)
                    {
                        titleLabel.text = originalText;
                    }
                    RefreshSessionList();
                    AgentCoreLog.Warning("[ChatWindow] 自动重命名未生成有效标题（LLM 不可用或无足够上下文）。");
                }
            };
        }

        /// <summary>
        /// 开始内联重命名会话。
        /// 将标题 Label 替换为可编辑的 TextField。
        /// </summary>
        /// <param name="sessionId">会话 ID</param>
        /// <param name="currentTitle">当前标题</param>
        /// <param name="itemElement">会话列表项 VisualElement</param>
        private void BeginRenameSession(string sessionId, string currentTitle, VisualElement itemElement)
        {
            if (!string.IsNullOrEmpty(_renamingSessionId)) return;
            _renamingSessionId = sessionId;

            // 查找标题 Label 并隐藏
            var titleLabel = itemElement.Q<Label>(className: "session-item-title");
            if (titleLabel != null)
            {
                titleLabel.style.display = DisplayStyle.None;
            }

            // 创建内联编辑 TextField
            var renameField = new TextField();
            renameField.AddToClassList("session-rename-field");
            renameField.value = currentTitle ?? "";
            renameField.selectAllOnFocus = true;

            // 插入到标题 Label 的位置
            int insertIndex = titleLabel != null ? itemElement.IndexOf(titleLabel) : 0;
            itemElement.Insert(insertIndex + 1, renameField);

            // 延迟聚焦（确保元素已布局）
            renameField.schedule.Execute(() => renameField.Focus());

            // 确认重命名（Enter 或失去焦点）
            Action commitRename = () =>
            {
                if (_renamingSessionId != sessionId) return;

                var newTitle = renameField.value?.Trim();
                if (!string.IsNullOrEmpty(newTitle) && newTitle != currentTitle)
                {
                    SessionManager.Instance.RenameSession(sessionId, newTitle);
                }

                // 清理：移除 TextField，恢复 Label
                _renamingSessionId = null;
                renameField.RemoveFromHierarchy();
                if (titleLabel != null)
                {
                    titleLabel.style.display = DisplayStyle.Flex;
                }

                RefreshSessionList();
            };

            renameField.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                {
                    evt.PreventDefault();
                    evt.StopPropagation();
                    commitRename();
                }
                else if (evt.keyCode == KeyCode.Escape)
                {
                    evt.PreventDefault();
                    evt.StopPropagation();
                    // 取消重命名
                    _renamingSessionId = null;
                    renameField.RemoveFromHierarchy();
                    if (titleLabel != null)
                    {
                        titleLabel.style.display = DisplayStyle.Flex;
                    }
                }
            });

            renameField.RegisterCallback<FocusOutEvent>(_ =>
            {
                // 失去焦点时提交
                if (_renamingSessionId == sessionId)
                {
                    commitRename();
                }
            });
        }

        /// <summary>
        /// 删除会话（带确认对话框）。
        /// </summary>
        /// <param name="sessionId">要删除的会话 ID</param>
        private void DeleteSessionWithConfirm(string sessionId)
        {
            var confirmed = EditorUtility.DisplayDialog(
                "删除会话",
                "确定要删除此会话吗？此操作不可撤销。",
                "删除",
                "取消");

            if (!confirmed) return;

            var isCurrentSession = SessionManager.Instance.CurrentSessionId == sessionId;

            // 执行删除
            SessionManager.Instance.DeleteSession(sessionId);

            if (isCurrentSession)
            {
                // 删除的是当前活动会话，需要切换到其他会话或创建新会话
                var sessions = SessionManager.Instance.GetSessionList();
                if (sessions != null && sessions.Count > 0)
                {
                    // 切换到最近的会话
                    SwitchToSession(sessions[0].Id);
                }
                else
                {
                    // 没有其他会话，创建新会话
                    ClearMessages();
                    _agentLoop?.ResetConversation();
                }
            }

            RefreshSessionList();
            AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] Session deleted: {sessionId}");
        }

        /// <summary>
        /// 将 UTC 时间格式化为相对时间字符串。
        /// </summary>
        /// <param name="utcTime">UTC 时间</param>
        /// <returns>相对时间字符串（如"刚刚"、"5分钟前"、"昨天"）</returns>
        private static string FormatRelativeTime(DateTime utcTime)
        {
            var now = DateTime.UtcNow;
            var diff = now - utcTime;

            if (diff.TotalSeconds < 60)
                return "刚刚";
            if (diff.TotalMinutes < 60)
                return $"{(int)diff.TotalMinutes}分钟前";
            if (diff.TotalHours < 24)
                return $"{(int)diff.TotalHours}小时前";
            if (diff.TotalDays < 2)
                return "昨天";
            if (diff.TotalDays < 7)
                return $"{(int)diff.TotalDays}天前";
            if (diff.TotalDays < 30)
                return $"{(int)(diff.TotalDays / 7)}周前";

            return utcTime.ToLocalTime().ToString("MM/dd");
        }

        /// <summary>
        /// 显示导出格式选择菜单（由 Ctrl+Shift+E 快捷键触发）。
        /// </summary>
        private void ShowExportMenu()
        {
            var sessionId = SessionManager.Instance?.CurrentSessionId;
            if (string.IsNullOrEmpty(sessionId))
            {
                AgentCoreLog.Warning("[AgentCore] No active session to export.");
                return;
            }

            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("导出为 Markdown (.md)"), false, () =>
            {
                ExportSession(sessionId, SessionExporter.ExportFormat.Markdown);
            });
            menu.AddItem(new GUIContent("导出为 JSON (.json)"), false, () =>
            {
                ExportSession(sessionId, SessionExporter.ExportFormat.Json);
            });
            menu.ShowAsContext();
        }

        /// <summary>
        /// 导出指定会话到文件。弹出文件保存对话框让用户选择路径。
        /// </summary>
        /// <param name="sessionId">要导出的会话 ID</param>
        /// <param name="format">导出格式</param>
        private void ExportSession(string sessionId, SessionExporter.ExportFormat format)
        {
            try
            {
                var session = SessionStorage.Load(sessionId);
                if (session == null)
                {
                    AgentCoreLog.Error($"[AgentCore] Failed to load session: {sessionId}");
                    return;
                }

                var defaultName = SessionExporter.GetDefaultFileName(session, format);
                var extension = format == SessionExporter.ExportFormat.Markdown ? "md" : "json";
                var filterDisplay = format == SessionExporter.ExportFormat.Markdown ? "Markdown files" : "JSON files";

                var path = EditorUtility.SaveFilePanel(
                    "导出会话",
                    "",
                    defaultName,
                    extension
                );

                if (string.IsNullOrEmpty(path))
                    return; // 用户取消

                SessionExporter.ExportToFile(session, path, format);
                AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] Session exported to: {path}");
                EditorUtility.RevealInFinder(path);
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"[AgentCore] Export failed: {ex.Message}");
            }
        }

        #endregion
    }
}
