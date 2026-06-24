using System.Threading;
using System.Threading.Tasks;

namespace AgentCore.Editor.Tools.Safety
{
    /// <summary>
    /// 工具执行确认提供者（G.1 治理层）。
    /// <para>
    /// 当 <see cref="ToolRiskPolicy"/> 评估结果为 <see cref="ToolPolicyOutcome.RequireConfirmation"/> 时，
    /// <c>ToolCallDispatcher</c> 通过本接口向用户征询批准与否，再决定是否真正执行工具。
    /// </para>
    /// <para>
    /// 设计约束：
    /// <list type="bullet">
    ///   <item>接口必须可在<b>非主线程</b>调用（实现可内部 marshal 到主线程）；</item>
    ///   <item>实现必须保证最终在合理时间内完成（推荐 60s 超时即视为拒绝）；</item>
    ///   <item>实现不得抛出异常，应通过返回 <c>false</c> 表达拒绝或失败；</item>
    ///   <item>实现应尊重 <paramref name="ct"/>，被取消时返回 <c>false</c>。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 默认交互由 ChatWindow 内嵌确认 UI 提供，避免阻塞式系统弹窗依赖 Unity 位于前台。
    /// </para>
    /// </summary>
    public interface IToolConfirmationProvider
    {
        /// <summary>
        /// 异步请求用户确认。
        /// </summary>
        /// <param name="request">结构化确认请求（不可为 null）。</param>
        /// <param name="ct">取消令牌；被取消时实现应尽快返回 <c>false</c>。</param>
        /// <returns>
        /// <c>true</c> 表示用户批准执行；
        /// <c>false</c> 表示用户拒绝、超时、UI 不可用或被取消。
        /// </returns>
        Task<bool> RequestConfirmationAsync(ToolConfirmationRequest request, CancellationToken ct);
    }
}
