using System;
using System.Collections.Generic;
using System.Linq;
using AgentCore.Editor.Tools.Infrastructure;

namespace AgentCore.Editor.Tools
{
    /// <summary>
    /// 会话级工具作用域状态（G.3 ActiveToolScope）。
    /// <para>
    /// 追踪当前会话中 LLM 已激活的 OnDemand 分类。
    /// 每次新会话重置。由 <see cref="ToolScopeResolver"/> 查询。
    /// </para>
    /// </summary>
    public class ToolScopeState
    {
        private readonly HashSet<string> _activatedCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>当前已激活的 OnDemand 分类列表（只读快照）。</summary>
        public IReadOnlyCollection<string> ActivatedCategories => _activatedCategories.ToList().AsReadOnly();

        /// <summary>已激活分类的数量。</summary>
        public int ActivatedCount => _activatedCategories.Count;

        /// <summary>
        /// 激活一个或多个 OnDemand 工具分类。
        /// </summary>
        /// <param name="categories">要激活的分类名称列表</param>
        /// <returns>实际新激活的分类数量（已激活的不计）</returns>
        public int ActivateCategories(IEnumerable<string> categories)
        {
            int count = 0;
            foreach (var cat in categories)
            {
                if (!string.IsNullOrWhiteSpace(cat) && _activatedCategories.Add(cat.Trim()))
                    count++;
            }
            return count;
        }

        /// <summary>
        /// 激活单个 OnDemand 工具分类。
        /// </summary>
        /// <param name="category">分类名称</param>
        /// <returns>是否为新激活（已激活返回 false）</returns>
        public bool ActivateCategory(string category)
        {
            if (string.IsNullOrWhiteSpace(category)) return false;
            return _activatedCategories.Add(category.Trim());
        }

        /// <summary>
        /// 检查指定分类是否已被激活。
        /// </summary>
        public bool IsCategoryActivated(string category)
        {
            if (string.IsNullOrWhiteSpace(category)) return false;
            return _activatedCategories.Contains(category.Trim());
        }

        /// <summary>
        /// 重置所有已激活分类（新会话时调用）。
        /// </summary>
        public void Reset()
        {
            _activatedCategories.Clear();
        }
    }
}
