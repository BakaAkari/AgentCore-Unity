using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AgentCore.Editor.Tools.FileSystem;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.Tools
{
    /// <summary>
    /// manage_file search_content（ripgrep 路径）excluded_dirs 有界目录遍历的单元测试：
    /// 任意深度的 obj/ 都要列出，跳过 .git / 隐藏目录，超出上限时报告不完整。
    /// </summary>
    [TestFixture]
    public class ManageFileExcludedDirsTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "agentcore-excluded-dirs-" + Guid.NewGuid().ToString("N"));
            foreach (var rel in new[]
                     {
                         "obj",
                         "A/B/obj/Debug",
                         "C/obj/inner/obj",
                         ".git/obj",
                         ".hidden/obj",
                         "D/E/F",
                     })
            {
                Directory.CreateDirectory(Path.Combine(_root, rel));
            }
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (Directory.Exists(_root))
                    Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // 临时目录清理失败不影响结果
            }
        }

        private static string[] Normalized(IEnumerable<string> hits, string root)
        {
            var prefix = root.Replace('\\', '/').TrimEnd('/') + "/";
            return hits.Select(h => h.Replace('\\', '/'))
                       .Select(h => h.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? h.Substring(prefix.Length) : h)
                       .OrderBy(h => h, StringComparer.OrdinalIgnoreCase)
                       .ToArray();
        }

        [Test]
        public void ListsObjAtAnyDepth_WithoutDescendingIntoIt()
        {
            var hits = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            var complete = ManageFileTool.CollectExcludedDirsBounded(
                new[] { _root }, includeHidden: false, hits, maxDirs: 1000, budget: TimeSpan.FromSeconds(10), CancellationToken.None);

            Assert.IsTrue(complete);
            CollectionAssert.AreEqual(new[] { "A/B/obj", "C/obj", "obj" }, Normalized(hits, _root));
        }

        [Test]
        public void SkipsGitAlways_AndHiddenUnlessRequested()
        {
            var hits = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            ManageFileTool.CollectExcludedDirsBounded(
                new[] { _root }, includeHidden: true, hits, maxDirs: 1000, budget: TimeSpan.FromSeconds(10), CancellationToken.None);

            var normalized = Normalized(hits, _root);
            CollectionAssert.Contains(normalized, ".hidden/obj");
            CollectionAssert.DoesNotContain(normalized, ".git/obj");
        }

        [Test]
        public void StopsAtDirCap_AndReportsIncomplete()
        {
            var hits = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            var complete = ManageFileTool.CollectExcludedDirsBounded(
                new[] { _root }, includeHidden: false, hits, maxDirs: 1, budget: TimeSpan.FromSeconds(10), CancellationToken.None);

            Assert.IsFalse(complete, "超过目录上限必须标记为不完整");
            Assert.IsFalse(hits.Any(h => h.Replace('\\', '/').EndsWith("A/B/obj", StringComparison.OrdinalIgnoreCase)),
                "上限为 1 时不应走到 A/B");
        }

        [Test]
        public void CancelledToken_ReportsIncomplete()
        {
            var hits = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                var complete = ManageFileTool.CollectExcludedDirsBounded(
                    new[] { _root }, includeHidden: false, hits, maxDirs: 1000, budget: TimeSpan.FromSeconds(10), cts.Token);

                Assert.IsFalse(complete);
            }
        }
    }
}
