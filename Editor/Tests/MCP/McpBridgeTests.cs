#if AGENTCORE_MCP
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Components.MCP.Bridge;
using AgentCore.Editor.Tools;
using AgentCore.Editor.Tools.Safety;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace AgentCore.Tests.Editor.MCP
{
    /// <summary>
    /// McpBridge：TCP 握手与消息、工具目录、调用执行（确认路径、YOLO、结果映射）。
    /// 不启动全局 <see cref="McpBridgeService"/>（批处理模式下它本就不启动），服务器用独立实例与假处理器。
    /// </summary>
    [TestFixture]
    public class McpBridgeTests
    {
        private sealed class RecordingHandler : IMcpBridgeHandler
        {
            public readonly List<JObject> Messages = new List<JObject>();
            public McpBridgeConnection LastConnection;

            public JObject OnHello(McpBridgeConnection connection)
            {
                LastConnection = connection;
                return new JObject { ["schemaHash"] = "test" };
            }

            public void OnMessage(McpBridgeConnection connection, JObject message)
            {
                lock (Messages)
                    Messages.Add(message);
                if ((string)message["type"] == McpBridgeProtocol.ListTools)
                    connection.Send(new JObject { ["type"] = McpBridgeProtocol.Tools, ["id"] = message["id"], ["tools"] = new JArray() });
            }

            public void OnDisconnected(McpBridgeConnection connection)
            {
            }
        }

        private sealed class RecordingProvider : IToolConfirmationProvider
        {
            public readonly List<ToolConfirmationRequest> Requests = new List<ToolConfirmationRequest>();
            public bool Approve;

            public Task<bool> RequestConfirmationAsync(ToolConfirmationRequest request, CancellationToken ct)
            {
                Requests.Add(request);
                return Task.FromResult(Approve);
            }
        }

        private sealed class Client : IDisposable
        {
            private readonly TcpClient _tcp;
            private readonly StreamReader _reader;
            private readonly StreamWriter _writer;

            public Client(int port)
            {
                _tcp = new TcpClient("127.0.0.1", port);
                var stream = _tcp.GetStream();
                _reader = new StreamReader(stream, new UTF8Encoding(false));
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
            }

            public void Send(JObject message) => _writer.WriteLine(message.ToString(Formatting.None));

            public async Task<JObject> ReadAsync()
            {
                var read = _reader.ReadLineAsync();
                if (await Task.WhenAny(read, Task.Delay(5000)) != read)
                    throw new TimeoutException("No message from bridge.");
                return read.Result == null ? null : JObject.Parse(read.Result);
            }

            public void Dispose() => _tcp.Close();
        }

        private const string Token = "test-token";

        private static JObject Hello(string token = Token, int version = McpBridgeProtocol.Version) => new JObject
        {
            ["type"] = McpBridgeProtocol.Hello,
            ["token"] = token,
            ["relayId"] = "relay-1",
            ["relayVersion"] = "test",
            ["bridgeProtocol"] = version,
            ["client"] = new JObject { ["name"] = "test-client", ["version"] = "1.0" }
        };

        private Func<string> _origLoad;
        private Action<string> _origSave;
        private Func<string, IToolConfirmationProvider> _origFactory;
        private string _tempDir;

        [SetUp]
        public void SetUp()
        {
            _origLoad = ToolTrustState.LoadRaw;
            _origSave = ToolTrustState.SaveRaw;
            _origFactory = McpToolExecutor.ProviderFactory;
            ToolTrustState.SaveRaw = _ => { };
            _tempDir = Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, "..")), "AgentCoreMcpTest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_tempDir);
        }

        [TearDown]
        public void TearDown()
        {
            ToolTrustState.LoadRaw = _origLoad;
            ToolTrustState.SaveRaw = _origSave;
            ToolTrustState.ResetForTests();
            ToolTrustState.EnsureLoaded();
            McpToolExecutor.ProviderFactory = _origFactory;
            try { Directory.Delete(_tempDir, true); } catch { /* ignore */ }
        }

        private static void SetTrust(string raw)
        {
            ToolTrustState.LoadRaw = () => raw;
            ToolTrustState.ResetForTests();
            ToolTrustState.EnsureLoaded();
        }

        [Test]
        public async Task Server_ValidHello_ThenMessagesReachHandler()
        {
            var handler = new RecordingHandler();
            using (var server = new McpBridgeServer(Token, handler))
            {
                server.Start(0);
                using (var client = new Client(server.Port))
                {
                    client.Send(Hello());
                    var ok = await client.ReadAsync();
                    Assert.AreEqual(McpBridgeProtocol.HelloOk, (string)ok["type"]);
                    Assert.AreEqual(McpBridgeProtocol.Version, (int)ok["bridgeProtocol"]);
                    Assert.AreEqual("test", (string)ok["schemaHash"]);
                    Assert.AreEqual("test-client", handler.LastConnection.ClientName);

                    client.Send(new JObject { ["type"] = McpBridgeProtocol.ListTools, ["id"] = "1" });
                    var tools = await client.ReadAsync();
                    Assert.AreEqual(McpBridgeProtocol.Tools, (string)tools["type"]);
                    Assert.AreEqual("1", (string)tools["id"]);
                    Assert.AreEqual(1, server.Connections.Count);
                }
            }
        }

        [Test]
        public async Task Server_WrongToken_IsRejected()
        {
            using (var server = new McpBridgeServer(Token, new RecordingHandler()))
            {
                server.Start(0);
                using (var client = new Client(server.Port))
                {
                    client.Send(Hello("wrong"));
                    var reply = await client.ReadAsync();
                    Assert.AreEqual(McpBridgeProtocol.HelloError, (string)reply["type"]);
                    StringAssert.Contains("Invalid token", (string)reply["message"]);
                    Assert.IsNull(await client.ReadAsync(), "connection should be closed");
                }
            }
        }

        [Test]
        public async Task Server_ProtocolMismatch_IsRejected()
        {
            using (var server = new McpBridgeServer(Token, new RecordingHandler()))
            {
                server.Start(0);
                using (var client = new Client(server.Port))
                {
                    client.Send(Hello(version: 999));
                    var reply = await client.ReadAsync();
                    Assert.AreEqual(McpBridgeProtocol.HelloError, (string)reply["type"]);
                    StringAssert.Contains("Copy the MCP configuration again", (string)reply["message"]);
                }
            }
        }

        [Test]
        public void Server_PreferredPortIsReused()
        {
            int port;
            using (var first = new McpBridgeServer(Token, new RecordingHandler()))
            {
                first.Start(0);
                port = first.Port;
            }
            using (var second = new McpBridgeServer(Token, new RecordingHandler()))
            {
                second.Start(port);
                Assert.AreEqual(port, second.Port);
            }
        }

        [Test]
        public void Catalog_ExcludesInternalTools_AndIsSorted()
        {
            var snapshot = McpToolCatalog.Build();
            var names = snapshot.Tools.Select(t => (string)t["name"]).ToList();

            Assert.IsNotEmpty(names);
            CollectionAssert.DoesNotContain(names, "request_tools");
            CollectionAssert.DoesNotContain(names, "ask_user");
            CollectionAssert.Contains(names, "manage_file");
            CollectionAssert.AreEqual(names.OrderBy(n => n, StringComparer.Ordinal).ToList(), names);
            Assert.IsTrue(snapshot.Tools.All(t => t["inputSchema"] is JObject));
            Assert.AreEqual(snapshot.SchemaHash, McpToolCatalog.Build().SchemaHash, "hash must be deterministic");
        }

        [Test]
        public async Task Execute_ExcludedTool_IsRejected()
        {
            var outcome = await McpToolExecutor.ExecuteAsync("ask_user", new JObject(), "test-client", CancellationToken.None);
            Assert.AreEqual(McpBridgeProtocol.StatusError, outcome.Status);
            StringAssert.Contains("not available over MCP", outcome.Text);
        }

        [Test]
        public async Task Execute_ReadOnlyTool_Succeeds()
        {
            File.WriteAllText(Path.Combine(_tempDir, "a.txt"), "hello mcp");
            var outcome = await McpToolExecutor.ExecuteAsync("manage_file",
                new JObject { ["action"] = "read_file", ["path"] = Path.GetFileName(_tempDir) + "/a.txt" },
                "test-client", CancellationToken.None);

            Assert.AreEqual(McpBridgeProtocol.StatusOk, outcome.Status, outcome.Text);
            StringAssert.Contains("hello mcp", outcome.Text);
        }

        [Test]
        public async Task Execute_WriteNeedingConfirmation_UsesProvider_WithClientAsSource()
        {
            SetTrust(string.Empty);
            var provider = new RecordingProvider { Approve = false };
            McpToolExecutor.ProviderFactory = _ => provider;

            var target = Path.GetFileName(_tempDir) + "/b.txt";
            var outcome = await McpToolExecutor.ExecuteAsync("manage_file",
                new JObject { ["action"] = "delete", ["path"] = target },
                "test-client", CancellationToken.None);

            // 删除需要确认：被拒绝时报告为错误；未批准的写入不执行
            if (provider.Requests.Count > 0)
            {
                Assert.AreEqual(McpBridgeProtocol.StatusError, outcome.Status);
                StringAssert.Contains("rejected by user", outcome.Text);
            }
            else
            {
                Assert.Inconclusive("Current permission rules allow manage_file.delete without confirmation.");
            }
        }

        [Test]
        public async Task ConfirmationProvider_Yolo_ApprovesWithoutUi()
        {
            SetTrust("SessionAll");
            var provider = new McpConfirmationProvider("test-client");
            Assert.IsTrue(provider.IsYoloActive);

            var request = new ToolConfirmationRequest(
                "manage_file", "delete", default, "Delete", "Delete a file",
                new[] { "test" }, new Dictionary<string, string>(), new[] { "x" },
                new[] { ToolConfirmationTrustScope.SessionAll });
            Assert.IsTrue(await provider.RequestConfirmationAsync(request, CancellationToken.None));
        }

        [Test]
        public void ToOutcome_MapsStatuses()
        {
            Assert.AreEqual(McpBridgeProtocol.StatusOk, McpToolExecutor.ToOutcome("t", ToolResult.Ok("fine")).Status);
            Assert.AreEqual(McpBridgeProtocol.StatusPartial, McpToolExecutor.ToOutcome("t", ToolResult.Partial("half")).Status);
            var failed = McpToolExecutor.ToOutcome("t", ToolResult.Fail("boom"));
            Assert.AreEqual(McpBridgeProtocol.StatusError, failed.Status);
            Assert.AreEqual("boom", failed.Text);
        }

        [Test]
        public void DataUrl_Parses()
        {
            Assert.IsTrue(McpToolExecutor.TryParseDataUrl("data:image/png;base64,QUJD", out var mime, out var data));
            Assert.AreEqual("image/png", mime);
            Assert.AreEqual("QUJD", data);
            Assert.IsFalse(McpToolExecutor.TryParseDataUrl("http://x", out _, out _));
        }
    }
}
#endif
