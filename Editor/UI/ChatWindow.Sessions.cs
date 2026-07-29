using System;
using System.Collections.Generic;
using System.Linq;
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

            // 从 SessionManager 获取最新标题, 走 GetDisplayTitle 做本地化 (存储值 = "新会话" 时按当前语言展示)
            var newTitle = SessionData.GetDisplayTitle(SessionManager.Instance.CurrentSessionTitle);
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
                var emptyLabel = new Label(AgentCore.Editor.L10n.Loc.Tr("session.empty", "暂无会话"));
                emptyLabel.style.color = new StyleColor(new Color(0.5f, 0.5f, 0.5f));
                emptyLabel.style.fontSize = 12;
                emptyLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
                emptyLabel.style.paddingTop = 20;
                _sessionListContainer.Add(emptyLabel);
                // 列表为空时不需要恢复滚动位置
                return;
            }

            // v1.12.0：分组渲染。活动会话按 tag 分组（Foldout），已归档会话收进单独的归档 Foldout。
            var active = sessions.Where(s => !s.Archived).ToList();
            var archived = sessions.Where(s => s.Archived).ToList();

            // 活动会话按 tag 分组：已登记 tag 按 registry 顺序（bucket 0）；未登记 tag 字典序（bucket 1）；
            // null/空 tag 归入"未分类"（bucket 2，排在最后）。registry 缺失时所有 tag 落入 bucket 1，行为等同旧版。
            var registryOrder = SessionTagRegistry.LoadOrderMap();
            var groups = active
                .GroupBy(s => string.IsNullOrEmpty(s.Tag) ? null : s.Tag)
                .OrderBy(g => g.Key == null ? 2 : (registryOrder.ContainsKey(g.Key) ? 0 : 1))
                .ThenBy(g => g.Key != null && registryOrder.TryGetValue(g.Key, out var o) ? o : int.MaxValue)
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var group in groups)
            {
                var tagKey = group.Key;
                var prefKey = tagKey ?? "__uncategorized__";
                var tagDisplay = tagKey ?? AgentCore.Editor.L10n.Loc.Tr("session.group.uncategorized", "未分类");
                var ordered = group.OrderByDescending(s => s.UpdatedAt).ToList();
                var headerText = $"{tagDisplay} ({ordered.Count})";

                var foldout = BuildSessionGroupFoldout(prefKey, headerText, defaultExpanded: true, ordered, currentId, showTagChip: false, tagName: tagKey);
                _sessionListContainer.Add(foldout);
            }

            // 归档区：仅在存在已归档会话时渲染；扁平时间倒序；默认折叠；会话项显示 tag chip。
            if (archived.Count > 0)
            {
                var orderedArchived = archived.OrderByDescending(s => s.UpdatedAt).ToList();
                var headerText = $"{AgentCore.Editor.L10n.Loc.Tr("session.group.archived", "已归档")} ({orderedArchived.Count})";

                var foldout = BuildSessionGroupFoldout("__archived__", headerText, defaultExpanded: false, orderedArchived, currentId, showTagChip: true);
                _sessionListContainer.Add(foldout);
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

        /// <summary>EditorPrefs 中存储分组 Foldout 折叠状态的 key 前缀（per-user / per-machine，跨 Unity 重启保留）。</summary>
        private const string FoldoutPrefPrefix = "AgentCore.SessionOrg.Foldout.";

        /// <summary>读取分组 Foldout 的展开状态。</summary>
        private static bool LoadFoldoutState(string key, bool defaultValue)
        {
            return EditorPrefs.GetBool(FoldoutPrefPrefix + key, defaultValue);
        }

        /// <summary>持久化分组 Foldout 的展开状态。</summary>
        private static void SaveFoldoutState(string key, bool value)
        {
            EditorPrefs.SetBool(FoldoutPrefPrefix + key, value);
        }

        /// <summary>
        /// 构建一个分组 Foldout（组头 + 组内会话项）。
        /// 折叠状态从 EditorPrefs 恢复并在变更时持久化。
        /// </summary>
        /// <param name="prefKey">EditorPrefs 折叠状态 key（不含前缀）。</param>
        /// <param name="headerText">组头文本（含 count）。</param>
        /// <param name="defaultExpanded">无持久化记录时的默认展开状态。</param>
        /// <param name="items">组内会话摘要（调用方已排序）。</param>
        /// <param name="currentId">当前活动会话 ID（用于高亮）。</param>
        /// <param name="showTagChip">组内会话项是否显示 tag chip（归档区为 true）。</param>
        /// <param name="tagName">
        /// 该组对应的实际 tag 名称。仅 tagged 组传入非空值；"未分类" / "已归档" 传 null。
        /// 非空时在组头（Foldout 的 Toggle 行）挂右键菜单以管理 tag（重命名 / 排序 / 删除）。
        /// </param>
        private Foldout BuildSessionGroupFoldout(
            string prefKey, string headerText, bool defaultExpanded,
            IEnumerable<SessionSummary> items, string currentId, bool showTagChip, string tagName = null)
        {
            var foldout = new Foldout { text = headerText };
            foldout.AddToClassList("session-group-header");
            foldout.value = LoadFoldoutState(prefKey, defaultExpanded);

            // 记住每个用户对该组的折叠选择。组内会话项无 bool 值控件，不存在事件冒泡混淆。
            foldout.RegisterValueChangedCallback(evt => SaveFoldoutState(prefKey, evt.newValue));

            // tag 组头右键菜单：把回调挂在 Foldout 的 Toggle（可见的组头行）上，而非整个 Foldout，
            // 避免右键组内会话项时误触发（会话项自身已有 ContextClickEvent 菜单）。
            if (tagName != null)
            {
                var headerToggle = foldout.Q<Toggle>();
                var hookTarget = (VisualElement)headerToggle ?? foldout;
                hookTarget.RegisterCallback<MouseDownEvent>(evt =>
                {
                    if (evt.button != 1) return; // 只响应右键，左键保留 Foldout 展开/折叠
                    ShowTagGroupContextMenu(tagName);
                    evt.StopPropagation();
                });
            }

            foreach (var session in items)
            {
                var item = CreateSessionItem(session, session.Id == currentId, showTagChip);
                foldout.Add(item);
            }

            return foldout;
        }

        /// <summary>
        /// 创建单个会话列表项 VisualElement。
        /// </summary>
        /// <param name="session">会话摘要数据</param>
        /// <param name="isActive">是否为当前活动会话</param>
        /// <param name="showTagChip">是否在标题前显示 tag chip（归档区按时间分组，需要 chip 辨识 tag）。</param>
        /// <returns>会话列表项 VisualElement</returns>
        private VisualElement CreateSessionItem(SessionSummary session, bool isActive, bool showTagChip = false)
        {
            var item = new VisualElement();
            item.name = $"session-item-{session.Id}";
            item.AddToClassList("session-item");
            item.userData = session.Id;

            if (isActive)
            {
                item.AddToClassList("session-active");
            }

            // tag chip（仅归档区显示）：置于最前，方便在按时间分组的归档区辨识会话 tag。
            if (showTagChip && !string.IsNullOrEmpty(session.Tag))
            {
                var tagChip = new Label(session.Tag);
                tagChip.AddToClassList("session-item-tag-chip");
                item.Insert(0, tagChip);
            }

            // 会话标题 (走 GetDisplayTitle 本地化)
            var title = SessionData.GetDisplayTitle(session.Title);
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
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"[AgentCore] Error saving current session before switch: {ex.Message}");
                return;
            }

            // 2. 异步加载目标会话（#1 CRITICAL：会话文件读取移出主线程，消除切换卡顿）。
            //    await 续体由 Unity 同步上下文回到主线程，后续 UI 重建仍在主线程执行。
            AsyncHelper.RunAsync(
                async () =>
                {
                    // AgentLoop.LoadSessionAsync 不再重复保存
                    if (!await _agentLoop.LoadSessionAsync(sessionId))
                    {
                        AgentCoreLog.Warning($"[AgentCore] Failed to switch to session: {sessionId}");
                        return;
                    }

                    OnSessionSwitchApplied(sessionId);
                },
                onError: ex =>
                {
                    AgentCoreLog.Error($"[AgentCore] Error switching session: {ex.Message}");
                });
        }

        /// <summary>
        /// 会话异步加载完成后的 UI 收尾（在主线程执行）：重置信任 scope、重建气泡、
        /// 刷新文件变更面板 / 上下文面板 / 会话列表。
        /// </summary>
        private void OnSessionSwitchApplied(string sessionId)
        {
            try
            {
                // 2.5 重置会话级工具信任 scope（YOLO / Trust Low-Med）。
                // 切换到另一段对话 = 进入新的对话上下文，上一段对话开启的直通状态不应延续。
                // 与 OnNewSessionClicked 语义一致：信任绑定对话 session。
                ClearPendingToolConfirmations();

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
                AgentCoreLog.Error($"[AgentCore] Error finalizing session switch: {ex.Message}");
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

                // 1.5 重置会话级工具信任 scope（YOLO / Trust Low-Med）。
                // 信任生命周期绑定到"对话 session"：新建对话即失效，避免上一段对话开启的
                // 直通状态无感知地延续到新对话。Domain Reload 仍保留（SessionState），
                // Editor 完全重启自然归零。
                ClearPendingToolConfirmations();

                // 2. 刷新会话列表
                RefreshSessionList();

                AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] New session created (tool trust scopes reset).");
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
            var currentSummary = SessionManager.Instance.GetSessionList().FirstOrDefault(s => s.Id == sessionId);
            if (currentSummary == null) return;

            var menu = new GenericMenu();

            menu.AddItem(new GUIContent(AgentCore.Editor.L10n.Loc.Tr("session.menu.autoRename", "自动重命名")), false, () =>
            {
                AutoRenameSession(sessionId, itemElement);
            });

            menu.AddItem(new GUIContent(AgentCore.Editor.L10n.Loc.Tr("session.menu.rename", "重命名")), false, () =>
            {
                BeginRenameSession(sessionId, currentTitle, itemElement);
            });

            // v1.12.0：tag 分组 / 归档管理
            menu.AddSeparator("");

            // tag 子菜单：现有 tag 列表 + 新建 tag + 移除 tag
            var existingTags = SessionManager.Instance.GetSessionList()
                .Where(s => !string.IsNullOrEmpty(s.Tag))
                .Select(s => s.Tag)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var setTagLabel = string.IsNullOrEmpty(currentSummary.Tag)
                ? AgentCore.Editor.L10n.Loc.Tr("session.menu.setTag", "设置 tag")
                : AgentCore.Editor.L10n.Loc.Tr("session.menu.changeTag", "修改 tag");

            foreach (var tag in existingTags)
            {
                var capturedTag = tag;
                menu.AddItem(new GUIContent($"{setTagLabel}/{capturedTag}"), currentSummary.Tag == capturedTag, () =>
                {
                    SessionManager.Instance.SetSessionTag(sessionId, capturedTag);
                    RefreshSessionList();
                });
            }

            menu.AddSeparator($"{setTagLabel}/");

            menu.AddItem(new GUIContent($"{setTagLabel}/{AgentCore.Editor.L10n.Loc.Tr("session.menu.newTag", "新建 tag...")}"), false, () =>
            {
                SessionTagInputDialog.Show(
                    AgentCore.Editor.L10n.Loc.Tr("session.dialog.newTagTitle", "新建 tag"),
                    AgentCore.Editor.L10n.Loc.Tr("session.dialog.newTagPrompt", "输入 tag 名称："),
                    newTag =>
                    {
                        SessionManager.Instance.SetSessionTag(sessionId, newTag);
                        RefreshSessionList();
                    });
            });

            if (!string.IsNullOrEmpty(currentSummary.Tag))
            {
                menu.AddItem(new GUIContent($"{setTagLabel}/{AgentCore.Editor.L10n.Loc.Tr("session.menu.removeTag", "移除 tag")}"), false, () =>
                {
                    SessionManager.Instance.SetSessionTag(sessionId, null);
                    RefreshSessionList();
                });
            }

            // 归档 / 取消归档（顶层）
            if (!currentSummary.Archived)
            {
                menu.AddItem(new GUIContent(AgentCore.Editor.L10n.Loc.Tr("session.menu.archive", "归档")), false, () =>
                {
                    SessionManager.Instance.SetSessionArchived(sessionId, true);
                    RefreshSessionList();
                });
            }
            else
            {
                menu.AddItem(new GUIContent(AgentCore.Editor.L10n.Loc.Tr("session.menu.unarchive", "取消归档")), false, () =>
                {
                    SessionManager.Instance.SetSessionArchived(sessionId, false);
                    RefreshSessionList();
                });
            }

            menu.AddSeparator("");

            menu.AddItem(new GUIContent(AgentCore.Editor.L10n.Loc.Tr("session.menu.exportMarkdown", "导出/Markdown (.md)")), false, () =>
            {
                ExportSession(sessionId, SessionExporter.ExportFormat.Markdown);
            });

            menu.AddItem(new GUIContent(AgentCore.Editor.L10n.Loc.Tr("session.menu.exportJson", "导出/JSON (.json)")), false, () =>
            {
                ExportSession(sessionId, SessionExporter.ExportFormat.Json);
            });

            menu.AddSeparator("");

            menu.AddItem(new GUIContent(AgentCore.Editor.L10n.Loc.Tr("common.delete", "删除")), false, () =>
            {
                DeleteSessionWithConfirm(sessionId);
            });

            menu.ShowAsContext();
        }

        /// <summary>
        /// tag 组头右键菜单：重命名 / 置顶 / 上移 / 下移 / 删除 tag。
        /// 所有操作都会 RefreshSessionList 立即刷新。
        /// </summary>
        private void ShowTagGroupContextMenu(string tagName)
        {
            if (string.IsNullOrEmpty(tagName)) return;

            var menu = new GenericMenu();

            // 重命名 tag（当前名称拼在提示语后，避免 L10n 依赖格式占位符）
            menu.AddItem(new GUIContent(AgentCore.Editor.L10n.Loc.Tr("session.tagMenu.rename", "重命名 tag")), false, () =>
            {
                SessionTagInputDialog.Show(
                    AgentCore.Editor.L10n.Loc.Tr("session.tagMenu.renameTitle", "重命名 tag"),
                    AgentCore.Editor.L10n.Loc.Tr("session.tagMenu.renamePrompt", "输入新的 tag 名称：") + $" ({tagName})",
                    newName =>
                    {
                        if (string.IsNullOrWhiteSpace(newName)) return;
                        SessionTagRegistry.RenameTag(tagName, newName);
                        RefreshSessionList();
                    });
            });

            menu.AddSeparator("");

            // 置顶
            menu.AddItem(new GUIContent(AgentCore.Editor.L10n.Loc.Tr("session.tagMenu.pinTop", "置顶")), false, () =>
            {
                SessionTagRegistry.PinTagToTop(tagName);
                RefreshSessionList();
            });

            // 上移
            menu.AddItem(new GUIContent(AgentCore.Editor.L10n.Loc.Tr("session.tagMenu.moveUp", "上移")), false, () =>
            {
                SessionTagRegistry.MoveTagUp(tagName);
                RefreshSessionList();
            });

            // 下移
            menu.AddItem(new GUIContent(AgentCore.Editor.L10n.Loc.Tr("session.tagMenu.moveDown", "下移")), false, () =>
            {
                SessionTagRegistry.MoveTagDown(tagName);
                RefreshSessionList();
            });

            menu.AddSeparator("");

            // 删除 tag（把所有该 tag 的 session 变未分类）
            menu.AddItem(new GUIContent(AgentCore.Editor.L10n.Loc.Tr("session.tagMenu.delete", "删除 tag")), false, () =>
            {
                var affectedCount = SessionManager.Instance.GetSessionList().Count(s => string.Equals(s.Tag, tagName, StringComparison.OrdinalIgnoreCase));
                var confirmMsg = string.Format(
                    AgentCore.Editor.L10n.Loc.Tr("session.tagMenu.deleteConfirm", "确认删除 tag \"{0}\"？{1} 个会话将变为未分类。"),
                    tagName, affectedCount);
                if (UnityEditor.EditorUtility.DisplayDialog(
                    AgentCore.Editor.L10n.Loc.Tr("session.tagMenu.deleteTitle", "删除 tag"),
                    confirmMsg,
                    AgentCore.Editor.L10n.Loc.Tr("common.confirm", "确认"),
                    AgentCore.Editor.L10n.Loc.Tr("common.cancel", "取消")))
                {
                    SessionTagRegistry.DeleteTag(tagName);
                    RefreshSessionList();
                }
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
                titleLabel.text = AgentCore.Editor.L10n.Loc.Tr("session.autoRename.inProgress", "正在生成标题…");
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
                    // 用户手动输入标题：标记 TitleManuallySet=true，让自动命名 debounce 不再覆盖（尊重用户意图）。
                    SessionManager.Instance.RenameSession(sessionId, newTitle, manuallySet: true);
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
                AgentCore.Editor.L10n.Loc.Tr("session.dialog.deleteTitle", "删除会话"),
                AgentCore.Editor.L10n.Loc.Tr("session.dialog.deleteBody", "确定要删除此会话吗？此操作不可撤销。"),
                AgentCore.Editor.L10n.Loc.Tr("common.delete", "删除"),
                AgentCore.Editor.L10n.Loc.Tr("common.cancel", "取消"));

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
                return AgentCore.Editor.L10n.Loc.Tr("session.time.justNow", "刚刚");
            if (diff.TotalMinutes < 60)
                return AgentCore.Editor.L10n.Loc.Tr("session.time.minutesAgo", "{0}分钟前", (int)diff.TotalMinutes);
            if (diff.TotalHours < 24)
                return AgentCore.Editor.L10n.Loc.Tr("session.time.hoursAgo", "{0}小时前", (int)diff.TotalHours);
            if (diff.TotalDays < 2)
                return AgentCore.Editor.L10n.Loc.Tr("session.time.yesterday", "昨天");
            if (diff.TotalDays < 7)
                return AgentCore.Editor.L10n.Loc.Tr("session.time.daysAgo", "{0}天前", (int)diff.TotalDays);
            if (diff.TotalDays < 30)
                return AgentCore.Editor.L10n.Loc.Tr("session.time.weeksAgo", "{0}周前", (int)(diff.TotalDays / 7));

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
            menu.AddItem(new GUIContent(AgentCore.Editor.L10n.Loc.Tr("session.menu.exportMarkdown.short", "导出为 Markdown (.md)")), false, () =>
            {
                ExportSession(sessionId, SessionExporter.ExportFormat.Markdown);
            });
            menu.AddItem(new GUIContent(AgentCore.Editor.L10n.Loc.Tr("session.menu.exportJson.short", "导出为 JSON (.json)")), false, () =>
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
            // 会话文件读取异步化（UI 层不再同步 SessionStorage.Load）。
            // await 续体回到主线程后再弹出文件保存对话框（EditorUtility.SaveFilePanel 须主线程）。
            AsyncHelper.RunAsync(
                async () =>
                {
                    var session = await SessionStorage.LoadAsync(sessionId);
                    if (session == null)
                    {
                        AgentCoreLog.Error($"[AgentCore] Failed to load session: {sessionId}");
                        return;
                    }

                    var defaultName = SessionExporter.GetDefaultFileName(session, format);
                    var extension = format == SessionExporter.ExportFormat.Markdown ? "md" : "json";

                    var path = EditorUtility.SaveFilePanel(
                        AgentCore.Editor.L10n.Loc.Tr("session.dialog.exportTitle", "导出会话"),
                        "",
                        defaultName,
                        extension
                    );

                    if (string.IsNullOrEmpty(path))
                        return; // 用户取消

                    SessionExporter.ExportToFile(session, path, format);
                    AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] Session exported to: {path}");
                    EditorUtility.RevealInFinder(path);
                },
                onError: ex => AgentCoreLog.Error($"[AgentCore] Export failed: {ex.Message}"));
        }

        #endregion
    }
}
