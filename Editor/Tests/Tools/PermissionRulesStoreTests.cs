using System;
using System.IO;
using AgentCore.Editor.Tools.Safety;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.Tools
{
    /// <summary>
    /// 权限规则文件存储（仅写用户文件、遗留项目文件只读、本地停用、只在显式修改时写盘）的单元测试。
    /// 全部使用临时目录，不触碰真实项目。
    /// </summary>
    [TestFixture]
    public class PermissionRulesStoreTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "AgentCorePermRules_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (Directory.Exists(_root))
                    Directory.Delete(_root, true);
            }
            catch
            {
                // 临时目录清理失败不影响测试结果
            }
        }

        private string UserFile => Path.Combine(_root, PermissionRulesStore.UserRelativePath);
        private string ProjectFile => Path.Combine(_root, PermissionRulesStore.ProjectRelativePath);

        private static PermissionRule Rule(string effect, string tool, string path = null)
            => new PermissionRule { effect = effect, tool = tool, path = path, enabled = true };

        [Test]
        public void NoFiles_NoRules_NothingWritten()
        {
            var store = new PermissionRulesStore(_root);
            Assert.AreEqual(0, store.GetActiveRules().Count);
            Assert.IsFalse(File.Exists(UserFile));
            Assert.IsFalse(File.Exists(ProjectFile));
            Assert.IsFalse(Directory.Exists(Path.Combine(_root, "UserSettings")));
        }

        private void WriteLegacyProjectFile(string json)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProjectFile));
            File.WriteAllText(ProjectFile, json);
        }

        private const string LegacyProjectJson =
            "{ \"version\": 1, \"rules\": [ { \"id\": \"p1\", \"effect\": \"ask\", \"tool\": \"manage_script.*\" } ] }";

        [Test]
        public void AddRule_WritesUserFileOnly_AndRoundTrips()
        {
            var store = new PermissionRulesStore(_root);
            var user = store.AddRule(PermissionRuleScope.User, Rule("deny", "manage_asset.delete", "Assets/Plugins/**"));

            Assert.IsFalse(string.IsNullOrEmpty(user.id));
            Assert.IsTrue(File.Exists(UserFile));
            Assert.IsFalse(Directory.Exists(Path.Combine(_root, "ProjectSettings")));
            Assert.IsFalse(store.ProjectFileExists);
            StringAssert.Contains("\"deny\"", File.ReadAllText(UserFile));
            StringAssert.DoesNotContain("\r\n", File.ReadAllText(UserFile));

            var active = new PermissionRulesStore(_root).GetActiveRules();
            Assert.AreEqual(1, active.Count);
            Assert.AreEqual(PermissionRuleScope.User, active[0].Scope);
            Assert.AreEqual(user.id, active[0].Rule.id);
            Assert.AreEqual("Assets/Plugins/**", active[0].Rule.path);
        }

        [Test]
        public void ProjectScopeWrites_Throw_AndNeverCreateProjectSettings()
        {
            var store = new PermissionRulesStore(_root);
            var user = store.AddRule(PermissionRuleScope.User, Rule("ask", "manage_file"));

            Assert.Throws<InvalidOperationException>(() => store.AddRule(PermissionRuleScope.Project, Rule("deny", "*")));
            Assert.Throws<InvalidOperationException>(() => store.UpdateRule(PermissionRuleScope.Project, user));
            Assert.Throws<InvalidOperationException>(() => store.RemoveRule(PermissionRuleScope.Project, user.id));

            Assert.IsFalse(Directory.Exists(Path.Combine(_root, "ProjectSettings")));
            Assert.AreEqual(1, store.GetRules(PermissionRuleScope.User).Count);
        }

        [Test]
        public void LegacyProjectFile_ReadOnlyLoaded_AndNeverModified()
        {
            WriteLegacyProjectFile(LegacyProjectJson);
            var projectBytes = File.ReadAllBytes(ProjectFile);

            var store = new PermissionRulesStore(_root);
            Assert.IsTrue(store.ProjectFileExists);
            var user = store.AddRule(PermissionRuleScope.User, Rule("deny", "execute_code"));
            store.SetProjectRuleDisabledLocally("p1", true);
            store.SetProjectRuleDisabledLocally("p1", false);
            user.effect = "ask";
            store.UpdateRule(PermissionRuleScope.User, user);
            store.RemoveRule(PermissionRuleScope.User, user.id);
            Assert.Throws<InvalidOperationException>(() => store.RemoveRule(PermissionRuleScope.Project, "p1"));

            CollectionAssert.AreEqual(projectBytes, File.ReadAllBytes(ProjectFile));
            var active = store.GetActiveRules();
            Assert.AreEqual(1, active.Count);
            Assert.AreEqual(PermissionRuleScope.Project, active[0].Scope);
            Assert.AreEqual("p1", active[0].Rule.id);
        }

        [Test]
        public void DisableProjectRuleLocally_OnlyTouchesUserFile()
        {
            WriteLegacyProjectFile(LegacyProjectJson);
            var projectBytes = File.ReadAllBytes(ProjectFile);
            var store = new PermissionRulesStore(_root);

            store.SetProjectRuleDisabledLocally("p1", true);

            Assert.AreEqual(0, store.GetActiveRules().Count);
            Assert.IsTrue(store.IsProjectRuleDisabledLocally("p1"));
            CollectionAssert.AreEqual(projectBytes, File.ReadAllBytes(ProjectFile));
            StringAssert.Contains("p1", File.ReadAllText(UserFile));

            // 全新实例（模拟重启）同样生效
            var reloaded = new PermissionRulesStore(_root);
            Assert.AreEqual(0, reloaded.GetActiveRules().Count);
            Assert.AreEqual(1, reloaded.GetRules(PermissionRuleScope.Project).Count);

            reloaded.SetProjectRuleDisabledLocally("p1", false);
            Assert.AreEqual(1, reloaded.GetActiveRules().Count);
        }

        [Test]
        public void DisabledRule_NotActive_ButListed()
        {
            var store = new PermissionRulesStore(_root);
            var r = Rule("deny", "*");
            r.enabled = false;
            store.AddRule(PermissionRuleScope.User, r);
            Assert.AreEqual(0, store.GetActiveRules().Count);
            Assert.AreEqual(1, store.GetRules(PermissionRuleScope.User).Count);
        }

        [Test]
        public void UpdateAndRemoveRule_InPlace()
        {
            var store = new PermissionRulesStore(_root);
            var added = store.AddRule(PermissionRuleScope.User, Rule("ask", "manage_file"));

            added.effect = "allow";
            Assert.IsTrue(store.UpdateRule(PermissionRuleScope.User, added));
            Assert.AreEqual("allow", new PermissionRulesStore(_root).GetRules(PermissionRuleScope.User)[0].effect);

            Assert.IsTrue(store.RemoveRule(PermissionRuleScope.User, added.id));
            Assert.AreEqual(0, new PermissionRulesStore(_root).GetRules(PermissionRuleScope.User).Count);
            Assert.IsFalse(Directory.Exists(Path.Combine(_root, "ProjectSettings")));
        }

        [Test]
        public void ExternalEdit_PickedUpWithoutReload()
        {
            var store = new PermissionRulesStore(_root);
            Assert.AreEqual(0, store.GetActiveRules().Count);

            Directory.CreateDirectory(Path.GetDirectoryName(ProjectFile));
            File.WriteAllText(ProjectFile, "{ \"version\": 1, \"rules\": [ { \"id\": \"t1\", \"effect\": \"deny\", \"tool\": \"manage_build\" } ] }");

            var active = store.GetActiveRules();
            Assert.AreEqual(1, active.Count);
            Assert.AreEqual("t1", active[0].Rule.id);
            Assert.IsTrue(active[0].Rule.enabled, "缺省 enabled 视为启用");
        }

        [Test]
        public void CorruptFile_TreatedAsNoRules_AndBackedUpOnSave()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UserFile));
            File.WriteAllText(UserFile, "{ this is not json");

            var store = new PermissionRulesStore(_root);
            Assert.AreEqual(0, store.GetActiveRules().Count);
            Assert.IsNotNull(store.UserLoadError);

            store.AddRule(PermissionRuleScope.User, Rule("allow", "read_console"));
            Assert.AreEqual(1, new PermissionRulesStore(_root).GetActiveRules().Count);
            var backup = Path.Combine(_root, "Library/AgentCore/PermissionRules.user.json.bak");
            Assert.IsTrue(File.Exists(backup));
            StringAssert.Contains("this is not json", File.ReadAllText(backup));
        }

        [Test]
        public void ReturnedRulesAreCopies()
        {
            var store = new PermissionRulesStore(_root);
            store.AddRule(PermissionRuleScope.User, Rule("ask", "a"));
            store.GetRules(PermissionRuleScope.User)[0].effect = "deny";
            store.GetActiveRules()[0].Rule.effect = "deny";
            Assert.AreEqual("ask", store.GetRules(PermissionRuleScope.User)[0].effect);
        }
    }
}
