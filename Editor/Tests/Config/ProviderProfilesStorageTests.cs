using System.Collections.Generic;
using System.IO;
using System.Text;
using AgentCore.Editor.Config;
using NUnit.Framework;
using UnityEditor;

namespace AgentCore.Tests.Editor.Config
{
    /// <summary>
    /// Provider Profile 本地单文件存储（<c>UserSettings/AgentCore/ProviderProfiles.json</c>）与旧版只读迁移的单元测试。
    /// 全部在临时目录中进行，不触碰工程文件。
    /// </summary>
    [TestFixture]
    public class ProviderProfilesStorageTests
    {
        private string _dir;
        private string _local;
        private string _legacyShared;
        private string _legacyState;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "AgentCoreProfilesStorageTests_" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _local = Path.Combine(_dir, "UserSettings", "AgentCore", "ProviderProfiles.json");
            _legacyShared = Path.Combine(_dir, "ProjectSettings", "AgentCoreProviderProfiles.asset");
            _legacyState = Path.Combine(_dir, "UserSettings", "AgentCore", "ProviderProfilesState.json");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
        }

        // ─── 读写往返 ───

        [Test]
        public void SaveAndLoad_RoundTripsAllFields_IncludingRuntimeState()
        {
            var p = Profile("p1");
            p.modelName = "model-a";
            p.displayName = "本地模型";
            p.extraRequestBody = "{\"foo\":1}";
            p.overrideTemperature = true;
            p.temperature = 0.25f;
            p.fallbackProfileIds.Add("p2");
            p.verifiedConnection = "a\nb";
            p.verifiedFormat = "openai";
            p.verifiedAuth = "bearer";
            p.verifiedCredential = "hash";
            p.connectionFailed = true;
            p.cacheMetricsKnown = true;
            p.cacheHitObserved = true;
            p.lastUsedAtUnixMs = 7;
            var data = new ProviderProfilesLocalData { activeProfileId = "p1", utilityProfileId = "p2" };
            data.profiles.Add(p);
            data.profiles.Add(Profile("p2"));

            Assert.IsTrue(ProviderProfilesStorage.Save(_local, data));
            var back = ProviderProfilesStorage.Load(_local, out bool recovered);

            Assert.IsFalse(recovered);
            Assert.AreEqual("p1", back.activeProfileId);
            Assert.AreEqual("p2", back.utilityProfileId);
            Assert.AreEqual(2, back.profiles.Count);
            var q = back.profiles[0];
            Assert.AreEqual("model-a", q.modelName);
            Assert.AreEqual("本地模型", q.displayName);
            Assert.AreEqual("{\"foo\":1}", q.extraRequestBody);
            Assert.IsTrue(q.overrideTemperature);
            Assert.AreEqual(0.25f, q.temperature, 0.0001f);
            CollectionAssert.AreEqual(new[] { "p2" }, q.fallbackProfileIds);
            Assert.AreEqual("a\nb", q.verifiedConnection);
            Assert.AreEqual("openai", q.verifiedFormat);
            Assert.AreEqual("bearer", q.verifiedAuth);
            Assert.AreEqual("hash", q.verifiedCredential);
            Assert.IsTrue(q.connectionFailed);
            Assert.IsTrue(q.cacheMetricsKnown);
            Assert.IsTrue(q.cacheHitObserved);
            Assert.AreEqual(7, q.lastUsedAtUnixMs);
            Assert.AreEqual(data.ToJson(), back.ToJson(), "序列化结果应稳定，未变时不会重复写盘");
        }

        [Test]
        public void Save_WritesUtf8WithoutBom_AndLfLineEndings()
        {
            var data = new ProviderProfilesLocalData();
            data.profiles.Add(Profile("p1"));

            ProviderProfilesStorage.Save(_local, data);
            var bytes = File.ReadAllBytes(_local);

            Assert.IsFalse(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "不得写 BOM");
            var text = Encoding.UTF8.GetString(bytes);
            StringAssert.DoesNotContain("\r", text);
            StringAssert.EndsWith("\n", text);
        }

        [Test]
        public void Save_SameContent_SkipsWrite_ChangedContent_KeepsBackup()
        {
            var data = new ProviderProfilesLocalData();
            data.profiles.Add(Profile("p1"));
            Assert.IsTrue(ProviderProfilesStorage.Save(_local, data));
            var first = File.ReadAllText(_local);

            Assert.IsFalse(ProviderProfilesStorage.Save(_local, data), "内容未变不写盘");
            Assert.IsFalse(File.Exists(ProviderProfilesStorage.BackupPathFor(_local)));

            data.activeProfileId = "p1";
            Assert.IsTrue(ProviderProfilesStorage.Save(_local, data));
            Assert.AreEqual(first, File.ReadAllText(ProviderProfilesStorage.BackupPathFor(_local)), "上一版保留为 .bak");
        }

        [Test]
        public void Load_MissingFile_ReturnsNull()
        {
            Assert.IsNull(ProviderProfilesStorage.Load(_local, out bool recovered));
            Assert.IsFalse(recovered);
        }

        // ─── 损坏恢复 ───

        [Test]
        public void Load_CorruptFile_KeepsCorruptCopy_AndRecoversFromBackup()
        {
            var data = new ProviderProfilesLocalData { activeProfileId = "p1" };
            data.profiles.Add(Profile("p1"));
            ProviderProfilesStorage.Save(_local, data);
            data.utilityProfileId = "p1";
            ProviderProfilesStorage.Save(_local, data);
            File.WriteAllText(_local, "{ \"profiles\": [ { \"id\": ");

            var back = ProviderProfilesStorage.Load(_local, out bool recovered);

            Assert.IsTrue(recovered);
            Assert.AreEqual("p1", back.activeProfileId);
            Assert.AreEqual("", back.utilityProfileId, "恢复的是上一版");
            Assert.AreEqual("{ \"profiles\": [ { \"id\": ", File.ReadAllText(ProviderProfilesStorage.CorruptPathFor(_local)));

            // 恢复后再保存，.bak 必须保留有效的上一版而不是被损坏的主文件覆盖
            back.utilityProfileId = "p1";
            ProviderProfilesStorage.Save(_local, back);
            var bakAfterSave = ProviderProfilesLocalData.FromJson(File.ReadAllText(ProviderProfilesStorage.BackupPathFor(_local)));
            Assert.AreEqual("p1", bakAfterSave.activeProfileId);
            Assert.AreEqual("", bakAfterSave.utilityProfileId);
        }

        [Test]
        public void LoadOrMigrate_CorruptWithoutBackup_FallsBackToEmptyWithoutLegacy()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_local));
            File.WriteAllText(_local, "not json");

            var data = ProviderProfilesStorage.LoadOrMigrate(_local, _legacyShared, _legacyState, _ => null,
                out bool needsSave, out bool migrated);

            Assert.IsNotNull(data);
            Assert.AreEqual(0, data.profiles.Count);
            Assert.IsFalse(needsSave);
            Assert.IsFalse(migrated);
            Assert.IsTrue(File.Exists(ProviderProfilesStorage.CorruptPathFor(_local)));
        }

        // ─── 旧版只读迁移 ───

        [Test]
        public void LoadOrMigrate_ExistingLocalFile_DoesNotReadLegacy()
        {
            var data = new ProviderProfilesLocalData();
            data.profiles.Add(Profile("local"));
            ProviderProfilesStorage.Save(_local, data);
            WriteFile(_legacyShared, "legacy");
            bool readerCalled = false;

            var back = ProviderProfilesStorage.LoadOrMigrate(_local, _legacyShared, _legacyState,
                _ => { readerCalled = true; return null; }, out bool needsSave, out bool migrated);

            Assert.IsFalse(readerCalled);
            Assert.IsFalse(needsSave);
            Assert.IsFalse(migrated);
            Assert.AreEqual("local", back.profiles[0].id);
        }

        [Test]
        public void LoadOrMigrate_LegacyFiles_AreMergedOnce_AndLeftUntouched()
        {
            WriteFile(_legacyShared, "legacy-asset-bytes");
            WriteFile(_legacyState,
                "{\"schemaVersion\":1,\"activeProfileId\":\"p2\",\"utilityProfileId\":\"\"," +
                "\"profiles\":{\"p1\":{\"verifiedCredential\":\"my-hash\",\"cacheHitObserved\":true,\"lastUsedAtUnixMs\":9}}," +
                "\"modelOverrides\":{\"p1\":\"personal-model\"}}");
            var sharedBytes = File.ReadAllBytes(_legacyShared);
            var stateBytes = File.ReadAllBytes(_legacyState);
            var sharedTime = File.GetLastWriteTimeUtc(_legacyShared);

            var data = ProviderProfilesStorage.LoadOrMigrate(_local, _legacyShared, _legacyState,
                path => { Assert.AreEqual(_legacyShared, path); return LegacyShared(); },
                out bool needsSave, out bool migrated);

            Assert.IsTrue(migrated);
            Assert.IsTrue(needsSave);
            Assert.AreEqual("p2", data.activeProfileId, "个人 active 选择优先于旧团队默认");
            Assert.AreEqual("", data.utilityProfileId, "个人选择\"跟随 active\"优先于旧团队默认");
            var p1 = data.profiles.Find(p => p.id == "p1");
            Assert.AreEqual("personal-model", p1.modelName, "个人模型选择写入 modelName");
            Assert.AreEqual("my-hash", p1.verifiedCredential, "运行时状态覆盖共享文件里的旧值");
            Assert.IsTrue(p1.cacheHitObserved);
            Assert.AreEqual(9, p1.lastUsedAtUnixMs);
            Assert.AreEqual("team-model-b", data.profiles.Find(p => p.id == "p2").modelName);

            Assert.IsFalse(File.Exists(_local), "LoadOrMigrate 本身不写盘");
            CollectionAssert.AreEqual(sharedBytes, File.ReadAllBytes(_legacyShared));
            CollectionAssert.AreEqual(stateBytes, File.ReadAllBytes(_legacyState));
            Assert.AreEqual(sharedTime, File.GetLastWriteTimeUtc(_legacyShared));

            // 写入本地文件后，再次加载不再读取旧文件
            ProviderProfilesStorage.Save(_local, data);
            bool readAgain = false;
            var again = ProviderProfilesStorage.LoadOrMigrate(_local, _legacyShared, _legacyState,
                _ => { readAgain = true; return null; }, out _, out bool migratedAgain);
            Assert.IsFalse(readAgain);
            Assert.IsFalse(migratedAgain);
            Assert.AreEqual(data.ToJson(), again.ToJson());
        }

        [Test]
        public void LoadOrMigrate_NoLegacyFiles_GivesEmptyData()
        {
            var data = ProviderProfilesStorage.LoadOrMigrate(_local, _legacyShared, _legacyState, _ => LegacyShared(),
                out bool needsSave, out bool migrated);

            Assert.AreEqual(0, data.profiles.Count);
            Assert.IsFalse(needsSave);
            Assert.IsFalse(migrated);
        }

        [Test]
        public void MergeLegacy_WithoutUserState_KeepsTeamValues()
        {
            var data = ProviderProfilesStorage.MergeLegacy(LegacyShared(), null);

            Assert.AreEqual("p1", data.activeProfileId);
            Assert.AreEqual("p2", data.utilityProfileId);
            Assert.AreEqual("team-model", data.profiles[0].modelName);
            Assert.AreEqual("teammate-hash", data.profiles[0].verifiedCredential, "旧共享文件内联的运行时字段保留");
        }

        [Test]
        public void MergeLegacy_PersonalChoicePointingToDeletedProfile_FallsBackToTeamValue()
        {
            var state = ProviderProfilesUserState.FromJson(
                "{\"activeProfileId\":\"gone\",\"utilityProfileId\":\"gone\",\"modelOverrides\":{\"p1\":\"  \"}}");

            var data = ProviderProfilesStorage.MergeLegacy(LegacyShared(), state);

            Assert.AreEqual("p1", data.activeProfileId);
            Assert.AreEqual("p2", data.utilityProfileId);
            Assert.AreEqual("team-model", data.profiles[0].modelName, "空白模型选择忽略");
        }

        [Test]
        public void ReadLegacyAsset_UsesUnitySerializer_AndLeavesFileAndSingletonUntouched()
        {
            var store = AgentCoreProviderProfiles.instance;
            var script = MonoScript.FromScriptableObject(store);
            Assume.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(script, out string guid, out long fileId), "需要 MonoScript GUID");

            var yaml =
                "%YAML 1.1\n" +
                "%TAG !u! tag:unity3d.com,2011:\n" +
                "--- !u!114 &1\n" +
                "MonoBehaviour:\n" +
                "  m_ObjectHideFlags: 53\n" +
                "  m_CorrespondingSourceObject: {fileID: 0}\n" +
                "  m_PrefabInstance: {fileID: 0}\n" +
                "  m_PrefabAsset: {fileID: 0}\n" +
                "  m_GameObject: {fileID: 0}\n" +
                "  m_Enabled: 1\n" +
                "  m_EditorHideFlags: 0\n" +
                "  m_Script: {fileID: " + fileId + ", guid: " + guid + ", type: 3}\n" +
                "  m_Name: \n" +
                "  m_EditorClassIdentifier: \n" +
                "  schemaVersion: 1\n" +
                "  profiles:\n" +
                "  - id: legacy-1\n" +
                "    displayName: Legacy\n" +
                "    endpoint: http://legacy.test/v1\n" +
                "    modelName: legacy-model\n" +
                "    verifiedCredential: old-hash\n" +
                "    fallbackProfileIds: []\n" +
                "  activeProfileId: legacy-1\n" +
                "  utilityProfileId: \n";
            WriteFile(_legacyShared, yaml);
            var before = File.ReadAllBytes(_legacyShared);

            var data = AgentCoreProviderProfiles.ReadLegacyAsset(_legacyShared);

            Assert.AreSame(store, AgentCoreProviderProfiles.instance, "单例不得被替换");
            CollectionAssert.AreEqual(before, File.ReadAllBytes(_legacyShared), "旧文件只读");
            Assert.IsNotNull(data);
            Assert.AreEqual("legacy-1", data.activeProfileId);
            Assert.AreEqual(1, data.profiles.Count);
            Assert.AreEqual("legacy-model", data.profiles[0].modelName);
            Assert.AreEqual("http://legacy.test/v1", data.profiles[0].endpoint);
            Assert.AreEqual("old-hash", data.profiles[0].verifiedCredential);
        }

        private static ProviderProfilesLocalData LegacyShared()
        {
            var a = Profile("p1");
            a.modelName = "team-model";
            a.verifiedCredential = "teammate-hash";
            var b = Profile("p2");
            b.modelName = "team-model-b";
            var data = new ProviderProfilesLocalData { activeProfileId = "p1", utilityProfileId = "p2" };
            data.profiles = new List<ProviderProfile> { a, b };
            return data;
        }

        private static void WriteFile(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }

        private static ProviderProfile Profile(string id)
        {
            var p = ProviderProfile.Create(id);
            p.id = id;
            p.endpoint = "http://example.test/v1";
            p.createdAtUnixMs = 100;
            p.lastUsedAtUnixMs = 0;
            return p;
        }
    }
}
