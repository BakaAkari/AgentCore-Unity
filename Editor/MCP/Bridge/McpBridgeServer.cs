using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentCore.Editor.Components.MCP.Bridge
{
    /// <summary>
    /// McpBridge 的消息处理方（由 <see cref="McpBridgeService"/> 实现；测试可替换）。回调在后台线程上触发。
    /// </summary>
    public interface IMcpBridgeHandler
    {
        /// <summary>收到合法 hello（令牌已校验）后调用；返回 helloOk 的附加字段。</summary>
        JObject OnHello(McpBridgeConnection connection);

        void OnMessage(McpBridgeConnection connection, JObject message);

        void OnDisconnected(McpBridgeConnection connection);
    }

    /// <summary>一个 relay 连接。<see cref="Send"/> 线程安全。</summary>
    public sealed class McpBridgeConnection
    {
        private readonly object _writeLock = new object();
        private readonly TcpClient _client;
        private readonly StreamWriter _writer;
        private volatile bool _closed;

        private readonly Func<string, bool> _testSink;

        internal McpBridgeConnection(TcpClient client, StreamWriter writer)
        {
            _client = client;
            _writer = writer;
        }

        /// <summary>测试用：不经网络，消息交给 <paramref name="sink"/>（返回 false 模拟发送失败）。</summary>
        internal McpBridgeConnection(string relayId, Func<string, bool> sink)
        {
            RelayId = relayId;
            _testSink = sink;
        }

        public string RelayId { get; internal set; }

        public string ClientName { get; internal set; }

        public string ClientVersion { get; internal set; }

        public bool IsClosed => _closed;

        public bool Send(JObject message)
        {
            if (_closed || message == null)
                return false;
            var line = message.ToString(Formatting.None);
            if (_testSink != null)
                return _testSink(line);
            try
            {
                lock (_writeLock)
                {
                    _writer.Write(line);
                    _writer.Write('\n');
                    _writer.Flush();
                }
                return true;
            }
            catch (Exception)
            {
                Close();
                return false;
            }
        }

        internal void Close()
        {
            if (_closed)
                return;
            _closed = true;
            try { _client?.Close(); } catch (Exception) { /* 已断开 */ }
        }
    }

    /// <summary>
    /// 只监听 127.0.0.1 的行分隔 JSON 服务器；每个连接一个读循环。首条消息必须是带正确令牌的 hello。
    /// </summary>
    public sealed class McpBridgeServer : IDisposable
    {
        private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(5);
        private const int MaxLineLength = 32 * 1024 * 1024;

        private readonly string _token;
        private readonly IMcpBridgeHandler _handler;
        private readonly List<McpBridgeConnection> _connections = new List<McpBridgeConnection>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private TcpListener _listener;

        public McpBridgeServer(string token, IMcpBridgeHandler handler)
        {
            _token = token ?? throw new ArgumentNullException(nameof(token));
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        public int Port { get; private set; }

        public bool IsRunning => _listener != null && !_cts.IsCancellationRequested;

        public IReadOnlyList<McpBridgeConnection> Connections
        {
            get
            {
                lock (_connections)
                    return _connections.FindAll(c => !c.IsClosed && c.RelayId != null);
            }
        }

        /// <summary>优先绑定 <paramref name="preferredPort"/>（重载前后保持同一端口），被占用时换随机端口。</summary>
        public void Start(int preferredPort)
        {
            if (_listener != null)
                throw new InvalidOperationException("Already started.");

            TcpListener listener = null;
            if (preferredPort > 0)
            {
                try
                {
                    listener = new TcpListener(IPAddress.Loopback, preferredPort);
                    listener.ExclusiveAddressUse = true;
                    listener.Start();
                }
                catch (SocketException)
                {
                    listener = null;
                }
            }

            if (listener == null)
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.ExclusiveAddressUse = true;
                listener.Start();
            }

            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoopAsync);
        }

        public void Broadcast(JObject message)
        {
            foreach (var connection in Connections)
                connection.Send(message);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (Exception) when (_cts.IsCancellationRequested)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    AgentCoreLog.Warning($"[AgentCore][MCP] Accept failed: {ex.Message}");
                    continue;
                }

                _ = Task.Run(() => HandleClientAsync(client));
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            client.NoDelay = true;
            McpBridgeConnection connection = null;
            try
            {
                var stream = client.GetStream();
                var utf8 = new UTF8Encoding(false);
                var reader = new StreamReader(stream, utf8, false, 8192);
                var writer = new StreamWriter(stream, utf8, 8192) { NewLine = "\n", AutoFlush = false };
                connection = new McpBridgeConnection(client, writer);
                lock (_connections)
                    _connections.Add(connection);

                var helloTask = reader.ReadLineAsync();
                if (await Task.WhenAny(helloTask, Task.Delay(HelloTimeout)).ConfigureAwait(false) != helloTask)
                    return;
                if (!TryAcceptHello(connection, helloTask.Result))
                    return;

                while (!_cts.IsCancellationRequested && !connection.IsClosed)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null)
                        break;
                    if (line.Length == 0)
                        continue;
                    if (line.Length > MaxLineLength)
                        break;

                    JObject message;
                    try
                    {
                        message = JObject.Parse(line);
                    }
                    catch (JsonException)
                    {
                        AgentCoreLog.Warning("[AgentCore][MCP] Dropped a malformed message from relay.");
                        continue;
                    }

                    try
                    {
                        _handler.OnMessage(connection, message);
                    }
                    catch (Exception ex)
                    {
                        AgentCoreLog.Warning($"[AgentCore][MCP] Message handler failed: {ex.Message}");
                    }
                }
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                // 关闭中
            }
            catch (IOException)
            {
                // relay 断开
            }
            catch (ObjectDisposedException)
            {
                // 关闭中
            }
            catch (Exception ex)
            {
                AgentCoreLog.Warning($"[AgentCore][MCP] Relay connection failed: {ex.Message}");
            }
            finally
            {
                if (connection != null)
                {
                    connection.Close();
                    lock (_connections)
                        _connections.Remove(connection);
                    if (connection.RelayId != null)
                    {
                        try { _handler.OnDisconnected(connection); }
                        catch (Exception ex) { AgentCoreLog.Warning($"[AgentCore][MCP] Disconnect handler failed: {ex.Message}"); }
                    }
                }
                else
                {
                    try { client.Close(); } catch (Exception) { /* ignore */ }
                }
            }
        }

        private bool TryAcceptHello(McpBridgeConnection connection, string line)
        {
            JObject hello;
            try
            {
                hello = string.IsNullOrEmpty(line) ? null : JObject.Parse(line);
            }
            catch (JsonException)
            {
                hello = null;
            }

            if (hello == null || (string)hello["type"] != McpBridgeProtocol.Hello)
                return Reject(connection, "Expected hello.");
            if (!FixedTimeEquals((string)hello["token"], _token))
                return Reject(connection, "Invalid token. Copy the MCP configuration again from Unity (Project Settings > AgentCore > Tools & Extensions > MCP Server).");
            var version = hello["bridgeProtocol"]?.Type == JTokenType.Integer ? (int)hello["bridgeProtocol"] : -1;
            if (version != McpBridgeProtocol.Version)
                return Reject(connection, $"AgentCore relay protocol {version} does not match the Unity side ({McpBridgeProtocol.Version}). Copy the MCP configuration again from Unity.");
            var relayId = (string)hello["relayId"];
            if (string.IsNullOrEmpty(relayId))
                return Reject(connection, "Missing relayId.");

            connection.RelayId = relayId;
            connection.ClientName = (string)hello["client"]?["name"];
            connection.ClientVersion = (string)hello["client"]?["version"];

            var ok = _handler.OnHello(connection) ?? new JObject();
            ok["type"] = McpBridgeProtocol.HelloOk;
            ok["bridgeProtocol"] = McpBridgeProtocol.Version;
            return connection.Send(ok);
        }

        private static bool Reject(McpBridgeConnection connection, string message)
        {
            connection.Send(new JObject { ["type"] = McpBridgeProtocol.HelloError, ["message"] = message });
            connection.Close();
            return false;
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length)
                return false;
            var diff = 0;
            for (var i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }

        public void Dispose()
        {
            if (_cts.IsCancellationRequested)
                return;
            _cts.Cancel();
            try { _listener?.Stop(); } catch (Exception) { /* ignore */ }
            List<McpBridgeConnection> snapshot;
            lock (_connections)
                snapshot = new List<McpBridgeConnection>(_connections);
            foreach (var connection in snapshot)
                connection.Close();
        }
    }
}
