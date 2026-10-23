using AgentCore.Editor.Core;
using AgentCore.Editor.Session;
using AgentCore.Editor.Tools;
using AgentCore.Editor.UI.Components;
using Newtonsoft.Json;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.Core
{
    /// <summary>
    /// 工具部分成功状态（事件 / 卡片状态 / 会话持久化）与 LLM 重试提示事件的单元测试。
    /// </summary>
    [TestFixture]
    public class ToolStatusAndRetryEventTests
    {
        [Test]
        public void ResolveFinalStatus_MapsSuccessPartialAndFailure()
        {
            Assert.AreEqual(ToolCallStatus.Completed, ToolCallCard.ResolveFinalStatus(true, false, "ok"));
            Assert.AreEqual(ToolCallStatus.Partial, ToolCallCard.ResolveFinalStatus(true, true, "ok"));
            Assert.AreEqual(ToolCallStatus.Failed, ToolCallCard.ResolveFinalStatus(false, false, "boom"));
            Assert.AreEqual(ToolCallStatus.Failed, ToolCallCard.ResolveFinalStatus(false, true, "boom"), "失败优先于部分成功");
        }

        [Test]
        public void ResolveFinalStatus_OldSessionWithoutFlag_DetectsPartialPrefix()
        {
            var content = ToolResult.Partial("3/5 done").GetContentForLLM();
            Assert.AreEqual(ToolCallStatus.Partial, ToolCallCard.ResolveFinalStatus(true, false, content));
            Assert.AreEqual(ToolCallStatus.Completed, ToolCallCard.ResolveFinalStatus(true, false, null));
        }

        [Test]
        public void ToolCallCompleted_CarriesPartialFlag()
        {
            Assert.IsTrue(AgentEvent.ToolCallCompleted("t", "r", 1, isPartial: true).IsPartial);
            Assert.IsFalse(AgentEvent.ToolCallCompleted("t", "r", 1).IsPartial);
        }

        [Test]
        public void LlmRetryNotice_IsNotAnErrorEvent()
        {
            var evt = AgentEvent.LlmRetryNotice("Retrying (1/3)...");
            Assert.AreEqual(AgentEventType.LlmRetryNotice, evt.Type);
            Assert.AreNotEqual(AgentEventType.Error, evt.Type);
            Assert.AreEqual("Retrying (1/3)...", evt.Content);
            Assert.AreEqual(string.Empty, AgentEvent.LlmRetryNotice(null).Content);
        }

        [Test]
        public void SerializableToolCallInfo_PartialFlagRoundTrips_AndIsOmittedWhenFalse()
        {
            var partial = new SerializableToolCallInfo { Id = "c1", ToolName = "t", Success = true, IsPartial = true };
            var json = JsonConvert.SerializeObject(partial);
            StringAssert.Contains("\"partial\":true", json);
            Assert.IsTrue(JsonConvert.DeserializeObject<SerializableToolCallInfo>(json).IsPartial);

            var full = new SerializableToolCallInfo { Id = "c2", ToolName = "t", Success = true };
            StringAssert.DoesNotContain("partial", JsonConvert.SerializeObject(full), "旧会话格式不变");
            Assert.IsFalse(JsonConvert.DeserializeObject<SerializableToolCallInfo>("{\"id\":\"c3\",\"success\":true}").IsPartial);
        }
    }
}
