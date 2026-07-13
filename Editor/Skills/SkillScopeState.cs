using System;
using System.Collections.Generic;
using System.Linq;

namespace AgentCore.Editor.Skills
{
    /// <summary>
    /// 会话级 Skill 加载状态。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 追踪当前会话中 LLM 已通过 <c>LoadSkillTool</c> 加载的 skill 集合。
    /// 结构与 <see cref="Tools.ToolScopeState"/> 完全对称，但作用域是知识而非工具。
    /// </para>
    /// <para>
    /// D2 决策：会话级生命周期，新会话开始时 <see cref="Reset"/>。
    /// 不做 Domain Reload 持久化（可作为未来 Phase 3 增强）。
    /// </para>
    /// </remarks>
    public sealed class SkillScopeState
    {
        private readonly HashSet<string> _loadedSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>当前已加载的 skill 名称（只读快照）。</summary>
        public IReadOnlyCollection<string> LoadedSkills => _loadedSkills.ToList().AsReadOnly();

        /// <summary>已加载 skill 的数量。</summary>
        public int LoadedCount => _loadedSkills.Count;

        /// <summary>
        /// 标记 skill 已加载。
        /// </summary>
        /// <returns><c>true</c> 表示新加载；<c>false</c> 表示之前已加载过。</returns>
        public bool MarkLoaded(string skillName)
        {
            if (string.IsNullOrWhiteSpace(skillName)) return false;
            return _loadedSkills.Add(skillName.Trim());
        }

        /// <summary>
        /// 检查 skill 是否已加载。
        /// </summary>
        public bool IsLoaded(string skillName)
        {
            if (string.IsNullOrWhiteSpace(skillName)) return false;
            return _loadedSkills.Contains(skillName.Trim());
        }

        /// <summary>
        /// 卸载单个 skill。
        /// </summary>
        /// <returns><c>true</c> 表示实际移除；<c>false</c> 表示原本就未加载。</returns>
        public bool Unload(string skillName)
        {
            if (string.IsNullOrWhiteSpace(skillName)) return false;
            return _loadedSkills.Remove(skillName.Trim());
        }

        /// <summary>
        /// 清空所有已加载 skill（新会话时调用）。
        /// </summary>
        public void Reset()
        {
            _loadedSkills.Clear();
        }
    }
}
