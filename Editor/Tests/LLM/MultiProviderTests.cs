using System.Collections.Generic;
using System.Linq;
using AgentCore.Editor.Config;
using AgentCore.Editor.LLM;
using AgentCore.Editor.LLM.Anthropic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.LLM
{
    /// <summary>
    /// v1.17.0 多供应商 / 缓存相关的纯函数单元测试（无网络）。
    /// </summary>
    [TestFixture]
    public class LlmUsageTests
    {
        [Test]
        public void FromOpenAI_CachedTokens_SplitsUncached()
        {
            var u = LlmUsage.FromOpenAI(JObject.Parse(
                "{\"prompt_tokens\":1000,\"completion_tokens\":50,\"prompt_tokens_details\":{\"cached_tokens\":800}}"));
            Assert.AreEqual(200, u.InputUncached);
            Assert.AreEqual(800, u.CacheRead);
            Assert.AreEqual(50, u.Output);
            Assert.AreEqual(0.8f, u.HitRate, 1e-4);
        }

        [Test]
        public void FromOpenAI_DeepSeekHitMiss()
        {
            var u = LlmUsage.FromOpenAI(JObject.Parse(
                "{\"prompt_tokens\":0,\"completion_tokens\":5,\"prompt_cache_hit_tokens\":300,\"prompt_cache_miss_tokens\":100}"));
            Assert.AreEqual(300, u.CacheRead);
            Assert.AreEqual(100, u.InputUncached);
            Assert.AreEqual(400, u.TotalInput);
        }

        [Test]
        public void FromOpenAI_LiteLLMAnthropicPassthrough()
        {
            var u = LlmUsage.FromOpenAI(JObject.Parse(
                "{\"prompt_tokens\":1000,\"completion_tokens\":1,\"cache_read_input_tokens\":600,\"cache_creation_input_tokens\":300}"));
            Assert.AreEqual(600, u.CacheRead);
            Assert.AreEqual(300, u.CacheWrite);
            Assert.AreEqual(100, u.InputUncached);
        }

        [Test]
        public void FromAnthropic_InputIsAlreadyUncached()
        {
            var u = LlmUsage.FromAnthropic(JObject.Parse(
                "{\"input_tokens\":12,\"cache_read_input_tokens\":5000,\"cache_creation_input_tokens\":200,\"output_tokens\":40}"));
            Assert.AreEqual(12, u.InputUncached);
            Assert.AreEqual(5000, u.CacheRead);
            Assert.AreEqual(200, u.CacheWrite);
            Assert.AreEqual(5212, u.TotalInput);
        }

        [Test]
        public void NullUsage_ReturnsNull()
        {
            Assert.IsNull(LlmUsage.FromOpenAI(null));
            Assert.IsNull(LlmUsage.FromAnthropic(null));
        }

        [Test]
        public void MergeLatest_OverwritesOnlyNonZero()
        {
            var a = new LlmUsage { InputUncached = 10, CacheRead = 90, Output = 1 };
            a.MergeLatest(new LlmUsage { Output = 77 });
            Assert.AreEqual(10, a.InputUncached);
            Assert.AreEqual(90, a.CacheRead);
            Assert.AreEqual(77, a.Output);
        }

        [Test]
        public void Add_Accumulates()
        {
            var a = new LlmUsage { InputUncached = 1, CacheRead = 2, Output = 3 };
            a.Add(new LlmUsage { InputUncached = 10, CacheRead = 20, Output = 30 });
            Assert.AreEqual(11, a.InputUncached);
            Assert.AreEqual(22, a.CacheRead);
            Assert.AreEqual(33, a.Output);
            Assert.AreEqual(2, a.Calls);
        }
    }

    [TestFixture]
    public class ModelRegistryTests
    {
        [TearDown]
        public void TearDown() => ModelRegistry.SetOverridesForTesting(null);

        [Test]
        public void Normalize_StripsGatewayPrefixes()
        {
            Assert.AreEqual("claude-opus-4-7", ModelRegistry.Normalize("anthropic/claude-opus-4-7"));
            Assert.AreEqual("claude-sonnet-4-5-20250929-v1:0", ModelRegistry.Normalize("us.anthropic.claude-sonnet-4-5-20250929-v1:0"));
            Assert.AreEqual("gpt-4o", ModelRegistry.Normalize("  GPT-4o "));
        }

        [Test]
        public void Lookup_LongestPrefixWins()
        {
            Assert.AreEqual("claude-opus-4-5", ModelRegistry.Lookup("claude-opus-4-5-20251101").Prefix);
            Assert.AreEqual("claude-opus-4", ModelRegistry.Lookup("claude-opus-4-20250514").Prefix);
            Assert.AreEqual(ThinkingMode.BudgetTokens, ModelRegistry.Lookup("claude-sonnet-4-5").Thinking);
        }

        [Test]
        public void Lookup_FamiliesAndUnknown()
        {
            Assert.AreEqual(ModelFamily.Anthropic, ModelRegistry.GetFamily("openrouter/anthropic/claude-sonnet-4-6"));
            Assert.AreEqual(ModelFamily.DeepSeek, ModelRegistry.GetFamily("deepseek-chat"));
            Assert.AreEqual(ModelFamily.OpenAI, ModelRegistry.GetFamily("gpt-5"));
            Assert.AreEqual(ModelFamily.Unknown, ModelRegistry.GetFamily("my-local-llama"));
            Assert.AreEqual(ModelFamily.Unknown, ModelRegistry.GetFamily(null));
        }

        [Test]
        public void Overrides_TakePrecedence()
        {
            ModelRegistry.SetOverridesForTesting(new List<ModelProfile>
            {
                new() { Prefix = "claude-sonnet-4-6", Family = ModelFamily.Anthropic, InputPrice = 1, OutputPrice = 2, ContextWindow = 4242 }
            });
            Assert.AreEqual(4242, ModelRegistry.Lookup("claude-sonnet-4-6").ContextWindow);
        }

        [Test]
        public void EstimateCost_UsesCacheMultipliers()
        {
            var p = new ModelProfile { Family = ModelFamily.Anthropic, InputPrice = 10, OutputPrice = 50 };
            var usage = new LlmUsage { InputUncached = 1_000_000, CacheRead = 1_000_000, CacheWrite = 1_000_000, Output = 1_000_000 };
            // 10 + 1 (0.1x) + 12.5 (1.25x) + 50
            Assert.AreEqual(73.5, p.EstimateCostUsd(usage).Value, 1e-9);
            // 1h TTL: write 2x → 10 + 1 + 20 + 50
            Assert.AreEqual(81.0, p.EstimateCostUsd(usage, ttl1h: true).Value, 1e-9);
        }

        [Test]
        public void EstimateCost_UnknownPricing_ReturnsNull()
        {
            Assert.IsNull(new ModelProfile().EstimateCostUsd(new LlmUsage { InputUncached = 5 }));
        }
    }

    [TestFixture]
    public class AnthropicMessageMapperTests
    {
        private static ToolDefinition Tool(string name) => new()
        {
            Type = "function",
            Function = new FunctionDefinition
            {
                Name = name,
                Description = name + " tool",
                Parameters = JObject.Parse("{\"type\":\"object\",\"properties\":{}}")
            }
        };

        private static ToolCall Call(string id, string name, string args) => new()
        {
            Id = id,
            Type = "function",
            Function = new FunctionCall { Name = name, Arguments = args }
        };

        private static AnthropicRequestOptions Opts(string model = "claude-sonnet-4-6") => new() { Model = model };

        [Test]
        public void LeadingSystem_BecomesTopLevelSystem_WithCacheBreakpoint()
        {
            var body = AnthropicMessageMapper.BuildRequest(Opts(), new List<ChatMessage>
            {
                ChatMessage.System("You are helpful."),
                ChatMessage.User("hi")
            }, null);

            var system = (JArray)body["system"];
            Assert.AreEqual(1, system.Count);
            Assert.AreEqual("You are helpful.", (string)system[0]["text"]);
            Assert.AreEqual("ephemeral", (string)system[0]["cache_control"]["type"]);
            Assert.AreEqual("user", (string)body["messages"][0]["role"]);
        }

        [Test]
        public void CacheTtl1h_AddsTtl()
        {
            var o = Opts();
            o.CacheTtl1h = true;
            var body = AnthropicMessageMapper.BuildRequest(o, new List<ChatMessage> { ChatMessage.System("s"), ChatMessage.User("u") }, null);
            Assert.AreEqual("1h", (string)body["system"][0]["cache_control"]["ttl"]);
        }

        [Test]
        public void ToolCallsAndResults_MapToBlocks()
        {
            var body = AnthropicMessageMapper.BuildRequest(Opts(), new List<ChatMessage>
            {
                ChatMessage.System("s"),
                ChatMessage.User("read it"),
                ChatMessage.Assistant("ok", new List<ToolCall> { Call("t1", "read", "{\"path\":\"a\"}") }),
                ChatMessage.Tool("t1", "content")
            }, new List<ToolDefinition> { Tool("read") });

            var msgs = (JArray)body["messages"];
            Assert.AreEqual(3, msgs.Count);
            var assistant = (JArray)msgs[1]["content"];
            var toolUse = assistant.First(b => (string)b["type"] == "tool_use");
            Assert.AreEqual("t1", (string)toolUse["id"]);
            Assert.AreEqual("a", (string)toolUse["input"]["path"]);
            var result = (JArray)msgs[2]["content"];
            Assert.AreEqual("tool_result", (string)result[0]["type"]);
            Assert.AreEqual("t1", (string)result[0]["tool_use_id"]);
            Assert.AreEqual("read", (string)body["tools"][0]["name"]);
            Assert.IsNotNull(body["tools"][0]["input_schema"]);
        }

        [Test]
        public void OrphanToolUse_GetsPlaceholderResult()
        {
            var body = AnthropicMessageMapper.BuildRequest(Opts(), new List<ChatMessage>
            {
                ChatMessage.User("go"),
                ChatMessage.Assistant(null, new List<ToolCall> { Call("t9", "x", "{}") }),
                ChatMessage.User("next")
            }, null);

            var msgs = (JArray)body["messages"];
            var lastUser = (JArray)msgs.Last["content"];
            Assert.AreEqual("tool_result", (string)lastUser[0]["type"], "tool_result must come first in user content");
            Assert.AreEqual("t9", (string)lastUser[0]["tool_use_id"]);
        }

        [Test]
        public void MidConversationSystem_BecomesReminderInUserTurn()
        {
            var body = AnthropicMessageMapper.BuildRequest(Opts(), new List<ChatMessage>
            {
                ChatMessage.System("s"),
                ChatMessage.User("q"),
                new ChatMessage { Role = "system", Content = "memory", Kind = MessageKind.OperatorNote }
            }, null);

            var msgs = (JArray)body["messages"];
            Assert.AreEqual(1, msgs.Count, "note merges into the adjacent user turn");
            var text = string.Join("\n", ((JArray)msgs[0]["content"]).Select(b => (string)b["text"]));
            StringAssert.Contains("<system-reminder>", text);
            StringAssert.Contains("memory", text);
        }

        [Test]
        public void FirstAndLastMessage_AreUser()
        {
            var body = AnthropicMessageMapper.BuildRequest(Opts(), new List<ChatMessage>
            {
                ChatMessage.Assistant("hello")
            }, null);
            var msgs = (JArray)body["messages"];
            Assert.AreEqual("user", (string)msgs.First["role"]);
            Assert.AreEqual("user", (string)msgs.Last["role"]);
        }

        [Test]
        public void SignedThinking_IsReplayedBeforeText_UnsignedDropped()
        {
            var assistant = ChatMessage.Assistant("answer", new List<ToolCall> { Call("t1", "x", "{}") });
            assistant.ReasoningBlocks = new List<ReasoningBlock>
            {
                new() { Type = "thinking", Thinking = "hmm", Signature = "sig" },
                new() { Type = "thinking", Thinking = "no signature" }
            };
            var body = AnthropicMessageMapper.BuildRequest(Opts(), new List<ChatMessage>
            {
                ChatMessage.User("q"), assistant, ChatMessage.Tool("t1", "r")
            }, null);

            var blocks = (JArray)body["messages"][1]["content"];
            Assert.AreEqual("thinking", (string)blocks[0]["type"]);
            Assert.AreEqual("sig", (string)blocks[0]["signature"]);
            Assert.AreEqual(1, blocks.Count(b => (string)b["type"] == "thinking"));
        }

        [Test]
        public void DropReasoning_OmitsThinkingBlocks()
        {
            var assistant = ChatMessage.Assistant("answer");
            assistant.ReasoningBlocks = new List<ReasoningBlock> { new() { Type = "thinking", Thinking = "t", Signature = "s" } };
            var o = Opts();
            o.DropReasoning = true;
            var body = AnthropicMessageMapper.BuildRequest(o, new List<ChatMessage> { ChatMessage.User("q"), assistant, ChatMessage.User("q2") }, null);
            Assert.IsFalse(body["messages"].SelectTokens("$..type").Any(t => (string)t == "thinking"));
        }

        [Test]
        public void Thinking_PerModelShape()
        {
            var adaptive = AnthropicMessageMapper.BuildRequest(Opts("claude-opus-4-7"), new List<ChatMessage> { ChatMessage.User("q") }, null);
            Assert.AreEqual("adaptive", (string)adaptive["thinking"]["type"]);

            var legacy = Opts("claude-sonnet-4-5");
            legacy.Level = ReasoningParamMapper.ReasoningLevel.Medium;
            var budget = AnthropicMessageMapper.BuildRequest(legacy, new List<ChatMessage> { ChatMessage.User("q") }, null);
            Assert.AreEqual("enabled", (string)budget["thinking"]["type"]);
            Assert.AreEqual(8192, (int)budget["thinking"]["budget_tokens"]);
            Assert.Greater((int)budget["max_tokens"], 8192);

            var off = Opts("claude-opus-4-7");
            off.Level = ReasoningParamMapper.ReasoningLevel.Off;
            Assert.IsNull(AnthropicMessageMapper.BuildRequest(off, new List<ChatMessage> { ChatMessage.User("q") }, null)["thinking"]);
        }

        [Test]
        public void Temperature_OnlyWhenSupportedAndThinkingOff()
        {
            var o = Opts("claude-opus-4-7");
            o.Temperature = 0.3f;
            o.Level = ReasoningParamMapper.ReasoningLevel.Off;
            Assert.IsNull(AnthropicMessageMapper.BuildRequest(o, new List<ChatMessage> { ChatMessage.User("q") }, null)["temperature"]);

            var legacy = Opts("claude-sonnet-4-6");
            legacy.Temperature = 0.3f;
            legacy.Level = ReasoningParamMapper.ReasoningLevel.Off;
            Assert.IsNotNull(AnthropicMessageMapper.BuildRequest(legacy, new List<ChatMessage> { ChatMessage.User("q") }, null)["temperature"]);
        }

        [Test]
        public void AtMostFourCacheBreakpoints()
        {
            var history = new List<ChatMessage> { ChatMessage.System("s") };
            for (int i = 0; i < 10; i++)
            {
                history.Add(ChatMessage.User("u" + i));
                history.Add(ChatMessage.Assistant("a" + i));
            }
            history.Add(ChatMessage.User("last"));
            var body = AnthropicMessageMapper.BuildRequest(Opts(), history, new List<ToolDefinition> { Tool("x") });
            int marks = body.SelectTokens("$..cache_control").Count();
            Assert.LessOrEqual(marks, 4);
            Assert.GreaterOrEqual(marks, 2);
        }

        /// <summary>
        /// 核心不变量：追加式历史下，第 N 次请求（去掉 cache_control 后）是第 N+1 次请求的严格前缀。
        /// 这是 prompt 缓存能命中的前提。
        /// </summary>
        [Test]
        public void PrefixStability_AppendOnlyHistory()
        {
            var tools = new List<ToolDefinition> { Tool("a"), Tool("b") };
            var history = new List<ChatMessage> { ChatMessage.System("system prompt"), ChatMessage.User("task") };

            var a1 = ChatMessage.Assistant("step", new List<ToolCall> { Call("c1", "a", "{\"k\":1}") });
            a1.ReasoningBlocks = new List<ReasoningBlock> { new() { Type = "thinking", Thinking = "plan", Signature = "s1" } };
            history.Add(a1);
            history.Add(ChatMessage.Tool("c1", "result 1"));
            var req1 = Strip(AnthropicMessageMapper.BuildRequest(Opts(), history, tools));

            var a2 = ChatMessage.Assistant("step 2", new List<ToolCall> { Call("c2", "b", "{}") });
            history.Add(a2);
            history.Add(ChatMessage.Tool("c2", "result 2"));
            history.Add(new ChatMessage { Role = "system", Content = "note", Kind = MessageKind.OperatorNote });
            var req2 = Strip(AnthropicMessageMapper.BuildRequest(Opts(), history, tools));

            Assert.IsTrue(JToken.DeepEquals(req1["system"], req2["system"]));
            Assert.IsTrue(JToken.DeepEquals(req1["tools"], req2["tools"]));
            var m1 = (JArray)req1["messages"];
            var m2 = (JArray)req2["messages"];
            Assert.Greater(m2.Count, m1.Count - 1);
            // 除最后一条（可能被后续 tool_result / reminder 合并扩展）外，全部逐字节相同
            for (int i = 0; i < m1.Count - 1; i++)
                Assert.IsTrue(JToken.DeepEquals(m1[i], m2[i]), $"message {i} changed between requests");
            // 最后一条：req1 的内容块是 req2 同位置内容块的前缀
            var last1 = (JArray)m1.Last["content"];
            var same2 = (JArray)m2[m1.Count - 1]["content"];
            for (int i = 0; i < last1.Count; i++)
                Assert.IsTrue(JToken.DeepEquals(last1[i], same2[i]), $"last message block {i} changed");
        }

        private static JObject Strip(JObject body)
        {
            var copy = (JObject)body.DeepClone();
            foreach (var cc in copy.SelectTokens("$..cache_control").ToList())
                cc.Parent.Remove();
            return copy;
        }
    }

    [TestFixture]
    public class AnthropicStreamAccumulatorTests
    {
        private static List<StreamChunk> Feed(AnthropicStreamAccumulator acc, params string[] events)
        {
            var chunks = new List<StreamChunk>();
            foreach (var e in events) acc.Process(JObject.Parse(e), chunks.Add);
            return chunks;
        }

        [Test]
        public void TextThinkingAndToolUse_Accumulate()
        {
            var acc = new AnthropicStreamAccumulator();
            var chunks = Feed(acc,
                "{\"type\":\"message_start\",\"message\":{\"id\":\"m\",\"model\":\"claude-opus-4-7\",\"usage\":{\"input_tokens\":10,\"cache_read_input_tokens\":900,\"cache_creation_input_tokens\":0,\"output_tokens\":1}}}",
                "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"\"}}",
                "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"let me\"}}",
                "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"signature_delta\",\"signature\":\"SIG\"}}",
                "{\"type\":\"content_block_stop\",\"index\":0}",
                "{\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}",
                "{\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hello\"}}",
                "{\"type\":\"content_block_stop\",\"index\":1}",
                "{\"type\":\"content_block_start\",\"index\":2,\"content_block\":{\"type\":\"tool_use\",\"id\":\"tu1\",\"name\":\"read\",\"input\":{}}}",
                "{\"type\":\"content_block_delta\",\"index\":2,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"path\\\":\"}}",
                "{\"type\":\"content_block_delta\",\"index\":2,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"\\\"a\\\"}\"}}",
                "{\"type\":\"content_block_stop\",\"index\":2}",
                "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"output_tokens\":42}}",
                "{\"type\":\"message_stop\"}");

            Assert.IsTrue(acc.Completed);
            Assert.AreEqual("tool_calls", AnthropicMessageMapper.MapStopReason(acc.StopReason));
            Assert.AreEqual(900, acc.Usage.CacheRead);
            Assert.AreEqual(42, acc.Usage.Output);
            Assert.IsTrue(chunks.Any(c => c.Type == StreamChunkType.ContentToken));
            Assert.IsTrue(chunks.Any(c => c.Type == StreamChunkType.Usage));

            var msg = acc.BuildMessage();
            Assert.AreEqual("Hello", msg.Content);
            Assert.AreEqual(1, msg.ToolCalls.Count);
            Assert.AreEqual("tu1", msg.ToolCalls[0].Id);
            Assert.AreEqual("a", (string)JObject.Parse(msg.ToolCalls[0].Function.Arguments)["path"]);
            Assert.AreEqual("SIG", msg.ReasoningBlocks[0].Signature);
            Assert.AreEqual("let me", msg.ReasoningBlocks[0].Thinking);
        }

        [Test]
        public void ErrorEvent_Throws()
        {
            var acc = new AnthropicStreamAccumulator();
            var ex = Assert.Throws<AnthropicApiException>(() => Feed(acc,
                "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}"));
            Assert.AreEqual("overloaded_error", ex.ErrorType);
        }

        [Test]
        public void FromMessage_NonStream()
        {
            var acc = AnthropicStreamAccumulator.FromMessage(JObject.Parse(
                "{\"id\":\"m\",\"model\":\"claude-opus-4-7\",\"stop_reason\":\"end_turn\",\"content\":[{\"type\":\"text\",\"text\":\"hi\"}],\"usage\":{\"input_tokens\":3,\"output_tokens\":1}}"));
            Assert.AreEqual("hi", acc.BuildMessage().Content);
            Assert.AreEqual("stop", AnthropicMessageMapper.MapStopReason(acc.StopReason));
        }
    }

    [TestFixture]
    public class OpenAIDialectAdapterTests
    {
        private static JObject Body(List<ChatMessage> messages, string model)
            => new()
            {
                ["model"] = model,
                ["messages"] = JArray.FromObject(messages.Select(m => new JObject { ["role"] = m.Role, ["content"] = m.Content }))
            };

        [Test]
        public void DeepSeek_EchoesReasoningContentOnToolCallTurns()
        {
            var assistant = ChatMessage.Assistant("", new List<ToolCall>
            {
                new() { Id = "c", Type = "function", Function = new FunctionCall { Name = "x", Arguments = "{}" } }
            });
            assistant.ReasoningContent = "thought";
            var messages = new List<ChatMessage> { ChatMessage.User("q"), assistant, ChatMessage.Tool("c", "r") };
            var body = Body(messages, "deepseek-reasoner");

            OpenAIDialectAdapter.Apply(body, messages, OpenAIDialects.DeepSeek, "deepseek-reasoner");
            Assert.AreEqual("thought", (string)body["messages"][1]["reasoning_content"]);
            Assert.IsNull(body["messages"][0]["reasoning_content"]);
        }

        [Test]
        public void OpenAI_PromptCacheKey_IsStableAcrossTurns()
        {
            var turn1 = new List<ChatMessage> { ChatMessage.System("s"), ChatMessage.User("first") };
            var turn2 = new List<ChatMessage>(turn1) { ChatMessage.Assistant("a"), ChatMessage.User("second") };
            var b1 = Body(turn1, "gpt-5");
            var b2 = Body(turn2, "gpt-5");
            OpenAIDialectAdapter.Apply(b1, turn1, OpenAIDialects.OpenAI, "gpt-5");
            OpenAIDialectAdapter.Apply(b2, turn2, OpenAIDialects.OpenAI, "gpt-5");
            Assert.IsNotNull(b1["prompt_cache_key"]);
            Assert.AreEqual((string)b1["prompt_cache_key"], (string)b2["prompt_cache_key"]);
        }

        [Test]
        public void OpenRouter_Claude_AddsCacheControl_OtherModelsUntouched()
        {
            var messages = new List<ChatMessage> { ChatMessage.System("sys"), ChatMessage.User("q") };
            var claude = Body(messages, "anthropic/claude-sonnet-4-6");
            OpenAIDialectAdapter.Apply(claude, messages, OpenAIDialects.OpenRouter, "anthropic/claude-sonnet-4-6");
            Assert.GreaterOrEqual(claude.SelectTokens("$..cache_control").Count(), 2);

            var gpt = Body(messages, "openai/gpt-5");
            OpenAIDialectAdapter.Apply(gpt, messages, OpenAIDialects.OpenRouter, "openai/gpt-5");
            Assert.AreEqual(0, gpt.SelectTokens("$..cache_control").Count());
        }
    }

    [TestFixture]
    public class ToolChangeTests
    {
        private static ToolDefinition Tool(string name, bool deferred = false) => new()
        {
            Type = "function",
            Function = new FunctionDefinition
            {
                Name = name,
                Description = name + " tool",
                Parameters = JObject.Parse("{\"type\":\"object\",\"properties\":{}}")
            },
            DeferLoading = deferred
        };

        private static ToolCall Call(string id, string name) => new()
        {
            Id = id,
            Type = "function",
            Function = new FunctionCall { Name = name, Arguments = "{}" }
        };

        private static AnthropicRequestOptions Opts(bool toolChanges) => new() { Model = "claude-opus-5-5", ToolChanges = toolChanges };

        private static List<ToolDefinition> Universe() => new() { Tool("a"), Tool("b", deferred: true), Tool("c", deferred: true) };

        private static JObject Strip(JObject body)
        {
            var copy = (JObject)body.DeepClone();
            foreach (var cc in copy.SelectTokens("$..cache_control").ToList())
                cc.Parent.Remove();
            return copy;
        }

        private static string ToolNames(JObject body)
            => string.Join(",", ((JArray)body["tools"]).Select(t => t.Value<string>("name")));

        [Test]
        public void Supported_DeclaresUniverse_AndRendersToolAddition()
        {
            var history = new List<ChatMessage>
            {
                ChatMessage.System("sys"),
                ChatMessage.User("task"),
                ToolChanges.CreateAddition(new[] { "b" })
            };
            var body = AnthropicMessageMapper.BuildRequest(Opts(true), history, Universe());

            Assert.AreEqual("a,b,c", ToolNames(body));
            Assert.IsNull(body["tools"][0]["defer_loading"]);
            Assert.AreEqual(true, body["tools"][1].Value<bool>("defer_loading"));

            var msgs = (JArray)body["messages"];
            Assert.AreEqual(2, msgs.Count);
            Assert.AreEqual("user", msgs[0].Value<string>("role"));
            Assert.AreEqual("system", msgs[1].Value<string>("role"));
            Assert.AreEqual("tool_addition", msgs[1]["content"][0].Value<string>("type"));
            Assert.AreEqual("b", msgs[1]["content"][0]["tool"].Value<string>("name"));
            // tool_addition 系统消息不能带断点；断点落在前一条 user 上
            Assert.IsNull(msgs[1].SelectToken("$..cache_control"));
            Assert.IsNotNull(msgs[0].SelectToken("$..cache_control"));
            Assert.IsTrue(AnthropicMessageMapper.UsesToolChanges(Opts(true), Universe()));
        }

        [Test]
        public void Supported_ActivationKeepsToolsAndPrefixStable()
        {
            var tools = Universe();
            var history = new List<ChatMessage> { ChatMessage.System("sys"), ChatMessage.User("task") };
            history.Add(ChatMessage.Assistant("look", new List<ToolCall> { Call("c1", "a") }));
            history.Add(ChatMessage.Tool("c1", "r1"));
            var req1 = Strip(AnthropicMessageMapper.BuildRequest(Opts(true), history, tools));

            // request_tools 激活了 b：只在末尾追加
            history.Add(ToolChanges.CreateAddition(new[] { "b" }));
            var req2 = Strip(AnthropicMessageMapper.BuildRequest(Opts(true), history, tools));

            history.Add(ChatMessage.Assistant("use b", new List<ToolCall> { Call("c2", "b") }));
            history.Add(ChatMessage.Tool("c2", "r2"));
            var req3 = Strip(AnthropicMessageMapper.BuildRequest(Opts(true), history, tools));

            Assert.IsTrue(JToken.DeepEquals(req1["tools"], req2["tools"]));
            Assert.IsTrue(JToken.DeepEquals(req2["tools"], req3["tools"]));
            Assert.IsTrue(JToken.DeepEquals(req1["system"], req3["system"]));

            var m1 = (JArray)req1["messages"];
            var m2 = (JArray)req2["messages"];
            var m3 = (JArray)req3["messages"];
            for (int i = 0; i < m1.Count; i++)
                Assert.IsTrue(JToken.DeepEquals(m1[i], m2[i]), $"req1→req2 message {i} changed");
            for (int i = 0; i < m2.Count - 1; i++)
                Assert.IsTrue(JToken.DeepEquals(m2[i], m3[i]), $"req2→req3 message {i} changed");
            Assert.AreEqual("system", m3[m2.Count - 1].Value<string>("role"));
            Assert.AreEqual("assistant", m3[m2.Count].Value<string>("role"));
        }

        [Test]
        public void Unsupported_SendsOnlyEnabledTools_AndDropsToolChange()
        {
            var history = new List<ChatMessage> { ChatMessage.System("sys"), ChatMessage.User("task") };
            var before = AnthropicMessageMapper.BuildRequest(Opts(false), history, Universe());
            Assert.AreEqual("a", ToolNames(before));

            history.Add(ToolChanges.CreateAddition(new[] { "c" }));
            var after = AnthropicMessageMapper.BuildRequest(Opts(false), history, Universe());
            Assert.AreEqual("a,c", ToolNames(after));
            Assert.IsNull(after.SelectToken("$..defer_loading"));
            Assert.IsFalse(((JArray)after["messages"]).Any(m => m.Value<string>("role") == "system"));
        }

        [Test]
        public void Supported_UsedDeferredToolWithoutRecord_AddedBeforeUse()
        {
            // ToolChange 被压缩掉，但历史里已调用过 c：c 必须在调用它的 assistant 之前启用
            var history = new List<ChatMessage>
            {
                ChatMessage.System("sys"),
                ChatMessage.User("task"),
                ChatMessage.Assistant("", new List<ToolCall> { Call("c1", "c") }),
                ChatMessage.Tool("c1", "r1")
            };
            var msgs = (JArray)AnthropicMessageMapper.BuildRequest(Opts(true), history, Universe())["messages"];
            Assert.AreEqual("user,system,assistant,user", string.Join(",", msgs.Select(m => m.Value<string>("role"))));
            Assert.AreEqual("c", msgs[1]["content"][0]["tool"].Value<string>("name"));
        }

        [Test]
        public void Supported_DuplicateAndUndeclaredAdditionsIgnored()
        {
            var history = new List<ChatMessage>
            {
                ChatMessage.System("sys"),
                ChatMessage.User("task"),
                ToolChanges.CreateAddition(new[] { "b", "gone" }),
                ChatMessage.Assistant("ok"),
                ChatMessage.User("again"),
                ToolChanges.CreateAddition(new[] { "b" })
            };
            var msgs = (JArray)AnthropicMessageMapper.BuildRequest(Opts(true), history, Universe())["messages"];
            var additions = msgs.SelectTokens("$..tool.name").Select(t => t.Value<string>()).ToList();
            CollectionAssert.AreEqual(new[] { "b" }, additions);
            Assert.AreEqual("user,system,assistant,user", string.Join(",", msgs.Select(m => m.Value<string>("role"))));
        }

        [Test]
        public void EffectiveTools_And_StripMessages()
        {
            var history = new List<ChatMessage>
            {
                ChatMessage.User("task"),
                ToolChanges.CreateAddition(new[] { "b" }),
                ChatMessage.Assistant("", new List<ToolCall> { Call("x", "c") })
            };
            var effective = ToolChanges.EffectiveTools(Universe(), history);
            CollectionAssert.AreEqual(new[] { "a", "b", "c" }, effective.Select(t => t.Function.Name).ToList());
            CollectionAssert.AreEqual(new[] { "a" }, ToolChanges.EffectiveTools(Universe(), new List<ChatMessage>()).Select(t => t.Function.Name).ToList());

            var stripped = ToolChanges.StripMessages(history);
            Assert.AreEqual(2, stripped.Count);
            Assert.IsFalse(stripped.Any(m => m.Kind == MessageKind.ToolChange));
            var plain = new List<ChatMessage> { ChatMessage.User("x") };
            Assert.AreSame(plain, ToolChanges.StripMessages(plain));
        }

        [Test]
        public void PendingAdditions_OnlyVisibleDeferredNotYetAdded()
        {
            var history = new List<ChatMessage> { ChatMessage.User("task"), ToolChanges.CreateAddition(new[] { "b" }) };
            var pending = ToolChanges.PendingAdditions(Universe(), new HashSet<string> { "a", "b", "c" }, history);
            CollectionAssert.AreEqual(new[] { "c" }, pending);
            Assert.AreEqual(0, ToolChanges.PendingAdditions(Universe(), new HashSet<string> { "a" }, history).Count);
        }

        [Test]
        public void ToolChangeMessage_RoundTripsNames()
        {
            var m = ToolChanges.CreateAddition(new[] { "x_tool", "y_tool" });
            Assert.AreEqual(MessageKind.ToolChange, m.Kind);
            Assert.AreEqual("system", m.Role);
            CollectionAssert.AreEqual(new[] { "x_tool", "y_tool" }, ToolChanges.GetAddedNames(m).ToList());
            Assert.AreEqual(0, ToolChanges.GetAddedNames(ChatMessage.System("Tools now available: z")).Count);
        }

        [Test]
        public void Registry_ToolChangeSupport()
        {
            Assert.IsTrue(ModelRegistry.Lookup("claude-opus-5-5").SupportsToolChanges);
            Assert.IsTrue(ModelRegistry.Lookup("claude-sonnet-5-5").SupportsToolChanges);
            Assert.IsTrue(ModelRegistry.Lookup("claude-opus-4-8").SupportsToolChanges);
            Assert.IsFalse(ModelRegistry.Lookup("claude-sonnet-5").SupportsToolChanges);
            Assert.IsFalse(ModelRegistry.Lookup("claude-haiku-5-5").SupportsToolChanges);
            Assert.IsFalse(ModelRegistry.Lookup("deepseek-chat").SupportsToolChanges);
        }

        [Test]
        public void TrimToFit_KeepsDroppedToolAdditions()
        {
            var big = new string('x', 4000);
            var history = new List<ChatMessage>
            {
                ChatMessage.System("sys"),
                ChatMessage.User("old " + big),
                ToolChanges.CreateAddition(new[] { "b" }),
                ChatMessage.Assistant("old answer " + big),
                ChatMessage.User("new question"),
                ChatMessage.Assistant("new answer")
            };
            var trimmed = AgentCore.Editor.Core.ContextWindowManager.TrimToFit(history, 1500, 0);
            Assert.Less(trimmed.Count, history.Count);
            Assert.AreEqual("new question", trimmed[1].Content);
            Assert.AreEqual(MessageKind.ToolChange, trimmed[2].Kind);
            CollectionAssert.AreEqual(new[] { "b" }, ToolChanges.GetAddedNames(trimmed[2]).ToList());
            Assert.AreEqual(6, history.Count, "original history must not be modified");
        }

        [Test]
        public void ToolChangeErrorDetection()
        {
            Assert.IsTrue(AnthropicMessagesClient.IsToolChangeError("{\"error\":{\"message\":\"tools.3.defer_loading: Extra inputs are not permitted\"}}"));
            Assert.IsTrue(AnthropicMessagesClient.IsToolChangeError("role 'system' is not supported on this model"));
            Assert.IsFalse(AnthropicMessagesClient.IsToolChangeError("messages.1: max_tokens too large"));
        }
    }

    [TestFixture]
    public class ToolOrderStabilizerTests
    {
        private static ToolDefinition T(string n) => new() { Type = "function", Function = new FunctionDefinition { Name = n } };
        private static string Names(List<ToolDefinition> l) => string.Join(",", l.Select(t => t.Function.Name));

        [Test]
        public void KeepsFirstSeenOrder_AppendsNewcomersSorted()
        {
            var s = new ToolOrderStabilizer();
            Assert.AreEqual("a,b,c", Names(s.Apply(new List<ToolDefinition> { T("c"), T("a"), T("b") })));
            Assert.AreEqual("a,b,c", Names(s.Apply(new List<ToolDefinition> { T("b"), T("c"), T("a") })));
            Assert.AreEqual("a,b,c,d,e", Names(s.Apply(new List<ToolDefinition> { T("e"), T("a"), T("d"), T("b"), T("c") })));
            Assert.AreEqual("a,c,e", Names(s.Apply(new List<ToolDefinition> { T("e"), T("c"), T("a") })));
        }

        [Test]
        public void Reset_Forgets()
        {
            var s = new ToolOrderStabilizer();
            s.Apply(new List<ToolDefinition> { T("z"), T("y") });
            s.Reset();
            Assert.AreEqual("a,z", Names(s.Apply(new List<ToolDefinition> { T("z"), T("a") })));
        }
    }

    [TestFixture]
    public class ActiveModelConfigResolverTests
    {
        private static ProviderProfile P(string endpoint, string format = ApiFormats.Auto, string dialect = OpenAIDialects.Auto, string auth = AuthSchemes.Auto)
            => new() { endpoint = endpoint, apiFormat = format, dialect = dialect, authScheme = auth };

        [Test]
        public void ApiFormat_AutoDetectsAnthropicHost()
        {
            Assert.AreEqual(ApiFormats.Anthropic, ActiveModelConfig.ResolveApiFormat(P("https://api.anthropic.com")));
            Assert.AreEqual(ApiFormats.OpenAI, ActiveModelConfig.ResolveApiFormat(P("https://api.openai.com/v1")));
            Assert.AreEqual(ApiFormats.Anthropic, ActiveModelConfig.ResolveApiFormat(P("http://my-proxy", ApiFormats.Anthropic)));
        }

        [Test]
        public void Dialect_AutoDetectsByHost()
        {
            Assert.AreEqual(OpenAIDialects.OpenAI, ActiveModelConfig.ResolveDialect(P("https://api.openai.com/v1")));
            Assert.AreEqual(OpenAIDialects.DeepSeek, ActiveModelConfig.ResolveDialect(P("https://api.deepseek.com")));
            Assert.AreEqual(OpenAIDialects.OpenRouter, ActiveModelConfig.ResolveDialect(P("https://openrouter.ai/api/v1")));
            Assert.AreEqual(OpenAIDialects.Generic, ActiveModelConfig.ResolveDialect(P("http://10.0.0.5:8000/v1")));
            Assert.AreEqual(OpenAIDialects.LiteLLM, ActiveModelConfig.ResolveDialect(P("http://10.0.0.5:4000", dialect: OpenAIDialects.LiteLLM)));
        }

        [Test]
        public void AuthScheme_FollowsFormat()
        {
            Assert.AreEqual(AuthSchemes.XApiKey, ActiveModelConfig.ResolveAuthScheme(P("https://api.anthropic.com")));
            Assert.AreEqual(AuthSchemes.Bearer, ActiveModelConfig.ResolveAuthScheme(P("https://api.openai.com")));
            Assert.AreEqual(AuthSchemes.Bearer, ActiveModelConfig.ResolveAuthScheme(P("https://api.anthropic.com", auth: AuthSchemes.Bearer)));
        }
    }
}

namespace AgentCore.Tests.Editor.LLM
{
    [NUnit.Framework.TestFixture]
    public class EditorIntegrationRegressionTests
    {
        [NUnit.Framework.Test]
        public void NativeModelDiscovery_UsesVersionAndConfiguredAuth()
        {
            var profile = new AgentCore.Editor.Config.ProviderProfile {
                endpoint = "https://example.invalid", apiFormat = "anthropic",
                authScheme = AgentCore.Editor.Config.AuthSchemes.XApiKey };
            using var request = AgentCore.Editor.Config.Settings.ModelSettingsService.BuildModelsRequest(profile, "test-key");
            NUnit.Framework.Assert.AreEqual("https://example.invalid/v1/models", request.RequestUri.AbsoluteUri);
            NUnit.Framework.Assert.IsTrue(request.Headers.Contains("anthropic-version"));
            NUnit.Framework.Assert.IsTrue(request.Headers.Contains("x-api-key"));
            NUnit.Framework.Assert.IsFalse(request.Headers.Contains("Authorization"));
        }

        [NUnit.Framework.Test]
        public void AnthropicStream_Truncated_Throws()
        {
            using var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes("data: {\"type\":\"ping\"}\n\n"));
            NUnit.Framework.Assert.ThrowsAsync<System.IO.EndOfStreamException>(async () =>
                await AgentCore.Editor.LLM.Anthropic.AnthropicSseReader.ReadAsync(stream, _ => {}, default));
        }

        [NUnit.Framework.Test]
        public void AnthropicStream_Cancelled_Throws()
        {
            using var stream = new System.IO.MemoryStream();
            using var ct = new System.Threading.CancellationTokenSource(); ct.Cancel();
            NUnit.Framework.Assert.CatchAsync<System.OperationCanceledException>(async () =>
                await AgentCore.Editor.LLM.Anthropic.AnthropicSseReader.ReadAsync(stream, _ => {}, ct.Token));
        }

        [NUnit.Framework.Test]
        public async System.Threading.Tasks.Task AnthropicStream_MessageStop_Completes()
        {
            using var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes("data: {\"type\":\"message_stop\"}\n\n"));
            int events = 0;
            await AgentCore.Editor.LLM.Anthropic.AnthropicSseReader.ReadAsync(stream, _ => events++, default);
            NUnit.Framework.Assert.AreEqual(1, events);
        }

        [NUnit.Framework.Test]
        public async System.Threading.Tasks.Task OpenAIStream_EmptyChoicesUsage_IsDelivered()
        {
            var data = "data: {\"model\":\"test\",\"choices\":[],\"usage\":{\"prompt_tokens\":1000,\"completion_tokens\":10,\"prompt_tokens_details\":{\"cached_tokens\":800}}}\n\ndata: [DONE]\n\n";
            using var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(data));
            var chunks = new List<StreamChunk>();
            await new StreamingResponseParser().ParseStreamAsync(stream, chunks.Add);
            var usage = chunks.Single(c => c.Type == StreamChunkType.Usage).Usage;
            NUnit.Framework.Assert.AreEqual(800, usage.CacheRead);
            NUnit.Framework.Assert.AreEqual(200, usage.InputUncached);
        }

        [NUnit.Framework.Test]
        public void SignedThinking_PersistenceRoundTrip_PreservesSignature()
        {
            var message = ChatMessage.Assistant("OK");
            message.ReasoningBlocks = new List<ReasoningBlock> {
                new ReasoningBlock { Type = "thinking", Thinking = "test", Signature = "synthetic-signature" } };
            var serialized = AgentCore.Editor.Session.SerializableChatMessage.FromChatMessage(message);
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(serialized);
            var restored = Newtonsoft.Json.JsonConvert.DeserializeObject<AgentCore.Editor.Session.SerializableChatMessage>(json).ToChatMessage();
            NUnit.Framework.Assert.AreEqual("synthetic-signature", restored.ReasoningBlocks[0].Signature);
            NUnit.Framework.Assert.IsTrue(restored.ReasoningBlocks[0].IsReplayable);
        }

        [NUnit.Framework.Test]
        public void PlanningNewline_InSeparateChunk_IsNotEmitted()
        {
            var extractor = new AgentCore.Editor.Core.VisiblePlanningTraceExtractor();
            extractor.Append("---THINKING---"); extractor.Append("\n");
            var delta = extractor.Append("plan\n---ACTION---\nfinal");
            NUnit.Framework.Assert.AreEqual("plan\n", delta.ReasoningContent);
            NUnit.Framework.Assert.AreEqual("final", delta.VisibleContent);
        }
    }
}
