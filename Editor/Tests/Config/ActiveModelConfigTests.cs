using AgentCore.Editor.Config;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.Config
{
    /// <summary>
    /// v1.13.0 Batch A：<see cref="ActiveModelConfig"/> 解析优先级 + <see cref="ProviderProfile"/> /
    /// <see cref="SecureKeyStorage"/> profile-scoped API 单元测试。
    /// <para>
    /// 注意：这些用例会临时操作真实的 <see cref="AgentCoreProviderProfiles"/> 单例与 EditorPrefs。
    /// <see cref="Setup"/>/<see cref="TearDown"/> 快照并还原 activeProfileId 及临时 profile，
    /// 保证测试运行后不改变工程既有配置。
    /// </para>
    /// </summary>
    [TestFixture]
    public class ActiveModelConfigTests
    {
        private string _originalActiveId;
        private ProviderProfile _temp;

        [SetUp]
        public void Setup()
        {
            _originalActiveId = AgentCoreProviderProfiles.instance.ActiveProfileId;

            _temp = ProviderProfile.Create("__test_profile__");
            _temp.endpoint = "http://test.local:9999/v1";
            _temp.modelName = "test-model-xyz";
            AgentCoreProviderProfiles.instance.AddProfile(_temp);
        }

        [TearDown]
        public void TearDown()
        {
            // 还原 active 指向 + 清掉临时 profile（RemoveProfile 会一并清 EditorPrefs key）。
            AgentCoreProviderProfiles.instance.SetActive(_originalActiveId);
            if (_temp != null)
                AgentCoreProviderProfiles.instance.RemoveProfile(_temp.id);
        }

        // ─── 无 profile（v1.13.0：抛异常，无 legacy fallthrough）───

        [Test]
        public void NoActiveProfile_EndpointThrows()
        {
            AgentCoreProviderProfiles.instance.SetActive("");

            Assert.IsFalse(ActiveModelConfig.IsUsingProfile);
            Assert.Throws<System.InvalidOperationException>(() => { var _ = ActiveModelConfig.Endpoint; });
            Assert.Throws<System.InvalidOperationException>(() => { var _ = ActiveModelConfig.ModelName; });
            Assert.Throws<System.InvalidOperationException>(() => { var _ = ActiveModelConfig.ApiKey; });
        }

        // ─── 有 profile 但 override=false → fallthrough ─────────

        [Test]
        public void ActiveProfile_TemperatureNotOverridden_FallsThroughToGlobal()
        {
            AgentCoreProviderProfiles.instance.UpdateProfile(_temp.id, p => p.overrideTemperature = false);
            AgentCoreProviderProfiles.instance.SetActive(_temp.id);

            Assert.IsTrue(ActiveModelConfig.IsUsingProfile);
            // endpoint 走 profile，但 temperature 未覆盖 → 全局默认
            Assert.AreEqual(_temp.endpoint, ActiveModelConfig.Endpoint);
            Assert.AreEqual(AgentCoreSettings.instance.temperature, ActiveModelConfig.Temperature);
        }

        // ─── 有 profile 且 override=true → 用 profile 值 ─────────

        [Test]
        public void ActiveProfile_TemperatureOverridden_UsesProfileValue()
        {
            AgentCoreProviderProfiles.instance.UpdateProfile(_temp.id, p =>
            {
                p.overrideTemperature = true;
                p.temperature = 0.123f;
            });
            AgentCoreProviderProfiles.instance.SetActive(_temp.id);

            Assert.AreEqual(0.123f, ActiveModelConfig.Temperature, 0.0001f);
        }

        [Test]
        public void ActiveProfile_MaxTokensOverridden_UsesProfileValue()
        {
            AgentCoreProviderProfiles.instance.UpdateProfile(_temp.id, p =>
            {
                p.overrideMaxTokens = true;
                p.maxTokens = 4321;
            });
            AgentCoreProviderProfiles.instance.SetActive(_temp.id);

            Assert.AreEqual(4321, ActiveModelConfig.MaxTokens);
        }

        // ─── IsUsingProfile 切换 ────────────────────────────────

        [Test]
        public void IsUsingProfile_TogglesWithActiveId()
        {
            AgentCoreProviderProfiles.instance.SetActive(_temp.id);
            Assert.IsTrue(ActiveModelConfig.IsUsingProfile);

            AgentCoreProviderProfiles.instance.SetActive("");
            Assert.IsFalse(ActiveModelConfig.IsUsingProfile);
        }

        // ─── extraRequestBody：空 = fallthrough ─────────────────

        [Test]
        public void ExtraRequestBody_EmptyOnProfile_FallsThroughToGlobal()
        {
            AgentCoreProviderProfiles.instance.UpdateProfile(_temp.id, p => p.extraRequestBody = "");
            AgentCoreProviderProfiles.instance.SetActive(_temp.id);

            Assert.AreEqual(AgentCoreSettings.instance.extraRequestBody, ActiveModelConfig.ExtraRequestBody);
        }

        [Test]
        public void ExtraRequestBody_NonEmptyOnProfile_UsesProfileValue()
        {
            AgentCoreProviderProfiles.instance.UpdateProfile(_temp.id, p => p.extraRequestBody = "{\"foo\":1}");
            AgentCoreProviderProfiles.instance.SetActive(_temp.id);

            Assert.AreEqual("{\"foo\":1}", ActiveModelConfig.ExtraRequestBody);
        }

        // ─── ProviderProfile.Create 工厂 ────────────────────────

        [Test]
        public void Create_GeneratesUniqueIdAndTimestamp()
        {
            var a = ProviderProfile.Create("A");
            var b = ProviderProfile.Create("B");

            Assert.IsFalse(string.IsNullOrEmpty(a.id));
            Assert.AreNotEqual(a.id, b.id);
            Assert.AreEqual("A", a.displayName);
            Assert.Greater(a.createdAtUnixMs, 0L);
            // 新建 profile 各 override 位默认 false
            Assert.IsFalse(a.overrideTemperature);
            Assert.IsFalse(a.overrideMaxTokens);
            Assert.IsFalse(a.overrideReasoning);
        }

        // ─── SecureKeyStorage profile-scoped API ────────────────

        [Test]
        public void SecureKeyStorage_ProfileKeyRoundTrip()
        {
            var id = _temp.id;
            SecureKeyStorage.SetProfileApiKey(id, "sk-test-123");
            Assert.IsTrue(SecureKeyStorage.HasProfileApiKey(id));
            Assert.AreEqual("sk-test-123", SecureKeyStorage.GetProfileApiKey(id));

            SecureKeyStorage.DeleteProfileApiKey(id);
            Assert.IsFalse(SecureKeyStorage.HasProfileApiKey(id));
            Assert.AreEqual("", SecureKeyStorage.GetProfileApiKey(id));
        }

        [Test]
        public void SecureKeyStorage_EmptyProfileId_IsSafeNoOp()
        {
            SecureKeyStorage.SetProfileApiKey("", "ignored");
            Assert.AreEqual("", SecureKeyStorage.GetProfileApiKey(""));
            Assert.IsFalse(SecureKeyStorage.HasProfileApiKey(""));
        }
    }
}
