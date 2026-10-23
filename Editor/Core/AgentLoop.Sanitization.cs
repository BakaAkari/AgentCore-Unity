using System.Collections.Generic;
using AgentCore.Editor.LLM;
using UnityEngine;

namespace AgentCore.Editor.Core
{
    public partial class AgentLoop
    {
        /// <summary>
        /// 就地修复消息历史中不完整的 tool_call / tool_result 配对（规则见 <see cref="HistorySanitizer"/>）。
        /// <para>
        /// 每次请求前已对发送副本做同样的修复；这里用于域重载恢复、会话加载和取消收尾，
        /// 让存盘的会话本身也是合法的。
        /// </para>
        /// </summary>
        /// <returns>补充的合成 tool_result 数量</returns>
        private int SanitizeMessageHistory()
        {
            var normalized = HistorySanitizer.Normalize(_messages, out var report);
            if (!report.Changed)
                return 0;

            _messages.Clear();
            _messages.AddRange(normalized);
            AgentCore.Editor.Utils.AgentCoreLog.Info($"[AgentCore] SanitizeMessageHistory: repaired history ({report}).");
            return report.SyntheticResults;
        }
    }
}
