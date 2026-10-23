using AgentCore.Tests.Editor;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using AgentCore.Editor.LLM;

namespace AgentCore.Tests.Editor.LLM
{
    /// <summary>
    /// SSE 成帧、流结束判定与 tool_call 完整性校验的单元测试。
    /// </summary>
    [TestFixture]
    public class StreamingResponseParserTests
    {
        private static async Task<List<StreamChunk>> Parse(string sse)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
            var chunks = new List<StreamChunk>();
            await new StreamingResponseParser().ParseStreamAsync(stream, chunks.Add);
            return chunks;
        }

        private static string Text(List<StreamChunk> chunks)
        {
            return string.Concat(chunks.Where(c => c.Type == StreamChunkType.ContentToken).Select(c => c.Content));
        }

        [Test]
        public async Task DataWithoutSpace_AndComments_AreParsed()
        {
            var chunks = await Parse(
                ": keep-alive\n\n" +
                "data:{\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\n" +
                "data: [DONE]\n\n");

            Assert.AreEqual("Hi", Text(chunks));
            Assert.AreEqual(1, chunks.Count(c => c.Type == StreamChunkType.Done));
        }

        [Test]
        public async Task MultiLineData_IsJoinedBeforeParsing()
        {
            var chunks = await Parse(
                "data: {\"choices\":[{\"delta\":\n" +
                "data: {\"content\":\"A\"}}]}\n\n" +
                "data: [DONE]\n\n");

            Assert.AreEqual("A", Text(chunks));
        }

        [Test]
        public async Task CrLfLineEndings_AreSupported()
        {
            var chunks = await Parse(
                "data: {\"choices\":[{\"delta\":{\"content\":\"x\"}}]}\r\n\r\ndata: [DONE]\r\n\r\n");

            Assert.AreEqual("x", Text(chunks));
        }

        [Test]
        public async Task ContentToolCallsAndFinishReason_InOneChunk_AreAllDelivered()
        {
            var chunks = await Parse(
                "data: {\"choices\":[{\"delta\":{\"content\":\"ok\",\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"function\":{\"name\":\"t\",\"arguments\":\"{}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\n" +
                "data: [DONE]\n\n");

            Assert.AreEqual("ok", Text(chunks));
            Assert.AreEqual(1, chunks.Count(c => c.Type == StreamChunkType.ToolCallDelta));
            var done = chunks.Where(c => c.Type == StreamChunkType.Done).ToList();
            Assert.AreEqual(1, done.Count, "[DONE] must not emit a second Done after finish_reason");
            Assert.AreEqual("tool_calls", done[0].FinishReason);
        }

        [Test]
        public async Task FinishReasonWithNullDelta_IsDelivered()
        {
            var chunks = await Parse("data: {\"choices\":[{\"delta\":null,\"finish_reason\":\"length\"}]}\n\n");

            Assert.AreEqual("length", chunks.Single(c => c.Type == StreamChunkType.Done).FinishReason);
        }

        [Test]
        public async Task StreamEndingWithoutDoneOrFinishReason_Throws()
        {
            await AsyncAssert.ThrowsAsync<EndOfStreamException>(async () =>
                await Parse("data: {\"choices\":[{\"delta\":{\"content\":\"half\"}}]}\n\n"));
        }

        [Test]
        public async Task StreamEndingAfterFinishReasonWithoutDone_Completes()
        {
            var chunks = await Parse("data: {\"choices\":[{\"delta\":{\"content\":\"a\"},\"finish_reason\":\"stop\"}]}\n\n");

            Assert.AreEqual("stop", chunks.Single(c => c.Type == StreamChunkType.Done).FinishReason);
        }

        [Test]
        public async Task ErrorPayload_ThrowsRetryableStreamError()
        {
            var ex = await AsyncAssert.ThrowsAsync<LlmStreamErrorException>(async () =>
                await Parse("data: {\"error\":{\"type\":\"server_error\",\"message\":\"boom\"}}\n\n"));
            Assert.IsTrue(ex.Retryable);
        }

        [Test]
        public async Task ErrorEvent_InvalidRequest_IsNotRetryable()
        {
            var ex = await AsyncAssert.ThrowsAsync<LlmStreamErrorException>(async () =>
                await Parse("event: error\ndata: {\"error\":{\"type\":\"invalid_request_error\",\"message\":\"bad\"}}\n\n"));
            Assert.IsFalse(ex.Retryable);
        }

        [Test]
        public void MarkInvalid_Length_MarksEveryCall()
        {
            var calls = new List<ToolCall>
            {
                new ToolCall { Id = "a", Function = new FunctionCall { Name = "t", Arguments = "{\"x\":1}" } },
                new ToolCall { Id = "b", Function = new FunctionCall { Name = "t", Arguments = "{\"content\":\"hal" } }
            };

            ToolCallIntegrity.MarkInvalid(calls, "length");

            Assert.AreEqual(ToolCallIntegrity.TruncatedReason, calls[0].InvalidReason);
            Assert.AreEqual(ToolCallIntegrity.TruncatedReason, calls[1].InvalidReason);
        }

        [Test]
        public void MarkInvalid_InvalidOrWrappedArguments_AreMarked()
        {
            var calls = new List<ToolCall>
            {
                new ToolCall { Id = "a", Function = new FunctionCall { Name = "t", Arguments = "{\"x\":1}" } },
                new ToolCall { Id = "b", Function = new FunctionCall { Name = "t", Arguments = "{\"_raw_arguments\":\"{oops\"}" } },
                new ToolCall { Id = "c", Function = new FunctionCall { Name = "t", Arguments = "[1,2]" } },
                new ToolCall { Id = "d", Function = new FunctionCall { Name = "t", Arguments = "" } }
            };

            ToolCallIntegrity.MarkInvalid(calls, "tool_calls");

            Assert.IsNull(calls[0].InvalidReason);
            Assert.AreEqual(ToolCallIntegrity.InvalidJsonReason, calls[1].InvalidReason);
            Assert.AreEqual(ToolCallIntegrity.InvalidJsonReason, calls[2].InvalidReason);
            Assert.IsNull(calls[3].InvalidReason);
        }
    }
}
