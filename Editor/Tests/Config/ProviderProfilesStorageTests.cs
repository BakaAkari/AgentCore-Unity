using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using AgentCore.Editor.Config;

namespace AgentCore.Tests.Editor.Config
{
    /// <summary>
    /// Provider Profile 分层存储（共享文件 / 每用户状态）纯逻辑的单元测试。
    /// </summary>
    [TestFixture]
    public class ProviderProfilesStorageTests
    {
        // 旧版共享文件的形态：运行时字段混在团队配置里，verifiedConnection 为跨行引号标量。
        private const string LegacyYaml =
            "%YAML 1.1\n" +
            "%TAG !u! tag:unity3d.com,2011:\n" +
            "--- !u!114 &1\n" +
            "MonoBehaviour:\n" +
            "  m_Name: \n" +
            "  schemaVersion: 1\n" +
            "  profiles:\n" +
            "  - id: p1\n" +
            "    displayName: Default\n" +
            "    endpoint: http://example.test/v1\n" +
            "    modelName: model-a\n" +
            "    extraRequestBody: \n" +
            "    verifiedConnection: 'http://example.test/v1\n" +
            "\n" +
            "      model-a\n" +
            "\n" +
            "      auto'\n" +
            "    verifiedFormat: openai\n" +
            "    verifiedAuth: bearer\n" +
            "    verifiedCredential: SGFzaE9mVGhlS2V5\n" +
            "    connectionFailed: 0\n" +
            "    cacheMetricsKnown: 1\n" +
            "    cacheHitObserved: 1\n" +
            "    apiFormat: auto\n" +
            "    fallbackProfileIds: []\n" +
            "    createdAtUnixMs: 100\n" +
            "    lastUsedAtUnixMs: 200\n" +
            "  - id: p2\n" +
            "    displayName: Other\n" +
            "    lastUsedAtUnixMs: 0\n" +
            "  activeProfileId: p1\n" +
            "  utilityProfileId: \n";

        private const string CleanYaml =
            "%YAML 1.1\n" +
            "%TAG !u! tag:unity3d.com,2011:\n" +
            "--- !u!114 &1\n" +
            "MonoBehaviour:\n" +
            "  m_Name: \n" +
            "  schemaVersion: 1\n" +
            "  profiles:\n" +
            "  - id: p1\n" +
            "    displayName: Default\n" +
            "    endpoint: http://example.test/v1\n" +
            "    modelName: model-a\n" +
            "    extraRequestBody: \n" +
            "    apiFormat: auto\n" +
            "    fallbackProfileIds: []\n" +
            "    createdAtUnixMs: 100\n" +
            "  - id: p2\n" +
            "    displayName: Other\n" +
            "  activeProfileId: p1\n" +
            "  utilityProfileId: \n";

        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "AgentCoreProfilesStorageTests_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
        }

        // ─── 共享文件：去除运行时字段 ───

        [Test]
        public void StripVolatileFields_RemovesRuntimeFieldsIncludingMultilineScalars()
        {
            var stripped = ProviderProfilesStorage.StripVolatileFields(LegacyYaml);

            Assert.AreEqual(CleanYaml, stripped);
            StringAssert.DoesNotContain("verifiedCredential", stripped);
            StringAssert.DoesNotContain("SGFzaE9mVGhlS2V5", stripped);
        }

        [Test]
        public void StripVolatileFields_CleanInput_IsUnchanged()
        {
            Assert.AreEqual(CleanYaml, ProviderProfilesStorage.StripVolatileFields(CleanYaml));
        }

        [Test]
        public void StripVolatileFields_VolatileLastLine_KeepsTrailingNewline()
        {
            var yaml = "  - id: p1\n    lastUsedAtUnixMs: 5\n";

            Assert.AreEqual("  - id: p1\n", ProviderProfilesStorage.StripVolatileFields(yaml));
        }

        // ─── 共享文件：未变则跳过 ───

        [Test]
        public void WriteSharedIfChanged_IdenticalBytes_SkipsWrite()
        {
            var path = Path.Combine(_dir, "shared.asset");
            File.WriteAllText(path, CleanYaml);
            var before = File.GetLastWriteTimeUtc(path);
            File.SetLastWriteTimeUtc(path, before.AddMinutes(-5));

            bool written = ProviderProfilesStorage.WriteSharedIfChanged(path, CleanYaml, Path.Combine(_dir, "shared.bak"));

            Assert.IsFalse(written);
            Assert.AreEqual(before.AddMinutes(-5), File.GetLastWriteTimeUtc(path));
            Assert.IsFalse(File.Exists(Path.Combine(_dir, "shared.bak")));
        }

        [Test]
        public void WriteSharedIfChanged_LegacyFileWithSameTeamConfig_SkipsWrite()
        {
            // 旧文件只多出运行时字段 / CRLF 换行：团队配置未变，不应产生 VCS 改动。
            var path = Path.Combine(_dir, "shared.asset");
            File.WriteAllText(path, LegacyYaml.Replace("\n", "\r\n"));

            bool written = ProviderProfilesStorage.WriteSharedIfChanged(path, CleanYaml, null);

            Assert.IsFalse(written);
            Assert.AreEqual(LegacyYaml.Replace("\n", "\r\n"), File.ReadAllText(path));
        }

        [Test]
        public void WriteSharedIfChanged_TeamConfigChanged_WritesCleanContentAndBackup()
        {
            var path = Path.Combine(_dir, "shared.asset");
            var backup = Path.Combine(_dir, "bak", "shared.bak");
            Directory.CreateDirectory(Path.GetDirectoryName(backup));
            File.WriteAllText(path, LegacyYaml);
            var changed = CleanYaml.Replace("model-a", "model-b");

            bool written = ProviderProfilesStorage.WriteSharedIfChanged(path, changed, backup);

            Assert.IsTrue(written);
            Assert.AreEqual(changed, File.ReadAllText(path));
            Assert.AreEqual(LegacyYaml, File.ReadAllText(backup));
        }

        [Test]
        public void WriteSharedIfChanged_MissingFile_IsCreated()
        {
            var path = Path.Combine(_dir, "sub", "shared.asset");

            Assert.IsTrue(ProviderProfilesStorage.WriteSharedIfChanged(path, CleanYaml, Path.Combine(_dir, "x.bak")));
            Assert.AreEqual(CleanYaml, File.ReadAllText(path));
        }

        [Test]
        public void WriteTextIfChanged_SameContent_SkipsSecondWrite()
        {
            var path = Path.Combine(_dir, "state.json");

            Assert.IsTrue(ProviderProfilesStorage.WriteTextIfChanged(path, "{}\n"));
            Assert.IsFalse(ProviderProfilesStorage.WriteTextIfChanged(path, "{}\n"));
            Assert.IsTrue(ProviderProfilesStorage.WriteTextIfChanged(path, "{ }\n"));
        }

        // ─── 用户状态：迁移与合并 ───

        [Test]
        public void MergeOnLoad_LegacyRuntimeFields_AreImportedIntoUserState()
        {
            var p = Profile("p1");
            p.verifiedConnection = "conn";
            p.verifiedFormat = "openai";
            p.verifiedAuth = "bearer";
            p.verifiedCredential = "hash";
            p.cacheMetricsKnown = true;
            p.lastUsedAtUnixMs = 42;
            var clean = Profile("p2");
            var state = new ProviderProfilesUserState();

            bool imported = ProviderProfilesStorage.MergeOnLoad(new[] { p, clean }, state);

            Assert.IsTrue(imported);
            Assert.IsTrue(state.profiles.ContainsKey("p1"));
            Assert.IsFalse(state.profiles.ContainsKey("p2"), "没有旧数据的 profile 不需要迁移条目");
            var s = state.profiles["p1"];
            Assert.AreEqual("conn", s.verifiedConnection);
            Assert.AreEqual("hash", s.verifiedCredential);
            Assert.IsTrue(s.cacheMetricsKnown);
            Assert.AreEqual(42, s.lastUsedAtUnixMs);
            // 内存中的值保持不变
            Assert.AreEqual("conn", p.verifiedConnection);
        }

        [Test]
        public void MergeOnLoad_UserStateWinsOverSharedLegacyValues()
        {
            var p = Profile("p1");
            p.verifiedCredential = "teammate-hash";
            p.lastUsedAtUnixMs = 1;
            var state = new ProviderProfilesUserState();
            state.profiles["p1"] = new ProviderProfileRuntimeState { verifiedCredential = "my-hash", lastUsedAtUnixMs = 99 };

            bool imported = ProviderProfilesStorage.MergeOnLoad(new[] { p }, state);

            Assert.IsFalse(imported);
            Assert.AreEqual("my-hash", p.verifiedCredential);
            Assert.AreEqual(99, p.lastUsedAtUnixMs);
        }

        [Test]
        public void MergeOnLoad_EmptyUserEntry_StillWins()
        {
            // 用户清空过验证结果（条目全默认）后，共享文件里残留的旧值不能被重新导入。
            var p = Profile("p1");
            p.verifiedCredential = "stale";
            var state = new ProviderProfilesUserState();
            state.profiles["p1"] = new ProviderProfileRuntimeState();

            ProviderProfilesStorage.MergeOnLoad(new[] { p }, state);

            Assert.IsNull(p.verifiedCredential);
        }

        [Test]
        public void CaptureAll_KeepsEntryForEveryProfile()
        {
            var a = Profile("a");
            a.connectionFailed = true;
            var b = Profile("b");
            var state = new ProviderProfilesUserState();

            ProviderProfilesStorage.CaptureAll(new List<ProviderProfile> { a, b, null }, state);

            Assert.IsTrue(state.profiles["a"].connectionFailed);
            Assert.IsTrue(state.profiles.ContainsKey("b"));
        }

        [Test]
        public void UserState_JsonRoundTrip_PreservesValues()
        {
            var state = new ProviderProfilesUserState { activeProfileId = "p2" };
            state.profiles["p1"] = new ProviderProfileRuntimeState
            {
                verifiedConnection = "a\nb",
                verifiedCredential = "hash",
                cacheHitObserved = true,
                lastUsedAtUnixMs = 7
            };

            var json = state.ToJson();
            var back = ProviderProfilesUserState.FromJson(json);

            Assert.AreEqual("p2", back.activeProfileId);
            Assert.AreEqual("a\nb", back.profiles["p1"].verifiedConnection);
            Assert.AreEqual("hash", back.profiles["p1"].verifiedCredential);
            Assert.IsTrue(back.profiles["p1"].cacheHitObserved);
            Assert.AreEqual(7, back.profiles["p1"].lastUsedAtUnixMs);
            Assert.AreEqual(json, back.ToJson(), "序列化结果应稳定，未变时不会重复写盘");
        }

        [Test]
        public void UserState_FromJson_EmptyOrMissingProfiles_GivesEmptyState()
        {
            Assert.IsNull(ProviderProfilesUserState.FromJson("").activeProfileId);
            Assert.AreEqual(0, ProviderProfilesUserState.FromJson("{\"schemaVersion\":1}").profiles.Count);
        }

        [Test]
        public void RuntimeState_Clear_ResetsAllVolatileFields()
        {
            var p = Profile("p1");
            p.verifiedConnection = "c";
            p.verifiedFormat = "openai";
            p.verifiedAuth = "bearer";
            p.verifiedCredential = "h";
            p.connectionFailed = true;
            p.cacheMetricsKnown = true;
            p.cacheHitObserved = true;
            p.lastUsedAtUnixMs = 5;

            ProviderProfileRuntimeState.Clear(p);

            Assert.IsTrue(ProviderProfileRuntimeState.Capture(p).IsEmpty);
            Assert.AreEqual("http://example.test/v1", p.endpoint, "团队配置字段不受影响");
        }

        [Test]
        public void ModelOverride_NoOverride_FallsBackToSharedModelName()
        {
            var p = Profile("p1");
            p.modelName = "team-model";
            Assert.AreEqual("team-model", ProviderProfilesStorage.ResolveModelName(p, new ProviderProfilesUserState()));
            Assert.AreEqual("team-model", ProviderProfilesStorage.ResolveModelName(p, null));
            Assert.IsNull(ProviderProfilesStorage.ResolveModelName(null, new ProviderProfilesUserState()));
        }

        [Test]
        public void ModelOverride_Set_ResolvesPersonalChoice_AndLeavesSharedModelUntouched()
        {
            var p = Profile("p1");
            p.modelName = "team-model";
            var state = new ProviderProfilesUserState();

            Assert.IsTrue(ProviderProfilesStorage.SetModelOverride(state, p, "my-model"));
            Assert.AreEqual("my-model", ProviderProfilesStorage.ResolveModelName(p, state));
            Assert.AreEqual("team-model", p.modelName, "个人选择不得改写共享 modelName");
            Assert.IsFalse(ProviderProfilesStorage.SetModelOverride(state, p, "my-model"), "重复选择不算变化");
        }

        [Test]
        public void ModelOverride_SelectingTeamDefaultOrEmpty_ClearsOverride()
        {
            var p = Profile("p1");
            p.modelName = "team-model";
            var state = new ProviderProfilesUserState();

            ProviderProfilesStorage.SetModelOverride(state, p, "my-model");
            Assert.IsTrue(ProviderProfilesStorage.SetModelOverride(state, p, "team-model"));
            Assert.AreEqual(0, state.modelOverrides.Count);
            Assert.AreEqual("team-model", ProviderProfilesStorage.ResolveModelName(p, state));

            ProviderProfilesStorage.SetModelOverride(state, p, "my-model");
            Assert.IsTrue(ProviderProfilesStorage.SetModelOverride(state, p, "  "));
            Assert.AreEqual(0, state.modelOverrides.Count);
            Assert.IsFalse(ProviderProfilesStorage.SetModelOverride(state, p, null), "无覆盖时清除不算变化");
        }

        [Test]
        public void ModelOverride_JsonRoundTrip_AndOmittedWhenEmpty()
        {
            var p = Profile("p1");
            p.modelName = "team-model";
            var state = new ProviderProfilesUserState();
            StringAssert.DoesNotContain("modelOverrides", state.ToJson(), "无个人选择时不写字段，旧状态文件不因此改动");

            ProviderProfilesStorage.SetModelOverride(state, p, "my-model");
            var json = state.ToJson();
            StringAssert.Contains("modelOverrides", json);

            var back = ProviderProfilesUserState.FromJson(json);
            Assert.AreEqual("my-model", ProviderProfilesStorage.ResolveModelName(p, back));
            Assert.AreEqual(json, back.ToJson());
        }

        [Test]
        public void ModelOverride_FromJson_DropsBlankValues_AndLegacyFileHasNone()
        {
            var state = ProviderProfilesUserState.FromJson("{\"modelOverrides\":{\"p1\":\"  \",\"p2\":\"m\"}}");
            Assert.AreEqual(1, state.modelOverrides.Count);
            Assert.AreEqual("m", state.modelOverrides["p2"]);

            var legacy = ProviderProfilesUserState.FromJson("{\"schemaVersion\":1,\"activeProfileId\":\"p1\"}");
            Assert.IsNotNull(legacy.modelOverrides);
            Assert.AreEqual(0, legacy.modelOverrides.Count);
        }

        [Test]
        public void UtilityOverride_PersonalChoiceWins_AndMissingProfileFallsBackToTeamDefault()
        {
            var state = new ProviderProfilesUserState();
            bool Exists(string id) => id == "p1" || id == "p2";

            Assert.AreEqual("p1", ProviderProfilesStorage.ResolveUtilityProfileId("p1", state, Exists), "无个人选择跟随团队默认");

            Assert.IsTrue(ProviderProfilesStorage.SetUtilityOverride(state, "p1", "p2"));
            Assert.AreEqual("p2", ProviderProfilesStorage.ResolveUtilityProfileId("p1", state, Exists));

            Assert.IsTrue(ProviderProfilesStorage.SetUtilityOverride(state, "p1", ""));
            Assert.AreEqual("", ProviderProfilesStorage.ResolveUtilityProfileId("p1", state, Exists), "个人选择\"跟随 active\"也优先于团队默认");

            state.utilityProfileId = "deleted";
            Assert.AreEqual("p1", ProviderProfilesStorage.ResolveUtilityProfileId("p1", state, Exists));
        }

        [Test]
        public void UtilityOverride_SelectingTeamDefault_ClearsOverride_AndIsOmittedFromJson()
        {
            var state = new ProviderProfilesUserState();
            StringAssert.DoesNotContain("utilityProfileId", state.ToJson(), "无个人选择时不写字段");

            ProviderProfilesStorage.SetUtilityOverride(state, "p1", "p2");
            var back = ProviderProfilesUserState.FromJson(state.ToJson());
            Assert.AreEqual("p2", back.utilityProfileId);

            Assert.IsTrue(ProviderProfilesStorage.SetUtilityOverride(state, "p1", "p1"));
            Assert.IsNull(state.utilityProfileId);
            Assert.IsFalse(ProviderProfilesStorage.SetUtilityOverride(state, "p1", "p1"), "无覆盖时再选团队默认不算变化");
            Assert.IsFalse(ProviderProfilesStorage.SetUtilityOverride(state, "", null), "团队默认为空时选\"跟随 active\"即团队默认");
        }

        private static ProviderProfile Profile(string id)
        {
            var p = ProviderProfile.Create(id);
            p.id = id;
            p.endpoint = "http://example.test/v1";
            p.lastUsedAtUnixMs = 0;
            return p;
        }
    }
}
