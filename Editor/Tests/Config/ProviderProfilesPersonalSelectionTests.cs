using System.IO;
using AgentCore.Editor.Config;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.Config
{
    /// <summary>
    /// <see cref="AgentCoreProviderProfiles"/> 的所有修改（增删改、切换 active / 辅助任务 profile、切换模型）
    /// 只写本地 <c>UserSettings/AgentCore/ProviderProfiles.json</c>，不创建或修改 <c>ProjectSettings/AgentCoreProviderProfiles.asset</c>。
    /// <para>
    /// 用例操作真实单例，但 <see cref="SetUp"/> 把 <see cref="AgentCoreProviderProfiles.LocalPath"/> 指向临时文件并重新加载，
    /// <see cref="TearDown"/> 还原路径并从真实文件重新加载，工程配置不受影响。
    /// </para>
    /// </summary>
    [TestFixture]
    public class ProviderProfilesPersonalSelectionTests
    {
        private const string LegacySharedPath = ProviderProfilesStorage.LegacySharedPath;

        private string _originalLocalPath;
        private string _dir;
        private string _local;
        private bool _legacyExisted;
        private byte[] _legacyBytes;
        private System.DateTime _legacyTime;
        private ProviderProfile _a;
        private ProviderProfile _b;

        private static AgentCoreProviderProfiles Store => AgentCoreProviderProfiles.instance;

        [SetUp]
        public void SetUp()
        {
            _legacyExisted = File.Exists(LegacySharedPath);
            if (_legacyExisted)
            {
                _legacyBytes = File.ReadAllBytes(LegacySharedPath);
                _legacyTime = File.GetLastWriteTimeUtc(LegacySharedPath);
            }

            _originalLocalPath = AgentCoreProviderProfiles.LocalPath;
            _dir = Path.Combine(Path.GetTempPath(), "AgentCoreProfilesLocalTests_" + System.Guid.NewGuid().ToString("N"));
            _local = Path.Combine(_dir, "ProviderProfiles.json");
            // 预先写空文件：不触发旧版迁移，断言不依赖工程原有配置
            ProviderProfilesStorage.Save(_local, new ProviderProfilesLocalData());
            AgentCoreProviderProfiles.LocalPath = _local;
            Store.ReloadFromDisk();

            _a = ProviderProfile.Create("__test_local_a__");
            _a.endpoint = "http://test.local:9998/v1";
            _a.modelName = "model-a";
            Store.AddProfile(_a);
            _b = ProviderProfile.Create("__test_local_b__");
            _b.endpoint = "http://test.local:9997/v1";
            _b.modelName = "model-b";
            Store.AddProfile(_b);
        }

        [TearDown]
        public void TearDown()
        {
            AgentCoreProviderProfiles.LocalPath = _originalLocalPath;
            Store.ReloadFromDisk();
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
        }

        [Test]
        public void AllMutations_WriteLocalFile_AndNeverTouchProjectSettings()
        {
            Store.SetActive(_b.id);
            Store.SetUtility(_a.id);
            Store.SetUserModel(_a.id, "picked-model");
            Store.UpdateProfile(_b.id, p => p.displayName = "renamed");

            var json = File.ReadAllText(_local);
            StringAssert.Contains("picked-model", json);
            StringAssert.Contains("renamed", json);
            StringAssert.Contains("\"activeProfileId\": \"" + _b.id + "\"", json);
            StringAssert.Contains("\"utilityProfileId\": \"" + _a.id + "\"", json);

            Store.RemoveProfile(_b.id);
            _b = null;
            StringAssert.DoesNotContain("__test_local_b__", File.ReadAllText(_local));

            AssertLegacySharedUntouched();
        }

        [Test]
        public void SetActiveAndUtility_ApplyDirectly_AndSurviveReload()
        {
            Store.SetActive(_b.id);
            Store.SetUtility(_a.id);
            Store.SetUserModel(_a.id, "picked-model");

            Store.ReloadFromDisk();

            Assert.AreEqual(_b.id, Store.ActiveProfileId);
            Assert.AreEqual(_a.id, Store.UtilityProfileId);
            var a = Store.FindById(_a.id);
            Assert.AreEqual("picked-model", a.modelName, "模型选择直接写入 modelName");
            Assert.AreEqual("picked-model", Store.GetEffectiveModelName(a));
            AssertLegacySharedUntouched();
        }

        [Test]
        public void SetUserModel_BlankOrUnknownProfile_IsNoOp()
        {
            var before = File.ReadAllText(_local);

            Store.SetUserModel(_a.id, "  ");
            Store.SetUserModel("missing-id", "x");
            Store.SetUserModel(_a.id, "model-a");

            Assert.AreEqual("model-a", _a.modelName);
            Assert.AreEqual(before, File.ReadAllText(_local), "无变化不写盘");
        }

        [Test]
        public void RemovingActiveAndUtilityProfile_ClearsSelection()
        {
            Store.SetActive(_a.id);
            Store.SetUtility(_a.id);

            Store.RemoveProfile(_a.id);
            _a = null;

            Assert.AreEqual("", Store.ActiveProfileId);
            Assert.AreEqual("", Store.UtilityProfileId);
            Assert.IsNull(Store.GetUtility());
        }

        [Test]
        public void SetUtility_UnknownId_FollowsActive()
        {
            Store.SetUtility("missing-id");

            Assert.AreEqual("", Store.UtilityProfileId);
        }

        private void AssertLegacySharedUntouched()
        {
            Assert.AreEqual(_legacyExisted, File.Exists(LegacySharedPath), "不得创建或删除 ProjectSettings 文件");
            if (_legacyExisted)
            {
                CollectionAssert.AreEqual(_legacyBytes, File.ReadAllBytes(LegacySharedPath), "不得修改 ProjectSettings 文件");
                Assert.AreEqual(_legacyTime, File.GetLastWriteTimeUtc(LegacySharedPath));
            }
        }
    }
}
