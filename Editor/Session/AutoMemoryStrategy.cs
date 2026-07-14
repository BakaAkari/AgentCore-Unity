using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Cloud;
using AgentCore.Editor.Config;
using AgentCore.Editor.LLM;
using Newtonsoft.Json;
using UnityEngine;

namespace AgentCore.Editor.Session
{
    /// <summary>
    /// 自动记忆策略 — 会话结束时提取关键信息存入 mem0。
    /// <para>
    /// 在以下场景自动触发：
    /// <list type="bullet">
    ///   <item>会话切换时（切换到新会话前保存旧会话的记忆）</item>
    ///   <item>对话重置时（<see cref="AgentCore.Editor.Core.AgentLoop.ResetConversation"/>）</item>
    ///   <item>窗口关闭时</item>
    /// </list>
    /// </para>
    /// <para>
    /// 工作流程：
    /// <list type="number">
    ///   <item>检查触发条件（mem0 已启用、自动记忆已开启、对话轮次足够）</item>
    ///   <item>构建对话摘要（只取 user/assistant 文本，忽略 tool 细节）</item>
    ///   <item>调用 LLM 提取值得记忆的关键信息</item>
    ///   <item>将提取的记忆逐条存入 mem0</item>
    /// </list>
    /// </para>
    /// </summary>
    public class AutoMemoryStrategy
    {
        /// <summary>日志前缀</summary>
        private const string LogPrefix = "[AgentCore] AutoMemory: ";

        /// <summary>对话摘要中每条消息的最大字符数</summary>
        private const int MaxContentLengthPerMessage = 500;

        /// <summary>对话摘要的最大总字符数（防止 prompt 过长）</summary>
        private const int MaxSummaryTotalLength = 8000;

        /// <summary>LLM 提取超时时间（秒）</summary>
        private const int ExtractionTimeoutSeconds = 30;

        /// <summary>
        /// 提取记忆的系统 prompt。
        /// 指导 LLM 从对话中提取值得长期记忆的关键信息。
        /// </summary>
        private const string ExtractionPrompt =
            "你是一个记忆提取助手。请从以下对话中提取值得长期记忆的关键信息。\n\n" +
            "提取规则：\n" +
            "1. 只提取对未来对话有价值的信息（用户偏好、项目约定、技术决策、重要发现等）\n" +
            "2. 忽略临时性的操作细节（如\"创建了一个 Cube\"这类一次性操作）\n" +
            "3. 每条记忆应该是独立的、自包含的句子\n" +
            "4. 最多提取 5 条记忆\n" +
            "5. 如果没有值得记忆的内容，返回空数组\n\n" +
            "请以 JSON 数组格式返回，例如：\n" +
            "[\"用户偏好使用 URP 渲染管线\", \"项目使用 Unity 2022.3 LTS 版本\"]\n\n" +
            "对话内容：\n";

        /// <summary>
        /// 从会话中提取关键信息并存入 mem0。
        /// 此方法是异步的，不会阻塞 UI。失败时静默处理。
        /// </summary>
        /// <param name="session">要提取记忆的会话数据</param>
        /// <param name="llmClient">LLM 客户端（用于生成摘要）</param>
        /// <param name="ct">取消令牌</param>
        public async Task ExtractAndStoreAsync(
            SessionData session,
            ILLMClient llmClient,
            CancellationToken ct = default)
        {
            try
            {
                // 1. 检查是否满足触发条件
                if (!ShouldTrigger(session))
                {
                    AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Skipped — trigger conditions not met.");
                    return;
                }

                // 2. 构建对话摘要
                var conversationSummary = BuildConversationSummary(session);
                if (string.IsNullOrWhiteSpace(conversationSummary))
                {
                    AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Skipped — conversation summary is empty.");
                    return;
                }

                // 3. 调用 LLM 提取记忆
                var memories = await ExtractMemoriesFromLLMAsync(llmClient, conversationSummary, ct);
                if (memories == null || memories.Count == 0)
                {
                    AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}No memories extracted from conversation.");
                    return;
                }

                // 4. 存入 mem0
                var mem0Client = Mem0Client.FromSettings();
                int storedCount = 0;

                foreach (var memory in memories)
                {
                    if (string.IsNullOrWhiteSpace(memory)) continue;

                    ct.ThrowIfCancellationRequested();

                    var result = await mem0Client.AddMemoryAsync(
                        memory,
                        metadata: new Dictionary<string, string>
                        {
                            ["source"] = "auto_memory",
                            ["session_id"] = session.Id ?? "",
                            ["session_title"] = session.Title ?? ""
                        },
                        ct: ct);

                    if (result.Success)
                    {
                        storedCount++;
                    }
                    else
                    {
                        Debug.LogWarning($"{LogPrefix}Failed to store memory: {result.Message}");
                    }
                }

                AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Stored {storedCount}/{memories.Count} memories for session '{session.Title}'.");
            }
            catch (OperationCanceledException)
            {
                AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}Extraction cancelled.");
            }
            catch (Exception ex)
            {
                // 静默处理 — 自动记忆失败不应影响正常功能
                Debug.LogWarning($"{LogPrefix}Failed to extract and store memories: {ex.Message}");
            }
        }

        /// <summary>
        /// 检查是否满足自动记忆的触发条件。
        /// </summary>
        /// <param name="session">会话数据</param>
        /// <returns>是否应该触发自动记忆</returns>
        private bool ShouldTrigger(SessionData session)
        {
            var settings = AgentCoreSettings.instance;

            // 条件 1: mem0 必须已启用
            if (!settings.mem0Enabled)
            {
                return false;
            }

            // 条件 2: 自动记忆必须已开启
            if (!settings.autoMemoryEnabled)
            {
                return false;
            }

            // 条件 3: mem0 Endpoint 必须已配置
            if (string.IsNullOrWhiteSpace(settings.mem0Endpoint))
            {
                return false;
            }

            // 条件 4: 会话数据不能为空
            if (session?.Turns == null || session.Turns.Count == 0)
            {
                return false;
            }

            // 条件 5: 用户对话轮次必须达到最小阈值
            var userTurns = session.Turns.Count(t =>
                string.Equals(t.Role, "user", StringComparison.OrdinalIgnoreCase));
            return userTurns >= settings.autoMemoryMinTurns;
        }

        /// <summary>
        /// 从会话数据构建对话摘要文本。
        /// 只取 user 和 assistant 的文本消息，忽略 system 和 tool 消息。
        /// 每条消息截断到 <see cref="MaxContentLengthPerMessage"/> 字符。
        /// 总长度限制在 <see cref="MaxSummaryTotalLength"/> 字符以内。
        /// </summary>
        /// <param name="session">会话数据</param>
        /// <returns>格式化的对话摘要文本</returns>
        private string BuildConversationSummary(SessionData session)
        {
            var sb = new StringBuilder();

            foreach (var turn in session.Turns)
            {
                // 只取 user 和 assistant 的对话内容
                if (!string.Equals(turn.Role, "user", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 跳过空内容
                if (string.IsNullOrWhiteSpace(turn.Content))
                {
                    continue;
                }

                // 截断过长的内容
                var content = turn.Content.Length > MaxContentLengthPerMessage
                    ? turn.Content.Substring(0, MaxContentLengthPerMessage) + "..."
                    : turn.Content;

                sb.AppendLine($"[{turn.Role}]: {content}");

                // 检查总长度限制
                if (sb.Length >= MaxSummaryTotalLength)
                {
                    sb.AppendLine("[... 对话内容过长，已截断 ...]");
                    break;
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// 调用 LLM 从对话摘要中提取值得记忆的关键信息。
        /// 使用非流式请求，返回 JSON 数组格式的记忆列表。
        /// </summary>
        /// <param name="llmClient">LLM 客户端</param>
        /// <param name="conversationSummary">对话摘要文本</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>提取的记忆列表，失败时返回空列表</returns>
        private async Task<List<string>> ExtractMemoriesFromLLMAsync(
            ILLMClient llmClient,
            string conversationSummary,
            CancellationToken ct)
        {
            try
            {
                // 构建提取请求消息
                var messages = new List<ChatMessage>
                {
                    ChatMessage.System(ExtractionPrompt + conversationSummary)
                };

                // 使用带超时的取消令牌
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(ExtractionTimeoutSeconds));

                // 发送非流式请求（不需要工具定义）
                var response = await llmClient.ChatCompletionAsync(messages, tools: null, ct: cts.Token);

                if (response?.Choices == null || response.Choices.Count == 0)
                {
                    Debug.LogWarning($"{LogPrefix}LLM returned empty response for memory extraction.");
                    return new List<string>();
                }

                var content = response.Choices[0].Message?.Content;
                if (string.IsNullOrWhiteSpace(content))
                {
                    return new List<string>();
                }

                // 解析 JSON 数组
                return ParseMemoriesFromResponse(content);
            }
            catch (OperationCanceledException)
            {
                AgentCore.Editor.Utils.AgentCoreLog.Info($"{LogPrefix}LLM extraction timed out or was cancelled.");
                return new List<string>();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LogPrefix}LLM extraction failed: {ex.Message}");
                return new List<string>();
            }
        }

        /// <summary>
        /// 从 LLM 响应文本中解析记忆列表。
        /// 支持纯 JSON 数组和包含 markdown 代码块的格式。
        /// </summary>
        /// <param name="responseContent">LLM 响应文本</param>
        /// <returns>解析出的记忆列表</returns>
        private List<string> ParseMemoriesFromResponse(string responseContent)
        {
            try
            {
                // 尝试提取 JSON 数组（可能被 markdown 代码块包裹）
                var jsonContent = responseContent.Trim();

                // 移除可能的 markdown 代码块标记
                if (jsonContent.Contains("```"))
                {
                    var startIdx = jsonContent.IndexOf('[');
                    var endIdx = jsonContent.LastIndexOf(']');
                    if (startIdx >= 0 && endIdx > startIdx)
                    {
                        jsonContent = jsonContent.Substring(startIdx, endIdx - startIdx + 1);
                    }
                }

                var memories = JsonConvert.DeserializeObject<List<string>>(jsonContent);

                if (memories == null)
                {
                    return new List<string>();
                }

                // 过滤空字符串并限制最多 5 条
                return memories
                    .Where(m => !string.IsNullOrWhiteSpace(m))
                    .Take(5)
                    .ToList();
            }
            catch (JsonException ex)
            {
                Debug.LogWarning($"{LogPrefix}Failed to parse memories JSON: {ex.Message}. Response: {responseContent}");
                return new List<string>();
            }
        }

        /// <summary>
        /// 静态便捷方法 — 在后台触发自动记忆提取（fire-and-forget）。
        /// 不会阻塞调用线程，所有异常都被静默处理。
        /// </summary>
        /// <param name="session">要提取记忆的会话数据</param>
        /// <param name="llmClient">LLM 客户端</param>
        public static void TriggerAsync(SessionData session, ILLMClient llmClient)
        {
            if (session == null || llmClient == null) return;

            var strategy = new AutoMemoryStrategy();
            // Fire-and-forget: 在后台执行，不阻塞调用方
            _ = Task.Run(async () =>
            {
                try
                {
                    await strategy.ExtractAndStoreAsync(session, llmClient);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[AgentCore] AutoMemory background task failed: {ex.Message}");
                }
            });
        }
    }
}
