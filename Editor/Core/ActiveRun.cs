using System;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCore.Editor.Core
{
    /// <summary>
    /// 一次 Agent 运行（用户发送 / ask_user 应答 / 域重载恢复各自开一个）的身份与生命周期。
    /// <para>
    /// <see cref="AgentLoop"/> 同一时刻只认一个 <c>_activeRun</c>：开跑时在第一个 await 之前同步登记，
    /// 取消只取消这一个 run 的令牌；被取消后仍在收尾的旧 run 通过 <see cref="AgentLoop"/> 的
    /// 当前 run 校验（令牌比对）失去写历史 / 改状态的资格，不会和新 run 交错写入。
    /// 参考 Codex <c>ActiveTurn</c> / Claude Code 每轮独立 AbortController 的做法。
    /// </para>
    /// </summary>
    internal sealed class ActiveRun
    {
        public readonly Guid Id = Guid.NewGuid();

        public readonly string Label;

        public readonly CancellationTokenSource Cts = new CancellationTokenSource();

        /// <summary>run 完全结束（含 Node B 等尾部任务）时完成。</summary>
        public readonly TaskCompletionSource<bool> Done = new TaskCompletionSource<bool>();

        /// <summary>
        /// 主循环返回后仍属于本 run 的尾部任务（Node B 审阅 / REVISE 重生成）。
        /// run 收尾前会等待它，保证状态复位只发生一次且在最后。
        /// </summary>
        public Task Tail;

        public ActiveRun(string label)
        {
            Label = label ?? string.Empty;
        }

        public CancellationToken Token => Cts.Token;

        public bool IsCancelled => Cts.IsCancellationRequested;

        public void Cancel()
        {
            try
            {
                Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
