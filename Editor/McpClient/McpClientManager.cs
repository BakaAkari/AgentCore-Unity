using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCore.Editor.Tools;
using AgentCore.Editor.Tools.Infrastructure;
using AgentCore.Editor.Utils;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentCore.Editor.McpClient
{
    /// <summary>一个已配置的外部 MCP 服务器：连接状态、工具与调用。</summary>
    public sealed class McpClientServer
    {
        private McpClientSession _session;
        private int _restarts;

        public McpClientServer(McpServerConfig config)
        {
            Config = config;
        }

        public McpServerConfig Config { get; }

        /// <summary>disabled / starting / connected / error</summary>
        public string Status { get; private set; } = "starting";

        public string Error { get; private set; }

        public IReadOnlyList<McpProxyTool> Tools { get; private set; } = Array.Empty<McpProxyTool>();

        public string Diagnostics => _session?.Diagnostics;

        internal Action<McpClientServer> OnToolsUpdated;

        public async Task StartAsync()
        {
            if (Config.Disabled)
            {
                Status = "disabled";
                return;
            }
            Status = "starting";
            Error = null;
            try
            {
                var session = McpClientSession.Create(Config);
                session.ToolsChanged += () => _ = RefreshToolsAsync();
                session.Closed += reason => OnClosed(reason);
                _session = session;
                await session.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                await RefreshToolsAsync().ConfigureAwait(false);
                Status = "connected";
            }
            catch (Exception ex)
            {
                Status = "error";
                Error = ex.Message;
                AgentCoreLog.Warning($"[AgentCore][MCP client] '{Config.Name}' failed to start: {ex.Message}");
                _session?.Dispose();
                _session = null;
            }
        }

        private async Task RefreshToolsAsync()
        {
            var session = _session;
            if (session == null)
                return;
            var definitions = await session.ListToolsAsync(CancellationToken.None).ConfigureAwait(false);
            Tools = definitions
                .Where(d => !string.IsNullOrEmpty((string)d["name"]))
                .Select(d => new McpProxyTool(this, d))
                .ToList();
            OnToolsUpdated?.Invoke(this);
        }

        private void OnClosed(string reason)
        {
            if (Status == "stopped")
                return;
            Status = "error";
            Error = reason;
            AgentCoreLog.Warning($"[AgentCore][MCP client] '{Config.Name}' disconnected: {reason}");
            if (_restarts++ < 3)
            {
                Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ =>
                {
                    if (Status != "stopped")
                        _ = StartAsync();
                });
            }
        }

        public async Task<JObject> CallToolAsync(string remoteName, JObject arguments, CancellationToken ct)
        {
            var session = _session;
            if (session == null || session.IsClosed)
                throw new InvalidOperationException($"The server is not connected ({Status}{(Error != null ? ": " + Error : string.Empty)}).");
            return await session.CallToolAsync(remoteName, arguments, ct).ConfigureAwait(false);
        }

        public void Stop()
        {
            Status = "stopped";
            try { _session?.Dispose(); } catch (Exception) { /* ignore */ }
            _session = null;
        }
    }

    /// <summary>
    /// 外部 MCP 服务器管理：编辑器启动 / 脚本重载后读取配置并在后台连接，把各服务器的工具注册进工具表；
    /// 重载前与退出时断开（stdio 服务器进程整树结束）。工具表重建（<see cref="ToolAutoDiscovery.Rebuilt"/>）后重新登记代理工具。
    /// </summary>
    [InitializeOnLoad]
    public static class McpClientManager
    {
        private static readonly object Gate = new object();
        private static List<McpClientServer> _servers = new List<McpClientServer>();

        /// <summary>配置文件解析错误（设置页显示）。</summary>
        public static IReadOnlyList<string> ConfigErrors { get; private set; } = Array.Empty<string>();

        public static event Action Changed;

        static McpClientManager()
        {
            if (Application.isBatchMode)
                return;
            ToolAutoDiscovery.Rebuilt += RegisterAllTools;
            AssemblyReloadEvents.beforeAssemblyReload += StopAll;
            EditorApplication.quitting += StopAll;
            MainThread.Post(Reload);
        }

        public static IReadOnlyList<McpClientServer> Servers
        {
            get
            {
                lock (Gate)
                    return _servers.ToList();
            }
        }

        /// <summary>重新读取配置并重连所有服务器（主线程）。</summary>
        public static void Reload()
        {
            StopAll();
            var errors = new List<string>();
            var configs = McpServerConfigStore.Load(errors);
            ConfigErrors = errors;

            var servers = configs.Select(c => new McpClientServer(c) { OnToolsUpdated = OnToolsUpdated }).ToList();
            lock (Gate)
                _servers = servers;
            RaiseChanged();

            foreach (var server in servers)
                _ = server.StartAsync().ContinueWith(_ => RaiseChanged());
        }

        public static void StopAll()
        {
            List<McpClientServer> servers;
            lock (Gate)
            {
                servers = _servers;
                _servers = new List<McpClientServer>();
            }
            foreach (var server in servers)
            {
                UnregisterTools(server);
                server.Stop();
            }
        }

        private static void OnToolsUpdated(McpClientServer server)
        {
            MainThread.Post(() =>
            {
                UnregisterTools(server);
                RegisterTools(server);
                RaiseChanged();
            });
        }

        private static void RegisterAllTools()
        {
            foreach (var server in Servers)
                RegisterTools(server);
        }

        private static void RegisterTools(McpClientServer server)
        {
            foreach (var tool in server.Tools)
            {
                if (ToolRegistry.Instance.HasTool(tool.Metadata.Name))
                    ToolRegistry.Instance.Unregister(tool.Metadata.Name);
                ToolRegistry.Instance.Register(tool);
            }
        }

        private static void UnregisterTools(McpClientServer server)
        {
            var prefix = McpProxyTool.ToolName(server.Config.Name, string.Empty);
            foreach (var name in ToolRegistry.Instance.GetAllToolNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                ToolRegistry.Instance.Unregister(name);
        }

        private static void RaiseChanged()
        {
            try { Changed?.Invoke(); } catch (Exception) { /* ignore */ }
        }
    }
}
