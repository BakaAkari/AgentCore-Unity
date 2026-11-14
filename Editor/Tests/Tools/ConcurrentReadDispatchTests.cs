using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.LLM;
using AgentCore.Editor.Tools;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Tools.Safety;
using AgentCore.Editor.Utils;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.Tools
{
    public class ConcurrentReadDispatchTests
    {
        private sealed class Rules : IPermissionRuleSource
        {
            public readonly List<ScopedPermissionRule> Items = new List<ScopedPermissionRule>();
            public IReadOnlyList<ScopedPermissionRule> GetActiveRules() => Items;
        }

        private class SerialTool : IAgentTool
        {
            public ToolMetadata Metadata { get; }
            private readonly Func<JObject, CancellationToken, Task<ToolResult>> _run;
            public SerialTool(string name, Func<JObject, CancellationToken, Task<ToolResult>> run,
                bool mainThread = false)
            {
                Metadata = new ToolMetadata(name, "test", "Test", JObject.Parse(@"{
                    ""type"":""object"", ""properties"":{
                        ""action"":{""type"":""string"",""enum"":[""read"",""write""]},
                        ""id"":{""type"":""integer""}}, ""required"":[""action"",""id""]}"),
                    requiresMainThread: mainThread, riskLevel: ToolRiskLevel.High,
                    capabilities: ToolCapability.WriteProjectFiles, requiresConfirmation: false, readOnlyActions: new[] { "read" });
                _run = run;
            }
            public Task<ToolResult> ExecuteAsync(JObject args, CancellationToken ct = default) => _run(args, ct);
        }

        private sealed class ReadTool : SerialTool, IConcurrentReadTool
        {
            public ReadTool(string name, Func<JObject, CancellationToken, Task<ToolResult>> run,
                bool mainThread = false) : base(name, run, mainThread) { }
            public bool SupportsConcurrentRead(string action) => action == "read";
        }

        private Rules _rules;
        private ToolCallDispatcher _dispatcher;
        private static TaskCompletionSource<bool> Gate() =>
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private static ToolCall Call(int id, string action = "read", string name = "probe") => new ToolCall
        {
            Id = id.ToString(), Type = "function",
            Function = new FunctionCall { Name = name, Arguments = new JObject { ["action"] = action, ["id"] = id }.ToString() }
        };
        private static async Task Ready(Task task)
        {
            Assert.AreSame(task, await Task.WhenAny(task, Task.Delay(10000)), "Dispatch did not make expected progress");
            await task;
        }

        [SetUp]
        public void Setup()
        {
            ToolRegistry.Instance.Clear();
            _rules = new Rules();
            _dispatcher = new ToolCallDispatcher(ToolRegistry.Instance, AutoToolConfirmationProvider.AlwaysApprove(), _rules);
        }

        [TearDown]
        public void Cleanup() => ToolAutoDiscovery.Reset();

        [Test]
        public async Task ReadsOverlap_ReturnInputOrder_AndCallbacksStayOnCallingContext()
        {
            var started = Gate(); var first = Gate(); var second = Gate(); var secondRecorded = Gate();
            int count = 0, caller = Thread.CurrentThread.ManagedThreadId;
            var callbackThreads = new List<int>(); var completionIds = new List<string>();
            ToolRegistry.Instance.Register(new ReadTool("probe", async (a, ct) =>
            {
                if (Interlocked.Increment(ref count) == 2) started.TrySetResult(true);
                await ((int)a["id"] == 0 ? first.Task : second.Task);
                return ToolResult.Ok(a["id"].ToString());
            }));
            var run = _dispatcher.DispatchAllAsync(new[] { Call(0), Call(1) }, onResult: r =>
            {
                callbackThreads.Add(Thread.CurrentThread.ManagedThreadId);
                completionIds.Add(r.ToolCall.Id);
                if (r.ToolCall.Id == "1") secondRecorded.TrySetResult(true);
            });
            try
            {
                await Ready(started.Task); // neither read can finish until BOTH have started
                second.TrySetResult(true);
                await Ready(secondRecorded.Task);
                Assert.IsFalse(run.IsCompleted);
                first.TrySetResult(true);
                var results = await run;
                CollectionAssert.AreEqual(new[] { "0", "1" }, results.Select(r => r.ToolCall.Id));
                CollectionAssert.AreEqual(new[] { "1", "0" }, completionIds);
                Assert.IsTrue(callbackThreads.All(t => t == caller));
            }
            finally { first.TrySetResult(true); second.TrySetResult(true); await run; }
        }

        [Test]
        public async Task ConcurrencyIsBounded_AndSynchronousReadsRunOffCallerThread()
        {
            var started = Gate();
            using var release = new ManualResetEventSlim();
            int count = 0, caller = Thread.CurrentThread.ManagedThreadId;
            var threads = new System.Collections.Concurrent.ConcurrentBag<int>();
            ToolRegistry.Instance.Register(new ReadTool("probe", (a, ct) =>
            {
                threads.Add(Thread.CurrentThread.ManagedThreadId);
                if (Interlocked.Increment(ref count) == 4) started.TrySetResult(true);
                if (!release.Wait(10000)) return Task.FromResult(ToolResult.Fail("test gate timed out"));
                return Task.FromResult(ToolResult.Ok("read"));
            }));
            var run = _dispatcher.DispatchAllAsync(Enumerable.Range(0, 7).Select(i => Call(i)).ToArray());
            try
            {
                await Ready(started.Task);
                Assert.AreEqual(4, Volatile.Read(ref count));
                Assert.IsTrue(threads.All(t => t != caller));
            }
            finally { release.Set(); }
            var results = await run;
            Assert.AreEqual(7, count);
            Assert.IsTrue(results.All(r => r.Result.Success));
        }

        [Test]
        public async Task WritesAreBarriers_AndKeepOriginalOrder()
        {
            var started = Gate(); var release = Gate();
            int reads = 0, finished = 0;
            var writes = new List<int>();
            ToolRegistry.Instance.Register(new ReadTool("probe", async (a, ct) =>
            {
                int id = (int)a["id"];
                if (id < 2)
                {
                    if (Interlocked.Increment(ref reads) == 2) started.TrySetResult(true);
                    await release.Task;
                    Interlocked.Increment(ref finished);
                }
                else if ((string)a["action"] == "write")
                {
                    Assert.AreEqual(2, finished);
                    writes.Add(id);
                }
                else CollectionAssert.AreEqual(new[] { 2, 3 }, writes);
                return ToolResult.Ok(id.ToString());
            }));
            var run = _dispatcher.DispatchAllAsync(new[] { Call(0), Call(1), Call(2, "write"), Call(3, "write"), Call(4) });
            try { await Ready(started.Task); Assert.IsEmpty(writes); }
            finally { release.TrySetResult(true); }
            var results = await run;
            Assert.IsTrue(results.All(r => r.Result.Success));
            CollectionAssert.AreEqual(new[] { 2, 3 }, writes);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task UnoptedOrMainThreadReadsRemainSerial(bool mainThread)
        {
            var started = Gate(); var release = Gate(); int count = 0;
            Func<JObject, CancellationToken, Task<ToolResult>> body = async (a, ct) =>
            {
                Interlocked.Increment(ref count); started.TrySetResult(true);
                await release.Task; return ToolResult.Ok("read");
            };
            IAgentTool tool = mainThread ? new ReadTool("probe", body, true) : new SerialTool("probe", body);
            ToolRegistry.Instance.Register(tool);
            var run = _dispatcher.DispatchAllAsync(new[] { Call(0), Call(1) });
            try { await Ready(started.Task); Assert.AreEqual(1, count); }
            finally { release.TrySetResult(true); }
            await run;
            Assert.AreEqual(2, count);
        }

        [Test]
        public async Task CancellationDrainsStartedReads_AndNeverStartsFollowingWrite()
        {
            using var cts = new CancellationTokenSource();
            var started = Gate(); var release = Gate(); int count = 0, writes = 0;
            ToolRegistry.Instance.Register(new ReadTool("probe", async (a, ct) =>
            {
                if ((string)a["action"] == "write") Interlocked.Increment(ref writes);
                if (Interlocked.Increment(ref count) == 2) started.TrySetResult(true);
                await release.Task; // deliberately ignores cancellation: dispatcher must still drain
                return ToolResult.Ok("read");
            }));
            var run = _dispatcher.DispatchAllAsync(new[] { Call(0), Call(1), Call(2, "write") }, cts.Token);
            try
            {
                await Ready(started.Task); cts.Cancel();
                Assert.IsFalse(run.IsCompleted);
            }
            finally { release.TrySetResult(true); }
            await AsyncAssert.ThrowsAsync<OperationCanceledException>(() => run);
            Assert.AreEqual(0, writes);
        }

        [Test]
        public async Task PermissionRulesAndSchemaStillApply_AndFailureDoesNotDropResults()
        {
            int executions = 0;
            ToolRegistry.Instance.Register(new ReadTool("probe", (a, ct) =>
            {
                Interlocked.Increment(ref executions);
                return Task.FromResult((int)a["id"] == 2 ? ToolResult.Fail("expected failure") : ToolResult.Ok("read"));
            }));
            _rules.Items.Add(new ScopedPermissionRule(new PermissionRule
                { id = "deny", effect = "deny", tool = "blocked", enabled = true }, PermissionRuleScope.User));
            ToolRegistry.Instance.Register(new ReadTool("blocked", (a, ct) =>
            {
                Assert.Fail("Denied tool must not execute"); return Task.FromResult(ToolResult.Ok("unexpected"));
            }));
            var invalid = Call(4); invalid.Function.Arguments = "{}";
            var results = await _dispatcher.DispatchAllAsync(new[] { Call(0), Call(1, name: "blocked"), Call(2), invalid, Call(5) });
            CollectionAssert.AreEqual(new[] { true, false, false, false, true }, results.Select(r => r.Result.Success));
            Assert.AreEqual(3, executions);
        }

        [Test]
        public void DiscoveryPreservesOptIn_AndOnlyAuditedFileActionsAreEnabled()
        {
            ToolAutoDiscovery.Reset(); ToolAutoDiscovery.DiscoverAndRegisterAll();
            var reader = ToolRegistry.Instance.GetTool("manage_file") as IConcurrentReadTool;
            Assert.NotNull(reader);
            foreach (var action in new[] { "read_file", "file_info", "list_directory", "search_content" })
                Assert.IsTrue(reader.SupportsConcurrentRead(action), action);
            foreach (var action in new[] { "write_file", "delete", "move", "copy", "create_directory", "unknown", null })
                Assert.IsFalse(reader.SupportsConcurrentRead(action), action);
            Assert.IsFalse(((IConcurrentReadTool)ToolRegistry.Instance.GetTool("manage_script")).SupportsConcurrentRead("read"));
        }

        [Test]
        public async Task CallbackException_DrainsStartedReads_AndNeverRunsFollowingWrite()
        {
            var started = Gate(); var first = Gate(); var second = Gate();
            var threw = Gate(); var secondExited = Gate();
            int count = 0, writes = 0;
            ToolRegistry.Instance.Register(new ReadTool("probe", async (a, ct) =>
            {
                int id = (int)a["id"];
                if ((string)a["action"] == "write")
                {
                    Interlocked.Increment(ref writes);
                    return ToolResult.Ok("write");
                }
                if (Interlocked.Increment(ref count) == 2) started.TrySetResult(true);
                await (id == 0 ? first.Task : second.Task);
                if (id == 1) secondExited.TrySetResult(true);
                return ToolResult.Ok("read");
            }));
            var run = _dispatcher.DispatchAllAsync(new[] { Call(0), Call(1), Call(2, "write") }, onResult: r =>
            {
                if (r.ToolCall.Id != "0") return;
                threw.TrySetResult(true);
                throw new InvalidOperationException("callback boom");
            });
            try
            {
                await Ready(started.Task); // both reads started; read 1 stays gated
                first.TrySetResult(true);
                await Ready(threw.Task); // the onResult callback for read 0 has thrown
                Assert.IsFalse(run.IsCompleted, "Callback failure must not surface before other started reads exit");
                second.TrySetResult(true);
                await AsyncAssert.ThrowsAsync<InvalidOperationException>(() => run);
                Assert.IsTrue(secondExited.Task.IsCompleted, "Other started read must run to completion before the error surfaces");
                Assert.AreEqual(0, Volatile.Read(ref writes), "Following write must never execute after a callback failure");
            }
            finally
            {
                first.TrySetResult(true);
                second.TrySetResult(true);
                try { await run; } catch { /* run faults by design on the happy path */ }
            }
        }

        [Test]
        public async Task RealManageFile_ConcurrentReadActionsReturnCorrectResults_AndCleanUp()
        {
            ToolAutoDiscovery.Reset();
            ToolAutoDiscovery.DiscoverAndRegisterAll();
            Assert.NotNull(ToolRegistry.Instance.GetTool("manage_file"), "manage_file must be registered by discovery");

            var root = PathUtils.ProjectRoot;
            var dirName = "AgentCoreConcurrentReadTest_" + Guid.NewGuid().ToString("N");
            var dir = Path.Combine(root, dirName);
            var alphaRel = dirName + "/alpha.txt";
            var betaRel = dirName + "/nested/beta.txt";
            var token = "AGCX-TOKEN-" + Guid.NewGuid().ToString("N");

            try
            {
                Directory.CreateDirectory(Path.Combine(root, dirName, "nested"));
                File.WriteAllText(Path.Combine(root, dirName, "alpha.txt"),
                    "first line\n" + token + " on second line\nthird line\n");
                File.WriteAllText(Path.Combine(root, dirName, "nested", "beta.txt"), "beta " + token + "\n");

                var calls = new[]
                {
                    FileCall("r", new JObject { ["action"] = "read_file", ["path"] = alphaRel }),
                    FileCall("l", new JObject { ["action"] = "list_directory", ["path"] = dirName, ["recursive"] = true }),
                    FileCall("s", new JObject { ["action"] = "search_content", ["path"] = dirName, ["pattern"] = token }),
                };

                var results = await _dispatcher.DispatchAllAsync(calls);

                Assert.AreEqual(3, results.Count);
                CollectionAssert.AreEqual(new[] { "r", "l", "s" }, results.Select(r => r.ToolCall.Id));

                var read = PayloadOf(results[0], "read_file")["data"];
                Assert.AreEqual(alphaRel, (string)read["path"]);
                Assert.AreEqual(3, (int)read["total_lines"]);
                StringAssert.Contains(token, (string)read["content"]);

                var list = PayloadOf(results[1], "list_directory")["data"];
                Assert.AreEqual(3, (int)list["count"]);
                Assert.IsFalse((bool)list["truncated"]);
                var entries = (JArray)list["entries"];
                Assert.IsTrue(entries.Any(e => (string)e["name"] == "nested" && (string)e["type"] == "directory"), "nested directory entry missing");
                Assert.IsTrue(entries.Any(e => (string)e["name"] == "alpha.txt" && (string)e["type"] == "file" && (string)e["path"] == alphaRel), "alpha.txt entry missing");
                Assert.IsTrue(entries.Any(e => (string)e["name"] == "beta.txt" && (string)e["type"] == "file" && (string)e["path"] == betaRel), "beta.txt entry missing");

                var search = PayloadOf(results[2], "search_content")["data"];
                Assert.AreEqual(2, (int)search["totalMatches"]);
                Assert.AreEqual(2, (int)search["files_matched"]);
                var matches = (JArray)search["matches"];
                Assert.AreEqual(2, matches.Count);
                Assert.IsTrue(matches.Any(m => (string)m["file"] == alphaRel && (string)m["match"] == token && (int)m["line"] == 2), "alpha.txt match missing");
                Assert.IsTrue(matches.Any(m => (string)m["file"] == betaRel && (string)m["match"] == token && (int)m["line"] == 1), "beta.txt match missing");
            }
            finally
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public async Task BatchGate_CancelledWaiterCannotEnterOrRelease_AndNextBatchRunsAfterDrain()
        {
            var started = Gate(); var release = Gate();
            int count = 0, writes = 0, batchStarted = 0;
            ToolRegistry.Instance.Register(new ReadTool("probe", async (a, ct) =>
            {
                int id = (int)a["id"];
                if ((string)a["action"] == "write")
                {
                    Interlocked.Increment(ref writes);
                    return ToolResult.Ok("write");
                }
                if (Interlocked.Increment(ref count) == 2) started.TrySetResult(true);
                await release.Task; // deliberately ignores cancellation: the gate must cover the drain
                return ToolResult.Ok("read");
            }));
            Action onBatchStarted = () => Interlocked.Increment(ref batchStarted);

            using var cts = new CancellationTokenSource();
            var first = _dispatcher.DispatchAllAsync(new[] { Call(0), Call(1) }, cts.Token, onBatchStarted: onBatchStarted);
            Task<List<ToolCallResult>> second = null;
            try
            {
                await Ready(started.Task);
                Assert.AreEqual(1, Volatile.Read(ref batchStarted), "First dispatch must open exactly one batch");
                cts.Cancel();

                using var cancelledCts = new CancellationTokenSource();
                var cancelled = _dispatcher.DispatchAllAsync(new[] { Call(2, "write") }, cancelledCts.Token, onBatchStarted: onBatchStarted);
                cancelledCts.Cancel();
                var cancelledOrTimeout = await Task.WhenAny(cancelled, Task.Delay(10000));
                Assert.AreSame(cancelled, cancelledOrTimeout, "Cancelled gate waiter did not surface cancellation");
                await AsyncAssert.CatchAsync<OperationCanceledException>(() => cancelled);

                using var freshCts = new CancellationTokenSource();
                second = _dispatcher.DispatchAllAsync(new[] { Call(3, "write") }, freshCts.Token, onBatchStarted: onBatchStarted);
                Assert.AreEqual(1, Volatile.Read(ref batchStarted), "Cancelled waiter must not release the gate or start a batch");
                Assert.AreEqual(0, Volatile.Read(ref writes), "Queued write must not execute while the gate is held");
            }
            finally { release.TrySetResult(true); }

            await AsyncAssert.ThrowsAsync<OperationCanceledException>(() => first);
            await Ready(second);
            var secondResults = await second;
            Assert.AreEqual(1, secondResults.Count);
            Assert.IsTrue(secondResults[0].Result.Success, secondResults[0].Result.Error);
            Assert.AreEqual(1, Volatile.Read(ref writes), "Exactly one write (the second batch) must execute");
            Assert.AreEqual(2, Volatile.Read(ref batchStarted), "Second batch must start only after the gate is released");
        }

        private static ToolCall FileCall(string id, JObject args) => new ToolCall
        {
            Id = id, Type = "function",
            Function = new FunctionCall { Name = "manage_file", Arguments = args.ToString() }
        };

        private static JObject PayloadOf(ToolCallResult result, string label)
        {
            Assert.IsTrue(result.Result.Success, $"{label} failed: {result.Result.Error}");
            var payload = JsonHelper.ParseObject(result.Result.Output);
            Assert.IsNotNull(payload, $"{label} returned unparseable output");
            return payload;
        }
    }
}
