using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Components.MCP.Bridge;
using AgentCore.Editor.McpClient;
using AgentCore.Editor.Tools.Safety;
using AgentCore.Editor.Utils;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.MCP
{
    /// <summary>
    /// AgentCore 作为 MCP 客户端：配置解析、stdio 与 HTTP 传输、工具代理。
    /// 外部服务器用 AgentCore 自己的 relay（后端为测试 bridge）充当。
    /// </summary>
    [TestFixture]
    public class McpClientTests
    {
        private const string BridgeToken = "relay-test-token";
        private string _project;
        private McpBridgeServer _server;

        [SetUp]
        public void SetUp()
        {
            _project = Path.Combine(Path.GetTempPath(), "agentcore-mcp-client-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(_project, "Library", "AgentCore", "Mcp"));
            Assert.IsTrue(McpRelayTests.SharedBuild.Success, McpRelayTests.SharedBuild.Error);
            _server = new McpBridgeServer(BridgeToken, new McpRelayTests.FakeBridge());
            _server.Start(0);
            McpRelayTests.WriteEndpoint(_project, _server.Port);
        }

        [TearDown]
        public void TearDown()
        {
            _server?.Dispose();
            try { Directory.Delete(_project, true); } catch { /* ignore */ }
        }

        private McpServerConfig StdioConfig(string name = "ext") => new McpServerConfig
        {
            Name = name,
            Command = McpRelayTests.SharedBuild.MonoPath,
            Args = { McpRelayTests.SharedBuild.RelayPath, "--project", _project },
            Scope = "user"
        };

        [Test]
        public void Config_ParsesClaudeDesktopFormat()
        {
            var configs = McpServerConfigStore.ParseJson(@"{
                ""mcpServers"": {
                    ""jira"": { ""command"": ""npx"", ""args"": [""-y"", ""mcp-atlassian""], ""env"": { ""JIRA_URL"": ""https://x"" } },
                    ""docs"": { ""type"": ""http"", ""url"": ""https://example.com/mcp"", ""headers"": { ""Authorization"": ""Bearer t"" } },
                    ""off"":  { ""command"": ""foo"", ""disabled"": true },
                    ""bad"":  { ""args"": [] }
                } }", "project");
            Assert.AreEqual(3, configs.Count);
            var jira = configs.Single(c => c.Name == "jira");
            CollectionAssert.AreEqual(new[] { "-y", "mcp-atlassian" }, jira.Args);
            Assert.AreEqual("https://x", jira.Env["JIRA_URL"]);
            Assert.IsFalse(jira.IsHttp);
            var docs = configs.Single(c => c.Name == "docs");
            Assert.IsTrue(docs.IsHttp);
            Assert.AreEqual("Bearer t", docs.Headers["Authorization"]);
            Assert.IsTrue(configs.Single(c => c.Name == "off").Disabled);
        }

        [Test]
        public async Task Session_Stdio_InitializeListAndCall()
        {
            using (var session = McpClientSession.Create(StdioConfig()))
            {
                await session.InitializeAsync(CancellationToken.None);
                Assert.AreEqual("agentcore-unity", session.ServerName);
                var tools = await session.ListToolsAsync(CancellationToken.None);
                Assert.AreEqual("echo", (string)tools.Single()["name"]);
                var result = await session.CallToolAsync("echo", new JObject { ["x"] = "中文" }, CancellationToken.None);
                StringAssert.Contains("中文", McpProxyTool.FlattenContent(result));
                Assert.IsFalse((bool)result["isError"]);
            }
        }

        [Test]
        public async Task Session_Http_InitializeListAndCall()
        {
            const string httpToken = "client-http-token-0123456789abcdef";
            File.WriteAllText(Path.Combine(_project, "Library", "AgentCore", "Mcp", "http-token"), httpToken);
            var port = McpHttpEndpoint.FindFreePort();
            var build = McpRelayTests.SharedBuild;
            using (var relay = Process.Start(new ProcessStartInfo
                   {
                       FileName = build.MonoPath,
                       Arguments = ProcessArgs.Join(new[] { build.RelayPath, "--project", _project, "--http", port.ToString() }),
                       UseShellExecute = false,
                       CreateNoWindow = true,
                   }))
            {
                try
                {
                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    while (McpHttpEndpoint.IsPortFree(port) && DateTime.UtcNow < deadline)
                        await Task.Delay(100);

                    var config = new McpServerConfig
                    {
                        Name = "ext-http",
                        Url = $"http://127.0.0.1:{port}/mcp",
                        Headers = { ["Authorization"] = "Bearer " + httpToken }
                    };
                    using (var session = McpClientSession.Create(config))
                    {
                        await session.InitializeAsync(CancellationToken.None);
                        var tools = await session.ListToolsAsync(CancellationToken.None);
                        Assert.AreEqual("echo", (string)tools.Single()["name"]);
                        var result = await session.CallToolAsync("echo", new JObject { ["y"] = 1 }, CancellationToken.None);
                        StringAssert.Contains("\"y\":1", McpProxyTool.FlattenContent(result));
                    }
                }
                finally
                {
                    try { relay.Kill(); } catch { /* exited */ }
                }
            }
        }

        [Test]
        public async Task Server_RegistersProxyTools_WithRiskAndCategory()
        {
            var server = new McpClientServer(StdioConfig("ext"));
            try
            {
                await server.StartAsync();
                Assert.AreEqual("connected", server.Status, server.Error);
                var tool = server.Tools.Single();
                Assert.AreEqual("mcp__ext__echo", tool.Metadata.Name);
                Assert.AreEqual("MCP: ext", tool.Metadata.Category);
                Assert.AreEqual(ToolRiskLevel.High, tool.Metadata.RiskLevel);
                Assert.AreEqual(AgentCore.Editor.Tools.Infrastructure.ToolVisibility.OnDemand, tool.Metadata.Visibility);

                var result = await tool.ExecuteAsync(new JObject { ["z"] = true });
                Assert.IsTrue(result.Success, result.Error);
                StringAssert.Contains("\"z\":true", result.Output);
            }
            finally
            {
                server.Stop();
            }
        }

        [Test]
        public async Task Server_BadCommand_ReportsError()
        {
            var server = new McpClientServer(new McpServerConfig { Name = "broken", Command = "agentcore-no-such-command-xyz", Scope = "user" });
            await server.StartAsync();
            Assert.AreEqual("error", server.Status);
            Assert.IsNotEmpty(server.Error);
            server.Stop();
        }

        [Test]
        public void ToolName_IsSanitizedAndBounded()
        {
            Assert.AreEqual("mcp__my_server__do_thing", McpProxyTool.ToolName("my server", "do.thing"));
            Assert.AreEqual(64, McpProxyTool.ToolName(new string('a', 50), new string('b', 50)).Length);
        }

        [Test]
        public void Flatten_HandlesTextImageResource()
        {
            var text = McpProxyTool.FlattenContent(new JObject
            {
                ["content"] = new JArray
                {
                    new JObject { ["type"] = "text", ["text"] = "hello" },
                    new JObject { ["type"] = "image", ["mimeType"] = "image/png", ["data"] = "QUJD" },
                    new JObject { ["type"] = "resource", ["resource"] = new JObject { ["uri"] = "file:///a", ["text"] = "body" } }
                }
            });
            StringAssert.Contains("hello", text);
            StringAssert.Contains("[image image/png", text);
            StringAssert.Contains("[resource file:///a]", text);
        }

        [Test]
        public void ProxyTools_AreNotReexportedByAgentCoreMcpServer()
        {
            var names = McpToolCatalog.Build().Tools.Select(t => (string)t["name"]).ToList();
            Assert.IsFalse(names.Any(n => n.StartsWith("mcp__", StringComparison.Ordinal)));
        }
    }
}
