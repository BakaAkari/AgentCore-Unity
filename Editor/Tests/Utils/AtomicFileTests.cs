using System.IO;
using System.Text;
using NUnit.Framework;
using AgentCore.Editor.Utils;

namespace AgentCore.Tests.Editor.Utils
{
    /// <summary>
    /// AtomicFile 原子写入的单元测试。
    /// </summary>
    [TestFixture]
    public class AtomicFileTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "AgentCoreAtomicFileTests_" + System.Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
        }

        [Test]
        public void WriteAllText_NewFileInMissingDirectory_IsCreated()
        {
            var path = Path.Combine(_dir, "sub", "a.json");

            AtomicFile.WriteAllText(path, "{}");

            Assert.AreEqual("{}", File.ReadAllText(path));
        }

        [Test]
        public void WriteAllText_ExistingFile_IsReplacedWithoutLeftovers()
        {
            var path = Path.Combine(_dir, "a.json");
            AtomicFile.WriteAllText(path, "old content that is longer");

            AtomicFile.WriteAllText(path, "new");

            Assert.AreEqual("new", File.ReadAllText(path));
            CollectionAssert.AreEqual(new[] { path }, Directory.GetFiles(_dir));
        }

        [Test]
        public void WriteAllText_WithBackupPath_KeepsPreviousVersion()
        {
            var path = Path.Combine(_dir, "a.json");
            var backup = path + ".bak";
            AtomicFile.WriteAllText(path, "v1", backupPath: backup);
            Assert.IsFalse(File.Exists(backup), "first write has nothing to back up");

            AtomicFile.WriteAllText(path, "v2", backupPath: backup);
            AtomicFile.WriteAllText(path, "v3", backupPath: backup);

            Assert.AreEqual("v3", File.ReadAllText(path));
            Assert.AreEqual("v2", File.ReadAllText(backup));
        }

        [Test]
        public void WriteAllText_DefaultEncoding_HasNoBom_ExplicitUtf8_HasBom()
        {
            var plain = Path.Combine(_dir, "plain.txt");
            var bom = Path.Combine(_dir, "bom.txt");

            AtomicFile.WriteAllText(plain, "x");
            AtomicFile.WriteAllText(bom, "x", Encoding.UTF8);

            CollectionAssert.AreEqual(new byte[] { (byte)'x' }, File.ReadAllBytes(plain));
            CollectionAssert.AreEqual(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'x' }, File.ReadAllBytes(bom));
        }
    }
}
