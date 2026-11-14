#if AGENTCORE_MCP
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Components.MCP.Bridge;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace AgentCore.Tests.Editor.MCP
{
    /// <summary>MCP Resources 与 Prompts 的 bridge 侧：列表、读取（经只读工具）、Skills 作为 prompt。</summary>
    [TestFixture]
    public class McpResourceTests
    {
        private string _dir;
        private string _dirName;

        [SetUp]
        public void SetUp()
        {
            _dirName = "AgentCoreMcpRes_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            _dir = Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, "..")), _dirName);
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch { /* ignore */ }
        }

        [Test]
        public void List_HasCoreResourcesAndAssetTemplate()
        {
            var list = McpResourceCatalog.List();
            var uris = list["resources"].Select(r => (string)r["uri"]).ToList();
            CollectionAssert.Contains(uris, McpResourceCatalog.ProjectContextUri);
            CollectionAssert.Contains(uris, McpResourceCatalog.SceneHierarchyUri);
            CollectionAssert.Contains(uris, McpResourceCatalog.ConsoleUri);
            Assert.AreEqual(McpResourceCatalog.AssetUriTemplate, (string)list["resourceTemplates"][0]["uriTemplate"]);
        }

        [Test]
        public async Task Read_ProjectContext_IsMarkdown()
        {
            var result = await McpResourceCatalog.ReadAsync(McpResourceCatalog.ProjectContextUri, "test", CancellationToken.None);
            var content = (JObject)result["contents"][0];
            Assert.AreEqual("text/markdown", (string)content["mimeType"]);
            StringAssert.Contains(Application.unityVersion, (string)content["text"]);
        }

        [Test]
        public async Task Read_AssetTemplate_ReturnsRawFile()
        {
            File.WriteAllText(Path.Combine(_dir, "note.txt"), "line one\nm_DeathTime: 90\n");
            var uri = McpResourceCatalog.AssetUriPrefix + _dirName + "/note.txt";
            var result = await McpResourceCatalog.ReadAsync(uri, "test", CancellationToken.None);
            var text = (string)result["contents"][0]["text"];
            StringAssert.StartsWith("line one", text, "no line-number prefix");
            StringAssert.Contains("m_DeathTime: 90", text);
        }

        [Test]
        public void Read_UnknownUri_IsInvalidParams()
        {
            var ex = Assert.ThrowsAsync<McpRequestException>(() => McpResourceCatalog.ReadAsync("unity://nope", "test", CancellationToken.None));
            Assert.AreEqual(-32602, ex.Code);
        }

        [Test]
        public void Read_MissingFile_IsInvalidParams()
        {
            var ex = Assert.ThrowsAsync<McpRequestException>(() =>
                McpResourceCatalog.ReadAsync(McpResourceCatalog.AssetUriPrefix + _dirName + "/missing.txt", "test", CancellationToken.None));
            Assert.AreEqual(-32602, ex.Code);
        }

        [Test]
        public void Prompts_ListBuiltinSkills_AndGetAppendsTask()
        {
            var prompts = McpPromptCatalog.List()["prompts"].Select(p => (string)p["name"]).ToList();
            Assert.IsNotEmpty(prompts);
            var name = prompts.First();

            var prompt = McpPromptCatalog.Get(name, new JObject { ["task"] = "Build a test level" });
            var text = (string)prompt["messages"][0]["content"]["text"];
            Assert.AreEqual("user", (string)prompt["messages"][0]["role"]);
            StringAssert.EndsWith("Task: Build a test level", text);

            var ex = Assert.Throws<McpRequestException>(() => McpPromptCatalog.Get("no-such-skill", null));
            Assert.AreEqual(-32602, ex.Code);
        }
    }
}
#endif
