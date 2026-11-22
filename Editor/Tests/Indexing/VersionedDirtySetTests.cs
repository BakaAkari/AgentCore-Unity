using System.Linq;
using AgentCore.Editor.Components.Indexing.Core;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.Indexing
{
    /// <summary>
    /// VersionedDirtySet 版本号与比较删除的单元测试。
    /// </summary>
    [TestFixture]
    public class VersionedDirtySetTests
    {
        [Test]
        public void Mark_AssignsIncreasingVersions_AndReportsNew()
        {
            var set = new VersionedDirtySet();

            var v1 = set.Mark("a.cs", out var new1);
            var v2 = set.Mark("a.cs", out var new2);

            Assert.IsTrue(new1);
            Assert.IsFalse(new2);
            Assert.Greater(v2, v1);
            Assert.AreEqual(1, set.Count);
        }

        [Test]
        public void TryRemove_SameVersion_Removes()
        {
            var set = new VersionedDirtySet();
            var v = set.Mark("a.cs");

            Assert.IsTrue(set.TryRemove("a.cs", v));
            Assert.AreEqual(0, set.Count);
        }

        [Test]
        public void TryRemove_AfterRemarkDuringIndexing_KeepsPath()
        {
            var set = new VersionedDirtySet();
            set.Mark("a.cs");
            var snapshot = set.Snapshot();

            // 索引进行中文件再次变更
            set.Mark("a.cs");

            var removed = set.RemoveProcessed(snapshot);

            Assert.AreEqual(0, removed);
            Assert.IsTrue(set.Contains("a.cs"), "索引期间的新改动不能被快照删除");
        }

        [Test]
        public void RemoveProcessed_OnlyRemovesUnchangedEntries()
        {
            var set = new VersionedDirtySet();
            set.Mark("a.cs");
            set.Mark("b.cs");
            set.Mark("c.cs");
            var snapshot = set.Snapshot();

            set.Mark("b.cs");
            set.Mark("d.cs");

            var removed = set.RemoveProcessed(snapshot);

            Assert.AreEqual(2, removed);
            CollectionAssert.AreEquivalent(new[] { "b.cs", "d.cs" }, set.Paths());
        }

        [Test]
        public void Snapshot_OrdersByVersion_AndHonoursMax()
        {
            var set = new VersionedDirtySet();
            set.Mark("first.cs");
            set.Mark("second.cs");
            set.Mark("third.cs");
            set.Mark("first.cs"); // 重新标记后排到最后

            var snapshot = set.Snapshot(2);

            CollectionAssert.AreEqual(new[] { "second.cs", "third.cs" }, snapshot.Select(kv => kv.Key).ToArray());
            Assert.AreEqual(3, set.Count, "Snapshot 不应删除任何条目");
        }

        [Test]
        public void Paths_AreCaseInsensitiveByDefault()
        {
            var set = new VersionedDirtySet();
            set.Mark("Assets/A.cs");
            var v = set.Mark("assets/a.cs");

            Assert.AreEqual(1, set.Count);
            Assert.IsTrue(set.TryRemove("ASSETS/A.CS", v));
        }

        [Test]
        public void RemoveUnconditionally_IgnoresVersion()
        {
            var set = new VersionedDirtySet();
            set.Mark("a.cs");
            set.Mark("a.cs");

            Assert.IsTrue(set.RemoveUnconditionally("a.cs"));
            Assert.IsFalse(set.Contains("a.cs"));
        }
    }
}
