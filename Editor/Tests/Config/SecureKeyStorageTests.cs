using System;
using NUnit.Framework;
using UnityEditor;
using AgentCore.Editor.Config;

namespace AgentCore.Tests.Editor.Config
{
    /// <summary>
    /// SecureKeyStorage / SecretProtector 的单元测试：DPAPI 加解密往返、旧版明文迁移。
    /// 注意：EditorPrefs 相关用例使用随机 profileId 的临时键，TearDown 中删除。
    /// </summary>
    [TestFixture]
    public class SecureKeyStorageTests
    {
        private string _profileId;

        [SetUp]
        public void SetUp()
        {
            _profileId = "test-" + Guid.NewGuid().ToString("N");
        }

        [TearDown]
        public void TearDown()
        {
            EditorPrefs.DeleteKey(SecureKeyStorage.PROFILE_KEY_PREFIX + _profileId);
        }

        [Test]
        public void SecretProtector_RoundTrip_RestoresPlaintext()
        {
            if (!SecretProtector.IsSupported)
                Assert.Ignore("DPAPI is only available on Windows.");

            const string plain = "sk-测试-1234567890";

            Assert.IsTrue(SecretProtector.TryProtect(plain, out var stored));
            Assert.IsTrue(SecretProtector.IsProtected(stored));
            StringAssert.DoesNotContain(plain, stored);
            Assert.IsTrue(SecretProtector.TryUnprotect(stored, out var restored));
            Assert.AreEqual(plain, restored);
        }

        [Test]
        public void SecretProtector_TamperedValue_FailsWithoutThrowing()
        {
            if (!SecretProtector.IsSupported)
                Assert.Ignore("DPAPI is only available on Windows.");

            Assert.IsFalse(SecretProtector.TryUnprotect(SecretProtector.ProtectedPrefix + "AAAA", out _));
            Assert.IsFalse(SecretProtector.TryUnprotect(SecretProtector.ProtectedPrefix + "not base64!", out _));
        }

        [Test]
        public void Reveal_LegacyPlaintext_ReturnsPlaintextAndOffersMigration()
        {
            var plain = SecureKeyStorage.Reveal("sk-legacy", out var migrated, out var unreadable);

            Assert.AreEqual("sk-legacy", plain);
            Assert.IsFalse(unreadable);
            if (SecretProtector.IsSupported)
            {
                Assert.IsTrue(SecretProtector.IsProtected(migrated));
                Assert.AreEqual("sk-legacy", SecureKeyStorage.Reveal(migrated, out var again, out _));
                Assert.IsNull(again, "已加密的值不应再次迁移");
            }
            else
            {
                Assert.IsNull(migrated);
            }
        }

        [Test]
        public void Reveal_Empty_ReturnsEmpty()
        {
            Assert.AreEqual("", SecureKeyStorage.Reveal("", out var migrated, out var unreadable));
            Assert.IsNull(migrated);
            Assert.IsFalse(unreadable);
            Assert.AreEqual("", SecureKeyStorage.Reveal(null, out _, out _));
        }

        [Test]
        public void GetProfileApiKey_LegacyPlaintextInPrefs_IsReadAndMigratedInPlace()
        {
            var prefKey = SecureKeyStorage.PROFILE_KEY_PREFIX + _profileId;
            EditorPrefs.SetString(prefKey, "sk-old-plaintext");

            Assert.AreEqual("sk-old-plaintext", SecureKeyStorage.GetProfileApiKey(_profileId));

            var raw = EditorPrefs.GetString(prefKey, "");
            if (SecretProtector.IsSupported)
                Assert.IsTrue(SecretProtector.IsProtected(raw), "旧明文应在首次读取后迁移为密文");
            else
                Assert.AreEqual("sk-old-plaintext", raw);

            // 迁移后再次读取仍得到原文
            Assert.AreEqual("sk-old-plaintext", SecureKeyStorage.GetProfileApiKey(_profileId));
        }

        [Test]
        public void SetProfileApiKey_StoresEncryptedOnWindows_AndReadsBack()
        {
            SecureKeyStorage.SetProfileApiKey(_profileId, "sk-new");

            var raw = EditorPrefs.GetString(SecureKeyStorage.PROFILE_KEY_PREFIX + _profileId, "");
            if (SecretProtector.IsSupported)
                Assert.IsTrue(SecretProtector.IsProtected(raw));
            Assert.AreEqual("sk-new", SecureKeyStorage.GetProfileApiKey(_profileId));
            Assert.IsTrue(SecureKeyStorage.HasProfileApiKey(_profileId));
        }

        [Test]
        public void SetProfileApiKey_Empty_ClearsKey()
        {
            SecureKeyStorage.SetProfileApiKey(_profileId, "sk-new");
            SecureKeyStorage.SetProfileApiKey(_profileId, "");

            Assert.AreEqual("", SecureKeyStorage.GetProfileApiKey(_profileId));
            Assert.IsFalse(SecureKeyStorage.HasProfileApiKey(_profileId));
        }
    }
}
