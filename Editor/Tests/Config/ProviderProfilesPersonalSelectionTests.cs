using System.IO;
using AgentCore.Editor.Config;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.Config
{
    /// <summary>
    /// 设置页 / chat 面板的个人选择（active profile、模型、辅助任务 profile）只写每用户状态文件，
    /// 不改团队共享的 <c>ProjectSettings/AgentCoreProviderProfiles.asset</c>；明确的"设为团队默认"操作才写共享文件。
    /// <para>
    /// 这些用例操作真实的 <see cref="AgentCoreProviderProfiles"/> 单例：<see cref="SetUp"/> 记录团队默认与个人选择，
    /// <see cref="TearDown"/> 还原并删除临时 profile，最后把共享文件与用户状态文件恢复为测试前的字节，
    /// 保证测试运行后工程配置与版本库状态不变。
    /// </para>
    /// </summary>
    [TestFixture]
    public class ProviderProfilesPersonalSelectionTests
    {
        private const string SharedPath = "ProjectSettings/AgentCoreProviderProfiles.asset";

        private byte[] _sharedBefore;
        private byte[] _userStateBefore;
        private string _teamActive;
        private string _teamUtility;
        private string _personalActive;
        private string _personalUtility;
        private ProviderProfile _a;
        private ProviderProfile _b;

        private static AgentCoreProviderProfiles Store => AgentCoreProviderProfiles.instance;

        [SetUp]
        public void SetUp()
        {
            _sharedBefore = File.Exists(SharedPath) ? File.ReadAllBytes(SharedPath) : null;
            _userStateBefore = File.Exists(ProviderProfilesStorage.UserStatePath)
                ? File.ReadAllBytes(ProviderProfilesStorage.UserStatePath)
                : null;
            _teamActive = Store.TeamDefaultProfileId;
            _teamUtility = Store.TeamDefaultUtilityProfileId;
            _personalActive = Store.ActiveProfileId;
            _personalUtility = Store.UtilityProfileId;

            _a = ProviderProfile.Create("__test_personal_a__");
            _a.endpoint = "http://test.local:9998/v1";
            _a.modelName = "team-model";
            Store.AddProfile(_a);
            _b = ProviderProfile.Create("__test_personal_b__");
            _b.endpoint = "http://test.local:9997/v1";
            _b.modelName = "team-model-b";
            Store.AddProfile(_b);

            // 团队默认 = A、辅助任务跟随 active，个人选择清空：之后的断言不依赖工程原有配置
            Store.SetTeamDefaults(_a.id, "");
        }

        [TearDown]
        public void TearDown()
        {
            Store.SetTeamDefaults(_teamActive, _teamUtility);
            Store.SetActive(_personalActive);
            Store.SetUtility(_personalUtility);
            if (_a != null) Store.RemoveProfile(_a.id);
            if (_b != null) Store.RemoveProfile(_b.id);

            Restore(SharedPath, _sharedBefore);
            Restore(ProviderProfilesStorage.UserStatePath, _userStateBefore);
        }

        [Test]
        public void PersonalChoices_OnlyWriteUserState_SharedFileUnchanged()
        {
            var shared = File.ReadAllBytes(SharedPath);

            Store.SetActive(_b.id);
            Store.SetUtility(_a.id);
            Store.SetUserModel(_a.id, "personal-model");

            CollectionAssert.AreEqual(shared, File.ReadAllBytes(SharedPath), "个人选择不得改写共享文件");
            Assert.AreEqual(_a.id, Store.TeamDefaultProfileId);
            Assert.AreEqual("", Store.TeamDefaultUtilityProfileId);
            Assert.AreEqual("team-model", _a.modelName, "共享 modelName 不变");

            var userState = File.ReadAllText(ProviderProfilesStorage.UserStatePath);
            StringAssert.Contains("personal-model", userState);
            StringAssert.Contains("\"utilityProfileId\": \"" + _a.id + "\"", userState);
            StringAssert.Contains("\"activeProfileId\": \"" + _b.id + "\"", userState);
        }

        [Test]
        public void PersonalOverlay_WinsOverTeamDefault_AndSelectingTeamDefaultFollowsItAgain()
        {
            Store.SetActive(_b.id);
            Store.SetUtility(_b.id);
            Store.SetUserModel(_a.id, "personal-model");

            Assert.AreEqual(_b.id, Store.ActiveProfileId);
            Assert.AreEqual(_b.id, Store.UtilityProfileId);
            Assert.AreEqual("personal-model", Store.GetEffectiveModelName(_a));
            Assert.IsTrue(Store.HasPersonalSelection);

            Store.SetActive(_a.id);
            Store.SetUtility("");
            Store.SetUserModel(_a.id, "team-model");

            Assert.AreEqual(_a.id, Store.ActiveProfileId);
            Assert.AreEqual("", Store.UtilityProfileId);
            Assert.AreEqual("team-model", Store.GetEffectiveModelName(_a));
            Assert.IsFalse(Store.HasPersonalSelection);
        }

        [Test]
        public void ExplicitTeamDefaultActions_WriteSharedFile_AndClearPersonalChoice()
        {
            Store.SetUserModel(_a.id, "new-team-model");
            Store.SetTeamDefaultModel(_a.id, "new-team-model");

            Assert.AreEqual("new-team-model", _a.modelName);
            StringAssert.Contains("new-team-model", File.ReadAllText(SharedPath));

            Store.SetActive(_b.id);
            Store.SetUtility(_a.id);
            Store.SetTeamDefaults(Store.ActiveProfileId, Store.UtilityProfileId);

            Assert.AreEqual(_b.id, Store.TeamDefaultProfileId);
            Assert.AreEqual(_a.id, Store.TeamDefaultUtilityProfileId);
            Assert.IsFalse(Store.HasPersonalSelection);
            var shared = File.ReadAllText(SharedPath);
            StringAssert.Contains("activeProfileId: " + _b.id, shared);
            StringAssert.Contains("utilityProfileId: " + _a.id, shared);
        }

        private static void Restore(string path, byte[] bytes)
        {
            if (bytes != null)
                File.WriteAllBytes(path, bytes);
            else if (File.Exists(path))
                File.Delete(path);
        }
    }
}
