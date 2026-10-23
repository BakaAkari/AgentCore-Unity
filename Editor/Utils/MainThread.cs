using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;

namespace AgentCore.Editor.Utils
{
    /// <summary>
    /// Unity 主线程调度器。任意线程都可以把工作投递到主线程执行，并（可选）await 结果。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>主线程 id 在 <c>[InitializeOnLoad]</c> 静态构造时捕获（Unity 保证在主线程执行）。</item>
    /// <item>工作项进入 <see cref="ConcurrentQueue{T}"/>，由 <see cref="EditorApplication.update"/> 每帧泵出。</item>
    /// <item>后台线程入队后调用 <see cref="EditorApplication.QueuePlayerLoopUpdate"/>，
    ///       编辑器失焦（不主动 tick）时也能尽快泵出，避免后台任务干等。</item>
    /// <item><see cref="RunAsync{T}"/> 的异常原样传播给 await 方。</item>
    /// <item>域重载前（beforeAssemblyReload）把仍排队的工作项全部以取消结束，
    ///       await 方得到 <see cref="OperationCanceledException"/> 而不是永远挂起；之后的新投递同样立即取消。</item>
    /// </list>
    /// 与 <see cref="AsyncHelper.RunOnMainThread"/> 的区别：后者是 fire-and-forget 的 UI 事件批处理，
    /// 本类面向"后台线程需要从 Unity API 取值 / 执行一段主线程逻辑并拿到结果"的场景。
    /// </remarks>
    [InitializeOnLoad]
    public static class MainThread
    {
        // 单个工作项：Execute 在主线程执行；Cancel 在域重载清队时调用
        private interface IWorkItem
        {
            void Execute();
            void Cancel();
        }

        private sealed class ActionItem : IWorkItem
        {
            private readonly Action _action;
            public ActionItem(Action action) { _action = action; }

            public void Execute()
            {
                try
                {
                    _action();
                }
                catch (Exception ex)
                {
                    // Post 没有 await 方，只能记日志
                    AgentCoreLog.Error($"[AgentCore] MainThread.Post callback error: {ex}");
                }
            }

            public void Cancel() { }
        }

        private sealed class FuncItem<T> : IWorkItem
        {
            private readonly Func<T> _func;
            public readonly TaskCompletionSource<T> Tcs =
                new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            public FuncItem(Func<T> func) { _func = func; }

            public void Execute()
            {
                try
                {
                    Tcs.TrySetResult(_func());
                }
                catch (OperationCanceledException oce)
                {
                    Tcs.TrySetCanceled(oce.CancellationToken);
                }
                catch (Exception ex)
                {
                    // 异常交给 await 方处理
                    Tcs.TrySetException(ex);
                }
            }

            public void Cancel() => Tcs.TrySetCanceled();
        }

        private static readonly ConcurrentQueue<IWorkItem> _queue = new ConcurrentQueue<IWorkItem>();

        private static readonly int _mainThreadId;

        // 1 = 域即将重载，不再接收新工作（新投递直接取消）
        private static int _shuttingDown;

        static MainThread()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            EditorApplication.update -= Pump;
            EditorApplication.update += Pump;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        /// <summary>当前线程是否为 Unity 主线程。</summary>
        public static bool IsMainThread => Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        /// <summary>
        /// 投递一个 fire-and-forget 操作到主线程。总是排队执行（即使当前就在主线程），保证先后顺序。
        /// 异常只记日志。域重载开始后的投递会被丢弃。
        /// </summary>
        public static void Post(Action action)
        {
            if (action == null) return;
            Enqueue(new ActionItem(action));
        }

        /// <summary>
        /// 在主线程执行 <paramref name="func"/> 并返回结果。
        /// 当前已在主线程时直接同步执行（避免主线程 await 自己导致的一帧延迟或死锁）。
        /// 异常会传播到返回的 Task；域重载期间未执行的工作以取消结束。
        /// </summary>
        public static Task<T> RunAsync<T>(Func<T> func)
        {
            if (func == null) throw new ArgumentNullException(nameof(func));

            var item = new FuncItem<T>(func);
            if (IsMainThread)
            {
                item.Execute();
                return item.Tcs.Task;
            }

            Enqueue(item);
            return item.Tcs.Task;
        }

        /// <summary>
        /// 在主线程执行 <paramref name="action"/>，完成（或抛出）后结束返回的 Task。
        /// </summary>
        public static Task RunAsync(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            return RunAsync<bool>(() => { action(); return true; });
        }

        private static void Enqueue(IWorkItem item)
        {
            if (Volatile.Read(ref _shuttingDown) != 0)
            {
                // 域即将重载：不会再有泵出机会，立即取消，避免 await 方永久挂起
                item.Cancel();
                return;
            }

            _queue.Enqueue(item);

            if (!IsMainThread)
            {
                // 编辑器失焦时 update 频率很低，主动请求一次 player loop 更新以尽快泵出
                try { EditorApplication.QueuePlayerLoopUpdate(); }
                catch (Exception) { /* 极少数时机（退出中）调用失败，下一帧 update 仍会泵出 */ }
            }
        }

        private static void Pump()
        {
            if (_queue.IsEmpty) return;

            // 每帧最多处理一批，防止极端积压卡死一帧；剩余的下一帧继续
            const int MaxPerFrame = 256;
            int processed = 0;
            while (processed < MaxPerFrame && _queue.TryDequeue(out var item))
            {
                item.Execute();
                processed++;
            }
        }

        private static void OnBeforeAssemblyReload()
        {
            Interlocked.Exchange(ref _shuttingDown, 1);
            EditorApplication.update -= Pump;

            // 剩余工作不再执行（新域里没有对应的 await 方），统一以取消结束
            int dropped = 0;
            while (_queue.TryDequeue(out var item))
            {
                item.Cancel();
                dropped++;
            }
            if (dropped > 0)
                AgentCoreLog.Warning($"[AgentCore] MainThread: {dropped} pending main-thread work item(s) cancelled by domain reload.");
        }
    }
}
