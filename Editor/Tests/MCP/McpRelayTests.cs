#if AGENTCORE_MCP
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AgentCore.Editor.Components.MCP.Bridge;
using AgentCore.Editor.Components.MCP.Relay;
using AgentCore.Editor.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.MCP
{
    /// <summary>
    /// relay 端到端：用 Unity 自带工具链编译 relay，以自带 Mono 启动，经 stdio 走 MCP 协议，
    /// 后端是测试里的 <see cref="McpBridgeServer"/> + 假处理器（临时“工程”目录中写入发现文件）。
    /// </summary>
    [TestFixture]
    public class McpRelayTests
    {
        private const string Token = "relay-test-token";

        internal static McpRelayBuilder.BuildResult SharedBuild => _build ?? (_build = McpRelayBuilder.Build(McpRelayBuilder.ResolveToolchain()));

        private static McpRelayBuilder.BuildResult _build;
        private string _project;

        /// <summary>假 bridge：一个 echo 工具；记录收到的消息；可选择不回复调用（模拟执行中）。</summary>
        public sealed class FakeBridge : IMcpBridgeHandler
        {
            public readonly List<JObject> Messages = new List<JObject>();
            public bool HoldCalls;
            public string SchemaHash = "hash-1";
            public readonly List<(McpBridgeConnection connection, string id)> Held = new List<(McpBridgeConnection, string)>();

            public JObject OnHello(McpBridgeConnection connection) => new JObject
            {
                ["projectPath"] = "fake",
                ["unityVersion"] = "test",
                ["schemaHash"] = SchemaHash
            };

            public void OnMessage(McpBridgeConnection connection, JObject message)
            {
                lock (Messages)
                    Messages.Add(message);
                var id = (string)message["id"];
                switch ((string)message["type"])
                {
                    case McpBridgeProtocol.ListTools:
                        connection.Send(new JObject
                        {
                            ["type"] = McpBridgeProtocol.Tools,
                            ["id"] = id,
                            ["schemaHash"] = SchemaHash,
                            ["tools"] = new JArray
                            {
                                new JObject
                                {
                                    ["name"] = "echo",
                                    ["description"] = "Echo",
                                    ["inputSchema"] = new JObject { ["type"] = "object", ["properties"] = new JObject() }
                                }
                            }
                        });
                        break;
                    case McpBridgeProtocol.Request:
                        connection.Send(AnswerRequest(id, (string)message["method"], message["params"] as JObject));
                        break;
                    case McpBridgeProtocol.ConfirmResult:
                        TaskCompletionSource<JToken> waiter;
                        lock (_confirms)
                        {
                            _confirms.TryGetValue((string)message["confirmId"], out waiter);
                            _confirms.Remove((string)message["confirmId"]);
                        }
                        waiter?.TrySetResult(message["approved"]);
                        break;
                    case McpBridgeProtocol.CallTool:
                        if ((string)message["name"] == "needs_confirm")
                        {
                            _ = ConfirmThenAnswer(connection, id);
                            break;
                        }
                        if (HoldCalls)
                        {
                            lock (Held)
                                Held.Add((connection, id));
                            break;
                        }
                        connection.Send(new JObject { ["type"] = McpBridgeProtocol.Progress, ["id"] = id, ["message"] = "working" });
                        connection.Send(new JObject
                        {
                            ["type"] = McpBridgeProtocol.Result,
                            ["id"] = id,
                            ["status"] = (string)message["name"] == "fail" ? "error" : "ok",
                            ["text"] = "echo:" + ((JObject)message["arguments"]).ToString(Formatting.None),
                            ["images"] = new JArray()
                        });
                        break;
                }
            }

            public void OnDisconnected(McpBridgeConnection connection)
            {
            }

            private readonly Dictionary<string, TaskCompletionSource<JToken>> _confirms = new Dictionary<string, TaskCompletionSource<JToken>>();

            /// <summary>模拟需要确认的工具：请客户端确认，按答复给出 approved / denied / fallback（客户端不支持）。</summary>
            private async Task ConfirmThenAnswer(McpBridgeConnection connection, string id)
            {
                var confirmId = "c-" + id;
                var tcs = new TaskCompletionSource<JToken>();
                lock (_confirms)
                    _confirms[confirmId] = tcs;
                connection.Send(new JObject
                {
                    ["type"] = McpBridgeProtocol.Confirm,
                    ["id"] = id,
                    ["confirmId"] = confirmId,
                    ["title"] = "Delete asset",
                    ["message"] = "Unity (AgentCore) asks for approval: manage_asset / delete"
                });
                var answer = await tcs.Task.ConfigureAwait(false);
                var text = answer == null || answer.Type == JTokenType.Null ? "fallback" : ((bool)answer ? "approved" : "denied");
                connection.Send(new JObject
                {
                    ["type"] = McpBridgeProtocol.Result,
                    ["id"] = id,
                    ["status"] = "ok",
                    ["text"] = text,
                    ["images"] = new JArray()
                });
            }

            private static JObject AnswerRequest(string id, string method, JObject parameters)
            {
                var response = new JObject { ["type"] = McpBridgeProtocol.Response, ["id"] = id };
                switch (method)
                {
                    case "resources/list":
                        response["result"] = new JObject
                        {
                            ["resources"] = new JArray { new JObject { ["uri"] = "unity://console", ["name"] = "console" } },
                            ["resourceTemplates"] = new JArray { new JObject { ["uriTemplate"] = "unity://asset/{+path}", ["name"] = "asset" } }
                        };
                        break;
                    case "resources/read":
                        if ((string)parameters?["uri"] == "unity://missing")
                            response["error"] = new JObject { ["code"] = -32602, ["message"] = "Resource not found: unity://missing" };
                        else
                            response["result"] = new JObject { ["contents"] = new JArray { new JObject { ["uri"] = parameters?["uri"], ["mimeType"] = "text/plain", ["text"] = "中文 content" } } };
                        break;
                    case "prompts/list":
                        response["result"] = new JObject { ["prompts"] = new JArray { new JObject { ["name"] = "scene-authoring" } } };
                        break;
                    case "prompts/get":
                        response["result"] = new JObject { ["messages"] = new JArray { new JObject { ["role"] = "user", ["content"] = new JObject { ["type"] = "text", ["text"] = "skill:" + (string)parameters?["arguments"]?["task"] } } } };
                        break;
                    default:
                        response["error"] = new JObject { ["code"] = -32601, ["message"] = "Method not found" };
                        break;
                }
                return response;
            }
        }

        /// <summary>以 stdio 驱动的 relay 进程。</summary>
        public sealed class RelayProcess : IDisposable
        {
            private readonly Process _process;
            private readonly Queue<JObject> _messages = new Queue<JObject>();
            private readonly StringBuilder _stderr = new StringBuilder();
            private int _nextId;

            public RelayProcess(McpRelayBuilder.BuildResult build, string project, IDictionary<string, string> environment = null)
            {
                var info = new ProcessStartInfo
                {
                    FileName = build.MonoPath,
                    Arguments = ProcessArgs.Join(new[] { build.RelayPath, "--project", project }),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false),
                };
                if (environment != null)
                {
                    foreach (var pair in environment)
                        info.EnvironmentVariables[pair.Key] = pair.Value;
                }
                _process = Process.Start(info);
                _process.OutputDataReceived += (_, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data))
                        return;
                    lock (_messages)
                        _messages.Enqueue(JObject.Parse(e.Data));
                };
                _process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                        lock (_stderr) _stderr.AppendLine(e.Data);
                };
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
            }

            public string Stderr
            {
                get { lock (_stderr) return _stderr.ToString(); }
            }

            public void Send(JObject message)
            {
                var bytes = new UTF8Encoding(false).GetBytes(message.ToString(Formatting.None) + "\n");
                _process.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                _process.StandardInput.BaseStream.Flush();
            }

            public int Request(string method, JObject parameters = null)
            {
                var id = ++_nextId;
                Send(new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JObject() });
                return id;
            }

            public void Notify(string method, JObject parameters = null)
            {
                var message = new JObject { ["jsonrpc"] = "2.0", ["method"] = method };
                if (parameters != null)
                    message["params"] = parameters;
                Send(message);
            }

            /// <summary>等待满足条件的消息（取走它；其余消息保留，供后续等待匹配）。</summary>
            public async Task<JObject> WaitForAsync(Func<JObject, bool> predicate, int timeoutMs = 15000)
            {
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    lock (_messages)
                    {
                        var match = _messages.FirstOrDefault(predicate);
                        if (match != null)
                        {
                            var rest = _messages.Where(m => !ReferenceEquals(m, match)).ToList();
                            _messages.Clear();
                            foreach (var m in rest)
                                _messages.Enqueue(m);
                            return match;
                        }
                    }
                    await Task.Delay(20);
                }
                Assert.Fail("Timed out waiting for relay output. stderr:\n" + Stderr);
                return null;
            }

            /// <summary>与 <see cref="WaitForAsync"/> 相同，但超时返回 null 而不是让测试失败。</summary>
            public async Task<JObject> TryWaitForAsync(Func<JObject, bool> predicate, int timeoutMs)
            {
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    lock (_messages)
                    {
                        var match = _messages.FirstOrDefault(predicate);
                        if (match != null)
                        {
                            var rest = _messages.Where(m => !ReferenceEquals(m, match)).ToList();
                            _messages.Clear();
                            foreach (var m in rest)
                                _messages.Enqueue(m);
                            return match;
                        }
                    }
                    await Task.Delay(20);
                }
                return null;
            }

            public Task<JObject> ResponseAsync(int id, int timeoutMs = 15000) =>
                WaitForAsync(m => m["id"] != null && m["id"].Type == JTokenType.Integer && (int)m["id"] == id, timeoutMs);

            public async Task InitializeAsync(string version = "2025-06-18")
            {
                var id = Request("initialize", new JObject
                {
                    ["protocolVersion"] = version,
                    ["capabilities"] = new JObject(),
                    ["clientInfo"] = new JObject { ["name"] = "relay-test", ["version"] = "1" }
                });
                var response = await ResponseAsync(id);
                Assert.AreEqual(version, (string)response["result"]["protocolVersion"]);
                Notify("notifications/initialized");
            }

            public void Dispose()
            {
                try
                {
                    _process.StandardInput.Close();
                    if (!_process.WaitForExit(3000))
                        _process.Kill();
                }
                catch (Exception)
                {
                    // ignore
                }
                _process.Dispose();
            }
        }

        [OneTimeSetUp]
        public void BuildRelay()
        {
            _build = McpRelayBuilder.Build(McpRelayBuilder.ResolveToolchain());
            Assert.IsTrue(_build.Success, _build.Error);
        }

        [SetUp]
        public void SetUp()
        {
            _project = Path.Combine(Path.GetTempPath(), "agentcore-mcp-relay-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(_project, "Library", "AgentCore", "Mcp"));
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_project, true); } catch { /* ignore */ }
        }

        public static void WriteEndpoint(string project, int port, string state = "ready", int? pid = null)
        {
            var endpoint = new JObject
            {
                ["port"] = port,
                ["token"] = Token,
                ["pid"] = pid ?? Process.GetCurrentProcess().Id,
                ["projectPath"] = project,
                ["bridgeProtocol"] = McpBridgeProtocol.Version,
                ["state"] = state
            };
            McpPaths.WriteAtomic(Path.Combine(project, "Library", "AgentCore", "Mcp", "endpoint.json"), endpoint.ToString());
        }

        [Test]
        public void Build_IsCachedWhenSourceUnchanged()
        {
            var again = McpRelayBuilder.Build(McpRelayBuilder.ResolveToolchain());
            Assert.IsTrue(again.Success, again.Error);
            Assert.AreEqual(_build.RelayPath, again.RelayPath);
            Assert.IsTrue(File.Exists(again.RelayPath));
        }

        [Test]
        public async Task Relay_InitializeListCall_AgainstBridge()
        {
            var bridge = new FakeBridge();
            using (var server = new McpBridgeServer(Token, bridge))
            {
                server.Start(0);
                WriteEndpoint(_project, server.Port);
                using (var relay = new RelayProcess(_build, _project))
                {
                    await relay.InitializeAsync();

                    var listId = relay.Request("tools/list");
                    var list = await relay.ResponseAsync(listId);
                    var tools = (JArray)list["result"]["tools"];
                    Assert.AreEqual("echo", (string)tools[0]["name"]);
                    Assert.IsTrue(File.Exists(Path.Combine(_project, "Library", "AgentCore", "Mcp", "tools-cache.json")), "tool cache written");

                    var callId = relay.Request("tools/call", new JObject
                    {
                        ["name"] = "echo",
                        ["arguments"] = new JObject { ["x"] = "中文" },
                        ["_meta"] = new JObject { ["progressToken"] = "p1" }
                    });
                    var progress = await relay.WaitForAsync(m => (string)m["method"] == "notifications/progress");
                    Assert.AreEqual("p1", (string)progress["params"]["progressToken"]);
                    var call = await relay.ResponseAsync(callId);
                    Assert.IsFalse((bool)call["result"]["isError"]);
                    StringAssert.Contains("中文", (string)call["result"]["content"][0]["text"]);

                    var failId = relay.Request("tools/call", new JObject { ["name"] = "fail", ["arguments"] = new JObject() });
                    var fail = await relay.ResponseAsync(failId);
                    Assert.IsTrue((bool)fail["result"]["isError"]);

                    var pingId = relay.Request("ping");
                    Assert.IsNotNull((await relay.ResponseAsync(pingId))["result"]);

                    var unknownId = relay.Request("completion/complete");
                    Assert.AreEqual(-32601, (int)(await relay.ResponseAsync(unknownId))["error"]["code"]);

                    var hello = bridge.Messages.Count; // 握手后的消息都经过处理器
                    Assert.Greater(hello, 0);
                    Assert.AreEqual("relay-test", server.Connections.Single().ClientName);
                }
            }
        }

        [Test]
        public async Task Relay_ResourcesAndPrompts_AreForwarded()
        {
            using (var server = new McpBridgeServer(Token, new FakeBridge()))
            {
                server.Start(0);
                WriteEndpoint(_project, server.Port);
                using (var relay = new RelayProcess(_build, _project))
                {
                    var initId = relay.Request("initialize", new JObject
                    {
                        ["protocolVersion"] = "2025-11-25",
                        ["capabilities"] = new JObject(),
                        ["clientInfo"] = new JObject { ["name"] = "t" }
                    });
                    var init = await relay.ResponseAsync(initId);
                    Assert.IsNotNull(init["result"]["capabilities"]["resources"]);
                    Assert.IsNotNull(init["result"]["capabilities"]["prompts"]);
                    relay.Notify("notifications/initialized");

                    var list = await relay.ResponseAsync(relay.Request("resources/list"));
                    Assert.AreEqual("unity://console", (string)list["result"]["resources"][0]["uri"]);
                    var templates = await relay.ResponseAsync(relay.Request("resources/templates/list"));
                    Assert.AreEqual("unity://asset/{+path}", (string)templates["result"]["resourceTemplates"][0]["uriTemplate"]);

                    var read = await relay.ResponseAsync(relay.Request("resources/read", new JObject { ["uri"] = "unity://asset/Assets/a.txt" }));
                    Assert.AreEqual("中文 content", (string)read["result"]["contents"][0]["text"]);
                    var missing = await relay.ResponseAsync(relay.Request("resources/read", new JObject { ["uri"] = "unity://missing" }));
                    Assert.AreEqual(-32602, (int)missing["error"]["code"]);

                    var prompts = await relay.ResponseAsync(relay.Request("prompts/list"));
                    Assert.AreEqual("scene-authoring", (string)prompts["result"]["prompts"][0]["name"]);
                    var prompt = await relay.ResponseAsync(relay.Request("prompts/get", new JObject { ["name"] = "scene-authoring", ["arguments"] = new JObject { ["task"] = "x" } }));
                    Assert.AreEqual("skill:x", (string)prompt["result"]["messages"][0]["content"]["text"]);
                }
            }
        }

        [Test]
        public async Task Relay_EditorNotRunning_ListsEmptyResourcesAndPrompts()
        {
            using (var relay = new RelayProcess(_build, _project))
            {
                await relay.InitializeAsync();
                Assert.AreEqual(0, ((JArray)(await relay.ResponseAsync(relay.Request("resources/list")))["result"]["resources"]).Count);
                Assert.AreEqual(0, ((JArray)(await relay.ResponseAsync(relay.Request("prompts/list")))["result"]["prompts"]).Count);
                var read = await relay.ResponseAsync(relay.Request("resources/read", new JObject { ["uri"] = "unity://console" }));
                StringAssert.Contains("not running", (string)read["error"]["message"]);
            }
        }

        [Test]
        public async Task Relay_UnknownProtocolVersion_FallsBackToLatest()
        {
            using (var relay = new RelayProcess(_build, _project))
            {
                var id = relay.Request("initialize", new JObject
                {
                    ["protocolVersion"] = "1999-01-01",
                    ["capabilities"] = new JObject(),
                    ["clientInfo"] = new JObject { ["name"] = "t" }
                });
                var response = await relay.ResponseAsync(id);
                Assert.AreEqual("2025-11-25", (string)response["result"]["protocolVersion"]);
                Assert.IsTrue((bool)response["result"]["capabilities"]["tools"]["listChanged"]);
            }
        }

        [Test]
        public async Task Relay_EditorNotRunning_ListsEmptyAndCallExplains()
        {
            using (var relay = new RelayProcess(_build, _project))
            {
                await relay.InitializeAsync();

                var listId = relay.Request("tools/list");
                var list = await relay.ResponseAsync(listId);
                Assert.AreEqual(0, ((JArray)list["result"]["tools"]).Count);

                var callId = relay.Request("tools/call", new JObject { ["name"] = "echo", ["arguments"] = new JObject() });
                var call = await relay.ResponseAsync(callId);
                Assert.IsTrue((bool)call["result"]["isError"]);
                StringAssert.Contains("not running", (string)call["result"]["content"][0]["text"]);
            }
        }

        [Test]
        public async Task Relay_WrongToken_ReportsReconfigure()
        {
            using (var server = new McpBridgeServer("other-token", new FakeBridge()))
            {
                server.Start(0);
                WriteEndpoint(_project, server.Port);
                using (var relay = new RelayProcess(_build, _project))
                {
                    await relay.InitializeAsync();
                    await Task.Delay(1500); // 让连接循环完成一次握手
                    var callId = relay.Request("tools/call", new JObject { ["name"] = "echo", ["arguments"] = new JObject() });
                    var call = await relay.ResponseAsync(callId);
                    Assert.IsTrue((bool)call["result"]["isError"]);
                    StringAssert.Contains("Invalid token", (string)call["result"]["content"][0]["text"]);
                }
            }
        }
    }
}
#endif
