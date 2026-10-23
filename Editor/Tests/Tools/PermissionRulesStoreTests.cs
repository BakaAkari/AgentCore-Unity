using System;
using System.IO;
using AgentCore.Editor.Tools.Safety;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.Tools
{
    /// <summary>
    /// 权限规则文件存储（用户 / 项目分层、本地停用、只在显式修改时写盘）的单元测试。
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

        [Test]
        public void AddRule_WritesToScopeFile_AndRoundTrips()
        {
            var store = new PermissionRulesStore(_root);
            var user = store.AddRule(PermissionRuleScope.User, Rule("deny", "manage_asset.delete", "Assets/Plugins/**"));
            var project = store.AddRule(PermissionRuleScope.Project, Rule("ask", "manage_script.*"));

            Assert.IsFalse(string.IsNullOrEmpty(user.id));
            Assert.IsTrue(File.Exists(UserFile));
            Assert.IsTrue(File.Exists(ProjectFile));
            StringAssert.Contains("\"deny\"", File.ReadAllText(UserFile));
            StringAssert.DoesNotContain("manage_script", File.ReadAllText(UserFile));
            StringAssert.Contains("manage_script.*", File.ReadAllText(ProjectFile));
            StringAssert.DoesNotContain("\r\n", File.ReadAllText(UserFile));

            var reloaded = new PermissionRulesStore(_root);
            var active = reloaded.GetActiveRules();
            Assert.AreEqual(2, active.Count);
            Assert.AreEqual(PermissionRuleScope.User, active[0].Scope);
            Assert.AreEqual(user.id, active[0].Rule.id);
            Assert.AreEqual("Assets/Plugins/**", active[0].Rule.path);
            Assert.AreEqual(PermissionRuleScope.Project, active[1].Scope);
            Assert.AreEqual(project.id, active[1].Rule.id);
        }

        [Test]
        public void DisableProjectRuleLocally_OnlyTouchesUserFile()
        {
            var store = new PermissionRulesStore(_root);
            var project = store.AddRule(PermissionRuleScope.Project, Rule("deny", "execute_code"));
            var projectBytes = File.ReadAllBytes(ProjectFile);

            store.SetProjectRuleDisabledLocally(project.id, true);

            Assert.AreEqual(0, store.GetActiveRules().Count);
            Assert.IsTrue(store.IsProjectRuleDisabledLocally(project.id));
            CollectionAssert.AreEqual(projectBytes, File.ReadAllBytes(ProjectFile));
            StringAssert.Contains(project.id, File.ReadAllText(UserFile));

            // 全新实例（模拟重启）同样生效
            var reloaded = new PermissionRulesStore(_root);
            Assert.AreEqual(0, reloaded.GetActiveRules().Count);
            Assert.AreEqual(1, reloaded.GetRules(PermissionRuleScope.Project).Count);

            reloaded.SetProjectRuleDisabledLocally(project.id, false);
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
        public void UpdateRule_InPlace_And_MoveBetweenScopes()
        {
            var store = new PermissionRulesStore(_root);
            var added = store.AddRule(PermissionRuleScope.User, Rule("ask", "manage_file"));

            added.effect = "allow";
            Assert.IsTrue(store.UpdateRule(PermissionRuleScope.User, added));
            Assert.AreEqual("allow", store.GetRules(PermissionRuleScope.User)[0].effect);

            Assert.IsTrue(store.UpdateRule(PermissionRuleScope.User, added, PermissionRuleScope.Project));
            Assert.AreEqual(0, store.GetRules(PermissionRuleScope.User).Count);
            Assert.AreEqual(1, store.GetRules(PermissionRuleScope.Project).Count);
            Assert.AreEqual(1, new PermissionRulesStore(_root).GetRules(PermissionRuleScope.Project).Count);
        }

        [Test]
        public void RemoveProjectRule_AlsoClearsLocalDisable()
        {
            var store = new PermissionRulesStore(_root);
            var project = store.AddRule(PermissionRuleScope.Project, Rule("deny", "*"));
            store.SetProjectRuleDisabledLocally(project.id, true);

            Assert.IsTrue(store.RemoveRule(PermissionRuleScope.Project, project.id));
            Assert.AreEqual(0, store.GetRules(PermissionRuleScope.Project).Count);
            Assert.IsFalse(store.IsProjectRuleDisabledLocally(project.id));
            StringAssert.DoesNotContain(project.id, File.ReadAllText(UserFile));
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
