using System;
using System.Collections.Generic;
using System.Linq;

namespace AgentCore.Editor.LLM
{
    /// <summary>
    /// 会话内工具定义顺序稳定器（v1.17.0）。
    /// <para>
    /// 工具定义位于 prompt 最前端（Anthropic 渲染顺序 tools → system → messages；OpenAI 兼容服务端模板
    /// 通常也把 tools 放在系统提示附近）。任何顺序抖动都会让整段缓存前缀失效。本类记住每个工具首次
    /// 出现的位置：已见过的工具按首见顺序排列，新出现的工具按名称排序后追加到末尾——工具集合只增长时
    /// 前缀保持不变。
    /// </para>
    /// </summary>
    public sealed class ToolOrderStabilizer
    {
        private readonly Dictionary<string, int> _firstSeen = new(StringComparer.Ordinal);

        /// <summary>返回按稳定顺序排列的新列表（输入 null / 空原样返回）。</summary>
        public List<ToolDefinition> Apply(List<ToolDefinition> definitions)
        {
            if (definitions == null || definitions.Count == 0) return definitions;

            var newcomers = definitions
                .Select(d => d?.Function?.Name ?? string.Empty)
                .Where(n => !_firstSeen.ContainsKey(n))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal);
            foreach (var name in newcomers) _firstSeen[name] = _firstSeen.Count;

            return definitions
                .OrderBy(d => _firstSeen[d?.Function?.Name ?? string.Empty])
                .ToList();
        }

        /// <summary>清空记忆（新会话时调用）。</summary>
        public void Reset() => _firstSeen.Clear();
    }
}
