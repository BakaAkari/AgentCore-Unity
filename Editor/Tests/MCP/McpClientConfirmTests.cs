#if AGENTCORE_MCP
using System;
using System.IO;
using System.Threading.Tasks;
using AgentCore.Editor.Components.MCP.Bridge;
using AgentCore.Editor.Components.MCP.Config;
using AgentCore.Editor.Tools.Safety;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.MCP
{
    /// <summary>
    /// 在客户端确认（elicitation）：旧协议由 relay 发起 elicitation/create 请求；新协议用 MRTR（input_required + 签名 requestState + 重试）；
    /// 客户端不支持时回到 Unity 窗口。以及“信任 MCP 客户端”审查级别。
    /// </summary>
    [TestFixture]
    public class McpClientConfirmTests
    {
        private const string Token = "relay-test-token";
        private string _project;
        private McpBridgeServer _server;
        private McpRelayTests.RelayProcess _relay;

        [SetUp]
        public void SetUp()
        {
            _project = Path.Combine(Path.GetTempPath(), "agentcore-mcp-confirm-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(_project, "Library", "AgentCore", "Mcp"));
            Assert.IsTrue(McpRelayTests.SharedBuild.Success, McpRelayTests.SharedBuild.Error);
            _server = new McpBridgeServer(Token, new McpRelayTests.FakeBridge());
            _server.Start(0);
            McpRelayTests.WriteEndpoint(_project, _server.Port);
            _relay = new McpRelayTests.RelayProcess(McpRelayTests.SharedBuild, _project);
        }

        [TearDown]
        public void TearDown()
        {
            _relay?.Dispose();
            _server?.Dispose();
            McpSettings.OverrideForTests(null, null);
            try { Directory.Delete(_project, true); } catch { /* ignore */ }
        }

        private async Task InitLegacy(bool elicitation)
        {
            var capabilities = new JObject();
            if (elicitation)
                capabilities["elicitation"] = new JObject();
            var response = await _relay.ResponseAsync(_relay.Request("initialize", new JObject
            {
                ["protocolVersion"] = "2025-11-25",
                ["capabilities"] = capabilities,
                ["clientInfo"] = new JObject { ["name"] = "confirm-test" }
            }));
            Assert.IsNotNull(response["result"]);
            _relay.Notify("notifications/initialized");
        }

        private static JObject Meta(bool elicitation)
        {
            var capabilities = new JObject();
            if (elicitation)
                capabilities["elicitation"] = new JObject { ["form"] = new JObject() };
            return new JObject
            {
                ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                ["io.modelcontextprotocol/clientInfo"] = new JObject { ["name"] = "confirm-test" },
                ["io.modelcontextprotocol/clientCapabilities"] = capabilities
            };
        }

        private async Task AnswerElicitation(string action, bool approve)
        {
            var request = await _relay.WaitForAsync(m => (string)m["method"] == "elicitation/create");
            StringAssert.Contains("Delete asset", (string)request["params"]["message"]);
            Assert.AreEqual("form", (string)request["params"]["mode"]);
            Assert.AreEqual("boolean", (string)request["params"]["requestedSchema"]["properties"]["approve"]["type"]);
            _relay.Send(new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = request["id"],
                ["result"] = new JObject { ["action"] = action, ["content"] = new JObject { ["approve"] = approve } }
            });
        }

        private static string Text(JObject response) => (string)response["result"]["content"][0]["text"];

        [Test]
        public async Task Legacy_ElicitationAccepted_Approves()
        {
            await InitLegacy(elicitation: true);
            var callId = _relay.Request("tools/call", new JObject { ["name"] = "needs_confirm", ["arguments"] = new JObject() });
            await AnswerElicitation("accept", true);
            Assert.AreEqual("approved", Text(await _relay.ResponseAsync(callId)));
        }

        [Test]
        public async Task Legacy_ElicitationDeclined_Denies()
        {
            await InitLegacy(elicitation: true);
            var callId = _relay.Request("tools/call", new JObject { ["name"] = "needs_confirm", ["arguments"] = new JObject() });
            await AnswerElicitation("decline", false);
            Assert.AreEqual("denied", Text(await _relay.ResponseAsync(callId)));
        }

        [Test]
        public async Task Legacy_WithoutElicitation_FallsBackToUnity()
        {
            await InitLegacy(elicitation: false);
            var callId = _relay.Request("tools/call", new JObject { ["name"] = "needs_confirm", ["arguments"] = new JObject() });
            Assert.AreEqual("fallback", Text(await _relay.ResponseAsync(callId)));
        }

        [Test]
        public async Task Modern_InputRequired_ThenRetryWithApproval_CompletesOnRetry()
        {
            var firstId = _relay.Request("tools/call", new JObject
            {
                ["name"] = "needs_confirm",
                ["arguments"] = new JObject(),
                ["_meta"] = Meta(elicitation: true)
            });
            var first = await _relay.ResponseAsync(firstId, 20000);
            Assert.AreEqual("input_required", (string)first["result"]["resultType"]);
            var inputRequest = first["result"]["inputRequests"]["approve"];
            Assert.AreEqual("elicitation/create", (string)inputRequest["method"]);
            StringAssert.Contains("Delete asset", (string)inputRequest["params"]["message"]);
            var state = (string)first["result"]["requestState"];
            Assert.IsNotEmpty(state);

            var retryParams = new JObject
            {
                ["name"] = "needs_confirm",
                ["arguments"] = new JObject(),
                ["inputResponses"] = new JObject
                {
                    ["approve"] = new JObject { ["action"] = "accept", ["content"] = new JObject { ["approve"] = true } }
                },
                ["requestState"] = state,
                ["_meta"] = Meta(elicitation: true)
            };
            var retryId = _relay.Request("tools/call", retryParams);
            var retry = await _relay.ResponseAsync(retryId, 20000);
            Assert.AreEqual("complete", (string)retry["result"]["resultType"]);
            Assert.AreEqual("approved", Text(retry));

            // requestState 只能用一次
            var replay = await _relay.ResponseAsync(_relay.Request("tools/call", (JObject)retryParams.DeepClone()));
            Assert.AreEqual(-32602, (int)replay["error"]["code"]);
        }

        [Test]
        public async Task Modern_TamperedRequestState_IsRejected()
        {
            var first = await _relay.ResponseAsync(_relay.Request("tools/call", new JObject
            {
                ["name"] = "needs_confirm",
                ["arguments"] = new JObject(),
                ["_meta"] = Meta(elicitation: true)
            }), 20000);
            var state = (string)first["result"]["requestState"];
            var tampered = "x" + state;

            var retry = await _relay.ResponseAsync(_relay.Request("tools/call", new JObject
            {
                ["name"] = "needs_confirm",
                ["arguments"] = new JObject(),
                ["inputResponses"] = new JObject { ["approve"] = new JObject { ["action"] = "accept", ["content"] = new JObject { ["approve"] = true } } },
                ["requestState"] = tampered,
                ["_meta"] = Meta(elicitation: true)
            }));
            Assert.AreEqual(-32602, (int)retry["error"]["code"]);
        }

        [Test]
        public async Task Modern_WithoutElicitation_FallsBackToUnity()
        {
            var response = await _relay.ResponseAsync(_relay.Request("tools/call", new JObject
            {
                ["name"] = "needs_confirm",
                ["arguments"] = new JObject(),
                ["_meta"] = Meta(elicitation: false)
            }), 20000);
            Assert.AreEqual("fallback", Text(response));
        }

        [Test]
        public async Task Provider_TrustClient_ApprovesAndActsAsYolo()
        {
            McpSettings.OverrideForTests(McpReviewLevel.TrustClient, McpConfirmLocation.UnityWindow);
            var provider = new McpConfirmationProvider("t");
            Assert.IsTrue(provider.IsYoloActive);
            var request = new ToolConfirmationRequest("manage_asset", "delete", default, "Delete", "d", new[] { "r" },
                new System.Collections.Generic.Dictionary<string, string>(), new[] { "Assets/x" });
            Assert.IsTrue(await provider.RequestConfirmationAsync(request, System.Threading.CancellationToken.None));
        }

        [Test]
        public void DescribeForClient_ListsToolActionTargets()
        {
            var request = new ToolConfirmationRequest("manage_asset", "delete", default, "Delete", "Removes the asset",
                new[] { "destructive" }, new System.Collections.Generic.Dictionary<string, string> { ["path"] = "Assets/x" }, new[] { "Assets/x" });
            var text = McpBridgeService.DescribeForClient(request);
            StringAssert.Contains("manage_asset / delete", text);
            StringAssert.Contains("Why: destructive", text);
            StringAssert.Contains("Targets: Assets/x", text);
            StringAssert.Contains("path=Assets/x", text);
        }
    }
}
#endif
