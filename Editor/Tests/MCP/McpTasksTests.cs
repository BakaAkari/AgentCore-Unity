using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AgentCore.Editor.Components.MCP.Bridge;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.MCP
{
    /// <summary>Tasks 扩展（io.modelcontextprotocol/tasks）：长调用转为任务、tasks/get 轮询、任务内确认（tasks/update）、取消。</summary>
    [TestFixture]
    public class McpTasksTests
    {
        private const string Token = "relay-test-token";
        private string _project;
        private McpRelayTests.FakeBridge _bridge;
        private McpBridgeServer _server;
        private McpRelayTests.RelayProcess _relay;

        [SetUp]
        public void SetUp()
        {
            _project = Path.Combine(Path.GetTempPath(), "agentcore-mcp-tasks-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(_project, "Library", "AgentCore", "Mcp"));
            Assert.IsTrue(McpRelayTests.SharedBuild.Success, McpRelayTests.SharedBuild.Error);
            _bridge = new McpRelayTests.FakeBridge();
            _server = new McpBridgeServer(Token, _bridge);
            _server.Start(0);
            McpRelayTests.WriteEndpoint(_project, _server.Port);
            _relay = new McpRelayTests.RelayProcess(McpRelayTests.SharedBuild, _project);
        }

        [TearDown]
        public void TearDown()
        {
            _relay?.Dispose();
            _server?.Dispose();
            try { Directory.Delete(_project, true); } catch { /* ignore */ }
        }

        private static JObject Meta(bool tasks, bool elicitation = false)
        {
            var capabilities = new JObject();
            if (tasks)
                capabilities["extensions"] = new JObject { ["io.modelcontextprotocol/tasks"] = new JObject() };
            if (elicitation)
                capabilities["elicitation"] = new JObject { ["form"] = new JObject() };
            return new JObject
            {
                ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                ["io.modelcontextprotocol/clientInfo"] = new JObject { ["name"] = "tasks-test" },
                ["io.modelcontextprotocol/clientCapabilities"] = capabilities
            };
        }

        private JObject P(JObject extra, JObject meta)
        {
            extra["_meta"] = meta;
            return extra;
        }

        private async Task<JObject> PollUntil(string taskId, Func<string, bool> done, int timeoutMs = 20000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                var response = await _relay.ResponseAsync(_relay.Request("tasks/get", P(new JObject { ["taskId"] = taskId }, Meta(true))));
                var status = (string)response["result"]["status"];
                if (done(status))
                    return (JObject)response["result"];
                await Task.Delay(300);
            }
            Assert.Fail("task did not reach the expected status");
            return null;
        }

        [Test]
        public async Task Discover_AdvertisesTasksExtension()
        {
            var response = await _relay.ResponseAsync(_relay.Request("server/discover", P(new JObject(), Meta(false))));
            Assert.IsNotNull(response["result"]["capabilities"]["extensions"]["io.modelcontextprotocol/tasks"]);
        }

        [Test]
        public async Task SlowCall_BecomesTask_ThenCompletes()
        {
            var first = await _relay.ResponseAsync(_relay.Request("tools/call",
                P(new JObject { ["name"] = "slow", ["arguments"] = new JObject { ["ms"] = 5000 } }, Meta(true))), 20000);
            Assert.AreEqual("task", (string)first["result"]["resultType"]);
            Assert.AreEqual("working", (string)first["result"]["status"]);
            Assert.Greater((int)first["result"]["pollIntervalMs"], 0);
            var taskId = (string)first["result"]["taskId"];

            var finalState = await PollUntil(taskId, s => s == "completed");
            Assert.AreEqual("complete", (string)finalState["resultType"]);
            Assert.AreEqual("slow done", (string)finalState["result"]["content"][0]["text"]);
        }

        [Test]
        public async Task FastCall_WithTasksSupport_AnswersDirectly()
        {
            var response = await _relay.ResponseAsync(_relay.Request("tools/call",
                P(new JObject { ["name"] = "echo", ["arguments"] = new JObject() }, Meta(true))), 20000);
            Assert.AreEqual("complete", (string)response["result"]["resultType"]);
            Assert.IsNull(response["result"]["taskId"]);
        }

        [Test]
        public async Task SlowCall_WithoutTasksSupport_Blocks()
        {
            var response = await _relay.ResponseAsync(_relay.Request("tools/call",
                P(new JObject { ["name"] = "slow", ["arguments"] = new JObject { ["ms"] = 4000 } }, Meta(false))), 20000);
            Assert.AreEqual("complete", (string)response["result"]["resultType"]);
            Assert.AreEqual("slow done", (string)response["result"]["content"][0]["text"]);
        }

        [Test]
        public async Task Task_ConfirmationViaTasksUpdate()
        {
            var first = await _relay.ResponseAsync(_relay.Request("tools/call",
                P(new JObject { ["name"] = "slow_confirm", ["arguments"] = new JObject { ["ms"] = 4000 } }, Meta(true, elicitation: true))), 20000);
            Assert.AreEqual("task", (string)first["result"]["resultType"]);
            var taskId = (string)first["result"]["taskId"];

            var waiting = await PollUntil(taskId, s => s == "input_required");
            Assert.AreEqual("elicitation/create", (string)waiting["inputRequests"]["approve"]["method"]);

            var update = await _relay.ResponseAsync(_relay.Request("tasks/update", P(new JObject
            {
                ["taskId"] = taskId,
                ["inputResponses"] = new JObject { ["approve"] = new JObject { ["action"] = "accept", ["content"] = new JObject { ["approve"] = true } } }
            }, Meta(true, elicitation: true))));
            Assert.AreEqual("complete", (string)update["result"]["resultType"]);

            var finalState = await PollUntil(taskId, s => s == "completed");
            Assert.AreEqual("approved", (string)finalState["result"]["content"][0]["text"]);
        }

        [Test]
        public async Task Task_Cancel_MarksCancelledAndCancelsCall()
        {
            var first = await _relay.ResponseAsync(_relay.Request("tools/call",
                P(new JObject { ["name"] = "slow", ["arguments"] = new JObject { ["ms"] = 8000 } }, Meta(true))), 20000);
            var taskId = (string)first["result"]["taskId"];

            await _relay.ResponseAsync(_relay.Request("tasks/cancel", P(new JObject { ["taskId"] = taskId }, Meta(true))));
            var state = await PollUntil(taskId, s => s == "cancelled", 5000);
            Assert.AreEqual("cancelled", (string)state["status"]);
            await Task.Delay(300);
            lock (_bridge.Messages)
                Assert.IsTrue(_bridge.Messages.Any(m => (string)m["type"] == McpBridgeProtocol.Cancel), "cancel forwarded to the editor");
        }

        [Test]
        public async Task UnknownTask_IsInvalidParams()
        {
            var response = await _relay.ResponseAsync(_relay.Request("tasks/get", P(new JObject { ["taskId"] = "nope" }, Meta(true))));
            Assert.AreEqual(-32602, (int)response["error"]["code"]);
        }
    }
}
