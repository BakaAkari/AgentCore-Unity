using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AgentCore.Editor.Components.MCP.Bridge;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.MCP
{
    /// <summary>
    /// relay 的 2026-07-28 新协议（无会话、per-request _meta、server/discover、subscriptions/listen）
    /// 与旧协议的资源订阅；后端为测试 bridge。
    /// </summary>
    [TestFixture]
    public class McpModernProtocolTests
    {
        private const string Token = "relay-test-token";
        private string _project;

        [SetUp]
        public void SetUp()
        {
            _project = Path.Combine(Path.GetTempPath(), "agentcore-mcp-modern-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(_project, "Library", "AgentCore", "Mcp"));
            Assert.IsTrue(McpRelayTests.SharedBuild.Success, McpRelayTests.SharedBuild.Error);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_project, true); } catch { /* ignore */ }
        }

        private static JObject Meta(string version = "2026-07-28", bool capabilities = true)
        {
            var meta = new JObject
            {
                ["io.modelcontextprotocol/protocolVersion"] = version,
                ["io.modelcontextprotocol/clientInfo"] = new JObject { ["name"] = "modern-test", ["version"] = "1" }
            };
            if (capabilities)
                meta["io.modelcontextprotocol/clientCapabilities"] = new JObject();
            return meta;
        }

        private static JObject Params(JObject extra = null, JObject meta = null)
        {
            var p = extra ?? new JObject();
            p["_meta"] = meta ?? Meta();
            return p;
        }

        private sealed class Harness : IDisposable
        {
            public readonly McpBridgeServer Server;
            public readonly McpRelayTests.RelayProcess Relay;

            public Harness(string project)
            {
                Server = new McpBridgeServer(Token, new McpRelayTests.FakeBridge());
                Server.Start(0);
                McpRelayTests.WriteEndpoint(project, Server.Port);
                Relay = new McpRelayTests.RelayProcess(McpRelayTests.SharedBuild, project);
            }

            public void Dispose()
            {
                Relay.Dispose();
                Server.Dispose();
            }
        }

        [Test]
        public async Task Modern_ToolsList_WithoutInitialize_HasResultTypeAndCacheHints()
        {
            using (var h = new Harness(_project))
            {
                var response = await h.Relay.ResponseAsync(h.Relay.Request("tools/list", Params()), 20000);
                var result = (JObject)response["result"];
                Assert.AreEqual("complete", (string)result["resultType"]);
                Assert.AreEqual("agentcore-unity", (string)result["_meta"]["io.modelcontextprotocol/serverInfo"]["name"]);
                Assert.AreEqual("private", (string)result["cacheScope"]);
                Assert.Greater((int)result["ttlMs"], 0);
                Assert.AreEqual("echo", (string)result["tools"][0]["name"]);
                Assert.AreEqual("modern-test", h.Server.Connections.Single().ClientName, "clientInfo from _meta");
            }
        }

        [Test]
        public async Task Modern_ToolsCall_ReturnsComplete()
        {
            using (var h = new Harness(_project))
            {
                var response = await h.Relay.ResponseAsync(h.Relay.Request("tools/call",
                    Params(new JObject { ["name"] = "echo", ["arguments"] = new JObject { ["a"] = 1 } })), 20000);
                Assert.AreEqual("complete", (string)response["result"]["resultType"]);
                Assert.IsFalse((bool)response["result"]["isError"]);
            }
        }

        [Test]
        public async Task Modern_UnsupportedVersion_IsRejectedWithSupportedList()
        {
            using (var h = new Harness(_project))
            {
                var response = await h.Relay.ResponseAsync(h.Relay.Request("tools/list", Params(meta: Meta("1900-01-01"))));
                Assert.AreEqual(-32022, (int)response["error"]["code"]);
                var supported = response["error"]["data"]["supported"].Select(t => (string)t).ToList();
                CollectionAssert.Contains(supported, "2026-07-28");
                CollectionAssert.Contains(supported, "2025-11-25");
                Assert.AreEqual("1900-01-01", (string)response["error"]["data"]["requested"]);
            }
        }

        [Test]
        public async Task Modern_MissingClientCapabilities_IsInvalidParams()
        {
            using (var h = new Harness(_project))
            {
                var response = await h.Relay.ResponseAsync(h.Relay.Request("tools/list", Params(meta: Meta(capabilities: false))));
                Assert.AreEqual(-32602, (int)response["error"]["code"]);
            }
        }

        [Test]
        public async Task Discover_ListsBothEras()
        {
            using (var h = new Harness(_project))
            {
                var response = await h.Relay.ResponseAsync(h.Relay.Request("server/discover", Params()));
                var result = response["result"];
                var versions = result["supportedVersions"].Select(t => (string)t).ToList();
                CollectionAssert.Contains(versions, "2026-07-28");
                CollectionAssert.Contains(versions, "2025-06-18");
                Assert.IsNotNull(result["capabilities"]["tools"]);
                Assert.IsTrue((bool)result["capabilities"]["resources"]["subscribe"]);
                Assert.AreEqual("complete", (string)result["resultType"]);
            }
        }

        [Test]
        public async Task Modern_Listen_AcknowledgesAndDeliversResourceAndToolNotifications()
        {
            using (var h = new Harness(_project))
            {
                // 先发一个请求让 relay 连上 bridge
                await h.Relay.ResponseAsync(h.Relay.Request("tools/list", Params()), 20000);

                var listenId = h.Relay.Request("subscriptions/listen", Params(new JObject
                {
                    ["notifications"] = new JObject
                    {
                        ["toolsListChanged"] = true,
                        ["promptsListChanged"] = true,
                        ["resourceSubscriptions"] = new JArray("unity://console", "unity://asset/Assets/a.txt")
                    }
                }));
                var ack = await h.Relay.WaitForAsync(m => (string)m["method"] == "notifications/subscriptions/acknowledged");
                Assert.AreEqual(listenId, (int)ack["params"]["_meta"]["io.modelcontextprotocol/subscriptionId"]);
                Assert.IsTrue((bool)ack["params"]["notifications"]["toolsListChanged"]);
                Assert.IsNull(ack["params"]["notifications"]["promptsListChanged"], "unsupported types are omitted");

                h.Server.Broadcast(new JObject { ["type"] = "event", ["kind"] = "resourceUpdated", ["uri"] = "unity://console" });
                var updated = await h.Relay.WaitForAsync(m => (string)m["method"] == "notifications/resources/updated");
                Assert.AreEqual("unity://console", (string)updated["params"]["uri"]);
                Assert.AreEqual(listenId, (int)updated["params"]["_meta"]["io.modelcontextprotocol/subscriptionId"]);

                h.Server.Broadcast(new JObject { ["type"] = "event", ["kind"] = "resourceUpdated", ["uri"] = "unity://asset/*" });
                var asset = await h.Relay.WaitForAsync(m => (string)m["method"] == "notifications/resources/updated");
                Assert.AreEqual("unity://asset/Assets/a.txt", (string)asset["params"]["uri"]);

                // 取消后不再收到
                h.Relay.Notify("notifications/cancelled", new JObject { ["requestId"] = listenId });
                await Task.Delay(300);
                h.Server.Broadcast(new JObject { ["type"] = "event", ["kind"] = "resourceUpdated", ["uri"] = "unity://console" });
                await Task.Delay(800);
                var late = await NextOrNull(h.Relay, m => (string)m["method"] == "notifications/resources/updated", 500);
                Assert.IsNull(late);
            }
        }

        [Test]
        public async Task Legacy_ResourcesSubscribe_DeliversUpdatesUntilUnsubscribed()
        {
            using (var h = new Harness(_project))
            {
                await h.Relay.InitializeAsync("2025-11-25");
                await h.Relay.ResponseAsync(h.Relay.Request("tools/list"), 20000);
                var sub = await h.Relay.ResponseAsync(h.Relay.Request("resources/subscribe", new JObject { ["uri"] = "unity://scene/hierarchy" }));
                Assert.IsNotNull(sub["result"]);
                Assert.IsNull(sub["result"]["resultType"], "legacy results stay untouched");

                h.Server.Broadcast(new JObject { ["type"] = "event", ["kind"] = "resourceUpdated", ["uri"] = "unity://scene/hierarchy" });
                var updated = await h.Relay.WaitForAsync(m => (string)m["method"] == "notifications/resources/updated");
                Assert.AreEqual("unity://scene/hierarchy", (string)updated["params"]["uri"]);
                Assert.IsNull(updated["params"]["_meta"], "legacy notifications carry no subscriptionId");

                await h.Relay.ResponseAsync(h.Relay.Request("resources/unsubscribe", new JObject { ["uri"] = "unity://scene/hierarchy" }));
                h.Server.Broadcast(new JObject { ["type"] = "event", ["kind"] = "resourceUpdated", ["uri"] = "unity://scene/hierarchy" });
                Assert.IsNull(await NextOrNull(h.Relay, m => (string)m["method"] == "notifications/resources/updated", 1000));
            }
        }

        private static async Task<JObject> NextOrNull(McpRelayTests.RelayProcess relay, Func<JObject, bool> predicate, int timeoutMs)
        {
            try
            {
                return await relay.TryWaitForAsync(predicate, timeoutMs);
            }
            catch (Exception)
            {
                return null;
            }
        }

        [Test]
        public void ResourceEvents_AreThrottledPerUri()
        {
            var sent = new List<string>();
            McpResourceEvents.ResetForTests();
            McpResourceEvents.Start(m => { lock (sent) sent.Add((string)m["uri"]); });
            try
            {
                McpResourceEvents.MarkDirty("unity://console");
                McpResourceEvents.MarkDirty("unity://console");
                McpResourceEvents.Flush();
                McpResourceEvents.MarkDirty("unity://console");
                McpResourceEvents.Flush(); // 节流期内：不发
                lock (sent)
                    Assert.AreEqual(1, sent.Count(u => u == "unity://console"));
            }
            finally
            {
                McpResourceEvents.Stop();
            }
        }
    }
}
