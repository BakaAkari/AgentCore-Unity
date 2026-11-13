#if AGENTCORE_MCP
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
    /// Domain Reload 与中断恢复：<see cref="McpCallLedger"/> 的交付 / 持久化 / resume，
    /// 以及 relay 在“编辑器重载”（bridge 断开后在同一端口重新监听）期间挂起、排队并在恢复后完成调用。
    /// </summary>
    [TestFixture]
    public class McpReloadTests
    {
        private const string Token = "relay-test-token";

        private sealed class Sink
        {
            public readonly List<JObject> Sent = new List<JObject>();
            public bool Fail;

            public McpBridgeConnection Connection(string relayId) => new McpBridgeConnection(relayId, line =>
            {
                if (Fail)
                    return false;
                Sent.Add(JObject.Parse(line));
                return true;
            });
        }

        private static JObject Result(string id, string text) => new JObject
        {
            ["type"] = McpBridgeProtocol.Result,
            ["id"] = id,
            ["status"] = McpBridgeProtocol.StatusOk,
            ["text"] = text,
            ["images"] = new JArray()
        };

        [Test]
        public void Ledger_Complete_SendsToCurrentConnection()
        {
            var ledger = new McpCallLedger();
            var sink = new Sink();
            ledger.Attach(sink.Connection("r1"));
            ledger.Begin("r1", "1", "echo");

            ledger.Complete("r1", "1", Result("1", "done"));

            Assert.AreEqual("done", (string)sink.Sent.Single()["text"]);
            Assert.AreEqual(0, ledger.PendingCount);
        }

        [Test]
        public void Ledger_ResultWhileDisconnected_IsDeliveredOnResume()
        {
            var ledger = new McpCallLedger();
            var old = new Sink { Fail = true };
            ledger.Attach(old.Connection("r1"));
            ledger.Begin("r1", "1", "echo");
            ledger.Complete("r1", "1", Result("1", "done"));
            Assert.AreEqual(1, ledger.PendingCount, "kept until the relay comes back");

            var fresh = new Sink();
            ledger.Resume(fresh.Connection("r1"), new[] { "1" });
            Assert.AreEqual("done", (string)fresh.Sent.Single()["text"]);
            Assert.AreEqual(0, ledger.PendingCount);
        }

        [Test]
        public void Ledger_SerializeRestore_ConvertsInterruptedAndAwaitingCompile()
        {
            var before = new McpCallLedger();
            before.Begin("r1", "run", "manage_scene");
            before.Begin("r1", "compile", "manage_script");
            before.AwaitCompile("r1", "compile", Result("compile", "script written"));
            before.Begin("r2", "other", "echo");
            before.Complete("r2", "other", Result("other", "for r2"));

            var after = new McpCallLedger();
            after.Restore(before.Serialize());

            var sink = new Sink();
            after.Resume(sink.Connection("r1"), new[] { "run", "compile", "unknown" });
            var byId = sink.Sent.Where(m => m["id"] != null).ToDictionary(m => (string)m["id"]);

            Assert.AreEqual(McpBridgeProtocol.StatusError, (string)byId["run"]["status"]);
            StringAssert.Contains("interrupted", (string)byId["run"]["text"]);
            Assert.AreEqual(McpBridgeProtocol.StatusOk, (string)byId["compile"]["status"]);
            StringAssert.Contains("script written", (string)byId["compile"]["text"]);
            StringAssert.Contains("compiled the scripts and reloaded", (string)byId["compile"]["text"]);
            var unknown = sink.Sent.Single(m => (string)m["type"] == McpBridgeProtocol.UnknownCalls);
            CollectionAssert.AreEqual(new[] { "unknown" }, unknown["ids"].Select(t => (string)t).ToArray());
            Assert.AreEqual(1, after.PendingCount, "other relay's result is kept for it");
        }

        [Test]
        public void Ledger_Cancelled_IsNotAnswered()
        {
            var ledger = new McpCallLedger();
            var sink = new Sink();
            ledger.Attach(sink.Connection("r1"));
            var ct = ledger.Begin("r1", "1", "echo");
            ledger.Cancel("r1", "1");
            Assert.IsTrue(ct.IsCancellationRequested);
            ledger.Complete("r1", "1", Result("1", "late"));
            Assert.IsEmpty(sink.Sent);
        }

        /// <summary>重载后的 bridge：用 ledger 的 resume 交还重载前挂起的调用，新调用照常答复。</summary>
        private sealed class ReloadedBridge : IMcpBridgeHandler
        {
            public readonly McpCallLedger Ledger = new McpCallLedger();

            public JObject OnHello(McpBridgeConnection connection)
            {
                Ledger.Attach(connection);
                return new JObject { ["schemaHash"] = "hash-2" };
            }

            public void OnMessage(McpBridgeConnection connection, JObject message)
            {
                var id = (string)message["id"];
                switch ((string)message["type"])
                {
                    case McpBridgeProtocol.Resume:
                        Ledger.Resume(connection, ((JArray)message["ids"]).Select(t => (string)t));
                        break;
                    case McpBridgeProtocol.CallTool:
                        Ledger.Begin(connection.RelayId, id, (string)message["name"]);
                        Ledger.Complete(connection.RelayId, id, Result(id, "after reload"));
                        break;
                    case McpBridgeProtocol.ListTools:
                        connection.Send(new JObject { ["type"] = McpBridgeProtocol.Tools, ["id"] = id, ["schemaHash"] = "hash-2", ["tools"] = new JArray() });
                        break;
                }
            }

            public void OnDisconnected(McpBridgeConnection connection) => Ledger.Detach(connection);
        }

        private string _project;

        [SetUp]
        public void SetUp()
        {
            _project = Path.Combine(Path.GetTempPath(), "agentcore-mcp-reload-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(_project, "Library", "AgentCore", "Mcp"));
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_project, true); } catch { /* ignore */ }
        }

        [Test]
        public async Task Relay_HoldsCallsAcrossReload_AndCompletesThemAfterwards()
        {
            var build = McpRelayTests.SharedBuild;
            Assert.IsTrue(build.Success, build.Error);

            var first = new McpRelayTests.FakeBridge { HoldCalls = true };
            var server = new McpBridgeServer(Token, first);
            server.Start(0);
            var port = server.Port;
            McpRelayTests.WriteEndpoint(_project, port);

            using (var relay = new McpRelayTests.RelayProcess(build, _project))
            {
                await relay.InitializeAsync();
                var listId = relay.Request("tools/list");
                await relay.ResponseAsync(listId); // 记下 hash-1

                // 调用已发出、bridge 正在执行时编辑器开始重载
                var runningId = relay.Request("tools/call", new JObject { ["name"] = "echo", ["arguments"] = new JObject() });
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (first.Held.Count == 0 && DateTime.UtcNow < deadline)
                    await Task.Delay(20);
                Assert.AreEqual(1, first.Held.Count, "call reached the bridge");
                var heldId = first.Held[0].id;

                McpRelayTests.WriteEndpoint(_project, port, state: "reloading");
                server.Broadcast(new JObject { ["type"] = McpBridgeProtocol.Reloading });
                server.Dispose();

                // 重载期间的新调用排队
                var queuedId = relay.Request("tools/call", new JObject { ["name"] = "echo", ["arguments"] = new JObject() });
                await Task.Delay(800);

                // “重载”完成：重载前挂起的调用作为待取结果放进新 ledger
                var reloaded = new ReloadedBridge();
                var before = new McpCallLedger();
                before.Begin(first.Held[0].connection.RelayId, heldId, "echo");
                before.AwaitCompile(first.Held[0].connection.RelayId, heldId, Result(heldId, "written before reload"));
                reloaded.Ledger.Restore(before.Serialize());

                using (var server2 = new McpBridgeServer(Token, reloaded))
                {
                    // 优先原端口；被 TIME_WAIT 占住时换端口，relay 每次重连都重读发现文件
                    server2.Start(port);
                    McpRelayTests.WriteEndpoint(_project, server2.Port);

                    var running = await relay.ResponseAsync(runningId, 20000);
                    Assert.IsFalse((bool)running["result"]["isError"]);
                    StringAssert.Contains("written before reload", (string)running["result"]["content"][0]["text"]);

                    var queued = await relay.ResponseAsync(queuedId, 20000);
                    StringAssert.Contains("after reload", (string)queued["result"]["content"][0]["text"]);

                    // 工具集合变了（hash-1 → hash-2）：客户端收到 list_changed
                    await relay.WaitForAsync(m => (string)m["method"] == "notifications/tools/list_changed", 5000);
                }
            }
        }

        [Test]
        public async Task Relay_ReloadNeverFinishes_TimesOutWithExplanation()
        {
            var build = McpRelayTests.SharedBuild;
            Assert.IsTrue(build.Success, build.Error);

            // 编辑器“在运行”（PID 有效）但一直处于 reloading
            McpRelayTests.WriteEndpoint(_project, 1, state: "reloading");
            var env = new Dictionary<string, string> { ["AGENTCORE_RELAY_RELOAD_TIMEOUT_MS"] = "2000" };
            using (var relay = new McpRelayTests.RelayProcess(build, _project, env))
            {
                await relay.InitializeAsync();
                var callId = relay.Request("tools/call", new JObject
                {
                    ["name"] = "echo",
                    ["arguments"] = new JObject(),
                    ["_meta"] = new JObject { ["progressToken"] = 7 }
                });

                var progress = await relay.WaitForAsync(m => (string)m["method"] == "notifications/progress", 10000);
                StringAssert.Contains("Waiting for the Unity editor", (string)progress["params"]["message"]);

                var call = await relay.ResponseAsync(callId, 15000);
                Assert.IsTrue((bool)call["result"]["isError"]);
                StringAssert.Contains("Timed out", (string)call["result"]["content"][0]["text"]);
                StringAssert.Contains("was not executed", (string)call["result"]["content"][0]["text"]);
            }
        }
    }
}
#endif
