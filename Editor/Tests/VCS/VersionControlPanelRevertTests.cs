using System.Collections.Generic;
using NUnit.Framework;
using AgentCore.Editor.Components.VCS.Tools;
using AgentCore.Editor.Components.VCS.UI;

namespace AgentCore.Tests.Editor.VCS
{
    /// <summary>面板 Revert All 的文件划分：未版本控制 / 忽略的文件不传给 svn revert，并在结果消息中报告为跳过。</summary>
    [TestFixture]
    public class VersionControlPanelRevertTests
    {
        [Test]
        public void PartitionRevertAll_SkipsUnversionedAndIgnored_KeepsEverythingElse()
        {
            var files = new List<VcsFileStatus>
            {
                new VcsFileStatus { FilePath = "Assets/a.txt", State = VcsFileState.Modified },
                new VcsFileStatus { FilePath = "Assets/new.txt", State = VcsFileState.Untracked },
                new VcsFileStatus { FilePath = "Assets/b.txt", State = VcsFileState.Added },
                new VcsFileStatus { FilePath = "Assets/ignored.tmp", State = VcsFileState.Ignored },
                new VcsFileStatus { FilePath = "Assets/copy.txt", State = VcsFileState.Copied },
                new VcsFileStatus { FilePath = "Assets/gone.txt", State = VcsFileState.Missing },
                new VcsFileStatus { FilePath = "Assets/c.txt", State = VcsFileState.Conflicted },
                null,
                new VcsFileStatus { FilePath = "", State = VcsFileState.Modified }
            };

            var (revertable, skipped) = VersionControlPanel.PartitionRevertAll(files);

            CollectionAssert.AreEqual(new[] { "Assets/a.txt", "Assets/b.txt", "Assets/copy.txt", "Assets/gone.txt", "Assets/c.txt" }, revertable);
            CollectionAssert.AreEqual(new[] { "Assets/new.txt", "Assets/ignored.tmp" }, skipped);
        }

        [Test]
        public void PartitionRevertAll_NullInput_ReturnsEmptyLists()
        {
            var (revertable, skipped) = VersionControlPanel.PartitionRevertAll(null);
            Assert.IsEmpty(revertable);
            Assert.IsEmpty(skipped);
        }

        [Test]
        public void DescribeSkippedUnversioned_ListsCountAndFirstPaths()
        {
            Assert.AreEqual(string.Empty, VersionControlPanel.DescribeSkippedUnversioned(new List<string>()));

            var two = VersionControlPanel.DescribeSkippedUnversioned(new[] { "Assets/x.txt", "Assets/中文 y.txt" });
            StringAssert.Contains("Skipped 2 unversioned file(s)", two);
            StringAssert.Contains("Assets/x.txt, Assets/中文 y.txt", two);

            var many = new List<string>();
            for (var i = 0; i < 8; i++)
                many.Add($"f{i}");
            var text = VersionControlPanel.DescribeSkippedUnversioned(many);
            StringAssert.Contains("Skipped 8 unversioned file(s)", text);
            StringAssert.Contains("f4 and 3 more", text);
            StringAssert.DoesNotContain("f5", text);
        }
    }
}
