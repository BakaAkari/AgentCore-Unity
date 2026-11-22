using System.Collections.Generic;
using AgentCore.Editor.Config;
using AgentCore.Editor.Config.Settings;
using AgentCore.Editor.Config.Settings.Pages;
using NUnit.Framework;
using UnityEditor;

namespace AgentCore.Tests.Editor.Config
{
    /// <summary>
    /// AgentCoreSettingsProvider 深链接导航：一次性 pending section、目标解析、激活/切换生命周期。
    /// </summary>
    [TestFixture]
    public class AgentCoreSettingsProviderTests
    {
        private sealed class FakePage : IAgentCoreSettingsPage
        {
            private readonly List<string> _log;

            public FakePage(string id, int order, List<string> log)
            {
                Id = id;
                Order = order;
                _log = log;
            }

            public string Id { get; }
            public string Title => Id;
            public string Description => Id;
            public int Order { get; }
            public void OnActivate(AgentCoreSettingsContext context) => _log.Add("activate:" + Id);
            public void OnDeactivate(AgentCoreSettingsContext context) => _log.Add("deactivate:" + Id);
            public void Draw(AgentCoreSettingsContext context) { }
        }

        private List<string> _log;

        [SetUp]
        public void SetUp()
        {
            _log = new List<string>();
            AgentCoreSettingsProvider.SetPendingSectionForTests(null);
        }

        [TearDown]
        public void TearDown()
        {
            AgentCoreSettingsProvider.SetPendingSectionForTests(null);
        }

        private AgentCoreSettingsProvider CreateProvider()
        {
            return new AgentCoreSettingsProvider(
                "Preferences/AgentCoreTest",
                SettingsScope.User,
                new IAgentCoreSettingsPage[]
                {
                    new FakePage("first", 100, _log),
                    new FakePage("second", 200, _log),
                    new FakePage("third", 300, _log),
                });
        }

        [Test]
        public void ConsumePendingSectionId_IsOneShot()
        {
            AgentCoreSettingsProvider.SetPendingSectionForTests("second");

            Assert.AreEqual("second", AgentCoreSettingsProvider.ConsumePendingSectionId());
            Assert.IsNull(AgentCoreSettingsProvider.ConsumePendingSectionId());
        }

        [Test]
        public void ResolveTargetSectionId_ReturnsKnownId()
        {
            var provider = CreateProvider();

            Assert.AreEqual("third", provider.ResolveTargetSectionId("third"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("no-such-page")]
        public void ResolveTargetSectionId_FallsBackToFirstPage_WhenNothingSelected(string requested)
        {
            var provider = CreateProvider();

            Assert.AreEqual("first", provider.ResolveTargetSectionId(requested));
        }

        [Test]
        public void ResolveTargetSectionId_FallsBackToCurrentSelection_WhenRequestUnknown()
        {
            var provider = CreateProvider();
            AgentCoreSettingsProvider.SetPendingSectionForTests("second");
            provider.OnActivate(string.Empty, null);

            Assert.AreEqual("second", provider.ResolveTargetSectionId("no-such-page"));
            Assert.AreEqual("second", provider.ResolveTargetSectionId(null));
        }

        [Test]
        public void OnActivate_WithoutPending_ActivatesFirstPage()
        {
            var provider = CreateProvider();

            provider.OnActivate(string.Empty, null);

            Assert.AreEqual("first", provider.SelectedSectionId);
            CollectionAssert.AreEqual(new[] { "activate:first" }, _log);
        }

        [Test]
        public void OnActivate_WithPending_ActivatesTargetPageAndConsumesPending()
        {
            AgentCoreSettingsProvider.SetPendingSectionForTests("third");
            var provider = CreateProvider();

            provider.OnActivate(string.Empty, null);

            Assert.AreEqual("third", provider.SelectedSectionId);
            CollectionAssert.AreEqual(new[] { "activate:third" }, _log);
            Assert.IsNull(AgentCoreSettingsProvider.ConsumePendingSectionId());
        }

        [Test]
        public void ApplyPendingSectionIfAny_SwitchesPagesAndRunsLifecycle()
        {
            var provider = CreateProvider();
            provider.OnActivate(string.Empty, null);
            _log.Clear();

            AgentCoreSettingsProvider.SetPendingSectionForTests("second");
            provider.ApplyPendingSectionIfAny();

            Assert.AreEqual("second", provider.SelectedSectionId);
            CollectionAssert.AreEqual(new[] { "deactivate:first", "activate:second" }, _log);
            Assert.IsNull(AgentCoreSettingsProvider.ConsumePendingSectionId());
        }

        [TestCase("no-such-page")]
        [TestCase("")]
        [TestCase(null)]
        public void ApplyPendingSectionIfAny_IgnoresUnknownOrEmptyId(string pending)
        {
            var provider = CreateProvider();
            provider.OnActivate(string.Empty, null);
            _log.Clear();

            AgentCoreSettingsProvider.SetPendingSectionForTests(pending);
            provider.ApplyPendingSectionIfAny();

            Assert.AreEqual("first", provider.SelectedSectionId);
            Assert.IsEmpty(_log);
        }

        [Test]
        public void SectionIdConstants_MatchBuiltInPageIds()
        {
            Assert.AreEqual(AgentCoreSettingsProvider.DashboardSectionId, new DashboardSettingsPage().Id);
            Assert.AreEqual(AgentCoreSettingsProvider.ModelAgentSectionId, new ModelAgentSettingsPage().Id);
            Assert.AreEqual(AgentCoreSettingsProvider.ContextMemorySectionId, new ContextMemorySettingsPage().Id);
            Assert.AreEqual(AgentCoreSettingsProvider.ToolsExtensionsSectionId, new ToolsExtensionsSettingsPage().Id);
            Assert.AreEqual(AgentCoreSettingsProvider.PermissionsSectionId, new PermissionsSettingsPage().Id);
            Assert.AreEqual(AgentCoreSettingsProvider.WorkspaceSectionId, new WorkspaceSettingsPage().Id);
        }
    }
}
