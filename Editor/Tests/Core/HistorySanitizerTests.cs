using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using AgentCore.Editor.Core;
using AgentCore.Editor.LLM;

namespace AgentCore.Tests.Editor.Core
{
    /// <summary>
    /// HistorySanitizer 请求前历史修复的单元测试。
    /// </summary>
    [TestFixture]
    public class HistorySanitizerTests
    {
        private static ChatMessage Calls(params string[] ids)
        {
            return ChatMessage.Assistant("", ids.Select(id => new ToolCall
            {
                Id = id,
                Function = new FunctionCall { Name = "t", Arguments = "{}" }
            }).ToList());
        }

        private static string Shape(List<ChatMessage> msgs)
        {
            return string.Join(" ", msgs.Select(m =>
                m.Role == "tool" ? "tool:" + m.ToolCallId
                : m.Role == "assistant" && m.ToolCalls != null && m.ToolCalls.Count > 0 ? "calls"
                : m.Role));
        }

        [Test]
        public void Normalize_CompleteHistory_IsUnchanged()
        {
            var input = new List<ChatMessage>
            {
                ChatMessage.User("hi"), Calls("a", "b"),
                ChatMessage.Tool("a", "1"), ChatMessage.Tool("b", "2"),
                ChatMessage.Assistant("done")
            };

            var output = HistorySanitizer.Normalize(input, out var report);

            Assert.IsFalse(report.Changed);
            CollectionAssert.AreEqual(input, output);
        }

        [Test]
        public void Normalize_MissingResult_InsertsSyntheticInCallOrder()
        {
            var input = new List<ChatMessage>
            {
                ChatMessage.User("hi"), Calls("a", "b", "c"), ChatMessage.Tool("b", "2"),
                ChatMessage.User("next")
            };

            var output = HistorySanitizer.Normalize(input, out var report);

            Assert.AreEqual("user calls tool:a tool:b tool:c user", Shape(output));
            Assert.AreEqual(2, report.SyntheticResults);
            Assert.AreEqual(HistorySanitizer.InterruptedToolResult, output[2].Content);
            Assert.AreEqual("2", output[3].Content);
        }

        [Test]
        public void Normalize_TrailingCallsWithoutResults_AreCompleted()
        {
            var input = new List<ChatMessage> { ChatMessage.User("hi"), Calls("a") };

            var output = HistorySanitizer.Normalize(input);

            Assert.AreEqual("user calls tool:a", Shape(output));
        }

        [Test]
        public void Normalize_OrphanToolResult_IsDropped()
        {
            var input = new List<ChatMessage>
            {
                ChatMessage.User("hi"), ChatMessage.Tool("ghost", "x"), ChatMessage.Assistant("ok")
            };

            var output = HistorySanitizer.Normalize(input, out var report);

            Assert.AreEqual("user assistant", Shape(output));
            Assert.AreEqual(1, report.DroppedOrphans);
        }

        [Test]
        public void Normalize_ResultDisplacedBySystemMessage_IsMovedBack()
        {
            var input = new List<ChatMessage>
            {
                Calls("a"), ChatMessage.System("note"), ChatMessage.Tool("a", "real")
            };

            var output = HistorySanitizer.Normalize(input, out var report);

            Assert.AreEqual("calls tool:a system", Shape(output));
            Assert.AreEqual("real", output[1].Content);
            Assert.AreEqual(1, report.MovedResults);
            Assert.AreEqual(0, report.SyntheticResults);
        }

        [Test]
        public void Normalize_DoesNotBorrowResultFromLaterCallBatch()
        {
            // 第二批调用复用了同一个 id：它后面的结果属于第二批，不能被挪给第一批
            var input = new List<ChatMessage>
            {
                Calls("a"), ChatMessage.User("cancelled"), Calls("a"), ChatMessage.Tool("a", "second")
            };

            var output = HistorySanitizer.Normalize(input);

            Assert.AreEqual("calls tool:a user calls tool:a", Shape(output));
            Assert.AreEqual(HistorySanitizer.InterruptedToolResult, output[1].Content);
            Assert.AreEqual("second", output[4].Content);
        }

        [Test]
        public void Normalize_DuplicateResults_KeepsFirst()
        {
            var input = new List<ChatMessage>
            {
                Calls("a"), ChatMessage.Tool("a", "first"), ChatMessage.Tool("a", "again")
            };

            var output = HistorySanitizer.Normalize(input, out var report);

            Assert.AreEqual("calls tool:a", Shape(output));
            Assert.AreEqual("first", output[1].Content);
            Assert.AreEqual(1, report.DroppedDuplicates);
        }

        [Test]
        public void Normalize_DoesNotMutateInput()
        {
            var input = new List<ChatMessage> { Calls("a"), ChatMessage.Tool("x", "orphan") };
            var copy = new List<ChatMessage>(input);

            HistorySanitizer.Normalize(input);

            CollectionAssert.AreEqual(copy, input);
        }
    }
}
