using System.Collections.Generic;
using AgentCore.Editor.LLM;
using AgentCore.Editor.LLM.Anthropic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.LLM
{
    /// <summary>
    /// v1.22.0 多档 reasoning：等级解析、能力吸附、请求管线与 400 学习（纯函数 / 离线）。
    /// </summary>
    [TestFixture]
    public class ReasoningParamMapperTests
    {
        [SetUp]
        public void SetUp() => RequestPruningRegistry.ClearAll();

        [TearDown]
        public void TearDown() => RequestPruningRegistry.ClearAll();

        [TestCase("off", ReasoningParamMapper.ReasoningLevel.Off)]
        [TestCase("minimal", ReasoningParamMapper.ReasoningLevel.Minimal)]
        [TestCase("low", ReasoningParamMapper.ReasoningLevel.Low)]
        [TestCase("medium", ReasoningParamMapper.ReasoningLevel.Medium)]
        [TestCase("high", ReasoningParamMapper.ReasoningLevel.High)]
        [TestCase("xhigh", ReasoningParamMapper.ReasoningLevel.XHigh)]
        [TestCase("max", ReasoningParamMapper.ReasoningLevel.Max)]
        [TestCase("MINIMAL", ReasoningParamMapper.ReasoningLevel.Minimal)]
        [TestCase("X-High", ReasoningParamMapper.ReasoningLevel.XHigh)]
        [TestCase("auto", ReasoningParamMapper.ReasoningLevel.Auto)]
        [TestCase("", ReasoningParamMapper.ReasoningLevel.Auto)]
        [TestCase(null, ReasoningParamMapper.ReasoningLevel.Auto)]
        [TestCase("bogus", ReasoningParamMapper.ReasoningLevel.Auto)]
        public void ParseLevel_AllTiers(string value, ReasoningParamMapper.ReasoningLevel expected)
        {
            Assert.AreEqual(expected, ReasoningParamMapper.ParseLevel(value));
        }

        [Test]
        public void ParseLevel_UltraAlias_IsMax()
        {
            Assert.AreEqual(ReasoningParamMapper.ReasoningLevel.Max, ReasoningParamMapper.ParseLevel("ultra"));
        }

        [TestCase(ReasoningParamMapper.ReasoningLevel.Auto, "auto")]
        [TestCase(ReasoningParamMapper.ReasoningLevel.Off, "off")]
        [TestCase(ReasoningParamMapper.ReasoningLevel.Minimal, "minimal")]
        [TestCase(ReasoningParamMapper.ReasoningLevel.Low, "low")]
        [TestCase(ReasoningParamMapper.ReasoningLevel.Medium, "medium")]
        [TestCase(ReasoningParamMapper.ReasoningLevel.High, "high")]
        [TestCase(ReasoningParamMapper.ReasoningLevel.XHigh, "xhigh")]
        [TestCase(ReasoningParamMapper.ReasoningLevel.Max, "max")]
        public void LevelToString_RoundTrips(ReasoningParamMapper.ReasoningLevel level, string expected)
        {
            Assert.AreEqual(expected, ReasoningParamMapper.LevelToString(level));
            Assert.AreEqual(expected, ReasoningParamMapper.ToEffortString(level));
            Assert.AreEqual(level, ReasoningParamMapper.ParseLevel(expected));
        }

        [TestCase(ReasoningParamMapper.ReasoningLevel.Minimal, 1024)]
        [TestCase(ReasoningParamMapper.ReasoningLevel.Low, 2048)]
        [TestCase(ReasoningParamMapper.ReasoningLevel.Medium, 8192)]
        [TestCase(ReasoningParamMapper.ReasoningLevel.High, 16384)]
        [TestCase(ReasoningParamMapper.ReasoningLevel.XHigh, 24576)]
        [TestCase(ReasoningParamMapper.ReasoningLevel.Max, 32768)]
        [TestCase(ReasoningParamMapper.ReasoningLevel.Off, 0)]
        [TestCase(ReasoningParamMapper.ReasoningLevel.Auto, 4096)]
        public void GetBudgetTokens_AllTiers(ReasoningParamMapper.ReasoningLevel level, int expected)
        {
            Assert.AreEqual(expected, ReasoningParamMapper.GetBudgetTokens(level));
        }

        [Test]
        public void Resolve_EmptyEffortEnabled_IsAuto()
        {
            var plan = ReasoningParamMapper.ResolveEffectivePlan(
                ReasoningParamMapper.ReasoningLevel.Auto, "", true, "https://openrouter.ai/api/v1", "anthropic/claude-opus-4-7");

            Assert.AreEqual(ReasoningParamMapper.ReasoningLevel.Auto, plan.RequestedLevel);
            Assert.AreEqual(ReasoningParamMapper.ReasoningLevel.Auto, plan.EffectiveLevel);
            Assert.IsFalse(plan.WasClamped);
            Assert.IsFalse(plan.IsDisabled);
            Assert.AreEqual(4096, plan.BudgetTokens);
        }

        [Test]
        public void Resolve_ReasoningDisabled_IsOff()
        {
            var plan = ReasoningParamMapper.ResolveEffectivePlan(
                ReasoningParamMapper.ReasoningLevel.Auto, "", false, "https://openrouter.ai/api/v1", "gpt-5");

            Assert.AreEqual(ReasoningParamMapper.ReasoningLevel.Off, plan.RequestedLevel);
            Assert.AreEqual(ReasoningParamMapper.ReasoningLevel.Off, plan.EffectiveLevel);
            Assert.IsTrue(plan.IsDisabled);
            Assert.AreEqual(0, plan.BudgetTokens);
        }

        [Test]
        public void Resolve_DefaultEffort_IsUsedWhenNoOverride()
        {
            var plan = ReasoningParamMapper.ResolveEffectivePlan(
                ReasoningParamMapper.ReasoningLevel.Auto, "medium", true, "https://openrouter.ai/api/v1", "gpt-5");

            Assert.AreEqual(ReasoningParamMapper.ReasoningLevel.Medium, plan.RequestedLevel);
            Assert.AreEqual(ReasoningParamMapper.ReasoningLevel.Medium, plan.EffectiveLevel);
        }

        [Test]
        public void Resolve_ModelCap_ClampsDown()
        {
            // gpt-5 built-in: minimal..high
            var plan = ReasoningParamMapper.ResolveEffectivePlan(
                ReasoningParamMapper.ReasoningLevel.Max, "", true, "https://api.openai.com/v1", "gpt-5");

            Assert.AreEqual(ReasoningParamMapper.ReasoningLevel.Max, plan.RequestedLevel);
            Assert.AreEqual(ReasoningParamMapper.ReasoningLevel.High, plan.EffectiveLevel);
            Assert.IsTrue(plan.WasClamped);
            Assert.AreEqual("model_cap", plan.ClampReason);
        }

        [Test]
        public void Resolve_ModelFloor_SnapsMinimalUp()
        {
            var profile = new ModelProfile
            {
                Prefix = "floor-model", Family = ModelFamily.OpenAI, MinEffort = "low", MaxEffort = "high"
            };
            var plan = ReasoningParamMapper.ResolveEffectivePlan(
                ReasoningParamMapper.ReasoningLevel.Minimal, "", true, "http://test", "floor-model", profile);

            Assert.AreEqual(ReasoningParamMapper.ReasoningLevel.Low, plan.EffectiveLevel);
            Assert.IsTrue(plan.WasClamped);
            Assert.AreEqual("model_floor", plan.ClampReason);
        }

        [Test]
        public void Resolve_AlwaysOn_OffMapsToLow()
        {
            var plan = ReasoningParamMapper.ResolveEffectivePlan(
                ReasoningParamMapper.ReasoningLevel.Off, "", true, "https://api.anthropic.com", "claude-opus-5-5");

            Assert.AreEqual(ReasoningParamMapper.ReasoningLevel.Off, plan.RequestedLevel);
            Assert.AreEqual(ReasoningParamMapper.ReasoningLevel.Low, plan.EffectiveLevel);
            Assert.IsTrue(plan.WasClamped);
            Assert.AreEqual("always_on", plan.ClampReason);
            Assert.IsFalse(plan.IsDisabled);
        }

        [Test]
        public void Resolve_UnknownModel_OptimisticPassThrough()
        {
            var plan = ReasoningParamMapper.ResolveEffectivePlan(
                ReasoningParamMapper.ReasoningLevel.Max, "", true, "http://localhost:8000/v1", "my-local-llama");

            Assert.AreEqual(ReasoningParamMapper.ReasoningLevel.Max, plan.EffectiveLevel);
            Assert.IsFalse(plan.WasClamped);
        }

        [Test]
        public void Resolve_LearnedRuntimeCap_ClampsDown()
        {
            RequestPruningRegistry.RegisterReasoningConstraint("http://learned", "my-local-llama", "medium");

            var plan = ReasoningParamMapper.ResolveEffectivePlan(
                ReasoningParamMapper.ReasoningLevel.Max, "", true, "http://learned", "my-local-llama");

            Assert.AreEqual(ReasoningParamMapper.ReasoningLevel.Medium, plan.EffectiveLevel);
            Assert.IsTrue(plan.WasClamped);
            Assert.AreEqual("runtime_learned", plan.ClampReason);
        }

        [Test]
        public void Resolve_LearnedBannedMinimal_SnapsUpToLow()
        {
            RequestPruningRegistry.RegisterReasoningConstraint(
                "http://learned2", "my-local-llama", null, new[] { "minimal" });

            var plan = ReasoningParamMapper.ResolveEffectivePlan(
                ReasoningParamMapper.ReasoningLevel.Minimal, "", true, "http://learned2", "my-local-llama");

            Assert.AreEqual(ReasoningParamMapper.ReasoningLevel.Low, plan.EffectiveLevel);
            Assert.IsTrue(plan.WasClamped);
            Assert.AreEqual("runtime_learned", plan.ClampReason);
        }

        [Test]
        public void IsTierSupported_UsesCapabilitiesAndLearnedBounds()
        {
            var gpt5 = ModelRegistry.Lookup("gpt-5");
            Assert.IsTrue(ReasoningResolution.IsTierSupported(gpt5, ReasoningParamMapper.ReasoningLevel.Minimal));
            Assert.IsTrue(ReasoningResolution.IsTierSupported(gpt5, ReasoningParamMapper.ReasoningLevel.High));
            Assert.IsFalse(ReasoningResolution.IsTierSupported(gpt5, ReasoningParamMapper.ReasoningLevel.XHigh));

            var alwaysOn = ModelRegistry.Lookup("claude-opus-5-5");
            Assert.IsFalse(ReasoningResolution.IsTierSupported(alwaysOn, ReasoningParamMapper.ReasoningLevel.Off));
            Assert.IsTrue(ReasoningResolution.IsTierSupported(alwaysOn, ReasoningParamMapper.ReasoningLevel.XHigh));
            Assert.IsFalse(ReasoningResolution.IsTierSupported(alwaysOn, ReasoningParamMapper.ReasoningLevel.Max));

            RequestPruningRegistry.RegisterReasoningConstraint("http://ui", "gpt-5", "medium");
            var plan = ReasoningParamMapper.ResolveEffectivePlan(
                ReasoningParamMapper.ReasoningLevel.Auto, "", true, "http://ui", "gpt-5");
            Assert.IsTrue(plan.IsTierSupported(ReasoningParamMapper.ReasoningLevel.Medium));
            Assert.IsFalse(plan.IsTierSupported(ReasoningParamMapper.ReasoningLevel.High));
        }
    }

    [TestFixture]
    public class ModelRegistryCapabilityTests
    {
        [TearDown]
        public void TearDown() => ModelRegistry.SetOverridesForTesting(null);

        [Test]
        public void OpenAI_BuiltIns_DeclareBounds()
        {
            Assert.AreEqual("high", ModelRegistry.Lookup("gpt-5").MaxEffort);
            Assert.AreEqual("minimal", ModelRegistry.Lookup("gpt-5").MinEffort);
            Assert.AreEqual("xhigh", ModelRegistry.Lookup("gpt-5.4").MaxEffort);
            Assert.AreEqual("high", ModelRegistry.Lookup("o3-mini").MaxEffort);
            Assert.AreEqual("low", ModelRegistry.Lookup("o1-preview").MinEffort);
            Assert.AreEqual(ReasoningProtocols.OpenAIEffort, ModelRegistry.Lookup("gpt-5").ReasoningProtocol);
        }

        [Test]
        public void OtherFamilies_DeclareBounds()
        {
            Assert.AreEqual("minimal", ModelRegistry.Lookup("gemini-2.5-pro").MinEffort);
            Assert.AreEqual("high", ModelRegistry.Lookup("gemini-2.5-pro").MaxEffort);
            Assert.AreEqual("high", ModelRegistry.Lookup("deepseek-chat").MaxEffort);
            Assert.AreEqual("high", ModelRegistry.Lookup("qwen3-max").MaxEffort);
            Assert.AreEqual("high", ModelRegistry.Lookup("glm-4.6").MaxEffort);
        }

        [Test]
        public void UnknownFamily_LeavesBoundsUnrestricted()
        {
            var profile = ModelRegistry.Lookup("my-local-llama");
            Assert.AreEqual(ModelFamily.Unknown, profile.Family);
            Assert.IsTrue(string.IsNullOrEmpty(profile.MinEffort));
            Assert.IsTrue(string.IsNullOrEmpty(profile.MaxEffort));
        }

        [Test]
        public void SupportsXHigh_RemainsConsistent()
        {
            Assert.IsTrue(ModelRegistry.Lookup("claude-opus-4-7").SupportsXHigh);
            Assert.IsFalse(ModelRegistry.Lookup("claude-sonnet-4-5").SupportsXHigh);
            Assert.IsTrue(string.IsNullOrEmpty(ModelRegistry.Lookup("claude-opus-4-7").MaxEffort));
        }
    }

    [TestFixture]
    public class RequestEnrichmentTierTests
    {
        [SetUp]
        public void SetUp() => RequestPruningRegistry.ClearAll();

        [TearDown]
        public void TearDown() => RequestPruningRegistry.ClearAll();

        private static ChatCompletionRequest Request()
            => new()
            {
                Model = "gpt-5",
                Messages = new List<ChatMessage> { ChatMessage.User("hi") },
                MaxTokens = 256
            };

        private static JObject Build(
            string overrideLevel, string endpoint, string model,
            bool enabled = true, string effort = "", int reasoningMaxTokens = 0)
        {
            var json = RequestEnrichment.BuildEnrichedJson(
                Request(), overrideLevel, endpoint, model, enabled, effort, reasoningMaxTokens);
            return JObject.Parse(json);
        }

        [Test]
        public void Auto_EmptyEffort_KeepsReasoningObjectAndThinkingFlag()
        {
            var body = Build(null, "https://openrouter.ai/api/v1", "anthropic/claude-opus-4-7", reasoningMaxTokens: 4096);

            Assert.IsNotNull(body["reasoning"]);
            Assert.IsNull(body["reasoning"]["effort"]);
            Assert.AreEqual(4096, (int)body["reasoning"]["max_tokens"]);
            Assert.AreEqual(true, body["chat_template_kwargs"].Value<bool>("thinking"));
        }

        [Test]
        public void Auto_DefaultEffort_RoutesThroughOpenAIEffort()
        {
            var body = Build(null, "https://api.openai.com/v1", "gpt-5", effort: "high");

            Assert.AreEqual("high", (string)body["reasoning_effort"]);
            Assert.IsNull(body["reasoning"]);
        }

        [Test]
        public void Auto_Disabled_SendsNoReasoning()
        {
            var body = Build(null, "https://openrouter.ai/api/v1", "anthropic/claude-opus-4-7", enabled: false);

            Assert.IsNull(body["reasoning"]);
            Assert.IsNull(body["chat_template_kwargs"]);
            Assert.IsNull(body["reasoning_effort"]);
        }

        [Test]
        public void OverrideMax_ClampedByModelCap()
        {
            var body = Build("max", "https://api.openai.com/v1", "gpt-5");

            Assert.AreEqual("high", (string)body["reasoning_effort"]);
        }

        [Test]
        public void OverrideXHigh_OpenRouter_RoutesEffortAndMaxTokens()
        {
            var body = Build("xhigh", "https://openrouter.ai/api/v1", "anthropic/claude-opus-4-7", reasoningMaxTokens: 2048);

            Assert.AreEqual("xhigh", (string)body["reasoning"]["effort"]);
            Assert.AreEqual(2048, (int)body["reasoning"]["max_tokens"]);
        }

        [Test]
        public void OverrideOff_OpenRouter_SendsNone()
        {
            var body = Build("off", "https://openrouter.ai/api/v1", "anthropic/claude-opus-4-7");

            Assert.AreEqual("none", (string)body["reasoning"]["effort"]);
        }

        [Test]
        public void OverrideHigh_Vllm_SendsThinkingAndEffort()
        {
            var body = Build("high", "http://localhost:8000/v1", "deepseek-v4-flash");

            Assert.AreEqual(true, body["chat_template_kwargs"].Value<bool>("thinking"));
            Assert.AreEqual("high", (string)body["reasoning"]["effort"]);
        }
    }

    [TestFixture]
    public class AnthropicTierTests
    {
        [SetUp]
        public void SetUp() => RequestPruningRegistry.ClearAll();

        [TearDown]
        public void TearDown() => RequestPruningRegistry.ClearAll();

        private static AnthropicRequestOptions Opts(string model) => new() { Model = model };

        private static JObject Build(AnthropicRequestOptions options)
            => AnthropicMessageMapper.BuildRequest(options, new List<ChatMessage> { ChatMessage.User("q") }, null);

        [TestCase(ReasoningParamMapper.ReasoningLevel.Minimal, 1024)]
        [TestCase(ReasoningParamMapper.ReasoningLevel.Low, 2048)]
        [TestCase(ReasoningParamMapper.ReasoningLevel.Medium, 8192)]
        [TestCase(ReasoningParamMapper.ReasoningLevel.High, 16384)]
        [TestCase(ReasoningParamMapper.ReasoningLevel.XHigh, 24576)]
        [TestCase(ReasoningParamMapper.ReasoningLevel.Max, 32768)]
        public void BudgetTokens_MultiTier(ReasoningParamMapper.ReasoningLevel level, int expected)
        {
            var o = Opts("test-claude");
            o.Level = level;
            o.Profile = new ModelProfile
            {
                Prefix = "test-claude",
                Family = ModelFamily.Anthropic,
                Thinking = ThinkingMode.BudgetTokens,
                MinEffort = "minimal",
                MaxEffort = "max"
            };

            var body = Build(o);

            Assert.AreEqual("enabled", (string)body["thinking"]["type"]);
            Assert.AreEqual(expected, (int)body["thinking"]["budget_tokens"]);
            Assert.Greater((int)body["max_tokens"], expected);
        }

        [Test]
        public void XHigh_OnLegacyClaude_ClampsToHigh()
        {
            var o = Opts("claude-sonnet-4-5");
            o.Level = ReasoningParamMapper.ReasoningLevel.XHigh;

            var body = Build(o);

            Assert.AreEqual(16384, (int)body["thinking"]["budget_tokens"]);
        }

        [Test]
        public void AlwaysOn_Off_UsesEffortLow()
        {
            var o = Opts("claude-opus-5-5");
            o.Level = ReasoningParamMapper.ReasoningLevel.Off;

            var body = Build(o);

            Assert.IsNull(body["thinking"]);
            Assert.AreEqual("low", (string)body["output_config"]["effort"]);
        }

        [Test]
        public void AlwaysOn_BetweenTools_KeepsSpecialShape()
        {
            var o = Opts("claude-sonnet-5-5");
            o.Level = ReasoningParamMapper.ReasoningLevel.Off;

            var body = Build(o);

            Assert.AreEqual("between_tools", (string)body["thinking"]["type"]);
        }

        [Test]
        public void AutoDefaultXHigh_ClampedForNonXHighModel()
        {
            var o = Opts("claude-sonnet-4-6");
            o.DefaultEffort = "xhigh";

            var body = Build(o);

            Assert.AreEqual("adaptive", (string)body["thinking"]["type"]);
            Assert.AreEqual("high", (string)body["output_config"]["effort"]);
        }

        [Test]
        public void Adaptive_Off_OmitsThinking()
        {
            var o = Opts("claude-opus-4-7");
            o.Level = ReasoningParamMapper.ReasoningLevel.Off;

            Assert.IsNull(Build(o)["thinking"]);
        }
    }

    [TestFixture]
    public class ReasoningPruningRegistryTests
    {
        [SetUp]
        public void SetUp() => RequestPruningRegistry.ClearAll();

        [TearDown]
        public void TearDown() => RequestPruningRegistry.ClearAll();

        private const string Endpoint = "http://test-endpoint";
        private const string Model = "test-model";

        [Test]
        public void Learn_InvalidValueWithSupportedList_SetsCap()
        {
            var result = RequestPruningRegistry.LearnReasoningFromErrorResponse(
                Endpoint, Model, "xhigh",
                "{\"error\":{\"message\":\"Invalid value: 'xhigh'. Supported values are: 'low', 'medium', 'high'\"}}");

            Assert.IsTrue(result.Learned);
            Assert.AreEqual("high", result.NextSuggestedEffort);

            var constraint = RequestPruningRegistry.GetReasoningConstraint(Endpoint, Model);
            Assert.AreEqual(ReasoningParamMapper.ReasoningLevel.High, constraint.MaxAllowedTier);
        }

        [Test]
        public void Learn_MustBeOneOf_ExtractsBracketList()
        {
            var result = RequestPruningRegistry.LearnReasoningFromErrorResponse(
                Endpoint, Model, "max",
                "{\"error\":{\"message\":\"reasoning_effort must be one of ['low', 'medium', 'high']\"}}");

            Assert.IsTrue(result.Learned);
            Assert.AreEqual("high", result.NextSuggestedEffort);
            Assert.AreEqual(
                ReasoningParamMapper.ReasoningLevel.High,
                RequestPruningRegistry.GetReasoningConstraint(Endpoint, Model).MaxAllowedTier);
        }

        [Test]
        public void Learn_NoList_ProgressivelyDowngrades()
        {
            var first = RequestPruningRegistry.LearnReasoningFromErrorResponse(
                Endpoint, Model, "max", "{\"error\":{\"message\":\"effort 'max' is not supported\"}}");
            Assert.IsTrue(first.Learned);
            Assert.AreEqual("xhigh", first.NextSuggestedEffort);
            Assert.AreEqual(
                ReasoningParamMapper.ReasoningLevel.XHigh,
                RequestPruningRegistry.GetReasoningConstraint(Endpoint, Model).MaxAllowedTier);

            var second = RequestPruningRegistry.LearnReasoningFromErrorResponse(
                Endpoint, Model, "xhigh", "{\"error\":{\"message\":\"effort 'xhigh' is not supported\"}}");
            Assert.AreEqual("high", second.NextSuggestedEffort);

            var third = RequestPruningRegistry.LearnReasoningFromErrorResponse(
                Endpoint, Model, "high", "{\"error\":{\"message\":\"effort 'high' is not supported\"}}");
            Assert.AreEqual("medium", third.NextSuggestedEffort);
            Assert.AreEqual(
                ReasoningParamMapper.ReasoningLevel.Medium,
                RequestPruningRegistry.GetReasoningConstraint(Endpoint, Model).MaxAllowedTier);
        }

        [Test]
        public void Learn_MinimalRejected_BansMinimalAndSnapsUp()
        {
            var result = RequestPruningRegistry.LearnReasoningFromErrorResponse(
                Endpoint, Model, "minimal", "{\"error\":{\"message\":\"'minimal' is not supported\"}}");

            Assert.IsTrue(result.Learned);
            Assert.AreEqual("low", result.NextSuggestedEffort);
            CollectionAssert.Contains(
                RequestPruningRegistry.GetReasoningConstraint(Endpoint, Model).BannedTiers,
                ReasoningParamMapper.ReasoningLevel.Minimal);

            var plan = ReasoningParamMapper.ResolveEffectivePlan(
                ReasoningParamMapper.ReasoningLevel.Minimal, "", true, Endpoint, Model);
            Assert.AreEqual(ReasoningParamMapper.ReasoningLevel.Low, plan.EffectiveLevel);
        }

        [Test]
        public void Learn_UnrelatedError_NotLearned()
        {
            Assert.IsFalse(RequestPruningRegistry.LearnReasoningFromErrorResponse(
                Endpoint, Model, "high", "{\"error\":{\"message\":\"invalid api key\"}}").Learned);
            Assert.IsFalse(RequestPruningRegistry.LearnReasoningFromErrorResponse(
                Endpoint, Model, "high", "{\"error\":{\"message\":\"Unsupported parameter: 'temperature'\"}}").Learned);
            Assert.IsTrue(RequestPruningRegistry.GetReasoningConstraint(Endpoint, Model).IsEmpty);
        }

        [Test]
        public void ClearAll_ClearsReasoningConstraints()
        {
            RequestPruningRegistry.LearnReasoningFromErrorResponse(
                Endpoint, Model, "max", "{\"error\":{\"message\":\"effort 'max' is not supported\"}}");
            Assert.IsFalse(RequestPruningRegistry.GetReasoningConstraint(Endpoint, Model).IsEmpty);

            RequestPruningRegistry.ClearAll();

            Assert.IsTrue(RequestPruningRegistry.GetReasoningConstraint(Endpoint, Model).IsEmpty);
        }

        [Test]
        public void ExistingFieldPruning_StillWorks()
        {
            var learned = RequestPruningRegistry.LearnFromErrorResponse(
                Endpoint, Model, "{\"error\":{\"message\":\"reasoning: Extra inputs are not permitted\"}}");

            CollectionAssert.Contains(learned, "reasoning");
            CollectionAssert.Contains(RequestPruningRegistry.GetBannedFields(Endpoint, Model), "reasoning");
        }
    }
}
