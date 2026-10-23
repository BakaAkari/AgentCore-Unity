using AgentCore.Tests.Editor;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using AgentCore.Editor.Config;
using AgentCore.Editor.Core;
using AgentCore.Editor.LLM;

namespace AgentCore.Tests.Editor.Core
{
    /// <summary>
    /// FallbackRouter 重试循环：连接 / 流中途分开计预算、Retry-After、已有输出不重试、退避等待可被用户取消。
    /// </summary>
    [TestFixture]
    public class FallbackRouterRetryTests
    {
        /// <summary>按调用序号执行脚本的假客户端。</summary>
        private sealed class ScriptedClient : ILLMClient
        {
            private readonly Func<int, Action<StreamChunk>, ChatMessage> _script;
            public int Calls { get; private set; }

            public ScriptedClient(Func<int, Action<StreamChunk>, ChatMessage> script) { _script = script; }

            public Task<ChatCompletionResponse> ChatCompletionAsync(List<ChatMessage> messages, List<ToolDefinition> tools = null,
                CancellationToken ct = default, int? contentMaxTokens = null, string reasoningLevelOverride = null)
                => throw new NotSupportedException();

            public Task<ChatMessage> ChatCompletionStreamAsync(List<ChatMessage> messages, Action<StreamChunk> onChunk,
                List<ToolDefinition> tools = null, CancellationToken ct = default, int? contentMaxTokens = null,
                string reasoningLevelOverride = null)
            {
                int n = Calls++;
                return Task.FromResult(_script(n, onChunk));
            }
        }

        private static FallbackRouter NewRouter() => new FallbackRouter(null)
        {
            RetryDelayMs = 1,
            MaxRetryDelayMs = 5,
            FallbackChainOverride = () => new List<ProviderProfile>()
        };

        private static LlmHttpException Http(int status, TimeSpan? retryAfter = null)
            => new LlmHttpException(status, "x", "", retryAfter);

        private static Task<ChatMessage> Run(FallbackRouter router, ILLMClient client, CancellationToken ct = default,
            List<string> status = null)
            => router.ExecuteStreamWithRetryAsync(client, new List<ChatMessage>(), _ => { }, ct: ct,
                onStatusUpdate: s => status?.Add(s));

        [Test]
        public async Task RetryableHttpError_IsRetried_ThenSucceeds()
        {
            var client = new ScriptedClient((n, _) => n < 2 ? throw Http(429, TimeSpan.Zero) : ChatMessage.Assistant("ok"));
            var status = new List<string>();

            var result = await Run(NewRouter(), client, status: status);

            Assert.AreEqual("ok", result.Content);
            Assert.AreEqual(3, client.Calls);
            Assert.AreEqual(2, status.Count);
        }

        [Test]
        public async Task NonRetryableHttpError_IsNotRetried()
        {
            var client = new ScriptedClient((n, _) => throw Http(401));

            await AsyncAssert.ThrowsAsync<Exception>(async () => await Run(NewRouter(), client));
            Assert.AreEqual(1, client.Calls);
        }

        [Test]
        public async Task ConnectBudget_IsEnforced()
        {
            var router = NewRouter();
            router.MaxConnectRetries = 2;
            var client = new ScriptedClient((n, _) => throw Http(503));

            await AsyncAssert.ThrowsAsync<Exception>(async () => await Run(router, client));
            Assert.AreEqual(3, client.Calls);
        }

        [Test]
        public async Task ConnectAndMidStreamBudgets_AreSeparate()
        {
            var router = NewRouter();
            router.MaxConnectRetries = 1;
            router.MaxMidStreamRetries = 1;
            var client = new ScriptedClient((n, _) => n switch
            {
                0 => throw Http(503),
                1 => throw new EndOfStreamException("cut"),
                _ => ChatMessage.Assistant("ok")
            });

            var result = await Run(router, client);

            Assert.AreEqual("ok", result.Content);
            Assert.AreEqual(3, client.Calls);
        }

        [Test]
        public async Task FailureAfterOutput_IsNotRetried()
        {
            var client = new ScriptedClient((n, onChunk) =>
            {
                onChunk(StreamChunk.Token("partial"));
                throw new EndOfStreamException("cut");
            });

            var ex = await AsyncAssert.ThrowsAsync<Exception>(async () => await Run(NewRouter(), client));
            StringAssert.Contains("partial output", ex.Message);
            Assert.AreEqual(1, client.Calls);
        }

        [Test]
        public async Task RetryAfterBeyondLimit_IsNotWaitedFor()
        {
            var client = new ScriptedClient((n, _) => throw Http(429, TimeSpan.FromMinutes(10)));

            await AsyncAssert.ThrowsAsync<Exception>(async () => await Run(NewRouter(), client));
            Assert.AreEqual(1, client.Calls);
        }

        [Test]
        public async Task UserCancelDuringBackoff_PropagatesAsCancellation()
        {
            var client = new ScriptedClient((n, _) => throw Http(429, TimeSpan.FromSeconds(60)));
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            await AsyncAssert.CatchAsync<OperationCanceledException>(async () => await Run(NewRouter(), client, cts.Token));
            Assert.AreEqual(1, client.Calls);
        }

        [Test]
        public async Task InternalTimeout_IsRetried_NotTreatedAsUserCancel()
        {
            var client = new ScriptedClient((n, _) => n == 0 ? throw new TaskCanceledException("http timeout") : ChatMessage.Assistant("ok"));

            var result = await Run(NewRouter(), client);

            Assert.AreEqual("ok", result.Content);
            Assert.AreEqual(2, client.Calls);
        }
    }
}
