using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Components.Indexing.Core;
using NUnit.Framework;

namespace AgentCore.Tests.Editor.Indexing
{
    /// <summary>
    /// IndexWriteQueue 单写线程顺序、异常传播与关闭行为的单元测试。
    /// </summary>
    [TestFixture]
    public class IndexWriteQueueTests
    {
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

        [Test]
        public void Enqueue_ExecutesInSubmissionOrder_OnOneThread()
        {
            using (var queue = new IndexWriteQueue("test-writer"))
            {
                var order = new List<int>();
                var threads = new HashSet<int>();
                var tasks = Enumerable.Range(0, 50).Select(i => queue.EnqueueAsync(() =>
                {
                    order.Add(i);
                    threads.Add(Thread.CurrentThread.ManagedThreadId);
                    return Task.CompletedTask;
                })).ToArray();

                Assert.IsTrue(Task.WaitAll(tasks, Wait));
                CollectionAssert.AreEqual(Enumerable.Range(0, 50).ToList(), order);
                Assert.AreEqual(1, threads.Count, "所有写操作必须在同一个写线程上执行");
            }
        }

        [Test]
        public void Enqueue_ReturnsValue_AndPropagatesException()
        {
            using (var queue = new IndexWriteQueue("test-writer"))
            {
                var ok = queue.EnqueueAsync(() => Task.FromResult(42));
                var bad = queue.EnqueueAsync<int>(() => throw new InvalidOperationException("boom"));

                Assert.AreEqual(42, ok.Result);
                var ex = Assert.Throws<AggregateException>(() => bad.Wait(Wait));
                Assert.IsInstanceOf<InvalidOperationException>(ex.InnerException);
            }
        }

        [Test]
        public void Enqueue_ReentrantCallOnWriter_RunsInline()
        {
            using (var queue = new IndexWriteQueue("test-writer"))
            {
                var outer = queue.EnqueueAsync(async () =>
                {
                    // 写线程内再次提交不能死锁
                    return await queue.EnqueueAsync(() => Task.FromResult(7));
                });

                Assert.IsTrue(outer.Wait(Wait));
                Assert.AreEqual(7, outer.Result);
            }
        }

        [Test]
        public void Enqueue_AlreadyCancelled_IsCancelledWithoutRunning()
        {
            using (var queue = new IndexWriteQueue("test-writer"))
            {
                var ran = false;
                var cts = new CancellationTokenSource();
                cts.Cancel();

                var task = queue.EnqueueAsync(() =>
                {
                    ran = true;
                    return Task.CompletedTask;
                }, cts.Token);

                Assert.Throws<AggregateException>(() => task.Wait(Wait));
                Assert.IsTrue(task.IsCanceled);
                Assert.IsFalse(ran);
            }
        }

        [Test]
        public void Complete_DrainsPendingWrites_ThenRejectsNewOnes()
        {
            var queue = new IndexWriteQueue("test-writer");
            var count = 0;
            var tasks = Enumerable.Range(0, 20)
                .Select(_ => queue.EnqueueAsync(() =>
                {
                    Interlocked.Increment(ref count);
                    return Task.CompletedTask;
                }))
                .ToArray();

            Assert.IsTrue(queue.Complete(Wait));
            Assert.IsTrue(tasks.All(t => t.IsCompleted && !t.IsFaulted));
            Assert.AreEqual(20, count);

            var late = queue.EnqueueAsync(() => Task.CompletedTask);
            var ex = Assert.Throws<AggregateException>(() => late.Wait(Wait));
            Assert.IsInstanceOf<ObjectDisposedException>(ex.InnerException, "关闭后的写入必须明确失败，不能静默丢弃");
        }
    }
}
