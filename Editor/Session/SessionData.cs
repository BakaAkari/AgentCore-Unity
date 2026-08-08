using System;
using System.Collections.Generic;
using System.Linq;
using AgentCore.Editor.Core;
using AgentCore.Editor.Core.SelfChallenge;
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
        /// <summary>
        /// 默认会话标题 (存储层稳定值, 用作"是否默认标题"的判断标记).
        /// </summary>
        /// <remarks>
        /// 保留原字符串 "新会话" 不动是**故意的**:
        /// 已存盘的历史会话 JSON 里 title 字段可能是这个值, 若改成 L10n 动态返回,
        /// <c>session.Title == DefaultTitle</c> 判断会永远失败, 破坏 AutoGenerateTitle
        /// 自动生成标题的逻辑. 展示层应通过 <see cref="GetDisplayTitle"/> 把存储值翻译为本地化标题.
        /// </remarks>
        public const string DefaultTitle = "新会话";

        /// <summary>
        /// v1.9.0+: 获取用于展示的会话标题.
        /// 若传入是空 / <see cref="DefaultTitle"/>, 返回当前语言的本地化"新会话"; 否则原样返回用户/LLM 生成的标题.
        /// </summary>
        /// <param name="storedTitle">存储层的原始标题字符串.</param>
        public static string GetDisplayTitle(string storedTitle)
        {
            if (string.IsNullOrEmpty(storedTitle) || storedTitle == DefaultTitle)
            {
                return AgentCore.Editor.L10n.Loc.Tr("session.defaultTitle", DefaultTitle);
            }
            return storedTitle;
        }

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

        /// <summary>用户手动打的 tag（单 tag，null=未分类）</summary>
        [JsonProperty("tag", NullValueHandling = NullValueHandling.Ignore)]
        public string Tag { get; set; }

        /// <summary>是否已归档</summary>
        [JsonProperty("archived", NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool Archived { get; set; }

        /// <summary>用户是否手动设置过标题（true 则自动命名跳过，尊重用户意图）</summary>
        [JsonProperty("title_manually_set", NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool TitleManuallySet { get; set; }

        /// <summary>
        /// v1.14.9: 会话级 Reasoning Effort 快捷覆盖（chat 面板下拉选择，类似 Codex/Claude Code
        /// 的思考强度切换）。取值 "auto"/"low"/"medium"/"high"；null/空 = 不覆盖，跟随全局/Profile
        /// 设置（<see cref="AgentCore.Editor.Config.ActiveModelConfig.ReasoningEffort"/>）。
        /// 会话粘滞：选定后持续生效直到用户手动改，随会话保存/加载。
        /// </summary>
        [JsonProperty("reasoning_effort_override", NullValueHandling = NullValueHandling.Ignore)]
        public string ReasoningEffortOverride { get; set; }

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
                CompressionMetrics = SerializableCompressionMetrics.FromCompressionMetrics(compressionMetrics),
                // v1.12.0: 保留会话组织字段（tag / 归档 / 手动命名标记），与 Id/Title 一样从已有会话继承
                Tag = existingSession?.Tag,
                Archived = existingSession?.Archived ?? false,
                TitleManuallySet = existingSession?.TitleManuallySet ?? false,
                // v1.14.9: 会话级 Reasoning Effort 覆盖同样从已有会话继承（AutoSave/ForceSave 每轮都会
                // 重新调用本方法构建新 SessionData，若不继承会在下一次自动保存时被悄悄清空）。
                ReasoningEffortOverride = existingSession?.ReasoningEffortOverride
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
    ///
    /// v1.11+ (Bug F'): JSON 结构改为 OpenAI 标准嵌套形式
    /// <c>{ id, type, function: { name, arguments } }</c>，
    /// 保留 <see cref="FunctionName"/> / <see cref="Arguments"/> 平铺属性作为向后兼容
    /// setter（读取旧 v1.10.x 存档时依然能反序列化）。
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

        /// <summary>
        /// 嵌套 function 对象（OpenAI 标准结构，v1.11+）。
        /// 主写入路径 — 新导出的 JSON 使用此字段。
        /// </summary>
        [JsonProperty("function", NullValueHandling = NullValueHandling.Ignore)]
        public SerializableFunctionCall Function { get; set; }

        /// <summary>
        /// [向后兼容 v1.10.x] 平铺函数名字段。
        /// 反序列化旧 JSON 时使用；新导出不会写入（NullValueHandling.Ignore）。
        /// </summary>
        [JsonProperty("function_name", NullValueHandling = NullValueHandling.Ignore)]
        public string FunctionName
        {
            get => null; // 不写入
            set
            {
                if (string.IsNullOrEmpty(value)) return;
                Function ??= new SerializableFunctionCall();
                Function.Name = value;
            }
        }

        /// <summary>
        /// [向后兼容 v1.10.x] 平铺参数字段。
        /// 反序列化旧 JSON 时使用；新导出不会写入（NullValueHandling.Ignore）。
        /// </summary>
        [JsonProperty("arguments", NullValueHandling = NullValueHandling.Ignore)]
        public string Arguments
        {
            get => null; // 不写入
            set
            {
                if (value == null) return;
                Function ??= new SerializableFunctionCall();
                Function.Arguments = value;
            }
        }

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
                Function = new SerializableFunctionCall
                {
                    Name = tc.Function?.Name,
                    Arguments = tc.Function?.Arguments
                }
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
                    Name = Function?.Name,
                    Arguments = Function?.Arguments
                }
            };
        }
    }

    /// <summary>
    /// OpenAI 标准 function call 嵌套结构（v1.11+, Bug F'）。
    /// </summary>
    [Serializable]
    public class SerializableFunctionCall
    {
        /// <summary>函数名</summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>JSON 参数字符串</summary>
        [JsonProperty("arguments")]
        public string Arguments { get; set; }
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

        /// <summary>ThinkingDrawer 内容；不进入 LLM 上下文。</summary>
        [JsonProperty("reasoning", NullValueHandling = NullValueHandling.Ignore)]
        public string Reasoning { get; set; }

        /// <summary>ThinkingDrawer 来源。</summary>
        [JsonProperty("reasoning_source", NullValueHandling = NullValueHandling.Ignore)]
        public ThinkingTraceSource ReasoningSource { get; set; }

        /// <summary>reasoning / planning trace 耗时（毫秒）。</summary>
        [JsonProperty("reasoning_duration_ms", NullValueHandling = NullValueHandling.Ignore)]
        public double ReasoningDurationMs { get; set; }

        /// <summary>原始 assistant content，仅用于审计/恢复，不进入 LLM 上下文。</summary>
        [JsonProperty("raw_assistant_content", NullValueHandling = NullValueHandling.Ignore)]
        public string RawAssistantContent { get; set; }

        /// <summary>可见规划 trace 解析状态。</summary>
        [JsonProperty("planning_trace_state", NullValueHandling = NullValueHandling.Ignore)]
        public VisiblePlanningTraceState PlanningTraceState { get; set; }

        /// <summary>时间戳（UTC ISO 8601）</summary>
        [JsonProperty("timestamp")]
        public DateTime Timestamp { get; set; }

        /// <summary>工具调用信息列表（简化版，用于 UI 恢复）</summary>
        [JsonProperty("tool_calls", NullValueHandling = NullValueHandling.Ignore)]
        public List<SerializableToolCallInfo> ToolCalls { get; set; }

        /// <summary>
        /// Self-Challenge 数据（Phase 9，v1.4.9 骨架起）；
        /// 未参与 self-challenge 的 turn（例如 v1.4.x 及以前的旧 session）反序列化时为 <c>null</c>，UI 层遇到 null 直接不渲染。
        /// </summary>
        [JsonProperty("self_challenge", NullValueHandling = NullValueHandling.Ignore)]
        public SelfChallengeData SelfChallenge { get; set; }

        /// <summary>
        /// Fork 支持：此 turn 完成时 LLM 消息历史长度快照（见 <see cref="ConversationTurn.MessageEndIndex"/>）。
        /// -1（默认值）表示未记录/不可作为 Fork 点——旧版本会话数据反序列化后自然是 -1，
        /// Fork UI 据此禁用该消息上的 Fork 按钮，不会用不可靠边界切出坏历史。
        /// </summary>
        [JsonProperty("message_end_index", NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int MessageEndIndex { get; set; } = -1;

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
                Reasoning = string.IsNullOrEmpty(turn.Reasoning) ? null : turn.Reasoning,
                ReasoningSource = turn.ReasoningSource,
                ReasoningDurationMs = turn.ReasoningDurationMs,
                RawAssistantContent = string.IsNullOrEmpty(turn.RawAssistantContent) ? null : turn.RawAssistantContent,
                PlanningTraceState = turn.PlanningTraceState,
                Timestamp = turn.Timestamp,
                SelfChallenge = turn.SelfChallenge,
                MessageEndIndex = turn.MessageEndIndex
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
            var turn = new ConversationTurn(Role, Content)
            {
                Reasoning = Reasoning ?? string.Empty,
                ReasoningSource = ReasoningSource,
                ReasoningDurationMs = ReasoningDurationMs,
                RawAssistantContent = RawAssistantContent ?? string.Empty,
                PlanningTraceState = PlanningTraceState,
                SelfChallenge = SelfChallenge,
                MessageEndIndex = MessageEndIndex
            };

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
