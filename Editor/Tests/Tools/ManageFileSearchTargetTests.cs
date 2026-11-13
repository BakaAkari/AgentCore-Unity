using System;
using System.IO;
using System.Linq;
using AgentCore.Editor.Tools;
using AgentCore.Editor.Tools.FileSystem;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace AgentCore.Tests.Editor.Tools
{
    /// <summary>
    /// manage_file 对文件路径与 Unity 序列化资源的处理（用户报告：查 RePawAgent.prefab 的 m_DeathTime 时
    /// search_content 传文件报 "Directory not found"、默认 '*' 静默跳过 .prefab，Agent 误答为代码默认值）。
    /// 临时目录建在工程根下、Assets 之外，不触发导入。两个搜索引擎（ripgrep / .NET）哪个可用就测哪个。
    /// </summary>
    [TestFixture]
    public class ManageFileSearchTargetTests
    {
        private string _dirName;
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            _dirName = "AgentCoreManageFileTest_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            _dir = Path.Combine(projectRoot, _dirName);
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, "RePawAgent.cs"), "public class RePawAgent { public float m_DeathTime = 60f; }\n");
            File.WriteAllText(Path.Combine(_dir, "RePawAgent.prefab"), "%YAML 1.1\n--- !u!114 &1\nMonoBehaviour:\n  m_DeathTime: 90\n");
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (Directory.Exists(_dir))
                    Directory.Delete(_dir, recursive: true);
            }
            catch
            {
                // 临时目录清理失败不影响结果
            }
        }

        private static ToolResult Run(JObject parameters) => new ManageFileTool().ExecuteAsync(parameters).Result;

        private static JObject Data(ToolResult result) => (JObject)JObject.Parse(result.Output)["data"];

        private static string[] MatchedFiles(ToolResult result) =>
            Data(result)["matches"].Select(m => Path.GetFileName((string)m["file"])).Distinct().ToArray();

        [Test]
        public void SearchContent_PathIsPrefabFile_SearchesThatFile()
        {
            var result = Run(new JObject
            {
                ["action"] = "search_content",
                ["pattern"] = "m_DeathTime",
                ["path"] = _dirName + "/RePawAgent.prefab"
            });

            Assert.IsTrue(result.Success, result.Error ?? result.Output);
            CollectionAssert.AreEqual(new[] { "RePawAgent.prefab" }, MatchedFiles(result));
            StringAssert.Contains("m_DeathTime: 90", (string)Data(result)["matches"][0]["context"]);
            CollectionAssert.IsEmpty(Data(result)["skipped_extensions"]);
        }

        [Test]
        public void SearchContent_DefaultPattern_SkipsPrefab_AndSaysSo()
        {
            var result = Run(new JObject
            {
                ["action"] = "search_content",
                ["pattern"] = "m_DeathTime",
                ["path"] = _dirName
            });

            Assert.IsTrue(result.Success, result.Error ?? result.Output);
            CollectionAssert.AreEqual(new[] { "RePawAgent.cs" }, MatchedFiles(result));
            CollectionAssert.Contains(Data(result)["skipped_extensions"].Select(t => (string)t).ToArray(), ".prefab");
            StringAssert.Contains("NOT searched", result.Output);
            StringAssert.Contains("*.prefab", result.Output);
        }

        [TestCase("*.prefab")]
        [TestCase("*.cs|*.prefab")]
        [TestCase("*|*.prefab")]
        public void SearchContent_PrefabNamedInFilePattern_IsSearched(string filePattern)
        {
            var result = Run(new JObject
            {
                ["action"] = "search_content",
                ["pattern"] = "m_DeathTime",
                ["path"] = _dirName,
                ["file_pattern"] = filePattern
            });

            Assert.IsTrue(result.Success, result.Error ?? result.Output);
            CollectionAssert.Contains(MatchedFiles(result), "RePawAgent.prefab");
            CollectionAssert.DoesNotContain(Data(result)["skipped_extensions"].Select(t => (string)t).ToArray(), ".prefab");
        }

        [Test]
        public void ListDirectory_PathIsFile_PointsToReadFile()
        {
            var result = Run(new JObject
            {
                ["action"] = "list_directory",
                ["path"] = _dirName + "/RePawAgent.prefab"
            });

            Assert.IsFalse(result.Success);
            var error = result.Error ?? result.Output;
            StringAssert.Contains("is a file, not a directory", error);
            StringAssert.Contains("read_file", error);
            StringAssert.Contains(_dirName, error);
        }

        [Test]
        public void SearchContent_MissingPath_ReportsPathNotFound()
        {
            var result = Run(new JObject
            {
                ["action"] = "search_content",
                ["pattern"] = "x",
                ["path"] = _dirName + "/missing.prefab"
            });

            Assert.IsFalse(result.Success);
            StringAssert.Contains("Path not found", result.Error ?? result.Output);
        }

        [TestCase(".prefab", "*", true)]
        [TestCase(".prefab", "*.*", true)]
        [TestCase(".prefab", "*.prefab", false)]
        [TestCase(".prefab", "*|*.prefab", false)]
        [TestCase(".PREFAB", "*.prefab", false)]
        [TestCase(".cs", "*", false)]
        [TestCase(".png", "*.prefab", true)]
        [TestCase("", "*", false)]
        public void IsSkippedByExtension(string ext, string filePattern, bool expected)
        {
            Assert.AreEqual(expected, ManageFileTool.IsSkippedByExtension(filePattern.Split('|'), ext));
        }
    }
}
