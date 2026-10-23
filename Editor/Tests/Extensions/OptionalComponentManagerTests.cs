using NUnit.Framework;
using AgentCore.Editor.Extensions;

namespace AgentCore.Tests.Editor.Extensions
{
    /// <summary>
    /// OptionalComponentManager.ApplyDefine 的单元测试：先比较再写，已是目标状态时不产生改动。
    /// </summary>
    [TestFixture]
    public class OptionalComponentManagerTests
    {
        [Test]
        public void ApplyDefine_AlreadyPresent_ReturnsSameString()
        {
            const string raw = "FOO;AGENTCORE_VCS;BAR";

            var result = OptionalComponentManager.ApplyDefine(raw, "AGENTCORE_VCS", true);

            Assert.AreSame(raw, result);
        }

        [Test]
        public void ApplyDefine_AlreadyAbsent_ReturnsSameString()
        {
            const string raw = "FOO;BAR";

            var result = OptionalComponentManager.ApplyDefine(raw, "AGENTCORE_VCS", false);

            Assert.AreSame(raw, result);
        }

        [Test]
        public void ApplyDefine_Add_KeepsExistingOrder()
        {
            var result = OptionalComponentManager.ApplyDefine("ZED;ALPHA", "AGENTCORE_VCS", true);

            Assert.AreEqual("ZED;ALPHA;AGENTCORE_VCS", result);
        }

        [Test]
        public void ApplyDefine_Remove_KeepsExistingOrder()
        {
            var result = OptionalComponentManager.ApplyDefine("ZED;AGENTCORE_VCS;ALPHA", "AGENTCORE_VCS", false);

            Assert.AreEqual("ZED;ALPHA", result);
        }

        [Test]
        public void ApplyDefine_EmptyOrNull_AddsDefine()
        {
            Assert.AreEqual("AGENTCORE_VCS", OptionalComponentManager.ApplyDefine("", "AGENTCORE_VCS", true));
            Assert.AreEqual("AGENTCORE_VCS", OptionalComponentManager.ApplyDefine(null, "AGENTCORE_VCS", true));
        }
    }
}
