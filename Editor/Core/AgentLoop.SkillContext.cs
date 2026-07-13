using System;
using System.Collections.Generic;
using System.Linq;
using AgentCore.Editor.LLM;
using AgentCore.Editor.Skills;
using AgentCore.Editor.Tools.Native.Meta;
using UnityEngine;

namespace AgentCore.Editor.Core
{
    /// <summary>
    /// AgentLoop 的 Skill Context 管理（ADR-18 Phase 1）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 负责维护 <c>_messages</c> 集合中与已加载 skill 对齐的一组 system message：
    /// <list type="bullet">
    ///   <item>会话初始化时创建 <see cref="SkillScopeState"/> 并注入到 <c>LoadSkillTool</c>。</item>
    ///   <item>LLM 调 <c>load/unload/reload</c> 时，通过 <c>LoadSkillTool.OnSkillMutation</c> 事件被通知。</item>
    ///   <item>每轮 <c>SendMessageAsync</c> 发送前调用 <see cref="SyncSkillMessages"/> 保证 message 集合与 state 一致。</item>
    /// </list>
    /// </para>
    /// <para>
    /// Skill message 使用 <see cref="SkillContentBuilder.Marker"/> 前缀标记，
    /// <c>ConversationCompressor</c> 依赖此标记跳过压缩。
    /// </para>
    /// </remarks>
    public partial class AgentLoop
    {
        /// <summary>会话级 Skill 加载状态。</summary>
        private SkillScopeState _skillScopeState;

        /// <summary>初始化 skill context（Initialize 时调用）。</summary>
        private void InitializeSkillContext()
        {
            _skillScopeState = new SkillScopeState();
            LoadSkillTool.SetScopeState(_skillScopeState);

            // 订阅 skill 变更事件：每次 LLM 调 load/unload/reload 时，标记下轮需要重建 skill messages
            LoadSkillTool.OnSkillMutation -= HandleSkillMutation;
            LoadSkillTool.OnSkillMutation += HandleSkillMutation;
        }

        /// <summary>重置 skill state（ResetConversation 时调用）。</summary>
        private void ResetSkillContext()
        {
            _skillScopeState?.Reset();
        }

        /// <summary>释放 skill context 资源（Dispose 时调用）。</summary>
        private void DisposeSkillContext()
        {
            LoadSkillTool.OnSkillMutation -= HandleSkillMutation;
            LoadSkillTool.SetScopeState(null);
        }

        /// <summary>
        /// 事件回调：LLM 调用 <c>load_skill</c> 修改了 skill state。
        /// </summary>
        /// <remarks>
        /// 事件本身不直接改 <c>_messages</c>，因为 tool 执行发生在 <c>ToolCallDispatcher</c> 内，
        /// 而 message 装配统一在下一轮 <see cref="SyncSkillMessages"/> 完成，避免并发问题。
        /// 此回调仅用于日志和未来扩展（如 UI 通知）。
        /// </remarks>
        private void HandleSkillMutation(string skillName, string action)
        {
            Debug.Log($"[AgentCore][Skills] Skill mutation: {action} '{skillName}' (will apply on next turn).");
        }

        /// <summary>
        /// 同步 <c>_messages</c> 中的 skill system message 到当前 <see cref="_skillScopeState"/>。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 每次向 LLM 发送前调用。语义：
        /// <list type="bullet">
        ///   <item>找出所有已在 <c>_messages</c> 中的 skill message（以 <see cref="SkillContentBuilder.Marker"/> 前缀识别）。</item>
        ///   <item>删除 state 中已不存在（被 unload）或需要 reload（内容变化）的旧消息。</item>
        ///   <item>为 state 中尚未存在于 <c>_messages</c> 的 skill 添加新消息。</item>
        /// </list>
        /// </para>
        /// <para>
        /// 插入位置：紧靠最后一条 user message 之前（与 Deferred Context 同位置）。
        /// 若 <c>_messages</c> 末尾不是 user message，跳过插入（发生在 tool_call 循环中间）。
        /// </para>
        /// </remarks>
        private void SyncSkillMessages()
        {
            if (_skillScopeState == null) return;

            // 1. 收集已在 _messages 中的 skill messages（按 name → index 索引）
            var existingByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _messages.Count; i++)
            {
                var msg = _messages[i];
                if (msg.Role != "system") continue;
                if (!SkillContentBuilder.IsSkillMessage(msg.Content)) continue;
                var name = SkillContentBuilder.TryExtractName(msg.Content);
                if (!string.IsNullOrEmpty(name))
                    existingByName[name] = i;
            }

            var loadedNames = new HashSet<string>(_skillScopeState.LoadedSkills, StringComparer.OrdinalIgnoreCase);

            // 2. 移除已被 unload 的 skill messages（倒序移除避免索引失效）
            var indicesToRemove = existingByName
                .Where(kv => !loadedNames.Contains(kv.Key))
                .Select(kv => kv.Value)
                .OrderByDescending(idx => idx)
                .ToList();

            foreach (var idx in indicesToRemove)
            {
                _messages.RemoveAt(idx);
            }

            // 移除后重建索引（因为 index 被打乱）
            existingByName.Clear();
            for (int i = 0; i < _messages.Count; i++)
            {
                var msg = _messages[i];
                if (msg.Role != "system") continue;
                if (!SkillContentBuilder.IsSkillMessage(msg.Content)) continue;
                var name = SkillContentBuilder.TryExtractName(msg.Content);
                if (!string.IsNullOrEmpty(name))
                    existingByName[name] = i;
            }

            // 3. 为未在 _messages 的 skill 添加新消息
            //    插入位置：最后一条 user message 之前
            var insertIndex = FindLastUserMessageIndex();
            if (insertIndex < 0)
            {
                // 没有 user message，跳过（Skill 需要在 user message 上下文中生效）
                return;
            }

            foreach (var skillName in _skillScopeState.LoadedSkills)
            {
                if (existingByName.ContainsKey(skillName)) continue;

                var content = SkillRegistry.Instance.GetContent(skillName);
                if (string.IsNullOrEmpty(content))
                {
                    Debug.LogWarning($"[AgentCore][Skills] Loaded skill '{skillName}' has no readable content, skipping injection.");
                    continue;
                }

                var messageText = SkillContentBuilder.Build(skillName, content);
                _messages.Insert(insertIndex, ChatMessage.System(messageText));
                Debug.Log($"[AgentCore][Skills] Injected skill '{skillName}' at index {insertIndex} (~{messageText.Length / 3} tokens).");
                // insertIndex 位置不递增：新消息插入后其他 skill 依然在其前面（保持顺序稳定）
                // 但索引可能因插入而位移，下一次插入会重新查找
                insertIndex = FindLastUserMessageIndex();
                if (insertIndex < 0) break;
            }
        }

        /// <summary>
        /// 处理 reload：如果某个 skill 的消息已存在但内容需要刷新，
        /// 先删除旧消息，等 <see cref="SyncSkillMessages"/> 时会自动重建。
        /// </summary>
        /// <remarks>
        /// 目前 <see cref="SyncSkillMessages"/> 只在缺失时插入，不检测内容变化。
        /// reload 语义通过"移除旧消息 + 下轮 sync 时重新加载"实现。
        /// 由于 <see cref="LoadSkillTool.HandleReload"/> 在 tool 执行线程运行，
        /// 不能直接操作 <c>_messages</c>（可能存在并发）。因此 reload 后必须依赖下一轮 SyncSkillMessages
        /// 检测到"state 中已加载 + message 中已存在旧内容"，此时不会重复插入。
        /// **限制**：Phase 1 的 reload 只刷新 registry 缓存，会话中的 skill message 内容不会变更，
        /// 用户需要 unload → load 才能看到新内容。这是有意的简化，Phase 2 再增强。
        /// </remarks>
        private void HandleSkillReload(string skillName)
        {
            // Phase 1: 暂不做主动替换，保持 unload+load 语义
            _ = skillName;
        }

        /// <summary>
        /// 查找 <c>_messages</c> 中最后一条 user 消息的索引（用于确定 skill message 插入位置）。
        /// </summary>
        /// <returns>找到则返回索引，否则返回 -1。</returns>
        private int FindLastUserMessageIndex()
        {
            for (int i = _messages.Count - 1; i >= 0; i--)
            {
                if (_messages[i].Role == "user")
                    return i;
            }
            return -1;
        }
    }
}
