#if AGENTCORE_MCP
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Components.MCP.Bridge;
using AgentCore.Editor.Core;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.MCP
{
    /// <summary>外部活动记录：MCP 调用开始 / 结束都记录来源、工具、参数与结果，供 Chat 活动条与外部活动窗口显示。</summary>
    [TestFixture]
    public class McpActivityTests
    {
        [SetUp]
        public void SetUp() => ExternalToolActivity.Clear();

        [Test]
        public void Records_BeginEnd_AndCapacity()
        {
            var changed = 0;
            void OnChanged() => Interlocked.Increment(ref changed);
            ExternalToolActivity.Changed += OnChanged;
            try
            {
                var record = ExternalToolActivity.Begin("claude-code", "manage_scene", "get_hierarchy", "{}");
                Assert.IsTrue(ExternalToolActivity.Running().Contains(record));
                ExternalToolActivity.End(record, "ok", "done");
                Assert.IsFalse(record.IsRunning);
                Assert.AreEqual("ok", record.Status);
                Assert.AreEqual(2, changed);

                for (var i = 0; i < ExternalToolActivity.Capacity + 10; i++)
                    ExternalToolActivity.End(ExternalToolActivity.Begin("c", "t", null, null), "ok", null);
                Assert.AreEqual(ExternalToolActivity.Capacity, ExternalToolActivity.Snapshot().Count);
            }
            finally
            {
                ExternalToolActivity.Changed -= OnChanged;
            }
        }

        [Test]
        public async Task McpCall_IsRecordedWithClientAndResult()
        {
            var outcome = await McpToolExecutor.ExecuteAsync("read_console", new JObject { ["action"] = "get_count" }, "activity-test", CancellationToken.None);
            Assert.AreEqual(McpBridgeProtocol.StatusOk, outcome.Status, outcome.Text);

            var record = ExternalToolActivity.Snapshot().First();
            Assert.AreEqual("activity-test", record.Source);
            Assert.AreEqual("read_console", record.Tool);
            Assert.AreEqual("get_count", record.Action);
            Assert.AreEqual("ok", record.Status);
            StringAssert.Contains("get_count", record.ArgumentsSummary);
            Assert.IsNotEmpty(record.ResultPreview);
        }
    }
}
#endif
