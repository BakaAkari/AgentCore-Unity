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
    /// "始终允许"后重新评估排队确认（<see cref="ToolConfirmationTrust.IsAllowedByPermissionRules"/>）：
    /// 新 allow 规则放行的自动批准；ask / deny 仍覆盖、ask 触发、batch 外壳保持等待。
    /// 结论须与 Dispatcher 对同一调用的规则判定一致。
    /// </summary>
    [TestFixture]
    public class ToolConfirmationRuleRecheckTests
    {
        #region 测试辅助

        private sealed class FakeTool : IAgentTool
        {
            public ToolMetadata Metadata { get; }

            public FakeTool(string name, ToolRiskLevel risk, ToolCapability caps)
            {
                Metadata = new ToolMetadata(
                    name,
                    "fake tool for rule re-check tests",
                    "Test",
                    JObject.Parse(@"{""type"":""object"",""properties"":{""action"":{""type"":""string""},""path"":{""type"":""string""}}}"),
                    requiresMainThread: false,
                    riskLevel: risk,
                    capabilities: caps,
                    requiresConfirmation: false);
            }

            public Task<ToolResult> ExecuteAsync(JObject parameters, CancellationToken cancellationToken = default)
                => Task.FromResult(ToolResult.Ok("executed"));
        }

        /// <summary>记录 Dispatcher 发出的确认请求（模拟用户尚未处理、在队列中等待）。</summary>
        private sealed class RecordingProvider : IToolConfirmationProvider
        {
            public readonly List<ToolConfirmationRequest> Requests = new List<ToolConfirmationRequest>();

            public Task<bool> RequestConfirmationAsync(ToolConfirmationRequest request, CancellationToken ct)
            {
                Requests.Add(request);
                return Task.FromResult(true);
            }
        }

        private sealed class ListRuleSource : IPermissionRuleSource
        {
            public readonly List<ScopedPermissionRule> Rules = new List<ScopedPermissionRule>();
            public IReadOnlyList<ScopedPermissionRule> GetActiveRules() => Rules;
        }

        private RecordingProvider _provider;
        private ListRuleSource _rules;
        private ToolCallDispatcher _dispatcher;

        [SetUp]
        public void SetUp()
        {
            ToolRegistry.Instance.Clear();
            ToolRegistry.Instance.Register(new FakeTool("write_tool", ToolRiskLevel.Medium, ToolCapability.ModifyScene));
            _provider = new RecordingProvider();
            _rules = new ListRuleSource();
            _dispatcher = new ToolCallDispatcher(ToolRegistry.Instance, _provider, _rules);
        }

        [TearDown]
        public void TearDown()
        {
            ToolRegistry.Instance.Clear();
        }

        private static ScopedPermissionRule Rule(string effect, string tool, string path = null, string id = null)
        {
            return new ScopedPermissionRule(new PermissionRule
            {
                id = id ?? effect + "-" + tool,
                effect = effect,
                tool = tool,
                path = path,
                enabled = true
            }, PermissionRuleScope.User);
        }

        /// <summary>无规则时派发一次调用，返回它产生的（待确认）请求。</summary>
        private async Task<ToolConfirmationRequest> QueuedRequest(string args)
        {
            await _dispatcher.DispatchAsync(new ToolCall
            {
                Id = "call_recheck",
                Type = "function",
                Function = new FunctionCall { Name = "write_tool", Arguments = args }
            });
            Assert.AreEqual(1, _provider.Requests.Count, "无规则时中风险写操作应进入确认");
            return _provider.Requests[0];
        }

        private static ToolConfirmationRequest PlainRequest(string tool, string action = "", IReadOnlyList<string> rulePaths = null)
        {
            return new ToolConfirmationRequest(
                tool,
                action,
                new ToolExecutionRisk(ToolRiskLevel.Medium, ToolCapability.ModifyScene, WorkspaceOperationRisk.Safe, false),
                tool,
                "",
                null,
                null,
                null)
            {
                RulePaths = rulePaths
            };
        }

        #endregion

        [Test]
        public async Task NewAllowRule_ApprovesQueuedRequest()
        {
            var queued = await QueuedRequest(@"{""action"":""set""}");
            Assert.IsFalse(ToolConfirmationTrust.IsAllowedByPermissionRules(queued, _rules.Rules), "无规则不放行");

            _rules.Rules.Add(Rule("allow", "write_tool"));
            Assert.IsTrue(ToolConfirmationTrust.IsAllowedByPermissionRules(queued, _rules.Rules));
        }

        [Test]
        public async Task AllowRuleForOtherTool_DoesNotApprove()
        {
            var queued = await QueuedRequest(@"{""action"":""set""}");
            _rules.Rules.Add(Rule("allow", "other_tool"));
            Assert.IsFalse(ToolConfirmationTrust.IsAllowedByPermissionRules(queued, _rules.Rules));
        }

        [Test]
        public async Task AskRuleStillCovering_DoesNotApprove()
        {
            var queued = await QueuedRequest(@"{""action"":""set""}");
            _rules.Rules.Add(Rule("allow", "write_tool"));
            _rules.Rules.Add(Rule("ask", "write_tool.set"));
            Assert.IsFalse(ToolConfirmationTrust.IsAllowedByPermissionRules(queued, _rules.Rules));
        }

        [Test]
        public async Task PathConditionedDeny_DoesNotApprove_OtherPathsDo()
        {
            var secret = await QueuedRequest(@"{""action"":""set"",""path"":""Assets\\Secret\\a.txt""}");
            _provider.Requests.Clear();
            var open = await QueuedRequest(@"{""action"":""set"",""path"":""Assets/Public/a.txt""}");

            _rules.Rules.Add(Rule("allow", "write_tool"));
            _rules.Rules.Add(Rule("deny", "*", path: "Assets/Secret/**"));

            Assert.IsFalse(ToolConfirmationTrust.IsAllowedByPermissionRules(secret, _rules.Rules), "deny 仍覆盖该路径");
            Assert.IsTrue(ToolConfirmationTrust.IsAllowedByPermissionRules(open, _rules.Rules));
        }

        [Test]
        public async Task PathConditionedAllow_UsesSameNormalizedPathsAsDispatcher()
        {
            var inside = await QueuedRequest(@"{""action"":""set"",""path"":""Assets\\Gen\\a.txt""}");
            _provider.Requests.Clear();
            var outside = await QueuedRequest(@"{""action"":""set"",""path"":""Assets/Other/a.txt""}");
            _provider.Requests.Clear();
            var noPath = await QueuedRequest(@"{""action"":""set""}");

            _rules.Rules.Add(Rule("allow", "write_tool", path: "Assets/Gen/**"));

            Assert.IsTrue(ToolConfirmationTrust.IsAllowedByPermissionRules(inside, _rules.Rules));
            Assert.IsFalse(ToolConfirmationTrust.IsAllowedByPermissionRules(outside, _rules.Rules));
            Assert.IsFalse(ToolConfirmationTrust.IsAllowedByPermissionRules(noPath, _rules.Rules), "路径条件规则不匹配无路径调用");
        }

        [Test]
        public void AskTriggeredRequest_NeverAutoApproved()
        {
            var askMatch = new PermissionRuleMatch(
                new PermissionRule { id = "ask-w", effect = "ask", tool = "write_tool" },
                PermissionRuleScope.User,
                PermissionRuleEffect.Ask);
            var asked = PlainRequest("write_tool").WithPermissionRule(askMatch, "ask");

            var rules = new List<ScopedPermissionRule> { Rule("allow", "write_tool") };
            Assert.IsFalse(ToolConfirmationTrust.IsAllowedByPermissionRules(asked, rules));
        }

        [Test]
        public void WithPermissionRule_KeepsRulePaths()
        {
            var askMatch = new PermissionRuleMatch(
                new PermissionRule { id = "ask-w", effect = "ask", tool = "write_tool" },
                PermissionRuleScope.User,
                PermissionRuleEffect.Ask);
            var paths = new[] { "Assets/a.txt" };
            var asked = PlainRequest("write_tool", rulePaths: paths).WithPermissionRule(askMatch, "ask");
            Assert.AreSame(paths, asked.RulePaths);
        }

        [Test]
        public void BatchWrapperAndEmptyInputs_NotApproved()
        {
            var rules = new List<ScopedPermissionRule> { Rule("allow", "*") };
            Assert.IsFalse(ToolConfirmationTrust.IsAllowedByPermissionRules(PlainRequest("batch_execute"), rules));
            Assert.IsFalse(ToolConfirmationTrust.IsAllowedByPermissionRules(null, rules));
            Assert.IsFalse(ToolConfirmationTrust.IsAllowedByPermissionRules(PlainRequest("write_tool"), null));
            Assert.IsFalse(ToolConfirmationTrust.IsAllowedByPermissionRules(PlainRequest("write_tool"), new List<ScopedPermissionRule>()));
            Assert.IsTrue(ToolConfirmationTrust.IsAllowedByPermissionRules(PlainRequest("write_tool"), rules));
        }
    }
}
