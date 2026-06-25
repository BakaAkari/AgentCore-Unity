using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.LLM
{
    // ==================== 请求模型 ====================

    /// <summary>
    /// OpenAI Chat Completion 请求体。
    /// </summary>
    [Serializable]
    public class ChatCompletionRequest
    {
        [JsonProperty("model")]
        public string Model { get; set; }

        [JsonProperty("messages")]
        public List<ChatMessage> Messages { get; set; } = new();

        [JsonProperty("tools", NullValueHandling = NullValueHandling.Ignore)]
        public List<ToolDefinition> Tools { get; set; }

        [JsonProperty("stream")]
        public bool Stream { get; set; }

        [JsonProperty("temperature", NullValueHandling = NullValueHandling.Ignore)]
        public float? Temperature { get; set; }

        [JsonProperty("max_tokens", NullValueHandling = NullValueHandling.Ignore)]
        public int? MaxTokens { get; set; }
    }

    /// <summary>
    /// 对话消息。支持 system/user/assistant/tool 四种角色。
    /// </summary>
    [Serializable]
    public class ChatMessage
    {
        [JsonProperty("role")]
        public string Role { get; set; }

        [JsonProperty("content", NullValueHandling = NullValueHandling.Ignore)]
        public string Content { get; set; }

        [JsonProperty("tool_calls", NullValueHandling = NullValueHandling.Ignore)]
        public List<ToolCall> ToolCalls { get; set; }

        [JsonProperty("tool_call_id", NullValueHandling = NullValueHandling.Ignore)]
        public string ToolCallId { get; set; }

        /// <summary>
        /// 消息时间戳（本地使用，不序列化到 API）。
        /// </summary>
        [JsonIgnore]
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        // 工厂方法
        public static ChatMessage System(string content) => new() { Role = "system", Content = content };
        public static ChatMessage User(string content) => new() { Role = "user", Content = content };
        public static ChatMessage Assistant(string content, List<ToolCall> toolCalls = null) =>
            new() { Role = "assistant", Content = content, ToolCalls = toolCalls };
        public static ChatMessage Tool(string toolCallId, string content) =>
            new() { Role = "tool", ToolCallId = toolCallId, Content = content };
    }

    // ==================== 工具定义模型 ====================

    /// <summary>
    /// OpenAI 工具定义（function calling）。
    /// </summary>
    [Serializable]
    public class ToolDefinition
    {
        [JsonProperty("type")]
        public string Type { get; set; } = "function";

        [JsonProperty("function")]
        public FunctionDefinition Function { get; set; }
    }

    /// <summary>
    /// 函数定义。
    /// </summary>
    [Serializable]
    public class FunctionDefinition
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("parameters")]
        public JObject Parameters { get; set; }
    }

    /// <summary>
    /// LLM 返回的工具调用请求。
    /// </summary>
    [Serializable]
    public class ToolCall
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; } = "function";

        [JsonProperty("function")]
        public FunctionCall Function { get; set; }

        /// <summary>
        /// 工具调用的索引（流式模式下用于追踪多个并行调用）。
        /// </summary>
        [JsonProperty("index", NullValueHandling = NullValueHandling.Ignore)]
        public int? Index { get; set; }
    }

    /// <summary>
    /// 函数调用详情。
    /// </summary>
    [Serializable]
    public class FunctionCall
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("arguments")]
        public string Arguments { get; set; }
    }

    // ==================== 响应模型（非流式）====================

    /// <summary>
    /// OpenAI Chat Completion 完整响应。
    /// </summary>
    [Serializable]
    public class ChatCompletionResponse
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("object")]
        public string Object { get; set; }

        [JsonProperty("created")]
        public long Created { get; set; }

        [JsonProperty("model")]
        public string Model { get; set; }

        [JsonProperty("choices")]
        public List<Choice> Choices { get; set; }

        [JsonProperty("usage")]
        public Usage Usage { get; set; }

        /// <summary>
        /// 获取第一个 choice 的消息。
        /// </summary>
        public ChatMessage GetMessage()
        {
            return Choices?.Count > 0 ? Choices[0].Message : null;
        }

        /// <summary>
        /// 检查响应是否包含 tool_calls。
        /// </summary>
        public bool HasToolCalls()
        {
            var msg = GetMessage();
            return msg?.ToolCalls != null && msg.ToolCalls.Count > 0;
        }
    }

    [Serializable]
    public class Choice
    {
        [JsonProperty("index")]
        public int Index { get; set; }

        [JsonProperty("message")]
        public ChatMessage Message { get; set; }

        [JsonProperty("finish_reason")]
        public string FinishReason { get; set; }
    }

    [Serializable]
    public class Usage
    {
        [JsonProperty("prompt_tokens")]
        public int PromptTokens { get; set; }

        [JsonProperty("completion_tokens")]
        public int CompletionTokens { get; set; }

        [JsonProperty("total_tokens")]
        public int TotalTokens { get; set; }
    }

    // ==================== 流式响应模型 ====================

    /// <summary>
    /// SSE 流式响应的单个 chunk。
    /// </summary>
    [Serializable]
    public class ChatCompletionChunk
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("object")]
        public string Object { get; set; }

        [JsonProperty("created")]
        public long Created { get; set; }

        [JsonProperty("model")]
        public string Model { get; set; }

        [JsonProperty("choices")]
        public List<ChunkChoice> Choices { get; set; }
    }

    [Serializable]
    public class ChunkChoice
    {
        [JsonProperty("index")]
        public int Index { get; set; }

        [JsonProperty("delta")]
        public DeltaContent Delta { get; set; }

        [JsonProperty("finish_reason")]
        public string FinishReason { get; set; }
    }

    /// <summary>
    /// 流式响应中的增量内容。
    /// </summary>
    [Serializable]
    public class DeltaContent
    {
        [JsonProperty("role")]
        public string Role { get; set; }

        [JsonProperty("content")]
        public string Content { get; set; }

        [JsonProperty("tool_calls")]
        public List<ToolCall> ToolCalls { get; set; }
    }

    // ==================== 流式解析结果 ====================

    /// <summary>
    /// 流式解析器输出的 chunk 类型。
    /// </summary>
    public enum StreamChunkType
    {
        /// <summary>文本内容 token</summary>
        ContentToken,
        /// <summary>结构化 reasoning token</summary>
        ReasoningToken,
        /// <summary>工具调用增量（Phase 2 使用）</summary>
        ToolCallDelta,
        /// <summary>流结束</summary>
        Done,
        /// <summary>解析错误</summary>
        Error
    }

    /// <summary>
    /// 流式解析器输出的统一 chunk。
    /// </summary>
    public class StreamChunk
    {
        public StreamChunkType Type { get; set; }

        /// <summary>文本 token（Type == ContentToken 时有值）</summary>
        public string Content { get; set; }

        /// <summary>结构化 reasoning token（Type == ReasoningToken 时有值）</summary>
        public string ReasoningContent { get; set; }

        /// <summary>工具调用增量（Type == ToolCallDelta 时有值）</summary>
        public ToolCall ToolCallDelta { get; set; }

        /// <summary>完成原因（Type == Done 时有值）</summary>
        public string FinishReason { get; set; }

        /// <summary>错误信息（Type == Error 时有值）</summary>
        public string Error { get; set; }

        // 工厂方法
        public static StreamChunk Token(string content) =>
            new() { Type = StreamChunkType.ContentToken, Content = content };

        public static StreamChunk Reasoning(string content) =>
            new() { Type = StreamChunkType.ReasoningToken, ReasoningContent = content };

        public static StreamChunk ToolDelta(ToolCall delta) =>
            new() { Type = StreamChunkType.ToolCallDelta, ToolCallDelta = delta };

        public static StreamChunk Finished(string reason = "stop") =>
            new() { Type = StreamChunkType.Done, FinishReason = reason };

        public static StreamChunk ParseError(string error) =>
            new() { Type = StreamChunkType.Error, Error = error };
    }
}
