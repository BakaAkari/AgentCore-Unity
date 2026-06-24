using System.Threading;
using System.Threading.Tasks;

namespace AgentCore.Editor.Tools.Safety
{
    /// <summary>
    /// 自动响应的 IToolConfirmationProvider 实现，用于测试或无人值守场景。
    /// 不会弹出任何 UI，始终返回构造时指定的固定结果。
    ///
    /// 适用场景：
    /// 1. 单元测试 / Editor 自动化测试中绕过对话框；
    /// 2. CI 环境下避免阻塞主线程；
    /// 3. 显式声明"当前会话信任所有 RequireConfirmation 工具"或"全部拒绝"的极端策略。
    ///
    /// 注意：生产环境（用户日常使用）应使用 <see cref="DialogToolConfirmationProvider"/>，
    /// 这里只作为可注入的替代实现，不会成为默认值。
    /// </summary>
    public sealed class AutoToolConfirmationProvider : IToolConfirmationProvider
    {
        private readonly bool _autoApprove;

        /// <summary>
        /// 构造一个固定返回结果的 ConfirmationProvider。
        /// </summary>
        /// <param name="autoApprove">true = 全部批准；false = 全部拒绝。默认 false（安全优先）。</param>
        public AutoToolConfirmationProvider(bool autoApprove = false)
        {
            _autoApprove = autoApprove;
        }

        /// <summary>
        /// 立即返回构造时指定的结果，不弹窗、不阻塞。
        /// 仅在 CancellationToken 已取消时返回 false（与 Dialog 实现保持一致的取消语义）。
        /// </summary>
        public Task<bool> RequestConfirmationAsync(ToolConfirmationRequest request, CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
            {
                return Task.FromResult(false);
            }

            return Task.FromResult(_autoApprove);
        }

        /// <summary>
        /// 全部自动批准（仅用于测试，绝不建议在生产环境使用）。
        /// </summary>
        public static AutoToolConfirmationProvider AlwaysApprove() => new AutoToolConfirmationProvider(true);

        /// <summary>
        /// 全部自动拒绝（用于"严格沙盒"或"只允许 Allow 直通工具"的场景）。
        /// </summary>
        public static AutoToolConfirmationProvider AlwaysReject() => new AutoToolConfirmationProvider(false);
    }
}
