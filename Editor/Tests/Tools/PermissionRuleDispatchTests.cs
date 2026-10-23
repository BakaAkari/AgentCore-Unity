using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.LLM;
using AgentCore.Editor.Tools;
using AgentCore.Editor.Tools.Safety;
using AgentCore.Editor.Workspace.Safety;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.Tools
{
    /// <summary>
    /// 权限规则接入策略评估 / ToolCallDispatcher / 会话信任后的行为测试：
    /// 优先级、YOLO 绕过 deny、Trust Low/Med 不放行 ask、batch_execute 逐子操作生效。
    /// </summary>
    [TestFixture]
    public class PermissionRuleDispatchTests
    {
        #region 测试辅助

        private sealed class FakeTool : IAgentTool
        {
            public int ExecuteCount { get; private set; }
            public ToolMetadata Metadata { get; }

            public FakeTool(string name, ToolRiskLevel risk, ToolCapability caps, JObject schema = null)
            {
                Metadata = new ToolMetadata(
                    name,
                    "fake tool for permission rule tests",
                    "Test",
                    schema ?? DefaultSchema,
                    requiresMainThread: false,
                    riskLevel: risk,
                    capabilities: caps,
                    requiresConfirmation: false);
            }

            public Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
            {
                ExecuteCount++;
                return Task.FromResult(ToolResult.Ok("executed"));
            }
        }

        /// <summary>模拟 ChatWindow：会话信任 + YOLO 查询 + "用户点击"结果。</summary>
        private sealed class FakeProvider : IToolConfirmationProvider, IToolSessionTrustState
        {
            public readonly HashSet<ToolConfirmationTrustScope> Scopes = new HashSet<ToolConfirmationTrustScope>();
            public readonly List<ToolConfirmationRequest> Prompts = new List<ToolConfirmationRequest>();
            public readonly List<ToolConfirmationRequest> Requests = new List<ToolConfirmationRequest>();
            public bool ClickApprove = true;

            public bool IsYoloActive => Scopes.Contains(ToolConfirmationTrustScope.SessionAll);

            public Task<bool> RequestConfirmationAsync(ToolConfirmationRequest request, CancellationToken ct)
            {
                Requests.Add(request);
                if (ToolConfirmationTrust.IsTrusted(request, Scopes))
                    return Task.FromResult(true);
                Prompts.Add(request);
                return Task.FromResult(ClickApprove);
            }
        }

        private sealed class ListRuleSource : IPermissionRuleSource
        {
            public readonly List<ScopedPermissionRule> Rules = new List<ScopedPermissionRule>();
            public IReadOnlyList<ScopedPermissionRule> GetActiveRules() => Rules;
        }

        private static readonly JObject DefaultSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": { ""type"": ""string"" },
                ""path"": { ""type"": ""string"" }
            }
        }");

        private static readonly JObject BatchSchema = JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": { ""operations"": { ""type"": ""array"" } }
        }");

        private FakeTool _read;
        private FakeTool _write;
        private FakeTool _delete;
        private FakeTool _batch;
        private FakeProvider _provider;
        private ListRuleSource _rules;
        private ToolCallDispatcher _dispatcher;

        [SetUp]
        public void SetUp()
        {
            ToolRegistry.Instance.Clear();
            _read = new FakeTool("read_tool", ToolRiskLevel.ReadOnly, ToolCapability.ReadProject);
            _write = new FakeTool("write_tool", ToolRiskLevel.Medium, ToolCapability.ModifyScene);
            _delete = new FakeTool("delete_tool", ToolRiskLevel.Medium, ToolCapability.DeleteProjectFiles);
            _batch = new FakeTool("batch_execute", ToolRiskLevel.High, ToolCapability.BatchExecute, BatchSchema);
            ToolRegistry.Instance.Register(_read);
            ToolRegistry.Instance.Register(_write);
            ToolRegistry.Instance.Register(_delete);
            ToolRegistry.Instance.Register(_batch);

            _provider = new FakeProvider();
            _rules = new ListRuleSource();
            _dispatcher = new ToolCallDispatcher(ToolRegistry.Instance, _provider, _rules);
        }

        [TearDown]
        public void TearDown()
        {
            ToolRegistry.Instance.Clear();
        }

        private void AddRule(string effect, string tool, string action = null, string path = null, string id = null,
            PermissionRuleScope scope = PermissionRuleScope.User, string note = null)
        {
            _rules.Rules.Add(new ScopedPermissionRule(new PermissionRule
            {
                id = id ?? effect + "-" + tool,
                effect = effect,
                tool = tool,
                action = action,
                path = path,
                note = note,
                enabled = true
            }, scope));
        }

        private static ToolCall Call(string name, string args)
        {
            return new ToolCall
            {
                Id = "call_rules",
                Type = "function",
                Function = new FunctionCall { Name = name, Arguments = args }
            };
        }

        private Task<ToolCallResult> Dispatch(string name, string args) => _dispatcher.DispatchAsync(Call(name, args));

        private static string Batch(params string[] ops) => "{\"operations\":[" + string.Join(",", ops) + "]}";

        #endregion

        #region 无规则 = 现有行为

        [Test]
        public async Task NoRules_ReadToolRunsWithoutPrompt_WriteToolPrompts()
        {
            var r1 = await Dispatch("read_tool", @"{""action"":""get""}");
            Assert.IsTrue(r1.Result.Success);
            Assert.AreEqual(0, _provider.Requests.Count);

            var r2 = await Dispatch("write_tool", @"{""action"":""set""}");
            Assert.IsTrue(r2.Result.Success);
            Assert.AreEqual(1, _provider.Prompts.Count);
            Assert.IsNull(_provider.Prompts[0].TriggeredByRule);
        }

        [Test]
        public void ApplyPermissionRule_NoMatch_ReturnsSameDecision()
        {
            var decision = ToolRiskPolicy.Evaluate(_write.Metadata, WorkspaceOperationRisk.Safe, "write_tool", "set");
            var applied = ToolRiskPolicy.ApplyPermissionRule(decision, PermissionRuleMatch.None, false, _write.Metadata, "write_tool", "set");
            Assert.AreEqual(decision.Outcome, applied.Outcome);
            Assert.AreSame(decision.ConfirmationRequest, applied.ConfirmationRequest);
            Assert.AreSame(decision.Reasons, applied.Reasons);
        }

        #endregion

        #region allow

        [Test]
        public async Task AllowRule_SkipsConfirmation()
        {
            AddRule("allow", "write_tool");
            var result = await Dispatch("write_tool", @"{""action"":""set""}");
            Assert.IsTrue(result.Result.Success);
            Assert.AreEqual(1, _write.ExecuteCount);
            Assert.AreEqual(0, _provider.Requests.Count);
        }

        [Test]
        public async Task AllowRule_SkipsFloorOperation()
        {
            // 底线操作（删除）在无规则时必弹确认；allow 是用户显式选择，直接放行
            var baseline = ToolRiskPolicy.Evaluate(_delete.Metadata, WorkspaceOperationRisk.Safe, "delete_tool", "delete");
            Assert.IsTrue(baseline.RequiresConfirmation);

            AddRule("allow", "delete_tool.delete");
            var result = await Dispatch("delete_tool", @"{""action"":""delete""}");
            Assert.IsTrue(result.Result.Success);
            Assert.AreEqual(0, _provider.Requests.Count);
        }

        #endregion

        #region ask

        [Test]
        public async Task AskRule_ForcesConfirmationOnReadOnlyTool_AndNamesRule()
        {
            AddRule("ask", "read_tool", id: "ask-read");
            var result = await Dispatch("read_tool", @"{""action"":""get""}");
            Assert.IsTrue(result.Result.Success);
            Assert.AreEqual(1, _provider.Prompts.Count);
            var req = _provider.Prompts[0];
            Assert.IsNotNull(req.TriggeredByRule);
            Assert.AreEqual("ask-read", req.TriggeredByRule.Rule.id);
            StringAssert.Contains("ask-read", string.Join("\n", req.Reasons));
        }

        [Test]
        public async Task AskRule_UserDenies_ToolNotExecuted()
        {
            AddRule("ask", "read_tool");
            _provider.ClickApprove = false;
            var result = await Dispatch("read_tool", @"{""action"":""get""}");
            Assert.IsFalse(result.Result.Success);
            Assert.AreEqual(0, _read.ExecuteCount);
        }

        [Test]
        public async Task AskRule_TrustLowMed_DoesNotAutoPass()
        {
            _provider.Scopes.Add(ToolConfirmationTrustScope.SessionLowMediumRisk);
            AddRule("ask", "read_tool");
            await Dispatch("read_tool", @"{""action"":""get""}");
            Assert.AreEqual(1, _provider.Prompts.Count, "Trust Low/Med 不应自动放行 ask 规则");
        }

        [Test]
        public async Task AskRule_OnAlreadyConfirmingTool_KeepsRiskReasonsAndMarksRule()
        {
            AddRule("ask", "write_tool");
            await Dispatch("write_tool", @"{""action"":""set""}");
            Assert.AreEqual(1, _provider.Prompts.Count);
            var req = _provider.Prompts[0];
            Assert.IsNotNull(req.TriggeredByRule);
            Assert.GreaterOrEqual(req.Reasons.Count, 2, "规则原因 + 原风险原因");
        }

        [Test]
        public async Task AskRule_Yolo_PassesWithoutPrompt()
        {
            _provider.Scopes.Add(ToolConfirmationTrustScope.SessionAll);
            AddRule("ask", "read_tool");
            var result = await Dispatch("read_tool", @"{""action"":""get""}");
            Assert.IsTrue(result.Result.Success);
            Assert.AreEqual(0, _provider.Prompts.Count);
        }

        [Test]
        public void Trust_LowMed_StillPassesPlainLowRiskRequest()
        {
            var plain = new ToolConfirmationRequest("t", "", new ToolExecutionRisk(ToolRiskLevel.Low, ToolCapability.ReadProject, WorkspaceOperationRisk.Safe, false),
                "t", "", null, null, null);
            var scopes = new HashSet<ToolConfirmationTrustScope> { ToolConfirmationTrustScope.SessionLowMediumRisk };
            Assert.IsTrue(ToolConfirmationTrust.IsTrusted(plain, scopes));

            var ruleMatch = new PermissionRuleMatch(new PermissionRule { id = "x", effect = "ask", tool = "t" }, PermissionRuleScope.User, PermissionRuleEffect.Ask);
            var asked = plain.WithPermissionRule(ruleMatch, "ask");
            Assert.IsFalse(ToolConfirmationTrust.IsTrusted(asked, scopes));
            scopes.Add(ToolConfirmationTrustScope.SessionAll);
            Assert.IsTrue(ToolConfirmationTrust.IsTrusted(asked, scopes));
        }

        #endregion

        #region deny

        [Test]
        public async Task DenyRule_ReturnsToolFailureNamingRule()
        {
            AddRule("deny", "read_tool", id: "no-read", note: "keep out");
            var result = await Dispatch("read_tool", @"{""action"":""get""}");
            Assert.IsFalse(result.Result.Success);
            Assert.AreEqual(0, _read.ExecuteCount);
            Assert.AreEqual(0, _provider.Requests.Count);
            StringAssert.Contains("no-read", result.Result.Error);
            StringAssert.Contains("keep out", result.Result.Error);
            Assert.IsTrue(result.Decision.HasValue && result.Decision.Value.IsBlocked);
        }

        [Test]
        public async Task DenyRule_BeatsAllowAndAsk()
        {
            AddRule("allow", "*");
            AddRule("ask", "write_tool");
            AddRule("deny", "write_tool.set", id: "deny-set");
            var denied = await Dispatch("write_tool", @"{""action"":""set""}");
            Assert.IsFalse(denied.Result.Success);
            StringAssert.Contains("deny-set", denied.Result.Error);

            var asked = await Dispatch("write_tool", @"{""action"":""other""}");
            Assert.IsTrue(asked.Result.Success);
            Assert.AreEqual(1, _provider.Prompts.Count);

            var allowed = await Dispatch("read_tool", @"{""action"":""get""}");
            Assert.IsTrue(allowed.Result.Success);
            Assert.AreEqual(1, _provider.Prompts.Count);
        }

        [Test]
        public async Task DenyRule_Yolo_IsBypassed()
        {
            _provider.Scopes.Add(ToolConfirmationTrustScope.SessionAll);
            AddRule("deny", "write_tool");
            var result = await Dispatch("write_tool", @"{""action"":""set""}");
            Assert.IsTrue(result.Result.Success, "YOLO = 零拦截，deny 规则不生效");
            Assert.AreEqual(1, _write.ExecuteCount);
            Assert.AreEqual(0, _provider.Prompts.Count);
        }

        [Test]
        public async Task DenyRule_NoTrustStateProvider_StillDenies()
        {
            AddRule("deny", "read_tool");
            var dispatcher = new ToolCallDispatcher(ToolRegistry.Instance, AutoToolConfirmationProvider.AlwaysApprove(), _rules);
            var result = await dispatcher.DispatchAsync(Call("read_tool", @"{""action"":""get""}"));
            Assert.IsFalse(result.Result.Success);
        }

        [Test]
        public async Task DenyRule_PathGlob()
        {
            AddRule("deny", "*", path: "Assets/Secret/**", id: "secret");
            var denied = await Dispatch("read_tool", @"{""action"":""get"",""path"":""Assets/Secret/key.txt""}");
            Assert.IsFalse(denied.Result.Success);
            StringAssert.Contains("secret", denied.Result.Error);

            var deniedBackslash = await Dispatch("read_tool", @"{""action"":""get"",""path"":""Assets\\Secret\\key.txt""}");
            Assert.IsFalse(deniedBackslash.Result.Success);

            var ok = await Dispatch("read_tool", @"{""action"":""get"",""path"":""Assets/Public/a.txt""}");
            Assert.IsTrue(ok.Result.Success);

            var noPath = await Dispatch("read_tool", @"{""action"":""get""}");
            Assert.IsTrue(noPath.Result.Success);
        }

        #endregion

        #region batch_execute

        private const string ReadOp = @"{""tool"":""read_tool"",""args"":{""action"":""get""}}";
        private const string WriteOp = @"{""tool"":""write_tool"",""args"":{""action"":""set""}}";

        [Test]
        public async Task Batch_NoRules_WriteSubOpPromptsOnce()
        {
            var result = await Dispatch("batch_execute", Batch(ReadOp, WriteOp));
            Assert.IsTrue(result.Result.Success);
            Assert.AreEqual(1, _provider.Prompts.Count);
            Assert.IsNull(_provider.Prompts[0].TriggeredByRule);
        }

        [Test]
        public async Task Batch_DenySubOp_BlocksWholeBatchNamingOperationAndRule()
        {
            AddRule("deny", "write_tool", id: "no-write");
            var result = await Dispatch("batch_execute", Batch(ReadOp, WriteOp));
            Assert.IsFalse(result.Result.Success);
            Assert.AreEqual(0, _batch.ExecuteCount);
            StringAssert.Contains("#2 write_tool", result.Result.Error);
            StringAssert.Contains("no-write", result.Result.Error);
        }

        [Test]
        public async Task Batch_DenySubOp_Yolo_Bypassed()
        {
            _provider.Scopes.Add(ToolConfirmationTrustScope.SessionAll);
            AddRule("deny", "write_tool");
            var result = await Dispatch("batch_execute", Batch(ReadOp, WriteOp));
            Assert.IsTrue(result.Result.Success);
            Assert.AreEqual(1, _batch.ExecuteCount);
        }

        [Test]
        public async Task Batch_AllowSubOp_NoPrompt()
        {
            AddRule("allow", "write_tool");
            var result = await Dispatch("batch_execute", Batch(ReadOp, WriteOp));
            Assert.IsTrue(result.Result.Success);
            Assert.AreEqual(0, _provider.Requests.Count);
        }

        [Test]
        public async Task Batch_AskSubOp_PromptsWithRule_TrustLowMedDoesNotPass()
        {
            _provider.Scopes.Add(ToolConfirmationTrustScope.SessionLowMediumRisk);
            AddRule("ask", "read_tool", id: "ask-read");
            var result = await Dispatch("batch_execute", Batch(ReadOp, ReadOp));
            Assert.IsTrue(result.Result.Success);
            Assert.AreEqual(1, _provider.Prompts.Count);
            Assert.IsNotNull(_provider.Prompts[0].TriggeredByRule);
            Assert.AreEqual("ask-read", _provider.Prompts[0].TriggeredByRule.Rule.id);
        }

        [Test]
        public async Task Batch_WrapperItselfIsNotMatched()
        {
            AddRule("deny", "batch_execute");
            var result = await Dispatch("batch_execute", Batch(ReadOp));
            Assert.IsTrue(result.Result.Success, "规则只匹配子操作，batch_execute 外壳不参与");
        }

        #endregion
    }
}
