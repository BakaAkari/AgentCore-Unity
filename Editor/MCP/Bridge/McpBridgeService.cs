using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.Components.MCP.Bridge
{
    /// <summary>
    /// McpBridge 的生命周期：组件启用后随编辑器启动，在 127.0.0.1 上监听 relay，写发现文件，
    /// 把 relay 的请求交给 <see cref="McpToolCatalog"/> / <see cref="McpToolExecutor"/>。
    /// 端口与令牌存 <see cref="SessionState"/>：Domain Reload 前后不变，编辑器重启重新生成。批处理模式不启动。
    /// </summary>
    [InitializeOnLoad]
    public static class McpBridgeService
    {
        private const string TokenKey = "AgentCore.Mcp.Token";
        private const string PortKey = "AgentCore.Mcp.Port";

        private static readonly Handler BridgeHandler = new Handler();
        private static readonly ConcurrentDictionary<string, CancellationTokenSource> RunningCalls =
            new ConcurrentDictionary<string, CancellationTokenSource>();

        private static McpBridgeServer _server;
        private static string _token;
        private static McpToolCatalog.Snapshot _catalog;

        /// <summary>最近一次调用的摘要（设置卡片显示）。</summary>
        public static string LastCallSummary { get; private set; }

        /// <summary>启动失败等错误（设置卡片显示）。</summary>
        public static string LastError { get; private set; }

        public static bool IsRunning => _server != null && _server.IsRunning;

        public static int ConnectedRelayCount => _server?.Connections.Count ?? 0;

        public static string[] ConnectedClients =>
            _server?.Connections.Select(c => string.IsNullOrEmpty(c.ClientName) ? "unknown client" : c.ClientName).Distinct().ToArray()
            ?? Array.Empty<string>();

        public static string CurrentSchemaHash => _catalog?.SchemaHash;

        static McpBridgeService()
        {
            if (Application.isBatchMode)
                return;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.quitting += OnQuitting;
            EditorApplication.delayCall += Start;
        }

        private static void Start()
        {
            if (_server != null)
                return;
            try
            {
                _token = SessionState.GetString(TokenKey, string.Empty);
                if (string.IsNullOrEmpty(_token))
                {
                    _token = NewToken();
                    SessionState.SetString(TokenKey, _token);
                }

                _catalog = McpToolCatalog.Build();

                var server = new McpBridgeServer(_token, BridgeHandler);
                server.Start(SessionState.GetInt(PortKey, 0));
                SessionState.SetInt(PortKey, server.Port);
                _server = server;

                WriteEndpoint("ready");
                LastError = null;
                AgentCoreLog.Info($"[AgentCore][MCP] Bridge listening on 127.0.0.1:{server.Port} ({_catalog.Tools.Count} tools).");
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                AgentCoreLog.Warning($"[AgentCore][MCP] Bridge failed to start: {ex.Message}");
            }
        }

        private static void OnBeforeAssemblyReload()
        {
            if (_server == null)
                return;
            WriteEndpoint("reloading");
            _server.Broadcast(new JObject { ["type"] = McpBridgeProtocol.Reloading });
            _server.Dispose();
            _server = null;
        }

        private static void OnQuitting()
        {
            _server?.Dispose();
            _server = null;
            try
            {
                if (File.Exists(McpPaths.EndpointFile))
                    File.Delete(McpPaths.EndpointFile);
            }
            catch (Exception)
            {
                // relay 会通过 PID 判断编辑器已退出
            }
        }

        private static void WriteEndpoint(string state)
        {
            if (_server == null && state == "ready")
                return;
            var endpoint = new JObject
            {
                ["port"] = _server?.Port ?? SessionState.GetInt(PortKey, 0),
                ["token"] = _token,
                ["pid"] = System.Diagnostics.Process.GetCurrentProcess().Id,
                ["projectPath"] = McpPaths.ProjectRoot,
                ["unityVersion"] = Application.unityVersion,
                ["editorContentsPath"] = EditorApplication.applicationContentsPath,
                ["bridgeProtocol"] = McpBridgeProtocol.Version,
                ["state"] = state,
                ["updatedAt"] = DateTime.UtcNow.ToString("o"),
            };
            try
            {
                McpPaths.WriteAtomic(McpPaths.EndpointFile, endpoint.ToString(Formatting.Indented));
            }
            catch (Exception ex)
            {
                LastError = $"Could not write {McpPaths.EndpointFile}: {ex.Message}";
                AgentCoreLog.Warning($"[AgentCore][MCP] {LastError}");
            }
        }

        private static string NewToken()
        {
            var bytes = new byte[24];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static string CallKey(McpBridgeConnection connection, string id) => connection.RelayId + "/" + id;

        private sealed class Handler : IMcpBridgeHandler
        {
            public JObject OnHello(McpBridgeConnection connection)
            {
                return new JObject
                {
                    ["projectPath"] = McpPaths.ProjectRoot,
                    ["unityVersion"] = Application.unityVersion,
                    ["schemaHash"] = _catalog?.SchemaHash,
                };
            }

            public void OnMessage(McpBridgeConnection connection, JObject message)
            {
                var type = (string)message["type"];
                var id = (string)message["id"];
                switch (type)
                {
                    case McpBridgeProtocol.ListTools:
                        _ = ListToolsAsync(connection, id);
                        break;
                    case McpBridgeProtocol.CallTool:
                        _ = CallToolAsync(connection, id, (string)message["name"], message["arguments"] as JObject);
                        break;
                    case McpBridgeProtocol.Cancel:
                        if (id != null && RunningCalls.TryGetValue(CallKey(connection, id), out var cts))
                        {
                            try { cts.Cancel(); } catch (ObjectDisposedException) { /* 已结束 */ }
                        }
                        break;
                }
            }

            public void OnDisconnected(McpBridgeConnection connection)
            {
                // 断开不取消正在执行的调用：relay 可能只是重连（重载期间），结果在 M1-04 中交还
            }

            private static async Task ListToolsAsync(McpBridgeConnection connection, string id)
            {
                try
                {
                    var snapshot = await MainThread.RunAsync(() => McpToolCatalog.Build()).ConfigureAwait(false);
                    _catalog = snapshot;
                    connection.Send(new JObject
                    {
                        ["type"] = McpBridgeProtocol.Tools,
                        ["id"] = id,
                        ["tools"] = snapshot.Tools,
                        ["schemaHash"] = snapshot.SchemaHash,
                    });
                }
                catch (Exception ex)
                {
                    AgentCoreLog.Warning($"[AgentCore][MCP] listTools failed: {ex.Message}");
                    connection.Send(new JObject
                    {
                        ["type"] = McpBridgeProtocol.Tools,
                        ["id"] = id,
                        ["tools"] = _catalog?.Tools ?? new JArray(),
                        ["schemaHash"] = _catalog?.SchemaHash,
                        ["error"] = ex.Message,
                    });
                }
            }

            private static async Task CallToolAsync(McpBridgeConnection connection, string id, string name, JObject arguments)
            {
                if (string.IsNullOrEmpty(id))
                    return;
                var key = CallKey(connection, id);
                using (var cts = new CancellationTokenSource())
                {
                    RunningCalls[key] = cts;
                    McpCallOutcome outcome;
                    try
                    {
                        outcome = await McpToolExecutor.ExecuteAsync(name, arguments, connection.ClientName, cts.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        RunningCalls.TryRemove(key, out _);
                    }

                    LastCallSummary = $"{DateTime.Now:HH:mm:ss} {connection.ClientName ?? "client"} → {name}: {outcome.Status}";
                    connection.Send(outcome.ToMessage(id));
                }
            }
        }
    }
}
