using AgentCore.Tests.Editor;
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using AgentCore.Editor.LLM;
using AgentCore.Editor.LLM.Anthropic;

namespace AgentCore.Tests.Editor.LLM
{
    /// <summary>
    /// Retry-After 解析、指数退避、失败阶段分类与 SSE 空闲看门狗的单元测试。
    /// </summary>
    [TestFixture]
    public class LlmRetryPolicyTests
    {
        private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        [TestCase("5", 5.0)]
        [TestCase(" 2.5 ", 2.5)]
        [TestCase("0", 0.0)]
        public void ParseRetryAfter_Seconds(string value, double expected)
        {
            Assert.AreEqual(TimeSpan.FromSeconds(expected), LlmRetryPolicy.ParseRetryAfter(value, Now));
        }

        [Test]
        public void ParseRetryAfter_HttpDate_IsRelativeToNow()
        {
            var value = Now.AddSeconds(30).ToString("r");
            Assert.AreEqual(TimeSpan.FromSeconds(30), LlmRetryPolicy.ParseRetryAfter(value, Now));
        }

        [Test]
        public void ParseRetryAfter_PastDate_IsZero()
        {
            var value = Now.AddMinutes(-1).ToString("r");
            Assert.AreEqual(TimeSpan.Zero, LlmRetryPolicy.ParseRetryAfter(value, Now));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("soon")]
        [TestCase("-3")]
        public void ParseRetryAfter_Invalid_IsNull(string value)
        {
            Assert.IsNull(LlmRetryPolicy.ParseRetryAfter(value, Now));
        }

        [Test]
        public void GetRetryAfter_PrefersMillisecondsHeader()
        {
            using var response = new HttpResponseMessage((System.Net.HttpStatusCode)429);
            response.Headers.TryAddWithoutValidation("Retry-After", "10");
            response.Headers.TryAddWithoutValidation("retry-after-ms", "1500");
            Assert.AreEqual(TimeSpan.FromMilliseconds(1500), LlmRetryPolicy.GetRetryAfter(response, Now));
        }

        [Test]
        public void LlmHttpException_FromResponse_CarriesStatusAndRetryAfter()
        {
            using var response = new HttpResponseMessage((System.Net.HttpStatusCode)529) { ReasonPhrase = "Overloaded" };
            response.Headers.TryAddWithoutValidation("Retry-After", "7");
            var ex = LlmHttpException.FromResponse(response, "{}");

            Assert.AreEqual(529, ex.StatusCode);
            Assert.AreEqual(TimeSpan.FromSeconds(7), ex.RetryAfter);
            StringAssert.StartsWith("LLM API error: HTTP 529 Overloaded", ex.Message);
            Assert.AreEqual(TimeSpan.FromSeconds(7), LlmRetryPolicy.GetRetryAfter(new Exception("wrap", ex)));
        }

        [Test]
        public void ComputeBackoff_GrowsExponentially_WithinJitterBounds()
        {
            var b = TimeSpan.FromSeconds(1);
            var max = TimeSpan.FromSeconds(30);
            Assert.AreEqual(TimeSpan.FromMilliseconds(500), LlmRetryPolicy.ComputeBackoff(1, b, max, 0));
            Assert.AreEqual(TimeSpan.FromSeconds(1), LlmRetryPolicy.ComputeBackoff(1, b, max, 1));
            Assert.AreEqual(TimeSpan.FromSeconds(2), LlmRetryPolicy.ComputeBackoff(3, b, max, 0));
            Assert.AreEqual(TimeSpan.FromSeconds(4), LlmRetryPolicy.ComputeBackoff(3, b, max, 1));
            // 上限截断
            Assert.AreEqual(TimeSpan.FromSeconds(30), LlmRetryPolicy.ComputeBackoff(20, b, max, 1));
            Assert.AreEqual(TimeSpan.FromSeconds(15), LlmRetryPolicy.ComputeBackoff(20, b, max, 0));
        }

        [Test]
        public void ComputeDelay_HonoursRetryAfter()
        {
            var d = LlmRetryPolicy.ComputeDelay(1, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), 0.5);
            Assert.AreEqual(TimeSpan.FromSeconds(9), d);
        }

        [Test]
        public void IsMidStreamFailure_ClassifiesPhases()
        {
            Assert.IsTrue(LlmRetryPolicy.IsMidStreamFailure(new EndOfStreamException()));
            Assert.IsTrue(LlmRetryPolicy.IsMidStreamFailure(new LlmStreamIdleTimeoutException("idle")));
            Assert.IsTrue(LlmRetryPolicy.IsMidStreamFailure(new LlmStreamErrorException("x", true)));
            Assert.IsTrue(LlmRetryPolicy.IsMidStreamFailure(new AnthropicApiException("overloaded_error", "x")));

            Assert.IsFalse(LlmRetryPolicy.IsMidStreamFailure(new LlmHttpException(429, "Too Many Requests", "", null)));
            Assert.IsFalse(LlmRetryPolicy.IsMidStreamFailure(new HttpRequestException("connection refused", new IOException())));
            Assert.IsFalse(LlmRetryPolicy.IsMidStreamFailure(new TimeoutException("no response headers")));
        }

        [TestCase(429, true)]
        [TestCase(529, true)]
        [TestCase(503, true)]
        [TestCase(408, true)]
        [TestCase(400, false)]
        [TestCase(401, false)]
        [TestCase(404, false)]
        public void IsRetryableStatus(int status, bool expected)
        {
            Assert.AreEqual(expected, LlmRetryPolicy.IsRetryableStatus(status));
        }

        /// <summary>写入一次数据后永远不结束的流（模拟远端静默挂起）。</summary>
        private sealed class HangingStream : Stream
        {
            private byte[] _pending;
            private readonly TaskCompletionSource<int> _never = new();
            public bool Disposed { get; private set; }

            public HangingStream(string first) { _pending = Encoding.UTF8.GetBytes(first); }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            {
                if (Disposed) throw new ObjectDisposedException(nameof(HangingStream));
                if (_pending != null)
                {
                    int n = Math.Min(count, _pending.Length);
                    Array.Copy(_pending, 0, buffer, offset, n);
                    _pending = null;
                    return Task.FromResult(n);
                }
                return _never.Task;
            }

            public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, default).GetAwaiter().GetResult();
            protected override void Dispose(bool disposing)
            {
                Disposed = true;
                _never.TrySetException(new ObjectDisposedException(nameof(HangingStream)));
                base.Dispose(disposing);
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        [Test]
        public async Task Watchdog_IdleStream_ThrowsIdleTimeoutAndDisposesStream()
        {
            var stream = new HangingStream("data: {\"choices\":[{\"delta\":{\"content\":\"a\"}}]}\n\n");
            var parser = new StreamingResponseParser { IdleTimeout = TimeSpan.FromMilliseconds(200) };

            await AsyncAssert.ThrowsAsync<LlmStreamIdleTimeoutException>(async () =>
                await parser.ParseStreamAsync(stream, _ => { }));
            Assert.IsTrue(stream.Disposed);
        }

        [Test]
        public async Task Watchdog_UserCancel_ThrowsOperationCanceled_NotTimeout()
        {
            var stream = new HangingStream("data: {\"choices\":[{\"delta\":{\"content\":\"a\"}}]}\n\n");
            var parser = new StreamingResponseParser { IdleTimeout = TimeSpan.FromSeconds(30) };
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            await AsyncAssert.CatchAsync<OperationCanceledException>(async () =>
                await parser.ParseStreamAsync(stream, _ => { }, cts.Token));
        }
    }
}
