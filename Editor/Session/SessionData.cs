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

        /// <summary>
        /// 从 AgentLoop 的运行时数据创建 SessionData。
        /// </summary>
        /// <param name="messages">LLM 消息历史</param>
        /// <param name="turns">UI 对话轮次</param>
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
            SessionData existingSession = null,
            bool updateTimestamp = true)
        {
            var session = new SessionData
            {
                Id = existingSession?.Id ?? Guid.NewGuid().ToString(),
                Title = existingSession?.Title ?? "",
                CreatedAt = existingSession?.CreatedAt ?? DateTime.UtcNow,
                UpdatedAt = updateTimestamp ? DateTime.UtcNow : (existingSession?.UpdatedAt ?? DateTime.UtcNow),
                MessageCount = messages?.Count ?? 0
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
            if (string.IsNullOrEmpty(session.Title))
            {
                session.Title = GenerateTitle(messages);
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
            if (messages == null) return "新会话";

            var firstUserMsg = messages.FirstOrDefault(m => m.Role == "user");
            if (firstUserMsg == null || string.IsNullOrEmpty(firstUserMsg.Content))
            {
                return "新会话";
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
        /// 注意：ConversationTurn 的 Id 和 Timestamp 在构造时自动生成，
        /// 这里需要通过反射或特殊处理来恢复原始值。
        /// </summary>
        public ConversationTurn ToConversationTurn()
        {
            var turn = new ConversationTurn(Role, Content);

            // ConversationTurn 的 Id 是只读属性（构造时生成），
            // 但为了恢复会话，我们需要使用反射设置原始 Id
            // 这样 UI 层的 MessageBubble 字典可以正确关联
            SetPrivateField(turn, "<Id>k__BackingField", Id);
            SetPrivateField(turn, "<Timestamp>k__BackingField", Timestamp);

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

        /// <summary>
        /// 通过反射设置只读属性的后备字段。
        /// </summary>
        private static void SetPrivateField(object obj, string fieldName, object value)
        {
            var field = obj.GetType().GetField(fieldName,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            field?.SetValue(obj, value);
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
}
