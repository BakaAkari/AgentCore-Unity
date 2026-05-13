using System.Collections.Generic;
using AgentCore.Editor.LLM;
using UnityEngine;

namespace AgentCore.Editor.Core
{
    public partial class AgentLoop
    {
        /// <summary>
        /// 清理消息历史中不完整的 tool_use/tool_result 配对。
        /// <para>
        /// Anthropic API 要求每个 assistant 消息中的 tool_call 都必须有对应的 tool result 消息紧随其后。
        /// 在 Domain Reload 或会话恢复后，消息历史可能包含不完整的配对（例如 assistant 发了 3 个 tool_calls，
        /// 但只有 1-2 个 tool result），这会导致 API 返回 400 错误。
        /// </para>
        /// <para>
        /// 此方法扫描整个消息历史，为所有缺失 tool_result 的 tool_call 补充占位响应。
        /// </para>
        /// </summary>
        /// <returns>补充的占位 tool_result 数量</returns>
        private int SanitizeMessageHistory()
        {
            int fixedCount = 0;

            for (int i = 0; i < _messages.Count; i++)
            {
                var msg = _messages[i];

                // 只处理包含 tool_calls 的 assistant 消息
                if (msg.Role != "assistant" || msg.ToolCalls == null || msg.ToolCalls.Count == 0)
                    continue;

                // 收集这条 assistant 消息之后紧跟的所有 tool result 的 ToolCallId
                var existingToolResultIds = new HashSet<string>();
                int insertPosition = i + 1;
                for (int j = i + 1; j < _messages.Count; j++)
                {
                    if (_messages[j].Role == "tool" && !string.IsNullOrEmpty(_messages[j].ToolCallId))
                    {
                        existingToolResultIds.Add(_messages[j].ToolCallId);
                        insertPosition = j + 1;
                    }
                    else
                    {
                        // 遇到非 tool 消息，停止搜索
                        break;
                    }
                }

                // 检查每个 tool_call 是否有对应的 tool_result
                int insertedInThisGroup = 0;
                foreach (var toolCall in msg.ToolCalls)
                {
                    if (string.IsNullOrEmpty(toolCall.Id))
                        continue;

                    if (!existingToolResultIds.Contains(toolCall.Id))
                    {
                        // 缺少对应的 tool_result，补充占位响应
                        string toolName = toolCall.Function?.Name ?? "unknown";
                        var placeholder = ChatMessage.Tool(toolCall.Id,
                            $"[Tool result unavailable] The execution of '{toolName}' was interrupted by a Domain Reload " +
                            "or session restoration. The result was not captured. Please retry if needed.");

                        _messages.Insert(insertPosition + insertedInThisGroup, placeholder);
                        insertedInThisGroup++;
                        fixedCount++;

                        Debug.Log($"[AgentCore] SanitizeMessageHistory: Added placeholder tool_result for " +
                                  $"tool_call '{toolCall.Id}' (tool: {toolName}).");
                    }
                }
            }

            if (fixedCount > 0)
            {
                Debug.Log($"[AgentCore] SanitizeMessageHistory: Fixed {fixedCount} missing tool_result(s).");
            }

            return fixedCount;
        }
    }
}
