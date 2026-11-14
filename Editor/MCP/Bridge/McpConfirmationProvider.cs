using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Components.MCP.Config;
using AgentCore.Editor.Tools.Safety;

namespace AgentCore.Editor.Components.MCP.Bridge
{
    /// <summary>一次 MCP 调用的来源：哪个 relay、哪个调用、哪个客户端。确认需要发回同一个客户端时使用。</summary>
    public sealed class McpCallContext
    {
        public string RelayId;
        public string CallId;
        public string ClientName;

        public static McpCallContext ForClient(string clientName) => new McpCallContext { ClientName = clientName };
    }

    /// <summary>
    /// MCP 调用的确认：
    /// <list type="bullet">
    /// <item>审查级别“信任 MCP 客户端”：一律直通（同时视为 YOLO，权限规则的 deny 也不阻断）。</item>
    /// <item>否则跟随 AgentCore 当前信任状态（<see cref="ToolTrustState"/>，与 Chat 共用，含 YOLO）。</item>
    /// <item>需要确认时：设置为“在客户端确认”且客户端支持 elicitation，则请客户端确认；否则在 Unity 的 AgentCore 窗口弹出确认卡片（标明来源客户端）。</item>
    /// </list>
    /// </summary>
    public sealed class McpConfirmationProvider : IToolConfirmationProvider, IToolSessionTrustState
    {
        private readonly McpCallContext _context;

        public McpConfirmationProvider(string clientName) : this(McpCallContext.ForClient(clientName))
        {
        }

        public McpConfirmationProvider(McpCallContext context)
        {
            _context = context ?? new McpCallContext();
            if (string.IsNullOrEmpty(_context.ClientName))
                _context.ClientName = "MCP client";
        }

        public bool IsYoloActive => McpSettings.ReviewLevel == McpReviewLevel.TrustClient || ToolTrustState.IsYoloActive;

        public async Task<bool> RequestConfirmationAsync(ToolConfirmationRequest request, CancellationToken ct)
        {
            if (request == null || ct.IsCancellationRequested)
                return false;
            if (McpSettings.ReviewLevel == McpReviewLevel.TrustClient || ToolTrustState.IsTrusted(request))
                return true;

            if (McpSettings.ConfirmLocation == McpConfirmLocation.ClientWhenSupported
                && !string.IsNullOrEmpty(_context.RelayId) && !string.IsNullOrEmpty(_context.CallId))
            {
                var answer = await McpBridgeService.AskClientAsync(_context, request, ct).ConfigureAwait(false);
                if (answer.HasValue)
                    return answer.Value;
                // 客户端不支持 / 已断开：回到 Unity 窗口
            }

            request.Source = _context.ClientName;
            return await ToolConfirmationHost.RequestAsync(request, ct).ConfigureAwait(false);
        }
    }
}
