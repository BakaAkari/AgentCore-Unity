using AgentCore.Editor.Core;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.Core
{
    /// <summary>
    /// VisiblePlanningTraceExtractor 的单元测试。
    /// </summary>
    [TestFixture]
    public class VisiblePlanningTraceExtractorTests
    {
        [Test]
        public void FinalizeContent_WithVisiblePlanningTrace_ExtractsReasoningAndContent()
        {
            var raw = "---THINKING---\nplan\n---ACTION---\nfinal";

            var result = VisiblePlanningTraceExtractor.FinalizeContent(raw);

            Assert.AreEqual(VisiblePlanningTraceState.Completed, result.State);
            Assert.AreEqual("plan", result.Reasoning);
            Assert.AreEqual("final", result.Content);
        }

        [Test]
        public void FinalizeContent_WithCodeFenceMarker_KeepsRawContent()
        {
            var raw = "```\n---THINKING---\nexample\n---ACTION---\n```";

            var result = VisiblePlanningTraceExtractor.FinalizeContent(raw);

            Assert.AreEqual(VisiblePlanningTraceState.Invalid, result.State);
            Assert.AreEqual(string.Empty, result.Reasoning);
            Assert.AreEqual(raw, result.Content);
        }

        [Test]
        public void FinalizeContent_WithIncompleteMarker_KeepsRawContent()
        {
            var raw = "---THINKING---\nplan only";

            var result = VisiblePlanningTraceExtractor.FinalizeContent(raw);

            Assert.AreEqual(VisiblePlanningTraceState.Invalid, result.State);
            Assert.AreEqual(string.Empty, result.Reasoning);
            Assert.AreEqual(raw, result.Content);
        }

        [Test]
        public void Append_WithMarkerSplitAcrossTokens_BuffersUntilAction()
        {
            var extractor = new VisiblePlanningTraceExtractor();

            var first = extractor.Append("---TH");
            var second = extractor.Append("INKING---\nplan\n");
            var third = extractor.Append("---ACTION---\nfinal");

            Assert.AreEqual(VisiblePlanningTraceState.None, first.State);
            Assert.AreEqual(string.Empty, first.VisibleContent);
            Assert.AreEqual(string.Empty, first.ReasoningContent);
            Assert.AreEqual(VisiblePlanningTraceState.Buffering, second.State);
            Assert.AreEqual(string.Empty, second.VisibleContent);
            Assert.AreEqual(VisiblePlanningTraceState.Completed, third.State);
            Assert.AreEqual("plan\n", third.ReasoningContent);
            Assert.AreEqual("final", third.VisibleContent);
        }
    }
}
