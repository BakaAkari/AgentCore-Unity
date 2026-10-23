using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.LLM.Anthropic
{
    /// <summary>
    /// Anthropic 服务端错误事件（流中途 <c>error</c> 事件或非 2xx 响应体）。
    /// 消息中保留错误类型，<c>FallbackRouter</c> 据此判断是否可重试（overloaded_error 等）。
    /// </summary>
    public sealed class AnthropicApiException : Exception
    {
        public string ErrorType { get; }

        public AnthropicApiException(string errorType, string message)
            : base($"Anthropic API error ({errorType}): {message}")
        {
            ErrorType = errorType;
        }
    }

    /// <summary>
    /// Anthropic Messages 事件累积器（v1.17.0）。纯逻辑，无 IO：
    /// 每个 SSE data 对象经 <see cref="Process"/> 转换为插件统一的 <see cref="StreamChunk"/>，
    /// 结束后 <see cref="BuildMessage"/> 产出带 thinking 块（含签名）、tool_calls 与 usage 的 assistant 消息。
    /// 非流式响应走 <see cref="FromMessage"/>，与流式共用同一套块处理逻辑。
    /// </summary>
    public sealed class AnthropicStreamAccumulator
    {
        private sealed class Block
        {
            public string Type;
            public readonly StringBuilder Text = new();
            public readonly StringBuilder Signature = new();
            public string Data;
            public string ToolId;
            public string ToolName;
            public int ToolOrdinal = -1;
            public readonly StringBuilder Json = new();
            public JObject InitialInput;
        }

        private readonly SortedDictionary<int, Block> _blocks = new();
        private int _toolCount;

        /// <summary>归一化用量（message_start 与 message_delta 合并）。</summary>
        public LlmUsage Usage { get; private set; }

        /// <summary>原始 stop_reason。</summary>
        public string StopReason { get; private set; }

        /// <summary>响应中的模型名。</summary>
        public string Model { get; private set; }

        /// <summary>是否已收到 message_stop。</summary>
        public bool Completed { get; private set; }

        public AnthropicStreamAccumulator(string requestedModel = null)
        {
            Model = requestedModel;
        }

        /// <summary>
        /// 处理一个事件对象；产出的 chunk 通过 <paramref name="emit"/> 推送。
        /// 收到 <c>error</c> 事件时抛 <see cref="AnthropicApiException"/>。
        /// </summary>
        public void Process(JObject evt, Action<StreamChunk> emit)
        {
            if (evt == null) return;
            switch (evt.Value<string>("type"))
            {
                case "message_start":
                {
                    var msg = evt["message"] as JObject;
                    var model = msg?.Value<string>("model");
                    if (!string.IsNullOrEmpty(model)) Model = model;
                    MergeUsage(msg?["usage"] as JObject);
                    break;
                }

                case "content_block_start":
                {
                    int index = evt.Value<int?>("index") ?? _blocks.Count;
                    var cb = evt["content_block"] as JObject;
                    var block = new Block { Type = cb?.Value<string>("type") ?? "text" };
                    switch (block.Type)
                    {
                        case "text":
                            var initialText = cb?.Value<string>("text");
                            if (!string.IsNullOrEmpty(initialText))
                            {
                                block.Text.Append(initialText);
                                emit?.Invoke(StreamChunk.Token(initialText));
                            }
                            break;
                        case "thinking":
                            var initialThinking = cb?.Value<string>("thinking");
                            if (!string.IsNullOrEmpty(initialThinking))
                            {
                                block.Text.Append(initialThinking);
                                emit?.Invoke(StreamChunk.Reasoning(initialThinking));
                            }
                            var sig = cb?.Value<string>("signature");
                            if (!string.IsNullOrEmpty(sig)) block.Signature.Append(sig);
                            break;
                        case "redacted_thinking":
                            block.Data = cb?.Value<string>("data");
                            break;
                        case "tool_use":
                        case "server_tool_use":
                            block.ToolId = cb?.Value<string>("id");
                            block.ToolName = cb?.Value<string>("name");
                            block.InitialInput = cb?["input"] as JObject;
                            block.ToolOrdinal = _toolCount++;
                            emit?.Invoke(StreamChunk.ToolDelta(new ToolCall
                            {
                                Index = block.ToolOrdinal,
                                Id = block.ToolId,
                                Function = new FunctionCall { Name = block.ToolName, Arguments = string.Empty }
                            }));
                            break;
                    }
                    _blocks[index] = block;
                    break;
                }

                case "content_block_delta":
                {
                    int index = evt.Value<int?>("index") ?? 0;
                    if (!_blocks.TryGetValue(index, out var block))
                    {
                        block = new Block { Type = "text" };
                        _blocks[index] = block;
                    }
                    var delta = evt["delta"] as JObject;
                    switch (delta?.Value<string>("type"))
                    {
                        case "text_delta":
                            var text = delta.Value<string>("text");
                            if (!string.IsNullOrEmpty(text))
                            {
                                block.Text.Append(text);
                                emit?.Invoke(StreamChunk.Token(text));
                            }
                            break;
                        case "thinking_delta":
                            var thinking = delta.Value<string>("thinking");
                            if (!string.IsNullOrEmpty(thinking))
                            {
                                block.Text.Append(thinking);
                                emit?.Invoke(StreamChunk.Reasoning(thinking));
                            }
                            break;
                        case "signature_delta":
                            block.Signature.Append(delta.Value<string>("signature"));
                            break;
                        case "input_json_delta":
                            var partial = delta.Value<string>("partial_json");
                            if (!string.IsNullOrEmpty(partial))
                            {
                                block.Json.Append(partial);
                                emit?.Invoke(StreamChunk.ToolDelta(new ToolCall
                                {
                                    Index = block.ToolOrdinal,
                                    Function = new FunctionCall { Arguments = partial }
                                }));
                            }
                            break;
                    }
                    break;
                }

                case "message_delta":
                {
                    var stop = evt["delta"]?.Value<string>("stop_reason");
                    if (!string.IsNullOrEmpty(stop)) StopReason = stop;
                    MergeUsage(evt["usage"] as JObject);
                    break;
                }

                case "message_stop":
                    Completed = true;
                    if (Usage != null && !Usage.IsEmpty) emit?.Invoke(StreamChunk.UsageReport(Usage));
                    emit?.Invoke(StreamChunk.Finished(AnthropicMessageMapper.MapStopReason(StopReason) ?? "stop"));
                    break;

                case "error":
                {
                    var err = evt["error"] as JObject;
                    throw new AnthropicApiException(err?.Value<string>("type") ?? "error", err?.Value<string>("message") ?? evt.ToString());
                }
            }
        }

        /// <summary>
        /// 处理非流式响应的完整 message 对象（content 块数组 + usage + stop_reason）。
        /// </summary>
        public static AnthropicStreamAccumulator FromMessage(JObject message, string requestedModel = null)
        {
            var acc = new AnthropicStreamAccumulator(requestedModel);
            if (message == null) return acc;
            if (message.Value<string>("type") == "error")
            {
                var err = message["error"] as JObject;
                throw new AnthropicApiException(err?.Value<string>("type") ?? "error", err?.Value<string>("message") ?? message.ToString());
            }

            var model = message.Value<string>("model");
            if (!string.IsNullOrEmpty(model)) acc.Model = model;
            acc.StopReason = message.Value<string>("stop_reason");
            acc.MergeUsage(message["usage"] as JObject);

            if (message["content"] is JArray content)
            {
                int i = 0;
                foreach (var token in content)
                {
                    if (token is not JObject cb) continue;
                    var block = new Block { Type = cb.Value<string>("type") ?? "text" };
                    switch (block.Type)
                    {
                        case "text": block.Text.Append(cb.Value<string>("text")); break;
                        case "thinking":
                            block.Text.Append(cb.Value<string>("thinking"));
                            block.Signature.Append(cb.Value<string>("signature"));
                            break;
                        case "redacted_thinking": block.Data = cb.Value<string>("data"); break;
                        case "tool_use":
                            block.ToolId = cb.Value<string>("id");
                            block.ToolName = cb.Value<string>("name");
                            block.InitialInput = cb["input"] as JObject;
                            block.ToolOrdinal = acc._toolCount++;
                            break;
                    }
                    acc._blocks[i++] = block;
                }
            }
            acc.Completed = true;
            return acc;
        }

        /// <summary>完整 reasoning 文本（所有 thinking 块拼接）。</summary>
        public string ReasoningText
            => string.Concat(_blocks.Values.Where(b => b.Type == "thinking").Select(b => b.Text.ToString()));

        /// <summary>
        /// 产出最终 assistant 消息：文本拼接、tool_calls（arguments 为 JSON 字符串）、可回放的 thinking 块、用量。
        /// </summary>
        public ChatMessage BuildMessage()
        {
            var text = new StringBuilder();
            var toolCalls = new List<ToolCall>();
            var reasoning = new List<ReasoningBlock>();

            foreach (var block in _blocks.Values)
            {
                switch (block.Type)
                {
                    case "text":
                        text.Append(block.Text);
                        break;
                    case "thinking":
                        reasoning.Add(new ReasoningBlock
                        {
                            Type = "thinking",
                            Thinking = block.Text.ToString(),
                            Signature = block.Signature.Length > 0 ? block.Signature.ToString() : null
                        });
                        break;
                    case "redacted_thinking":
                        reasoning.Add(new ReasoningBlock { Type = "redacted_thinking", Data = block.Data });
                        break;
                    case "tool_use":
                        if (string.IsNullOrEmpty(block.ToolName)) break;
                        var args = block.Json.Length > 0
                            ? block.Json.ToString()
                            : (block.InitialInput?.ToString(Newtonsoft.Json.Formatting.None) ?? "{}");
                        toolCalls.Add(new ToolCall
                        {
                            Id = string.IsNullOrEmpty(block.ToolId) ? "toolu_" + Guid.NewGuid().ToString("N") : block.ToolId,
                            Type = "function",
                            Function = new FunctionCall { Name = block.ToolName, Arguments = args }
                        });
                        break;
                }
            }

            var message = ChatMessage.Assistant(text.Length > 0 ? text.ToString() : null, toolCalls.Count > 0 ? toolCalls : null);
            if (reasoning.Count > 0) message.ReasoningBlocks = reasoning;
            var reasoningText = ReasoningText;
            if (!string.IsNullOrEmpty(reasoningText)) message.ReasoningContent = reasoningText;
            message.Usage = Usage;
            return message;
        }

        private void MergeUsage(JObject usage)
        {
            if (usage == null) return;
            var parsed = LlmUsage.FromAnthropic(usage, Model);
            if (Usage == null) Usage = parsed;
            else Usage.MergeLatest(parsed);
            if (Usage != null && string.IsNullOrEmpty(Usage.Model)) Usage.Model = Model;
        }
    }
}
