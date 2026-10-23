using System.Collections.Generic;
using AgentCore.Editor.Tools.Safety;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.Tools
{
    /// <summary>
    /// 权限规则匹配器（glob / 路径规范化 / 优先级）的单元测试。
    /// </summary>
    [TestFixture]
    public class PermissionRuleMatcherTests
    {
        private static PermissionRule Rule(string effect, string tool, string action = null, string path = null, bool enabled = true, string id = null)
        {
            return new PermissionRule { id = id ?? effect + "-" + tool, effect = effect, tool = tool, action = action, path = path, enabled = enabled };
        }

        private static List<ScopedPermissionRule> Scoped(params PermissionRule[] rules)
        {
            var list = new List<ScopedPermissionRule>();
            foreach (var r in rules)
                list.Add(new ScopedPermissionRule(r, PermissionRuleScope.User));
            return list;
        }

        #region glob

        [TestCase("manage_asset", "manage_asset", true)]
        [TestCase("manage_asset", "MANAGE_ASSET", true)]
        [TestCase("manage_asset", "manage_assets", false)]
        [TestCase("manage_*", "manage_script", true)]
        [TestCase("*", "anything", true)]
        [TestCase("*", "", true)]
        [TestCase("manage_?cene", "manage_scene", true)]
        [TestCase("read_*", "manage_file", false)]
        [TestCase("a.b", "aXb", false)]
        public void GlobMatch_NameMode(string pattern, string text, bool expected)
        {
            Assert.AreEqual(expected, PermissionRuleMatcher.GlobMatch(pattern, text, pathMode: false));
        }

        [TestCase("Assets/Plugins/**", "Assets/Plugins/a.dll", true)]
        [TestCase("Assets/Plugins/**", "Assets/Plugins/x/y/z.cs", true)]
        [TestCase("Assets/Plugins/**", "Assets/Plugins", true)]
        [TestCase("Assets/Plugins/**", "Assets/PluginsOther/a.cs", false)]
        [TestCase("Assets/Plugins/**", "Assets/Other/Plugins/a.cs", false)]
        [TestCase("Assets/*.cs", "Assets/a.cs", true)]
        [TestCase("Assets/*.cs", "Assets/sub/a.cs", false)]
        [TestCase("**/*.cs", "a.cs", true)]
        [TestCase("**/*.cs", "Assets/x/y/a.cs", true)]
        [TestCase("**/*.cs", "Assets/x/y/a.cso", false)]
        [TestCase("Assets/**/Editor/*.cs", "Assets/Editor/a.cs", true)]
        [TestCase("Assets/**/Editor/*.cs", "Assets/x/y/Editor/a.cs", true)]
        [TestCase("Assets/**/Editor/*.cs", "Assets/x/Editor/sub/a.cs", false)]
        [TestCase("assets/plugins/**", "Assets/Plugins/a.dll", true)]
        [TestCase("Assets/a?.txt", "Assets/ab.txt", true)]
        [TestCase("Assets/a?.txt", "Assets/a/.txt", false)]
        [TestCase("Assets/[x].txt", "Assets/[x].txt", true)]
        [TestCase("**/**", "anything/at/all", true)]
        public void GlobMatch_PathMode(string pattern, string text, bool expected)
        {
            Assert.AreEqual(expected, PermissionRuleMatcher.GlobMatch(pattern, text, pathMode: true));
        }

        #endregion

        #region 路径规范化

        [TestCase(@"Assets\Plugins\a.dll", null, "Assets/Plugins/a.dll")]
        [TestCase("./Assets/a.cs", null, "Assets/a.cs")]
        [TestCase("Assets/x/../Plugins/a.cs", null, "Assets/Plugins/a.cs")]
        [TestCase("D:/Proj/Assets/a.cs", "D:/Proj", "Assets/a.cs")]
        [TestCase(@"D:\Proj\Assets\a.cs", @"D:\Proj\", "Assets/a.cs")]
        [TestCase("d:/proj/Assets/a.cs", "D:/Proj", "Assets/a.cs")]
        [TestCase("D:/Other/a.cs", "D:/Proj", "D:/Other/a.cs")]
        [TestCase("D:/Proj/../Proj/Assets/a.cs", "D:/Proj", "Assets/a.cs")]
        public void NormalizePath(string raw, string root, string expected)
        {
            Assert.AreEqual(expected, PermissionRuleMatcher.NormalizePath(raw, root));
        }

        [Test]
        public void NormalizePath_Blank_ReturnsNull()
        {
            Assert.IsNull(PermissionRuleMatcher.NormalizePath("  ", "D:/Proj"));
        }

        #endregion

        #region 单条规则匹配

        [Test]
        public void Matches_ToolActionShorthand()
        {
            var rule = Rule("deny", "manage_asset.delete");
            Assert.IsTrue(PermissionRuleMatcher.Matches(rule, PermissionRuleEffect.Deny, "manage_asset", "delete", null));
            Assert.IsFalse(PermissionRuleMatcher.Matches(rule, PermissionRuleEffect.Deny, "manage_asset", "create", null));
            Assert.IsFalse(PermissionRuleMatcher.Matches(rule, PermissionRuleEffect.Deny, "manage_asset", "", null));
        }

        [Test]
        public void Matches_ActionGlob()
        {
            var rule = Rule("ask", "manage_script.*");
            Assert.IsTrue(PermissionRuleMatcher.Matches(rule, PermissionRuleEffect.Ask, "manage_script", "create", null));
            Assert.IsTrue(PermissionRuleMatcher.Matches(rule, PermissionRuleEffect.Ask, "manage_script", "", null));
            Assert.IsFalse(PermissionRuleMatcher.Matches(rule, PermissionRuleEffect.Ask, "manage_scene", "create", null));
        }

        [Test]
        public void Matches_SeparateActionField()
        {
            var rule = Rule("deny", "manage_*", action: "delete*");
            Assert.IsTrue(PermissionRuleMatcher.Matches(rule, PermissionRuleEffect.Deny, "manage_file", "delete_file", null));
            Assert.IsFalse(PermissionRuleMatcher.Matches(rule, PermissionRuleEffect.Deny, "manage_file", "read_file", null));
        }

        [Test]
        public void Matches_EmptyToolMeansAny()
        {
            var rule = Rule("ask", "", path: "Assets/Secret/**");
            Assert.IsTrue(PermissionRuleMatcher.Matches(rule, PermissionRuleEffect.Ask, "whatever", "", new[] { "Assets/Secret/a.txt" }));
        }

        [Test]
        public void Matches_PathRule_NoPaths_DoesNotMatch()
        {
            var rule = Rule("deny", "*", path: "Assets/Plugins/**");
            Assert.IsFalse(PermissionRuleMatcher.Matches(rule, PermissionRuleEffect.Deny, "manage_file", "write_file", null));
            Assert.IsFalse(PermissionRuleMatcher.Matches(rule, PermissionRuleEffect.Deny, "manage_file", "write_file", new string[0]));
        }

        [Test]
        public void Matches_DenyAndAsk_AnyPath_AllowNeedsAllPaths()
        {
            var paths = new[] { "Assets/Sandbox/a.cs", "Assets/Core/a.cs" };
            Assert.IsTrue(PermissionRuleMatcher.Matches(Rule("deny", "*", path: "Assets/Sandbox/**"), PermissionRuleEffect.Deny, "t", "", paths));
            Assert.IsTrue(PermissionRuleMatcher.Matches(Rule("ask", "*", path: "Assets/Sandbox/**"), PermissionRuleEffect.Ask, "t", "", paths));
            Assert.IsFalse(PermissionRuleMatcher.Matches(Rule("allow", "*", path: "Assets/Sandbox/**"), PermissionRuleEffect.Allow, "t", "", paths));
            Assert.IsTrue(PermissionRuleMatcher.Matches(Rule("allow", "*", path: "Assets/**"), PermissionRuleEffect.Allow, "t", "", paths));
        }

        [Test]
        public void Matches_BackslashPattern_Normalized()
        {
            var rule = Rule("deny", "*", path: @"Assets\Plugins\**");
            Assert.IsTrue(PermissionRuleMatcher.Matches(rule, PermissionRuleEffect.Deny, "t", "", new[] { "Assets/Plugins/a.dll" }));
        }

        #endregion

        #region 优先级

        [Test]
        public void Evaluate_NoRules_ReturnsNone()
        {
            Assert.IsFalse(PermissionRuleMatcher.Evaluate(null, "t", "a", null).HasMatch);
            Assert.IsFalse(PermissionRuleMatcher.Evaluate(new List<ScopedPermissionRule>(), "t", "a", null).HasMatch);
        }

        [Test]
        public void Evaluate_DenyBeatsAskBeatsAllow_RegardlessOfOrder()
        {
            var rules = Scoped(
                Rule("allow", "*", id: "a"),
                Rule("ask", "manage_asset", id: "k"),
                Rule("deny", "manage_asset.delete", id: "d"));

            var m1 = PermissionRuleMatcher.Evaluate(rules, "manage_asset", "delete", null);
            Assert.AreEqual(PermissionRuleEffect.Deny, m1.Effect);
            Assert.AreEqual("d", m1.Rule.id);

            var m2 = PermissionRuleMatcher.Evaluate(rules, "manage_asset", "create", null);
            Assert.AreEqual(PermissionRuleEffect.Ask, m2.Effect);
            Assert.AreEqual("k", m2.Rule.id);

            var m3 = PermissionRuleMatcher.Evaluate(rules, "manage_scene", "create", null);
            Assert.AreEqual(PermissionRuleEffect.Allow, m3.Effect);
            Assert.AreEqual("a", m3.Rule.id);
        }

        [Test]
        public void Evaluate_DisabledAndInvalidRules_Ignored()
        {
            var rules = Scoped(
                Rule("deny", "*", enabled: false),
                Rule("block", "*"),
                Rule(null, "*"));
            Assert.IsFalse(PermissionRuleMatcher.Evaluate(rules, "anything", "", null).HasMatch);
        }

        [Test]
        public void Evaluate_ProjectAndUserRulesShareOnePrecedence()
        {
            var rules = new List<ScopedPermissionRule>
            {
                new ScopedPermissionRule(Rule("allow", "manage_file", id: "u"), PermissionRuleScope.User),
                new ScopedPermissionRule(Rule("deny", "manage_file", id: "p"), PermissionRuleScope.Project)
            };
            var match = PermissionRuleMatcher.Evaluate(rules, "manage_file", "write_file", null);
            Assert.AreEqual(PermissionRuleEffect.Deny, match.Effect);
            Assert.AreEqual(PermissionRuleScope.Project, match.Scope);
        }

        [Test]
        public void Describe_ContainsMatcherScopeIdAndNote()
        {
            var rule = Rule("deny", "manage_asset", action: "delete", path: "Assets/Plugins/**", id: "r1");
            rule.note = "third-party code";
            var text = new PermissionRuleMatch(rule, PermissionRuleScope.Project, PermissionRuleEffect.Deny).Describe();
            StringAssert.Contains("deny", text);
            StringAssert.Contains("manage_asset.delete @ Assets/Plugins/**", text);
            StringAssert.Contains("project", text);
            StringAssert.Contains("r1", text);
            StringAssert.Contains("third-party code", text);
        }

        #endregion
    }
}
