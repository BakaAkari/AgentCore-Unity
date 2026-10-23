using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCore.Editor.LLM
{
    /// <summary>
    /// SSE 流空闲看门狗触发：连续 <see cref="StreamIdleWatchdog.DefaultIdleTimeout"/> 没有收到任何一行。
    /// 属于流中途失败，与用户取消（<see cref="OperationCanceledException"/>）严格区分。
    /// </summary>
    public class LlmStreamIdleTimeoutException : TimeoutException
    {
        public LlmStreamIdleTimeoutException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// SSE 响应流的空闲看门狗：每收到一行就重置计时，连续 <see cref="IdleTimeout"/> 没有新数据才判定死连接。
    /// <para>
    /// 根因（v1.13 已核实）：<see cref="System.Net.Http.HttpCompletionOption.ResponseHeadersRead"/> 模式下
    /// <c>HttpClient.Timeout</c> 只覆盖到拿到响应头为止，之后手动读 body 不受保护。远端代理主动关闭连接
    /// （TCP CLOSE_WAIT）、静默丢包等情况下读取既不返回 null 也不抛异常，会永久挂起。
    /// </para>
    /// <para>
    /// 只看"多久没收到数据"，不看"总共流了多久"——长时间持续输出的健康流不会被切断
    /// （流式请求的 HttpClient 本身不设总超时，见 <see cref="OpenAIChatTransporter"/>）。
    /// </para>
    /// <para>
    /// 与旧实现（每行一个 <c>Task.Delay</c> 竞速）相比，整条流只有一个计时器与一个完成源：
    /// 不再为每一行留下一个要等满超时才释放的定时器。看门狗随流一起 Dispose。
    /// 触发（空闲超时或用户取消）时主动 Dispose 底层流，唤醒/终结挂起的读取，避免 socket 停在 CLOSE_WAIT。
    /// </para>
    /// </summary>
    public sealed class StreamIdleWatchdog : IDisposable
    {
        /// <summary>默认空闲上限：思考型模型首包前可能静默较久，但 180s 无任何字节（含 keep-alive）基本可判定为死连接。</summary>
        public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(180);

        /// <summary>测试用：覆盖默认空闲上限（null = 使用 <see cref="DefaultIdleTimeout"/>）。</summary>
        internal static TimeSpan? IdleTimeoutOverride { get; set; }

        private readonly Stream _stream;
        private readonly CancellationToken _userToken;
        private readonly CancellationTokenSource _idle = new();
        private readonly TaskCompletionSource<bool> _tripped =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _idleRegistration;
        private readonly CancellationTokenRegistration _userRegistration;
        private bool _disposed;

        public TimeSpan IdleTimeout { get; }

        /// <summary>是否由空闲超时（而非用户取消）触发。</summary>
        public bool IdleTimedOut => _idle.IsCancellationRequested && !_userToken.IsCancellationRequested;

        public StreamIdleWatchdog(Stream stream, CancellationToken userToken, TimeSpan? idleTimeout = null)
        {
            _stream = stream;
            _userToken = userToken;
            IdleTimeout = idleTimeout ?? IdleTimeoutOverride ?? DefaultIdleTimeout;
            _idleRegistration = _idle.Token.Register(Trip);
            _userRegistration = userToken.Register(Trip);
            _idle.CancelAfter(IdleTimeout);
        }

        private void Trip()
        {
            if (!_tripped.TrySetResult(true)) return;
            try { _stream?.Dispose(); } catch { /* 已关闭或正在被读取任务处理，忽略 */ }
        }

        /// <summary>收到数据：重新开始计时。</summary>
        public void Reset()
        {
            if (_disposed || _tripped.Task.IsCompleted) return;
            try { _idle.CancelAfter(IdleTimeout); } catch (ObjectDisposedException) { }
        }

        /// <summary>
        /// 读一行；读到即重置计时。用户取消抛 <see cref="OperationCanceledException"/>，
        /// 空闲超时抛 <see cref="LlmStreamIdleTimeoutException"/>（可重试的流中途失败）。
        /// </summary>
        public async Task<string> ReadLineAsync(StreamReader reader)
        {
            Task<string> readTask;
            try
            {
                readTask = reader.ReadLineAsync();
            }
            catch (Exception) when (_tripped.Task.IsCompleted)
            {
                throw TrippedException();
            }

            // 缓冲区里已有整行时同步完成，跳过 WhenAny 分配
            if (!readTask.IsCompleted)
            {
                var completed = await Task.WhenAny(readTask, _tripped.Task);
                if (completed != readTask)
                {
                    // 读取仍挂起：底层流已在 Trip 中关闭；观察掉被放弃任务的异常，避免 unobserved task exception
                    _ = readTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    throw TrippedException();
                }
            }

            string line;
            try
            {
                line = await readTask;
            }
            catch (Exception) when (_tripped.Task.IsCompleted)
            {
                // 流被看门狗 / 取消关闭导致的读取异常，按触发原因上报
                throw TrippedException();
            }

            Reset();
            return line;
        }

        private Exception TrippedException()
        {
            // 用户取消与空闲超时必须区分：前者不重试，后者交给 FallbackRouter 按流中途失败重试
            if (_userToken.IsCancellationRequested)
                return new OperationCanceledException(_userToken);
            return new LlmStreamIdleTimeoutException(
                $"[AgentCore] SSE stream read idle for {IdleTimeout.TotalSeconds:0}s with no new data. " +
                "Connection likely dropped by remote (e.g. proxy closed it) without a clean stream-end signal.");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _userRegistration.Dispose();
            _idleRegistration.Dispose();
            _idle.Dispose();
        }
    }
}
