using System;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Utils
{
    /// <summary>
    /// 异步操作与 Unity 主线程之间的桥接工具。
    /// Unity Editor 的 UI 操作必须在主线程执行，但网络请求需要异步。
    /// </summary>
    public static class AsyncHelper
    {
        /// <summary>
        /// 将操作调度到 Unity 主线程执行。
        /// 使用 EditorApplication.delayCall 确保在主线程安全执行。
        /// </summary>
        public static void RunOnMainThread(Action action)
        {
            if (action == null) return;
            EditorApplication.delayCall += () =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[AgentCore] Main thread callback error: {ex}");
                }
            };
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
                Debug.Log("[AgentCore] Async operation cancelled.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AgentCore] Async error: {ex}");
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
                Debug.Log("[AgentCore] Async operation cancelled.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AgentCore] Async error: {ex}");
                onError?.Invoke(ex);
            }
        }
    }
}
