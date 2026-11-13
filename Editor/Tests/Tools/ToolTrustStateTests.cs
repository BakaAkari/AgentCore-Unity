using System;
using AgentCore.Editor.Tools.Safety;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.Tools
{
    /// <summary>
    /// 共享信任状态 <see cref="ToolTrustState"/>：Chat 与 MCP 共用，持久化往返、YOLO 判断、变更事件。
    /// 持久化换成内存替身，不触碰真实 SessionState（避免影响正在使用的编辑器会话）。
    /// </summary>
    [TestFixture]
    public class ToolTrustStateTests
    {
        private Func<string> _origLoad;
        private Action<string> _origSave;
        private string _stored;
        private int _changed;

        [SetUp]
        public void SetUp()
        {
            _origLoad = ToolTrustState.LoadRaw;
            _origSave = ToolTrustState.SaveRaw;
            _stored = string.Empty;
            ToolTrustState.LoadRaw = () => _stored;
            ToolTrustState.SaveRaw = raw => _stored = raw;
            ToolTrustState.ResetForTests();
            ToolTrustState.Changed += OnChanged;
            _changed = 0;
        }

        [TearDown]
        public void TearDown()
        {
            ToolTrustState.Changed -= OnChanged;
            ToolTrustState.LoadRaw = _origLoad;
            ToolTrustState.SaveRaw = _origSave;
            // 回到真实状态
            ToolTrustState.ResetForTests();
            ToolTrustState.EnsureLoaded();
        }

        private void OnChanged() => _changed++;

        [Test]
        public void Add_PersistsAndReloads()
        {
            ToolTrustState.EnsureLoaded();
            ToolTrustState.Add(ToolConfirmationTrustScope.SessionLowMediumRisk);

            Assert.AreEqual("SessionLowMediumRisk", _stored);
            Assert.IsFalse(ToolTrustState.IsYoloActive);

            // 模拟 Domain Reload：内存丢失后从持久化恢复
            ToolTrustState.ResetForTests();
            ToolTrustState.EnsureLoaded();
            CollectionAssert.AreEquivalent(new[] { ToolConfirmationTrustScope.SessionLowMediumRisk }, ToolTrustState.Snapshot());
        }

        [Test]
        public void Yolo_IsReportedFromPersistedState()
        {
            _stored = "SessionAll";
            ToolTrustState.EnsureLoaded();
            Assert.IsTrue(ToolTrustState.IsYoloActive);
            Assert.IsTrue(new SharedToolSessionTrustState().IsYoloActive);
        }

        [Test]
        public void Clear_ErasesAndRaisesChanged()
        {
            ToolTrustState.EnsureLoaded();
            ToolTrustState.Add(ToolConfirmationTrustScope.SessionAll);
            Assert.AreEqual(1, _changed);

            ToolTrustState.Clear();
            Assert.AreEqual(string.Empty, _stored);
            Assert.IsFalse(ToolTrustState.IsYoloActive);
            Assert.AreEqual(2, _changed);
        }

        [Test]
        public void Add_SameScopeTwice_RaisesChangedOnce()
        {
            ToolTrustState.EnsureLoaded();
            ToolTrustState.Add(ToolConfirmationTrustScope.SessionAll);
            ToolTrustState.Add(ToolConfirmationTrustScope.SessionAll);
            Assert.AreEqual(1, _changed);
        }

        [Test]
        public void Parse_IgnoresUnknownTokens()
        {
            CollectionAssert.AreEqual(
                new[] { ToolConfirmationTrustScope.SessionAll },
                ToolTrustState.Parse("Bogus, SessionAll ,"));
        }
    }
}
