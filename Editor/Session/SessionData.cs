using System;
using System.Collections.Generic;
using System.Linq;
using AgentCore.Editor.Core;
using AgentCore.Editor.LLM;
using Newtonsoft.Json;

namespace AgentCore.Editor.Session
{
    /// <summary>
    /// 会话数据模型 — 一个完整对话会话的所有状态。
    /// 可序列化为 JSON 持久化到磁盘。
    /// </summary>
    [Serializable]
    public class SessionData
    {
        /// <summary>默认会话标题</summary>
        public const string DefaultTitle = "新会话";

        /// <summary>会话唯一标识（GUID）</summary>
        [JsonProperty("id")]
        public string Id { get; set; }

        /// <summary>会话显示标题（用户可修改，默认取第一条用户消息前 30 字符）</summary>
        [JsonProperty("title")]
        public string Title { get; set; }

        /// <summary>创建时间（UTC）</summary>
        [JsonProperty("created_at")]
        public DateTime CreatedAt { get; set; }

        /// <summary>最后更新时间（UTC）</summary>
        [JsonProperty("updated_at")]
        public DateTime UpdatedAt { get; set; }

        /// <summary>LLM 消息历史（system/user/assistant/tool）</summary>
        [JsonProperty("messages")]
        public List<SerializableChatMessage> Messages { get; set; } = new List<SerializableChatMessage>();

        /// <summary>UI 对话轮次（供显示用）</summary>
        [JsonProperty("turns")]
        public List<SerializableConversationTurn> Turns { get; set; } = new List<SerializableConversationTurn>();

        /// <summary>消息总数（快速统计用）</summary>
        [JsonProperty("message_count")]
        public int MessageCount { get; set; }

        /// <summary>压缩统计数据（会话级别）</summary>
        [JsonProperty("compression_metrics", NullValueHandling = NullValueHandling.Ignore)]
        public SerializableCompressionMetrics CompressionMetrics { get; set; }

        /// <summary>
        /// 从 AgentLoop 的运行时数据创建 SessionData。
        /// </summary>
        /// <param name="messages">LLM 消息历史</param>
        /// <param name="turns">UI 对话轮次</param>
        /// <param name="compressionMetrics">压缩统计数据（会话级别）</param>
        /// <param name="existingSession">已有的会话数据（用于保留 Id、Title、CreatedAt）</param>
        /// <param name="updateTimestamp">
        /// 是否更新 UpdatedAt 时间戳。
        /// 当会话内容有实质性变化（新增消息、标题变更等）时传 true；
        /// 仅做例行保存（如切换会话时保存旧会话）时传 false，以避免排序跳变。
        /// </param>
        /// <returns>可序列化的会话数据</returns>
        public static SessionData FromAgentLoop(
            List<ChatMessage> messages,
            List<ConversationTurn> turns,
            Core.Compression.CompressionMetrics compressionMetrics = null,
            SessionData existingSession = null,
            bool updateTimestamp = true)
        {
            var session = new SessionData
            {
                Id = existingSession?.Id ?? Guid.NewGuid().ToString(),
                Title = existingSession?.Title ?? "",
                CreatedAt = existingSession?.CreatedAt ?? DateTime.UtcNow,
                UpdatedAt = updateTimestamp ? DateTime.UtcNow : (existingSession?.UpdatedAt ?? DateTime.UtcNow),
                MessageCount = messages?.Count ?? 0,
                CompressionMetrics = SerializableCompressionMetrics.FromCompressionMetrics(compressionMetrics)
            };

            // 转换 ChatMessage -> SerializableChatMessage
            if (messages != null)
            {
                foreach (var msg in messages)
                {
                    session.Messages.Add(SerializableChatMessage.FromChatMessage(msg));
                }
            }

            // 转换 ConversationTurn -> SerializableConversationTurn
            if (turns != null)
            {
                foreach (var turn in turns)
                {
                    session.Turns.Add(SerializableConversationTurn.FromConversationTurn(turn));
                }
            }

            // 自动生成标题：取用户第一条消息的前 30 个字符
            // 当标题为空或仍是默认的"新会话"时，尝试从消息内容生成标题
            if (string.IsNullOrEmpty(session.Title) || session.Title == DefaultTitle)
            {
                var generated = GenerateTitle(messages);
                // 只有当生成的标题不是默认值时才更新（避免覆盖用户手动设置的标题）
                if (generated != DefaultTitle)
                {
                    session.Title = generated;
                }
            }

            return session;
        }

        /// <summary>
        /// 将序列化的消息数据转回运行时 ChatMessage 列表。
        /// </summary>
        /// <returns>ChatMessage 列表</returns>
        public List<ChatMessage> ToMessages()
        {
            var result = new List<ChatMessage>();
            if (Messages == null) return result;

            foreach (var msg in Messages)
            {
                result.Add(msg.ToChatMessage());
            }

            return result;
        }

        /// <summary>
        /// 将序列化的轮次数据转回运行时 ConversationTurn 列表。
        /// </summary>
        /// <returns>ConversationTurn 列表</returns>
        public List<ConversationTurn> ToConversationTurns()
        {
            var result = new List<ConversationTurn>();
            if (Turns == null) return result;

            foreach (var turn in Turns)
            {
                result.Add(turn.ToConversationTurn());
            }

            return result;
        }

        /// <summary>
        /// 从消息列表中自动生成会话标题。
        /// 取用户第一条消息的前 30 个字符。
        /// </summary>
        private static string GenerateTitle(List<ChatMessage> messages)
        {
            if (messages == null) return DefaultTitle;

            var firstUserMsg = messages.FirstOrDefault(m => m.Role == "user");
            if (firstUserMsg == null || string.IsNullOrEmpty(firstUserMsg.Content))
            {
                return DefaultTitle;
            }

            var content = firstUserMsg.Content.Trim();
            // 移除换行符
            content = content.Replace("\n", " ").Replace("\r", "");

            if (content.Length <= 30)
            {
                return content;
            }

            return content.Substring(0, 30) + "...";
        }
    }

    /// <summary>
    /// 可序列化的 ChatMessage — 用于 JSON 持久化。
    /// 与 LLM.ChatMessage 的转换通过 ToChatMessage() / FromChatMessage() 实现。
    /// </summary>
    [Serializable]
    public class SerializableChatMessage
    {
        /// <summary>角色：system / user / assistant / tool</summary>
        [JsonProperty("role")]
        public string Role { get; set; }

        /// <summary>文本内容</summary>
        [JsonProperty("content", NullValueHandling = NullValueHandling.Ignore)]
        public string Content { get; set; }

        /// <summary>tool 消息的 tool_call_id</summary>
        [JsonProperty("tool_call_id", NullValueHandling = NullValueHandling.Ignore)]
        public string ToolCallId { get; set; }

        /// <summary>assistant 消息的 tool_calls</summary>
        [JsonProperty("tool_calls", NullValueHandling = NullValueHandling.Ignore)]
        public List<SerializableToolCall> ToolCalls { get; set; }

        /// <summary>
        /// 从运行时 ChatMessage 创建可序列化版本。
        /// </summary>
        public static SerializableChatMessage FromChatMessage(ChatMessage msg)
        {
            if (msg == null) return null;

            var result = new SerializableChatMessage
            {
                Role = msg.Role,
                Content = msg.Content,
                ToolCallId = msg.ToolCallId
            };

            if (msg.ToolCalls != null && msg.ToolCalls.Count > 0)
            {
                result.ToolCalls = new List<SerializableToolCall>();
                foreach (var tc in msg.ToolCalls)
                {
                    result.ToolCalls.Add(SerializableToolCall.FromToolCall(tc));
                }
            }

            return result;
        }

        /// <summary>
        /// 转换回运行时 ChatMessage。
        /// </summary>
        public ChatMessage ToChatMessage()
        {
            var msg = new ChatMessage
            {
                Role = Role,
                Content = Content,
                ToolCallId = ToolCallId
            };

            if (ToolCalls != null && ToolCalls.Count > 0)
            {
                msg.ToolCalls = new List<ToolCall>();
                foreach (var tc in ToolCalls)
                {
                    msg.ToolCalls.Add(tc.ToToolCall());
                }
            }

            return msg;
        }
    }

    /// <summary>
    /// 可序列化的 ToolCall — 用于 JSON 持久化。
    /// </summary>
    [Serializable]
    public class SerializableToolCall
    {
        /// <summary>tool_call id</summary>
        [JsonProperty("id")]
        public string Id { get; set; }

        /// <summary>类型，固定为 "function"</summary>
        [JsonProperty("type")]
        public string Type { get; set; } = "function";

        /// <summary>函数名</summary>
        [JsonProperty("function_name")]
        public string FunctionName { get; set; }

        /// <summary>JSON 参数字符串</summary>
        [JsonProperty("arguments")]
        public string Arguments { get; set; }

        /// <summary>
        /// 从运行时 ToolCall 创建可序列化版本。
        /// </summary>
        public static SerializableToolCall FromToolCall(ToolCall tc)
        {
            if (tc == null) return null;

            return new SerializableToolCall
            {
                Id = tc.Id,
                Type = tc.Type ?? "function",
                FunctionName = tc.Function?.Name,
                Arguments = tc.Function?.Arguments
            };
        }

        /// <summary>
        /// 转换回运行时 ToolCall。
        /// </summary>
        public ToolCall ToToolCall()
        {
            return new ToolCall
            {
                Id = Id,
                Type = Type ?? "function",
                Function = new FunctionCall
                {
                    Name = FunctionName,
                    Arguments = Arguments
                }
            };
        }
    }

    /// <summary>
    /// 可序列化的 ConversationTurn — 用于 JSON 持久化和 UI 恢复。
    /// </summary>
    [Serializable]
    public class SerializableConversationTurn
    {
        /// <summary>轮次唯一标识</summary>
        [JsonProperty("id")]
        public string Id { get; set; }

        /// <summary>角色：user / assistant / system</summary>
        [JsonProperty("role")]
        public string Role { get; set; }

        /// <summary>消息内容</summary>
        [JsonProperty("content")]
        public string Content { get; set; }

        /// <summary>时间戳（UTC ISO 8601）</summary>
        [JsonProperty("timestamp")]
        public DateTime Timestamp { get; set; }

        /// <summary>工具调用信息列表（简化版，用于 UI 恢复）</summary>
        [JsonProperty("tool_calls", NullValueHandling = NullValueHandling.Ignore)]
        public List<SerializableToolCallInfo> ToolCalls { get; set; }

        /// <summary>
        /// 从运行时 ConversationTurn 创建可序列化版本。
        /// </summary>
        public static SerializableConversationTurn FromConversationTurn(ConversationTurn turn)
        {
            if (turn == null) return null;

            var result = new SerializableConversationTurn
            {
                Id = turn.Id,
                Role = turn.Role,
                Content = turn.Content,
                Timestamp = turn.Timestamp
            };

            if (turn.ToolCalls != null && turn.ToolCalls.Count > 0)
            {
                result.ToolCalls = new List<SerializableToolCallInfo>();
                foreach (var tc in turn.ToolCalls)
                {
                    result.ToolCalls.Add(new SerializableToolCallInfo
                    {
                        Id = tc.Id,
                        ToolName = tc.ToolName,
                        Arguments = tc.Arguments,
                        Result = tc.Result,
                        Success = tc.Success,
                        ExecutionTimeMs = tc.ExecutionTimeMs
                    });
                }
            }

            return result;
        }

        /// <summary>
        /// 转换回运行时 ConversationTurn。
        /// Id 和 Timestamp 通过 internal set 直接赋值恢复原始值。
        /// </summary>
        public ConversationTurn ToConversationTurn()
        {
            var turn = new ConversationTurn(Role, Content);

            // 通过 internal set 直接恢复原始 Id 和 Timestamp，
            // 这样 UI 层的 MessageBubble 字典可以正确关联
            turn.Id = Id;
            turn.Timestamp = Timestamp;

            // 恢复工具调用信息
            if (ToolCalls != null && ToolCalls.Count > 0)
            {
                turn.ToolCalls = new List<ToolCallInfo>();
                foreach (var tc in ToolCalls)
                {
                    var callInfo = new ToolCallInfo(tc.Id, tc.ToolName, tc.Arguments)
                    {
                        Result = tc.Result,
                        Success = tc.Success,
                        ExecutionTimeMs = tc.ExecutionTimeMs,
                        EndTime = DateTime.UtcNow // 标记为已完成
                    };
                    turn.ToolCalls.Add(callInfo);
                }
            }

            return turn;
        }
    }

    /// <summary>
    /// 可序列化的工具调用信息 — 用于 JSON 持久化。
    /// </summary>
    [Serializable]
    public class SerializableToolCallInfo
    {
        /// <summary>工具调用 ID</summary>
        [JsonProperty("id")]
        public string Id { get; set; }

        /// <summary>工具名称</summary>
        [JsonProperty("tool_name")]
        public string ToolName { get; set; }

        /// <summary>工具参数（JSON string）</summary>
        [JsonProperty("arguments")]
        public string Arguments { get; set; }

        /// <summary>执行结果</summary>
        [JsonProperty("result", NullValueHandling = NullValueHandling.Ignore)]
        public string Result { get; set; }

        /// <summary>执行是否成功</summary>
        [JsonProperty("success")]
        public bool Success { get; set; }

        /// <summary>执行耗时（毫秒）</summary>
        [JsonProperty("execution_time_ms")]
        public double ExecutionTimeMs { get; set; }
    }

    /// <summary>
    /// 可序列化的压缩统计数据 — 用于会话级别的压缩统计持久化。
    /// </summary>
    [Serializable]
    public class SerializableCompressionMetrics
    {
        /// <summary>工具结果压缩次数</summary>
        [JsonProperty("tool_result_compressions")]
        public int ToolResultCompressions { get; set; }

        /// <summary>工具结果压缩失败次数</summary>
        [JsonProperty("tool_result_failures")]
        public int ToolResultFailures { get; set; }

        /// <summary>工具结果跳过压缩次数</summary>
        [JsonProperty("tool_result_skipped")]
        public int ToolResultSkipped { get; set; }

        /// <summary>工具结果原始 token 总数</summary>
        [JsonProperty("tool_result_original_tokens")]
        public int ToolResultOriginalTokens { get; set; }

        /// <summary>工具结果压缩后 token 总数</summary>
        [JsonProperty("tool_result_compressed_tokens")]
        public int ToolResultCompressedTokens { get; set; }

        /// <summary>对话压缩次数</summary>
        [JsonProperty("conversation_compressions")]
        public int ConversationCompressions { get; set; }

        /// <summary>对话压缩失败次数</summary>
        [JsonProperty("conversation_failures")]
        public int ConversationFailures { get; set; }

        /// <summary>对话压缩的消息数</summary>
        [JsonProperty("conversation_messages_compressed")]
        public int ConversationMessagesCompressed { get; set; }

        /// <summary>对话原始 token 总数</summary>
        [JsonProperty("conversation_original_tokens")]
        public int ConversationOriginalTokens { get; set; }

        /// <summary>对话压缩后 token 总数</summary>
        [JsonProperty("conversation_compressed_tokens")]
        public int ConversationCompressedTokens { get; set; }

        /// <summary>
        /// 从 CompressionMetrics 创建可序列化版本。
        /// </summary>
        public static SerializableCompressionMetrics FromCompressionMetrics(Core.Compression.CompressionMetrics metrics)
        {
            if (metrics == null) return null;

            return new SerializableCompressionMetrics
            {
                ToolResultCompressions = metrics.ToolResultCompressionCount,
                ToolResultFailures = metrics.ToolResultCompressionFailureCount,
                ToolResultSkipped = metrics.ToolResultCompressionSkippedCount,
                ToolResultOriginalTokens = metrics.ToolResultOriginalTokens,
                ToolResultCompressedTokens = metrics.ToolResultOriginalTokens - metrics.ToolResultTokensSaved,
                ConversationCompressions = metrics.ConversationCompressionCount,
                ConversationFailures = metrics.ConversationCompressionFailureCount,
                ConversationMessagesCompressed = metrics.ConversationMessagesCompressed,
                ConversationOriginalTokens = metrics.ConversationOriginalTokens,
                ConversationCompressedTokens = metrics.ConversationOriginalTokens - metrics.ConversationTokensSaved
            };
        }

        /// <summary>
        /// 恢复到 CompressionMetrics 实例。
        /// </summary>
        public void RestoreToCompressionMetrics(Core.Compression.CompressionMetrics metrics)
        {
            if (metrics == null) return;

            // RestoreFromPersistence 只接受 6 个参数：
            // toolResultSuccessCount, conversationSuccessCount,
            // toolResultOriginalTokens, conversationOriginalTokens,
            // toolResultTokensSaved, conversationTokensSaved
            int toolResultTokensSaved = ToolResultOriginalTokens - ToolResultCompressedTokens;
            int conversationTokensSaved = ConversationOriginalTokens - ConversationCompressedTokens;

            metrics.RestoreFromPersistence(
                ToolResultCompressions,  // toolResultSuccessCount
                ConversationCompressions,  // conversationSuccessCount
                ToolResultOriginalTokens,
                ConversationOriginalTokens,
                toolResultTokensSaved,
                conversationTokensSaved
            );
        }
    }
}
