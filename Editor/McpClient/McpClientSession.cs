using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Utils;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.McpClient
{
    /// <summary>
    /// 与一个外部 MCP 服务器的会话（AgentCore 作为客户端）。
    /// 先按旧协议 <c>initialize</c>（目前绝大多数服务器）；服务器只支持新协议（2026-07-28）时改用每请求 <c>_meta</c>。
    /// 不声明 sampling / elicitation / roots 能力：服务器发来的请求一律以 -32601 拒绝。
    /// </summary>
    public sealed class McpClientSession : IDisposable
    {
        public const string LegacyVersion = "2025-11-25";
        public const string ModernVersion = "2026-07-28";

        private static readonly TimeSpan InitTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(60);

        /// <summary>单次工具调用的上限（外部服务可能较慢，如构建、检索）。</summary>
        public static TimeSpan CallTimeout = TimeSpan.FromMinutes(10);

        private readonly IMcpClientTransport _transport;
        private readonly Dictionary<long, TaskCompletionSource<JObject>> _pending = new Dictionary<long, TaskCompletionSource<JObject>>();
        private long _nextId;

        public McpClientSession(McpServerConfig config, IMcpClientTransport transport)
        {
            Config = config;
            _transport = transport;
            _transport.MessageReceived += OnMessage;
            _transport.Closed += reason =>
            {
                IsClosed = true;
                CloseReason = reason;
                FailPending(reason);
                Closed?.Invoke(reason);
            };
        }

        public McpServerConfig Config { get; }

        public bool Modern { get; private set; }

        public string NegotiatedVersion { get; private set; }

        public string ServerName { get; private set; }

        public bool IsClosed { get; private set; }

        public string CloseReason { get; private set; }

        public string Diagnostics => _transport.Diagnostics;

        /// <summary>服务器通知工具列表变化。</summary>
        public event Action ToolsChanged;

        public event Action<string> Closed;

        public static McpClientSession Create(McpServerConfig config) =>
            new McpClientSession(config, config.IsHttp ? (IMcpClientTransport)new HttpClientTransport(config) : new StdioClientTransport(config));

        public async Task InitializeAsync(CancellationToken ct)
        {
            await _transport.StartAsync(ct).ConfigureAwait(false);

            var response = await RequestRawAsync("initialize", new JObject
            {
                ["protocolVersion"] = LegacyVersion,
                ["capabilities"] = new JObject(),
                ["clientInfo"] = new JObject { ["name"] = "agentcore-unity", ["version"] = "1" }
            }, InitTimeout, ct, modern: false).ConfigureAwait(false);

            if (response?["result"] is JObject result)
            {
                NegotiatedVersion = (string)result["protocolVersion"] ?? LegacyVersion;
                ServerName = (string)result["serverInfo"]?["name"] ?? Config.Name;
                (_transport as HttpClientTransport)?.SetProtocolVersion(NegotiatedVersion);
                await _transport.SendAsync(new JObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, ct).ConfigureAwait(false);
                return;
            }

            // 只支持新协议的服务器：initialize 被拒绝 —— 用 server/discover 确认后改为每请求 _meta
            var discover = await RequestRawAsync("server/discover", new JObject(), InitTimeout, ct, modern: true).ConfigureAwait(false);
            if (discover?["result"] is JObject discovered)
            {
                Modern = true;
                NegotiatedVersion = ModernVersion;
                ServerName = (string)discovered["_meta"]?["io.modelcontextprotocol/serverInfo"]?["name"] ?? Config.Name;
                (_transport as HttpClientTransport)?.SetProtocolVersion(ModernVersion);
                return;
            }

            var message = (string)response?["error"]?["message"] ?? CloseReason ?? "no response";
            throw new InvalidOperationException("MCP initialize failed: " + message);
        }

        /// <summary>完整工具列表（跟随 nextCursor 分页）。</summary>
        public async Task<List<JObject>> ListToolsAsync(CancellationToken ct)
        {
            var tools = new List<JObject>();
            string cursor = null;
            for (var page = 0; page < 50; page++)
            {
                var parameters = new JObject();
                if (cursor != null)
                    parameters["cursor"] = cursor;
                var response = await RequestAsync("tools/list", parameters, ListTimeout, ct).ConfigureAwait(false);
                if (response["result"]?["tools"] is JArray list)
                {
                    foreach (var tool in list)
                    {
                        if (tool is JObject t)
                            tools.Add(t);
                    }
                }
                cursor = (string)response["result"]?["nextCursor"];
                if (string.IsNullOrEmpty(cursor))
                    break;
            }
            return tools;
        }

        /// <summary>调用工具，返回 CallToolResult（content / isError）。协议错误抛 <see cref="InvalidOperationException"/>。</summary>
        public async Task<JObject> CallToolAsync(string name, JObject arguments, CancellationToken ct)
        {
            var response = await RequestAsync("tools/call", new JObject { ["name"] = name, ["arguments"] = arguments ?? new JObject() }, CallTimeout, ct).ConfigureAwait(false);
            if (response["error"] is JObject error)
                throw new InvalidOperationException((string)error["message"] ?? "Tool call failed.");
            if ((string)response["result"]?["resultType"] == "input_required")
                throw new InvalidOperationException("The server asked for additional input, which AgentCore does not support yet.");
            return response["result"] as JObject ?? new JObject();
        }

        private async Task<JObject> RequestAsync(string method, JObject parameters, TimeSpan timeout, CancellationToken ct)
        {
            var response = await RequestRawAsync(method, parameters, timeout, ct, Modern).ConfigureAwait(false);
            if (response == null)
                throw new TimeoutException($"{method} timed out after {timeout.TotalSeconds:0}s.");
            return response;
        }

        private async Task<JObject> RequestRawAsync(string method, JObject parameters, TimeSpan timeout, CancellationToken ct, bool modern)
        {
            if (IsClosed)
                throw new InvalidOperationException("The MCP server connection is closed: " + CloseReason);

            var id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pending)
                _pending[id] = tcs;

            if (modern)
            {
                parameters["_meta"] = new JObject
                {
                    ["io.modelcontextprotocol/protocolVersion"] = ModernVersion,
                    ["io.modelcontextprotocol/clientInfo"] = new JObject { ["name"] = "agentcore-unity", ["version"] = "1" },
                    ["io.modelcontextprotocol/clientCapabilities"] = new JObject()
                };
            }

            try
            {
                var send = _transport.SendAsync(new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }, ct);
                var delay = Task.Delay(timeout, ct);
                var first = await Task.WhenAny(tcs.Task, send, delay).ConfigureAwait(false);
                if (first == send)
                {
                    await send.ConfigureAwait(false); // 传输错误在这里抛出
                    first = await Task.WhenAny(tcs.Task, delay).ConfigureAwait(false);
                }
                ct.ThrowIfCancellationRequested();
                return first == tcs.Task ? tcs.Task.Result : null;
            }
            finally
            {
                lock (_pending)
                    _pending.Remove(id);
            }
        }

        private void OnMessage(JObject message)
        {
            var method = (string)message["method"];
            var id = message["id"];

            if (method == null)
            {
                if (id == null || id.Type != JTokenType.Integer)
                    return;
                TaskCompletionSource<JObject> tcs;
                lock (_pending)
                    _pending.TryGetValue((long)id, out tcs);
                tcs?.TrySetResult(message);
                return;
            }

            if (id != null && id.Type != JTokenType.Null)
            {
                // 服务器发来的请求：ping 照常答复，其余（sampling / elicitation / roots）不支持
                var reply = method == "ping"
                    ? new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = new JObject() }
                    : new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JObject { ["code"] = -32601, ["message"] = "AgentCore does not support " + method } };
                _ = _transport.SendAsync(reply, CancellationToken.None).ContinueWith(_ => { });
                return;
            }

            if (method == "notifications/tools/list_changed")
            {
                try { ToolsChanged?.Invoke(); } catch (Exception ex) { AgentCoreLog.Warning("[AgentCore][MCP client] ToolsChanged handler failed: " + ex.Message); }
            }
        }

        private void FailPending(string reason)
        {
            List<TaskCompletionSource<JObject>> pending;
            lock (_pending)
                pending = new List<TaskCompletionSource<JObject>>(_pending.Values);
            foreach (var tcs in pending)
                tcs.TrySetResult(new JObject { ["error"] = new JObject { ["code"] = -32603, ["message"] = reason } });
        }

        public void Dispose()
        {
            IsClosed = true;
            _transport.Dispose();
        }
    }
}
