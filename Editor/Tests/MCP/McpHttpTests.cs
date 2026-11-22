using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using AgentCore.Editor.Components.MCP.Bridge;
using AgentCore.Editor.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.MCP
{
    /// <summary>
    /// 本机 Streamable HTTP 端点：relay 以 --http 模式运行，后端为测试 bridge。
    /// 覆盖令牌 / Origin / 方法校验、新协议（头部一致性、SSE 结果、subscriptions/listen）与旧协议会话。
    /// </summary>
    [TestFixture]
    public class McpHttpTests
    {
        private const string BridgeToken = "relay-test-token";
        private const string HttpToken = "http-test-token-0123456789abcdef";

        private string _project;
        private McpBridgeServer _server;
        private Process _relay;
        private string _url;

        [SetUp]
        public void SetUp()
        {
            _project = Path.Combine(Path.GetTempPath(), "agentcore-mcp-http-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var mcpDir = Path.Combine(_project, "Library", "AgentCore", "Mcp");
            Directory.CreateDirectory(mcpDir);
            File.WriteAllText(Path.Combine(mcpDir, "http-token"), HttpToken);

            var build = McpRelayTests.SharedBuild;
            Assert.IsTrue(build.Success, build.Error);
            _server = new McpBridgeServer(BridgeToken, new McpRelayTests.FakeBridge());
            _server.Start(0);
            McpRelayTests.WriteEndpoint(_project, _server.Port);

            var port = McpHttpEndpoint.FindFreePort();
            _url = $"http://127.0.0.1:{port}/mcp";
            _relay = Process.Start(new ProcessStartInfo
            {
                FileName = build.MonoPath,
                Arguments = ProcessArgs.Join(new[] { build.RelayPath, "--project", _project, "--http", port.ToString() }),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            });
            _relay.ErrorDataReceived += (_, __) => { };
            _relay.BeginErrorReadLine();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (McpHttpEndpoint.IsPortFree(port) && DateTime.UtcNow < deadline)
                System.Threading.Thread.Sleep(100);
            Assert.IsFalse(McpHttpEndpoint.IsPortFree(port), "HTTP relay should be listening");
        }

        [TearDown]
        public void TearDown()
        {
            try { _relay?.Kill(); } catch { /* exited */ }
            _relay?.Dispose();
            _server?.Dispose();
            try { Directory.Delete(_project, true); } catch { /* ignore */ }
        }

        private static JObject Meta() => new JObject
        {
            ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
            ["io.modelcontextprotocol/clientInfo"] = new JObject { ["name"] = "http-test" },
            ["io.modelcontextprotocol/clientCapabilities"] = new JObject()
        };

        private sealed class HttpReply
        {
            public int Status;
            public string ContentType;
            public string Body;
            public string SessionId;
        }

        private HttpReply Post(JObject body, Action<HttpWebRequest> configure = null, bool token = true)
        {
            var request = (HttpWebRequest)WebRequest.Create(_url);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Accept = "application/json, text/event-stream";
            request.Timeout = 20000;
            if (token)
                request.Headers["Authorization"] = "Bearer " + HttpToken;
            var method = (string)body["method"];
            if (body["params"]?["_meta"] != null)
            {
                request.Headers["MCP-Protocol-Version"] = "2026-07-28";
                request.Headers["Mcp-Method"] = method;
                var name = (string)body["params"]["name"] ?? (string)body["params"]["uri"];
                if (name != null)
                    request.Headers["Mcp-Name"] = name;
            }
            configure?.Invoke(request);
            var bytes = Encoding.UTF8.GetBytes(body.ToString(Formatting.None));
            using (var stream = request.GetRequestStream())
                stream.Write(bytes, 0, bytes.Length);
            return Read(request);
        }

        private static HttpReply Read(HttpWebRequest request)
        {
            HttpWebResponse response;
            try
            {
                response = (HttpWebResponse)request.GetResponse();
            }
            catch (WebException ex) when (ex.Response is HttpWebResponse error)
            {
                response = error;
            }
            using (response)
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                return new HttpReply
                {
                    Status = (int)response.StatusCode,
                    ContentType = response.ContentType,
                    Body = reader.ReadToEnd(),
                    SessionId = response.Headers["Mcp-Session-Id"]
                };
            }
        }

        /// <summary>SSE 正文中最后一个 data 行的 JSON（最终响应）。</summary>
        private static JObject LastSseMessage(string body)
        {
            JObject last = null;
            foreach (var line in body.Split('\n'))
            {
                if (line.StartsWith("data: ", StringComparison.Ordinal))
                    last = JObject.Parse(line.Substring(6));
            }
            return last;
        }

        [Test]
        public void Rejects_MissingToken_ForeignOrigin_AndGet()
        {
            var body = new JObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "server/discover", ["params"] = new JObject { ["_meta"] = Meta() } };
            Assert.AreEqual(401, Post(body, token: false).Status);
            Assert.AreEqual(403, Post(body, r => r.Headers["Origin"] = "http://evil.example").Status);
            Assert.AreEqual(200, Post(body, r => r.Headers["Origin"] = "http://localhost:3000").Status);

            var get = (HttpWebRequest)WebRequest.Create(_url);
            get.Headers["Authorization"] = "Bearer " + HttpToken;
            Assert.AreEqual(405, Read(get).Status);
        }

        [Test]
        public void Modern_ToolsCall_StreamsSseResult()
        {
            var reply = Post(new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 7,
                ["method"] = "tools/call",
                ["params"] = new JObject { ["name"] = "echo", ["arguments"] = new JObject { ["k"] = "中文" }, ["_meta"] = Meta() }
            });
            Assert.AreEqual(200, reply.Status);
            StringAssert.StartsWith("text/event-stream", reply.ContentType);
            var final = LastSseMessage(reply.Body);
            Assert.AreEqual(7, (int)final["id"]);
            Assert.AreEqual("complete", (string)final["result"]["resultType"]);
            StringAssert.Contains("中文", (string)final["result"]["content"][0]["text"]);
        }

        [Test]
        public void Modern_HeaderMismatch_Is400()
        {
            var reply = Post(new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JObject { ["name"] = "echo", ["arguments"] = new JObject(), ["_meta"] = Meta() }
            }, r => r.Headers["Mcp-Name"] = "something_else");
            Assert.AreEqual(400, reply.Status);
            Assert.AreEqual(-32020, (int)JObject.Parse(reply.Body)["error"]["code"]);
        }

        [Test]
        public void Modern_UnknownMethod_Is404()
        {
            var reply = Post(new JObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "nope/nothing", ["params"] = new JObject { ["_meta"] = Meta() } });
            Assert.AreEqual(404, reply.Status);
            Assert.AreEqual(-32601, (int)JObject.Parse(reply.Body)["error"]["code"]);
        }

        [Test]
        public void Legacy_SessionLifecycle()
        {
            var init = Post(new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "initialize",
                ["params"] = new JObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JObject(), ["clientInfo"] = new JObject { ["name"] = "legacy-http" } }
            });
            Assert.AreEqual(200, init.Status);
            Assert.IsNotEmpty(init.SessionId);
            var session = init.SessionId;

            var initialized = Post(new JObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, r => r.Headers["Mcp-Session-Id"] = session);
            Assert.AreEqual(202, initialized.Status);

            var list = Post(new JObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/list" }, r => r.Headers["Mcp-Session-Id"] = session);
            Assert.AreEqual(200, list.Status);
            Assert.AreEqual("echo", (string)JObject.Parse(list.Body)["result"]["tools"][0]["name"]);

            Assert.AreEqual(400, Post(new JObject { ["jsonrpc"] = "2.0", ["id"] = 3, ["method"] = "tools/list" }).Status);

            var delete = (HttpWebRequest)WebRequest.Create(_url);
            delete.Method = "DELETE";
            delete.Headers["Authorization"] = "Bearer " + HttpToken;
            delete.Headers["Mcp-Session-Id"] = session;
            Assert.AreEqual(200, Read(delete).Status);
            Assert.AreEqual(404, Post(new JObject { ["jsonrpc"] = "2.0", ["id"] = 4, ["method"] = "tools/list" }, r => r.Headers["Mcp-Session-Id"] = session).Status);
        }

        [Test]
        public async Task Modern_Listen_OverSse_DeliversResourceUpdates()
        {
            // 先让 relay 连上 bridge
            Post(new JObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/list", ["params"] = new JObject { ["_meta"] = Meta() } });

            var request = (HttpWebRequest)WebRequest.Create(_url);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Accept = "application/json, text/event-stream";
            request.Headers["Authorization"] = "Bearer " + HttpToken;
            request.Headers["MCP-Protocol-Version"] = "2026-07-28";
            request.Headers["Mcp-Method"] = "subscriptions/listen";
            var body = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 99,
                ["method"] = "subscriptions/listen",
                ["params"] = new JObject
                {
                    ["notifications"] = new JObject { ["resourceSubscriptions"] = new JArray("unity://console") },
                    ["_meta"] = Meta()
                }
            };
            var bytes = Encoding.UTF8.GetBytes(body.ToString(Formatting.None));
            using (var stream = request.GetRequestStream())
                stream.Write(bytes, 0, bytes.Length);

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                StringAssert.StartsWith("text/event-stream", response.ContentType);
                var ack = await NextData(reader);
                Assert.AreEqual("notifications/subscriptions/acknowledged", (string)ack["method"]);

                _server.Broadcast(new JObject { ["type"] = "event", ["kind"] = "resourceUpdated", ["uri"] = "unity://console" });
                var updated = await NextData(reader);
                Assert.AreEqual("notifications/resources/updated", (string)updated["method"]);
                Assert.AreEqual(99, (int)updated["params"]["_meta"]["io.modelcontextprotocol/subscriptionId"]);
            }
        }

        private static async Task<JObject> NextData(StreamReader reader)
        {
            var read = Task.Run(() =>
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.StartsWith("data: ", StringComparison.Ordinal))
                        return JObject.Parse(line.Substring(6));
                }
                return null;
            });
            if (await Task.WhenAny(read, Task.Delay(10000)) != read)
                Assert.Fail("no SSE event in time");
            return read.Result;
        }

        [Test]
        public void HttpConfigs_ContainUrlAndToken()
        {
            var claude = McpHttpEndpoint.ClaudeCodeCommand("http://127.0.0.1:1234/mcp", "tok");
            Assert.AreEqual("claude mcp add --transport http --scope user agentcore-http http://127.0.0.1:1234/mcp --header \"Authorization: Bearer tok\"", claude);
            var codex = McpHttpEndpoint.CodexToml("http://127.0.0.1:1234/mcp", "tok");
            StringAssert.Contains("[mcp_servers.agentcore_http]", codex);
            StringAssert.Contains("url = 'http://127.0.0.1:1234/mcp'", codex);
            StringAssert.Contains("http_headers = { Authorization = 'Bearer tok' }", codex);
        }
    }
}
