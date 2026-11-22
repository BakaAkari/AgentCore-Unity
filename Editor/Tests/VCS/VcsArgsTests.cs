using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using AgentCore.Editor.Components.VCS.Tools;

namespace AgentCore.Tests.Editor.VCS
{
    /// <summary>
    /// <see cref="VcsArgs"/> 参数转义与路径分组的单元测试。
    /// </summary>
    [TestFixture]
    public class VcsArgsTests
    {
        #region ChunkPaths

        [Test]
        public void ChunkPaths_Empty_ReturnsNoChunks()
        {
            Assert.IsEmpty(VcsArgs.ChunkPaths("git", new[] { "add", "--" }, new string[0]));
            Assert.IsEmpty(VcsArgs.ChunkPaths("git", new[] { "add", "--" }, null));
        }

        [Test]
        public void ChunkPaths_ShortList_SingleChunk()
        {
            var paths = new[] { "a.txt", "b c.txt", "中文.txt" };

            var chunks = VcsArgs.ChunkPaths("git", new[] { "add", "--" }, paths);

            Assert.AreEqual(1, chunks.Count);
            CollectionAssert.AreEqual(paths, chunks[0]);
        }

        [Test]
        public void ChunkPaths_LongList_EveryChunkFitsAndOrderPreserved()
        {
            var prefix = new[] { "add", "--" };
            var paths = Enumerable.Range(0, 5000)
                .Select(i => $"Assets/Some Folder With Spaces/Sub/{i:D5}_资源文件.prefab")
                .ToList();

            var chunks = VcsArgs.ChunkPaths("git", prefix, paths);

            Assert.Greater(chunks.Count, 1);
            foreach (var chunk in chunks)
            {
                var all = new List<string>(prefix);
                all.AddRange(chunk);
                var commandLineLength = "git".Length + 1 + VcsArgs.Join(all).Length;
                Assert.LessOrEqual(commandLineLength, VcsArgs.MaxCommandLineChars);
            }
            CollectionAssert.AreEqual(paths, chunks.SelectMany(c => c).ToList());
        }

        [Test]
        public void ChunkPaths_SingleOversizedPath_GetsOwnChunk()
        {
            var huge = new string('x', 100);
            var paths = new[] { "a", huge, "b" };

            var chunks = VcsArgs.ChunkPaths("git", new string[0], paths, maxChars: 50);

            Assert.AreEqual(3, chunks.Count);
            Assert.AreEqual(huge, chunks[1].Single());
        }

        #endregion

        #region Quote

        [TestCase("plain", "plain")]
        [TestCase("with space", "\"with space\"")]
        [TestCase("say \"hi\"", "\"say \\\"hi\\\"\"")]
        [TestCase(@"C:\dir with space\", "\"C:\\dir with space\\\\\"")]
        [TestCase("", "\"\"")]
        public void QuoteWindows_FollowsArgvRules(string input, string expected)
        {
            Assert.AreEqual(expected, VcsArgs.QuoteWindows(input));
        }

        #endregion
    }
}
