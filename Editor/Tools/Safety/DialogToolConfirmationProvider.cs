using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace AgentCore.Editor.Tools.Safety
{
    /// <summary>
    /// 兼容/后备确认提供者。
    /// <para>
    /// 历史版本曾使用阻塞式 <c>EditorUtility.DisplayDialog</c>。该路径会依赖 Unity 前台窗口，
    /// 容易在用户切换到其他应用时阻断工具执行，因此当前实现不再弹出任何系统对话框。
    /// </para>
    /// <para>
    /// 正常交互路径应由 ChatWindow 内嵌确认 UI 注入；如果仍有旧宿主实例化本类型，
    /// 本类型按 fail-safe 语义自动拒绝，避免重新引入阻塞弹窗。
    /// </para>
    /// </summary>
    public sealed class DialogToolConfirmationProvider : IToolConfirmationProvider
    {
        /// <summary>日志前缀。</summary>
        private const string LogPrefix = "[AgentCore] DialogConfirmation: ";

        /// <summary>保留兼容常量；当前实现不会等待或弹窗。</summary>
        public const int DefaultTimeoutSeconds = 60;

        /// <summary>
        /// 创建兼容确认提供者。
        /// </summary>
        /// <param name="timeoutSeconds">保留兼容参数；当前实现不会显示阻塞弹窗。</param>
        public DialogToolConfirmationProvider(int timeoutSeconds = DefaultTimeoutSeconds)
        {
        }

        /// <inheritdoc />
        public Task<bool> RequestConfirmationAsync(ToolConfirmationRequest request, CancellationToken ct)
        {
            if (request == null)
            {
                Debug.LogWarning($"{LogPrefix}Null request → auto reject.");
                return Task.FromResult(false);
            }

            Debug.LogWarning(
                $"{LogPrefix}Blocking dialog confirmation is disabled. Auto reject '{request.ToolName}'. " +
                "Use ChatWindow embedded confirmation provider for interactive approval.");
            return Task.FromResult(false);
        }
    }
}
