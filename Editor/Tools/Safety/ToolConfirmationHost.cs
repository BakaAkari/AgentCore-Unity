using System;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Utils;

namespace AgentCore.Editor.Tools.Safety
{
    /// <summary>
    /// 编辑器里显示工具确认卡片的宿主（目前是 Chat 窗口）。
    /// <para>
    /// 内嵌 Chat 之外的调用来源（MCP 等）需要确认时经这里请求：宿主未打开时先打开它，再把请求排进与 Chat 相同的确认队列。
    /// Chat 窗口在 CreateGUI 时注册、销毁时注销；<see cref="OpenHost"/> 由 Chat 窗口在域加载时设置。
    /// </para>
    /// </summary>
    public static class ToolConfirmationHost
    {
        /// <summary>打开宿主后等待其注册处理器的最长时间。</summary>
        private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(10);

        private static Func<ToolConfirmationRequest, CancellationToken, Task<bool>> _handler;

        /// <summary>打开宿主窗口（主线程调用）。</summary>
        public static Action OpenHost { get; set; }

        public static bool HasHandler => Volatile.Read(ref _handler) != null;

        public static void Register(Func<ToolConfirmationRequest, CancellationToken, Task<bool>> handler)
        {
            Volatile.Write(ref _handler, handler);
        }

        public static void Unregister(Func<ToolConfirmationRequest, CancellationToken, Task<bool>> handler)
        {
            // 方法组每次转换都是新委托实例：按 Delegate.Equals（同目标同方法）比较，而不是引用
            var current = Volatile.Read(ref _handler);
            if (current != null && current.Equals(handler))
                Interlocked.CompareExchange(ref _handler, null, current);
        }

        /// <summary>
        /// 在宿主中请求确认；宿主不可用（打不开）时返回 false（拒绝），与 dispatcher 的 fail-safe 语义一致。
        /// 可在任意线程调用。
        /// </summary>
        public static async Task<bool> RequestAsync(ToolConfirmationRequest request, CancellationToken ct)
        {
            if (request == null || ct.IsCancellationRequested)
                return false;

            var handler = Volatile.Read(ref _handler);
            if (handler == null)
            {
                await MainThread.RunAsync(() =>
                {
                    try
                    {
                        OpenHost?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        AgentCoreLog.Warning($"[AgentCore] Failed to open the confirmation window: {ex.Message}");
                    }
                }).ConfigureAwait(false);

                var deadline = DateTime.UtcNow + OpenTimeout;
                while ((handler = Volatile.Read(ref _handler)) == null && DateTime.UtcNow < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    // 窗口的 CreateGUI 需要编辑器 tick：失焦时主动唤醒
                    try { UnityEditor.EditorApplication.QueuePlayerLoopUpdate(); } catch (Exception) { /* ignore */ }
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }

                if (handler == null)
                {
                    AgentCoreLog.Warning($"[AgentCore] Confirmation for '{request.ToolName}' rejected: the AgentCore window could not be opened.");
                    return false;
                }
            }

            // 处理器是 Chat 的确认队列，要求在主线程调用
            var task = await MainThread.RunAsync(() => handler(request, ct)).ConfigureAwait(false);
            return await task.ConfigureAwait(false);
        }
    }
}
