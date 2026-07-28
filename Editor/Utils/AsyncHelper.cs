using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Utils
{
    /// <summary>
    /// 异步操作与 Unity 主线程之间的桥接工具。
    /// Unity Editor 的 UI 操作必须在主线程执行，但网络请求需要异步。
    /// </summary>
    public static class AsyncHelper
    {
        // v1.6.5: 事件批处理队列 — 后台线程入队，主线程每帧 drain
        // 旧实现：每 token 注册一个 EditorApplication.delayCall → 高频下主线程被 delayCall 队列淹没
        // 新实现：ConcurrentQueue + EditorApplication.update 每帧批量执行
        private static readonly ConcurrentQueue<Action> _mainThreadQueue = new();
        private static bool _updateHookRegistered;

        /// <summary>
        /// 将操作调度到 Unity 主线程执行。
        /// v1.6.5: 使用 ConcurrentQueue 批处理，每帧通过 EditorApplication.update drain 一次。
        /// </summary>
        public static void RunOnMainThread(Action action)
        {
            if (action == null) return;
            _mainThreadQueue.Enqueue(action);
            EnsureUpdateHook();
        }

        private static void EnsureUpdateHook()
        {
            if (_updateHookRegistered) return;
            _updateHookRegistered = true;
            EditorApplication.update += DrainMainThreadQueue;
        }

        private static void DrainMainThreadQueue()
        {
            // fast-path: 队列空时不进 marker, 避免每帧无谓 sample
            if (_mainThreadQueue.IsEmpty) return;

            using (AgentCoreProfilerMarkers.DrainQueue.Auto())
            {
                // 每帧最多处理 256 个回调，防止极端积压下卡死一帧
                int processed = 0;
                const int MaxPerFrame = 256;

                while (processed < MaxPerFrame && _mainThreadQueue.TryDequeue(out var action))
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        AgentCoreLog.Error($"[AgentCore] Main thread callback error: {ex}");
                    }
                    processed++;
                }
            }
        }

        /// <summary>
        /// 在 Editor 中安全运行 async Task。
        /// 捕获所有异常并输出到 Console，防止未观察的 Task 异常。
        /// </summary>
        public static async void RunAsync(Func<Task> asyncFunc, Action<Exception> onError = null)
        {
            try
            {
                await asyncFunc();
            }
            catch (OperationCanceledException)
            {
                // 取消操作是正常流程，不记录错误
                AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] Async operation cancelled.");
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"[AgentCore] Async error: {ex}");
                onError?.Invoke(ex);
            }
        }

        /// <summary>
        /// 在 Editor 中安全运行 async Task&lt;T&gt;，返回结果通过回调传递。
        /// </summary>
        public static async void RunAsync<T>(Func<Task<T>> asyncFunc, Action<T> onSuccess = null, Action<Exception> onError = null)
        {
            try
            {
                var result = await asyncFunc();
                onSuccess?.Invoke(result);
            }
            catch (OperationCanceledException)
            {
                AgentCore.Editor.Utils.AgentCoreLog.Info("[AgentCore] Async operation cancelled.");
            }
            catch (Exception ex)
            {
                AgentCoreLog.Error($"[AgentCore] Async error: {ex}");
                onError?.Invoke(ex);
            }
        }
    }
}
