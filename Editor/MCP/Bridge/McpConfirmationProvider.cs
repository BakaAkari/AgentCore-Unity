using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools.Safety;

namespace AgentCore.Editor.Components.MCP.Bridge
{
    /// <summary>
    /// MCP 调用的确认：审查级别跟随 AgentCore 当前信任状态（<see cref="ToolTrustState"/>，与 Chat 共用，含 YOLO）；
    /// 需要用户确认时在 Unity 的 AgentCore 窗口里弹出与 Chat 相同的确认卡片，并标明来源客户端。
    /// </summary>
    public sealed class McpConfirmationProvider : IToolConfirmationProvider, IToolSessionTrustState
    {
        private readonly string _clientName;

        public McpConfirmationProvider(string clientName)
        {
            _clientName = string.IsNullOrEmpty(clientName) ? "MCP client" : clientName;
        }

        public bool IsYoloActive => ToolTrustState.IsYoloActive;

        public Task<bool> RequestConfirmationAsync(ToolConfirmationRequest request, CancellationToken ct)
        {
            if (request == null || ct.IsCancellationRequested)
                return Task.FromResult(false);
            if (ToolTrustState.IsTrusted(request))
                return Task.FromResult(true);

            request.Source = _clientName;
            return ToolConfirmationHost.RequestAsync(request, ct);
        }
    }
}
