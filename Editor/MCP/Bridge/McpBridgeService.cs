using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using AgentCore.Editor.Components.MCP.Relay;
using AgentCore.Editor.Core;
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
    /// <para>
    /// 端口与令牌存 <see cref="SessionState"/>：Domain Reload 前后不变，编辑器重启重新生成。批处理模式不启动。
    /// </para>
    /// <para>
    /// Domain Reload（plans/mcp-server-design.md §4）：改脚本的调用先等编译结束再答复；重载打断等待、或打断执行中的调用时，
    /// 结果经 <see cref="McpCallLedger"/> 存进 SessionState，重载后由 relay 用 resume 取回。
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    public static class McpBridgeService
    {
        private const string TokenKey = "AgentCore.Mcp.Token";
        private const string PortKey = "AgentCore.Mcp.Port";
        private const string PendingKey = "AgentCore.Mcp.PendingResults";

        private static readonly Handler BridgeHandler = new Handler();
        private static readonly McpCallLedger Ledger = new McpCallLedger();

        private static McpBridgeServer _server;
        private static volatile bool _reloading;
        private static string _token;
        private static McpToolCatalog.Snapshot _catalog;

        /// <summary>最近一次调用的摘要（设置卡片显示）。</summary>
        public static string LastCallSummary { get; private set; }

        /// <summary>启动失败等错误（设置卡片显示）。</summary>
        public static string LastError { get; private set; }

        public static bool IsRunning => _server != null && _server.IsRunning;

        public static string[] ConnectedClients =>
            _server?.Connections.Select(c => string.IsNullOrEmpty(c.ClientName) ? "unknown client" : c.ClientName).Distinct().ToArray()
            ?? Array.Empty<string>();

        public static string CurrentSchemaHash => _catalog?.SchemaHash;

        static McpBridgeService()
        {
            if (Application.isBatchMode)
                return;

            // 重载前存下的结果放回待取区（relay 重连后用 resume 取回）
            Ledger.Restore(SessionState.GetString(PendingKey, string.Empty));
            SessionState.EraseString(PendingKey);

            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.quitting += OnQuitting;
            // MainThread.Post 会唤醒空闲 / 失焦的编辑器（delayCall 在编辑器空闲时可能一直不执行）
            MainThread.Post(Start);
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

                // relay 随包以源码分发：启用后在后台编译一次（源码不变则复用）
                McpRelayBuilder.EnsureBuiltAsync();
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
            _reloading = true;
            WriteEndpoint("reloading");
            SessionState.SetString(PendingKey, Ledger.Serialize());
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

        /// <summary>
        /// 等待改脚本的调用触发的编译（主线程）：与内嵌 Chat 相同的做法 —— 稍等让 Unity 发现文件变化，再刷新并等待编译结束。
        /// 编译成功后会紧接着 Domain Reload，此时等待被打断，由 <see cref="OnBeforeAssemblyReload"/> 交给 ledger。
        /// </summary>
        private static async Task<ErrorReport> WaitForCompilationAsync()
        {
            await Task.Delay(1500);
            using (var watcher = new CompilationWatcher())
            {
                if (!EditorApplication.isCompiling)
                    watcher.CompilationTimeoutSeconds = 15f;
                return await watcher.RefreshAndWaitAsync();
            }
        }

        private sealed class Handler : IMcpBridgeHandler
        {
            public JObject OnHello(McpBridgeConnection connection)
            {
                Ledger.Attach(connection);
                Ledger.Prune();
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
                        if (id != null)
                            Ledger.Cancel(connection.RelayId, id);
                        break;
                    case McpBridgeProtocol.Resume:
                        Ledger.Resume(connection, (message["ids"] as JArray)?.Select(t => (string)t).Where(s => s != null));
                        break;
                }
            }

            public void OnDisconnected(McpBridgeConnection connection)
            {
                // 不取消正在执行的调用：relay 可能只是在重连（编辑器重载、网络抖动），结果留待 resume
                Ledger.Detach(connection);
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
                var relayId = connection.RelayId;
                var client = connection.ClientName;
                var ct = Ledger.Begin(relayId, id, name);
                if (_reloading)
                {
                    // 重载已开始：不执行也不答复，relay 重连后重发
                    Ledger.Forget(relayId, id);
                    return;
                }

                var outcome = await McpToolExecutor.ExecuteAsync(name, arguments, client, ct).ConfigureAwait(false);
                if (outcome.NotStarted)
                {
                    Ledger.Forget(relayId, id);
                    return;
                }

                if (outcome.Status != McpBridgeProtocol.StatusError
                    && (outcome.CompileRelated || McpToolExecutor.MayTriggerCompilation(name, arguments)))
                {
                    Ledger.AwaitCompile(relayId, id, outcome.ToMessage(id));
                    connection.Send(new JObject
                    {
                        ["type"] = McpBridgeProtocol.Progress,
                        ["id"] = id,
                        ["message"] = "Waiting for Unity to compile the changed scripts..."
                    });
                    var compiled = await AppendCompilationResultAsync(id, outcome, client).ConfigureAwait(false);
                    Ledger.Complete(relayId, id, compiled);
                    return;
                }

                LastCallSummary = $"{DateTime.Now:HH:mm:ss} {client ?? "client"} → {name}: {outcome.Status}";
                Ledger.Complete(relayId, id, outcome.ToMessage(id));
            }

            /// <summary>在主线程等待编译结束，把编译错误附在结果后面（有错误时 ok 降为 partial）。</summary>
            private static async Task<JObject> AppendCompilationResultAsync(string id, McpCallOutcome outcome, string client)
            {
                ErrorReport report = null;
                try
                {
                    var wait = await MainThread.RunAsync(() => WaitForCompilationAsync()).ConfigureAwait(false);
                    report = await wait.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AgentCoreLog.Warning($"[AgentCore][MCP] Waiting for compilation failed: {ex.Message}");
                }

                var message = outcome.ToMessage(id);
                if (report != null && report.HasErrors)
                {
                    message["text"] = (string)message["text"] +
                                      "\n\n--- Compilation Errors Detected ---\nThe script change caused compilation errors. Please fix them:\n" +
                                      report.FormatForLLM(maxErrors: 10);
                    if ((string)message["status"] == McpBridgeProtocol.StatusOk)
                        message["status"] = McpBridgeProtocol.StatusPartial;
                }
                LastCallSummary = $"{DateTime.Now:HH:mm:ss} {client ?? "client"} → compile: {(report?.HasErrors == true ? "errors" : "ok")}";
                return message;
            }
        }
    }
}
