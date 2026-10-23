using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCore.Editor.Components.Indexing.Core
{
    /// <summary>
    /// 索引存储的单写线程队列：所有写操作排队，由唯一的后台线程按提交顺序串行执行。
    /// <para>
    /// 面板、后台增量服务与 search_code 工具共享同一个存储实例，写入必须串行，否则 JSONL
    /// 内存结构与操作日志会交错。读操作不经过本队列。
    /// </para>
    /// </summary>
    public sealed class IndexWriteQueue : IDisposable
    {
        private readonly BlockingCollection<WorkItem> _queue = new BlockingCollection<WorkItem>();
        private readonly Thread _worker;
        private int _completed;

        /// <summary>
        /// 创建并启动写线程。
        /// </summary>
        /// <param name="name">线程名（便于调试）。</param>
        public IndexWriteQueue(string name = "AgentCore.IndexWriter")
        {
            _worker = new Thread(Run)
            {
                Name = name,
                IsBackground = true,
            };
            _worker.Start();
        }

        /// <summary>队列中尚未执行的写操作数。</summary>
        public int PendingCount => _queue.Count;

        /// <summary>是否已停止接收新的写操作。</summary>
        public bool IsCompleted => Volatile.Read(ref _completed) != 0;

        /// <summary>
        /// 提交一个返回值的写操作。
        /// </summary>
        public Task<T> EnqueueAsync<T>(Func<Task<T>> operation, CancellationToken ct = default)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            // 写线程内部再次提交（重入）时直接执行，避免自己等自己
            if (Thread.CurrentThread == _worker)
                return operation();

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(ct);
                return tcs.Task;
            }

            var item = new WorkItem(() =>
            {
                if (ct.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(ct);
                    return;
                }

                try
                {
                    tcs.TrySetResult(operation().GetAwaiter().GetResult());
                }
                catch (OperationCanceledException oce)
                {
                    tcs.TrySetCanceled(oce.CancellationToken);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }, () => tcs.TrySetException(new ObjectDisposedException(
                nameof(IndexWriteQueue), "索引写入队列已关闭（编辑器正在重载或退出），本次写入未执行。")));

            try
            {
                _queue.Add(item);
            }
            catch (InvalidOperationException)
            {
                // CompleteAdding 之后再提交
                item.Abandon();
            }

            return tcs.Task;
        }

        /// <summary>
        /// 提交一个无返回值的写操作。
        /// </summary>
        public Task EnqueueAsync(Func<Task> operation, CancellationToken ct = default)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            return EnqueueAsync<bool>(async () =>
            {
                await operation().ConfigureAwait(false);
                return true;
            }, ct);
        }

        /// <summary>
        /// 停止接收新写入，并在 <paramref name="drainTimeout"/> 内等待已排队写入执行完。
        /// 超时仍未执行的写入以 <see cref="ObjectDisposedException"/> 结束。
        /// </summary>
        /// <returns>true 表示队列已全部执行完。</returns>
        public bool Complete(TimeSpan drainTimeout)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                return !_worker.IsAlive;

            try
            {
                _queue.CompleteAdding();
            }
            catch (ObjectDisposedException)
            {
                return true;
            }

            var drained = Thread.CurrentThread == _worker || _worker.Join(drainTimeout);
            if (!drained)
            {
                // 写线程卡住：把剩余项标记失败，调用方不会永远等待
                while (_queue.TryTake(out var leftover))
                    leftover.Abandon();
            }

            return drained;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Complete(TimeSpan.FromSeconds(5));
        }

        private void Run()
        {
            try
            {
                foreach (var item in _queue.GetConsumingEnumerable())
                    item.Execute();
            }
            catch (Exception ex)
            {
                AgentCore.Editor.Utils.AgentCoreLog.Warning($"[IndexWriteQueue] Writer thread stopped: {ex.Message}");
            }
        }

        private sealed class WorkItem
        {
            private readonly Action _execute;
            private readonly Action _abandon;

            public WorkItem(Action execute, Action abandon)
            {
                _execute = execute;
                _abandon = abandon;
            }

            public void Execute() => _execute();

            public void Abandon() => _abandon();
        }
    }
}
